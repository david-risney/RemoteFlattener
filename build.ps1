<#
.SYNOPSIS
    Build, run, and publish RemoteFlattener.

.EXAMPLE
    .\build.ps1           # build (debug)
    .\build.ps1 run       # build + launch
    .\build.ps1 publish   # single-file release exe → publish\
    .\build.ps1 clean     # remove bin/obj/publish
#>
param(
    [ValidateSet('build', 'run', 'publish', 'clean')]
    [string] $Task = 'build'
)

$ErrorActionPreference = 'Stop'
$proj = "$PSScriptRoot\RemoteFlattener\RemoteFlattener.csproj"

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
        dotnet publish $proj -p:PublishProfile=SingleFile
        if ($LASTEXITCODE -eq 0) {
            Write-Host "Published to: $PSScriptRoot\publish\RemoteFlattener.exe" -ForegroundColor Green
        }
    }
    'clean' {
        Remove-Item "$PSScriptRoot\RemoteFlattener\bin"     -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item "$PSScriptRoot\RemoteFlattener\obj"     -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item "$PSScriptRoot\publish"                 -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "Cleaned." -ForegroundColor Green
    }
}
