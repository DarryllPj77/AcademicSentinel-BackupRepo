using AcademicSentinel.Client.Services.SAC.Models;
using AcademicSentinel.Client.Services.SAC.Utilities;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using Microsoft.Win32;

namespace AcademicSentinel.Client.Services.SAC.DetectionService
{
    internal sealed class BehavioralMonitoringService
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern uint GetClipboardSequenceNumber();

        [DllImport("user32.dll")]
        private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        [DllImport("kernel32.dll")]
        private static extern uint GetTickCount();

        // P/Invoke for the LMS-anchored focus detection.
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        private const int VK_CONTROL = 0x11;
        private const int VK_C = 0x43;
        private const int VK_V = 0x56;

        private static readonly string[] _blacklistedApps =
        {
            "discord",
            "obs32",
            "taskmgr",
            "processhacker",
            "procmon",
            "procexp",
            "windbg",
            "x64dbg",
            "ollydbg",
            "dnspy",
            "ida",
            "fiddler",
            "cheatengine",
            "teamviewer",
            "anydesk",
            "ultravnc",
            "gotomypc"
        };

        private readonly DetectionSettings _settings;
        private readonly HashSet<string> _blacklistedProcessNames;
        private readonly Dictionary<string, DateTime> _lastReportedAtByEvent = new(StringComparer.OrdinalIgnoreCase);

        private IntPtr _lastForegroundWindow;
        private bool _lastForegroundWasSac;
        private string _lastWindowName = "Unknown";
        private IntPtr _temporarilyExemptWindow;
        private DateTime _lastProcessScanAt = DateTime.MinValue;
        private HashSet<string> _lastReportedProcesses = new(StringComparer.OrdinalIgnoreCase);

        // RTFM_RATE and RTFM_SUSTAINED removed per QA decision — the per-event
        // ALT_TAB / WINDOW_SWITCH detection is sufficient. Keeping the
        // _focusLostAtUtc field as an inert reset target for StartMonitoring's
        // bookkeeping symmetry is unnecessary; both detectors are now gone.

        private bool _copyDown;
        private bool _pasteDown;
        private uint _lastClipboardSequenceNumber;

        private int _lastReportedIdleLevel;
        private bool _isMonitoring;
        private DateTime _monitoringStartedAtUtc;

        // ---- LMS-anchored focus detection state ----
        // Domain extracted from the room's LmsExamUrl, e.g. "feu.instructure.com".
        // Empty when no URL is configured (legacy rooms) — anchoring stays off.
        private string _anchoredLmsDomain = string.Empty;
        // Window handle of the browser window currently treated as the LMS exam.
        // IntPtr.Zero before the student opens the LMS or after it closes.
        private IntPtr _anchoredCanvasWindow = IntPtr.Zero;
        // True once we've fired CANVAS_NOT_FOUND for this monitoring cycle so
        // we don't spam the warning every poll while the student takes their
        // time opening the browser.
        private bool _canvasNotFoundReported;

        public BehavioralMonitoringService(DetectionSettings settings, IEnumerable<string> blacklistedProcessNames)
        {
            _settings = settings ?? new DetectionSettings();
            _blacklistedProcessNames = blacklistedProcessNames?.ToHashSet(StringComparer.OrdinalIgnoreCase)
                ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _lastForegroundWindow = GetForegroundWindow();
            _lastClipboardSequenceNumber = GetClipboardSequenceNumber();
        }

        public void StartMonitoring()
        {
            DisableTaskManager();
            _isMonitoring = true;
            _lastForegroundWindow = GetForegroundWindow();
            _lastWindowName = GetWindowName(_lastForegroundWindow);
            _lastForegroundWasSac = false;
            _temporarilyExemptWindow = IntPtr.Zero;
            _lastReportedIdleLevel = 0;
            _monitoringStartedAtUtc = DateTime.UtcNow;
            _lastClipboardSequenceNumber = GetClipboardSequenceNumber();
            _lastProcessScanAt = DateTime.MinValue;
            _lastReportedProcesses.Clear();
            _lastReportedAtByEvent.Clear();
            _copyDown = false;
            _pasteDown = false;

            // Extract the anchored LMS domain from the configured URL. Done
            // once per monitoring cycle so a network blip doesn't re-parse.
            _anchoredLmsDomain = ExtractDomain(_settings.LmsExamUrl);
            _anchoredCanvasWindow = string.IsNullOrEmpty(_anchoredLmsDomain)
                ? IntPtr.Zero
                : FindLmsWindow(_anchoredLmsDomain);
            _canvasNotFoundReported = false;
        }

