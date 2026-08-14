# Dev loop: build + pack the plugin, then upload it to a running DMX Core 100.
# The Core applies the upload immediately by hot-reloading the plugin — no
# restart needed (requires Core 2026.8+; older Cores stage it for the next
# restart).
#
#   ./deploy-dev.ps1                                                  # localhost, Administrator, prompts for PIN
#   ./deploy-dev.ps1 -Pin 1111                                        # no prompt
#   ./deploy-dev.ps1 -Server http://192.168.1.50:8080 -User Manager -Pin 1234
param(
    [string]$Server = 'http://localhost:8080',
    [string]$User = 'Administrator',
    [string]$Pin = '',
    [string]$Password = ''
)
$ErrorActionPreference = 'Stop'

& (Join-Path $PSScriptRoot 'pack.ps1')

$package = Get-ChildItem (Join-Path $PSScriptRoot 'artifacts') -Filter '*.dmxplugin' | Select-Object -First 1
if (-not $package)
{
    throw 'pack.ps1 produced no .dmxplugin package'
}

$users = Invoke-RestMethod "$Server/api/website/login-users"
$account = $users.data | Where-Object { $_.name -eq $User } | Select-Object -First 1
if (-not $account)
{
    throw "No user named '$User' on $Server (available: $(($users.data | ForEach-Object { $_.name }) -join ', '))"
}

if (-not $Pin -and -not $Password)
{
    $entered = Read-Host "PIN for $User on $Server (Enter = 1111; use -Pin/-Password to skip this prompt)"
    $Pin = if ($entered) { $entered } else { '1111' }
}

$loginBody = @{ userId = $account.userId; pin = $Pin; password = $Password } | ConvertTo-Json
$login = Invoke-RestMethod -Method Post -Uri "$Server/api/website/login" -ContentType 'application/json' -Body $loginBody
if (-not $login.success)
{
    throw "Login failed: $($login.errorText)"
}

$headers = @{ Authorization = "Bearer $($login.data.token)" }
$response = Invoke-RestMethod -Method Post -Uri "$Server/api/website/plugins/upload" -Headers $headers -Form @{ file = $package }
if (-not $response.success)
{
    throw "Upload failed: $($response.errorText)"
}

$result = $response.data
if ($result.applied)
{
    Write-Host "Deployed $($result.code) $($result.version) -> $($result.state)"
    if ($result.error)
    {
        Write-Host "Plugin error: $($result.error)"
    }
}
else
{
    Write-Host "Staged $($result.code) $($result.version) — applied on next restart (Core in safe mode or pre-hot-reload version)"
}
