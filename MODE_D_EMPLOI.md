# Mode d'Emploi — Déverrouillage Tensor Core NVIDIA RTX 4050 Laptop (`10DE:28A1`)

> **RÈGLE CRITIQUE** : Ne redémarrez **JAMAIS** le PC tant que votre entraînement IA (`YOLOv26s`) est en cours d'exécution. Attendez la fin complète de l'entraînement.

---

## 1. Contenu du Dossier `C:\Users\lenov\Documents\UNLOCK_GPU`

| Fichier / Dossier | Rôle |
| :--- | :--- |
| **`GERER_UNLOCK_GPU.bat`** | **Centre de contrôle interactif** (à lancer par double-clic). Affiche l'état réel des registres matériels `SS0/SS1`, le débit Tensor Core en `TFLOPS`, et permet d'activer/désactiver le système. |
| **`check_gpu_unlock_status.py`** | Sonde matérielle Python + `ssdiag` qui lit directement les registres `SS0`/`SS1` de votre `RTX 4050` et mesure le ratio `FP16(acc FP32) / FP32`. |
| **`deploy_usb_unlock.ps1`** | Script automatisé qui prépare une clé USB FAT32 avec les 8 fichiers `ad102` requis et verrouille la cible sur le bus PCIe `0000:01:00.0` de votre RTX 4050. |
| **`nv-uefi-unlocker\`** | Distribution officielle `v2.1.6` vérifiée par empreinte SHA-256 (`ssdiag-x86_64-windows.exe`, `BOOTX64.EFI`, `assets-x86_64-windows.exe`). |
| **`nv-uefi-unlocker\usbroot\`** | Les 8 fichiers binaires signés `AD102` pré-générés localement pour votre `RTX 4050 Laptop (10DE:28A1)`. |

---

## 2. Mise en Route en 3 Étapes

### Étape 1 — Préparer une clé USB (formatée en `FAT32`)
1. Branchez une clé USB formatée en **`FAT32`**.
2. Double-cliquez sur **`GERER_UNLOCK_GPU.bat`**.
3. Choisissez :
   - **Option `[2]`** : Mode Sécurisé (demande d'appuyer sur `ESPACE` ou `ENTRÉE` au démarrage UEFI), **OU**
   - **Option `[3]`** : Mode Automatique (déverrouille tout seul après 5 secondes et lance Windows automatiquement sans toucher au clavier).
4. Choisissez l'**Option `[4]`** (*ACTIVER le maintien continu Windows*) pour désactiver le Démarrage Rapide hybride (`HiberbootEnabled=0`) et empêcher Windows de plonger la carte dans l'état `D3cold` (ce qui effacerait le déverrouillage en cours de session).

### Étape 2 — Réglage unique dans votre BIOS AMI Aptio V (`ALASKA - 1072009`)
1. Depuis **`GERER_UNLOCK_GPU.bat`**, choisissez l'**Option `[6]`** pour redémarrer directement dans le BIOS AMI.
2. Modifiez uniquement ces paramètres :
   - Onglet **`Security` $\rightarrow$ `Secure Boot`** : **`Disabled`**
   - Onglet **`Boot` $\rightarrow$ `Fast Boot`** : **`Disabled`**
   - Onglet **`Boot`** : Placez votre **Clé USB UEFI** en **Boot Option #1** (et *Windows Boot Manager* en **#2**).
   - *(Si disponible)* Onglet **`Advanced` / `Chipset` $\rightarrow$ `Display Mode`** : **`dGPU Only`**
3. Appuyez sur **`F10`** (*Save & Exit*).

### Étape 3 — Démarrage et Vérification de l'État Réel
1. **Règle importante sur PC portable Optimus** : Utilisez toujours **"Redémarrer"** depuis Windows pour que la `RTX 4050` reste alimentée au passage dans l'outil UEFI.
2. Une fois sous Windows, lancez **`GERER_UNLOCK_GPU.bat`** :
   - Les registres matériels afficheront : **`SS0 = 0x88888888`** et **`SS1 low nibble = 0x8`**.
   - Le verdict affichera : **`>>> VERDICT OFFICIEL : [ UNLOCK 100% ACTIF (SS0 = 0x88888888 FULL-RATE) ]`**.

---

## 3. Désactivation et Retour 100 % d'Origine

- **Pour désactiver temporairement au démarrage** : Retirez simplement la clé USB (ou appuyez sur `Échap` pendant le compte à rebours de 5 secondes au boot). La `RTX 4050` démarre à 100 % dans son état d'usine NVIDIA.
- **Pour restaurer les paramètres d'alimentation Windows d'origine** : Lancez **`GERER_UNLOCK_GPU.bat`** et choisissez l'**Option `[5]`**.
