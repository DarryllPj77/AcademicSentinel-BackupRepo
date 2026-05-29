using AcademicSentinel.Client.Services.SAC.Models;
using AcademicSentinel.Client.Services.SAC.Utilities;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.Win32;

namespace AcademicSentinel.Client.Services.SAC.DetectionService
{
    internal sealed class BehavioralMonitoringService : IDisposable
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

        // OpenProcess + QueryFullProcessImageNameW form the resilient
        // fallback for process-name lookup. System.Diagnostics.Process
        // denies introspection for packaged / sandboxed apps — most
        // notably the new Microsoft Teams (post-2022), several Store
        // apps, and some UWP shells — so the legacy
        // Process.GetProcessById path returns "Unknown application"
        // and the allowlist match silently misses. Asking the kernel
        // for the image name via PROCESS_QUERY_LIMITED_INFORMATION
        // works for those cases because it's the minimum-rights
        // access introduced precisely for this scenario (Windows 8.1+).
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        // Cached at construction time so we can short-circuit "is the
        // foreground window one of OUR own windows?" cheaply. Using
        // Process.GetCurrentProcess().Id is reliable even when
        // GetProcessById for arbitrary PIDs is flaky (packaged-app
        // restrictions don't apply to inspecting our own process).
        private static readonly uint _selfProcessId = (uint)Process.GetCurrentProcess().Id;

        // SAC's own process name (lowercased, no extension). Used as a
        // belt-and-suspenders check alongside _selfProcessId so quirks
        // where a child/helper window briefly reports a different PID
        // — or where GetWindowThreadProcessId fails — still get
        // recognised as one of our own windows by name.
        private static readonly string _selfProcessName =
            (Process.GetCurrentProcess().ProcessName ?? string.Empty).ToLowerInvariant();

