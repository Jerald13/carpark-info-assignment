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

    # -- 6. The user stories -----------------------------------------------------------------
    Write-Step "6. The three user stories return the documented counts"
    Assert-That "whole catalogue" { (Get-Json '/api/v1/carparks?includeTotal=true&pageSize=1').pagination.totalCount } "2181"
    Assert-That "free parking (R10)" { (Get-Json '/api/v1/carparks?freeParking=true&includeTotal=true&pageSize=1').pagination.totalCount } "1605"
    Assert-That "night parking (R11)" { (Get-Json '/api/v1/carparks?nightParking=true&includeTotal=true&pageSize=1').pagination.totalCount } "1795"
    Assert-That "fits a 2.0 m vehicle (R12)" { (Get-Json '/api/v1/carparks?vehicleHeight=2.0&includeTotal=true&pageSize=1').pagination.totalCount } "2056"
    Assert-That "all three combined" { (Get-Json '/api/v1/carparks?freeParking=true&nightParking=true&vehicleHeight=2.0&includeTotal=true&pageSize=1').pagination.totalCount } "1348"
    Assert-That "comma-separated carParkType" { (Get-Json '/api/v1/carparks?carParkType=MULTI_STOREY,BASEMENT&includeTotal=true&pageSize=1').pagination.totalCount } "1071"

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
    Assert-That "admin endpoints reject a normal user" { Get-Status '/api/v1/admin/job-runs' 'GET' $auth } "403"

    # -- 9. Health ---------------------------------------------------------------------------
    Write-Step "9. Health"
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
