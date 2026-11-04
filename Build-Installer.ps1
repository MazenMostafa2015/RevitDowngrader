<#
.SYNOPSIS
  One-click build: restores NuGet packages, compiles the WPF app and the Revit add-ins with MSBuild
  (via the dotnet CLI), obtains Inno Setup if needed, and produces dist\RevitDowngrader-Setup.exe

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\Build-Installer.ps1

.EXAMPLE
  # Revit 2027 on a different runtime than .NET 8 (check Autodesk's SDK notes for the right value):
  powershell -ExecutionPolicy Bypass -File .\Build-Installer.ps1 -NewerYears 2024,2025,2026,2027 -TfmOverride @{2027='net10.0-windows'}
#>
[CmdletBinding()]
param(
    [string]$Version = '1.0.0',
    [int[]]$NewerYears = @(2024, 2025, 2026),
    [hashtable]$TfmOverride = @{}
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$ProgressPreference = 'SilentlyContinue'

$Root       = $PSScriptRoot
$Tools      = Join-Path $Root '.tools'
$Staging    = Join-Path $Root 'staging'
$Dist       = Join-Path $Root 'dist'
$Solution   = Join-Path $Root 'RevitDowngrader.sln'
$AppProj    = Join-Path $Root 'src\RevitDowngrader\RevitDowngrader.csproj'
$AddinProj  = Join-Path $Root 'src\RevitDowngrader.Addin\RevitDowngrader.Addin.csproj'
$LegacyYear = 2023

function Write-Step([string]$Message) {
    Write-Host ""
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Invoke-Checked([string]$Exe, [string[]]$Arguments) {
    & $Exe @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code ${LASTEXITCODE}: $Exe $($Arguments -join ' ')"
    }
}

function Get-AddinTfm([int]$Year) {
    if ($TfmOverride.ContainsKey($Year)) { return [string]$TfmOverride[$Year] }
    # Revit 2023/2024 = .NET Framework 4.8; Revit 2025/2026 = .NET 8.
    # Revit 2027+: verify Autodesk's SDK notes and use -TfmOverride if it differs.
    if ($Year -le 2024) { return 'net48' }
    return 'net8.0-windows'
}

function Get-DotNetExe {
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) {
        $sdks = & $cmd.Source --list-sdks 2>$null
        foreach ($line in @($sdks)) {
            if ($line -match '^(\d+)\.' -and [int]$Matches[1] -ge 8) { return $cmd.Source }
        }
    }

    $localDir = Join-Path $Tools 'dotnet'
    $local = Join-Path $localDir 'dotnet.exe'
    if (-not (Test-Path $local)) {
        Write-Step 'Installing a local .NET SDK 8 into .tools (one time only)'
        New-Item -ItemType Directory -Force -Path $Tools | Out-Null
        $script = Join-Path $Tools 'dotnet-install.ps1'
        Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $script -UseBasicParsing
        & $script -Channel 8.0 -InstallDir $localDir -NoPath
    }
    if (-not (Test-Path $local)) { throw 'Could not find or install the .NET SDK. Install the .NET 8 SDK manually and re-run.' }
    $env:DOTNET_ROOT = $localDir
    return $local
}

function Get-IsccExe {
    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path $Tools 'InnoSetup\ISCC.exe')
    )
    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path $candidate)) { return $candidate }
    }
    $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    Write-Step 'Downloading and installing Inno Setup into .tools (one time only)'
    New-Item -ItemType Directory -Force -Path $Tools | Out-Null
    $installer = Join-Path $Tools 'innosetup-installer.exe'
    $innoDir = Join-Path $Tools 'InnoSetup'
    Invoke-WebRequest -Uri 'https://jrsoftware.org/download.php/is.exe' -OutFile $installer -UseBasicParsing
    $installArgs = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/CURRENTUSER', "/DIR=`"$innoDir`"")
    Start-Process -FilePath $installer -ArgumentList $installArgs -Wait

    $iscc = Join-Path $innoDir 'ISCC.exe'
    if (-not (Test-Path $iscc)) {
        throw 'Inno Setup could not be installed automatically. Install Inno Setup 6 from https://jrsoftware.org/isinfo.php and re-run.'
    }
    return $iscc
}

# ---------------------------------------------------------------------------------------------
$dotnet = Get-DotNetExe
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

Write-Step 'Cleaning staging and dist folders'
if (Test-Path $Staging) { Remove-Item $Staging -Recurse -Force }
if (Test-Path $Dist)    { Remove-Item $Dist -Recurse -Force }
New-Item -ItemType Directory -Force -Path (Join-Path $Staging 'app'), (Join-Path $Staging 'addin'), $Dist | Out-Null

Write-Step 'Restoring NuGet packages'
Invoke-Checked $dotnet @('restore', $Solution)

Write-Step 'Building the WPF application (MSBuild, Release)'
Invoke-Checked $dotnet @('build', $AppProj, '-c', 'Release', '--nologo', "-p:Version=$Version", '-p:DebugType=none')
$appOut = Join-Path $Root 'src\RevitDowngrader\bin\Release\net48'
if (-not (Test-Path (Join-Path $appOut 'RevitDowngrader.exe'))) { throw "Build output not found: $appOut" }
Copy-Item -Path (Join-Path $appOut '*') -Destination (Join-Path $Staging 'app') -Recurse -Force -Exclude '*.pdb'

$years = @($LegacyYear) + @($NewerYears) | Select-Object -Unique
$built = @()
foreach ($year in $years) {
    $tfm = Get-AddinTfm $year
    Write-Step "Building the Revit $year add-in ($tfm)"
    try {
        Invoke-Checked $dotnet @('build', $AddinProj, '-c', 'Release', '--nologo', '--no-incremental',
                                 "-p:RevitYear=$year", "-p:AddinTfm=$tfm")
        $dll = Join-Path $Root "src\RevitDowngrader.Addin\bin\$year\RevitDowngrader.Addin.dll"
        if (-not (Test-Path $dll)) { throw "Add-in output not found: $dll" }
        $dest = Join-Path $Staging "addin\$year"
        New-Item -ItemType Directory -Force -Path $dest | Out-Null
        Copy-Item $dll $dest -Force
        $built += $year
    }
    catch {
        if ($year -eq $LegacyYear) { throw }
        Write-Warning "Skipping the Revit $year add-in: $($_.Exception.Message)"
    }
}
Write-Host "Add-ins built for Revit: $($built -join ', ')" -ForegroundColor Green

$iscc = Get-IsccExe
Write-Step "Compiling the installer with Inno Setup ($iscc)"
Invoke-Checked $iscc @("/DAppVersion=$Version", (Join-Path $Root 'Setup.iss'))

$setupExe = Join-Path $Dist 'RevitDowngrader-Setup.exe'
if (-not (Test-Path $setupExe)) { throw "Installer was not produced: $setupExe" }

$sizeMb = [math]::Round((Get-Item $setupExe).Length / 1MB, 1)
Write-Host ""
Write-Host "SUCCESS: $setupExe ($sizeMb MB)" -ForegroundColor Green