        // OS-shell and packaged-host process names whose foreground
        // appearance is never a genuine student action — they're
        // transient window-manager artefacts that surface for a few
        // milliseconds when SAC toggles Topmost / resizes / restores
        // from the softlock overlay. Treat them as benign so they
        // never produce WINDOW_SWITCH or "Unknown application" entries.
        private static readonly HashSet<string> _systemShellProcessNames =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "explorer",                     // Windows shell (taskbar, file picker, Start)
                "dwm",                          // Desktop Window Manager
                "shellexperiencehost",          // shell components
                "applicationframehost",         // UWP / packaged-app host
                "searchhost",                   // Windows 11 search
                "searchui",                     // Windows 10 search
                "searchapp",                    //  ”      ”
                "startmenuexperiencehost",      // Windows 11 Start menu
                "lockapp",                      // lock-screen host
                "textinputhost",                // touch keyboard / IME host
                "sihost",                       // shell infrastructure host
                "ctfmon",                       // text services framework
                "runtimebroker",                // packaged-app broker
            };

        /// <summary>
        /// Final pre-emit gate for the focus violation path. Returns
        /// true when the resolved foreground belongs to a process the
        /// SAC must never treat as a student-initiated app switch:
        ///   • the SAC itself (PID match or process-name match), or
        ///   • a known Windows shell / packaged-app host process, or
        ///   • a foreground we genuinely cannot identify.
        ///
        /// Skipping the emit here means the event never reaches the
        /// findings list — so it appears nowhere: not in the SAC's
        /// Detection Reports, not in the server's audit table, not
        /// in the IMC Global Log Feed.
        /// </summary>
        private bool ShouldSuppressForegroundViolation(IntPtr foreground, out string resolvedProcName)
        {
            resolvedProcName = string.Empty;

            // Genuinely no foreground window — Windows briefly reports
            // this during desktop locks, switcher transitions, and a
            // few system events. Suppress entirely; we have no truth.
            if (foreground == IntPtr.Zero) return true;

            // PID-based self check. Strongest signal because it works
            // even when the foreground belongs to a packaged child
            // window of the SAC that doesn't share our usual chrome.
            if (IsSelfForeground(foreground)) return true;

            string proc = TryGetForegroundProcessName(foreground);
            resolvedProcName = proc ?? string.Empty;

            // Process name-based cross-check — defensive against any
            // case where GetWindowThreadProcessId returns 0 but we
            // can still resolve the image name through QueryFullProcessImageName.
            if (!string.IsNullOrEmpty(proc)
                && string.Equals(proc, _selfProcessName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Transient OS shell / packaged-app host foregrounds.
            if (!string.IsNullOrEmpty(proc) && _systemShellProcessNames.Contains(proc))
            {
                return true;
            }

            // Unidentifiable foreground — would otherwise render as
            // "Unknown application". Conservative choice: skip rather
            // than blame the student for something we can't name.
            if (string.IsNullOrEmpty(proc)) return true;

            return false;
        }

        /// <summary>
        /// True when the foreground window belongs to this very same
        /// process — i.e., the SAC itself (header bar, compact softlock
        /// overlay, modal dialog, anything we ship). Used as an
        /// override on top of the cached `_latestKnownSacActive` flag
        /// so the WinEvent hook (which fires asynchronously and reads
        /// stale cache) cannot misclassify the SAC's own foreground
        /// transition as a WINDOW_SWITCH violation.
        /// </summary>
        private static bool IsSelfForeground(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return false;
            GetWindowThreadProcessId(hWnd, out uint pid);
            return pid != 0 && pid == _selfProcessId;
        }

        /// <summary>
        /// Returns the foreground window's process name (no extension,
        /// case as Windows reports it) or null when both
        /// System.Diagnostics.Process and the Win32 fallback fail.
        ///
        /// Used by both <see cref="IsAllowedExceptionApp"/> and
        /// <see cref="GetSanitizedWindowLabel"/> so a packaged app
        /// like Microsoft Teams is identified consistently across the
        /// allowlist gate and the violation-description renderer —
        /// previously the two diverged silently (allowlist missed,
        /// description showed "Unknown application").
        /// </summary>
        private static string TryGetForegroundProcessName(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return null;
            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == 0) return null;

            // Fast path: works for the overwhelming majority of
            // desktop apps. Cheap, fully managed.
            try
            {
                using var p = Process.GetProcessById((int)pid);
                if (!string.IsNullOrWhiteSpace(p.ProcessName))
                    return p.ProcessName;
            }
            catch
            {
                // Fall through to the Win32 fallback below.
            }

            // Fallback: ask the kernel directly via
            // PROCESS_QUERY_LIMITED_INFORMATION — the access right
            // explicitly designed to inspect protected / packaged
            // processes that deny the broader rights Process.GetProcessById
            // tries to acquire.
            IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (handle == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(512);
                uint size = (uint)sb.Capacity;
                if (QueryFullProcessImageName(handle, 0, sb, ref size) && sb.Length > 0)
                {
                    try
                    {
                        return Path.GetFileNameWithoutExtension(sb.ToString());
                    }
                    catch
                    {
                        // Defensive — Path methods only throw on
                        // pathological inputs but we don't want one
                        // bad foreground sample to crash the poll loop.
                    }
                }
            }
            finally
            {
                CloseHandle(handle);
            }
            return null;
        }

        // ============================================================
        // EVENT-DRIVEN FOREGROUND TRACKING (Phase 1, Task A)
        // ============================================================
        // SetWinEventHook with EVENT_SYSTEM_FOREGROUND gives us a callback
        // the instant the OS changes the foreground window — there's no
        // polling-interval delay to evade. This complements (does not
        // replace) the existing per-poll re-evaluation: the timer remains
        // the safety net, the hook is the immediate signal.
        //
        // WINEVENT_OUTOFCONTEXT delivers events to the thread that called
        // SetWinEventHook, so that thread MUST have a message pump.
        // StartMonitoring is expected to be invoked from the WPF UI
        // thread (which always has one).
        private delegate void WinEventDelegate(
            IntPtr hWinEventHook,
            uint eventType,
            IntPtr hwnd,
            int idObject,
            int idChild,
            uint dwEventThread,
            uint dwmsEventTime);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWinEventHook(
            uint eventMin,
            uint eventMax,
            IntPtr hmodWinEventProc,
            WinEventDelegate lpfnWinEventProc,
            uint idProcess,
            uint idThread,
            uint dwFlags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        private const uint EVENT_SYSTEM_FOREGROUND  = 0x0003;
        private const uint WINEVENT_OUTOFCONTEXT    = 0x0000;
        private const uint WINEVENT_SKIPOWNPROCESS  = 0x0002;
        // OBJECT_SELF: idObject value indicating the event is about the
        // window itself, not a child control or accessibility element.
        private const int  OBJID_WINDOW             = 0;

        // GetSystemMetrics — used for the multi-monitor check (Phase 3,
        // Task A).  SM_CMONITORS returns the number of display monitors
        // on the desktop.  Cheap (a single user32 call), so safe to call
        // every Poll().  An external display being attached IS a high-
        // severity violation per spec, so emission goes through the
        // normal AddEvent pipeline with a long cooldown.
        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);
        private const int SM_CMONITORS = 80;

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
        // Set permanently by Dispose() to block re-entry into StartMonitoring
        // after the runtime tears the service down.  Phase 5 — unmanaged
        // resources (WinEvent hook, WMI watcher) are released via the
        // existing StopMonitoring() path; Dispose just invokes it once and
        // raises the gate so accidental restart cannot re-install them.
        private bool _isDisposed;
        private DateTime _monitoringStartedAtUtc;

        // ---- LMS-anchored focus detection state ----
        // Domain extracted from the room's LmsExamUrl, e.g. "feu.instructure.com".
        // Empty when no URL is configured (legacy rooms) — anchoring stays off.
        private string _anchoredLmsDomain = string.Empty;
        // Deep-path URL anchor built from the full LmsExamUrl. When non-null
        // it overrides domain-only matching: the foreground browser's
        // address-bar URL must satisfy host + core-path + restricted-segment
        // rules. Closes the Same-Domain Cheating gap where a student opened
        // docs.google.com/document/... while the anchor was a Google Form.
        private UrlAnchorValidator.AnchorSpec _urlAnchor;
        // Signature of the originally authorized tab. Used to enforce the
        // No-Multiple-Tabs rule: even if a second tab loads the exam URL,
        // it must match this signature or the access is treated as a switch.
        private string _anchoredTabSignature;

        // Most recent foreground HWND that matched the anchored LMS domain.
        // Used purely for CANVAS_CLOSED detection — focus approval is now
        // domain-by-title only (Fix #1), not HWND comparison.
        private IntPtr _anchoredCanvasWindow = IntPtr.Zero;

        // (CANVAS_NOT_FOUND emission removed — see CheckCanvasPresence
        // below. The anchored-window tracking it performed is retained;
        // only the violation/grace-timer fields tied to the penalty were
        // dropped to prevent double-scoring with WINDOW_SWITCH.)

        // Tracks whether the previous foreground evaluation considered the
        // student to be on the LMS. Retained for any consumer that needs
        // the raw signal; the CANVAS_RETURNED gate no longer keys off it
        // alone — see _wasPreviouslyOutOfExamFocus below.
        private bool _wasPreviouslyOnLms;

        // Tracks whether the previous evaluation considered the student
        // to be OUT OF THE EXAM ENTIRELY — i.e., foreground was neither
        // the SAC window nor the LMS. This is the only state from which
        // a "returned to the LMS" transition is semantically meaningful.
        //
        // Without this flag, _wasPreviouslyOnLms=false conflated two
        // very different prior states:
        //   (a) student was on a real off-exam window (violation context)
        //   (b) student was on the SAC itself (legitimate exam context)
        //
        // Treating (b) as a "left the exam" precondition produced
        // spurious CANVAS_RETURNED entries whenever focus oscillated
        // between SAC and LMS — most visibly on every maximize / expand
        // click that briefly handed focus to the SAC before settling
        // back on the LMS browser.
        private bool _wasPreviouslyOutOfExamFocus;

        // ====================================================================
        // EDGE-BASED EMIT GATE for WINDOW_SWITCH / ALLOWED_APP.
        // ====================================================================
        // Symptom this fixes: opening / maximizing / restoring / focusing
        // the SAC softlock UI after a real switch was already logged
        // caused the same external target to be re-logged on every
        // round trip back to it. The detector was level-based —
        // "current foreground is non-SAC non-LMS" — which mistakes a
        // "look at the softlock and return" round trip for a fresh
        // exit from the LMS anchor.
        //
        // Fix: track the LAST EMITTED external-target key and a flag
        // for whether the student has been back on the LMS since that
        // emit. A repeat emission to the same key without an
        // intervening LMS visit is dropped as a non-transition. SAC
        // self-foreground transitions (handled by
        // ShouldSuppressForegroundViolation earlier) never touch
        // either field — opening the softlock contributes nothing.
        //
        // Re-arm semantics (a new emission for the same target is
        // allowed in any of these cases):
        //   • The student goes back to the LMS, then leaves again.
        //   • The student switches to a DIFFERENT external target —
        //     the key changes, so the duplicate check misses.
        //   • A WINDOW_SWITCH after an ALLOWED_APP (or vice versa) —
        //     the key includes the event type as well as the target.
        private string _lastEmittedExternalKey;
        private bool _hasBeenOnLmsSinceLastEmit = true;

        // ---- WinEvent foreground-hook state (Phase 1, Task A) ----
        // Handle returned by SetWinEventHook; IntPtr.Zero when not installed.
        private IntPtr _foregroundHookHandle = IntPtr.Zero;
        // Strong reference to the marshalled delegate.  Windows holds the
        // function pointer for the lifetime of the hook, so if this
        // managed delegate is collected by the GC the next callback
        // crashes the process.  Keep it alive at the instance level.
        private WinEventDelegate _foregroundHookDelegate;
        // Events produced from inside the hook callback are pushed here
        // and drained at the top of the next Poll() so the public
        // contract (Poll returns a snapshot list) is preserved.
        private readonly System.Collections.Concurrent.ConcurrentQueue<MonitoringDetectionEvent> _hookEventQueue
            = new();
        // Latest known "is the SAC window the foreground?" value, refreshed
        // every Poll().  Read by the WinEvent callback because the OS
        // gives us the new HWND but not the caller's perspective on it.
        private volatile bool _latestKnownSacActive;
        // Mutex that serialises DetectFocus / DetectFocusAnchored access
        // between Poll() (timer thread) and the WinEvent callback (the
        // thread that registered the hook).  All shared focus-tracking
        // state mutations live inside this lock.  Phase 3 reuses the
        // same lock for thread-safe AddEvent calls from the WMI
        // background thread — the lock is held only for microseconds
        // (one dictionary lookup + maybe a write), so contention is
        // negligible and we avoid introducing a second AddEvent lock.
        private readonly object _focusDetectionLock = new();

        // ---- WMI process-creation watcher state (Phase 3, Task B) ----
        // Supplements the 5-second polling scan in
        // ScanAndHandleBlacklistedProcesses with an instant kernel-driven
        // notification: WMI fires __InstanceCreationEvent for Win32_Process
        // at WITHIN-interval granularity (we pick 1s).  Watcher events
        // arrive on a WMI worker thread, so AddEvent calls are routed
        // through EmitFromBackgroundThread which acquires
        // _focusDetectionLock and enqueues to _hookEventQueue.
        private ManagementEventWatcher _processCreationWatcher;

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
            // Refuse to start after Dispose so we cannot re-install the
            // unmanaged WinEvent hook / WMI watcher on a torn-down service.
            if (_isDisposed) return;

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
            // Build the deep-path URL anchor from the full teacher URL.
            // Null when the URL is malformed or the platform's path shape
            // isn't recognized — in that case detection falls back to the
            // legacy domain-only check.
            _urlAnchor = UrlAnchorValidator.BuildAnchor(_settings.LmsExamUrl);
            _anchoredTabSignature = null;
            _anchoredCanvasWindow = string.IsNullOrEmpty(_anchoredLmsDomain)
                ? IntPtr.Zero
                : FindLmsWindow(_anchoredLmsDomain);

            // (CANVAS_NOT_FOUND grace-timer reset removed alongside the
            //  penalty emission — see CheckCanvasPresence.)

            // Initial state — no prior evaluation. Neither flag should
            // ever cause CANVAS_RETURNED on the very first poll: the
            // student hasn't "returned" from anything yet.
            _wasPreviouslyOnLms = false;
            _wasPreviouslyOutOfExamFocus = false;

            // Edge-emit gate — fresh session begins with no prior
            // external emission and "has been on LMS" = true so the
            // very first real external switch passes through.
            _lastEmittedExternalKey = null;
            _hasBeenOnLmsSinceLastEmit = true;

            // Drain any stale findings left in the queue from a previous
            // monitoring cycle that wasn't shut down cleanly.
            while (_hookEventQueue.TryDequeue(out _)) { /* drop */ }
            _latestKnownSacActive = false;

            // Install the event-driven detectors LAST so they cannot fire
            // before _isMonitoring / state is fully initialised above.
            InstallForegroundHook();
            InstallProcessCreationWatcher();
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
            // Flip the monitoring flag BEFORE removing the hook so any
            // callback already in flight returns early via the guard at
            // the top of OnForegroundWinEvent.
            _isMonitoring = false;

            // Uninstall the foreground hook deterministically — must run
            // even if a later reset throws so we never leak the OS-level
            // hook handle.
            UninstallForegroundHook();
            UninstallProcessCreationWatcher();

            // Drop any findings that landed between the last Poll and
            // shutdown — they're no longer relevant.
            while (_hookEventQueue.TryDequeue(out _)) { /* drop */ }

            _copyDown = false;
            _pasteDown = false;
            _temporarilyExemptWindow = IntPtr.Zero;
            _lastReportedIdleLevel = 0;
            _wasPreviouslyOnLms = false;
            _anchoredTabSignature = null;
            _lastReportedProcesses.Clear();
            _lastReportedAtByEvent.Clear();

            // Drop UIA COM references held by BrowserUrlReader's per-HWND
            // cache (Phase 2) so the underlying COM proxies for closed
            // browser tabs/windows can be reclaimed by the GC.  Without
            // this, a long-lived process that runs many exam sessions
            // would slowly accumulate AutomationElement wrappers — they
            // don't leak unmanaged memory per se but they do pin COM
            // RCWs and prevent GC collection of large UIA trees.
            BrowserUrlReader.Clear();
        }

        /// <summary>
        /// Releases the unmanaged WinEvent hook and the WMI process-creation
        /// watcher by delegating to <see cref="StopMonitoring"/>.  After
        /// Dispose, <see cref="StartMonitoring"/> is a no-op so the service
        /// cannot resurrect the OS-level resources.  Idempotent.
        /// </summary>
        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            // StopMonitoring is the single owner of the hook + watcher
            // teardown logic (UninstallForegroundHook, UninstallProcess-
            // CreationWatcher, BrowserUrlReader.Clear).  Wrap in try/catch
            // so that even a teardown failure cannot leave a half-disposed
            // service that still holds OS handles.
            try { StopMonitoring(); } catch { /* swallow — best effort */ }
        }

        public IReadOnlyList<MonitoringDetectionEvent> Poll(bool isSacWindowActive)
        {
            if (!_isMonitoring)
                return Array.Empty<MonitoringDetectionEvent>();

            // Refresh the cached SAC-active flag so the WinEvent hook
            // callback (which fires asynchronously and doesn't receive
            // this argument) can pass through the latest known value
            // when it calls DetectFocus.
            _latestKnownSacActive = isSacWindowActive;

            var findings = new List<MonitoringDetectionEvent>();

            // Drain anything the WinEvent hook produced since the last
            // poll — preserves "instant" detection by guaranteeing those
            // findings ship on the very next Poll() turnaround.
            while (_hookEventQueue.TryDequeue(out var queued))
                findings.Add(queued);

            // The hook callback (Phase 1) and the WMI watcher (Phase 3) can
            // run concurrently with this Poll on different threads, and both
            // call AddEvent which mutates the shared _lastReportedAtByEvent
            // dictionary.  Hold the focus-detection lock around EVERY
            // detector — not just DetectFocus — so every AddEvent in the
            // class is serialised against the background callers.  The lock
            // is uncontended in the common case (the polling thread holds
            // it for the duration of one Poll; hook / WMI threads wait at
            // most a few hundred microseconds).
            lock (_focusDetectionLock)
            {
                DetectFocus(isSacWindowActive, findings);
                CheckCanvasPresence(findings);
                DetectClipboardAndScreenshot(findings);
                DetectIdle(findings);
                ScanAndHandleBlacklistedProcesses(findings);
                DetectMultiMonitor(findings);
            }

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

            // Same hard-suppression rule as the anchored path so the
            // legacy detector also never misclassifies one of our own
            // windows, a Windows shell foreground, or an unidentifiable
            // foreground as a foreign app switch.
            if (ShouldSuppressForegroundViolation(foreground, out _))
            {
                _lastForegroundWindow = foreground;
                _lastForegroundWasSac = true;
                return;
            }

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
                        string prevLabel = GetSanitizedWindowLabel(previousForeground);
                        string currLabel = GetSanitizedWindowLabel(foreground);
                        AddEvent(findings, DetectionConstants.EventWindowSwitch, 2,
                            $"Window switched from '{prevLabel}' to '{currLabel}' while monitoring is active.", 0);
                        ClearTemporaryExemptWindow();
                    }
                }
                else if (!isSacWindowActive)
                {
                    string prevLabel = GetSanitizedWindowLabel(previousForeground);
                    string currLabel = GetSanitizedWindowLabel(foreground);
                    AddEvent(findings, DetectionConstants.EventWindowSwitch, 2,
                        $"Window switched from '{prevLabel}' to '{currLabel}' while monitoring is active.", 0);
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

            // SELF-FOREGROUND / SHELL-FOREGROUND HARD SUPPRESS.
            //   Any foreground that belongs to:
            //     • the SAC itself (PID or process-name match),
            //     • a Windows shell / packaged-app host process
            //       (explorer, dwm, ApplicationFrameHost, etc.),
            //     • or a window we genuinely cannot identify
            //   is dropped entirely. This catches two distinct paths:
            //
            //     1) The cached `isSacWindowActive` flag is stale when
            //        the WinEvent foreground hook fires before the
            //        next polling tick — the hook would otherwise see
            //        the SAC's own window appear in foreground with
            //        the stale `false` flag and emit a spurious
            //        WINDOW_SWITCH to "Unknown application".
            //
            //     2) When `BtnExpandCompact_Click` toggles `Topmost=false`
            //        and resizes the softlock window, the OS briefly
            //        promotes a shell window (taskbar, DWM, etc.) into
            //        the foreground slot while z-order resettles. That
            //        is never a student action; it should not be a
            //        violation OR a log line anywhere.
            //
            //   Skipping outright (rather than relabelling) guarantees
            //   the event never reaches the findings list, so it
            //   appears in neither the SAC Detection Reports nor the
            //   IMC Global Log Feed — exactly what the spec calls for.
            if (ShouldSuppressForegroundViolation(foreground, out _))
            {
                _lastForegroundWindow = foreground;
                _lastWindowName = currentTitle;
                _lastForegroundWasSac = true;
                // Don't touch _wasPreviouslyOutOfExamFocus — the
                // student didn't actually leave the exam, so no
                // CANVAS_RETURNED should fire when they next focus
                // the LMS.
                return;
            }

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

            // ---- URL-BASED DEEP-PATH OVERRIDE ----
            // When the teacher's URL parsed into a valid anchor AND the
            // foreground is a browser, the address-bar URL is THE source of
            // truth. This closes Same-Domain Cheating: titles can't tell
            // docs.google.com/forms/... apart from docs.google.com/document/...
            // but the URL absolutely can.
            //
            // If UIA read fails (browser still painting, protected process,
            // etc.), the read returns null and we fall through to the
            // legacy title-based isOnLms above.
            string urlViolationReason = null;
            if (_urlAnchor != null && IsBrowserProcessForeground(foreground))
            {
                string activeUrl = BrowserUrlReader.TryGetForegroundBrowserUrl(foreground);
                if (!string.IsNullOrEmpty(activeUrl))
                {
                    var urlResult = UrlAnchorValidator.Validate(_urlAnchor, activeUrl);
                    if (urlResult.IsAuthorized)
                    {
                        // URL passed all gates. Enforce No-Multiple-Tabs:
                        // the first authorized hit captures the tab
                        // signature; any subsequent authorized hit with a
                        // different signature counts as a tab switch.
                        string currentSig = UrlAnchorValidator
                            .MakeTabSignature(foreground, _monitoringStartedAtUtc.Ticks);
                        if (_anchoredTabSignature == null)
                        {
                            _anchoredTabSignature = currentSig;
                            isOnLms = true;
                        }
                        else if (string.Equals(_anchoredTabSignature, currentSig, StringComparison.Ordinal))
                        {
                            isOnLms = true;
                        }
                        else
                        {
                            isOnLms = false;
                            urlViolationReason =
                                "Same exam URL but a different tab — only the originally anchored tab is permitted.";
                        }
                    }
                    else
                    {
                        // URL failed host / core-path / restricted-segment /
                        // suffix gate — definitive violation. Override the
                        // title-based isOnLms regardless of HWND match.
                        isOnLms = false;
                        urlViolationReason = urlResult.Reason;
                    }
                }
                else
                {
                    // ----------------------------------------------------
                    // STRICT TAB WHITELISTING FALLBACK (Phase 1, Task B)
                    // ----------------------------------------------------
                    // UIA URL read failed (browser still painting, page
                    // marked as protected / off-screen, etc.).  The
                    // previous behaviour fell back to the non-LMS title
                    // blacklist — a known-bad list.  Blacklists can
                    // always be evaded by renaming a tab to anything not
                    // on the list, so we invert the logic to a strict
                    // whitelist: the foreground window's title MUST
                    // contain _anchoredLmsDomain.
                    //
                    // sameAnchoredHwnd and browserFallback are NOT
                    // sufficient on their own when the URL can't be
                    // verified — both can be true for a tab that has
                    // navigated AWAY from the LMS but still shares the
                    // browser HWND.  Domain-in-title is the only signal
                    // we trust here.
                    //
                    // The SAC window itself is exempted downstream by
                    // the existing `!isSacWindowActive && !isOnLms`
                    // gate, so this branch never produces a false
                    // positive on the SAC.
                    if (!titleSaysLms)
                    {
                        isOnLms = false;
                        urlViolationReason =
                            "Browser URL could not be read and the window title does not contain the LMS domain.";
                    }
                    // else: titleSaysLms is true — isOnLms is already
                    //       set by the title-based path above; do not
                    //       override it.  This is the whitelist match.
                }
            }

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

            // ---- CANVAS_RETURNED — student returned to the LMS FROM a
            //      real out-of-exam window (Facebook tab, Word, another
            //      app). Informational only (0 pts); IMC renders the
            //      green RETURN badge.
            //
            //      Gate requires BOTH:
            //        • current foreground is the LMS, AND
            //        • previous foreground was neither SAC nor LMS
            //          (i.e., the student had actually left the exam).
            //
            //      Without the second clause, every focus oscillation
            //      between SAC and LMS (most commonly: clicking maximize
            //      / expand on the softlock overlay, which briefly hands
            //      focus to the SAC before the LMS settles back on top)
            //      produced a false "returned focus" entry.
            if (isOnLms && _wasPreviouslyOutOfExamFocus)
            {
                AddEvent(findings, DetectionConstants.EventCanvasReturned, 0,
                    $"Student returned focus to the LMS exam ({_anchoredLmsDomain}).",
                    cooldownSeconds: 2);
            }
            _wasPreviouslyOnLms = isOnLms;
            // Update the out-of-exam tracker AFTER the gate so the next
            // poll's CANVAS_RETURNED check sees the correct prior state.
            // True only when the student is neither in the SAC nor in
            // the LMS — i.e., the only state from which a return is
            // semantically meaningful.
            _wasPreviouslyOutOfExamFocus = !isSacWindowActive && !isOnLms;

            // Re-arm the edge-emit gate whenever the student is actually
            // on the LMS. After this point the next real external switch
            // is allowed to emit again even if it's to the same target
            // we previously logged.
            if (isOnLms)
            {
                _hasBeenOnLmsSinceLastEmit = true;
            }

            if (!isSacWindowActive && !isOnLms)
            {
                // ALLOWED-APPS GATE — instructor-configured per-session
                // allowlist (RoomDetectionSettings.AllowedAppsCsv).
                // If the foreground is one of those apps, emit an
                // ALLOWED_APP informational event (severity 0) instead
                // of WINDOW_SWITCH. Both the SAC log and the IMC log
                // surface it as a non-violation entry. We do NOT
                // update _wasPreviouslyOutOfExamFocus because the
                // student is still considered "inside the allowed
                // exam context" — when they return to the LMS we
                // don't want a spurious CANVAS_RETURNED log either.
                if (IsAllowedExceptionApp(foreground, currentTitle))
                {
                    // Description is just the friendly app name. Both
                    // the SAC and the IMC compose the final display
                    // string ("Allowed app switch detected: ALLOWED_APP
                    // | <AppName>") around it so the rendered wording
                    // stays consistent across both surfaces.
                    string allowedAppLabel = GetFriendlyAllowedAppName(foreground);

                    // Edge-emit gate. Re-entering the same allowed app
                    // after a quick SAC-softlock peek is NOT a new
                    // transition — only the very first arrival at this
                    // target (or arrival after an intervening LMS
                    // visit, or arrival at a DIFFERENT target) counts.
                    string emitKey = "ALLOWED_APP:" + allowedAppLabel;
                    bool isRepeatOfSameTarget =
                        !_hasBeenOnLmsSinceLastEmit
                        && string.Equals(emitKey, _lastEmittedExternalKey, StringComparison.OrdinalIgnoreCase);

                    if (!isRepeatOfSameTarget)
                    {
                        AddEvent(findings, DetectionConstants.EventAllowedApp, 0,
                            allowedAppLabel,
                            cooldownSeconds: 3);
                        _lastEmittedExternalKey = emitKey;
                        _hasBeenOnLmsSinceLastEmit = false;
                    }

                    _lastForegroundWindow = foreground;
                    _lastWindowName = currentTitle;
                    _lastForegroundWasSac = false;
                    return;
                }

                string description;
                if (urlViolationReason != null)
                {
                    // Deep-path URL gate rejected
                    description = $"Browser navigated to a non-exam URL. {urlViolationReason}";
                }
                else if (sameAnchoredHwnd && titleHasNonLmsKeyword)
                {
                    description = "Focus left the LMS tab in the same browser window.";
                }
                else
                {
                    // Use the existing sanitizer to get the clean app name the student switched to
                    string targetApp = GetSanitizedWindowLabel(foreground);
                    description = $"Focus lost from LMS exam ({_anchoredLmsDomain}) to '{targetApp}'.";
                }

                // Edge-emit gate (same shape as the ALLOWED_APP branch
                // above). Bouncing back to the same unauthorized
                // target after a SAC-softlock peek is not a fresh
                // transition — only re-arms when the student returns
                // to the LMS OR switches to a different external
                // target.
                string windowSwitchEmitKey = "WINDOW_SWITCH:" + description;
                bool isWindowSwitchRepeat =
                    !_hasBeenOnLmsSinceLastEmit
                    && string.Equals(windowSwitchEmitKey, _lastEmittedExternalKey, StringComparison.OrdinalIgnoreCase);

                if (!isWindowSwitchRepeat)
                {
                    // 1-second source-level cooldown: browsers (Facebook,
                    // Twitter, loading pages, etc.) mutate window titles
                    // multiple times per second during page load, and each
                    // mutation re-enters DetectFocusAnchored with the
                    // title diff branch open. Without this cooldown each
                    // mutation produced its own WINDOW_SWITCH and overwhelmed
                    // the SAC's 2-second per-type ReportViolationAsync dedup
                    // when both calls fell within the same dispatcher tick.
                    // 1 s is short enough that genuinely separate user
                    // switches (typically several seconds apart) each still
                    // pass, but long enough to coalesce same-target title
                    // shake into a single emission.
                    AddEvent(findings, DetectionConstants.EventWindowSwitch, 1,
                        description, cooldownSeconds: 1);
                    _lastEmittedExternalKey = windowSwitchEmitKey;
                    _hasBeenOnLmsSinceLastEmit = false;
                }
            }

            _lastForegroundWindow = foreground;
            _lastWindowName = currentTitle;
            _lastForegroundWasSac = isSacWindowActive;
        }

        private void ClearTemporaryExemptWindow()
        {
            _temporarilyExemptWindow = IntPtr.Zero;
        }

        // ============================================================
        // WinEvent foreground-hook plumbing (Phase 1, Task A)
        // ============================================================

        /// <summary>
        /// Registers an EVENT_SYSTEM_FOREGROUND hook so the SAC reacts to
        /// foreground changes the instant they happen rather than waiting
        /// for the next polling tick. Idempotent — safe to call twice.
        /// MUST be invoked from a thread with a message pump (typically
        /// the WPF UI thread); the OS delivers callbacks via that thread.
        /// </summary>
        private void InstallForegroundHook()
        {
            if (_foregroundHookHandle != IntPtr.Zero) return;

            try
            {
                // Hold a strong reference at the instance level so the GC
                // doesn't collect the delegate while Windows still holds
                // the unmanaged function pointer.
                _foregroundHookDelegate = OnForegroundWinEvent;

                _foregroundHookHandle = SetWinEventHook(
                    EVENT_SYSTEM_FOREGROUND,
                    EVENT_SYSTEM_FOREGROUND,
                    IntPtr.Zero,
                    _foregroundHookDelegate,
                    idProcess: 0,                          // all processes
                    idThread:  0,                          // all threads
                    dwFlags:   WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

                if (_foregroundHookHandle == IntPtr.Zero)
                {
                    // Hook registration failed (rare).  Polling continues
                    // unchanged; we just don't get the instant signal.
                    _foregroundHookDelegate = null;
                }
            }
            catch
            {
                // Don't let any hook-setup failure prevent monitoring
                // from starting; we fall back to pure polling.
                _foregroundHookHandle = IntPtr.Zero;
                _foregroundHookDelegate = null;
            }
        }

        /// <summary>
        /// Deterministically removes the foreground hook and drops the
        /// rooted delegate so the GC can reclaim it. Idempotent.
        /// </summary>
        private void UninstallForegroundHook()
        {
            if (_foregroundHookHandle == IntPtr.Zero)
            {
                _foregroundHookDelegate = null;
                return;
            }

            try
            {
                UnhookWinEvent(_foregroundHookHandle);
            }
            catch
            {
                // UnhookWinEvent can fail if the hook was already torn
                // down by the OS; nothing actionable on this side.
            }
            finally
            {
                _foregroundHookHandle  = IntPtr.Zero;
                _foregroundHookDelegate = null;
            }
        }

        // ============================================================
        // MULTI-MONITOR DETECTION (Phase 3, Task A)
        // ============================================================

        /// <summary>
        /// Emits a <see cref="DetectionConstants.EventMultiMonitor"/>
        /// finding when the OS reports more than one display attached.
        /// Re-evaluated every Poll() so a student who plugs in a second
        /// monitor mid-session is caught after StartMonitoring.  The
        /// AddEvent cooldown (60 s) prevents the event from spamming
        /// when the multi-monitor state is sustained.
        /// </summary>
        private void DetectMultiMonitor(ICollection<MonitoringDetectionEvent> findings)
        {
            int monitorCount;
            try
            {
                monitorCount = GetSystemMetrics(SM_CMONITORS);
            }
            catch
            {
                // GetSystemMetrics should never throw, but defend against
                // hostile shims that might intercept user32 calls.
                return;
            }

            if (monitorCount <= 1) return;

            AddEvent(findings, DetectionConstants.EventMultiMonitor, 3,
                $"Multiple displays attached ({monitorCount}). External monitors must be disconnected before the exam.",
                cooldownSeconds: 60);
        }

        // ============================================================
        // WMI PROCESS-CREATION WATCHER (Phase 3, Task B)
        // ============================================================

        /// <summary>
        /// Subscribes to Win32 process-creation events via WMI so that
        /// blacklisted applications are caught the instant they spawn,
        /// rather than waiting up to 5 seconds for the next polling
        /// scan in <see cref="ScanAndHandleBlacklistedProcesses"/>.
        ///
        /// The WMI query polls every 1 second internally — fast enough
        /// to feel instant, slow enough not to burn CPU.  The polling
        /// scan remains as a safety net for processes that started
        /// before this watcher was registered.
        ///
        /// Idempotent — safe to call multiple times.
        /// </summary>
        private void InstallProcessCreationWatcher()
        {
            if (!_settings.EnableProcessDetection) return;
            if (_processCreationWatcher != null) return;

            try
            {
                // WITHIN 1 = poll the Win32_Process table once per second.
                // TargetInstance is the freshly-created process record.
                var query = new WqlEventQuery(
                    "SELECT TargetInstance FROM __InstanceCreationEvent " +
                    "WITHIN 1 " +
                    "WHERE TargetInstance ISA 'Win32_Process'");

                _processCreationWatcher = new ManagementEventWatcher(query);
                _processCreationWatcher.EventArrived += OnProcessCreated;
                _processCreationWatcher.Start();
            }
            catch
            {
                // WMI may be disabled / unreachable in hardened SOEs.
                // Polling scan continues unchanged; we just lose the
                // sub-second response.
                try { _processCreationWatcher?.Dispose(); } catch { /* ignore */ }
                _processCreationWatcher = null;
            }
        }

        /// <summary>
        /// Deterministically tears down the WMI watcher.  Critical for
        /// resource hygiene: ManagementEventWatcher holds a COM proxy and
        /// a background thread; leaking it across monitoring cycles
        /// would accumulate handles over the lifetime of the process.
        /// </summary>
        private void UninstallProcessCreationWatcher()
        {
            var watcher = _processCreationWatcher;
            if (watcher == null) return;

            try
            {
                watcher.EventArrived -= OnProcessCreated;
                watcher.Stop();
            }
            catch
            {
                // Stop() can throw if the watcher already faulted.
            }

            try
            {
                watcher.Dispose();
            }
            catch
            {
                // ignore — best-effort disposal
            }
            finally
            {
                _processCreationWatcher = null;
            }
        }

        /// <summary>
        /// WMI EventArrived callback.  Extracts the process name from
        /// TargetInstance, validates it against the blacklists (with the
        /// protected-process guard so browsers / OS utilities can't be
        /// flagged by an instructor's per-room blacklist), and emits
        /// PROCESS_DETECTED via the thread-safe
        /// <see cref="EmitFromBackgroundThread"/> helper.
        ///
        /// CRITICAL: must never let an exception escape — WMI callbacks
        /// run on a worker thread owned by System.Management, and an
        /// unhandled exception there terminates the process.
        /// </summary>
        private void OnProcessCreated(object sender, EventArrivedEventArgs e)
        {
            if (!_isMonitoring) return;

            try
            {
                var target = e.NewEvent?["TargetInstance"] as ManagementBaseObject;
                if (target == null) return;

                string rawName = Convert.ToString(target["Name"]) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(rawName)) return;

                // Strip the ".exe" so the value matches the conventions
                // already used by _blacklistedApps / _blacklistedProcessNames
                // / _protectedProcesses (all stored without extensions).
                string nameNoExt = rawName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    ? rawName.Substring(0, rawName.Length - 4)
                    : rawName;

                // Protected-process guard FIRST — same precedence rule as
                // ScanAndHandleBlacklistedProcesses, so an instructor's
                // per-room blacklist still can't sneak past a global
                // protection (browsers, OS utilities, core Windows).
                if (_protectedProcesses.Contains(nameNoExt)) return;

                bool isBlacklisted =
                    _blacklistedApps.Any(b =>
                        string.Equals(b, nameNoExt, StringComparison.OrdinalIgnoreCase))
                    || _blacklistedProcessNames.Contains(nameNoExt);

                if (!isBlacklisted) return;

                // Allowlist supersedes blacklist. If the instructor has
                // explicitly permitted this app for the session, never
                // fire a PROCESS_DETECTED for it — even if it appears
                // in the global blacklist (e.g., a meeting app the
                // instructor wants used for Q&A).
                if (_settings.AllowedAppProcessNames is { Count: > 0 }
                    && _settings.AllowedAppProcessNames.Contains(nameNoExt))
                {
                    return;
                }

                EmitFromBackgroundThread(
                    DetectionConstants.EventProcessDetected,
                    severity: 3,
                    description: $"Unauthorized process started: {nameNoExt}",
                    cooldownSeconds: 5);
            }
            catch
            {
                // Swallowing intentionally — see XML doc above.
            }
        }

        /// <summary>
        /// Thread-safe AddEvent for callers that run on a non-Poll
        /// thread (the WinEvent hook in Phase 1 and the WMI watcher
        /// here).  Acquires <see cref="_focusDetectionLock"/> just for
        /// the dictionary read/write inside AddEvent, then enqueues any
        /// resulting events to <see cref="_hookEventQueue"/> so the
        /// next Poll() can drain them into its findings list.
        ///
        /// Crucially this does NOT touch ObservableCollections — the
        /// public Poll() return path is the only place where
        /// MonitoringDetectionEvent instances leave this class, and
        /// the consumer of Poll() is responsible for any UI-thread
        /// marshalling.
        /// </summary>
        private void EmitFromBackgroundThread(string eventType, int severity, string description, int cooldownSeconds)
        {
            var localFindings = new List<MonitoringDetectionEvent>();
            lock (_focusDetectionLock)
            {
                if (!_isMonitoring) return;          // re-check under the lock
                AddEvent(localFindings, eventType, severity, description, cooldownSeconds);
            }
            foreach (var f in localFindings)
                _hookEventQueue.Enqueue(f);
        }

        /// <summary>
        /// SetWinEventHook callback.  Triggers immediate focus
        /// re-evaluation via DetectFocus (the dispatcher routes to the
        /// anchored or legacy path based on configuration).  Any findings
        /// are pushed to <see cref="_hookEventQueue"/> and drained by the
        /// next <see cref="Poll"/> call so the existing emission pipeline
        /// stays intact — MonitoringDetectionEvent severities and event
        /// types are unchanged.
        ///
        /// CRITICAL: this method must NEVER let an exception escape.
        /// An unhandled exception in a SetWinEventHook callback
        /// fast-fails the process.
        /// </summary>
        private void OnForegroundWinEvent(
            IntPtr hWinEventHook,
            uint   eventType,
            IntPtr hwnd,
            int    idObject,
            int    idChild,
            uint   dwEventThread,
            uint   dwmsEventTime)
        {
            if (!_isMonitoring)                  return;
            if (eventType != EVENT_SYSTEM_FOREGROUND) return;
            if (idObject  != OBJID_WINDOW)       return;
            if (hwnd      == IntPtr.Zero)        return;

            try
            {
                var hookFindings = new List<MonitoringDetectionEvent>();

                // Serialise against any concurrent Poll() that might be
                // running on the timer thread — same lock as Poll uses.
                lock (_focusDetectionLock)
                {
                    if (!_isMonitoring) return;     // re-check after lock
                    DetectFocus(_latestKnownSacActive, hookFindings);
                }

                // Hand findings to Poll() via the cross-thread queue so
                // the public IReadOnlyList contract isn't broken.
                foreach (var f in hookFindings)
                    _hookEventQueue.Enqueue(f);
            }
            catch
            {
                // Swallowing intentionally — see XML doc above.
            }
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

                // PASSIVE MONITORING — DO NOT MUTATE THE CLIPBOARD.
                // We previously called Clipboard.Clear() here to
                // neutralise right-click paste; that behaviour is
                // banned by design because it (a) actively
                // interferes with system input and (b) made Ctrl+V
                // appear to be blocked even though the hook itself
                // passes the keystroke through. Detection is logged
                // here and via the hook-based CLIPBOARD_PASTE event;
                // no clipboard state is altered. If a future
                // requirement asks for paste prevention again,
                // implement it at the application boundary (sandbox
                // the exam shell), NOT by mutating the global
                // clipboard mid-session.
            }

            bool ctrlPressed = IsKeyDown(VK_CONTROL);

            bool copyPressed = ctrlPressed && IsKeyDown(VK_C);
            if (copyPressed && !_copyDown)
            {
                AddEvent(findings, DetectionConstants.EventClipboardCopy, 2,
                    "Copy command (Ctrl+C) detected while monitoring is active.", 2);
            }
            _copyDown = copyPressed;

            // ----------------------------------------------------
            // Polled Ctrl+V (rising-edge) — paste violation flow
            // ----------------------------------------------------
            // The keyboard-hook path
            // (KeyboardHookService.PasteCombinationDetected →
            // SacDetectorRuntime.OnPasteCombinationDetected →
            // EmitSyntheticFinding) is the FAST path that catches
            // quick key taps which would land entirely between two
            // monitoring ticks. THIS polling path is the AUDITABLE
            // path — it puts CLIPBOARD_PASTE on exactly the same
            // pipeline as the working CLIPBOARD_COPY detection above
            // (AddEvent → Poll → EvaluateAndMapFindings →
            // ReportViolationAsync → server → IMC log feed). Keeping
            // both means the global log feed sees paste violations
            // even if the hook channel ever drops (e.g. a slow
            // dispatcher tick that delays the BeginInvoke or a
            // downstream exception in the hook-path consumer).
            //
            // Severity 2 + cooldown 0 mirrors the original
            // behaviour; AddEvent's per-event-type dedup throttles
            // repeated identical entries inside the same tick.
            bool pastePressed = ctrlPressed && IsKeyDown(VK_V);
            if (pastePressed && !_pasteDown)
            {
                AddEvent(findings, DetectionConstants.EventClipboardPaste, 2,
                    "Paste command (Ctrl+V) detected while monitoring is active.", 0);
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

        // ============================================================
        // Sanitization helpers — produce privacy-safe labels for use in
        // WINDOW_SWITCH violation descriptions. Raw OS window titles often
        // leak private data (file paths, document names, chat-channel
        // names, etc.); the rules below normalize the output:
        //
        //   * Browsers           → "{Tab name} - {Browser product name}"
        //   * All other apps     → only the application's product name
        //                          (FileDescription, falling back to
        //                          ProcessName). The raw title is dropped.
        //
        // HWND→PID→Process is used instead of string parsing so the
        // solution generalizes to any app without hardcoded lists.
        // ============================================================

        private static readonly Regex _browserNotifPrefix =
            new(@"^\(\d+\)\s*", RegexOptions.Compiled);

        /// <summary>
        /// Returns a sanitized, privacy-safe label for the given window
        /// suitable for logging in a violation description.
        /// </summary>
        /// <summary>
        /// Per-session instructor allowlist (Allowed Apps During Exam).
        /// Returns true when the foreground window belongs to an app
        /// the teacher has explicitly permitted, so the focus / process
        /// detectors should suppress their violation for this window.
        ///
        /// Two evaluation modes (combined):
        ///   • Process-name tokens (no ".") match
        ///     Process.ProcessName of the foreground window — catches
        ///     standalone desktop apps like Microsoft Teams, Zoom,
        ///     Calculator, Notepad, Acrobat Reader.
        ///   • Domain tokens (containing ".") match a case-insensitive
        ///     substring of the foreground BROWSER window's title —
        ///     catches browser-based meeting tools like
        ///     "meet.google.com" without whitelisting the entire
        ///     browser process. Non-browser foregrounds never go
        ///     through the domain check.
        ///
        /// Empty allowlist → always returns false (feature off).
        /// </summary>
        private bool IsAllowedExceptionApp(IntPtr hWnd, string currentTitle)
        {
            if (hWnd == IntPtr.Zero) return false;

            bool hasProcRules  = _settings.AllowedAppProcessNames  is { Count: > 0 };
            bool hasTitleRules = _settings.AllowedAppTitleKeywords is { Count: > 0 };
            if (!hasProcRules && !hasTitleRules) return false;

            // Resilient process-name lookup that survives packaged /
            // sandboxed apps (e.g. new Microsoft Teams). Returning null
            // here used to silently route Teams into the strict "not
            // allowed → violation" branch even when the allowlist
            // explicitly named it.
            string processName = TryGetForegroundProcessName(hWnd);

            if (hasProcRules
                && !string.IsNullOrEmpty(processName)
                && _settings.AllowedAppProcessNames.Contains(processName))
            {
                return true;
            }

            if (hasTitleRules
                && !string.IsNullOrEmpty(processName)
                && _browserProcessNames.Contains(processName)
                && !string.IsNullOrEmpty(currentTitle))
            {
                foreach (var keyword in _settings.AllowedAppTitleKeywords)
                {
                    if (currentTitle.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Best-effort human-friendly name for an allowlisted process.
        /// Some well-known process tokens map to a properly-cased
        /// display name (so the SAC log reads "Allowed app: Microsoft
        /// Teams" instead of "Allowed app: ms-teams"); the rest fall
        /// through to <see cref="GetSanitizedWindowLabel"/> which uses
        /// the FileDescription when available.
        /// </summary>
        private static string GetFriendlyAllowedAppName(IntPtr hWnd)
        {
            string proc = TryGetForegroundProcessName(hWnd);
            if (!string.IsNullOrEmpty(proc))
            {
                switch (proc.ToLowerInvariant())
                {
                    case "teams":
                    case "ms-teams":
                    case "msteams":
                        return "Microsoft Teams";
                    case "zoom":
                    case "cpthost":
                        return "Zoom";
                    case "notepad":
                        return "Notepad";
                    case "notepad++":
                        return "Notepad++";
                    case "calc":
                    case "calculatorapp":
                    case "win32calc":
                        return "Calculator";
                    case "acrord32":
                    case "acrobat":
                        return "Adobe Acrobat Reader";
                    case "sumatrapdf":
                        return "Sumatra PDF";
                    case "foxitreader":
                        return "Foxit Reader";
                }
            }

            // Fall through to the existing label sanitizer — which now
            // also benefits from TryGetForegroundProcessName so the
            // worst-case is the process name (not "Unknown application").
            return GetSanitizedWindowLabel(hWnd);
        }

        private static string GetSanitizedWindowLabel(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return "Unknown application";

            string rawTitle = GetWindowName(hWnd);

            // Resolve the process name through the resilient helper
            // first. This survives packaged apps (Teams, UWP) where
            // Process.GetProcessById denies access and would otherwise
            // funnel us into "Unknown application".
            string processName = TryGetForegroundProcessName(hWnd) ?? string.Empty;
            if (string.IsNullOrEmpty(processName))
            {
                return "Unknown application";
            }

            try
            {
                GetWindowThreadProcessId(hWnd, out uint pid);
                if (pid == 0) return processName;

                using var process = Process.GetProcessById((int)pid);
                if (process == null) return processName;

                string appDisplayName = TryGetFileDescription(process) ?? processName;
                if (string.IsNullOrWhiteSpace(appDisplayName))
                    appDisplayName = processName;

                bool isBrowser = _browserProcessNames.Contains(processName);

                if (!isBrowser)
                {
                    // Privacy rule for non-browsers: discard the raw title
                    // entirely (it may contain file paths, doc names, chat
                    // channels, etc.) and return only the clean app name.
                    return appDisplayName;
                }

                string tab = ExtractBrowserTabName(rawTitle, appDisplayName, processName);
                if (string.IsNullOrWhiteSpace(tab))
                    return appDisplayName;

                return $"{tab} - {appDisplayName}";
            }
            catch
            {
                // Process may have exited between PID lookup and inspection,
                // or MainModule access may have been denied (UAC / bitness
                // mismatch). Fall back to the resilient process-name
                // result so packaged apps still produce a meaningful
                // label instead of the generic placeholder.
                return processName.Length > 0 ? processName : "Unknown application";
            }
        }

        /// <summary>
        /// Safe accessor for the executable's FileDescription. Catches the
        /// Win32Exception that .NET throws when the calling process can't
        /// read MainModule (cross-bitness, protected-process, etc.).
        /// </summary>
        private static string? TryGetFileDescription(Process process)
        {
            try
            {
                var module = process.MainModule;
                var info = module?.FileVersionInfo;
                var desc = info?.FileDescription;
                return string.IsNullOrWhiteSpace(desc) ? null : desc.Trim();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Strips a browser title down to just the tab name:
        ///   * Removes leading notification counters like "(3) ".
        ///   * Drops the trailing " - {Browser}" / " — {Browser}" segment
        ///     when the trailer matches the browser display name or
        ///     process name (so the final formatted output doesn't repeat
        ///     the browser name twice).
        /// </summary>
        private static string ExtractBrowserTabName(string rawTitle, string browserDisplayName, string processName)
        {
            if (string.IsNullOrWhiteSpace(rawTitle) || rawTitle == "Unknown")
                return string.Empty;

            string title = _browserNotifPrefix.Replace(rawTitle, string.Empty).Trim();

            // Try common separators in priority order. Most Chromium /
            // Gecko builds use a hyphen with surrounding spaces.
            string[] separators = { " - ", " — ", " – " };
            foreach (var sep in separators)
            {
                int idx = title.LastIndexOf(sep, StringComparison.Ordinal);
                if (idx <= 0) continue;

                string trailer = title.Substring(idx + sep.Length).Trim();
                bool trailerIsBrowser =
                    (!string.IsNullOrEmpty(browserDisplayName)
                     && trailer.IndexOf(browserDisplayName, StringComparison.OrdinalIgnoreCase) >= 0)
                    || (!string.IsNullOrEmpty(processName)
                        && trailer.IndexOf(processName, StringComparison.OrdinalIgnoreCase) >= 0);

                if (trailerIsBrowser)
                {
                    title = title.Substring(0, idx).Trim();
                    break;
                }
            }

            return title;
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
