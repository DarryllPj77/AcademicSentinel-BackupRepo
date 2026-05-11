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

        // GetWindowThreadProcessId — used to determine whether the foreground
        // window belongs to a browser process. Lets us fall back to "trust
        // any browser window" when CANVAS_NOT_FOUND fired and we never got
        // a chance to anchor to the LMS by title.
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        private const int VK_CONTROL = 0x11;
        private const int VK_C = 0x43;
        private const int VK_V = 0x56;

        /// <summary>
        /// Curated list of genuinely unauthorised applications detected by PBD.
        /// Browsers and OS utilities live in <see cref="_protectedProcesses"/>
        /// instead — those must never be flagged because they are required
        /// for legitimate exam workflows or already covered by another module.
        /// </summary>
        private static readonly string[] _blacklistedApps =
        {
            // Communication / Remote Access
            "discord", "teamviewer", "anydesk", "ultravnc", "gotomypc",

            // Screen Capture / Streaming
            "obs32", "obs64",

            // Packet Analysis
            "fiddler", "wireshark",

            // Debuggers / Reverse Engineering
            "x64dbg", "ollydbg", "dnspy", "ida", "windbg",

            // Process Monitors
            "processhacker", "procmon", "procexp",

            // Cheating Tools
            "cheatengine",

            // AI Desktop Apps (standalone .exe only — not browser-based,
            // so e.g. claude.ai accessed via Chrome stays out of this list).
            "claude"
        };

        /// <summary>
        /// Permanently exempt from PBD regardless of any per-room custom
        /// blacklist supplied by the instructor. Browsers are needed for the
        /// LMS, OS utilities are covered by other detectors, and core Windows
        /// processes must never be killed/flagged. The
        /// <see cref="ScanAndHandleBlacklistedProcesses"/> filter applies this
        /// guard before checking the running process set.
        /// </summary>
        private static readonly HashSet<string> _protectedProcesses =
            new(StringComparer.OrdinalIgnoreCase)
        {
            // Chromium-based browsers
            "chrome", "brave", "msedge", "opera", "operagx",
            "vivaldi", "arc", "thorium", "chromium",
            "yandexbrowser", "slimjet", "coccoc",

            // Firefox-based browsers
            "firefox", "librewolf", "waterfox", "floorp",
            "palemoon", "basilisk", "seamonkey",

            // Other browsers
            "iexplore", "maxthon",

            // Already covered by KeyboardHookService SNIP_TOOL event
            "snippingtool",

            // Disabled via registry at session start — redundant here
            "taskmgr",

            // Core Windows system processes — must never be blocked
            "explorer", "dwm", "svchost", "csrss",
            "winlogon", "lsass"
        };

        /// <summary>
        /// Process-name set used by <see cref="IsBrowserProcessForeground"/>
        /// to decide whether the current foreground window belongs to a
        /// browser. Mirrors the browser entries of <see cref="_protectedProcesses"/>
        /// — a separate copy is used so the focus detector can answer
        /// "is this a browser?" without leaking that semantic into the
        /// PBD filter. Process names are matched case-insensitively.
        /// </summary>
        private static readonly HashSet<string> _browserProcessNames =
            new(StringComparer.OrdinalIgnoreCase)
        {
            "chrome", "brave", "msedge", "opera", "operagx",
            "vivaldi", "arc", "thorium", "chromium",
            "yandexbrowser", "slimjet", "coccoc",
            "firefox", "librewolf", "waterfox", "floorp",
            "palemoon", "basilisk", "seamonkey",
            "iexplore", "maxthon"
        };

        /// <summary>
        /// Title-substring blacklist used to detect TAB switches inside the
        /// same browser HWND. All browser tabs share one HWND, so HWND
        /// comparison alone can't catch "student stayed in Brave but
        /// switched from Canvas to Facebook". When the foreground window's
        /// title contains any of these keywords, anchored-HWND approval is
        /// overridden and a WINDOW_SWITCH violation fires.
        ///
        /// Keep this list focused on common distraction / non-LMS sites —
        /// adding too many false-positive prone keywords (e.g. "search")
        /// would reject legitimate Canvas pages that happen to mention them.
        /// </summary>
        private static readonly string[] _nonLmsTitleKeywords =
        {
            // Social media
            "facebook", "fb.com",
            "twitter.com", " - x", "x.com",
            "tiktok", "instagram", "reddit",

            // Video / streaming
            "youtube", "twitch", "netflix",

            // Productivity / docs (note: legitimate Canvas pages embed Google
            // Drive viewers but their titles still surface the LMS course)
            "google docs", "google sheets", "google slides", "google drive",
            "dropbox.com", "onedrive",
            "notion.so", "notion - ",

            // Email
            "gmail", "outlook.com", "outlook - ",

            // Messaging / chat
            "messenger", "whatsapp web", "telegram web", "discord",

            // Dev / Q&A
            "github.com", "stackoverflow", "stack overflow",

            // AI assistants
            "chatgpt", "openai", "claude.ai",
            "perplexity", "gemini.google", "copilot.microsoft",

            // Reference
            "wikipedia"
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
        // Most recent foreground HWND that matched the anchored LMS domain.
        // Used purely for CANVAS_CLOSED detection — focus approval is now
        // domain-by-title only (Fix #1), not HWND comparison.
        private IntPtr _anchoredCanvasWindow = IntPtr.Zero;

        // (CANVAS_NOT_FOUND emission removed — see CheckCanvasPresence
        // below. The anchored-window tracking it performed is retained;
        // only the violation/grace-timer fields tied to the penalty were
        // dropped to prevent double-scoring with WINDOW_SWITCH.)

        // Tracks whether the previous foreground evaluation considered the
        // student to be on the LMS. Used to fire CANVAS_RETURNED exactly on
        // the false → true transition (instructor-facing positive log entry).
        private bool _wasPreviouslyOnLms;

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

            // (CANVAS_NOT_FOUND grace-timer reset removed alongside the
            //  penalty emission — see CheckCanvasPresence.)

            // Treat the start of monitoring as "not yet on LMS" so the very
            // first time the student actually focuses the LMS window we
            // emit CANVAS_RETURNED (instructor-visible "they're on Canvas").
            _wasPreviouslyOnLms = false;
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

        /// <summary>
        /// Checks whether the given window handle belongs to a known browser
        /// process. Used by <see cref="DetectFocusAnchored"/> as a fallback
        /// when the LMS was never anchored by title (e.g. CANVAS_NOT_FOUND
        /// fired because the page loaded before SAC could observe its
        /// domain in the title) — in that case, focus on any browser window
        /// whose title doesn't match a known non-LMS keyword is treated as
        /// "probably Canvas, give the student the benefit of the doubt and
        /// anchor the window."
        /// </summary>
        private static bool IsBrowserProcessForeground(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return false;

            try
            {
                GetWindowThreadProcessId(hWnd, out uint pid);
                if (pid == 0) return false;

                using var process = Process.GetProcessById((int)pid);
                var name = process?.ProcessName;
                return !string.IsNullOrWhiteSpace(name)
                       && _browserProcessNames.Contains(name);
            }
            catch
            {
                // Process may have died between EnumWindows and inspection —
                // treat as "not a browser" rather than crashing the poll.
                return false;
            }
        }

        /// <summary>
        /// Returns true if <paramref name="title"/> contains any keyword from
        /// <see cref="_nonLmsTitleKeywords"/>, indicating the foreground window
        /// is on a known non-LMS site (e.g. Facebook tab). This is the only
        /// way to detect tab switches inside a single browser HWND.
        /// </summary>
        private static bool TitleContainsNonLmsKeyword(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return false;
            foreach (var kw in _nonLmsTitleKeywords)
            {
                if (title.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        public void StopMonitoring()
        {
            EnableTaskManager();
            _isMonitoring = false;
            _copyDown = false;
            _pasteDown = false;
            _temporarilyExemptWindow = IntPtr.Zero;
            _lastReportedIdleLevel = 0;
            _wasPreviouslyOnLms = false;
            _lastReportedProcesses.Clear();
            _lastReportedAtByEvent.Clear();
        }

        public IReadOnlyList<MonitoringDetectionEvent> Poll(bool isSacWindowActive)
        {
            if (!_isMonitoring)
                return Array.Empty<MonitoringDetectionEvent>();

            var findings = new List<MonitoringDetectionEvent>();

            DetectFocus(isSacWindowActive, findings);
            CheckCanvasPresence(findings);
            DetectClipboardAndScreenshot(findings);
            DetectIdle(findings);
            ScanAndHandleBlacklistedProcesses(findings);

            _lastForegroundWasSac = isSacWindowActive;

            return findings;
        }

        /// <summary>
        /// Per-poll passive tracker: scans for a top-level window whose title
        /// contains the anchored LMS domain and refreshes
        /// <see cref="_anchoredCanvasWindow"/> when one is found. The
        /// <c>CANVAS_NOT_FOUND</c> violation emission that used to live here
        /// was removed because it double-scored with <c>WINDOW_SWITCH</c>
        /// whenever a stale anchored-window state coincided with a focus
        /// change. Tracking remains intact — WINDOW_SWITCH and CANVAS_CLOSED
        /// detection still rely on this refresh.
        /// </summary>
        private void CheckCanvasPresence(ICollection<MonitoringDetectionEvent> findings)
        {
            if (string.IsNullOrWhiteSpace(_anchoredLmsDomain)) return;

            var found = FindLmsWindow(_anchoredLmsDomain);
            if (found != IntPtr.Zero)
            {
                _anchoredCanvasWindow = found;
            }
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

        /// <summary>
        /// LMS-anchored focus detection with four-way approval logic:
        /// <list type="number">
        ///   <item><b>Title contains LMS domain</b> — strongest evidence,
        ///   anchors the HWND for future use.</item>
        ///   <item><b>Same HWND as previously anchored</b> — trusts the
        ///   browser window identity for within-Canvas navigation where
        ///   page titles drop the domain.</item>
        ///   <item><b>Browser-process fallback</b> — when no anchor exists
        ///   (CANVAS_NOT_FOUND fired), focus on ANY browser window whose
        ///   title isn't on the non-LMS keyword list is tentatively
        ///   accepted and anchored.</item>
        ///   <item><b>Non-LMS keyword override</b> — if the foreground
        ///   title contains a known distraction-site keyword (Facebook,
        ///   Google Docs, etc.), the violation fires REGARDLESS of HWND
        ///   match. This is the only practical way to detect tab switches
        ///   inside a single browser window.</item>
        /// </list>
        /// Fires <c>CANVAS_RETURNED</c> on the false→true transition,
        /// <c>CANVAS_CLOSED</c> when the anchored HWND becomes invalid, and
        /// <c>WINDOW_SWITCH</c> for any other non-SAC, non-LMS focus.
        /// </summary>
        private void DetectFocusAnchored(bool isSacWindowActive, ICollection<MonitoringDetectionEvent> findings)
        {
            var foreground = GetForegroundWindow();
            string currentTitle = GetWindowName(foreground);

            // Skip only if BOTH the HWND and the title are unchanged.
            // Browser tab switches keep the same HWND but mutate the title,
            // so we must re-evaluate even when the foreground window object
            // hasn't changed — that's the only way to catch "still in Brave,
            // but now on facebook.com instead of Canvas".
            if (foreground == _lastForegroundWindow
                && string.Equals(currentTitle, _lastWindowName, StringComparison.Ordinal))
            {
                return;
            }

            // ---- Compute the four signals ----
            bool titleSaysLms = !string.IsNullOrWhiteSpace(_anchoredLmsDomain)
                                && !string.IsNullOrWhiteSpace(currentTitle)
                                && currentTitle.IndexOf(_anchoredLmsDomain,
                                   StringComparison.OrdinalIgnoreCase) >= 0;

            bool sameAnchoredHwnd = _anchoredCanvasWindow != IntPtr.Zero
                                    && foreground == _anchoredCanvasWindow
                                    && IsWindow(foreground);

            // Negative override — catches tab switches WITHIN the same
            // browser window. Always overrides positive HWND/process trust.
            bool titleHasNonLmsKeyword = TitleContainsNonLmsKeyword(currentTitle);

            // Browser-process fallback only kicks in when we have no
            // anchor at all yet — i.e. CANVAS_NOT_FOUND fired and the
            // student is now finally focusing what should be Canvas.
            bool browserFallback = _anchoredCanvasWindow == IntPtr.Zero
                                   && !titleHasNonLmsKeyword
                                   && IsBrowserProcessForeground(foreground);

            bool isOnLms = !titleHasNonLmsKeyword
                           && (titleSaysLms || sameAnchoredHwnd || browserFallback);

            // ---- CANVAS_CLOSED: anchored HWND is gone AND new foreground
            //      isn't on the LMS. 5s cooldown swallows brief reloads.
            if (_anchoredCanvasWindow != IntPtr.Zero
                && !IsWindow(_anchoredCanvasWindow)
                && !isOnLms)
            {
                AddEvent(findings, DetectionConstants.EventCanvasClosed, 4,
                    "LMS exam browser window was closed during the session.",
                    cooldownSeconds: 5);
                _anchoredCanvasWindow = IntPtr.Zero;
            }

            // ---- Re-anchor the HWND on any positive signal so future polls
            //      can rely on sameAnchoredHwnd / CANVAS_CLOSED detection.
            if (titleSaysLms || browserFallback)
            {
                _anchoredCanvasWindow = foreground;
            }

            // ---- CANVAS_RETURNED — student was OFF the LMS, now back.
            //      Informational only (0 pts); IMC renders green RETURN badge.
            if (isOnLms && !_wasPreviouslyOnLms)
            {
                AddEvent(findings, DetectionConstants.EventCanvasReturned, 0,
                    $"Student returned focus to the LMS exam ({_anchoredLmsDomain}).",
                    cooldownSeconds: 2);
            }
            _wasPreviouslyOnLms = isOnLms;

            // ---- Violation: not SAC, not approved as LMS.
            if (!isSacWindowActive && !isOnLms)
            {
                // Provide a more specific description when the trigger was
                // a non-LMS keyword on the same browser HWND — that's a
                // tab switch inside the browser, not a window switch.
                string description = (sameAnchoredHwnd && titleHasNonLmsKeyword)
                    ? $"Focus left the LMS tab in the same browser window. Now viewing '{currentTitle}'."
                    : $"Focus lost from LMS exam ({_anchoredLmsDomain}). Switched to '{currentTitle}'.";

                AddEvent(findings, DetectionConstants.EventWindowSwitch, 1,
                    description, cooldownSeconds: 0);
            }

            _lastForegroundWindow = foreground;
            _lastWindowName = currentTitle;
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

            // Tier 3 — critical (idle > criticalThreshold). Reported as
            // INACTIVITY with input-tier 3 so the decision engine scores 40.
            if (idleSeconds >= criticalThreshold && _lastReportedIdleLevel < 3)
            {
                _lastReportedIdleLevel = 3;
                AddEvent(findings, DetectionConstants.EventInactivity, 3,
                    $"Critical inactivity detected ({idleSeconds}s). Mouse and keyboard appear idle.", 10);
                return;
            }

            // Tier 2 — violation. INACTIVITY with input-tier 2 → 20 pts.
            if (idleSeconds >= violationThreshold && _lastReportedIdleLevel < 2)
            {
                _lastReportedIdleLevel = 2;
                AddEvent(findings, DetectionConstants.EventInactivity, 2,
                    $"Inactivity detected ({idleSeconds}s). Mouse and keyboard appear idle.", 10);
                return;
            }

            // Tier 1 — warning. IDLE with input-tier 1 → 10 pts.
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

            // Protection filter — exclude browsers, OS utilities, and any
            // process that is already covered by another detection module
            // BEFORE checking which ones are currently running. Protected
            // processes cannot be overridden by an instructor's per-room
            // custom blacklist (Fix #3).
            var detected = _blacklistedApps
                .Concat(_blacklistedProcessNames)
                .Where(p => !_protectedProcesses.Contains(p))
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
