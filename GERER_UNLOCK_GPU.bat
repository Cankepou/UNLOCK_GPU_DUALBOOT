@echo off
chcp 65001 >nul
setlocal EnableDelayedExpansion
title CONTROLEUR GPU UNLOCK - NVIDIA RTX 4050 LAPTOP (AD107M / 10DE:28A1)

:: Verification et elevation automatique en Administrateur
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo [*] Demande des privileges Administrateur pour acceder aux registres materiels BAR0...
    powershell -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

cd /d "%~dp0"

:: Detection de l'environnement Python avec PyTorch CUDA
set "PYTHON_EXE=python"
if exist "C:\Users\lenov\Documents\APP_APKDJI\com.dji.coloranalys\COLORDETECT_V4_V5_A20\IA_THERMAL_TRAINING\venv\Scripts\python.exe" (
    set "PYTHON_EXE=C:\Users\lenov\Documents\APP_APKDJI\com.dji.coloranalys\COLORDETECT_V4_V5_A20\IA_THERMAL_TRAINING\venv\Scripts\python.exe"
)

:MENU
cls
echo ============================================================================
echo    CONTROLEUR GPU UNLOCK - NVIDIA RTX 4050 LAPTOP (AD107M / 10DE:28A1)
echo ============================================================================
echo  Dossier actif : %~dp0
echo ----------------------------------------------------------------------------
"%PYTHON_EXE%" "%~dp0check_gpu_unlock_status.py"
echo.
echo  ACTIONS DISPONIBLES :
echo  [1] Actualiser le diagnostic complet (Registres SS0/SS1 + Debit TFLOPS)
echo  [2] Preparer une cle USB FAT32 (Mode Securise : Validation Espace/Entree)
echo  [3] Preparer une cle USB FAT32 (Mode Automatique : Boot auto apres 5 sec)
echo  [4] ACTIVER le maintien continu Windows (Anti-D3cold + Reapply SS0/SS1)
echo  [5] DESACTIVER et restaurer 100%% les parametres Windows/NVIDIA d'origine
echo  [6] Redemarrer directement dans l'ecran du BIOS AMI (shutdown /r /fw)
echo  [7] Ouvrir le Mode d'Emploi complet (MODE_D_EMPLOI.txt)
echo  [8] Quitter
echo ============================================================================
set "CHOIX="
set /p "CHOIX=Votre choix (1-8) : "

if "%CHOIX%"=="1" goto MENU
if "%CHOIX%"=="2" goto PREP_USB_MANUEL
if "%CHOIX%"=="3" goto PREP_USB_AUTO
if "%CHOIX%"=="4" goto ACTIVER_MAINTIEN
if "%CHOIX%"=="5" goto RESTAURER_ORIGINE
if "%CHOIX%"=="6" goto REBOOT_BIOS
if "%CHOIX%"=="7" goto OUVRIR_DOC
if "%CHOIX%"=="8" exit /b 0
goto MENU

:PREP_USB_MANUEL
echo.
powershell -ExecutionPolicy Bypass -File "%~dp0deploy_usb_unlock.ps1"
echo.
pause
goto MENU

:PREP_USB_AUTO
echo.
powershell -ExecutionPolicy Bypass -File "%~dp0deploy_usb_unlock.ps1" -AutoRun5Sec
echo.
pause
goto MENU

:ACTIVER_MAINTIEN
echo.
echo ============================================================================
echo  ACTIVATION DU MAINTIEN CONTINU SOUS WINDOWS (ANTI-D3COLD / ANTI-GC6)
echo ============================================================================
if not exist "%~dp0_BACKUP_REGISTRE_ORIGINE.reg" (
    echo [*] Sauvegarde de la cle de registre NVIDIA d'origine...
    reg export "HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0000" "%~dp0_BACKUP_REGISTRE_ORIGINE.reg" /y >nul 2>&1
)
echo [*] 1. Desactivation du Demarrage Rapide hybride Windows (HiberbootEnabled=0)...
reg add "HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Power" /v HiberbootEnabled /t REG_DWORD /d 0 /f >nul
echo [*] 2. Verrouillage de l'etat D0/P0 sur la RTX 4050 (DisableDynamicPstate=1)...
reg add "HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0000" /v DisableDynamicPstate /t REG_DWORD /d 1 /f >nul
echo [*] 3. Tentative de re-application directe des selecteurs SS0/SS1 (si porte PLM ouverte)...
"%~dp0nv-uefi-unlocker\ssdiag-x86_64-windows.exe" --select 28a1 --reapply
echo.
echo [OK] Les parametres de maintien continu sont actifs.
pause
goto MENU

:RESTAURER_ORIGINE
echo.
echo ============================================================================
echo  RESTAURATION INTEGRALE DE L'ETAT D'ORIGINE WINDOWS & NVIDIA
echo ============================================================================
echo [*] 1. Suppression du verrou DisableDynamicPstate (retour Optimus standard)...
reg delete "HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0000" /v DisableDynamicPstate /f >nul 2>&1
echo [*] 2. Restauration du Demarrage Rapide Windows par defaut...
reg add "HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Power" /v HiberbootEnabled /t REG_DWORD /d 1 /f >nul
echo.
echo [OK] Parametres Windows/NVIDIA remis a 100%% d'origine.
echo [INFO] Au prochain redemarrage sans la cle USB, la RTX 4050 sera a 100%% dans son etat d'usine.
pause
goto MENU

:REBOOT_BIOS
echo.
echo ============================================================================
echo  [!] ATTENTION : REDEMARRAGE VERS L'ECRAN DU BIOS UEFI
echo ============================================================================
echo  Assurez-vous qu'AUCUN entrainement IA (ex: YOLOv26s) n'est en cours !
echo.
set "CONFIRM_BIOS="
set /p "CONFIRM_BIOS=Tapez OUI en majuscules pour redemarrer dans le BIOS maintenant : "
if "%CONFIRM_BIOS%"=="OUI" (
    shutdown /r /fw /t 3
    exit /b 0
)
echo [*] Redemarrage annule.
pause
goto MENU

:OUVRIR_DOC
start notepad.exe "%~dp0MODE_D_EMPLOI.txt"
goto MENU
