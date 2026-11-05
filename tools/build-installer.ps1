#requires -Version 5.1
<#
.SYNOPSIS
    Builds dist\ClaudeMemory-Setup.msi and dist\ClaudeMemory-Setup.exe: per-user
    installers for the app and CLI.

.DESCRIPTION
    Runs tools\publish.ps1 (self-contained, compressed, single-file win-x64) and then
    packages the two resulting executables with WiX. WiX is a user-local dotnet tool,
    installed once with:

        dotnet tool install --global wix --version 6.0.2

    (WiX 7 requires accepting a paid maintenance-fee EULA; the 6.0.2 tool is MIT and
    pinned. The BootstrapperApplications extension package used for the .exe
    installer ships under the Open Source Maintenance Fee EULA - free for users who
    do not generate revenue with it; see PRD section 11.)

    Nothing here needs elevation: files go to %LOCALAPPDATA%\Programs\claude-memory
    and the Start Menu shortcut is per-user. The package authors no HKLM content;
    the Windows Installer service itself writes the standard uninstall entry under
    HKLM for every MSI (OS behavior, documented in the PRD), while the .exe
    bootstrapper registers its own uninstall entry under HKCU.

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

# 4. Wrap the MSI in a Burn bootstrapper: dist\ClaudeMemory-Setup.exe.
#    The standard bootstrapper application lives in the BootstrapperApplications
#    extension. `wix extension add` is not used (it fails silently on some
#    machines); instead the pinned NuGet package is fetched once and its
#    extension DLL is passed to wix build by path, which needs no global state.
$balVersion = '6.0.2'
$balCache = Join-Path $env:TEMP 'claude-memory-wixext'
$balDll = Join-Path $balCache "ba\$balVersion\wixext6\WixToolset.BootstrapperApplications.wixext.dll"
if (-not (Test-Path -LiteralPath $balDll)) {
    Remove-Item $balCache -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path (Join-Path $balCache "ba\$balVersion") | Out-Null
    $zip = Join-Path $balCache 'bootstrapperapplications.zip'
    Invoke-WebRequest `
        -Uri "https://api.nuget.org/v3-flatcontainer/wixtoolset.bootstrapperapplications.wixext/$balVersion/wixtoolset.bootstrapperapplications.wixext.$balVersion.nupkg" `
        -OutFile $zip -TimeoutSec 120
    Expand-Archive -Path $zip -DestinationPath (Join-Path $balCache "ba\$balVersion") -Force
}
if (-not (Test-Path -LiteralPath $balDll)) {
    throw "BootstrapperApplications extension dll is missing: $balDll"
}

$setupExe = Join-Path $repo 'dist\ClaudeMemory-Setup.exe'
$bundleWxs = Join-Path $PSScriptRoot 'installer\Bundle.wxs'
$licenseRtf = Join-Path $PSScriptRoot 'installer\license.rtf'
wix build `
    -arch x64 `
    -ext $balDll `
    -d "AppVersion=$appVersion" `
    -d "AppIconSource=$appIcon" `
    -d "LicenseRtf=$licenseRtf" `
    -d "MsiSource=$msi" `
    -out $setupExe `
    $bundleWxs
if ($LASTEXITCODE -ne 0) {
    throw "wix bundle build failed (exit $LASTEXITCODE)."
}

Remove-Item (Join-Path $repo 'dist\ClaudeMemory-Setup.wixpdb') -Force -ErrorAction SilentlyContinue
Write-Host ('installer {0} + bootstrapper {1} (version {2})' -f $msi, $setupExe, $appVersion)
