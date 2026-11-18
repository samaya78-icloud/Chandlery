<#
.SYNOPSIS
  Runs Chandlery as a persistent site on this PC and publishes it through the existing Cloudflare tunnel.

.DESCRIPTION
  Install  (once, elevated)  builds the app into C:\Chandlery\app, creates the chandlery_live database and its
                             private settings, registers a "Chandlery Server" boot task (SYSTEM, restarts on failure),
                             and reminds you to add the tunnel route in the Cloudflare dashboard.
  Update   (elevated)        rebuilds and republishes the code and restarts the site. Data and uploads are kept.
  Status                     shows whether the task, the app and the tunnel are up.

  Run from an elevated PowerShell:   powershell -ExecutionPolicy Bypass -File deploy\Chandlery-Host.ps1 -Action Install
#>
param(
    [ValidateSet('Install', 'Update', 'Status')][string]$Action = 'Update',
    [string]$Hostname = 'chandlery.sdtshop.co.in',
    [int]$Port = 5100,
    [string]$Root = 'C:\Chandlery',
    [string]$Database = 'chandlery_live',
    [string]$TunnelName = 'shoppos',
    [string]$TunnelTask = 'ShopPOS Tunnel',
    [string]$CloudflaredDir = "$env:USERPROFILE\.cloudflared"
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$appDir = Join-Path $Root 'app'
$taskName = 'Chandlery Server'
$cloudflared = 'C:\Program Files (x86)\cloudflared\cloudflared.exe'

function Test-Admin {
    ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Wait-Site {
    $url = "http://127.0.0.1:$Port/login"
    for ($i = 0; $i -lt 45; $i++) {
        try { $r = Invoke-WebRequest $url -UseBasicParsing -TimeoutSec 5; if ($r.StatusCode -eq 200) { return $true } } catch { }
        Start-Sleep -Seconds 2
    }
    return $false
}

function Stop-Site {
    if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) { Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue }
    Get-Process -Name Chandlery -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$appDir\*" } | Stop-Process -Force
    Start-Sleep -Seconds 2
}

function Publish-App {
    Write-Host '== Building styles and publishing' -ForegroundColor Cyan
    Push-Location $repo
    try {
        npm run css:build | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'npm run css:build failed' }
        Stop-Site
        dotnet publish Chandlery.csproj -c Release -o $appDir --nologo -v q
        if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
        # The developer's connection string must not travel with the published copy.
        Get-ChildItem $appDir -Filter 'appsettings.Development*.json' | Remove-Item -Force
    } finally { Pop-Location }
}

function Write-Settings {
    $prod = Join-Path $appDir 'appsettings.Production.json'
    if (Test-Path $prod) { Write-Host "== Keeping existing $prod"; return }
    Write-Host '== Creating database and private settings' -ForegroundColor Cyan

    $dev = Get-Content (Join-Path $repo 'appsettings.Development.json') -Raw | ConvertFrom-Json
    $conn = $dev.ConnectionStrings.Chandlery
    if ($conn -match 'CHANGE_ME') { throw 'appsettings.Development.json still has the placeholder database password.' }
    $parts = @{}
    foreach ($kv in $conn.Split(';', [StringSplitOptions]::RemoveEmptyEntries)) { $k, $v = $kv.Split('=', 2); $parts[$k.Trim()] = $v }
    $psql = Get-ChildItem 'C:\Program Files\PostgreSQL\*\bin\psql.exe' | Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $psql) { throw 'psql.exe not found under C:\Program Files\PostgreSQL.' }

    $env:PGPASSWORD = $parts['Password']
    try {
        $exists = & $psql.FullName -h $parts['Host'] -p $parts['Port'] -U $parts['Username'] -d postgres -tAc "SELECT 1 FROM pg_database WHERE datname='$Database'"
        if (-not $exists) { & $psql.FullName -h $parts['Host'] -p $parts['Port'] -U $parts['Username'] -d postgres -c "CREATE DATABASE $Database" | Out-Null }
    } finally { Remove-Item Env:PGPASSWORD }

    $liveConn = ($conn -replace '(?i)Database=[^;]*', "Database=$Database")
    $bytes = New-Object byte[] 32   # Windows PowerShell 5.1 has no static RandomNumberGenerator.GetBytes
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create(); $rng.GetBytes($bytes); $rng.Dispose()
    $key = [Convert]::ToBase64String($bytes)
    [ordered]@{
        AllowedHosts      = "$Hostname;localhost;127.0.0.1"
        ConnectionStrings = [ordered]@{ Chandlery = $liveConn }
        Database          = [ordered]@{ AutoMigrate = $true }
        Crew              = [ordered]@{ IdKey = $key }
    } | ConvertTo-Json -Depth 4 | Set-Content -Path $prod -Encoding UTF8

    # Only SYSTEM, Administrators and the current user may read it (it holds the DB password and the crew-ID key).
    icacls $prod /inheritance:r /grant:r '*S-1-5-18:(F)' '*S-1-5-32-544:(F)' "${env:USERNAME}:(F)" | Out-Null
}

