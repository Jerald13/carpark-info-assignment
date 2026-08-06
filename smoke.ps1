#!/usr/bin/env pwsh
<#
.SYNOPSIS
    End-to-end smoke test: does a reviewer who clones this repository get a working system?

.DESCRIPTION
    Runs the README's instructions exactly, then exercises every documented endpoint against a
    real HTTP socket and asserts the answers are the ones the documentation promises.

    This exists because the unit and functional suites cannot answer that question. They call the
    API in-process with an HttpClient, which is the right way to test behaviour but never touches:

        * a real socket, a real port, or TLS
        * HTTP-to-HTTPS redirects
        * the shape of the generated OpenAPI document
        * whether Swagger UI can actually build a request from it

    A defect in any of those leaves every test green while the reviewer sees a page that spins for
    ever. That happened: carParkType was declared as an array parameter, Swagger UI ran JSON.parse
    over it, the request was never sent, and 232 tests passed throughout.

.EXAMPLE
    pwsh ./smoke.ps1
    pwsh ./smoke.ps1 -SkipIngest      # reuse an existing database
#>

[CmdletBinding()]
param(
    [int]$Port = 5199,
    [switch]$SkipIngest
)

$ErrorActionPreference = 'Stop'
$root      = $PSScriptRoot
$dbPath    = Join-Path $root 'smoke.db'
$baseUrl   = "http://localhost:$Port"
$apiProc   = $null
$failures  = @()

function Write-Step($text) { Write-Host "`n$text" -ForegroundColor Cyan }

function Assert-That {
    param([string]$Name, [scriptblock]$Check, [string]$Expected)

    Write-Host -NoNewline ("  {0,-52}" -f $Name)
    try {
        $actual = & $Check
        if ("$actual" -eq "$Expected") {
            Write-Host "PASS" -ForegroundColor Green -NoNewline
            Write-Host "  ($actual)" -ForegroundColor DarkGray
        }
        else {
            Write-Host "FAIL" -ForegroundColor Red -NoNewline
            Write-Host "  expected $Expected, got $actual" -ForegroundColor Red
            $script:failures += "$Name - expected $Expected, got $actual"
        }
    }
    catch {
        Write-Host "ERROR" -ForegroundColor Red -NoNewline
        Write-Host "  $($_.Exception.Message)" -ForegroundColor Red
        $script:failures += "$Name - $($_.Exception.Message)"
    }
}

# Windows PowerShell 5.1 has no -SkipHttpErrorCheck, and treats any non-2xx as a terminating
# error, so the status has to be read back off the exception's response.
function Get-Json($path) {
    Invoke-RestMethod -Uri "$baseUrl$path" -TimeoutSec 20
}

# Reads the ProblemDetails body off a non-2xx. Invoke-WebRequest on Windows PowerShell 5.1 throws
# before handing back the body, so it has to come off the exception's response stream.
function Get-Problem($path, $headers = @{}) {
    $request = [Net.HttpWebRequest]::Create("$baseUrl$path")
    foreach ($key in $headers.Keys) { $request.Headers.Add($key, $headers[$key]) }

    try { $request.GetResponse().Close(); return $null }
    catch [Net.WebException] {
        $reader = New-Object IO.StreamReader($_.Exception.Response.GetResponseStream())
        return $reader.ReadToEnd() | ConvertFrom-Json
    }
}

