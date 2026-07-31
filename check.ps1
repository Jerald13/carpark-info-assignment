#!/usr/bin/env pwsh
# Runs exactly what CI runs. Use this before every commit.
#
# Written because every commit up to 199711a failed CI on a gate that had never been run
# locally: `dotnet build` and `dotnet test` were green while `dotnet format` was not.
# "It builds" is not the same as "CI passes".

$ErrorActionPreference = 'Continue'
$failed = @()

function Invoke-Gate([string]$Name, [scriptblock]$Command) {
    Write-Host -NoNewline ("{0,-32}" -f $Name)
    $output = & $Command 2>&1
    if ($LASTEXITCODE -eq 0) {
        Write-Host "PASS" -ForegroundColor Green
    } else {
        Write-Host "FAIL" -ForegroundColor Red
        $script:failed += $Name
        $output | Select-Object -Last 15 | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
    }
}

Write-Host "`nRunning the CI gates locally`n" -ForegroundColor Cyan

Invoke-Gate "1. restore"            { dotnet restore }
Invoke-Gate "2. format"             { dotnet format --verify-no-changes --no-restore }
Invoke-Gate "3. build (Release)"    { dotnet build --no-restore --configuration Release -warnaserror }
Invoke-Gate "4. test"               { dotnet test --no-build --configuration Release }

Write-Host -NoNewline ("{0,-32}" -f "5. vulnerable packages")
$audit = dotnet list package --vulnerable --include-transitive 2>&1 | Out-String
if ($audit -match 'has the following vulnerable') {
    Write-Host "FAIL" -ForegroundColor Red
    $failed += "5. vulnerable packages"
    Write-Host $audit -ForegroundColor DarkGray
} else {
    Write-Host "PASS" -ForegroundColor Green
}

Write-Host ""
if ($failed.Count -gt 0) {
    Write-Host "$($failed.Count) gate(s) failed. Do not commit." -ForegroundColor Red
    Write-Host ($failed -join "`n") -ForegroundColor Red
    exit 1
}

Write-Host "All gates passed. Safe to commit." -ForegroundColor Green
exit 0
