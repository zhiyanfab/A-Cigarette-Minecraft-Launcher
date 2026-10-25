param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'publish-single'),
    [string]$DotnetPath = 'dotnet'
)
$ErrorActionPreference = 'Stop'
$taskProject = Join-Path $PSScriptRoot 'src\ACML.App\ACML.App.csproj'
$taskCoreTests = Join-Path $PSScriptRoot 'tests\ACML.SmokeTests\ACML.SmokeTests.csproj'
$taskWindowsTests = Join-Path $PSScriptRoot 'tests\ACML.WindowsTests\ACML.WindowsTests.csproj'
$taskExeName = 'A Cigarette Minecraft Launcher.exe'
if (Test-Path -LiteralPath $OutputDirectory) {
    $taskUnexpected = Get-ChildItem -LiteralPath $OutputDirectory -Force | Where-Object { $_.Name -ne $taskExeName -or $_.PSIsContainer }
    if ($taskUnexpected) { throw "Output folder must be empty or contain only $taskExeName; choose a new output directory." }
}
& $DotnetPath run --project $taskCoreTests -c Release
if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }
& $DotnetPath run --project $taskWindowsTests -c Release -- (Join-Path $PSScriptRoot 'src\ACML.App\Assets\logo-original.png')
if ($LASTEXITCODE -ne 0) { throw 'Windows tests failed.' }
& $DotnetPath publish $taskProject -c Release -r win-x64 --self-contained true -o $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Move-Item -LiteralPath (Join-Path $OutputDirectory 'ACML.exe') -Destination (Join-Path $OutputDirectory $taskExeName) -Force
$taskPublished = @(Get-ChildItem -LiteralPath $OutputDirectory -Force)
if ($taskPublished.Count -ne 1 -or $taskPublished[0].Name -ne $taskExeName) { throw "Expected a single $taskExeName." }
Write-Host "Ready: $(Join-Path $OutputDirectory $taskExeName)"