function Register-SiteTask {
    Write-Host '== Registering the boot task' -ForegroundColor Cyan
    New-Item -ItemType Directory -Force (Join-Path $Root 'logs') | Out-Null
    $run = Join-Path $Root 'run.ps1'
    @"
`$env:ASPNETCORE_ENVIRONMENT = 'Production'
`$env:ASPNETCORE_URLS = 'http://127.0.0.1:$Port'
`$env:ASPNETCORE_FORWARDEDHEADERS_ENABLED = 'true'   # behind the Cloudflare tunnel the browser's scheme is https
Set-Location '$appDir'
& '$appDir\Chandlery.exe' *>> '$Root\logs\chandlery.log'
"@ | Set-Content -Path $run -Encoding UTF8

    $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$run`""
    $trigger = New-ScheduledTaskTrigger -AtStartup
    $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable `
        -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit ([TimeSpan]::Zero)
    Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
}

function Add-TunnelRoute {
    # The shoppos tunnel is managed in the Cloudflare dashboard, so config.yml is ignored and the route cannot be added from here.
    # Add it once under Networking > Tunnels > shoppos > Routes > Add route > Published application:
    #   chandlery.sdtshop.co.in  ->  http://127.0.0.1:5100   (this also creates the DNS record)
    # and add the hostname to the Access application so the email policy applies.
    Write-Host "== Tunnel route: add $Hostname -> http://127.0.0.1:$Port in the Cloudflare dashboard (see the comment in this script)" -ForegroundColor Yellow
}

function Show-Status {
    $t = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    "Task '$taskName': " + $(if ($t) { $t.State } else { 'not installed' })
    "App on 127.0.0.1:${Port}: " + $(try { (Invoke-WebRequest "http://127.0.0.1:$Port/login" -UseBasicParsing -TimeoutSec 5).StatusCode } catch { 'not answering' })
    "Tunnel '$TunnelTask': " + $(try { (Get-ScheduledTask -TaskName $TunnelTask).State } catch { 'not found' })
    "Public https://${Hostname}: " + $(try { (Invoke-WebRequest "https://$Hostname/login" -UseBasicParsing -TimeoutSec 10 -MaximumRedirection 0).StatusCode } catch { $_.Exception.Message })
}

if ($Action -eq 'Status') { Show-Status; return }
if (-not (Test-Admin)) { throw 'Run this from an elevated PowerShell (Run as administrator).' }

New-Item -ItemType Directory -Force $appDir | Out-Null
Publish-App
Write-Settings

if ($Action -eq 'Install') {
    Register-SiteTask
    Start-ScheduledTask -TaskName $taskName
    Write-Host '== Waiting for the site to answer' -ForegroundColor Cyan
    if (-not (Wait-Site)) { throw "The site did not answer on port $Port. See $Root\logs\chandlery.log" }
    Add-TunnelRoute
} else {
    Start-ScheduledTask -TaskName $taskName
    if (-not (Wait-Site)) { throw "The site did not answer on port $Port. See $Root\logs\chandlery.log" }
}
Write-Host ''
Show-Status
