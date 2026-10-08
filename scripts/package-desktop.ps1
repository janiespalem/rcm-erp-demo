param([string]$Version = '0.1.0')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$commit = (git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$') { throw 'Cannot identify the source commit' }
git -C $root diff --quiet HEAD
if ($LASTEXITCODE -ne 0) { throw 'Commit tracked source changes before packaging' }
Push-Location (Join-Path $root 'dotnet')
try {
    dotnet publish Rcm.Desktop -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -p:DebugType=embedded -p:ContinuousIntegrationBuild=true -p:Version=$Version -p:SourceRevisionId=$commit -o artifacts/desktop
    if ($LASTEXITCODE -ne 0) { throw 'Self-contained desktop publish failed' }
    if (-not (Test-Path artifacts/desktop/FactoryFlow.exe)) { throw 'FactoryFlow executable missing' }
    if (Get-ChildItem artifacts/desktop -Recurse -Filter *.dll) { throw 'Loose DLLs remain in application package' }
    [ordered]@{ commit = $commit; version = $Version; identity = 'FactoryFlow'; architecture = 'win-x64' } | ConvertTo-Json | Set-Content artifacts/desktop/release.json -Encoding utf8NoBOM
    vpk pack --packId FactoryFlow --packVersion $Version --packDir artifacts/desktop --mainExe FactoryFlow.exe --packTitle FactoryFlow --packAuthors FactoryFlow --channel win --runtime win-x64 --noPortable --outputDir artifacts/release
    if ($LASTEXITCODE -ne 0) { throw 'FactoryFlow installer packaging failed' }
    $installers = @(Get-ChildItem artifacts/release -Filter 'FactoryFlow*Setup.exe' -File)
    if ($installers.Count -ne 1) { throw 'Expected exactly one FactoryFlow installer' }
    if ($installers[0].Name -ne 'FactoryFlow-Setup.exe') {
        Move-Item -LiteralPath $installers[0].FullName -Destination artifacts/release/FactoryFlow-Setup.exe
    }
    Copy-Item artifacts/desktop/release.json artifacts/release/release.json
    Copy-Item "$PSScriptRoot/test-demo-install.ps1" artifacts/release/test-demo-install.ps1
    $candidateFiles = @('FactoryFlow-Setup.exe', 'release.json', 'test-demo-install.ps1') | ForEach-Object {
        [ordered]@{ name = $_; sha256 = (Get-FileHash (Join-Path artifacts/release $_) -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
    [ordered]@{ sourceRevisionId = $commit; version = $Version; files = @($candidateFiles) } | ConvertTo-Json -Depth 4 | Set-Content artifacts/release/verification.json -Encoding utf8NoBOM
    Write-Output "Private coexistence candidate tag: factoryflow-coexistence-$($commit.Substring(0,12))"
    Get-FileHash artifacts/release/FactoryFlow-Setup.exe -Algorithm SHA256 | Format-List
} finally { Pop-Location }
