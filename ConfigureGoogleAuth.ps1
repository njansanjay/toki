$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security
$files = Get-ChildItem -Path (Join-Path $env:USERPROFILE 'Downloads') -Filter '*.json' -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending
$currentConfigPath = Join-Path $PSScriptRoot 'oauth.local.json'
$currentClientId = $null
if (Test-Path -LiteralPath $currentConfigPath) {
    try { $currentClientId = (Get-Content -LiteralPath $currentConfigPath -Raw | ConvertFrom-Json).google_client_id } catch { }
}
if (-not $currentClientId) {
    $dataPath = Join-Path (Join-Path $env:LOCALAPPDATA 'TOKI') 'data.json'
    if (Test-Path -LiteralPath $dataPath) {
        try { $currentClientId = (Get-Content -LiteralPath $dataPath -Raw | ConvertFrom-Json).GoogleClientId } catch { }
    }
}
$selected = $null
$clientSecret = $null
foreach ($file in $files) {
    try {
        $json = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
        if ($json.installed -and $json.installed.client_id -and $json.installed.client_secret -and (-not $currentClientId -or $json.installed.client_id -eq $currentClientId)) {
            $selected = $json.installed.client_id
            $clientSecret = $json.installed.client_secret
            break
        }
    } catch { }
}
if (-not $selected) { throw 'No matching complete Google Desktop OAuth JSON found in Downloads.' }
$secretBytes = [System.Text.Encoding]::UTF8.GetBytes($clientSecret)
$protectedBytes = [System.Security.Cryptography.ProtectedData]::Protect($secretBytes, $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
$config = @{ google_client_id = $selected; google_client_secret_protected = [Convert]::ToBase64String($protectedBytes) } | ConvertTo-Json -Compress
Set-Content -LiteralPath $currentConfigPath -Value $config -Encoding UTF8
if ([System.Text.Encoding]::UTF8.GetString([System.Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String((Get-Content -LiteralPath $currentConfigPath -Raw | ConvertFrom-Json).google_client_secret_protected), $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)) -ne $clientSecret) {
    throw 'Encrypted Google OAuth configuration did not verify.'
}
Write-Output 'Configured TOKI Google Desktop OAuth credentials locally. Client secret was encrypted for this Windows user.'
