<#
.SYNOPSIS
    Build, run, and publish RemoteFlattener.

.EXAMPLE
    .\build.ps1           # build (debug)
    .\build.ps1 run       # build + launch
    .\build.ps1 publish   # single-file release exe → publish\
    .\build.ps1 clean     # remove bin/obj/publish
    .\build.ps1 watch     # poll git every 60 s; pull → rebuild → restart on changes
#>
param(
    [ValidateSet('build', 'run', 'publish', 'clean', 'watch')]
    [string] $Task = 'build',

    # How often (seconds) the watch loop polls for upstream changes.
    [int] $PollSeconds = 60
)

$ErrorActionPreference = 'Stop'
$proj    = "$PSScriptRoot\RemoteFlattener\RemoteFlattener.csproj"
$pubExe  = "$PSScriptRoot\publish\RemoteFlattener.exe"

# ── Ensure .NET 8 SDK is installed ──────────────────────────────────────────
function Ensure-DotnetSdk {
    $sdkAvailable = $false
    try {
        $sdks = & dotnet --list-sdks 2>$null
        if ($LASTEXITCODE -eq 0 -and $sdks -match '^8\.') {
            $sdkAvailable = $true
        }
    } catch { }

    if (-not $sdkAvailable) {
        Write-Host ".NET 8 SDK not found – installing via winget..." -ForegroundColor Yellow
        winget install Microsoft.DotNet.SDK.8 --accept-source-agreements --accept-package-agreements
        if ($LASTEXITCODE -ne 0) { throw "Failed to install .NET 8 SDK." }
        # Refresh PATH so dotnet is available in this session
        $env:Path = [System.Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' +
                    [System.Environment]::GetEnvironmentVariable('Path', 'User')
        Write-Host ".NET 8 SDK installed." -ForegroundColor Green
    }
}

function Invoke-Restore {
    dotnet restore $proj
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed (exit $LASTEXITCODE)." }
}

Ensure-DotnetSdk
Invoke-Restore

function Invoke-Publish {
    dotnet publish $proj -p:PublishProfile=SingleFile
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }
}

function Stop-RemoteFlattener {
    $procs = Get-Process -Name 'RemoteFlattener' -ErrorAction SilentlyContinue
    if ($procs) {
        $procs | Stop-Process -Force
        # Give the OS a moment to release file handles before we overwrite the exe.
        Start-Sleep -Milliseconds 500
        Write-Host "  Stopped $($procs.Count) RemoteFlattener process(es)." -ForegroundColor Yellow
    }
}

function Start-RemoteFlattener {
    if (-not (Test-Path $pubExe)) {
        Write-Warning "Published exe not found at '$pubExe' – skipping launch."
        return
    }
    Start-Process $pubExe
    Write-Host "  Started RemoteFlattener." -ForegroundColor Green
}

switch ($Task) {
    'build' {
        dotnet build $proj -c Debug
    }
    'run' {
        dotnet build $proj -c Debug
        if ($LASTEXITCODE -eq 0) {
            $exe = "$PSScriptRoot\RemoteFlattener\bin\Debug\net8.0-windows10.0.19041.0\RemoteFlattener.exe"
            Start-Process $exe
        }
    }
    'publish' {
        Invoke-Publish
        Write-Host "Published to: $pubExe" -ForegroundColor Green
    }
    'clean' {
        Remove-Item "$PSScriptRoot\RemoteFlattener\bin"     -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item "$PSScriptRoot\RemoteFlattener\obj"     -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item "$PSScriptRoot\publish"                 -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "Cleaned." -ForegroundColor Green
    }
    'watch' {
        Push-Location $PSScriptRoot
        try {
            Write-Host "Watch mode started (polling every $PollSeconds s).  Press Ctrl+C to stop." -ForegroundColor Cyan

            # Do an initial fetch so the first comparison is meaningful.
            git fetch --quiet 2>&1 | Out-Null

            while ($true) {
                Write-Host "`n[$(Get-Date -Format 'HH:mm:ss')] Checking for upstream changes..." -ForegroundColor DarkCyan

                git fetch --quiet 2>&1 | Out-Null

                # Count commits that are on origin but not local.
                $behind = [int](git rev-list 'HEAD..@{u}' --count 2>$null)

                if ($behind -gt 0) {
                    Write-Host "  $behind new commit(s) found – pulling..." -ForegroundColor Cyan
                    git pull
                    if ($LASTEXITCODE -ne 0) {
                        Write-Warning "git pull failed – skipping rebuild this cycle."
                    }
                    else {
                        Write-Host "  Publishing..." -ForegroundColor Cyan
                        Stop-RemoteFlattener
                        Invoke-Publish
                        Start-RemoteFlattener
                        Write-Host "  Done." -ForegroundColor Green
                    }
                }
                else {
                    Write-Host "  Up to date." -ForegroundColor DarkGray
                }

                Start-Sleep -Seconds $PollSeconds
            }
        }
        finally {
            Pop-Location
        }
    }
}
