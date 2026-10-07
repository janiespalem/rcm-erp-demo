param([string]$ExistingRcmSetupPath)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true') { throw 'Run this installation test on a disposable Windows CI runner.' }
$root = Split-Path $PSScriptRoot
$artifacts = Join-Path $root 'dotnet/artifacts'
$results = Join-Path $artifacts 'tests'
New-Item -ItemType Directory -Path $results -Force | Out-Null
$rcmDirectory = Join-Path $env:LOCALAPPDATA 'RCM'
$demoDirectory = Join-Path $env:LOCALAPPDATA 'FactoryFlow'
foreach ($directory in @($rcmDirectory, $demoDirectory)) {
    if ((Test-Path (Join-Path $directory 'Update.exe')) -or (Test-Path (Join-Path $directory 'current'))) {
        throw 'Runner already contains an RCM or FactoryFlow installation.'
    }
}
$realRcm = -not [string]::IsNullOrWhiteSpace($ExistingRcmSetupPath)
if ($realRcm) {
    $rcmInstaller = (Resolve-Path -LiteralPath $ExistingRcmSetupPath).Path
} else {
    dotnet publish "$PSScriptRoot/windows/CoexistenceStub" -c Release -r win-x64 --self-contained true -p:RestoreLockedMode=false -p:PublishSingleFile=true -o "$artifacts/rcm-stub"
    if ($LASTEXITCODE -ne 0) { throw 'Synthetic RCM fixture publish failed' }
    vpk pack --packId RCM --packVersion 0.0.1 --packDir "$artifacts/rcm-stub" --mainExe Rcm.Desktop.exe --packTitle 'Synthetic RCM coexistence fixture' --channel win --runtime win-x64 --noPortable --outputDir "$artifacts/rcm-stub-release"
    if ($LASTEXITCODE -ne 0) { throw 'Synthetic RCM fixture packaging failed' }
    $stubInstallers = @(Get-ChildItem "$artifacts/rcm-stub-release" -Filter 'RCM*Setup.exe' -File)
    if ($stubInstallers.Count -ne 1) { throw 'Expected exactly one synthetic RCM installer' }
    $rcmInstaller = $stubInstallers[0].FullName
}
$rcmExe = Join-Path $rcmDirectory 'current/Rcm.Desktop.exe'
$demoExe = Join-Path $demoDirectory 'current/FactoryFlow.exe'
$identity = Join-Path $rcmDirectory 'identity'
New-Item -ItemType Directory -Path $identity -Force | Out-Null
$sentinel = Join-Path $identity ("synthetic-preservation-" + [Guid]::NewGuid().ToString('N') + '.bin')
[IO.File]::WriteAllText($sentinel, 'Synthetic production identity must remain unchanged.')
$before = (Get-FileHash $sentinel -Algorithm SHA256).Hash
$identityBefore = @{}
Get-ChildItem $identity -File -Recurse | ForEach-Object { $identityBefore[$_.FullName] = (Get-FileHash $_.FullName -Algorithm SHA256).Hash }
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = $listener.LocalEndpoint.Port
$sinkReservation = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$sinkReservation.Start()
$sinkPort = $sinkReservation.LocalEndpoint.Port
$sinkReservation.Stop()
$sink = [Net.HttpListener]::new()
$sink.Prefixes.Add("http://127.0.0.1:$sinkPort/")
$sink.Start()
$sinkJob = Start-ThreadJob -ArgumentList $sink -ScriptBlock {
    param($server)
    try {
        while ($server.IsListening) {
            $request = $server.GetContext()
            $request.Response.StatusCode = 503
            $request.Response.Close()
        }
    } catch [Net.HttpListenerException] { }
      catch [ObjectDisposedException] { }
}
$originalEnvironment = @{}
foreach ($name in @('RCM_SERVER_URL', 'RCM_UPDATE_URL', 'FACTORYFLOW_SERVER_URL', 'FACTORYFLOW_COEXISTENCE_MARKER')) {
    $originalEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
}
$env:FACTORYFLOW_COEXISTENCE_MARKER = Join-Path $results 'rcm-stub-running.txt'
$env:FACTORYFLOW_SERVER_URL = 'http://127.0.0.1:18081/'
function Get-InstalledProcess([string]$Executable) {
    $installedProcesses = @(Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($Executable)) -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $Executable })
    if ($installedProcesses.Count -gt 1) { throw "Multiple instances found for $Executable" }
    if ($installedProcesses.Count -eq 1) { return $installedProcesses[0] }
    return $null
}
$rcm = $null
$demo = $null
try {
    foreach ($installer in @($rcmInstaller, "$artifacts/release/FactoryFlow-Setup.exe")) {
        if ($installer -eq $rcmInstaller) {
            $env:RCM_SERVER_URL = "http://127.0.0.1:$sinkPort/"
            $env:RCM_UPDATE_URL = "http://127.0.0.1:$sinkPort/updates/"
        } else {
            $env:RCM_SERVER_URL = "http://127.0.0.1:$port/production-api/"
            $env:RCM_UPDATE_URL = "http://127.0.0.1:$port/production-updates/"
        }
        $process = Start-Process -FilePath $installer -ArgumentList '--silent' -PassThru
        if (-not $process.WaitForExit(120000)) { $process.Kill(); throw "Installer timed out: $installer" }
        if ($process.ExitCode -ne 0) { throw "Installer failed: $installer ($($process.ExitCode))" }
    }
    if (-not (Test-Path $rcmExe) -or -not (Test-Path $demoExe)) { throw 'Separate default installation paths missing' }
    $env:RCM_SERVER_URL = "http://127.0.0.1:$sinkPort/"
    $env:RCM_UPDATE_URL = "http://127.0.0.1:$sinkPort/updates/"
    $rcm = Get-InstalledProcess $rcmExe
    if (-not $rcm) { $rcm = Start-Process -FilePath $rcmExe -PassThru }
    if ($realRcm) {
        $rcmDeadline = [DateTime]::UtcNow.AddSeconds(30)
        do {
            Start-Sleep -Milliseconds 200
            $rcm.Refresh()
            if ($rcm.HasExited) { throw 'Real RCM exited before displaying a native window' }
        } until ($rcm.MainWindowHandle -ne 0 -or [DateTime]::UtcNow -gt $rcmDeadline)
        if ($rcm.MainWindowHandle -eq 0) { throw 'Real RCM did not display a native window using the local sink' }
    }
    $env:RCM_SERVER_URL = "http://127.0.0.1:$port/production-api/"
    $env:RCM_UPDATE_URL = "http://127.0.0.1:$port/production-updates/"
    $demo = Get-InstalledProcess $demoExe
    if (-not $demo) { $demo = Start-Process -FilePath $demoExe -PassThru }
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        Start-Sleep -Milliseconds 200
        $demo.Refresh()
        if ($demo.HasExited) { throw 'FactoryFlow exited before displaying login' }
    } until ($demo.MainWindowHandle -ne 0 -or [DateTime]::UtcNow -gt $deadline)
    if ($demo.MainWindowHandle -eq 0) { throw 'Installed FactoryFlow did not display a native window' }
    Start-Sleep -Seconds 5
    if ($listener.Pending()) { throw 'Demo contacted an RCM production environment endpoint' }
    if ($rcm.HasExited) { throw 'RCM did not remain running beside FactoryFlow' }
    if (-not $realRcm -and -not (Test-Path $env:FACTORYFLOW_COEXISTENCE_MARKER)) { throw 'Synthetic RCM fixture marker missing' }
    foreach ($path in $identityBefore.Keys) {
        if (-not (Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $identityBefore[$path]) { throw 'RCM identity data changed' }
    }
    $release = Get-Content (Join-Path $demoDirectory 'current/release.json') -Raw | ConvertFrom-Json
    if ($release.identity -ne 'FactoryFlow' -or $release.architecture -ne 'win-x64' -or $release.commit -notmatch '^[0-9a-f]{40}$') { throw 'Installed FactoryFlow source manifest invalid' }
    [ordered]@{
        platform = [Environment]::OSVersion.VersionString
        coexistence = $(if ($realRcm) { 'Installed and ran beside the supplied real RCM installer, with RCM requests directed to a local HTTP 503 sink.' } else { 'Installed and ran beside synthetic RCM Velopack package; real production RCM requires separate acceptance.' })
        rcmInstallerSha256 = (Get-FileHash $rcmInstaller -Algorithm SHA256).Hash
        rcmExecutableSha256 = (Get-FileHash $rcmExe -Algorithm SHA256).Hash
        factoryFlowExecutable = $demoExe
        factoryFlowInstallerSha256 = (Get-FileHash "$artifacts/release/FactoryFlow-Setup.exe" -Algorithm SHA256).Hash
        factoryFlowExecutableSha256 = (Get-FileHash $demoExe -Algorithm SHA256).Hash
        factoryFlowRelease = $release
        ignoredProductionEnvironmentRequests = 0
        rcmIdentitySha256 = $before
        factoryFlowWindowTitle = $demo.MainWindowTitle
    } | ConvertTo-Json | Set-Content (Join-Path $results 'installer-isolation.json') -Encoding utf8NoBOM
} finally {
    foreach ($exe in @($demoExe, $rcmExe)) {
        $process = Get-InstalledProcess $exe
        if ($process -and -not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
    }
    foreach ($name in $originalEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $originalEnvironment[$name]) }
    $listener.Stop()
    $sink.Stop()
    $sink.Close()
    Stop-Job $sinkJob
    Remove-Job $sinkJob
}
