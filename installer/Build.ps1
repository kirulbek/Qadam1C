$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
Set-Location $projectRoot
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.dotnet'
dotnet restore -r win-x64 --source https://api.nuget.org/v3/index.json
if ($LASTEXITCODE -ne 0) { throw 'Restore failed' }
# Recreate only the verified staging directory, so previous tests cannot enter the installer.
$staging = [IO.Path]::GetFullPath((Join-Path $projectRoot 'release\app'))
$expectedStaging = [IO.Path]::GetFullPath((Join-Path $projectRoot 'release\app'))
if ($staging -ne $expectedStaging -or !$staging.StartsWith($projectRoot + [IO.Path]::DirectorySeparatorChar)) { throw 'Unsafe staging path' }
if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
dotnet publish -c Release -r win-x64 --self-contained true --no-restore -o .\release\app
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
# Test the actual self-contained publish, then keep the test records outside the package.
$checkResults = @()
foreach ($checkName in @('--completion-test', '--smart-completion-test', '--favorites-test', '--snippet-test', '--outline-test')) {
    $check = Start-Process -FilePath (Join-Path $staging 'Qadam1C.exe') -ArgumentList $checkName -WindowStyle Hidden -Wait -PassThru
    $checkResults += "$checkName : $($check.ExitCode)"
    if ($check.ExitCode -ne 0) { throw "Published application check failed: $checkName. See release/app/completion-error.log" }
}
$appVersion = ([xml](Get-Content -LiteralPath (Join-Path $projectRoot 'Qadam1C.csproj') -Raw)).Project.PropertyGroup.Version
Set-Content -LiteralPath (Join-Path $projectRoot "release\checks-$appVersion.txt") -Value $checkResults -Encoding UTF8
foreach ($record in @('smart-completion-test-result.txt', 'favorites-test-result.txt', 'snippet-test-result.txt')) {
    $recordPath = Join-Path $staging $record
    if (Test-Path -LiteralPath $recordPath) { Remove-Item -LiteralPath $recordPath }
}
$unexpectedData = Get-ChildItem -LiteralPath (Join-Path $staging 'data') -Recurse -File | Where-Object { $_.Name -notin @('1cnik.st', 'default-snippets.json') }
if ($unexpectedData) { throw 'Unexpected user/test data in installer staging directory' }
# Publish only build outputs; the dist directory with personal caches is never used.
Set-Content -LiteralPath .\release\app\installed.txt -Value 'Qadam1C installed edition' -Encoding UTF8
$iscc = Join-Path $PSScriptRoot 'tools\inno\ISCC.exe'
if (!(Test-Path -LiteralPath $iscc)) { throw 'Inno Setup compiler missing: installer\tools\inno\ISCC.exe' }
& $iscc (Join-Path $PSScriptRoot 'Qadam1C.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed' }
