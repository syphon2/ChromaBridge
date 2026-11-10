# Registers GameLightSync as a Dynamic Lighting background controller.
#  1. Trusts the local GameLightSync package certificate (asks for admin once, via UAC)
#  2. Installs the sparse package pointing at the dist folder (no admin)
# Afterwards: Settings > Personalization > Dynamic Lighting > Background light control -> move GameLightSync to the top.
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$dist = (Resolve-Path (Join-Path $here '..\..\dist')).Path
$msix = Join-Path $here 'GameLightSync.msix'
$cer = Join-Path $here 'GameLightSync.cer'

$thumb = (New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 $cer).Thumbprint
if (-not (Test-Path "Cert:\LocalMachine\TrustedPeople\$thumb")) {
    Write-Host 'Trusting the GameLightSync package certificate (Windows will ask for admin rights)...'
    Start-Process powershell.exe -Verb RunAs -Wait -ArgumentList @(
        '-NoProfile', '-Command',
        "Import-Certificate -FilePath '$cer' -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null")
    if (-not (Test-Path "Cert:\LocalMachine\TrustedPeople\$thumb")) { throw 'Certificate was not imported (UAC declined?)' }
}

Get-Process GameLightSync -ErrorAction SilentlyContinue | Stop-Process -Force
Get-AppxPackage GameLightSync | Remove-AppxPackage
Add-AppxPackage -Path $msix -ExternalLocation $dist
Write-Host "Installed: $((Get-AppxPackage GameLightSync).PackageFullName)"

Start-Process (Join-Path $dist 'GameLightSync.exe')
Write-Host ''
Write-Host 'Done. Now open Settings > Personalization > Dynamic Lighting > Background light control'
Write-Host 'and drag GameLightSync to the top of the list.'
Start-Process 'ms-settings:personalization-lighting'