function Get-Status($path, $method = 'GET', $headers = @{}) {
    try {
        $r = Invoke-WebRequest -Uri "$baseUrl$path" -Method $method -Headers $headers `
                -TimeoutSec 20 -MaximumRedirection 0 -UseBasicParsing -ErrorAction Stop
        return [int]$r.StatusCode
    }
    catch {
        if ($_.Exception.Response) { return [int]$_.Exception.Response.StatusCode }
        return -1
    }
}

try {
    Write-Host "`nSMOKE TEST - the reviewer's path, end to end" -ForegroundColor White
    Write-Host "Running what the README says, against a real socket on port $Port.`n" -ForegroundColor DarkGray

    # -- 1. Build ----------------------------------------------------------------------------
    Write-Step "1. Build"
    dotnet build --configuration Release -warnaserror 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Build failed. Run 'dotnet build' to see why." }
    Write-Host "  solution builds clean" -ForegroundColor Green

    # -- 2. Ingest ---------------------------------------------------------------------------
    if (-not $SkipIngest) {
        Write-Step "2. Load the data (README step 1)"
        Remove-Item $dbPath, "$dbPath-shm", "$dbPath-wal" -ErrorAction SilentlyContinue

        $env:ConnectionStrings__CarparkDatabase = "Data Source=$dbPath"
        $ingest = dotnet run --project src/CarparkInfo.BatchJob --no-launch-profile --no-build `
                    -c Release -- --file hdb-carpark-information-20220824010400.csv 2>&1

        $read = ($ingest | Select-String 'read\s*:\s*(\d+)').Matches.Groups[1].Value
        Assert-That "batch job ingests the supplied dataset" { $read } "2181"
    }

    # -- 3. Start the API --------------------------------------------------------------------
    Write-Step "3. Start the API (README step 2)"
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:ConnectionStrings__CarparkDatabase = "Data Source=$dbPath"

    $apiProc = Start-Process -PassThru -WindowStyle Hidden -FilePath 'dotnet' -ArgumentList @(
        'run', '--project', 'src/CarparkInfo.Api', '--no-launch-profile', '--no-build',
        '-c', 'Release', '--urls', $baseUrl)

    $ready = $false
    foreach ($i in 1..60) {
        Start-Sleep -Seconds 1
        if ((Get-Status '/api/v1/health/live') -eq 200) { $ready = $true; break }
    }
    if (-not $ready) {
        Write-Host "
API did not become ready. Its output was:" -ForegroundColor Red
        foreach ($f in @($apiOut, "$apiOut.err")) {
            if (Test-Path $f) { Get-Content $f -Tail 40 | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray } }
        }
        throw "API did not become ready on $baseUrl within 60 seconds."
    }
    Write-Host "  API is listening on $baseUrl" -ForegroundColor Green

    # -- 4. No redirect ----------------------------------------------------------------------
    Write-Step "4. Plain HTTP answers directly"
    Write-Host "     A 307 here sends a browser to the untrusted dev certificate," -ForegroundColor DarkGray
    Write-Host "     where the request dies silently and Swagger spins for ever." -ForegroundColor DarkGray
    Assert-That "GET /api/v1/carparks returns 200, not a redirect" { Get-Status '/api/v1/carparks?pageSize=1' } "200"
    Assert-That "GET /swagger/index.html" { Get-Status '/swagger/index.html' } "200"
    Assert-That "GET /openapi/v1.json" { Get-Status '/openapi/v1.json' } "200"

    # -- 5. The contract Swagger UI has to consume -------------------------------------------
    Write-Step "5. The OpenAPI document is usable by Swagger UI"
    Write-Host "     Swagger runs JSON.parse over any array-typed parameter. One array" -ForegroundColor DarkGray
    Write-Host "     parameter is enough to make every Execute button do nothing at all." -ForegroundColor DarkGray

    $doc = Get-Json '/openapi/v1.json'
    Assert-That "OpenAPI version is 3.1" { $doc.openapi.Substring(0,3) } "3.1"

    $arrayParams = @()
    foreach ($p in $doc.paths.PSObject.Properties) {
        foreach ($op in $p.Value.PSObject.Properties) {
            foreach ($param in @($op.Value.parameters)) {
                if ($null -ne $param -and $null -ne $param.schema -and
                    ($param.schema.type -eq 'array' -or $null -ne $param.schema.items)) {
                    $arrayParams += "$($p.Name):$($param.name)"
                }
            }
        }
    }
    Assert-That "no array-typed query parameters" { $arrayParams.Count } "0"
    Assert-That "Authorize button is declared" { [bool]$doc.components.securitySchemes.Bearer } "True"

    # Declaring the scheme only draws the button. Swagger sends the Authorization header for an
    # OPERATION only when that operation carries a security requirement. Without it the button
    # works, the token is accepted, and every protected endpoint still answers 401 - which is
    # exactly what shipped, because both this script and the functional tests set the header
    # themselves and never exercised the browser's path.
    Assert-That "protected operations require the scheme" {
        (@('/api/v1/favourites', '/api/v1/admin/job-runs') | ForEach-Object {
            [bool]$doc.paths.$_.get.security }) -notcontains $false } "True"
    Assert-That "anonymous operations do not" {
        [bool]$doc.paths.'/api/v1/carparks'.get.PSObject.Properties['security'] } "False"

    # Swagger UI does not render an <example> as a hint - it PRE-FILLS the input box with it. Every
    # filter carrying one meant the page opened with seven filters already applied, so a reviewer's
    # first Execute on the assignment's main endpoint returned nothing and read as a broken search.
    Assert-That "no search filter is pre-filled" {
        @($doc.paths.'/api/v1/carparks'.get.parameters |
            Where-Object { $_.name -ne 'PageSize' -and $_.schema.PSObject.Properties['example'] }).Count } "0"

    # -- 6. The user stories -----------------------------------------------------------------
    Write-Step "6. The three user stories return the documented counts"
    # The very first thing a reviewer does: expand the endpoint and press Execute with nothing set.
    Assert-That "a bare request returns the catalogue" { (Get-Json '/api/v1/carparks').data.Count } "20"
    Assert-That "whole catalogue" { (Get-Json '/api/v1/carparks?includeTotal=true&pageSize=1').pagination.totalCount } "2181"
    Assert-That "free parking (R10)" { (Get-Json '/api/v1/carparks?freeParking=true&includeTotal=true&pageSize=1').pagination.totalCount } "1605"
    Assert-That "night parking (R11)" { (Get-Json '/api/v1/carparks?nightParking=true&includeTotal=true&pageSize=1').pagination.totalCount } "1795"
    Assert-That "fits a 2.0 m vehicle (R12)" { (Get-Json '/api/v1/carparks?vehicleHeight=2.0&includeTotal=true&pageSize=1').pagination.totalCount } "2056"
    Assert-That "all three combined" { (Get-Json '/api/v1/carparks?freeParking=true&nightParking=true&vehicleHeight=2.0&includeTotal=true&pageSize=1').pagination.totalCount } "1348"

    # The boolean filters are tri-state and only true was ever exercised. `== true` in the
    # repository collapsed false into null, so ?nightParking=false returned all 2,181 carparks -
    # including the 1,795 that DO have night parking. A parameter accepted and silently ignored.
    Assert-That "freeParking=false excludes free parking" { (Get-Json '/api/v1/carparks?freeParking=false&includeTotal=true&pageSize=1').pagination.totalCount } "576"
    Assert-That "nightParking=false excludes night parking" { (Get-Json '/api/v1/carparks?nightParking=false&includeTotal=true&pageSize=1').pagination.totalCount } "386"
    Assert-That "true and false partition the catalogue" {
        (Get-Json '/api/v1/carparks?nightParking=true&includeTotal=true&pageSize=1').pagination.totalCount +
        (Get-Json '/api/v1/carparks?nightParking=false&includeTotal=true&pageSize=1').pagination.totalCount } "2181"

    # A parameter the API accepts and ignores is worse than one it rejects: the caller gets a 200
    # and a plausible answer with no way to know it is the wrong one. Both of these used to do that.
    Assert-That "an invented cursor is rejected" { Get-Status '/api/v1/carparks?cursor=100' } "400"
    Assert-That "an unknown sort is rejected" { Get-Status '/api/v1/carparks?sort=banana' } "400"
    Assert-That "a real cursor is still accepted" {
        Get-Status ('/api/v1/carparks?pageSize=5&cursor=' +
            [uri]::EscapeDataString((Get-Json '/api/v1/carparks?pageSize=5').pagination.nextCursor)) } "200"
    Assert-That "comma-separated carParkType" { (Get-Json '/api/v1/carparks?carParkType=MULTI_STOREY,BASEMENT&includeTotal=true&pageSize=1').pagination.totalCount } "1071"

    # Radius search prefilters with an index-seekable bounding box, then discards the corners with
    # an exact haversine pass. totalCount used to be taken from the BOX: a 5 km search reported 420
    # and returned 391, so a client paging to totalCount waited for 29 rows that never existed.
    $geo = '/api/v1/carparks?lat=1.3009&lon=103.8546&radiusKm=2'
    Assert-That "radius count matches rows returned" {
        (Get-Json "$geo&includeTotal=true&pageSize=1").pagination.totalCount -eq
        (Get-Json "$geo&pageSize=200").data.Count } "True"
    Assert-That "no result lies beyond the radius" {
        (@((Get-Json "$geo&pageSize=200").data | Where-Object { $_.distanceKm -gt 2 })).Count } "0"

    # -- 7. Response shape -------------------------------------------------------------------
    Write-Step "7. The response shape stops clients repeating the height bug"
    Assert-That "unrestricted carpark reports isRestricted=false" { (Get-Json '/api/v1/carparks/AK19').heightRestriction.isRestricted } "False"
    Assert-That "unrestricted carpark reports a null limit" { $null -eq (Get-Json '/api/v1/carparks/AK19').heightRestriction.maxVehicleHeightMetres } "True"
    Assert-That "restricted carpark reports its limit" { [decimal](Get-Json '/api/v1/carparks/ACB').heightRestriction.maxVehicleHeightMetres } "1.8"
    Assert-That "coordinates are map-ready WGS84" { [math]::Round((Get-Json '/api/v1/carparks/ACB').location.latitude, 4) } "1.3019"
    Assert-That "address with four commas survives" { (Get-Json '/api/v1/carparks/C10').address } "BLK 339,341,344-345,371-381 CLEMENTI AVENUE 5"

    # -- 8. Auth and favourites --------------------------------------------------------------
    Write-Step "8. Register, log in, favourite - the whole authenticated journey"
    $email = "smoke-$([guid]::NewGuid().ToString('N').Substring(0,8))@example.com"
    $pass  = 'correct-horse-battery-staple'

    Invoke-RestMethod "$baseUrl/api/v1/auth/register" -Method Post -ContentType 'application/json' `
        -Body (@{ email = $email; password = $pass; displayName = 'Smoke' } | ConvertTo-Json) | Out-Null

    $tokens = Invoke-RestMethod "$baseUrl/api/v1/auth/login" -Method Post -ContentType 'application/json' `
        -Body (@{ email = $email; password = $pass } | ConvertTo-Json)

    $auth = @{ Authorization = "Bearer $($tokens.accessToken)" }

    Assert-That "login issues an access token" { [bool]$tokens.accessToken } "True"
    Assert-That "favourites reject anonymous callers" { Get-Status '/api/v1/favourites' } "401"
    Assert-That "PUT /favourites/ACB first time" { Get-Status '/api/v1/favourites/ACB' 'PUT' $auth } "201"
    Assert-That "PUT /favourites/ACB again is idempotent" { Get-Status '/api/v1/favourites/ACB' 'PUT' $auth } "200"
    Assert-That "the favourite is listed" {
        (Invoke-RestMethod "$baseUrl/api/v1/favourites" -Headers $auth).data.Count } "1"

    # Favourites paging was hard-coded: nextCursor null, hasMore false, and "total" set to the size
    # of the page rather than the list. A user with more favourites than one page could not reach
    # them, and every existing test used the default page size against one favourite - so the page
    # was always the whole list and the lie was always accidentally true.
    Invoke-WebRequest "$baseUrl/api/v1/favourites/ACM" -Method Put -Headers $auth -UseBasicParsing | Out-Null
    Invoke-WebRequest "$baseUrl/api/v1/favourites/AH1" -Method Put -Headers $auth -UseBasicParsing | Out-Null

    $page1 = Invoke-RestMethod "$baseUrl/api/v1/favourites?pageSize=1" -Headers $auth
    Assert-That "totalCount is the list, not the page" { $page1.pagination.totalCount } "3"
    Assert-That "a partial page reports hasMore" { $page1.pagination.hasMore } "True"

    $page2 = Invoke-RestMethod ("$baseUrl/api/v1/favourites?pageSize=1&cursor=" +
        [uri]::EscapeDataString($page1.pagination.nextCursor)) -Headers $auth
    Assert-That "the cursor reaches a different favourite" {
        $page2.data[0].carParkNo -ne $page1.data[0].carParkNo } "True"
    Assert-That "admin endpoints reject a normal user" { Get-Status '/api/v1/admin/job-runs' 'GET' $auth } "403"

    # A 401 that says only "Unauthorized" is three different mistakes wearing the same face: no
    # token, an expired one, or the refresh token pasted by accident. The server knows which; it
    # used to put the reason in WWW-Authenticate where no browser shows it.
    Assert-That "a 401 says WHY - no token" {
        (Get-Problem '/api/v1/admin/job-runs').detail -like 'No bearer token*' } "True"
    Assert-That "a 401 says WHY - bad token" {
        (Get-Problem '/api/v1/admin/job-runs' @{ Authorization = 'Bearer not.a.token' }).title } "Invalid token"

    # -- 9. The administrator path ------------------------------------------------------------
    # Every admin check above asserts a REJECTION. That is exactly how three documented admin
    # endpoints shipped unreachable: nothing in the solution ever granted UserRoles.Admin, and no
    # test or check ever asked whether an administrator could actually get in.
    Write-Step "9. The seeded administrator can actually use the admin endpoints"
    Write-Host "     Proving the door is locked is not the same as proving the key exists." -ForegroundColor DarkGray

    $adminTokens = Invoke-RestMethod "$baseUrl/api/v1/auth/login" -Method Post -ContentType 'application/json' `
        -Body (@{ email = 'admin@carpark.local'; password = 'Admin!ChangeMe123' } | ConvertTo-Json)

    $adminAuth = @{ Authorization = "Bearer $($adminTokens.accessToken)" }

    Assert-That "the README's admin credentials work" { [bool]$adminTokens.accessToken } "True"
    Assert-That "GET /admin/job-runs as admin" { Get-Status '/api/v1/admin/job-runs' 'GET' $adminAuth } "200"

    $runs = Invoke-RestMethod "$baseUrl/api/v1/admin/job-runs" -Headers $adminAuth
    Assert-That "the ingestion run is reported" { $runs[0].status } "Succeeded"
    Assert-That "enums serialise as names, not ordinals" { $runs[0].status -is [string] } "True"

    $defects = Invoke-RestMethod "$baseUrl/api/v1/admin/job-runs/$($runs[0].id)/defects" -Headers $adminAuth
    Assert-That "the R6 defect report is reachable" { @($defects).Count } "3"
    Assert-That "all three are warnings, not errors" { (@($defects) | Where-Object { $_.severity -ne 'Warning' }).Count } "0"

    # -- 10. Health --------------------------------------------------------------------------
    Write-Step "10. Health"
    Assert-That "liveness" { (Get-Json '/api/v1/health/live').status } "Healthy"
    Assert-That "readiness reports a fresh feed" { (Get-Json '/api/v1/health/ready').feed.isFresh } "True"
}
finally {
    if ($apiProc -and -not $apiProc.HasExited) {
        Stop-Process -Id $apiProc.Id -Force -ErrorAction SilentlyContinue
    }
    Remove-Item $dbPath, "$dbPath-shm", "$dbPath-wal" -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $root 'smoke-api.log'), (Join-Path $root 'smoke-api.log.err') -ErrorAction SilentlyContinue
    Remove-Item Env:\ConnectionStrings__CarparkDatabase -ErrorAction SilentlyContinue
}

Write-Host ""
if ($failures.Count -gt 0) {
    Write-Host "$($failures.Count) check(s) failed:" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    Write-Host "`nA reviewer cloning this repository would hit the same problem.`n" -ForegroundColor Red
    exit 1
}

Write-Host "All checks passed." -ForegroundColor Green
Write-Host "A reviewer cloning this repository gets a working system.`n" -ForegroundColor Green
exit 0
