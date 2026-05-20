param(
    [Parameter(Mandatory=$true, Position=0)]
    [string]$Key
)
# Persist the key for your user (survives reboots).
[Environment]::SetEnvironmentVariable('RIOT_API_KEY', $Key, 'User')
# Also set it for the current shell so launching from here works immediately.
$env:RIOT_API_KEY = $Key
Write-Host "RIOT_API_KEY stored (User scope) and set in current session."
Write-Host "Restart any running LolCoach instance to pick it up."
