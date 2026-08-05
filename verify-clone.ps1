#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Clones this repository into an empty folder and follows the README, configuring nothing.

.DESCRIPTION
    smoke.ps1 answers "do the endpoints behave correctly?". This answers a different and narrower
    question: "does a reviewer who clones the repository and types the two commands in the README
    get a working system?"

    The distinction is not academic. smoke.ps1 sets ConnectionStrings__CarparkDatabase to one
    absolute path for both the batch job and the API - and that single environment variable hid a
    defect that broke the reviewer's entire journey. The default connection string was
    "Data Source=carpark.db", a RELATIVE path, and `dotnet run --project X` sets the working
    directory to X's own folder. So the two documented commands used two different databases:

        src/CarparkInfo.BatchJob/carpark.db   1344 KB   2,181 carparks
        src/CarparkInfo.Api/carpark.db           4 KB           0

    Clone, run both commands, and every search returned an empty list. 255 tests passed throughout,
    because every one of them supplies its own connection string - including the smoke test whose
    stated purpose is to prove a reviewer can clone and run.

    A harness that configures what the documentation does not mention cannot detect that the
    documentation is incomplete. This script therefore sets NOTHING: no environment variables, no
    connection string, no arguments the README does not print.

.EXAMPLE
    pwsh ./verify-clone.ps1
#>

[CmdletBinding()]
param(
    [int]$Port = 5299,
    [string]$WorkingDirectory = (Join-Path ([IO.Path]::GetTempPath()) 'carpark-clone-check')
)

$ErrorActionPreference = 'Stop'
$source = $PSScriptRoot
$base = "http://localhost:$Port"
$api = $null
$failures = @()

function Assert-That {
    param([string]$Name, [scriptblock]$Check, [string]$Expected)

    Write-Host -NoNewline ("  {0,-48}" -f $Name)
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

try {
    Write-Host "`nCLONE CHECK - the README, followed literally, configuring nothing" -ForegroundColor White
    Write-Host "Nothing below sets an environment variable or a connection string.`n" -ForegroundColor DarkGray

    # -- 1. Clone ------------------------------------------------------------------------------
    Write-Host "1. Clone into an empty folder" -ForegroundColor Cyan
    if (Test-Path $WorkingDirectory) { Remove-Item $WorkingDirectory -Recurse -Force }
    git clone --quiet $source $WorkingDirectory
    Set-Location $WorkingDirectory

    Assert-That "the dataset is committed" { Test-Path 'hdb-carpark-information-20220824010400.csv' } "True"
    Assert-That "no database is committed" { @(Get-ChildItem -Recurse -Filter *.db -EA SilentlyContinue).Count } "0"

    # -- 2. README step 1 ----------------------------------------------------------------------
    Write-Host "`n2. README step 1 - load the data" -ForegroundColor Cyan
    $out = dotnet run --project src/CarparkInfo.BatchJob -c Release -- `
        --file hdb-carpark-information-20220824010400.csv 2>&1
    Assert-That "the batch job ingests the dataset" {
        ($out | Select-String 'read\s*:\s*(\d+)').Matches.Groups[1].Value } "2181"

    # -- 3. README step 2 ----------------------------------------------------------------------
    Write-Host "`n3. README step 2 - start the API" -ForegroundColor Cyan
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $api = Start-Process -PassThru -FilePath 'dotnet' -ArgumentList @(
        'run', '--project', 'src/CarparkInfo.Api', '--no-launch-profile', '-c', 'Release', '--urls', $base)

    $ready = $false
    foreach ($i in 1..90) {
        Start-Sleep -Seconds 1
        try { Invoke-RestMethod "$base/api/v1/health/live" -TimeoutSec 3 | Out-Null; $ready = $true; break } catch {}
    }
    if (-not $ready) { throw "API did not become ready on $base within 90 seconds." }
    Write-Host "  API is listening on $base" -ForegroundColor Green

    # -- 4. The API serves the data the batch job loaded ---------------------------------------
    Write-Host "`n4. The API serves the data the batch job just loaded" -ForegroundColor Cyan
    Write-Host "     A relative default connection string puts these in different files." -ForegroundColor DarkGray

    # Checked AFTER the API has started, because that is when the second file appears. Run before
    # it, this assertion passes even when the defect is present - which is precisely how a check
    # can look thorough and prove nothing.
    Assert-That "exactly ONE database file exists" {
        @(Get-ChildItem -Recurse -Filter carpark.db).Count } "1"

    Assert-That "the catalogue is not empty" {
        (Invoke-RestMethod "$base/api/v1/carparks?includeTotal=true&pageSize=1").pagination.totalCount } "2181"
    Assert-That "free parking (user story 1)" {
        (Invoke-RestMethod "$base/api/v1/carparks?freeParking=true&includeTotal=true&pageSize=1").pagination.totalCount } "1605"
    Assert-That "night parking (user story 2)" {
        (Invoke-RestMethod "$base/api/v1/carparks?nightParking=true&includeTotal=true&pageSize=1").pagination.totalCount } "1795"
    Assert-That "fits a 2.0 m vehicle (user story 3)" {
        (Invoke-RestMethod "$base/api/v1/carparks?vehicleHeight=2.0&includeTotal=true&pageSize=1").pagination.totalCount } "2056"

    # -- 5. The README's own credentials -------------------------------------------------------
    Write-Host "`n5. The credentials printed in the README" -ForegroundColor Cyan
    $tokens = Invoke-RestMethod "$base/api/v1/auth/login" -Method Post -ContentType 'application/json' `
        -Body '{"email":"admin@carpark.local","password":"Admin!ChangeMe123"}'
    $auth = @{ Authorization = "Bearer $($tokens.accessToken)" }

    Assert-That "the seeded administrator can sign in" { [bool]$tokens.accessToken } "True"

    $runs = Invoke-RestMethod "$base/api/v1/admin/job-runs" -Headers $auth
    Assert-That "the API can see the batch job's run" { @($runs).Count } "1"
    Assert-That "the run succeeded" { $runs[0].status } "Succeeded"
    # Assigned before counting, deliberately. `@(Invoke-RestMethod ... -Headers $auth).Count` binds
    # .Count to the $auth hashtable rather than to the array, so it reports 1 whatever the API
    # returned - a false failure that looks exactly like a real one.
    $defects = Invoke-RestMethod "$base/api/v1/admin/job-runs/$($runs[0].id)/defects" -Headers $auth
    Assert-That "the defect report is reachable" { @($defects).Count } "3"
}
finally {
    if ($api -and -not $api.HasExited) { Stop-Process -Id $api.Id -Force -ErrorAction SilentlyContinue }
    Remove-Item Env:\ASPNETCORE_ENVIRONMENT -ErrorAction SilentlyContinue
    Set-Location $source
}

Write-Host ""
if ($failures.Count -gt 0) {
    Write-Host "$($failures.Count) check(s) failed:" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    Write-Host "`nA reviewer cloning this repository would hit the same problem.`n" -ForegroundColor Red
    exit 1
}

Write-Host "All checks passed." -ForegroundColor Green
Write-Host "A fresh clone plus the two README commands gives a working system.`n" -ForegroundColor Green
exit 0
