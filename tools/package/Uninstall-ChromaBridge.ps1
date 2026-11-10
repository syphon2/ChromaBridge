# Removes the ChromaBridge package and its certificate trust (asks for admin once).
$ErrorActionPreference = 'Stop'
$cer = Join-Path $PSScriptRoot 'ChromaBridge.cer'
Get-Process ChromaBridge -ErrorAction SilentlyContinue | Stop-Process -Force
Get-AppxPackage ChromaBridge | Remove-AppxPackage
$thumb = (New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 $cer).Thumbprint
if (Test-Path "Cert:\LocalMachine\TrustedPeople\$thumb") {
    Start-Process powershell.exe -Verb RunAs -Wait -ArgumentList @(
        '-NoProfile', '-Command', "Remove-Item 'Cert:\LocalMachine\TrustedPeople\$thumb'")
}
Write-Host 'ChromaBridge package and certificate removed.'
