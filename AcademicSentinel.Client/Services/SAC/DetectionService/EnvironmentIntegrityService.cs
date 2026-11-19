using System;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace AcademicSentinel.Client.Services.SAC.DetectionService
{
    internal sealed class EnvironmentIntegrityService
    {
        public async Task<(bool IsVm, bool IsRemote)> PerformFullScanAsync()
        {
            return await Task.Run(() =>
            {
                bool isVm = false;
                bool isRemote = false;

                try
                {
                    // VAC/HAS now uses ONLY guest-side signals — values that
                    // a host machine cannot produce. The previous driver-scan
                    // and process-scan paths were catching native hosts that
                    // simply had VirtualBox/VMware installed (their kernel
                    // drivers stay loaded even with no VM running) or had an
                    // emulator running side-by-side with the SAC. Those are
                    // PBD's job — VAC must answer "am I inside a guest?"
                    //
                    // We require TWO independent guest signals to fire, so a
                    // single noisy WMI string can't trip a false positive.
                    int guestSignals = 0;
                    if (DetectVmFromComputerSystemWmi()) guestSignals++;
                    if (DetectVmFromVideoControllerWmi()) guestSignals++;
                    if (DetectVmFromBiosWmi()) guestSignals++;
                    if (DetectVmFromBaseBoardWmi()) guestSignals++;

                    isVm = guestSignals >= 2;
                }
                catch
                {
                    isVm = false;
                }

                try
                {
                    isRemote = DetectRemoteDesktopSession();
                }
                catch
                {
                    isRemote = false;
                }

                return (isVm, isRemote);
            });
        }

        public int GetConnectedDisplayCount()
        {
            try
            {
                const int SM_CMONITORS = 80;
                var count = GetSystemMetrics(SM_CMONITORS);
                return count > 0 ? count : 1;
            }
            catch
            {
                return 1;
            }
        }

        public bool HasMultipleMonitors() => GetConnectedDisplayCount() > 1;

        private static bool DetectVmFromComputerSystemWmi()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Manufacturer, Model FROM Win32_ComputerSystem");
                foreach (ManagementObject obj in searcher.Get())
                {
                    var manufacturer = Convert.ToString(obj["Manufacturer"]) ?? string.Empty;
                    var model = Convert.ToString(obj["Model"]) ?? string.Empty;
                    var text = $"{manufacturer} {model}";

                    // Guest-only model/manufacturer strings. Catching open
                    // Android emulators on the HOST is PBD's job, not VAC's
                    // — the SAC running on Windows is not "inside" BlueStacks
                    // even when BlueStacks is open.
                    if (ContainsAny(text,
                            "VMware", "VirtualBox", "innotek", "QEMU", "Hyper-V",
                            "Xen", "Parallels", "KVM", "Bochs"))
                        return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private static bool DetectVmFromVideoControllerWmi()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
                foreach (ManagementObject obj in searcher.Get())
                {
                    var name = Convert.ToString(obj["Name"]) ?? string.Empty;
                    // Strict guest-only adapter strings. Emulator names
                    // (BlueStacks/Nox/MEmu) were dropped because their
                    // graphics drivers stay installed on a host even when no
                    // emulator is running — that produced false positives.
                    if (ContainsAny(name,
                            "VMware SVGA", "VirtualBox Graphics",
                            "Parallels Display", "QEMU", "Hyper-V Video"))
                        return true;
                }
            }
            catch
            {
            }

            return false;
        }

        // BIOS strings are written by the hypervisor at guest boot — they
        // cannot appear on a native host machine. Strong, low-false-positive
        // signal that we're inside a VM.
        private static bool DetectVmFromBiosWmi()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Manufacturer, SMBIOSBIOSVersion, SerialNumber, Version FROM Win32_BIOS");
                foreach (ManagementObject obj in searcher.Get())
                {
                    var manufacturer = Convert.ToString(obj["Manufacturer"]) ?? string.Empty;
                    var smbios = Convert.ToString(obj["SMBIOSBIOSVersion"]) ?? string.Empty;
                    var serial = Convert.ToString(obj["SerialNumber"]) ?? string.Empty;
                    var version = Convert.ToString(obj["Version"]) ?? string.Empty;
                    var combined = $"{manufacturer} {smbios} {serial} {version}";

                    // Surface devices ship with "Microsoft Corporation" as the
                    // legitimate BIOS manufacturer, so we deliberately do NOT
                    // key off that string. The keywords below are guest-only.
                    if (ContainsAny(combined,
                            "VMware", "VirtualBox", "VBOX",
                            "QEMU", "Parallels", "Xen",
                            "innotek", "BOCHS"))
                    {
                        return true;
                    }
                }
            }
            catch
            {
            }
            return false;
        }

        // Win32_BaseBoard.Product is the motherboard product string. Real
        // hardware reports the actual board (e.g. "PRIME B550-PLUS"). VM
        // guests report virtual board names.
        private static bool DetectVmFromBaseBoardWmi()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Manufacturer, Product FROM Win32_BaseBoard");
                foreach (ManagementObject obj in searcher.Get())
                {
                    var manufacturer = Convert.ToString(obj["Manufacturer"]) ?? string.Empty;
                    var product = Convert.ToString(obj["Product"]) ?? string.Empty;
                    var combined = $"{manufacturer} {product}";

                    if (ContainsAny(combined,
                            "VMware", "Virtual Machine", "VirtualBox",
                            "440BX Desktop Reference Platform", // VMware Workstation default
                            "Oracle Corporation",                // VirtualBox vendor
                            "Parallels Software", "Xen", "QEMU"))
                    {
                        return true;
                    }
                }
            }
            catch
            {
            }
            return false;
        }

        // Kept available for tests — no longer in the production scan flow
        // because virtual NIC MAC prefixes are too broad and matched hosts
        // with bridged adapters (false positives).
        private static bool DetectVmFromMacPrefixes()
        {
            try
            {
                var vmPrefixes = new[]
                {
                    "005056", // VMware ESXi
                    "000C29", // VMware Workstation
                    "001C14", // VMware
                    "000569", // VMware
                    "080027", // VirtualBox / older BlueStacks
                    "0A0027", // VirtualBox NAT variant
                    "001C42", // Parallels
                    "525400"  // QEMU/KVM
                };

                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    var mac = nic.GetPhysicalAddress()?.ToString() ?? string.Empty;
                    if (mac.Length < 6)
                        continue;

                    if (vmPrefixes.Any(p => mac.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                        return true;
                }
            }
            catch
            {
            }

            return false;
        }

        // Android emulators run as native Windows processes — even when the WMI
        // probe doesn't recognise them as a VM, their host process is visible.
        private static bool DetectAndroidEmulatorByProcess()
        {
            // Process names (no .exe). Match anywhere in the name to be tolerant
            // of suffixes like HD-Player, Bluestacks_bgp, BstkSVC, NoxVMHandle.
            var emulatorProcessSignatures = new[]
            {
                "bluestacks", "bstk", "hd-player", "hd-agent",
                "nox", "noxvmhandle",
                "memu", "memuheadless",
                "ldplayer", "dnplayer",
                "genymotion", "vboxheadless",
                "mumumvm", "mumuplayer",
                "andy", "droid4x"
            };

            try
            {
                foreach (var process in Process.GetProcesses())
                {
                    string name;
                    try { name = process.ProcessName ?? string.Empty; }
                    catch { continue; }

                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    if (emulatorProcessSignatures.Any(sig =>
                            name.IndexOf(sig, StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        return true;
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        // BlueStacks/Nox install a virtualization-layer kernel driver that's
        // visible via Win32_SystemDriver even when their host process is not yet
        // running — useful for catching a paused emulator.
        private static bool DetectAndroidEmulatorByDriver()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Name, DisplayName FROM Win32_SystemDriver");
                foreach (ManagementObject obj in searcher.Get())
                {
                    var name = Convert.ToString(obj["Name"]) ?? string.Empty;
                    var display = Convert.ToString(obj["DisplayName"]) ?? string.Empty;
                    var combined = $"{name} {display}";

                    if (ContainsAny(combined,
                            "BlueStacks", "BstHdDrv", "BstkVMM",
                            "Nox", "NoxVm",
                            "MEmu", "Mvbox",
                            "LDPlayer", "VBoxNetLwf", "VBoxDrv",
                            "Genymotion"))
                    {
                        return true;
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        private static bool DetectRemoteDesktopSession()
        {
            try
            {
                const int SM_REMOTESESSION = 0x1000;
                return GetSystemMetrics(SM_REMOTESESSION) != 0;
            }
            catch
            {
                return false;
            }
        }

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        private static bool ContainsAny(string source, params string[] probes)
        {
            if (string.IsNullOrWhiteSpace(source) || probes == null || probes.Length == 0)
                return false;

            return probes.Any(p => source.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0);
        }
    }
}
