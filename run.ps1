# Launches the LoL Coach overlay.
# Requires GROQ_API_KEY (or set Provider=claude + ANTHROPIC_API_KEY) in your env.
$ErrorActionPreference = 'Stop'
$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User')
$exe = Join-Path $PSScriptRoot 'bin\Release\net8.0-windows\LolCoach.exe'
if (-not (Test-Path $exe)) {
    & "C:\Program Files\dotnet\dotnet.exe" build (Join-Path $PSScriptRoot 'LolCoach.csproj') -c Release | Out-Host
}
Start-Process -FilePath $exe
