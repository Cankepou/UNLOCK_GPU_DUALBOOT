import os
import sys
import time
import subprocess
import ctypes
import winreg


def is_admin():
    try:
        return bool(ctypes.windll.shell32.IsUserAnAdmin())
    except Exception:
        return False


def get_registry_dword(root, subkey, name):
    try:
        with winreg.OpenKey(root, subkey, 0, winreg.KEY_READ) as k:
            val, _ = winreg.QueryValueEx(k, name)
            return val
    except Exception:
        return None


def check_windows_settings():
    hiberboot = get_registry_dword(
        winreg.HKEY_LOCAL_MACHINE,
        r"SYSTEM\CurrentControlSet\Control\Session Manager\Power",
        "HiberbootEnabled",
    )
    nv_subkey = r"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0000"
    disable_dyn_pstate = get_registry_dword(
        winreg.HKEY_LOCAL_MACHINE, nv_subkey, "DisableDynamicPstate"
    )
    rm_gc6 = get_registry_dword(
        winreg.HKEY_LOCAL_MACHINE, nv_subkey, "RMGC6Feature"
    )
    return {
        "hiberboot": hiberboot,
        "disable_dyn_pstate": disable_dyn_pstate,
        "rm_gc6": rm_gc6,
    }


def check_running_compute_apps():
    try:
        out = subprocess.check_output(
            [
                "nvidia-smi",
                "--query-compute-apps=pid,process_name,used_memory",
                "--format=csv,noheader",
            ],
            stderr=subprocess.DEVNULL,
            text=True,
        ).strip()
        if not out:
            return []
        lines = [line.strip() for line in out.splitlines() if line.strip()]
        my_pid = str(os.getpid())
        apps = [line for line in lines if not line.startswith(my_pid + ",")]
        return apps
    except Exception:
        return []


def run_ssdiag(base_dir, reapply=False):
    ssdiag_exe = os.path.join(base_dir, "nv-uefi-unlocker", "ssdiag-x86_64-windows.exe")
    if not os.path.exists(ssdiag_exe):
        return None, "Outil ssdiag-x86_64-windows.exe introuvable."
    cmd = [ssdiag_exe, "--select", "28a1"]
    if reapply:
        cmd.append("--reapply")
    try:
        proc = subprocess.run(
            cmd,
            capture_output=True,
            text=True,
            timeout=15,
        )
        output = (proc.stdout or "") + ("\n" + proc.stderr if proc.stderr else "")
        return proc.returncode, output.strip()
    except Exception as e:
        return -1, f"Erreur lors de l'execution de ssdiag : {e}"


def run_tensor_benchmark(skip_if_busy=False):
    try:
        import torch
    except ImportError:
        return None

    if not torch.cuda.is_available():
        return None

    device = torch.device("cuda:0")
    props = torch.cuda.get_device_properties(device)

    # Dimensions calibrees pour ne consommer que ~200 Mo de VRAM (4096x4096)
    n = 4096
    # Forcer l'accumulation FP32 stricte (cible exacte du bride NVIDIA)
    torch.backends.cuda.matmul.allow_fp16_reduced_precision_reduction = False
    torch.backends.cuda.matmul.allow_bf16_reduced_precision_reduction = False
    torch.backends.cuda.matmul.allow_tf32 = False

    a_fp32 = torch.randn((n, n), device=device, dtype=torch.float32)
    b_fp32 = torch.randn((n, n), device=device, dtype=torch.float32)
    a_fp16 = a_fp32.half()
    b_fp16 = b_fp32.half()

    flops_per_matmul = 2.0 * (n ** 3)

    # Warmup
    for _ in range(4):
        _ = torch.matmul(a_fp32, b_fp32)
        _ = torch.matmul(a_fp16, b_fp16)
    torch.cuda.synchronize()

    # Mesure FP32 standard (CUDA Cores)
    iters_fp32 = 10
    t0 = time.perf_counter()
    for _ in range(iters_fp32):
        _ = torch.matmul(a_fp32, b_fp32)
    torch.cuda.synchronize()
    dt_fp32 = time.perf_counter() - t0
    tflops_fp32 = (flops_per_matmul * iters_fp32) / dt_fp32 / 1e12

    # Mesure FP16 Tensor Core avec accumulation FP32
    iters_fp16 = 25
    t0 = time.perf_counter()
    for _ in range(iters_fp16):
        _ = torch.matmul(a_fp16, b_fp16)
    torch.cuda.synchronize()
    dt_fp16 = time.perf_counter() - t0
    tflops_fp16 = (flops_per_matmul * iters_fp16) / dt_fp16 / 1e12

    del a_fp32, b_fp32, a_fp16, b_fp16
    torch.cuda.empty_cache()

    ratio = tflops_fp16 / max(tflops_fp32, 0.001)
    return {
        "gpu_name": props.name,
        "tflops_fp32": tflops_fp32,
        "tflops_fp16_acc32": tflops_fp16,
        "ratio": ratio,
    }


