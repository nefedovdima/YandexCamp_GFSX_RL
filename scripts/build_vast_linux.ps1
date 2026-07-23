[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$UnityEditorPath
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $UnityEditorPath -PathType Leaf)) {
    throw "Unity Editor executable was not found: $UnityEditorPath"
}

$projectPath = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$buildDirectory = Join-Path $projectPath 'Build_Vast'
$logPath = Join-Path $buildDirectory 'build.log'

New-Item -ItemType Directory -Force -Path $buildDirectory | Out-Null

$unityArguments = @(
    '-batchmode'
    '-quit'
    '-projectPath'
    $projectPath
    '-executeMethod'
    'VastLinuxBuilder.BuildFromCommandLine'
    '-logFile'
    $logPath
)

Write-Host "Building Vast Linux Player from: $projectPath"
Write-Host "Unity log: $logPath"

& $UnityEditorPath @unityArguments
$unityExitCode = $LASTEXITCODE

if ($unityExitCode -ne 0) {
    Write-Error "Unity build failed with exit code $unityExitCode. See: $logPath"
    exit $unityExitCode
}

Write-Host 'Vast Linux Player build completed successfully.'
Write-Host "Manifest: $(Join-Path $buildDirectory 'BUILD_MANIFEST.txt')"
exit 0
