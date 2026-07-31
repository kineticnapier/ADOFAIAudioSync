<#
.SYNOPSIS
Builds and packages ADOFAI AudioSync on Windows.

.EXAMPLE
.\build.ps1

.EXAMPLE
.\build.ps1 -GameManagedDir "D:\SteamLibrary\steamapps\common\A Dance of Fire and Ice\A Dance of Fire and Ice_Data\Managed"

.EXAMPLE
.\build.ps1 -DeployDir "C:\Program Files (x86)\Steam\steamapps\common\A Dance of Fire and Ice\Mods\ADOFAIAudioSync"
#>

[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$GameManagedDir = $env:ADOFAI_GAME_MANAGED_DIR,

    [string]$MSBuildPath,

    [string]$DeployDir,

    [switch]$SkipPackage
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-ManagedDirectory {
    param([string]$RequestedPath)

    $candidates = @()
    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $candidates += $RequestedPath
    }

    $candidates += @(
        "C:\Program Files (x86)\Steam\steamapps\common\A Dance of Fire and Ice\A Dance of Fire and Ice_Data\Managed",
        "C:\Program Files\Steam\steamapps\common\A Dance of Fire and Ice\A Dance of Fire and Ice_Data\Managed"
    )

    foreach ($candidate in $candidates) {
        if ([string]::IsNullOrWhiteSpace($candidate)) {
            continue
        }

        if (Test-Path -LiteralPath (Join-Path $candidate "Assembly-CSharp.dll") -PathType Leaf) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    throw @"
ADOFAI's Managed directory was not found.
Pass -GameManagedDir or set ADOFAI_GAME_MANAGED_DIR.
Example:
  .\build.ps1 -GameManagedDir "D:\SteamLibrary\steamapps\common\A Dance of Fire and Ice\A Dance of Fire and Ice_Data\Managed"
"@
}

function Resolve-MSBuild {
    param([string]$RequestedPath)

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        if (-not (Test-Path -LiteralPath $RequestedPath -PathType Leaf)) {
            throw "MSBuild was not found at '$RequestedPath'."
        }
        return (Resolve-Path -LiteralPath $RequestedPath).Path
    }

    $command = Get-Command "MSBuild.exe" -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $vswhereCandidates = @()
    if (-not [string]::IsNullOrWhiteSpace(${env:ProgramFiles(x86)})) {
        $vswhereCandidates += Join-Path ${env:ProgramFiles(x86)} `
            "Microsoft Visual Studio\Installer\vswhere.exe"
    }
    if (-not [string]::IsNullOrWhiteSpace($env:ProgramFiles)) {
        $vswhereCandidates += Join-Path $env:ProgramFiles `
            "Microsoft Visual Studio\Installer\vswhere.exe"
    }

    foreach ($vswhere in $vswhereCandidates) {
        if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) {
            continue
        }

        $found = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild `
            -find "MSBuild\**\Bin\MSBuild.exe" |
            Select-Object -First 1
        if (-not [string]::IsNullOrWhiteSpace($found) -and
            (Test-Path -LiteralPath $found -PathType Leaf)) {
            return (Resolve-Path -LiteralPath $found).Path
        }
    }

    throw @"
MSBuild.exe was not found.
Install Visual Studio 2022 Build Tools with the '.NET desktop build tools' workload,
or pass -MSBuildPath explicitly.
"@
}

function Assert-GameReferences {
    param([string]$ManagedDirectory)

    $requiredFiles = @(
        "Assembly-CSharp.dll",
        "RDTools.dll",
        "Newtonsoft.Json.dll",
        "UnityEngine.CoreModule.dll",
        "UnityEngine.AudioModule.dll",
        "UnityEngine.IMGUIModule.dll",
        "UnityEngine.InputLegacyModule.dll",
        "UnityModManager\UnityModManager.dll",
        "UnityModManager\0Harmony.dll"
    )

    $missing = @()
    foreach ($relativePath in $requiredFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $ManagedDirectory $relativePath) -PathType Leaf)) {
            $missing += $relativePath
        }
    }

    if ($missing.Count -gt 0) {
        throw "Required game references are missing:`n  $($missing -join "`n  ")"
    }
}

