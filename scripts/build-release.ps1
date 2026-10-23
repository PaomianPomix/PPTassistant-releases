#Requires -Version 5.1
<#
.SYNOPSIS
Build the self-contained Windows x64 application and its per-user installer.
.EXAMPLE
./scripts/build-release.ps1 -Version 1.1.0
.EXAMPLE
./scripts/build-release.ps1 -Version 1.1.0 -DotnetPath C:/dotnet/dotnet.exe -IsccPath 'C:/Program Files (x86)/Inno Setup 6/ISCC.exe'
#>
[CmdletBinding()]
param(
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')]
    [string] $Version = '1.1.0',
    [string] $DotnetPath,
    [string] $IsccPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($env:OS -ne 'Windows_NT') {
    throw 'The Windows installer must be built on Windows.'
}
foreach ($part in $Version.Split('.')) {
    if ([long] $part -gt 65534) { throw 'Each version component must be between 0 and 65534.' }
}

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = Join-Path $repositoryRoot 'artifacts'
$releaseRoot = Join-Path $artifactsRoot 'release'
$projectPath = Join-Path $repositoryRoot 'src/PPTAssistant.App/PPTAssistant.App.csproj'
$installerScript = Join-Path $repositoryRoot 'installer/PPTAssistant.iss'
$iconPath = Join-Path $repositoryRoot 'src/PPTAssistant.App/Assets/PPTA.ico'

function Resolve-BuildTool {
    param([string] $ExplicitPath, [string[]] $Candidates, [string] $ToolName)
    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        if (-not (Test-Path -LiteralPath $ExplicitPath -PathType Leaf)) {
            throw "$ToolName was not found at the supplied path: $ExplicitPath"
        }
        return (Resolve-Path -LiteralPath $ExplicitPath).Path
    }
    foreach ($candidate in $Candidates) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and
            (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }
    $command = Get-Command $ToolName -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $command) { return $command.Source }
    throw "$ToolName is required. Install the documented build prerequisites or supply its path."
}

function Assert-ArtifactPath {
    param([string] $Path)
    $fullPath = [IO.Path]::GetFullPath($Path)
    $prefix = $artifactsRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a path outside the artifacts directory: $fullPath"
    }
    # Reject directory links so cleanup cannot escape into another checkout or user folder.
    $cursor = $fullPath
    while ($cursor.Length -ge $repositoryRoot.Length) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Build output paths must not contain directory links: $cursor"
            }
        }
        if ($cursor -eq $repositoryRoot) { break }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
}

$dotnet = Resolve-BuildTool -ExplicitPath $DotnetPath -Candidates @(
    (Join-Path $repositoryRoot 'tools/dotnet/dotnet.exe')
) -ToolName 'dotnet.exe'
$innoCandidates = @($env:ISCC_PATH, (Join-Path $repositoryRoot 'tools/inno-setup-7.1.0/ISCC.exe'))
foreach ($programFilesPath in @(${env:ProgramFiles(x86)}, $env:ProgramFiles)) {
    if (-not [string]::IsNullOrWhiteSpace($programFilesPath)) {
        $innoCandidates += Join-Path $programFilesPath 'Inno Setup 7/ISCC.exe'
        $innoCandidates += Join-Path $programFilesPath 'Inno Setup 6/ISCC.exe'
    }
}
$iscc = Resolve-BuildTool -ExplicitPath $IsccPath -Candidates $innoCandidates -ToolName 'ISCC.exe'
foreach ($requiredFile in @($projectPath, $installerScript, $iconPath)) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "Required source file is missing: $requiredFile"
    }
}

$buildRoot = Join-Path $artifactsRoot ('build-' + $Version + '-' + [Guid]::NewGuid().ToString('N'))
$publishRoot = Join-Path $buildRoot 'publish'
$installerRoot = Join-Path $buildRoot 'installer'
$installerName = 'PPTAssistant-' + $Version + '-win-x64-Setup.exe'
Assert-ArtifactPath $buildRoot
Assert-ArtifactPath $releaseRoot
New-Item -ItemType Directory -Path $publishRoot, $installerRoot, $releaseRoot -Force | Out-Null

try {
    Push-Location $repositoryRoot
    try {
        $publishArguments = @(
            'publish', $projectPath, '--configuration', 'Release', '--runtime', 'win-x64',
            '--self-contained', 'true', '--output', $publishRoot, '--nologo',
            '-p:PublishTrimmed=false', '-p:PublishSingleFile=false', '-p:PublishReadyToRun=false',
            '-p:DebugSymbols=false', '-p:DebugType=None', '-p:GenerateDocumentationFile=false',
            "-p:Version=$Version", "-p:AssemblyVersion=$Version.0", "-p:FileVersion=$Version.0"
        )
        & $dotnet @publishArguments
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }
    }
    finally { Pop-Location }

    foreach ($requiredBinary in @('PPTAssistant.exe', 'PPTAssistant.dll', 'coreclr.dll', 'hostfxr.dll', 'PresentationFramework.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $publishRoot $requiredBinary) -PathType Leaf)) {
            throw "The self-contained publish output is incomplete: $requiredBinary"
        }
    }
    $unwantedFiles = @(Get-ChildItem -LiteralPath $publishRoot -Recurse -File | Where-Object {
        $_.Extension -eq '.pdb' -or $_.Name -match '(?i)(DevProbe|\.Tests\.|testhost|xunit|nunit|Microsoft\.TestPlatform)'
    })
    if ($unwantedFiles.Count -gt 0) {
        throw ('Publish output includes debug/test files: ' + ($unwantedFiles.Name -join ', '))
    }
    Copy-Item -LiteralPath $iconPath -Destination (Join-Path $publishRoot 'PPTAssistant.ico')

    $compilerArguments = @(
        "/DAppVersion=$Version", "/DPublishDir=$publishRoot", "/DOutputDir=$installerRoot",
        "/DProjectRoot=$repositoryRoot", $installerScript
    )
    & $iscc @compilerArguments
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed with exit code $LASTEXITCODE." }
    $compiledInstaller = Join-Path $installerRoot $installerName
    if (-not (Test-Path -LiteralPath $compiledInstaller -PathType Leaf)) {
        throw "Inno Setup did not produce the expected installer: $installerName"
    }

    $releaseInstaller = Join-Path $releaseRoot $installerName
    Assert-ArtifactPath $releaseInstaller
    Copy-Item -LiteralPath $compiledInstaller -Destination $releaseInstaller -Force
    $checksumPath = Join-Path $releaseRoot 'SHA256SUMS.txt'
    Assert-ArtifactPath $checksumPath
    $checksumLines = @(Get-ChildItem -LiteralPath $releaseRoot -Filter 'PPTAssistant-*-win-x64-Setup.exe' -File |
        Sort-Object Name | ForEach-Object {
            (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant() + '  ' + $_.Name
        })
    [IO.File]::WriteAllLines($checksumPath, $checksumLines, [Text.UTF8Encoding]::new($false))
    Write-Host "Installer: $releaseInstaller"
    Write-Host "Checksums: $checksumPath"
}
finally {
    if (Test-Path -LiteralPath $buildRoot) {
        Assert-ArtifactPath $buildRoot
        Remove-Item -LiteralPath $buildRoot -Recurse -Force
    }
}
