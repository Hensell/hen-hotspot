#requires -Version 5.1
[CmdletBinding()]
param(
    [string] $CompilerPath,
    [string] $OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'Build the installer on Windows.'
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot 'src\HenHotspot.csproj'
[xml] $project = Get-Content -LiteralPath $projectPath -Raw
$appVersion = [string] $project.Project.PropertyGroup.Version
if ($appVersion -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') {
    throw "Expected a numeric release version in HenHotspot.csproj; found '$appVersion'."
}

if (-not $CompilerPath) {
    $compilerCommand = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($compilerCommand) {
        $CompilerPath = $compilerCommand.Source
    }
    else {
        $compilerCandidates = @(
            (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
            (Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe'),
            (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 7\ISCC.exe'),
            (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
            (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 7\ISCC.exe')
        )
        $CompilerPath = $compilerCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    }
}
if (-not $CompilerPath -or -not (Test-Path -LiteralPath $CompilerPath -PathType Leaf)) {
    throw 'Install Inno Setup 6.7.3 or later from https://jrsoftware.org/isdl.php, or supply -CompilerPath with the full ISCC.exe path.'
}
$CompilerPath = (Resolve-Path -LiteralPath $CompilerPath).Path
if (-not (Get-Command dotnet.exe -ErrorAction SilentlyContinue)) {
    throw 'Install the .NET 10 SDK before building the installer.'
}

if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repositoryRoot 'dist'
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)

# Every invocation gets a clean directory; stale binaries and local data cannot leak in.
$buildDirectory = Join-Path $repositoryRoot ('work\installer\' + [Guid]::NewGuid().ToString('N'))
$publishDirectory = Join-Path $buildDirectory 'publish'
$compileDirectory = Join-Path $buildDirectory 'setup'
New-Item -ItemType Directory -Path $publishDirectory, $compileDirectory -Force | Out-Null

Push-Location $repositoryRoot
try {
    & dotnet publish $projectPath -c Release -r win-x64 --self-contained true `
        -p:RestoreLockedMode=true -warnaserror -o $publishDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "Self-contained publishing failed with exit code $LASTEXITCODE."
    }

    $requiredFiles = @(
        'HenHotspot.exe', 'HenHotspot.dll', 'HenHotspot.runtimeconfig.json',
        'coreclr.dll', 'System.Private.CoreLib.dll', 'PresentationFramework.dll',
        'e_sqlite3.dll', 'WinDivert.dll', 'WinDivert64.sys', 'LICENSE',
        'ThirdParty\WinDivert\LICENSE', 'ThirdParty\WinDivert\NOTICE.txt',
        'ThirdParty\WinDivert\WinDivert-2.2.2-Source.zip',
        'ThirdParty\DotNet\LICENSE.TXT', 'ThirdParty\DotNet\THIRD-PARTY-NOTICES.TXT',
        'ThirdParty\SQLitePCLRaw\LICENSE.TXT', 'ThirdParty\WindowsSdk\CsWinRT-LICENSE.txt'
    )
    foreach ($relativePath in $requiredFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory $relativePath) -PathType Leaf)) {
            throw "Required installer payload is missing: $relativePath"
        }
    }
    $runtimeConfig = Get-Content -LiteralPath (Join-Path $publishDirectory 'HenHotspot.runtimeconfig.json') -Raw | ConvertFrom-Json
    if (@($runtimeConfig.runtimeOptions.includedFrameworks).Count -lt 2) {
        throw 'The payload must include both the .NET and Windows Desktop runtimes.'
    }
    $unexpectedData = Get-ChildItem -LiteralPath $publishDirectory -Recurse -File | Where-Object {
        $_.Name -match '\.(db|db-shm|db-wal|log)$' -or
        $_.Name -in @('preferences.json', 'policy-draft.json', 'device-access.json', 'hen-hotspot-diagnostico.json', 'windivert-self-test.json')
    }
    if ($unexpectedData) {
        throw 'Local runtime data was found in the publish directory; packaging was stopped.'
    }

    & $CompilerPath "/DAppVersion=$appVersion" "/DPublishDir=$publishDirectory" `
        "/DInstallerOutputDir=$compileDirectory" (Join-Path $PSScriptRoot 'HenHotspot.iss')
    if ($LASTEXITCODE -ne 0) {
        throw "Installer compilation failed with exit code $LASTEXITCODE."
    }

    $fileName = "HenHotspot-Setup-$appVersion-win-x64.exe"
    $compiledInstaller = Join-Path $compileDirectory $fileName
    $installerPath = Join-Path $OutputDirectory $fileName
    if (-not (Test-Path -LiteralPath $compiledInstaller -PathType Leaf)) {
        throw 'The compiler did not produce the expected installer.'
    }
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    Copy-Item -LiteralPath $compiledInstaller -Destination $installerPath -Force
    $checksum = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText("$installerPath.sha256", "$checksum  $fileName`n", [Text.Encoding]::ASCII)

    [PSCustomObject]@{
        Version = $appVersion
        Installer = $installerPath
        SHA256 = $checksum
        SizeMB = [Math]::Round((Get-Item -LiteralPath $installerPath).Length / 1MB, 1)
        PublishDirectory = $publishDirectory
    }
}
finally {
    Pop-Location
}
