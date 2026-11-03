$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$path = Join-Path $workspace '.local/processes.json'
if (-not (Test-Path -LiteralPath $path)) {
    Write-Host 'No local ChurchTranslate processes were recorded.'
    return
}
$recorded = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
foreach ($property in $recorded.PSObject.Properties) {
    $process = Get-CimInstance Win32_Process -Filter "ProcessId = $($property.Value)" -ErrorAction SilentlyContinue
    if ($process -and ($process.CommandLine -like "*$($property.Name).csproj*" -or
        $process.CommandLine -like "*$($property.Name).dll*")) {
        Get-CimInstance Win32_Process -Filter "ParentProcessId = $($property.Value)" -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -eq 'dotnet.exe' -and $_.CommandLine -like '*Microsoft.AspNetCore.Components.WebAssembly.DevServer*' } |
            ForEach-Object { Stop-Process -Id $_.ProcessId -ErrorAction SilentlyContinue }
        Stop-Process -Id $property.Value -ErrorAction SilentlyContinue
        Write-Host "Stopped $($property.Name)."
    }
}
Remove-Item -LiteralPath $path
