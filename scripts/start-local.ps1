param(
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$stateDir = Join-Path $workspace '.local'
New-Item -ItemType Directory -Path $stateDir -Force | Out-Null
$env:ASPNETCORE_ENVIRONMENT = 'Development'

if (-not $NoBuild) {
    Push-Location $workspace
    try {
        dotnet build ChurchTranslate.sln -v:q
        if ($LASTEXITCODE -ne 0) { throw 'The solution did not build.' }
    }
    finally { Pop-Location }
}

function Test-LocalPort([int]$port) {
    $client = [System.Net.Sockets.TcpClient]::new()
    try {
        $result = $client.BeginConnect('127.0.0.1', $port, $null, $null)
        if (-not $result.AsyncWaitHandle.WaitOne(300)) { return $false }
        $client.EndConnect($result)
        return $true
    }
    catch { return $false }
    finally { $client.Dispose() }
}

$services = @(
    @{ Name = 'Api'; Port = 5300 },
    @{ Name = 'Admin'; Port = 5301 },
    @{ Name = 'Listener'; Port = 5302 }
)
$pidFile = Join-Path $stateDir 'processes.json'
$pids = @{}
if (Test-Path -LiteralPath $pidFile) {
    $previous = Get-Content -LiteralPath $pidFile -Raw | ConvertFrom-Json
    foreach ($entry in $previous.PSObject.Properties) { $pids[$entry.Name] = $entry.Value }
}
$dotnet = (Get-Command dotnet).Source
foreach ($service in $services) {
    $name = "ChurchTranslate.$($service.Name)"
    if (Test-LocalPort $service.Port) {
        Write-Host "$name already responds on port $($service.Port)."
        continue
    }
    $projectDir = Join-Path $workspace "src/$name"
    $process = Start-Process -FilePath $dotnet -ArgumentList @('run', '--no-build', '--no-launch-profile', '--project', "$name.csproj", '--urls', "http://127.0.0.1:$($service.Port)") -WorkingDirectory $projectDir -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $stateDir "$name.out.log") -RedirectStandardError (Join-Path $stateDir "$name.err.log")
    $pids[$name] = $process.Id
}

$pids | ConvertTo-Json | Set-Content -LiteralPath $pidFile
foreach ($service in $services) {
    $ready = $false
    for ($i = 0; $i -lt 30; $i++) {
        if (Test-LocalPort $service.Port) { $ready = $true; break }
        Start-Sleep -Milliseconds 200
    }
    if (-not $ready) {
        $name = "ChurchTranslate.$($service.Name)"
        throw "$name did not start. Check $stateDir/$name.err.log"
    }
}
Write-Host 'Local ChurchTranslate is ready:'
Write-Host '  Studio:   http://127.0.0.1:5301/studio'
Write-Host '  Listener: http://127.0.0.1:5302/'
Write-Host 'Google sign-in is skipped only in this Development preview.'
