#requires -Version 5.1
<#
.SYNOPSIS
    Builds dist\ClaudeMemory-Setup.msi: a per-user installer for the app and CLI.

.DESCRIPTION
    Runs tools\publish.ps1 (self-contained, compressed, single-file win-x64) and then
    packages the two resulting executables with WiX. WiX is a user-local dotnet tool,
    installed once with:

        dotnet tool install --global wix --version 6.0.2

    (WiX 7 requires accepting a paid maintenance-fee EULA; 6.0.2 is MIT and pinned.)

    The MSI needs no elevation: files go to %LOCALAPPDATA%\Programs\claude-memory,
    the Start Menu shortcut is per-user, and the uninstall entry is written under
    HKCU (PRD §11 forbids machine-wide/HKLM registration).

    Run from the repository root:

        powershell -File tools\build-installer.ps1
#>
[CmdletBinding()]
param(
    # Configuration to publish. Release is the only supported value.
    [ValidateSet('Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot

# 1. Publish the single-file executables the installer packages.
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'publish.ps1') `
    -Configuration $Configuration
if ($LASTEXITCODE -ne 0) {
    throw "publish.ps1 failed (exit $LASTEXITCODE)."
}

$appExe = Join-Path $repo 'dist\ClaudeMemory\ClaudeMemory.exe'
$cliExe = Join-Path $repo 'dist\ClaudeMemory.Cli\ClaudeMemory.Cli.exe'
foreach ($exe in @($appExe, $cliExe)) {
    if (-not (Test-Path -LiteralPath $exe)) {
        throw "expected publish output is missing: $exe"
    }
}

# 2. MSI ProductVersion from the built app (2.0.0 -> "2.0.0"; drop a trailing .0).
$rawVersion = (Get-Item -LiteralPath $appExe).VersionInfo.FileVersion
if ($rawVersion -notmatch '^(\d+\.\d+\.\d+)') {
    throw "could not parse a ProductVersion from FileVersion '$rawVersion'."
}
$appVersion = $Matches[1]

# 3. Build the MSI next to the other dist outputs.
$msi = Join-Path $repo 'dist\ClaudeMemory-Setup.msi'
$appDir = Split-Path -Parent $appExe
$cliDir = Split-Path -Parent $cliExe
# The Icon table entry gets the small .ico, not the executable: pointing the Icon
# element at the .exe made WiX embed the whole 70 MB executable as the icon stream.
$appIcon = Join-Path $repo 'src\ClaudeMemory.App\app.ico'
wix build `
    -arch x64 `
    -d "AppVersion=$appVersion" `
    -d "AppIconSource=$appIcon" `
    -bindpath "app=$appDir" `
    -bindpath "cli=$cliDir" `
    -out $msi `
    (Join-Path $PSScriptRoot 'installer\Package.wxs')
if ($LASTEXITCODE -ne 0) {
    throw "wix build failed (exit $LASTEXITCODE)."
}

Write-Host ('installer {0} (version {1})' -f $msi, $appVersion)
