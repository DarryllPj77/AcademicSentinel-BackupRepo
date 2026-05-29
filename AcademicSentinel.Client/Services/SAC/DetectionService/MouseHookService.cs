using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace AcademicSentinel.Client.Services.SAC.DetectionService
{
    /// <summary>
    /// Low-level mouse hook for context-menu / right-click detection.
    ///
    /// Windows exposes WM_CLIPBOARDUPDATE for *write* events but has no
    /// equivalent global signal for "a paste happened" — the
    /// destination app pulls clipboard data via GetClipboardData,
    /// which raises no global notification an external monitor can
    /// observe. The only way to flag a paste vector that arrived
    /// through a context menu (right-click → Paste) in a
    /// non-cooperating third-party app is to listen for the
    /// right-button release that triggers the menu and log it as a
    /// "potential paste" event. We never see what the user clicks
    /// inside the menu — that rendering belongs to the destination
    /// app — so this is necessarily a heuristic, identified
    /// downstream as RIGHT_CLICK_CONTEXT rather than a confirmed
    /// paste.
    ///
    /// STRICTLY PASSIVE: the callback always returns the result of
    /// CallNextHookEx. A non-zero return would suppress the mouse
    /// click system-wide, which would break the exam app's own
    /// right-click behaviour and violate the observe-only contract.
    /// </summary>
    internal sealed class MouseHookService : IDisposable
    {
        /// <summary>
        /// Fires on every WM_RBUTTONUP the OS delivers to the hook.
        /// Raised via InvokeOnDispatcherSafe so the hook callback
        /// itself stays well under LowLevelHooksTimeout (default
        /// 300ms in HKCU\Control Panel\Desktop\LowLevelHooksTimeout).
        /// The subscriber MUST treat this as a notification only and
        /// offload any I/O / logging to a worker thread.
        /// </summary>
        public event Action RightClickContextDetected;

        private const int WH_MOUSE_LL  = 14;

        // We deliberately listen for the BUTTON_UP message rather
        // than DOWN. The context menu is conventionally attached to
        // the button-release; firing on UP means we capture the
        // menu-open intent rather than a click that may have been
        // cancelled by dragging the cursor away before release.
        private const int WM_RBUTTONUP = 0x0205;

        private LowLevelMouseProc _proc;
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
            RightClickContextDetected = null;
        }

        private static IntPtr SetHook(LowLevelMouseProc proc)
        {
            using var curProcess = Process.GetCurrentProcess();
            using var curModule  = curProcess.MainModule;
            return SetWindowsHookEx(
                WH_MOUSE_LL, proc,
                GetModuleHandle(curModule?.ModuleName), 0);
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int wm = wParam.ToInt32();
                if (wm == WM_RBUTTONUP)
                {
                    // BeginInvoke under the hood — the hook returns
                    // immediately; the consumer runs on the dispatcher.
                    InvokeOnDispatcherSafe(RightClickContextDetected);
                }
            }

            // PASSIVE MONITORING CONTRACT — always pass the mouse
            // event down the hook chain. Returning a non-zero IntPtr
            // from a WH_MOUSE_LL callback suppresses the click
            // system-wide, which would break the exam app's own
            // right-click behaviour and silently violate the
            // observe-only design. If you ever need to react to a
            // click, do it inside the InvokeOnDispatcherSafe handler
            // above — never here, and never by returning anything
            // other than the result of CallNextHookEx.
            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        private static void InvokeOnDispatcherSafe(Action handler)
        {
            if (handler == null) return;
            try
            {
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
                // Hook-thread errors must not crash the dispatcher.
            }
        }

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);
    }
}
