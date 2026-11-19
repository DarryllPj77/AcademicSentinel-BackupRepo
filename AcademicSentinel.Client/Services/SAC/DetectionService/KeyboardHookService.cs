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

        /// <summary>
        /// Fires on the rising edge of Ctrl+V (either L/R Ctrl + V).
        /// Raised from the hook thread via InvokeOnDispatcherSafe —
        /// the hook callback itself stays well under
        /// LowLevelHooksTimeout (default 300ms in
        /// HKCU\Control Panel\Desktop\LowLevelHooksTimeout). The
        /// subscriber MUST treat the call as a notification only and
        /// offload any I/O / logging to a worker thread.
        /// </summary>
        public event Action PasteCombinationDetected;

        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN    = 0x0100;
        private const int WM_KEYUP      = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP   = 0x0105;

        private const int VK_SNAPSHOT = 0x2C;
        private const int VK_LWIN = 0x5B;
        private const int VK_RWIN = 0x5C;
        private const int VK_LSHIFT = 0xA0;
        private const int VK_RSHIFT = 0xA1;
        private const int VK_LCONTROL = 0xA2;
        private const int VK_RCONTROL = 0xA3;
        private const int VK_S = 0x53;
        private const int VK_V = 0x56;

        private LowLevelKeyboardProc _proc;
        private IntPtr _hookId = IntPtr.Zero;
        private bool _isDisposed;

        // ----------------------------------------------------------------
        // Hook-queue-synchronized modifier state (Phase 4)
        // ----------------------------------------------------------------
        // The low-level keyboard hook delivers WM_KEYDOWN / WM_SYSKEYDOWN
        // and the matching key-up messages in the exact order the OS
        // sees them.  GetAsyncKeyState polls a separate global table that
        // can drift out of phase with the hook queue under load — when
        // VK_S arrives we may observe modifier states that are either
        // STALE (key already released) or AHEAD (key release not yet
        // queued).  Tracking the modifiers off the hook stream itself
        // eliminates that gap: the modifier flags update on the SAME
        // queue as the trigger key, so the combo check sees a perfectly
        // consistent snapshot.
        //
        // Threading: all writes happen inside HookCallback, which is
        // invoked serially by the OS on the hook thread.  No other
        // thread reads or writes these fields, so plain `bool` is
        // sufficient — no `volatile` / lock needed.
        private bool _isLWinDown;
        private bool _isRWinDown;
        private bool _isLShiftDown;
        private bool _isRShiftDown;
        private bool _isLCtrlDown;
        private bool _isRCtrlDown;

        public void Install()
        {
            if (_hookId != IntPtr.Zero || _isDisposed)
                return;

            // Reset modifier-state flags before the hook is wired up.  If
            // the user happened to be holding a modifier when monitoring
            // started, we'd otherwise miss the eventual key-up (the hook
            // wasn't installed during the keydown) and the flag would
            // stay stuck true indefinitely.  Starting from false means
            // we may briefly fail to detect a combo if the user was
            // already holding Win/Shift before Install — acceptable, the
            // next press cycle restores correct tracking.
            _isLWinDown   = false;
            _isRWinDown   = false;
            _isLShiftDown = false;
            _isRShiftDown = false;
            _isLCtrlDown  = false;
            _isRCtrlDown  = false;

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
            PasteCombinationDetected = null;
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
                int wm     = wParam.ToInt32();
                int vkCode = Marshal.ReadInt32(lParam);

                bool isDown = wm == WM_KEYDOWN || wm == WM_SYSKEYDOWN;
                bool isUp   = wm == WM_KEYUP   || wm == WM_SYSKEYUP;

                // -----------------------------------------------------------
                // Track modifier state DIRECTLY off the hook stream.
                // -----------------------------------------------------------
                // The OS delivers down / up messages in queue order, so the
                // modifier flag is always consistent with whatever trigger
                // key arrives next on the SAME queue.  This is what fixes
                // the GetAsyncKeyState race — there is no separate global
                // table to fall out of sync with.
                if (isDown || isUp)
                {
                    switch (vkCode)
                    {
                        case VK_LWIN:     _isLWinDown   = isDown; break;
                        case VK_RWIN:     _isRWinDown   = isDown; break;
                        case VK_LSHIFT:   _isLShiftDown = isDown; break;
                        case VK_RSHIFT:   _isRShiftDown = isDown; break;
                        case VK_LCONTROL: _isLCtrlDown  = isDown; break;
                        case VK_RCONTROL: _isRCtrlDown  = isDown; break;
                    }
                }

                // -----------------------------------------------------------
                // Trigger detection only on keydown of the action keys.
                // -----------------------------------------------------------
                if (isDown)
                {
                    // Plain PrintScreen key → fire screenshot event.
                    if (vkCode == VK_SNAPSHOT)
                    {
                        InvokeOnDispatcherSafe(ScreenshotKeyDetected);
                    }

                    // Win+Shift+S — Snipping Tool / ScreenSketch overlay.
                    // Combo evaluated against the hook-tracked boolean
                    // fields rather than GetAsyncKeyState, so the
                    // modifier snapshot is queue-consistent with the
                    // VK_S keydown we're acting on.
                    if (vkCode == VK_S
                        && (_isLWinDown   || _isRWinDown)
                        && (_isLShiftDown || _isRShiftDown))
                    {
                        InvokeOnDispatcherSafe(SnippingToolComboDetected);
                    }

                    // Ctrl+V — event-driven paste detection. This
                    // replaces the previous polling logic in
                    // BehavioralMonitoringService that sampled
                    // IsKeyDown(VK_V) on the monitoring tick and
                    // missed every key tap shorter than the tick
                    // interval. Same queue-consistent modifier
                    // tracking pattern as Win+Shift+S above — the
                    // Ctrl state recorded above arrived on the SAME
                    // hook stream as this VK_V keydown, so there is
                    // no GetAsyncKeyState race. The hook callback
                    // returns immediately; PasteCombinationDetected
                    // is posted asynchronously via BeginInvoke.
                    if (vkCode == VK_V
                        && (_isLCtrlDown || _isRCtrlDown))
                    {
                        InvokeOnDispatcherSafe(PasteCombinationDetected);
                    }
                }
            }

            // PASSIVE MONITORING CONTRACT — always pass the
            // keystroke down the hook chain. Returning a non-zero
            // IntPtr from a WH_KEYBOARD_LL callback suppresses the
            // key system-wide, which would (a) break the exam app's
            // own paste / typing inside any focused control and
            // (b) silently violate the "observe only, never block"
            // design. If you ever need to react to a key, do it
            // inside the InvokeOnDispatcherSafe handlers above —
            // never here, and never by returning anything other
            // than the result of CallNextHookEx.
            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        private static void InvokeOnDispatcherSafe(Action handler)
        {
            if (handler == null) return;
            // Lock-free, microsecond-fast hand-off to the thread pool.
            // We deliberately do NOT go through Dispatcher.BeginInvoke
            // here: under UI load the dispatcher queue can take long
            // enough to enqueue that the hook callback drifts toward
            // the 300 ms LowLevelHooksTimeout, after which Windows
            // silently uninstalls the hook for the rest of the
            // session with no surfaced error. That regression was the
            // observed "Ctrl+V detected once, then never again"
            // symptom. The thread-pool worker downstream
            // (SacDetectorRuntime.OnPasteCombinationDetected →
            // EmitSyntheticFinding) does its own dispatcher marshal
            // before touching any ObservableCollection, so the final
            // hop into UI land still happens cleanly — just not from
            // inside the hook itself.
            System.Threading.ThreadPool.UnsafeQueueUserWorkItem(_ =>
            {
                try { handler(); }
                catch
                {
                    // Subscribers own their own error reporting; we
                    // just must not propagate up the thread-pool stack.
                }
            }, null);
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
    }
}