function Get-ModVersion {
    param([string]$ProjectDirectory)

    $assemblyInfoPath = Join-Path $ProjectDirectory "Properties\AssemblyInfo.cs"
    $source = [System.IO.File]::ReadAllText($assemblyInfoPath)
    $match = [regex]::Match(
        $source,
        'AssemblyFileVersion\("(?<version>\d+\.\d+\.\d+)(?:\.\d+)?"\)'
    )
    if (-not $match.Success) {
        throw "Could not read the version from '$assemblyInfoPath'."
    }
    return $match.Groups["version"].Value
}

function Write-ModInfo {
    param(
        [string]$DestinationPath,
        [string]$Version
    )

    $modInfo = [ordered]@{
        Id = "ADOFAIAudioSync"
        DisplayName = "ADOFAI AudioSync"
        Author = "kineticnapier"
        Version = $Version
        AssemblyName = "ADOFAIAudioSync.dll"
        EntryMethod = "Kiner.ADOFAIAudioSync.Main.Load"
        HomePage = "https://github.com/kineticnapier/ADOFAIAudioSync"
    }

    $json = $modInfo | ConvertTo-Json
    $utf8WithoutBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText(
        $DestinationPath,
        $json + [Environment]::NewLine,
        $utf8WithoutBom
    )
}

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectPath = Join-Path $scriptRoot "src\ADOFAIAudioSync.csproj"
if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
    throw "ADOFAIAudioSync.csproj was not found at '$projectPath'."
}

$projectDirectory = Split-Path -Parent $projectPath
$managedDirectory = Resolve-ManagedDirectory -RequestedPath $GameManagedDir
$resolvedMSBuild = Resolve-MSBuild -RequestedPath $MSBuildPath
$version = Get-ModVersion -ProjectDirectory $projectDirectory

Assert-GameReferences -ManagedDirectory $managedDirectory

Write-Host "Building ADOFAIAudioSync v$version ($Configuration)"
Write-Host "Project : $projectPath"
Write-Host "Managed : $managedDirectory"
Write-Host "MSBuild : $resolvedMSBuild"

$msbuildArguments = @(
    $projectPath,
    "/t:Rebuild",
    "/m",
    "/nologo",
    "/verbosity:minimal",
    "/p:Configuration=$Configuration",
    "/p:Platform=AnyCPU",
    "/p:GameManagedDir=$managedDirectory"
)

& $resolvedMSBuild @msbuildArguments
if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE."
}

$outputDirectory = Join-Path $projectDirectory "bin\$Configuration"
$dllPath = Join-Path $outputDirectory "ADOFAIAudioSync.dll"
$pdbPath = Join-Path $outputDirectory "ADOFAIAudioSync.pdb"

if (-not (Test-Path -LiteralPath $dllPath -PathType Leaf)) {
    throw "Build succeeded, but '$dllPath' was not created."
}

if (-not $SkipPackage) {
    $artifactsDirectory = Join-Path $scriptRoot "artifacts"
    $packageName = "ADOFAIAudioSync-v$version"
    $packageDirectory = Join-Path $artifactsDirectory $packageName
    $zipPath = Join-Path $artifactsDirectory "$packageName.zip"

    New-Item -ItemType Directory -Path $artifactsDirectory -Force | Out-Null
    if (Test-Path -LiteralPath $packageDirectory) {
        Remove-Item -LiteralPath $packageDirectory -Recurse -Force
    }
    if (Test-Path -LiteralPath $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }

    New-Item -ItemType Directory -Path $packageDirectory | Out-Null
    Copy-Item -LiteralPath $dllPath -Destination $packageDirectory
    if (Test-Path -LiteralPath $pdbPath -PathType Leaf) {
        Copy-Item -LiteralPath $pdbPath -Destination $packageDirectory
    }
    Write-ModInfo -DestinationPath (Join-Path $packageDirectory "Info.json") `
        -Version $version

    Compress-Archive -Path (Join-Path $packageDirectory "*") `
        -DestinationPath $zipPath -CompressionLevel Optimal
    Write-Host "Package : $zipPath"
}

if (-not [string]::IsNullOrWhiteSpace($DeployDir)) {
    New-Item -ItemType Directory -Path $DeployDir -Force | Out-Null
    Copy-Item -LiteralPath $dllPath -Destination $DeployDir -Force
    if (Test-Path -LiteralPath $pdbPath -PathType Leaf) {
        Copy-Item -LiteralPath $pdbPath -Destination $DeployDir -Force
    }
    Write-ModInfo -DestinationPath (Join-Path $DeployDir "Info.json") `
        -Version $version
    Write-Host "Deployed: $DeployDir"
}

Write-Host "Build completed successfully."
