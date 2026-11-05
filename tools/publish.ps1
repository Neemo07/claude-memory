#requires -Version 5.1
<#
.SYNOPSIS
    Publishes self-contained win-x64 single-file builds of the C# app and CLI to dist\.

.DESCRIPTION
    The output needs no Python and no .NET installation on the target machine; the
    WebView2 Runtime is still required for the preview pane (Windows 10 1809+ ships
    it). Each target publishes exactly one compressed .exe; native libraries
    (WebView2Loader.dll) self-extract to a temp folder at startup. Run from the
    repository root:

        powershell -File tools\publish.ps1

    Both folders are removed and rebuilt, so the publish is reproducible.
#>
[CmdletBinding()]
param(
    # Configuration to publish. Release is the only supported value.
    [ValidateSet('Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot

$targets = @(
    @{ Project = 'src\ClaudeMemory.App'; Output = 'dist\ClaudeMemory' },
    @{ Project = 'src\ClaudeMemory.Cli'; Output = 'dist\ClaudeMemory.Cli' }
)

foreach ($target in $targets) {
    $out = Join-Path $repo $target.Output
    if (Test-Path -LiteralPath $out) {
        Remove-Item -LiteralPath $out -Recurse -Force
    }

    dotnet publish (Join-Path $repo $target.Project) `
        -c $Configuration -r win-x64 --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=none `
        -p:AllowedReferenceRelatedFileExtensions=none `
        -o $out
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $($target.Project) (exit $LASTEXITCODE)."
    }

    Write-Host ('published {0}' -f $out)
}
