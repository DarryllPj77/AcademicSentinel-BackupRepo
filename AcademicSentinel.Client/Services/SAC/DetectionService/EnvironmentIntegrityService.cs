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
                    isVm = DetectVmFromComputerSystemWmi()
                        || DetectVmFromVideoControllerWmi()
                        || DetectVmFromMacPrefixes()
                        || DetectAndroidEmulatorByProcess()
                        || DetectAndroidEmulatorByDriver();
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

                    if (ContainsAny(text,
                            "VMware", "VirtualBox", "innotek", "QEMU", "Hyper-V",
                            "Xen", "Parallels", "KVM", "Bochs",
                            // Android emulators that surface via Win32_ComputerSystem
                            "BlueStacks", "BST", "Nox", "BigNox", "MEmu", "LDPlayer",
                            "Genymotion", "Andy", "Droid4X"))
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
                    if (ContainsAny(name,
                            "VMware SVGA", "VirtualBox Graphics",
                            "Parallels Display", "QEMU", "Hyper-V Video",
                            // BlueStacks ships its own paravirtualized graphics adapter
                            "BlueStacks", "BstkVMM", "Nox", "MEmu"))
                        return true;
                }
            }
            catch
            {
            }

            return false;
        }

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
