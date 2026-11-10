# Builds and signs ChromaBridge.msix (a sparse package that only carries app identity).
# Only needs to be re-run when AppxManifest.xml changes - updating ChromaBridge.exe does not need a new package.
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$subject = 'CN=ChromaBridge Local'
$kits = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin\10.*\x64\makeappx.exe' |
    Sort-Object { [version]$_.Directory.Parent.Name } -Descending | Select-Object -First 1
if (-not $kits) { throw 'Windows SDK (makeappx.exe) not found' }
$makeappx = $kits.FullName
$signtool = Join-Path $kits.DirectoryName 'signtool.exe'

# --- staging folder with manifest + logos ---
$stage = Join-Path $here 'stage'
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force "$stage\Assets", "$stage\Public" | Out-Null
Copy-Item "$here\AppxManifest.xml" $stage
Set-Content "$stage\Public\readme.txt" 'ChromaBridge Dynamic Lighting extension'

Add-Type -AssemblyName System.Drawing
foreach ($logo in @(@('StoreLogo', 50), @('Square44x44Logo', 44), @('Square150x150Logo', 150))) {
    $size = $logo[1]
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::FromArgb(40, 40, 40))
    $brush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(68, 214, 44))
    $k = $size / 10
    for ($r = 0; $r -lt 3; $r++) { for ($c = 0; $c -lt 4; $c++) { $g.FillRectangle($brush, [float](1.2 * $k + $c * 2 * $k), [float](2.5 * $k + $r * 2 * $k), [float]1.4 * $k, [float]1.4 * $k) } }
    $g.Dispose()
    $bmp.Save("$stage\Assets\$($logo[0]).png", [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

# --- pack ---
$msix = Join-Path $here 'ChromaBridge.msix'
& $makeappx pack /o /nv /d $stage /p $msix | Out-Null
if ($LASTEXITCODE) { throw "makeappx failed ($LASTEXITCODE)" }

# --- sign with a fresh local self-signed certificate ---
$cert = New-SelfSignedCertificate -Type Custom -Subject $subject -FriendlyName 'ChromaBridge package signing' `
    -KeyUsage DigitalSignature -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(10) `
    -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
& $signtool sign /q /fd SHA256 /sha1 $cert.Thumbprint /s My $msix
if ($LASTEXITCODE) { throw "signtool failed ($LASTEXITCODE)" }
Export-Certificate -Cert $cert -FilePath (Join-Path $here 'ChromaBridge.cer') | Out-Null

# The private key is not needed any more: delete it so nothing else can ever be signed with this certificate.
Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)" -DeleteKey
Remove-Item $stage -Recurse -Force

Write-Host "Built and signed $msix (certificate $($cert.Thumbprint), private key deleted)"
