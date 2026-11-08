using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Management;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("UNLOCK_GPU Universal Standalone Dual-Boot Manager")]
[assembly: AssemblyDescription("Exécutable 100% Autonome Tout-en-Un — Gestionnaire Universel de Partition Dual-Boot UEFI UNLOCK_GPU (NVIDIA RTX 20 / 30 / 40)")]
[assembly: AssemblyCompany("Architecture Systeme UEFI & GPU")]
[assembly: AssemblyProduct("UNLOCK_GPU_DUALBOOT")]
[assembly: AssemblyVersion("3.0.0.0")]
[assembly: AssemblyFileVersion("3.0.0.0")]

namespace UnlockGpuDualBoot
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (!IsAdministrator())
            {
                MessageBox.Show(
                    "Veuillez exécuter UNLOCK_GPU_DUALBOOT.exe en tant qu'Administrateur.",
                    "Privilèges Administrateur Requis",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning
                );
                return;
            }

            Application.Run(new MainForm());
        }

        public static bool IsAdministrator()
        {
            try
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                {
                    WindowsPrincipal principal = new WindowsPrincipal(identity);
                    return principal.IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch
            {
                return false;
            }
        }
    }

    public sealed class DetectedNvidiaGpu
    {
        public string RegistrySlot { get; set; }
        public string Name { get; set; }
        public string DeviceIdHex { get; set; }
        public int DeviceIdInt { get; set; }
        public string Family { get; set; }
        public string FamilyLabel { get; set; }
        public bool DisableDynamicPstate { get; set; }
        public bool EnableGpuFirmware { get; set; }

        public override string ToString()
        {
            return string.Format("{0} (10DE:{1} — {2})", Name, DeviceIdHex.ToUpperInvariant(), FamilyLabel);
        }
    }

    public sealed class ShrinkCandidateVolume
    {
        public string DriveLetter { get; set; }
        public int DiskNumber { get; set; }
        public int PartitionNumber { get; set; }
        public string FileSystemLabel { get; set; }
        public ulong SizeBytes { get; set; }
        public ulong FreeBytes { get; set; }
        public bool IsSystemDisk { get; set; }

        public override string ToString()
        {
            double totalGb = Math.Round(SizeBytes / (1024.0 * 1024.0 * 1024.0), 1);
            double freeGb = Math.Round(FreeBytes / (1024.0 * 1024.0 * 1024.0), 1);
            string tag = IsSystemDisk
                ? "[Disque Système Windows — Réduction fin de volume sécurisée]"
                : "[Recommandé — Disque Secondaire Indépendant]";
            string lbl = string.IsNullOrEmpty(FileSystemLabel) ? "Volume Local" : FileSystemLabel;
            return string.Format("Lecteur {0}: [{1}] — Disque {2} ({3} Go libres / {4} Go) {5}",
                DriveLetter, lbl, DiskNumber, freeGb, totalGb, tag);
        }
    }

    public sealed class DualBootStatus
    {
        public List<DetectedNvidiaGpu> Gpus { get; set; }
        public List<ShrinkCandidateVolume> CandidateVolumes { get; set; }
        public bool PartitionExists { get; set; }
        public string PartitionDriveLetter { get; set; }
        public int PartitionDiskNumber { get; set; }
        public int PartitionNumber { get; set; }
        public ulong PartitionSizeBytes { get; set; }
        public bool FilesComplete { get; set; }
        public string DeployedFamiliesSummary { get; set; }
        public bool AutoMode5Sec { get; set; }
        public string UefiEntryGuid { get; set; }
        public bool UefiEntryIsFirst { get; set; }
        public bool FastStartupDisabled { get; set; }
        public bool AllGpusD0Locked { get; set; }
        public string SsdiagSummary { get; set; }
        public bool? GpuUnlocked { get; set; }
    }

    public sealed class MainForm : Form
    {
        private const string PartitionLabel = "UNLOCK_GPU";
        private const string UefiEntryDescription = "UNLOCK_GPU (NVIDIA UEFI Dual-Boot)";
        private const string DisplayClassKeyPath = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
        private const string StorageWmiScope = @"\\.\root\Microsoft\Windows\Storage";
        private const string EmbeddedResourceName = "UnlockPayload.gz";
        private const ulong PartitionTargetBytes = 536870912UL; // 512 Mo exactement

        private readonly string _baseDir;
        private Label _lblGpuState;
        private Label _lblPartState;
        private Label _lblUefiState;
        private Label _lblWinPowerState;
        private ComboBox _cmbTargetVolume;
        private ComboBox _cmbGpuFamily;
        private RadioButton _rbInteractiveMode;
        private RadioButton _rbAuto5SecMode;
        private CheckBox _chkUniversalPcieScan;
        private TextBox _txtLog;
        private Button _btnRefresh;
        private Button _btnCreatePartition;
        private Button _btnRebootOnceUnlock;
        private Button _btnSetDualBootEveryBoot;
        private Button _btnSetWindowsDefault;
        private Button _btnDeletePartition;
        private Button _btnReadUefiLog;
        private Button _btnRestoreWinStock;
        private bool _isBusy;
        private DualBootStatus _lastStatus;

        public MainForm()
        {
            _baseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            InitializeGui();
            Shown += async (s, e) => await RefreshStatusAsync(true, true);
        }

        private void InitializeGui()
        {
            Text = "UNLOCK_GPU — Exécutable Autonome Tout-en-Un (Dual-Boot UEFI Universel RTX 20 / 30 / 40)";
            Size = new Size(1020, 820);
            MinimumSize = new Size(980, 780);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(20, 24, 33);
            ForeColor = Color.FromArgb(235, 240, 245);
            Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);

            Panel headerPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 68,
                BackColor = Color.FromArgb(14, 18, 26),
                Padding = new Padding(18, 10, 18, 10)
            };

            Label lblTitle = new Label
            {
                Text = "CENTRE DUAL-BOOT UEFI AUTONOME — UNLOCK_GPU (100% STANDALONE & ZÉRO RISQUE WINDOWS)",
                Font = new Font("Segoe UI", 12.0f, FontStyle.Bold),
                ForeColor = Color.FromArgb(86, 196, 255),
                AutoSize = true,
                Location = new Point(18, 10)
            };

            Label lblSubtitle = new Label
            {
                Text = "Tous les firmwares signés & outils UEFI sont embarqués dans ce .EXE — Compatible tout PC & NVIDIA RTX Séries 20 / 30 / 40",
                Font = new Font("Segoe UI", 9.0f, FontStyle.Regular),
                ForeColor = Color.FromArgb(170, 182, 198),
                AutoSize = true,
                Location = new Point(18, 38)
            };

            headerPanel.Controls.Add(lblTitle);
            headerPanel.Controls.Add(lblSubtitle);

            GroupBox grpStatus = new GroupBox
            {
                Text = " 1. Diagnostic Matériel Automatique (GPU NVIDIA / Partition / NVRAM UEFI) ",
                ForeColor = Color.FromArgb(130, 200, 255),
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                Location = new Point(18, 76),
                Size = new Size(966, 130),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };

            _lblGpuState = CreateStatusLabel(grpStatus, "GPU NVIDIA Détecté & État :", "Analyse en cours...", 24);
            _lblPartState = CreateStatusLabel(grpStatus, "Partition Dual-Boot (FAT32) :", "Analyse en cours...", 50);
            _lblUefiState = CreateStatusLabel(grpStatus, "Entrée Boot UEFI (NVRAM) :", "Analyse en cours...", 76);
            _lblWinPowerState = CreateStatusLabel(grpStatus, "Maintien Windows (Anti-D3cold) :", "Analyse en cours...", 102);

            GroupBox grpConfig = new GroupBox
            {
                Text = " 2. Configuration Universelle (Choix du Disque Hôte, de l'Architecture GPU & du Mode de Boot) ",
                ForeColor = Color.FromArgb(130, 200, 255),
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                Location = new Point(18, 214),
                Size = new Size(966, 148),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };

            Label lblVol = new Label
            {
                Text = "Volume où prélever 512 Mo :",
                Font = new Font("Segoe UI", 9.0f, FontStyle.Bold),
                ForeColor = Color.FromArgb(200, 215, 230),
                Location = new Point(16, 28),
                Size = new Size(210, 22)
            };

            _cmbTargetVolume = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(230, 25),
                Size = new Size(718, 26),
                BackColor = Color.FromArgb(30, 36, 48),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9.0f, FontStyle.Regular),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };

            Label lblFam = new Label
            {
                Text = "Famille GPU à installer :",
                Font = new Font("Segoe UI", 9.0f, FontStyle.Bold),
                ForeColor = Color.FromArgb(200, 215, 230),
                Location = new Point(16, 60),
                Size = new Size(210, 22)
            };

            _cmbGpuFamily = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(230, 57),
                Size = new Size(430, 26),
                BackColor = Color.FromArgb(30, 36, 48),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9.0f, FontStyle.Regular)
            };
            _cmbGpuFamily.Items.Add("AUTO — Détecter automatiquement la carte NVIDIA de ce PC (Recommandé)");
            _cmbGpuFamily.Items.Add("AD102 — Toutes cartes NVIDIA RTX Série 40 (100% Embarqué Hors-Ligne)");
            _cmbGpuFamily.Items.Add("GA102 — Toutes cartes NVIDIA RTX Série 30 (Ampere Desktop & Laptop)");
            _cmbGpuFamily.Items.Add("TU10X — Toutes cartes NVIDIA RTX Série 20 / GTX 16 (Turing + GSP)");
            _cmbGpuFamily.Items.Add("ALL — Pack Universel Multi-GPU (Installer Séries 20 + 30 + 40 simultanément)");
            _cmbGpuFamily.SelectedIndex = 0;

            _chkUniversalPcieScan = new CheckBox
            {
                Text = "Scan PCIe Universel (Auto-détecter le slot GPU au boot)",
                Font = new Font("Segoe UI", 9.0f, FontStyle.Regular),
                ForeColor = Color.FromArgb(140, 230, 175),
                Location = new Point(672, 59),
                AutoSize = true,
                Checked = true
            };

            _rbInteractiveMode = new RadioButton
            {
                Text = "Mode Dual-Boot Interactif (Recommandé) : À l'écran UEFI, [ENTRÉE/ESPACE] = Déverrouiller GPU + Windows | [ÉCHAP] = Windows Normal",
                Font = new Font("Segoe UI", 9.0f, FontStyle.Regular),
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(16, 92),
                Checked = true
            };

            _rbAuto5SecMode = new RadioButton
            {
                Text = "Mode Automatique 5 secondes : Déverrouille automatiquement après 5s et lance Windows sans toucher au clavier ([ÉCHAP] annule)",
                Font = new Font("Segoe UI", 9.0f, FontStyle.Regular),
                ForeColor = Color.FromArgb(210, 220, 230),
                AutoSize = true,
                Location = new Point(16, 118),
                Checked = false
            };

            grpConfig.Controls.Add(lblVol);
            grpConfig.Controls.Add(_cmbTargetVolume);
            grpConfig.Controls.Add(lblFam);
            grpConfig.Controls.Add(_cmbGpuFamily);
            grpConfig.Controls.Add(_chkUniversalPcieScan);
            grpConfig.Controls.Add(_rbInteractiveMode);
            grpConfig.Controls.Add(_rbAuto5SecMode);

            GroupBox grpActions = new GroupBox
            {
                Text = " 3. Actions de Partitionnement & Sélection du Démarrage Dual-Boot ",
                ForeColor = Color.FromArgb(130, 200, 255),
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                Location = new Point(18, 370),
                Size = new Size(966, 176),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };

            _btnCreatePartition = CreateActionButton(
                grpActions,
                "1. Créer / Mettre à jour la Partition UNLOCK_GPU (512 Mo) + Entrée Dual-Boot UEFI",
                new Point(16, 26),
                new Size(460, 42),
                Color.FromArgb(22, 118, 68)
            );
            _btnCreatePartition.Click += async (s, e) => await CreateOrUpdateDualBootAsync();

            _btnRebootOnceUnlock = CreateActionButton(
                grpActions,
                "2. REDÉMARRER EN MODE UNLOCK_GPU (1 seul boot via BootNext -> Windows)",
                new Point(490, 26),
                new Size(460, 42),
                Color.FromArgb(196, 108, 18)
            );
            _btnRebootOnceUnlock.Click += async (s, e) => await RebootToUnlockGpuOnceAsync();

            _btnSetDualBootEveryBoot = CreateActionButton(
                grpActions,
                "3. Activer le Choix Dual-Boot à CHAQUE Démarrage (UNLOCK_GPU en N°1 -> Windows)",
                new Point(16, 78),
                new Size(460, 40),
                Color.FromArgb(36, 92, 162)
            );
            _btnSetDualBootEveryBoot.Click += async (s, e) => await SetBootOrderAsync(true);

            _btnSetWindowsDefault = CreateActionButton(
                grpActions,
                "4. Remettre Windows Direct par Défaut en N°1 (Sans écran UNLOCK_GPU au boot)",
                new Point(490, 78),
                new Size(460, 40),
                Color.FromArgb(52, 73, 94)
            );
            _btnSetWindowsDefault.Click += async (s, e) => await SetBootOrderAsync(false);

            _btnRefresh = CreateActionButton(
                grpActions,
                "Diagnostic Complet (+ Sonde BAR0)",
                new Point(16, 128),
                new Size(224, 36),
                Color.FromArgb(45, 55, 72)
            );
            _btnRefresh.Click += async (s, e) => await RefreshStatusAsync(true, true);

            _btnReadUefiLog = CreateActionButton(
                grpActions,
                "Lire le Journal UEFI (loader.log)",
                new Point(252, 128),
                new Size(224, 36),
                Color.FromArgb(45, 55, 72)
            );
            _btnReadUefiLog.Click += (s, e) => ReadUefiLoaderLog();

            _btnRestoreWinStock = CreateActionButton(
                grpActions,
                "Restaurer Alimentation Windows Stock",
                new Point(490, 128),
                new Size(224, 36),
                Color.FromArgb(95, 65, 45)
            );
            _btnRestoreWinStock.Click += async (s, e) => await RestoreWindowsPowerStockAsync();

            _btnDeletePartition = CreateActionButton(
                grpActions,
                "Supprimer Partition & Refusionner",
                new Point(726, 128),
                new Size(224, 36),
                Color.FromArgb(148, 38, 38)
            );
            _btnDeletePartition.Click += async (s, e) => await RemoveDualBootAndMergePartitionAsync();

            GroupBox grpLog = new GroupBox
            {
                Text = " Journal d'Exécution & Diagnostic Matériel Universel (MMIO BAR0 / Partition GPT / NVRAM UEFI) ",
                ForeColor = Color.FromArgb(130, 200, 255),
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                Location = new Point(18, 554),
                Size = new Size(966, 212),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };

            _txtLog = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Color.FromArgb(12, 15, 20),
                ForeColor = Color.FromArgb(205, 225, 245),
                Font = new Font("Consolas", 9.5f, FontStyle.Regular),
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None
            };

            Panel logInnerPanel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(8)
            };
            logInnerPanel.Controls.Add(_txtLog);
            grpLog.Controls.Add(logInnerPanel);

            Controls.Add(headerPanel);
            Controls.Add(grpStatus);
            Controls.Add(grpConfig);
            Controls.Add(grpActions);
            Controls.Add(grpLog);
        }

        private Label CreateStatusLabel(Control parent, string title, string initialValue, int y)
        {
            Label lblTitle = new Label
            {
                Text = title,
                Font = new Font("Segoe UI", 9.2f, FontStyle.Bold),
                ForeColor = Color.FromArgb(190, 205, 220),
                Location = new Point(16, y),
                Size = new Size(235, 20)
            };

            Label lblVal = new Label
            {
                Text = initialValue,
                Font = new Font("Segoe UI", 9.2f, FontStyle.Bold),
                ForeColor = Color.White,
                Location = new Point(255, y),
                Size = new Size(695, 20),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };

            parent.Controls.Add(lblTitle);
            parent.Controls.Add(lblVal);
            return lblVal;
        }

        private Button CreateActionButton(Control parent, string text, Point location, Size size, Color backColor)
        {
            Button btn = new Button
            {
                Text = text,
                Location = location,
                Size = size,
                BackColor = backColor,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9.0f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btn.FlatAppearance.BorderColor = Color.FromArgb(90, 110, 135);
            btn.FlatAppearance.BorderSize = 1;
            parent.Controls.Add(btn);
            return btn;
        }

        private void AppendLog(string message)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(AppendLog), message);
                return;
            }
            string line = string.Format("[{0:HH:mm:ss}] {1}{2}", DateTime.Now, message, Environment.NewLine);
            _txtLog.AppendText(line);
        }

        private void SetBusy(bool busy)
        {
            _isBusy = busy;
            _btnRefresh.Enabled = !busy;
            _btnCreatePartition.Enabled = !busy;
            _btnRebootOnceUnlock.Enabled = !busy;
            _btnSetDualBootEveryBoot.Enabled = !busy;
            _btnSetWindowsDefault.Enabled = !busy;
            _btnDeletePartition.Enabled = !busy;
            _btnReadUefiLog.Enabled = !busy;
            _btnRestoreWinStock.Enabled = !busy;
            _cmbTargetVolume.Enabled = !busy;
            _cmbGpuFamily.Enabled = !busy;
            Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
        }

        /// <summary>
        /// Extrait un ou plusieurs fichiers depuis le conteneur GZip embarqué dans les ressources de l'EXE.
        /// Permet au .EXE de fonctionner de manière 100% autonome même s'il est copié tout seul sur un autre PC.
        /// </summary>
        private Dictionary<string, long> ExtractFromEmbeddedPayload(string targetDirectory, HashSet<string> filterFileNames)
        {
            Dictionary<string, long> extracted = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            Assembly asm = Assembly.GetExecutingAssembly();
            using (Stream resStream = asm.GetManifestResourceStream(EmbeddedResourceName))
            {
                if (resStream == null)
                {
                    throw new InvalidOperationException("Ressource embarquée '" + EmbeddedResourceName + "' introuvable dans l'exécutable.");
                }

                using (GZipStream gz = new GZipStream(resStream, CompressionMode.Decompress))
                using (BinaryReader br = new BinaryReader(gz, Encoding.UTF8))
                {
                    int fileCount = br.ReadInt32();
                    for (int i = 0; i < fileCount; i++)
                    {
                        string name = br.ReadString();
                        long length = br.ReadInt64();

                        if (filterFileNames == null || filterFileNames.Contains(name))
                        {
                            string outPath;
                            if (string.Equals(name, "BOOTX64.EFI", StringComparison.OrdinalIgnoreCase))
                            {
                                string efiDir = Path.Combine(targetDirectory, @"EFI\BOOT");
                                Directory.CreateDirectory(efiDir);
                                outPath = Path.Combine(efiDir, "BOOTX64.EFI");
                            }
                            else
                            {
                                Directory.CreateDirectory(targetDirectory);
                                outPath = Path.Combine(targetDirectory, name);
                            }

                            using (FileStream fsOut = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None))
                            {
                                byte[] buffer = new byte[65536];
                                long remaining = length;
                                while (remaining > 0)
                                {
                                    int toRead = (int)Math.Min(buffer.Length, remaining);
                                    int read = br.Read(buffer, 0, toRead);
                                    if (read <= 0) throw new EndOfStreamException("Fin de flux inattendue lors de l'extraction de " + name);
                                    fsOut.Write(buffer, 0, read);
                                    remaining -= read;
                                }
                            }
                            extracted[name] = length;
                        }
                        else
                        {
                            byte[] skipBuf = new byte[65536];
                            long remaining = length;
                            while (remaining > 0)
                            {
                                int toRead = (int)Math.Min(skipBuf.Length, remaining);
                                int read = br.Read(skipBuf, 0, toRead);
                                if (read <= 0) break;
                                remaining -= read;
                            }
                        }
                    }
                }
            }
            return extracted;
        }

        private string EnsureStandaloneToolsAvailable()
        {
            string unlockerDir = Path.Combine(_baseDir, "nv-uefi-unlocker");
            string ssdiagPath = Path.Combine(unlockerDir, "ssdiag-x86_64-windows.exe");
            string assetsPath = Path.Combine(unlockerDir, "assets-x86_64-windows.exe");
            string makePath = Path.Combine(unlockerDir, "make-usbroot.ps1");
            string bootEfiPath = Path.Combine(unlockerDir, "BOOTX64.EFI");

            if (File.Exists(ssdiagPath) && File.Exists(assetsPath) && File.Exists(makePath) && File.Exists(bootEfiPath))
            {
                return unlockerDir;
            }

            Directory.CreateDirectory(unlockerDir);
            HashSet<string> toolFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "ssdiag-x86_64-windows.exe",
                "assets-x86_64-windows.exe",
                "make-usbroot.ps1",
                "BOOTX64.EFI"
            };
            ExtractFromEmbeddedPayload(unlockerDir, toolFiles);
            string efiExtracted = Path.Combine(unlockerDir, @"EFI\BOOT\BOOTX64.EFI");
            if (File.Exists(efiExtracted) && !File.Exists(bootEfiPath))
            {
                File.Copy(efiExtracted, bootEfiPath, true);
            }
            return unlockerDir;
        }

        private async Task RefreshStatusAsync(bool verboseLog, bool probeSsdiag)
        {
            if (_isBusy) return;
            SetBusy(true);
            try
            {
                if (verboseLog)
                {
                    AppendLog("Inspection matérielle via l'API WMI Windows Storage (Cartes NVIDIA, Volumes GPT, Partition UNLOCK_GPU, NVRAM UEFI)...");
                }

                DualBootStatus status = await Task.Run(() => QuerySystemStatus(probeSsdiag));
                _lastStatus = status;

                string prevSelectedDrive = "";
                ShrinkCandidateVolume prevVol = _cmbTargetVolume.SelectedItem as ShrinkCandidateVolume;
                if (prevVol != null)
                {
                    prevSelectedDrive = prevVol.DriveLetter;
                }

                _cmbTargetVolume.Items.Clear();
                int bestIndex = 0;
                for (int i = 0; i < status.CandidateVolumes.Count; i++)
                {
                    ShrinkCandidateVolume v = status.CandidateVolumes[i];
                    _cmbTargetVolume.Items.Add(v);
                    if (!string.IsNullOrEmpty(prevSelectedDrive) &&
                        string.Equals(v.DriveLetter, prevSelectedDrive, StringComparison.OrdinalIgnoreCase))
                    {
                        bestIndex = i;
                    }
                    else if (string.IsNullOrEmpty(prevSelectedDrive) && !v.IsSystemDisk && bestIndex == 0)
                    {
                        bestIndex = i;
                    }
                }
                if (_cmbTargetVolume.Items.Count > 0)
                {
                    _cmbTargetVolume.SelectedIndex = bestIndex;
                }

                string gpuDesc = status.Gpus.Count > 0
                    ? status.Gpus[0].ToString()
                    : "Aucune carte NVIDIA RTX détectée";

                if (status.GpuUnlocked == true)
                {
                    _lblGpuState.Text = gpuDesc + "  >>>  DÉVERROUILLÉ 100% FULL-RATE (SS0=0x88888888)";
                    _lblGpuState.ForeColor = Color.FromArgb(80, 235, 130);
                }
                else if (status.GpuUnlocked == false)
                {
                    _lblGpuState.Text = gpuDesc + "  >>>  BRIDÉ D'USINE (50% Half-Rate actuellement)";
                    _lblGpuState.ForeColor = Color.FromArgb(255, 185, 75);
                }
                else
                {
                    _lblGpuState.Text = gpuDesc;
                    _lblGpuState.ForeColor = Color.FromArgb(86, 196, 255);
                }

                if (status.PartitionExists)
                {
                    double sizeMb = Math.Round(status.PartitionSizeBytes / (1024.0 * 1024.0), 1);
                    string modeStr = status.AutoMode5Sec ? "Mode Auto 5s" : "Mode Interactif (ENTRÉE/ÉCHAP)";
                    string filesStr = status.FilesComplete
                        ? ("PRÊTE (" + status.DeployedFamiliesSummary + ")")
                        : "FICHIERS INCOMPLETS";
                    _lblPartState.Text = string.Format(
                        "PRÉSENTE — Lecteur {0}: (Disque {1}, Part {2}, {3} Mo, FAT32) | {4} | {5}",
                        status.PartitionDriveLetter,
                        status.PartitionDiskNumber,
                        status.PartitionNumber,
                        sizeMb,
                        filesStr,
                        modeStr
                    );
                    _lblPartState.ForeColor = status.FilesComplete ? Color.FromArgb(80, 235, 130) : Color.FromArgb(255, 110, 110);
                }
                else
                {
                    _lblPartState.Text = "NON CRÉÉE (Sélectionnez un volume ci-dessous et cliquez sur le Bouton 1)";
                    _lblPartState.ForeColor = Color.FromArgb(255, 185, 75);
                }

                if (!string.IsNullOrEmpty(status.UefiEntryGuid))
                {
                    if (status.UefiEntryIsFirst)
                    {
                        _lblUefiState.Text = string.Format("ACTIVE EN N°1 (Menu Dual-Boot affiché à chaque démarrage) — GUID {0}", status.UefiEntryGuid);
                        _lblUefiState.ForeColor = Color.FromArgb(86, 196, 255);
                    }
                    else
                    {
                        _lblUefiState.Text = string.Format("ENREGISTRÉE — Windows est en N°1 par défaut (Prêt pour BootNext 1-clic) — GUID {0}", status.UefiEntryGuid);
                        _lblUefiState.ForeColor = Color.FromArgb(80, 235, 130);
                    }
                }
                else
                {
                    _lblUefiState.Text = "NON ENREGISTRÉE — Windows Boot Manager est le seul démarrage actif";
                    _lblUefiState.ForeColor = Color.FromArgb(200, 210, 220);
                }

                if (status.FastStartupDisabled && status.AllGpusD0Locked)
                {
                    _lblWinPowerState.Text = "OPTIMISÉ POUR UNLOCK_GPU (HiberbootEnabled=0 + Anti-D3cold actif sur GPU NVIDIA)";
                    _lblWinPowerState.ForeColor = Color.FromArgb(80, 235, 130);
                }
                else
                {
                    _lblWinPowerState.Text = string.Format(
                        "HiberbootEnabled={0} | Anti-D3cold NVIDIA={1} (Sera configuré automatiquement)",
                        status.FastStartupDisabled ? "0 (OK)" : "1 (Actif)",
                        status.AllGpusD0Locked ? "Actif (OK)" : "Standard"
                    );
                    _lblWinPowerState.ForeColor = Color.FromArgb(255, 185, 75);
                }

                if (verboseLog && !string.IsNullOrWhiteSpace(status.SsdiagSummary))
                {
                    AppendLog("Sortie sonde matérielle universelle ssdiag :\r\n" + status.SsdiagSummary);
                }
            }
            catch (Exception ex)
            {
                AppendLog("[ERREUR] Échec lors du diagnostic : " + ex.Message);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private List<DetectedNvidiaGpu> DiscoverNvidiaGpus()
        {
            List<DetectedNvidiaGpu> list = new List<DetectedNvidiaGpu>();
            try
            {
                using (RegistryKey classKey = Registry.LocalMachine.OpenSubKey(DisplayClassKeyPath, false))
                {
                    if (classKey == null) return list;
                    foreach (string subName in classKey.GetSubKeyNames())
                    {
                        if (!Regex.IsMatch(subName, @"^\d{4}$")) continue;
                        using (RegistryKey subKey = classKey.OpenSubKey(subName, false))
                        {
                            if (subKey == null) continue;
                            string matchingId = Convert.ToString(subKey.GetValue("MatchingDeviceId", ""));
                            Match m = Regex.Match(matchingId, @"(?i)ven_10de&dev_([0-9a-f]{4})");
                            if (!m.Success) continue;

                            string devHex = m.Groups[1].Value.ToLowerInvariant();
                            int devInt = Convert.ToInt32(devHex, 16);
                            string desc = Convert.ToString(subKey.GetValue("DriverDesc", "NVIDIA GPU"));

                            string family = "";
                            string familyLabel = "";
                            if (devInt >= 0x2600 && devInt <= 0x28FF)
                            {
                                family = "ad102";
                                familyLabel = "RTX Série 40 (Ada)";
                            }
                            else if ((devInt >= 0x2200 && devInt <= 0x22FF) || (devInt >= 0x2400 && devInt <= 0x25FF))
                            {
                                family = "ga102";
                                familyLabel = "RTX Série 30 (Ampere)";
                            }
                            else if ((devInt >= 0x1E02 && devInt <= 0x1FFF) || (devInt >= 0x2182 && devInt <= 0x21D1))
                            {
                                family = "tu10x";
                                familyLabel = "RTX Série 20 / 16 (Turing)";
                            }
                            else
                            {
                                family = "ad102";
                                familyLabel = "Architecture NVIDIA (" + devHex + ")";
                            }

                            object ddp = subKey.GetValue("DisableDynamicPstate");
                            object egf = subKey.GetValue("EnableGpuFirmware");

                            list.Add(new DetectedNvidiaGpu
                            {
                                RegistrySlot = subName,
                                Name = desc,
                                DeviceIdHex = devHex,
                                DeviceIdInt = devInt,
                                Family = family,
                                FamilyLabel = familyLabel,
                                DisableDynamicPstate = (ddp != null && Convert.ToInt32(ddp) == 1),
                                EnableGpuFirmware = (egf != null && Convert.ToInt32(egf) == 1)
                            });
                        }
                    }
                }
            }
            catch
            {
            }
            return list;
        }

        private static string[] GetExpectedFilesForFamily(string family)
        {
            switch (family.ToLowerInvariant())
            {
                case "ga102":
                    return new string[]
                    {
                        "booter_load_ga102.bin",
                        "gsp_boot_desc_ga102.bin",
                        "gsp_boot_ga102.bin",
                        "fwimage_ga102.bin",
                        "fwsignature_ga102.bin",
                        "scrubber_ad10x.bin",
                        "v67-ga102-four-plm.bin"
                    };
                case "tu10x":
                    return new string[]
                    {
                        "booter_load_tu10x.bin",
                        "gsp_boot_desc_tu10x.bin",
                        "gsp_boot_tu10x.bin",
                        "fwimage_tu10x.bin",
                        "fwsignature_tu10x.bin",
                        "sec2_bl_tu10x.bin",
                        "v67-tu10x-four-plm.bin"
                    };
                default:
                    return new string[]
                    {
                        "booter_load_ad102.bin",
                        "gsp_boot_desc_ad102.bin",
                        "gsp_boot_ad102.bin",
                        "fwimage_ad102.bin",
                        "fwsignature_ad102.bin",
                        "scrubber_ad10x.bin",
                        "v67-ad102-four-plm.bin"
                    };
            }
        }

        private DualBootStatus QuerySystemStatus(bool probeSsdiag)
        {
            List<DetectedNvidiaGpu> gpus = DiscoverNvidiaGpus();
            bool allD0 = gpus.Count > 0;
            foreach (DetectedNvidiaGpu g in gpus)
            {
                if (!g.DisableDynamicPstate) allD0 = false;
            }

            DualBootStatus st = new DualBootStatus
            {
                Gpus = gpus,
                CandidateVolumes = new List<ShrinkCandidateVolume>(),
                PartitionExists = false,
                PartitionDriveLetter = "",
                PartitionDiskNumber = -1,
                PartitionNumber = -1,
                PartitionSizeBytes = 0,
                FilesComplete = false,
                DeployedFamiliesSummary = "",
                AutoMode5Sec = false,
                UefiEntryGuid = "",
                UefiEntryIsFirst = false,
                FastStartupDisabled = false,
                AllGpusD0Locked = allD0,
                SsdiagSummary = "",
                GpuUnlocked = null
            };

            try
            {
                using (RegistryKey kPower = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Power", false))
                {
                    if (kPower != null)
                    {
                        object v = kPower.GetValue("HiberbootEnabled");
                        if (v != null && Convert.ToInt32(v) == 0)
                        {
                            st.FastStartupDisabled = true;
                        }
                    }
                }
            }
            catch
            {
            }

            if (probeSsdiag)
            {
                try
                {
                    string unlockerDir = EnsureStandaloneToolsAvailable();
                    string ssdiagPath = Path.Combine(unlockerDir, "ssdiag-x86_64-windows.exe");
                    if (File.Exists(ssdiagPath))
                    {
                        int exitCode;
                        string output = RunExternalProcess(ssdiagPath, "", 12000, out exitCode);
                        st.SsdiagSummary = output.Trim();
                        st.GpuUnlocked = (output.IndexOf("0x88888888", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                          output.IndexOf("UNLOCKED", StringComparison.OrdinalIgnoreCase) >= 0);
                    }
                }
                catch
                {
                }
            }

            try
            {
                ManagementScope scope = new ManagementScope(StorageWmiScope);
                scope.Connect();

                char sysDriveChar = 'C';
                string sysEnv = Environment.GetEnvironmentVariable("SystemDrive");
                if (!string.IsNullOrEmpty(sysEnv))
                {
                    sysDriveChar = char.ToUpperInvariant(sysEnv[0]);
                }

                Dictionary<char, int> driveToDisk = new Dictionary<char, int>();
                Dictionary<char, int> driveToPartNum = new Dictionary<char, int>();
                Dictionary<char, ulong> driveToPartSize = new Dictionary<char, ulong>();

                using (ManagementObjectSearcher partSearcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT DiskNumber, PartitionNumber, DriveLetter, Size FROM MSFT_Partition")))
                {
                    foreach (ManagementObject part in partSearcher.Get())
                    {
                        object dlObj = part["DriveLetter"];
                        if (dlObj == null) continue;
                        char dlChar = Convert.ToChar(dlObj);
                        if (!char.IsLetter(dlChar)) continue;
                        dlChar = char.ToUpperInvariant(dlChar);

                        driveToDisk[dlChar] = Convert.ToInt32(part["DiskNumber"]);
                        driveToPartNum[dlChar] = Convert.ToInt32(part["PartitionNumber"]);
                        driveToPartSize[dlChar] = Convert.ToUInt64(part["Size"]);
                    }
                }

                int sysDiskNumber = driveToDisk.ContainsKey(sysDriveChar) ? driveToDisk[sysDriveChar] : 0;

                using (ManagementObjectSearcher volSearcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT DriveLetter, FileSystemLabel, FileSystem, DriveType, Size, SizeRemaining FROM MSFT_Volume")))
                {
                    foreach (ManagementObject vol in volSearcher.Get())
                    {
                        string fsLabel = Convert.ToString(vol["FileSystemLabel"] ?? "");
                        string fsType = Convert.ToString(vol["FileSystem"] ?? "");
                        object dlObj = vol["DriveLetter"];
                        char dlChar = '\0';
                        if (dlObj != null)
                        {
                            char c = Convert.ToChar(dlObj);
                            if (char.IsLetter(c)) dlChar = char.ToUpperInvariant(c);
                        }

                        if (string.Equals(fsLabel, PartitionLabel, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(fsType, "FAT32", StringComparison.OrdinalIgnoreCase))
                        {
                            st.PartitionExists = true;
                            st.PartitionDriveLetter = dlChar != '\0' ? dlChar.ToString() : "";
                            st.PartitionSizeBytes = Convert.ToUInt64(vol["Size"] ?? 0UL);
                            if (dlChar != '\0' && driveToDisk.ContainsKey(dlChar))
                            {
                                st.PartitionDiskNumber = driveToDisk[dlChar];
                                st.PartitionNumber = driveToPartNum[dlChar];
                            }
                            continue;
                        }

                        uint driveType = Convert.ToUInt32(vol["DriveType"] ?? 0);
                        ulong sizeRem = Convert.ToUInt64(vol["SizeRemaining"] ?? 0UL);
                        ulong sizeTot = Convert.ToUInt64(vol["Size"] ?? 0UL);

                        if (dlChar != '\0' &&
                            driveType == 3 &&
                            string.Equals(fsType, "NTFS", StringComparison.OrdinalIgnoreCase) &&
                            sizeRem > 5UL * 1024UL * 1024UL * 1024UL &&
                            driveToDisk.ContainsKey(dlChar))
                        {
                            int dNum = driveToDisk[dlChar];
                            int pNum = driveToPartNum[dlChar];
                            st.CandidateVolumes.Add(new ShrinkCandidateVolume
                            {
                                DriveLetter = dlChar.ToString(),
                                DiskNumber = dNum,
                                PartitionNumber = pNum,
                                FileSystemLabel = fsLabel,
                                SizeBytes = sizeTot,
                                FreeBytes = sizeRem,
                                IsSystemDisk = (dNum == sysDiskNumber)
                            });
                        }
                    }
                }
            }
            catch
            {
            }

            st.CandidateVolumes.Sort((a, b) =>
            {
                if (a.IsSystemDisk != b.IsSystemDisk) return a.IsSystemDisk ? 1 : -1;
                return string.Compare(a.DriveLetter, b.DriveLetter, StringComparison.OrdinalIgnoreCase);
            });

            if (st.PartitionExists && !string.IsNullOrEmpty(st.PartitionDriveLetter))
            {
                string root = st.PartitionDriveLetter + @":\";
                bool bootEfiOk = File.Exists(Path.Combine(root, @"EFI\BOOT\BOOTX64.EFI"));
                List<string> readyFams = new List<string>();

                foreach (string fam in new[] { "ad102", "ga102", "tu10x" })
                {
                    bool famOk = true;
                    foreach (string req in GetExpectedFilesForFamily(fam))
                    {
                        string p = Path.Combine(root, req);
                        if (!File.Exists(p) || new FileInfo(p).Length == 0)
                        {
                            famOk = false;
                            break;
                        }
                    }
                    if (famOk) readyFams.Add(fam.ToUpperInvariant());
                }

                st.FilesComplete = bootEfiOk && readyFams.Count > 0;
                st.DeployedFamiliesSummary = string.Join(" + ", readyFams.ToArray());
                st.AutoMode5Sec = File.Exists(Path.Combine(root, "i_know_what_i_am_doing.txt"));
            }

            string guid;
            bool isFirst;
            FindUefiBootEntry(out guid, out isFirst);
            st.UefiEntryGuid = guid;
            st.UefiEntryIsFirst = isFirst;

            return st;
        }

        private void FindUefiBootEntry(out string foundGuid, out bool isFirstInDisplayOrder)
        {
            foundGuid = "";
            isFirstInDisplayOrder = false;

            int exitCode;
            string bcdOut = RunExternalProcess("bcdedit.exe", "/enum firmware", 10000, out exitCode);
            if (string.IsNullOrWhiteSpace(bcdOut))
            {
                return;
            }

            string[] blocks = Regex.Split(bcdOut, @"\r?\n\r?\n");
            string firstDisplayGuid = "";

            foreach (string block in blocks)
            {
                if (block.IndexOf("{fwbootmgr}", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    MatchCollection mc = Regex.Matches(block, @"\{[0-9a-fA-F\-]{36}\}|\{bootmgr\}");
                    if (mc.Count > 0)
                    {
                        firstDisplayGuid = mc[0].Value;
                    }
                }

                if (block.IndexOf("UNLOCK_GPU", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Match mGuid = Regex.Match(block, @"\{[0-9a-fA-F\-]{36}\}");
                    if (mGuid.Success)
                    {
                        foundGuid = mGuid.Value;
                    }
                }
            }

            if (!string.IsNullOrEmpty(foundGuid) &&
                !string.IsNullOrEmpty(firstDisplayGuid) &&
                string.Equals(foundGuid, firstDisplayGuid, StringComparison.OrdinalIgnoreCase))
            {
                isFirstInDisplayOrder = true;
            }
        }

        private List<string> DetermineTargetFamiliesToDeploy()
        {
            int idx = _cmbGpuFamily.SelectedIndex;
            List<string> families = new List<string>();
            if (idx == 1) families.Add("ad102");
            else if (idx == 2) families.Add("ga102");
            else if (idx == 3) families.Add("tu10x");
            else if (idx == 4)
            {
                families.Add("ad102");
                families.Add("ga102");
                families.Add("tu10x");
            }
            else
            {
                if (_lastStatus != null && _lastStatus.Gpus != null && _lastStatus.Gpus.Count > 0)
                {
                    foreach (DetectedNvidiaGpu g in _lastStatus.Gpus)
                    {
                        if (!families.Contains(g.Family))
                        {
                            families.Add(g.Family);
                        }
                    }
                }
                if (families.Count == 0)
                {
                    families.Add("ad102");
                }
            }
            return families;
        }

        private async Task CreateOrUpdateDualBootAsync()
        {
            if (_isBusy) return;

            ShrinkCandidateVolume selectedVol = _cmbTargetVolume.SelectedItem as ShrinkCandidateVolume;
            if (selectedVol == null && (_lastStatus == null || !_lastStatus.PartitionExists))
            {
                MessageBox.Show("Veuillez sélectionner un volume disque dans la liste déroulante.", "Volume Requis", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string targetDriveToShrink = selectedVol != null ? selectedVol.DriveLetter : "D";
            List<string> targetFamilies = DetermineTargetFamiliesToDeploy();
            bool auto5Sec = _rbAuto5SecMode.Checked;
            bool universalPcie = _chkUniversalPcieScan.Checked;

            string famSummary = string.Join(", ", targetFamilies.ToArray()).ToUpperInvariant();
            string modeDesc = auto5Sec
                ? "Mode Automatique 5 secondes (déverrouille après 5s et lance Windows)"
                : "Mode Interactif (ENTRÉE/ESPACE = Unlock GPU -> Windows | ÉCHAP = Windows Normal)";

            DialogResult confirm = MessageBox.Show(
                "Configuration Universelle prête à être appliquée :\n\n" +
                "• Volume hôte sélectionné : Lecteur " + targetDriveToShrink + ": (prélèvement non-destructif de 512 Mo en fin de partition)\n" +
                "• Sécurité Boot Windows : La partition EFI de Windows (\\EFI\\Microsoft\\Boot\\bootmgfw.efi) n'est JAMAIS touchée\n" +
                "• Famille(s) GPU déployée(s) : " + famSummary + "\n" +
                "• Détection PCIe au boot : " + (universalPcie ? "Universelle (toute carte mère / tout slot PCIe)" : "Ciblée BDF") + "\n" +
                "• Mode au démarrage : " + modeDesc + "\n\n" +
                "Voulez-vous créer/mettre à jour la partition Dual-Boot 'UNLOCK_GPU' maintenant ?",
                "Confirmation — Création / Mise à jour Universelle du Dual-Boot",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question
            );

            if (confirm != DialogResult.Yes)
            {
                return;
            }

            SetBusy(true);
            try
            {
                AppendLog("==========================================================================");
                AppendLog("DÉPLOIEMENT AUTONOME DE LA PARTITION DUAL-BOOT UEFI UNLOCK_GPU");
                AppendLog("==========================================================================");

                await Task.Run(() => ApplyWindowsContinuousHoldSettings(targetFamilies.Contains("tu10x")));
                AppendLog("[OK] Maintien continu Windows activé sur toutes les cartes NVIDIA détectées.");

                string driveLetter = await Task.Run(() => EnsureDualBootPartitionExistsWmi(targetDriveToShrink));
                AppendLog(string.Format("[OK] Partition FAT32 '{0}' prête sur le lecteur {1}:\\", PartitionLabel, driveLetter));

                foreach (string fam in targetFamilies)
                {
                    AppendLog("Déploiement et vérification des firmwares signés pour la famille : " + fam.ToUpperInvariant() + "...");
                    if (string.Equals(fam, "ad102", StringComparison.OrdinalIgnoreCase))
                    {
                        await Task.Run(() => DeployAd102DirectlyFromEmbeddedResource(driveLetter, auto5Sec, universalPcie));
                    }
                    else
                    {
                        string famSourceDir = await Task.Run(() => EnsureFamilyAssetsReady(fam));
                        await Task.Run(() => DeployFamilyFilesToPartition(famSourceDir, fam, driveLetter, auto5Sec, universalPcie));
                    }
                }

                string entryGuid = await Task.Run(() => EnsureUefiEntryRegistered(driveLetter));
                AppendLog("[OK] Entrée UEFI NVRAM enregistrée sous le GUID : " + entryGuid + " (Windows maintenu en N°1 par défaut).");

                AppendLog("==========================================================================");
                AppendLog("SUCCÈS TOTAL : La partition Dual-Boot Universelle UNLOCK_GPU est prête !");
                AppendLog("==========================================================================");

                MessageBox.Show(
                    "La partition Dual-Boot universelle 'UNLOCK_GPU' (" + driveLetter + ":\\) est opérationnelle !\n\n" +
                    "• Famille(s) installée(s) : " + famSummary + "\n" +
                    "• Le démarrage de Windows reste 100 % intact et en position N°1 par défaut.\n" +
                    "• Cliquez sur le Bouton [2] pour redémarrer immédiatement en mode UNLOCK_GPU, ou sur le Bouton [3] pour afficher le choix à chaque démarrage.",
                    "Dual-Boot Universel Opérationnel",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );
            }
            catch (Exception ex)
            {
                AppendLog("[ERREUR CRITIQUE] " + ex.Message);
                MessageBox.Show(
                    "Une erreur est survenue pendant l'opération :\n\n" + ex.Message,
                    "Erreur",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error
                );
            }
            finally
            {
                SetBusy(false);
            }
            await RefreshStatusAsync(false, false);
        }

        private void DeployAd102DirectlyFromEmbeddedResource(string driveLetter, bool auto5Sec, bool universalPcie)
        {
            string root = driveLetter + @":\";
            if (!Directory.Exists(root))
            {
                throw new DirectoryNotFoundException("Le volume " + root + " est inaccessible.");
            }

            HashSet<string> needed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "BOOTX64.EFI",
                "booter_load_ad102.bin",
                "fwimage_ad102.bin",
                "fwsignature_ad102.bin",
                "gsp_boot_ad102.bin",
                "gsp_boot_desc_ad102.bin",
                "scrubber_ad10x.bin",
                "v67-ad102-four-plm.bin"
            };

            AppendLog("  -> Extraction directe depuis le conteneur interne de l'EXE vers " + root + "...");
            Dictionary<string, long> written = ExtractFromEmbeddedPayload(root, needed);

            foreach (string req in needed)
            {
                string checkPath = string.Equals(req, "BOOTX64.EFI", StringComparison.OrdinalIgnoreCase)
                    ? Path.Combine(root, @"EFI\BOOT\BOOTX64.EFI")
                    : Path.Combine(root, req);
                FileInfo fi = new FileInfo(checkPath);
                if (!fi.Exists || fi.Length == 0 || !written.ContainsKey(req) || fi.Length != written[req])
                {
                    throw new IOException("Échec de vérification physique sur " + checkPath);
                }
                AppendLog(string.Format("  -> Extrait et vérifié [AD102] : {0} ({1} octets)", req, fi.Length));
            }

            ConfigurePartitionMarkers(root, auto5Sec, universalPcie);
        }

        private void ConfigurePartitionMarkers(string root, bool auto5Sec, bool universalPcie)
        {
            string bdfPath = Path.Combine(root, "tx-select-bdf.txt");
            if (universalPcie)
            {
                if (File.Exists(bdfPath))
                {
                    File.Delete(bdfPath);
                }
                AppendLog("  -> Mode Scan PCIe Universel actif (détection automatique du bus PCIe sur tout PC).");
            }
            else
            {
                File.WriteAllText(bdfPath, "0000:01:00.0", Encoding.ASCII);
                AppendLog("  -> Ciblage PCIe écrit : tx-select-bdf.txt (0000:01:00.0)");
            }

            string autoMarkerPath = Path.Combine(root, "i_know_what_i_am_doing.txt");
            if (auto5Sec)
            {
                File.WriteAllText(autoMarkerPath, "auto", Encoding.ASCII);
                AppendLog("  -> Mode Auto 5 secondes activé (i_know_what_i_am_doing.txt présent).");
            }
            else
            {
                if (File.Exists(autoMarkerPath))
                {
                    File.Delete(autoMarkerPath);
                }
                AppendLog("  -> Mode Dual-Boot Interactif activé (ENTRÉE/ESPACE = Unlock | ÉCHAP = Windows Normal).");
            }
        }

        private string EnsureFamilyAssetsReady(string family)
        {
            string[] expected = GetExpectedFilesForFamily(family);
            string unlockerDir = EnsureStandaloneToolsAvailable();
            string usbrootDir = Path.Combine(unlockerDir, "usbroot");

            if (Directory.Exists(usbrootDir) && File.Exists(Path.Combine(usbrootDir, "BOOTX64.EFI")))
            {
                bool allPresent = true;
                foreach (string f in expected)
                {
                    if (!File.Exists(Path.Combine(usbrootDir, f)))
                    {
                        allPresent = false;
                        break;
                    }
                }
                if (allPresent) return usbrootDir;
            }

            string makeScript = Path.Combine(unlockerDir, "make-usbroot.ps1");
            if (!File.Exists(makeScript))
            {
                throw new FileNotFoundException("Script make-usbroot.ps1 introuvable dans " + unlockerDir);
            }

            AppendLog("Génération locale des binaires de la famille '" + family + "' via make-usbroot.ps1...");
            int exitCode;
            string cmdArgs = string.Format("-File \"{0}\" -Family {1} -SkipGspCheck", makeScript, family);
            string outGen = RunExternalProcessInDir("powershell.exe", cmdArgs, unlockerDir, 600000, out exitCode);
            if (exitCode != 0)
            {
                throw new InvalidOperationException("Échec de génération des firmwares pour la famille " + family + " :\n" + outGen);
            }

            return usbrootDir;
        }

        private static char FindFreeDriveLetter()
        {
            HashSet<char> used = new HashSet<char>();
            foreach (string drive in Directory.GetLogicalDrives())
            {
                if (!string.IsNullOrEmpty(drive))
                {
                    used.Add(char.ToUpperInvariant(drive[0]));
                }
            }
            char[] preferred = new char[] { 'U', 'V', 'W', 'X', 'Y', 'Z', 'R', 'S', 'T', 'Q', 'P' };
            foreach (char c in preferred)
            {
                if (!used.Contains(c)) return c;
            }
            throw new InvalidOperationException("Aucune lettre de lecteur libre disponible entre P: et Z:.");
        }

        private string EnsureDualBootPartitionExistsWmi(string sourceDriveLetter)
        {
            char srcChar = char.ToUpperInvariant(sourceDriveLetter.Trim()[0]);
            ManagementScope scope = new ManagementScope(StorageWmiScope);
            scope.Connect();

            using (ManagementObjectSearcher volSearch = new ManagementObjectSearcher(
                scope,
                new ObjectQuery("SELECT DriveLetter, FileSystemLabel, FileSystem FROM MSFT_Volume")))
            {
                foreach (ManagementObject vol in volSearch.Get())
                {
                    string lbl = Convert.ToString(vol["FileSystemLabel"] ?? "");
                    string fs = Convert.ToString(vol["FileSystem"] ?? "");
                    if (string.Equals(lbl, PartitionLabel, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(fs, "FAT32", StringComparison.OrdinalIgnoreCase))
                    {
                        object dlObj = vol["DriveLetter"];
                        if (dlObj != null && char.IsLetter(Convert.ToChar(dlObj)))
                        {
                            return char.ToUpperInvariant(Convert.ToChar(dlObj)).ToString();
                        }
                    }
                }
            }

            ManagementObject srcPartition = null;
            int diskNumber = -1;
            ulong currentPartSize = 0;

            using (ManagementObjectSearcher partSearch = new ManagementObjectSearcher(
                scope,
                new ObjectQuery("SELECT * FROM MSFT_Partition")))
            {
                foreach (ManagementObject part in partSearch.Get())
                {
                    object dlObj = part["DriveLetter"];
                    if (dlObj != null && char.ToUpperInvariant(Convert.ToChar(dlObj)) == srcChar)
                    {
                        srcPartition = part;
                        diskNumber = Convert.ToInt32(part["DiskNumber"]);
                        currentPartSize = Convert.ToUInt64(part["Size"]);
                        break;
                    }
                }
            }

            if (srcPartition == null)
            {
                throw new InvalidOperationException("La partition du lecteur " + srcChar + ": est introuvable via WMI Storage.");
            }

            ManagementBaseObject supIn = srcPartition.GetMethodParameters("GetSupportedSize");
            ManagementBaseObject supOut = srcPartition.InvokeMethod("GetSupportedSize", supIn, null);
            uint supRet = Convert.ToUInt32(supOut["ReturnValue"]);
            if (supRet != 0)
            {
                throw new InvalidOperationException("GetSupportedSize a retourné le code d'erreur WMI : " + supRet);
            }

            ulong sizeMin = Convert.ToUInt64(supOut["SizeMin"]);
            ulong targetNewSize = currentPartSize - PartitionTargetBytes;
            if (targetNewSize < sizeMin + (1024UL * 1024UL * 1024UL))
            {
                throw new InvalidOperationException("Espace réductible insuffisant en fin de volume sur " + srcChar + ":.");
            }

            char newLetter = FindFreeDriveLetter();

            AppendLog(string.Format("Réduction non-destructive de 512 Mo en fin du lecteur {0}: (Disque {1})...", srcChar, diskNumber));
            ManagementBaseObject resizeIn = srcPartition.GetMethodParameters("Resize");
            resizeIn["Size"] = targetNewSize;
            ManagementBaseObject resizeOut = srcPartition.InvokeMethod("Resize", resizeIn, null);
            uint resizeRet = Convert.ToUInt32(resizeOut["ReturnValue"]);
            if (resizeRet != 0)
            {
                throw new InvalidOperationException("La réduction de la partition " + srcChar + ": a retourné le code WMI : " + resizeRet);
            }

            ManagementObject targetDisk = null;
            using (ManagementObjectSearcher diskSearch = new ManagementObjectSearcher(
                scope,
                new ObjectQuery("SELECT * FROM MSFT_Disk WHERE Number = " + diskNumber)))
            {
                foreach (ManagementObject d in diskSearch.Get())
                {
                    targetDisk = d;
                    break;
                }
            }

            if (targetDisk == null)
            {
                throw new InvalidOperationException("Objet MSFT_Disk introuvable pour le Disque " + diskNumber);
            }

            AppendLog(string.Format("Création de la partition de 512 Mo (Lecteur {0}:) sur le Disque {1}...", newLetter, diskNumber));
            ManagementBaseObject createIn = targetDisk.GetMethodParameters("CreatePartition");
            createIn["Size"] = PartitionTargetBytes;
            createIn["DriveLetter"] = newLetter;
            createIn["AssignDriveLetter"] = false;
            createIn["UseMaximumSize"] = false;

            ManagementBaseObject createOut = targetDisk.InvokeMethod("CreatePartition", createIn, null);
            uint createRet = Convert.ToUInt32(createOut["ReturnValue"]);
            if (createRet != 0)
            {
                throw new InvalidOperationException("CreatePartition a retourné le code d'erreur WMI : " + createRet);
            }

            AppendLog(string.Format("Formatage de la partition {0}: en FAT32 (Label : {1})...", newLetter, PartitionLabel));
            ManagementObject newVol = null;
            for (int retry = 0; retry < 15; retry++)
            {
                Thread.Sleep(500);
                using (ManagementObjectSearcher vSearch = new ManagementObjectSearcher(
                    scope,
                    new ObjectQuery("SELECT * FROM MSFT_Volume")))
                {
                    foreach (ManagementObject v in vSearch.Get())
                    {
                        object dlObj = v["DriveLetter"];
                        if (dlObj != null && char.ToUpperInvariant(Convert.ToChar(dlObj)) == newLetter)
                        {
                            newVol = v;
                            break;
                        }
                    }
                }
                if (newVol != null) break;
            }

            if (newVol == null)
            {
                throw new InvalidOperationException("Le nouveau volume " + newLetter + ": n'est pas encore visible par WMI.");
            }

            ManagementBaseObject fmtIn = newVol.GetMethodParameters("Format");
            fmtIn["FileSystem"] = "FAT32";
            fmtIn["FileSystemLabel"] = PartitionLabel;
            fmtIn["Full"] = false;
            fmtIn["Force"] = true;

            ManagementBaseObject fmtOut = newVol.InvokeMethod("Format", fmtIn, null);
            uint fmtRet = Convert.ToUInt32(fmtOut["ReturnValue"]);
            if (fmtRet != 0)
            {
                throw new InvalidOperationException("Le formatage FAT32 a retourné le code WMI : " + fmtRet);
            }

            Thread.Sleep(800);
            return newLetter.ToString();
        }

        private void DeployFamilyFilesToPartition(string sourceDir, string family, string driveLetter, bool auto5Sec, bool universalPcie)
        {
            string root = driveLetter + @":\";
            if (!Directory.Exists(root))
            {
                throw new DirectoryNotFoundException("Le volume " + root + " est inaccessible.");
            }

            string efiBootDir = Path.Combine(root, @"EFI\BOOT");
            Directory.CreateDirectory(efiBootDir);

            string srcBootEfi = Path.Combine(sourceDir, @"EFI\BOOT\BOOTX64.EFI");
            if (!File.Exists(srcBootEfi))
            {
                srcBootEfi = Path.Combine(sourceDir, "BOOTX64.EFI");
            }

            string dstBootEfi = Path.Combine(efiBootDir, "BOOTX64.EFI");
            File.Copy(srcBootEfi, dstBootEfi, true);

            FileInfo fiEfi = new FileInfo(dstBootEfi);
            if (!fiEfi.Exists || fiEfi.Length != new FileInfo(srcBootEfi).Length || fiEfi.Length == 0)
            {
                throw new IOException("Échec de vérification de taille sur EFI\\BOOT\\BOOTX64.EFI.");
            }
            AppendLog(string.Format("  -> Copié et vérifié : EFI\\BOOT\\BOOTX64.EFI ({0} octets)", fiEfi.Length));

            string[] expectedFiles = GetExpectedFilesForFamily(family);
            foreach (string fileName in expectedFiles)
            {
                string srcFile = Path.Combine(sourceDir, fileName);
                string dstFile = Path.Combine(root, fileName);
                File.Copy(srcFile, dstFile, true);

                long expectedLen = new FileInfo(srcFile).Length;
                FileInfo dstInfo = new FileInfo(dstFile);
                if (!dstInfo.Exists || dstInfo.Length != expectedLen || dstInfo.Length == 0)
                {
                    throw new IOException("Échec de vérification physique sur le fichier " + fileName);
                }
                AppendLog(string.Format("  -> Copié et vérifié [{0}] : {1} ({2} octets)", family.ToUpperInvariant(), fileName, dstInfo.Length));
            }

            ConfigurePartitionMarkers(root, auto5Sec, universalPcie);
        }

        private void ApplyWindowsContinuousHoldSettings(bool includeTuringGspMode)
        {
            using (RegistryKey kPower = Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Power"))
            {
                if (kPower != null)
                {
                    kPower.SetValue("HiberbootEnabled", 0, RegistryValueKind.DWord);
                }
            }

            List<DetectedNvidiaGpu> gpus = DiscoverNvidiaGpus();
            foreach (DetectedNvidiaGpu gpu in gpus)
            {
                string subPath = DisplayClassKeyPath + @"\" + gpu.RegistrySlot;
                using (RegistryKey kNv = Registry.LocalMachine.OpenSubKey(subPath, true))
                {
                    if (kNv != null)
                    {
                        kNv.SetValue("DisableDynamicPstate", 1, RegistryValueKind.DWord);
                        if (includeTuringGspMode || string.Equals(gpu.Family, "tu10x", StringComparison.OrdinalIgnoreCase))
                        {
                            kNv.SetValue("EnableGpuFirmware", 1, RegistryValueKind.DWord);
                        }
                    }
                }
            }
        }

        private string EnsureUefiEntryRegistered(string driveLetter)
        {
            string existingGuid;
            bool isFirst;
            FindUefiBootEntry(out existingGuid, out isFirst);

            int exitCode;
            string guid = existingGuid;

            if (string.IsNullOrEmpty(guid))
            {
                string copyOut = RunExternalProcess(
                    "bcdedit.exe",
                    string.Format("/copy {{bootmgr}} /d \"{0}\"", UefiEntryDescription),
                    10000,
                    out exitCode
                );
                if (exitCode != 0)
                {
                    throw new InvalidOperationException("Impossible de créer l'entrée UEFI via bcdedit /copy : " + copyOut);
                }

                Match m = Regex.Match(copyOut, @"\{[0-9a-fA-F\-]{36}\}");
                if (!m.Success)
                {
                    throw new InvalidOperationException("GUID non trouvé dans la sortie de bcdedit : " + copyOut);
                }
                guid = m.Value;
            }

            string setDevOut = RunExternalProcess(
                "bcdedit.exe",
                string.Format("/set {0} device partition={1}:", guid, driveLetter),
                10000,
                out exitCode
            );
            if (exitCode != 0)
            {
                throw new InvalidOperationException("Erreur bcdedit /set device : " + setDevOut);
            }

            string setPathOut = RunExternalProcess(
                "bcdedit.exe",
                string.Format("/set {0} path \\EFI\\BOOT\\BOOTX64.EFI", guid),
                10000,
                out exitCode
            );
            if (exitCode != 0)
            {
                throw new InvalidOperationException("Erreur bcdedit /set path : " + setPathOut);
            }

            RunExternalProcess("bcdedit.exe", "/set {fwbootmgr} displayorder {bootmgr} /addfirst", 10000, out exitCode);
            RunExternalProcess("bcdedit.exe", string.Format("/set {{fwbootmgr}} displayorder {0} /addlast", guid), 10000, out exitCode);

            return guid;
        }

        private async Task RebootToUnlockGpuOnceAsync()
        {
            if (_isBusy) return;
            await RefreshStatusAsync(false, false);

            if (_lastStatus == null || !_lastStatus.PartitionExists || !_lastStatus.FilesComplete)
            {
                MessageBox.Show(
                    "La partition Dual-Boot 'UNLOCK_GPU' n'est pas encore créée ou ses fichiers sont incomplets.\n\nCliquez d'abord sur le Bouton [1] pour la préparer.",
                    "Partition UNLOCK_GPU Requise",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning
                );
                return;
            }

            string computeApps = CheckRunningCudaApps();
            if (!string.IsNullOrEmpty(computeApps))
            {
                DialogResult warnCuda = MessageBox.Show(
                    "ATTENTION CRITIQUE : Un processus CUDA est actuellement actif sur votre GPU NVIDIA :\n\n" +
                    computeApps + "\n\n" +
                    "Si un entraînement IA ou un calcul est en cours, NE REDÉMARREZ PAS !\n" +
                    "Voulez-vous vraiment continuer malgré ce processus actif ?",
                    "Processus GPU Actif Détecté",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Stop
                );
                if (warnCuda != DialogResult.Yes)
                {
                    return;
                }
            }

            DialogResult confirm = MessageBox.Show(
                "Voulez-vous redémarrer maintenant sur la partition Dual-Boot 'UNLOCK_GPU' ?\n\n" +
                "• Le PC va programmer 'BootNext' (uniquement pour ce redémarrage) sur la partition UNLOCK_GPU.\n" +
                "• Votre carte NVIDIA sera déverrouillée en UEFI puis Windows démarrera automatiquement.\n" +
                "• Le démarrage normal de Windows reste le choix par défaut N°1 pour tous les démarrages suivants.",
                "Redémarrage Dual-Boot UNLOCK_GPU (One-Shot)",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question
            );

            if (confirm != DialogResult.Yes)
            {
                return;
            }

            SetBusy(true);
            try
            {
                string guid = _lastStatus.UefiEntryGuid;
                if (string.IsNullOrEmpty(guid))
                {
                    guid = await Task.Run(() => EnsureUefiEntryRegistered(_lastStatus.PartitionDriveLetter));
                }

                await Task.Run(() => ApplyWindowsContinuousHoldSettings(false));

                int exitCode;
                string seqOut = RunExternalProcess(
                    "bcdedit.exe",
                    string.Format("/set {{fwbootmgr}} bootsequence {0}", guid),
                    10000,
                    out exitCode
                );

                if (exitCode != 0)
                {
                    throw new InvalidOperationException("Impossible de programmer la séquence BootNext UEFI : " + seqOut);
                }

                AppendLog("[OK] Séquence UEFI BootNext programmée sur " + guid + ". Redémarrage vers UNLOCK_GPU dans 3 secondes...");
                RunExternalProcess("shutdown.exe", "/r /t 3 /c \"Redémarrage Dual-Boot vers UNLOCK_GPU puis lancement de Windows\"", 5000, out exitCode);
                Application.Exit();
            }
            catch (Exception ex)
            {
                AppendLog("[ERREUR] " + ex.Message);
                MessageBox.Show("Erreur lors de la programmation du redémarrage :\n\n" + ex.Message, "Erreur", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async Task SetBootOrderAsync(bool unlockFirst)
        {
            if (_isBusy) return;
            await RefreshStatusAsync(false, false);

            if (unlockFirst && (_lastStatus == null || !_lastStatus.PartitionExists || !_lastStatus.FilesComplete))
            {
                MessageBox.Show(
                    "Veuillez d'abord créer la partition UNLOCK_GPU avec le Bouton [1].",
                    "Partition UNLOCK_GPU Requise",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning
                );
                return;
            }

            SetBusy(true);
            try
            {
                int exitCode;
                if (unlockFirst)
                {
                    string guid = _lastStatus.UefiEntryGuid;
                    if (string.IsNullOrEmpty(guid))
                    {
                        guid = await Task.Run(() => EnsureUefiEntryRegistered(_lastStatus.PartitionDriveLetter));
                    }

                    RunExternalProcess("bcdedit.exe", "/set {fwbootmgr} displayorder {bootmgr} /addfirst", 10000, out exitCode);
                    string outOrder = RunExternalProcess("bcdedit.exe", string.Format("/set {{fwbootmgr}} displayorder {0} /addfirst", guid), 10000, out exitCode);
                    if (exitCode != 0)
                    {
                        throw new InvalidOperationException("Erreur lors de la modification de l'ordre UEFI : " + outOrder);
                    }

                    AppendLog("[OK] Dual-Boot Permanent activé : 1. UNLOCK_GPU -> 2. Windows Boot Manager.");
                    MessageBox.Show(
                        "Le choix Dual-Boot à chaque démarrage est maintenant ACTIF !\n\n" +
                        "À chaque démarrage du PC :\n" +
                        "• Appuyez sur [ENTRÉE] ou [ESPACE] (ou laissez 5s en mode Auto) pour déverrouiller le GPU NVIDIA et lancer Windows.\n" +
                        "• Appuyez sur [ÉCHAP] pour lancer Windows immédiatement sans déverrouillage.",
                        "Dual-Boot Permanent Activé",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information
                    );
                }
                else
                {
                    RunExternalProcess("bcdedit.exe", "/set {fwbootmgr} displayorder {bootmgr} /addfirst", 10000, out exitCode);
                    if (!string.IsNullOrEmpty(_lastStatus.UefiEntryGuid))
                    {
                        RunExternalProcess("bcdedit.exe", string.Format("/set {{fwbootmgr}} displayorder {0} /addlast", _lastStatus.UefiEntryGuid), 10000, out exitCode);
                    }

                    AppendLog("[OK] Windows Boot Manager ({bootmgr}) remis en N°1 par défaut dans la NVRAM UEFI.");
                    MessageBox.Show(
                        "Windows (Windows Boot Manager) est à nouveau en N°1 par défaut.\n\n" +
                        "Le PC démarrera directement sur Windows sans afficher l'écran UNLOCK_GPU (sauf si vous cliquez sur le Bouton [2] ou utilisez le menu Boot du BIOS).",
                        "Windows par Défaut",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information
                    );
                }
            }
            catch (Exception ex)
            {
                AppendLog("[ERREUR] " + ex.Message);
                MessageBox.Show("Erreur :\n\n" + ex.Message, "Erreur", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                SetBusy(false);
            }
            await RefreshStatusAsync(false, false);
        }

        private async Task RemoveDualBootAndMergePartitionAsync()
        {
            if (_isBusy) return;
            await RefreshStatusAsync(false, false);

            DialogResult confirm = MessageBox.Show(
                "Cette opération va :\n\n" +
                "1. Remettre Windows Boot Manager ({bootmgr}) en N°1 et supprimer l'entrée UEFI Dual-Boot.\n" +
                "2. Supprimer la partition FAT32 'UNLOCK_GPU' (512 Mo).\n" +
                "3. Ré-étendre automatiquement la partition adjacente (ex: D: ou C:) pour lui restituer 100 % des 512 Mo.\n\n" +
                "Confirmez-vous la suppression de la partition Dual-Boot UNLOCK_GPU ?",
                "Confirmation — Suppression & Refusion Universelle",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning
            );

            if (confirm != DialogResult.Yes)
            {
                return;
            }

            SetBusy(true);
            try
            {
                AppendLog("Suppression de l'entrée UEFI et refusion WMI de l'espace de 512 Mo...");

                int exitCode;
                RunExternalProcess("bcdedit.exe", "/set {fwbootmgr} displayorder {bootmgr} /addfirst", 10000, out exitCode);

                string guid;
                bool isFirst;
                FindUefiBootEntry(out guid, out isFirst);
                while (!string.IsNullOrEmpty(guid))
                {
                    RunExternalProcess("bcdedit.exe", string.Format("/delete {0} /f", guid), 10000, out exitCode);
                    AppendLog("[OK] Entrée UEFI " + guid + " supprimée de la NVRAM.");
                    FindUefiBootEntry(out guid, out isFirst);
                }

                await Task.Run(() => RemoveUnlockPartitionAndExtendAdjacentWmi());

                AppendLog("[OK] Partition UNLOCK_GPU supprimée et 512 Mo restitués intégralement.");
                MessageBox.Show(
                    "La partition 'UNLOCK_GPU' et son entrée UEFI ont été intégralement supprimées.\n\n" +
                    "Les 512 Mo ont été restitués à votre volume d'origine et Windows est à 100 % dans sa configuration initiale.",
                    "Restauration Terminée",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );
            }
            catch (Exception ex)
            {
                AppendLog("[ERREUR] " + ex.Message);
                MessageBox.Show("Erreur lors de la suppression :\n\n" + ex.Message, "Erreur", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                SetBusy(false);
            }
            await RefreshStatusAsync(false, false);
        }

        private void RemoveUnlockPartitionAndExtendAdjacentWmi()
        {
            ManagementScope scope = new ManagementScope(StorageWmiScope);
            scope.Connect();

            char unlockLetter = '\0';
            using (ManagementObjectSearcher vSearch = new ManagementObjectSearcher(
                scope,
                new ObjectQuery("SELECT DriveLetter, FileSystemLabel, FileSystem FROM MSFT_Volume")))
            {
                foreach (ManagementObject v in vSearch.Get())
                {
                    string lbl = Convert.ToString(v["FileSystemLabel"] ?? "");
                    string fs = Convert.ToString(v["FileSystem"] ?? "");
                    if (string.Equals(lbl, PartitionLabel, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(fs, "FAT32", StringComparison.OrdinalIgnoreCase))
                    {
                        object dlObj = v["DriveLetter"];
                        if (dlObj != null && char.IsLetter(Convert.ToChar(dlObj)))
                        {
                            unlockLetter = char.ToUpperInvariant(Convert.ToChar(dlObj));
                            break;
                        }
                    }
                }
            }

            if (unlockLetter == '\0')
            {
                return;
            }

            ManagementObject unlockPart = null;
            int diskNum = -1;
            ulong uOffset = 0;
            ulong uSize = 0;

            using (ManagementObjectSearcher pSearch = new ManagementObjectSearcher(
                scope,
                new ObjectQuery("SELECT * FROM MSFT_Partition")))
            {
                foreach (ManagementObject p in pSearch.Get())
                {
                    object dlObj = p["DriveLetter"];
                    if (dlObj != null && char.ToUpperInvariant(Convert.ToChar(dlObj)) == unlockLetter)
                    {
                        unlockPart = p;
                        diskNum = Convert.ToInt32(p["DiskNumber"]);
                        uOffset = Convert.ToUInt64(p["Offset"]);
                        uSize = Convert.ToUInt64(p["Size"]);
                        break;
                    }
                }
            }

            if (unlockPart == null)
            {
                return;
            }

            if (uSize > 2UL * 1024UL * 1024UL * 1024UL)
            {
                throw new InvalidOperationException("Sécurité : la partition UNLOCK_GPU dépasse 2 Go, suppression refusée.");
            }

            ManagementObject prevPart = null;
            ulong bestOffset = 0;
            using (ManagementObjectSearcher pSearch = new ManagementObjectSearcher(
                scope,
                new ObjectQuery("SELECT * FROM MSFT_Partition WHERE DiskNumber = " + diskNum)))
            {
                foreach (ManagementObject p in pSearch.Get())
                {
                    ulong off = Convert.ToUInt64(p["Offset"]);
                    object dlObj = p["DriveLetter"];
                    if (off < uOffset && dlObj != null && char.IsLetter(Convert.ToChar(dlObj)))
                    {
                        if (prevPart == null || off > bestOffset)
                        {
                            prevPart = p;
                            bestOffset = off;
                        }
                    }
                }
            }

            ManagementBaseObject delOut = unlockPart.InvokeMethod("DeleteObject", null, null);
            uint delRet = Convert.ToUInt32(delOut["ReturnValue"]);
            if (delRet != 0)
            {
                throw new InvalidOperationException("DeleteObject a retourné le code d'erreur WMI : " + delRet);
            }

            Thread.Sleep(600);

            if (prevPart != null)
            {
                ManagementBaseObject supIn = prevPart.GetMethodParameters("GetSupportedSize");
                ManagementBaseObject supOut = prevPart.InvokeMethod("GetSupportedSize", supIn, null);
                if (Convert.ToUInt32(supOut["ReturnValue"]) == 0)
                {
                    ulong sizeMax = Convert.ToUInt64(supOut["SizeMax"]);
                    ManagementBaseObject resIn = prevPart.GetMethodParameters("Resize");
                    resIn["Size"] = sizeMax;
                    prevPart.InvokeMethod("Resize", resIn, null);
                }
            }
        }

        private async Task RestoreWindowsPowerStockAsync()
        {
            if (_isBusy) return;
            SetBusy(true);
            try
            {
                await Task.Run(() =>
                {
                    using (RegistryKey kPower = Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Power"))
                    {
                        if (kPower != null)
                        {
                            kPower.SetValue("HiberbootEnabled", 1, RegistryValueKind.DWord);
                        }
                    }
                    List<DetectedNvidiaGpu> gpus = DiscoverNvidiaGpus();
                    foreach (DetectedNvidiaGpu gpu in gpus)
                    {
                        using (RegistryKey kNv = Registry.LocalMachine.OpenSubKey(DisplayClassKeyPath + @"\" + gpu.RegistrySlot, true))
                        {
                            if (kNv != null && kNv.GetValue("DisableDynamicPstate") != null)
                            {
                                kNv.DeleteValue("DisableDynamicPstate", false);
                            }
                        }
                    }
                });

                AppendLog("[OK] Paramètres d'alimentation Windows et NVIDIA remis à 100 % d'origine (HiberbootEnabled=1, Optimus standard).");
                MessageBox.Show(
                    "Les paramètres d'alimentation Windows et NVIDIA ont été remis à 100 % d'origine sur toutes les cartes détectées.",
                    "Paramètres Restaurés",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );
            }
            catch (Exception ex)
            {
                AppendLog("[ERREUR] " + ex.Message);
            }
            finally
            {
                SetBusy(false);
            }
            await RefreshStatusAsync(false, false);
        }

        private void ReadUefiLoaderLog()
        {
            if (_lastStatus == null || !_lastStatus.PartitionExists || string.IsNullOrEmpty(_lastStatus.PartitionDriveLetter))
            {
                MessageBox.Show("La partition UNLOCK_GPU n'est pas montée.", "Information", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string logPath = _lastStatus.PartitionDriveLetter + @":\loader.log";
            if (!File.Exists(logPath))
            {
                AppendLog("[INFO] Aucun fichier " + logPath + " trouvé (la partition n'a pas encore été démarrée en UEFI).");
                MessageBox.Show(
                    "Aucun fichier 'loader.log' n'est encore présent sur " + _lastStatus.PartitionDriveLetter + ":\\.\n\n" +
                    "Ce fichier est généré automatiquement par BOOTX64.EFI lors du premier démarrage sur la partition UNLOCK_GPU.",
                    "Journal UEFI non généré",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );
                return;
            }

            try
            {
                string content = File.ReadAllText(logPath, Encoding.UTF8);
                AppendLog("=== CONTENU DE " + logPath + " ===\r\n" + content);
            }
            catch (Exception ex)
            {
                AppendLog("[ERREUR] Lecture de loader.log impossible : " + ex.Message);
            }
        }

        private string CheckRunningCudaApps()
        {
            try
            {
                int exitCode;
                string outSmi = RunExternalProcess(
                    "nvidia-smi.exe",
                    "--query-compute-apps=pid,process_name,used_memory --format=csv,noheader",
                    5000,
                    out exitCode
                );
                if (exitCode == 0 && !string.IsNullOrWhiteSpace(outSmi))
                {
                    return outSmi.Trim();
                }
            }
            catch
            {
            }
            return "";
        }

        private static string RunExternalProcess(string fileName, string arguments, int timeoutMs, out int exitCode)
        {
            return RunExternalProcessInDir(fileName, arguments, AppDomain.CurrentDomain.BaseDirectory, timeoutMs, out exitCode);
        }

        private static string RunExternalProcessInDir(string fileName, string arguments, string workingDir, int timeoutMs, out int exitCode)
        {
            exitCode = -1;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    WorkingDirectory = workingDir,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                using (Process proc = new Process { StartInfo = psi })
                {
                    StringBuilder sb = new StringBuilder();
                    proc.OutputDataReceived += (s, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
                    proc.ErrorDataReceived += (s, e) => { if (e.Data != null) sb.AppendLine(e.Data); };

                    proc.Start();
                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();

                    if (!proc.WaitForExit(timeoutMs))
                    {
                        try { proc.Kill(); } catch { }
                        exitCode = -2;
                        return sb.ToString() + "\n[TIMEOUT]";
                    }

                    proc.WaitForExit();
                    exitCode = proc.ExitCode;
                    return sb.ToString();
                }
            }
            catch (Exception ex)
            {
                return "Exception d'exécution (" + fileName + ") : " + ex.Message;
            }
        }
    }
}
