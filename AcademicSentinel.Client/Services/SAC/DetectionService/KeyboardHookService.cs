using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace AcademicSentinel.Client.Services.SAC.DetectionService
{
    /// <summary>
    /// Low-level keyboard hook for CSAD screenshot detection.
    /// Captures the PrintScreen key (VK_SNAPSHOT = 0x2C) and the
    /// Snipping Tool combo Win+Shift+S, both of which DO NOT necessarily
    /// modify the clipboard (especially Win+Shift+S, which goes to a
    /// modal screen-clip overlay first), so they can't be caught by
    /// the clipboard-sequence-number poll alone.
    /// </summary>
    internal sealed class KeyboardHookService : IDisposable
    {
        public event Action ScreenshotKeyDetected;
        public event Action SnippingToolComboDetected;

        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;

        private const int VK_SNAPSHOT = 0x2C;
        private const int VK_LWIN = 0x5B;
        private const int VK_RWIN = 0x5C;
        private const int VK_LSHIFT = 0xA0;
        private const int VK_RSHIFT = 0xA1;
        private const int VK_S = 0x53;

        private LowLevelKeyboardProc _proc;
        private IntPtr _hookId = IntPtr.Zero;
        private bool _isDisposed;

        public void Install()
        {
            if (_hookId != IntPtr.Zero || _isDisposed)
                return;

            _proc = HookCallback;
            _hookId = SetHook(_proc);
        }

        public void Uninstall()
        {
            if (_hookId != IntPtr.Zero)
            {
                try { UnhookWindowsHookEx(_hookId); } catch { }
                _hookId = IntPtr.Zero;
            }
        }

        public void Dispose()
        {
            if (_isDisposed)
                return;

            _isDisposed = true;
            Uninstall();
            ScreenshotKeyDetected = null;
            SnippingToolComboDetected = null;
        }

        private static IntPtr SetHook(LowLevelKeyboardProc proc)
        {
            using var curProcess = Process.GetCurrentProcess();
            using var curModule = curProcess.MainModule;
            return SetWindowsHookEx(
                WH_KEYBOARD_LL, proc,
                GetModuleHandle(curModule?.ModuleName), 0);
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int wm = wParam.ToInt32();
                if (wm == WM_KEYDOWN || wm == WM_SYSKEYDOWN)
                {
                    int vkCode = Marshal.ReadInt32(lParam);

                    // Plain PrintScreen key → fire screenshot event.
                    if (vkCode == VK_SNAPSHOT)
                    {
                        InvokeOnDispatcherSafe(ScreenshotKeyDetected);
                    }

                    // Win+Shift+S — Snipping Tool / ScreenSketch overlay.
                    // The hook only sees one keydown at a time, so check
                    // modifier state synchronously when 'S' is pressed.
                    if (vkCode == VK_S
                        && IsKeyDownSync(VK_LWIN, VK_RWIN)
                        && IsKeyDownSync(VK_LSHIFT, VK_RSHIFT))
                    {
                        InvokeOnDispatcherSafe(SnippingToolComboDetected);
                    }
                }
            }

            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        private static void InvokeOnDispatcherSafe(Action handler)
        {
            if (handler == null) return;
            try
            {
                // Marshal back to the WPF UI thread — listeners typically
                // need to touch the dispatcher (DetectionReports collection).
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher != null && !dispatcher.CheckAccess())
                {
                    dispatcher.BeginInvoke(DispatcherPriority.Normal, handler);
                }
                else
                {
                    handler();
                }
            }
            catch
            {
            }
        }

        private static bool IsKeyDownSync(params int[] vKeys)
        {
            foreach (var vk in vKeys)
            {
                if ((GetAsyncKeyState(vk) & 0x8000) != 0)
                    return true;
            }
            return false;
        }

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);
    }
}
