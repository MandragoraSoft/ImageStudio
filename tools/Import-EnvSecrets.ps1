<#
.SYNOPSIS
  Kopiuje dane wtyczek magazynu (Google Drive, OneDrive) z pliku .env do SecretStore aplikacji (DPAPI, bieżący użytkownik).
.DESCRIPTION
  Zapisuje to samo, co zakładka Ustawienia -> Zapis: pliki %LOCALAPPDATA%\ImageStudio\secrets-<nazwa>.bin
  (albo w IMAGESTUDIO_DATA_DIR, jeśli ustawiony). Wartości nie są wypisywane. Pusta wartość w .env jest pomijana,
  a istniejący sekret zostaje, chyba że podasz -Force.
.EXAMPLE
  pwsh tools/Import-EnvSecrets.ps1
#>
param(
    [string]$EnvFile = (Join-Path $PSScriptRoot '..\.env'),
    [switch]$Force
)

Add-Type -AssemblyName System.Security

# zmienna .env -> nazwa sekretu w SecretStore (zgodnie z CloudMirrorStorage: storage-<id>-clientid / -client)
$map = [ordered]@{
    GOOGLE_OAUTH_CLIENT_ID     = 'storage-googledrive-clientid'
    GOOGLE_OAUTH_CLIENT_SECRET = 'storage-googledrive-client'
    AZURE_APP_CLIENT_ID        = 'storage-onedrive-clientid'
}

if (-not (Test-Path $EnvFile)) { throw "Brak pliku: $EnvFile" }

$values = @{}
foreach ($line in Get-Content $EnvFile) {
    $t = $line.Trim()
    if ($t -eq '' -or $t.StartsWith('#')) { continue }
    $eq = $t.IndexOf('=')
    if ($eq -le 0) { continue }
    $v = $t.Substring($eq + 1).Trim()
    if ($v.Length -ge 2 -and ($v[0] -eq '"' -or $v[0] -eq "'") -and $v[-1] -eq $v[0]) { $v = $v.Substring(1, $v.Length - 2) }
    $values[$t.Substring(0, $eq).Trim()] = $v
}

$dir = if ($env:IMAGESTUDIO_DATA_DIR) { $env:IMAGESTUDIO_DATA_DIR } else { Join-Path $env:LOCALAPPDATA 'ImageStudio' }
New-Item -ItemType Directory -Force $dir | Out-Null

foreach ($key in $map.Keys) {
    $name = $map[$key]
    $path = Join-Path $dir "secrets-$name.bin"
    if (-not $values[$key]) { Write-Host "pominięto  $key (pusta lub brak w .env)"; continue }
    if ((Test-Path $path) -and -not $Force) { Write-Host "istnieje   $name (użyj -Force, żeby nadpisać)"; continue }

    $bytes = [Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes($values[$key].Trim()), $null, 'CurrentUser')
    [IO.File]::WriteAllBytes($path, $bytes)
    Write-Host "zapisano   $name  <- $key ($($values[$key].Trim().Length) znaków)"
}
