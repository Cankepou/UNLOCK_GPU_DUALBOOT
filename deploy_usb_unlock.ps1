param(
    [Parameter(Mandatory = $false)]
    [string]$DriveLetter = "",
    [switch]$AutoRun5Sec
)

$ErrorActionPreference = "Stop"
$baseDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$unlockerDir = Join-Path $baseDir "nv-uefi-unlocker"
$usbrootDir = Join-Path $unlockerDir "usbroot"

$requiredRootFiles = @(
    "booter_load_ad102.bin",
    "gsp_boot_desc_ad102.bin",
    "gsp_boot_ad102.bin",
    "fwimage_ad102.bin",
    "fwsignature_ad102.bin",
    "scrubber_ad10x.bin",
    "v67-ad102-four-plm.bin"
)

# Verification que les fichiers ad102 sont deja generes, sinon les generer
$missing = $false
foreach ($f in $requiredRootFiles) {
    if (-not (Test-Path (Join-Path $usbrootDir $f))) {
        $missing = $true
    }
}
if (-not (Test-Path (Join-Path $usbrootDir "BOOTX64.EFI"))) {
    $missing = $true
}

if ($missing) {
    Write-Host "[*] Generation locale des firmwares signes AD102 (RTX 4050 Laptop 28a1)..." -ForegroundColor Cyan
    Push-Location $unlockerDir
    try {
        & powershell -ExecutionPolicy Bypass -File .\make-usbroot.ps1 -Family ad102
    } finally {
        Pop-Location
    }
}

Write-Host ""
Write-Host "============================================================================" -ForegroundColor Cyan
Write-Host "   PREPARATION DE LA CLE USB UEFI UNLOCK (RTX 4050 LAPTOP - 10DE:28A1)      " -ForegroundColor Cyan
Write-Host "============================================================================" -ForegroundColor Cyan

# Lister les volumes FAT32 amovibles disponibles
$volumes = Get-Volume | Where-Object { $_.DriveLetter -and $_.DriveLetter -ne 'C' -and $_.FileSystem -eq 'FAT32' }
if ($volumes) {
    Write-Host " Cle(s) USB / Volume(s) FAT32 detecte(s) :" -ForegroundColor Green
    foreach ($v in $volumes) {
        $sizeGB = [math]::Round($v.Size / 1GB, 2)
        Write-Host ("   -> Lecteur {0}:  [{1}]  ({2} Go, {3})" -f $v.DriveLetter, $v.FileSystemLabel, $sizeGB, $v.DriveType)
    }
} else {
    Write-Host " [!] Aucune cle USB formatee en FAT32 n'est actuellement branchee." -ForegroundColor Yellow
    Write-Host "     Veuillez brancher une cle USB formatee en FAT32 puis relancer cette option." -ForegroundColor Yellow
    exit 1
}

if (-not $DriveLetter) {
    Write-Host ""
    $DriveLetter = Read-Host " Entrez la lettre de votre cle USB FAT32 (ex: E ou F)"
}

$DriveLetter = $DriveLetter.Trim().TrimEnd(':').ToUpperInvariant()
if ($DriveLetter -eq "C" -or $DriveLetter.Length -ne 1) {
    Write-Host " [ERREUR] Lettre de lecteur invalide ou interdite ($DriveLetter:)." -ForegroundColor Red
    exit 1
}

$targetRoot = "${DriveLetter}:\"
if (-not (Test-Path $targetRoot)) {
    Write-Host " [ERREUR] Le lecteur $targetRoot n'existe pas." -ForegroundColor Red
    exit 1
}

$volInfo = Get-Volume -DriveLetter $DriveLetter -ErrorAction SilentlyContinue
if (-not $volInfo -or $volInfo.FileSystem -ne "FAT32") {
    Write-Host " [ERREUR] Le lecteur $targetRoot n'est pas formate en FAT32 (Actuel: $($volInfo.FileSystem))." -ForegroundColor Red
    Write-Host "          Le BIOS UEFI exige impérativement une cle USB en FAT32." -ForegroundColor Yellow
    exit 1
}

$efiBootDir = Join-Path $targetRoot "EFI\BOOT"
New-Item -ItemType Directory -Path $efiBootDir -Force | Out-Null

$destBootEfi = Join-Path $efiBootDir "BOOTX64.EFI"
if (Test-Path $destBootEfi) {
    $backupEfi = Join-Path $efiBootDir "BOOTX64.EFI.bak"
    Copy-Item $destBootEfi $backupEfi -Force
    Write-Host " [*] Ancien BOOTX64.EFI sauvegarde sous $backupEfi" -ForegroundColor Gray
}

# Copie de BOOTX64.EFI
Copy-Item (Join-Path $usbrootDir "BOOTX64.EFI") $destBootEfi -Force
Write-Host (" [OK] Copie : EFI\BOOT\BOOTX64.EFI ({0} octets)" -f (Get-Item $destBootEfi).Length) -ForegroundColor Green

# Copie des 7 fichiers binaires AD102 requis a la racine de la cle USB
foreach ($f in $requiredRootFiles) {
    $srcFile = Join-Path $usbrootDir $f
    $dstFile = Join-Path $targetRoot $f
    Copy-Item $srcFile $dstFile -Force
    Write-Host (" [OK] Copie : {0,-28} ({1,10} octets)" -f $f, (Get-Item $dstFile).Length) -ForegroundColor Green
}

# Ciblage strict du bus PCIe de la RTX 4050 Laptop (0000:01:00.0)
$bdfPath = Join-Path $targetRoot "tx-select-bdf.txt"
Set-Content -Path $bdfPath -Value "0000:01:00.0" -Encoding Ascii -NoNewline
Write-Host " [OK] Ciblage matériel strict créé : tx-select-bdf.txt -> 0000:01:00.0 (RTX 4050 Laptop)" -ForegroundColor Green

if ($AutoRun5Sec) {
    $autoMarker = Join-Path $targetRoot "i_know_what_i_am_doing.txt"
    Set-Content -Path $autoMarker -Value "auto" -Encoding Ascii
    Write-Host " [OK] Mode Auto-Boot 5 secondes active (i_know_what_i_am_doing.txt present)." -ForegroundColor Green
} else {
    $autoMarker = Join-Path $targetRoot "i_know_what_i_am_doing.txt"
    if (Test-Path $autoMarker) {
        Remove-Item $autoMarker -Force
    }
    Write-Host " [OK] Mode Confirmation Manuelle actif (Appuyez sur ESPACE ou ENTREE a l'ecran UEFI)." -ForegroundColor Green
}

Write-Host ""
Write-Host "============================================================================" -ForegroundColor Green
Write-Host " CLE USB PRETE SUR LE LECTEUR $targetRoot !" -ForegroundColor Green
Write-Host "============================================================================" -ForegroundColor Green