        /// <summary>
        /// Extracts the host portion of an LMS exam URL using Uri.TryCreate.
        /// Returns "" if the URL is missing or malformed — anchoring then stays off.
        /// </summary>
        private static string ExtractDomain(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return string.Empty;
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed)) return string.Empty;
            return parsed.Host?.ToLowerInvariant() ?? string.Empty;
        }

        /// <summary>
        /// Walks every top-level visible window and returns the first whose
        /// title contains the LMS domain (case-insensitive). Modern browsers
        /// surface the active tab's hostname or page title — both contain
        /// the domain when the exam page is open.
        /// </summary>
        private static IntPtr FindLmsWindow(string anchoredDomain)
        {
            if (string.IsNullOrEmpty(anchoredDomain)) return IntPtr.Zero;

            IntPtr match = IntPtr.Zero;
            EnumWindows((hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd)) return true;
                if (GetWindowTextLength(hWnd) <= 0) return true;

                var title = GetWindowName(hWnd);
                if (string.IsNullOrWhiteSpace(title)) return true;

                if (title.IndexOf(anchoredDomain, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    match = hWnd;
                    return false; // stop enumerating
                }
                return true;
            }, IntPtr.Zero);

            return match;
        }

        public void StopMonitoring()
        {
            EnableTaskManager();
            _isMonitoring = false;
            _copyDown = false;
            _pasteDown = false;
            _temporarilyExemptWindow = IntPtr.Zero;
            _lastReportedIdleLevel = 0;
            _lastReportedProcesses.Clear();
            _lastReportedAtByEvent.Clear();
        }

        public IReadOnlyList<MonitoringDetectionEvent> Poll(bool isSacWindowActive)
        {
            if (!_isMonitoring)
                return Array.Empty<MonitoringDetectionEvent>();

            var findings = new List<MonitoringDetectionEvent>();

            DetectFocus(isSacWindowActive, findings);
            DetectClipboardAndScreenshot(findings);
            DetectIdle(findings);
            ScanAndHandleBlacklistedProcesses(findings);

            _lastForegroundWasSac = isSacWindowActive;

            return findings;
        }

        private void DetectFocus(bool isSacWindowActive, ICollection<MonitoringDetectionEvent> findings)
        {
            if (!_settings.EnableFocusDetection)
                return;

            // ============================================================
            // Path A — LMS anchored focus detection (per-room URL set).
            // Approved targets: SAC window OR _anchoredCanvasWindow (the
            // browser whose title contains _anchoredLmsDomain). Anything
            // else is a violation.
            // ============================================================
            if (!string.IsNullOrEmpty(_anchoredLmsDomain))
            {
                DetectFocusAnchored(isSacWindowActive, findings);
                return;
            }

            // ============================================================
            // Path B — legacy focus detection (no LMS URL configured).
            // Kept for backwards compatibility with rooms whose settings
            // pre-date the LmsExamUrl column. Server validation now blocks
            // creating new sessions without a URL, so this path will only
            // run on legacy seed data.
            // ============================================================
            var foreground = GetForegroundWindow();
            if (foreground != _lastForegroundWindow)
            {
                IntPtr previousForeground = _lastForegroundWindow;
                bool previousWasSac = _lastForegroundWasSac;
                string previous = _lastWindowName;
                string current = GetWindowName(foreground);

                if (isSacWindowActive && !previousWasSac)
                {
                    _temporarilyExemptWindow = previousForeground;
                }
                else if (!isSacWindowActive && previousWasSac)
                {
                    bool isReturningToExemptWindow = _temporarilyExemptWindow != IntPtr.Zero && foreground == _temporarilyExemptWindow;
                    if (isReturningToExemptWindow)
                    {
                        ClearTemporaryExemptWindow();
                    }
                    else
                    {
                        AddEvent(findings, DetectionConstants.EventWindowSwitch, 2,
                            $"Window switched from '{previous}' to '{current}' while monitoring is active.", 0);
                        ClearTemporaryExemptWindow();
                    }
                }
                else if (!isSacWindowActive)
                {
                    AddEvent(findings, DetectionConstants.EventWindowSwitch, 2,
                        $"Window switched from '{previous}' to '{current}' while monitoring is active.", 0);
                    ClearTemporaryExemptWindow();
                }

                _lastForegroundWindow = foreground;
                _lastWindowName = current;
                _lastForegroundWasSac = isSacWindowActive;
            }
        }

        // LMS-anchored focus detection. Runs every poll while monitoring is
        // active and an LMS exam URL is configured for the room.
        private void DetectFocusAnchored(bool isSacWindowActive, ICollection<MonitoringDetectionEvent> findings)
        {
            // (1) Detect a closed LMS window — fire CANVAS_CLOSED once and
            //     attempt re-anchor on the next poll.
            if (_anchoredCanvasWindow != IntPtr.Zero && !IsWindow(_anchoredCanvasWindow))
            {
                AddEvent(findings, DetectionConstants.EventCanvasClosed, 3,
                    $"LMS exam browser window was closed during the session.",
                    cooldownSeconds: 5);
                _anchoredCanvasWindow = IntPtr.Zero;
            }

            // (2) Try to re-anchor whenever we don't currently have a window.
            //     This handles: never-opened-yet, browser restart, or a new
            //     window opened to the same domain.
            if (_anchoredCanvasWindow == IntPtr.Zero)
            {
                _anchoredCanvasWindow = FindLmsWindow(_anchoredLmsDomain);
                if (_anchoredCanvasWindow != IntPtr.Zero)
                {
                    // Successfully re-anchored — clear the not-found warning
                    // so we'll fire it again only if the LMS disappears later.
                    _canvasNotFoundReported = false;
                }
                else if (!_canvasNotFoundReported)
                {
                    // (3) LMS not yet open — one-time warning. The student
                    //     will resolve it by opening the exam in a browser.
                    AddEvent(findings, DetectionConstants.EventCanvasNotFound, 1,
                        $"LMS exam window not detected. Please open {_anchoredLmsDomain} in your browser.",
                        cooldownSeconds: 30);
                    _canvasNotFoundReported = true;
                }
            }

            // (4) Walk the foreground window. Approved = SAC OR anchored LMS.
            //     If the foreground hasn't changed, nothing to do.
            var foreground = GetForegroundWindow();
            if (foreground == _lastForegroundWindow)
                return;

            string previous = _lastWindowName;
            string current = GetWindowName(foreground);

            // Has focus moved to a window whose title still contains the
            // LMS domain? If so, silently re-anchor — never violation.
            // This handles "student switched browser tab to a different
            // exam page on the same LMS" and "student reopened browser".
            bool foregroundIsLms =
                !string.IsNullOrWhiteSpace(current)
                && current.IndexOf(_anchoredLmsDomain, StringComparison.OrdinalIgnoreCase) >= 0;

            if (foregroundIsLms)
            {
                _anchoredCanvasWindow = foreground; // silent re-anchor
                _canvasNotFoundReported = false;
            }
            else if (!isSacWindowActive && foreground != _anchoredCanvasWindow)
            {
                // Focus moved to a non-SAC, non-LMS window — violation.
                // Tier 2 / 20 pts per spec.
                AddEvent(findings, DetectionConstants.EventWindowSwitch, 2,
                    $"Focus lost from LMS exam ({_anchoredLmsDomain}). Switched to '{current}'.",
                    cooldownSeconds: 0);
            }

            _lastForegroundWindow = foreground;
            _lastWindowName = current;
            _lastForegroundWasSac = isSacWindowActive;
        }

        private void ClearTemporaryExemptWindow()
        {
            _temporarilyExemptWindow = IntPtr.Zero;
        }

        private void DetectClipboardAndScreenshot(ICollection<MonitoringDetectionEvent> findings)
        {
            if (!_settings.EnableClipboardMonitoring)
                return;

            uint currentSequence = GetClipboardSequenceNumber();
            bool clipboardChanged = currentSequence != _lastClipboardSequenceNumber;
            if (clipboardChanged)
            {
                _lastClipboardSequenceNumber = currentSequence;

                if (TryClipboardContainsImage())
                {
                    AddEvent(findings, DetectionConstants.EventScreenshot, 3,
                        "Screenshot or image capture detected in clipboard while monitoring is active.", 1);
                }
                else
                {
                    AddEvent(findings, DetectionConstants.EventClipboardCopy, 2,
                        "Clipboard content changed while monitoring is active.", 1);
                }
            }

            bool ctrlPressed = IsKeyDown(VK_CONTROL);

            bool copyPressed = ctrlPressed && IsKeyDown(VK_C);
            if (copyPressed && !_copyDown)
            {
                AddEvent(findings, DetectionConstants.EventClipboardCopy, 2,
                    "Copy command (Ctrl+C) detected while monitoring is active.", 2);
            }
            _copyDown = copyPressed;

            bool pastePressed = ctrlPressed && IsKeyDown(VK_V);
            if (pastePressed && !_pasteDown)
            {
                AddEvent(findings, DetectionConstants.EventClipboardPaste, 2,
                    "Paste command (Ctrl+V) detected while monitoring is active.", 2);
            }
            _pasteDown = pastePressed;
        }

        private void DetectIdle(ICollection<MonitoringDetectionEvent> findings)
        {
            if (!_settings.EnableIdleDetection)
                return;

            int idleSecondsFromSystem = GetSystemIdleSeconds();
            int monitoringElapsedSeconds = (int)Math.Max(0, (DateTime.UtcNow - _monitoringStartedAtUtc).TotalSeconds);
            int idleSeconds = Math.Min(idleSecondsFromSystem, monitoringElapsedSeconds);
            int warningThreshold = Math.Max(5, _settings.IdleWarningThresholdSeconds);
            int violationThreshold = Math.Max(warningThreshold + 1, _settings.IdleViolationThresholdSeconds);
            int criticalThreshold = Math.Max(violationThreshold + 1, _settings.IdleCriticalThresholdSeconds);

            if (idleSeconds < warningThreshold)
            {
                _lastReportedIdleLevel = 0;
                return;
            }

            if (idleSeconds >= criticalThreshold && _lastReportedIdleLevel < 3)
            {
                _lastReportedIdleLevel = 3;
                AddEvent(findings, DetectionConstants.EventIdle, 3,
                    $"Critical inactivity detected ({idleSeconds}s). Mouse and keyboard appear idle.", 10);
                return;
            }

            if (idleSeconds >= violationThreshold && _lastReportedIdleLevel < 2)
            {
                _lastReportedIdleLevel = 2;
                AddEvent(findings, DetectionConstants.EventIdle, 2,
                    $"Inactivity detected ({idleSeconds}s). Mouse and keyboard appear idle.", 10);
                return;
            }

            if (idleSeconds >= warningThreshold && _lastReportedIdleLevel < 1)
            {
                _lastReportedIdleLevel = 1;
                AddEvent(findings, DetectionConstants.EventIdle, 1,
                    $"Idle warning: no keyboard/mouse input for {idleSeconds}s.", 10);
            }
        }

        private void ScanAndHandleBlacklistedProcesses(ICollection<MonitoringDetectionEvent> findings)
        {
            if (!_settings.EnableProcessDetection)
                return;

            var now = DateTime.UtcNow;
            if ((now - _lastProcessScanAt).TotalSeconds < 5)
                return;

            _lastProcessScanAt = now;

            var running = Process.GetProcesses()
                .Select(p =>
                {
                    try { return p.ProcessName; }
                    catch { return string.Empty; }
                })
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var detected = _blacklistedApps
                .Concat(_blacklistedProcessNames)
                .Where(running.Contains)
                .OrderBy(p => p)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (detected.Count == 0)
            {
                _lastReportedProcesses.Clear();
                return;
            }

            if (_lastReportedProcesses.SetEquals(detected))
                return;

            _lastReportedProcesses = detected;

            AddEvent(findings, DetectionConstants.EventProcessDetected, 3,
                $"Unauthorized process detected: {string.Join(", ", detected.Take(5))}", 5);
        }

        private void DetectBlacklistedProcesses(ICollection<MonitoringDetectionEvent> findings)
        {
            ScanAndHandleBlacklistedProcesses(findings);
        }

        private static void DisableTaskManager()
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Policies\System", writable: true);
                key?.SetValue("DisableTaskMgr", 1, RegistryValueKind.DWord);
            }
            catch
            {
            }
        }

        private static void EnableTaskManager()
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Policies\System", writable: true);
                key?.SetValue("DisableTaskMgr", 0, RegistryValueKind.DWord);
            }
            catch
            {
            }
        }

        private void AddEvent(ICollection<MonitoringDetectionEvent> findings, string eventType, int severity, string description, int cooldownSeconds)
        {
            var now = DateTime.UtcNow;
            if (_lastReportedAtByEvent.TryGetValue(eventType, out var lastReportedAt) && (now - lastReportedAt).TotalSeconds < cooldownSeconds)
                return;

            _lastReportedAtByEvent[eventType] = now;
            findings.Add(new MonitoringDetectionEvent
            {
                EventType = eventType,
                SeverityScore = severity,
                Description = description,
                Timestamp = now
            });
        }

        private static bool IsKeyDown(int vKey)
        {
            return (GetAsyncKeyState(vKey) & 0x8000) != 0;
        }

        private static bool TryClipboardContainsImage()
        {
            try
            {
                return Clipboard.ContainsImage();
            }
            catch
            {
                return false;
            }
        }

        private static string GetWindowName(IntPtr handle)
        {
            if (handle == IntPtr.Zero)
                return "Unknown";

            var title = new StringBuilder(256);
            if (GetWindowText(handle, title, title.Capacity) <= 0)
                return "Unknown";

            string value = title.ToString().Trim();
            return string.IsNullOrWhiteSpace(value) ? "Unknown" : value;
        }

        private static int GetSystemIdleSeconds()
        {
            LASTINPUTINFO lastInputInfo = new LASTINPUTINFO
            {
                cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>()
            };

            if (!GetLastInputInfo(ref lastInputInfo))
                return 0;

            uint elapsed = GetTickCount() - lastInputInfo.dwTime;
            return (int)(elapsed / 1000);
        }
    }
}
