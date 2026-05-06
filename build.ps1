<#
.SYNOPSIS
    Build, run, and publish RemoteFlattener.

.EXAMPLE
    .\build.ps1           # build (debug)
    .\build.ps1 run       # build + launch
    .\build.ps1 publish   # single-file release exe → publish\
    .\build.ps1 clean     # remove bin/obj/publish
    .\build.ps1 watch     # poll every 60 s; rebuild + restart on upstream commits or local file changes
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

function Start-RemoteFlattenerIfNotRunning {
    if (-not (Test-Path $pubExe)) {
        Write-Warning "Published exe not found at '$pubExe' – skipping launch."
        return
    }
    $running = Get-Process -Name 'RemoteFlattener' -ErrorAction SilentlyContinue
    if (-not $running) {
        Start-Process $pubExe
        Write-Host "  Started RemoteFlattener." -ForegroundColor Green
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

            # Source files to monitor for local changes; excludes bin\ and obj\ output folders.
            $sourceRoot = "$PSScriptRoot\RemoteFlattener"
            $watchExtensions = '*.cs', '*.xaml', '*.csproj', '*.json'

            function Get-LatestSourceChange {
                $watchExtensions | ForEach-Object {
                    Get-ChildItem -Path $sourceRoot -Filter $_ -Recurse -ErrorAction SilentlyContinue |
                        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
                } | Sort-Object LastWriteTime -Descending |
                  Select-Object -First 1 -ExpandProperty LastWriteTime
            }

            # Seed $lastBuildTime from the published exe (or epoch when it doesn't exist yet).
            $lastBuildTime = if (Test-Path $pubExe) {
                (Get-Item $pubExe).LastWriteTime
            } else {
                [datetime]::MinValue
            }
            Write-Host "  Last build: $(if ($lastBuildTime -eq [datetime]::MinValue) { 'never' } else { $lastBuildTime.ToString('HH:mm:ss') })" -ForegroundColor DarkGray

            # Do an initial fetch so the first comparison is meaningful.
            git fetch --quiet 2>&1 | Out-Null

            while ($true) {
                Write-Host "`n[$(Get-Date -Format 'HH:mm:ss')] Checking for changes..." -ForegroundColor DarkCyan

                $rebuilt = $false

                # ── 1. Upstream commits ──────────────────────────────────────
                git fetch --quiet 2>&1 | Out-Null
                $behind = [int](git rev-list 'HEAD..@{u}' --count 2>$null)

                if ($behind -gt 0) {
                    Write-Host "  $behind new commit(s) upstream – pulling..." -ForegroundColor Cyan
                    git pull
                    if ($LASTEXITCODE -ne 0) {
                        Write-Warning "git pull failed (dirty working tree or conflict?) – will still check local changes."
                    }
                    else {
                        Write-Host "  Publishing..." -ForegroundColor Cyan
                        try {
                            Stop-RemoteFlattener
                            Invoke-Publish
                            $lastBuildTime = Get-Date
                            Start-RemoteFlattener
                            $rebuilt = $true
                            Write-Host "  Done." -ForegroundColor Green
                        }
                        catch {
                            Write-Warning "Build failed: $_"
                            Write-Warning "Will retry on next change."
                        }
                    }
                }

                # ── 2. Local file changes ────────────────────────────────────
                # Runs even when a pull failed so local edits are not silently skipped.
                if (-not $rebuilt) {
                    $latestChange = Get-LatestSourceChange
                    if ($latestChange -and $latestChange -gt $lastBuildTime) {
                        Write-Host "  Local change detected (newest file: $($latestChange.ToString('HH:mm:ss'))) – rebuilding..." -ForegroundColor Cyan
                        try {
                            Stop-RemoteFlattener
                            Invoke-Publish
                            $lastBuildTime = Get-Date
                            Start-RemoteFlattener
                            $rebuilt = $true
                            Write-Host "  Done." -ForegroundColor Green
                        }
                        catch {
                            Write-Warning "Build failed: $_"
                            Write-Warning "Will retry on next change."
                        }
                    }
                }

                # ── 3. No rebuild – ensure app is still running ──────────────
                if (-not $rebuilt) {
                    Write-Host "  No changes." -ForegroundColor DarkGray
                    Start-RemoteFlattenerIfNotRunning
                }

                Start-Sleep -Seconds $PollSeconds
            }
        }
        finally {
            Pop-Location
        }
    }
}