def main():
    base_dir = os.path.dirname(os.path.abspath(__file__))
    reapply_mode = "--reapply" in sys.argv

    print("============================================================================")
    print("   DIAGNOSTIC MATERIEL & TENSOR CORE - NVIDIA RTX 4050 LAPTOP (10DE:28A1)   ")
    print("============================================================================")

    admin_ok = is_admin()
    print(f" [*] Privileges Administrateur : {'OUI (Lecture BAR0 active)' if admin_ok else 'NON (Lancez en Administrateur pour lire SS0/SS1)'}")

    win_cfg = check_windows_settings()
    fast_startup_off = (win_cfg["hiberboot"] == 0)
    d0_lock_on = (win_cfg["disable_dyn_pstate"] == 1)

    print(f" [*] Demarrage Rapide Windows  : {'DESACTIVE (OK pour Boot UEFI)' if fast_startup_off else 'ACTIF (A desactiver via Option 3 du menu)'}")
    print(f" [*] Verrou Maintien D0/P0     : {'ACTIF (Anti-D3cold / Anti-GC6 actif)' if d0_lock_on else 'STANDARD OPTIMUS (Risque de perte en veille D3cold)'}")

    apps = check_running_compute_apps()
    if apps:
        print("\n [!] ATTENTION : Processus CUDA actif detecte sur le GPU :")
        for app in apps:
            print(f"     -> {app}")
        print("     (Les mesures TFLOPS ci-dessous sont partagees avec votre entrainement en cours)")

    print("\n----------------------------------------------------------------------------")
    print(" 1. LECTURE DIRECTE DES REGISTRES MATERIELS (SS0 / SS1 via ssdiag) :")
    print("----------------------------------------------------------------------------")
    if admin_ok:
        code, ss_out = run_ssdiag(base_dir, reapply=reapply_mode)
        print(ss_out)
        ss_unlocked = ("0x88888888" in ss_out.lower())
    else:
        print(" [INFO] Lecture directe des registres MMIO BAR0 ignoree (droits Admin requis).")
        ss_unlocked = None

    print("\n----------------------------------------------------------------------------")
    print(" 2. MESURE DE DEBIT REEL TENSOR CORE (FP16 avec Accumulation FP32) :")
    print("----------------------------------------------------------------------------")
    bench = run_tensor_benchmark()
    if bench is None:
        print(" [WARN] PyTorch CUDA non disponible dans cet environnement Python.")
    else:
        print(f" [*] Carte Graphique          : {bench['gpu_name']}")
        print(f" [*] Debit FP32 Standard      : {bench['tflops_fp32']:.2f} TFLOPS")
        print(f" [*] Debit Tensor FP16->FP32  : {bench['tflops_fp16_acc32']:.2f} TFLOPS")
        print(f" [*] Ratio Tensor / FP32      : {bench['ratio']:.2f}x (Stock bride ~2.0x | Unlock Full-Rate ~3.5x a 4.0x)")

    print("\n============================================================================")
    if ss_unlocked is True:
        print(" >>> VERDICT OFFICIEL : [ UNLOCK 100% ACTIF (SS0 = 0x88888888 FULL-RATE) ]")
    elif ss_unlocked is False:
        print(" >>> VERDICT OFFICIEL : [ GPU BRIDE D'USINE (50% HALF-RATE ACTUELLEMENT) ]")
    elif bench is not None:
        if bench["ratio"] >= 3.1:
            print(" >>> VERDICT PROBABLE : [ UNLOCK ACTIF (Ratio Tensor Core Full-Rate detecte) ]")
        else:
            print(" >>> VERDICT PROBABLE : [ GPU BRIDE D'USINE (50% HALF-RATE ACTUELLEMENT) ]")
    print("============================================================================")


if __name__ == "__main__":
    main()
