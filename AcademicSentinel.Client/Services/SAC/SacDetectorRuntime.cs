using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using AcademicSentinel.Client.Services.SAC.DetectionService;
using AcademicSentinel.Client.Services.SAC.Models;
using AcademicSentinel.Client.Services.SAC.Utilities;

namespace AcademicSentinel.Client.Services.SAC
{
    internal sealed class SacDetectorRuntime : IDisposable
    {
        private readonly DetectorRuntimeOptions _options;
        private readonly BehavioralMonitoringService _behavioralMonitoringService;
        private readonly EnvironmentIntegrityService _environmentIntegrityService;
        private readonly DecisionEngineService _decisionEngineService;
        private readonly KeyboardHookService _keyboardHookService;
        private readonly MouseHookService _mouseHookService;
        private readonly HardwareSoftwareArtifactService _hardwareSoftwareArtifactService;
        private bool _isStarted;
        private bool _isDisposed;
        public bool IsPaused { get; set; } = false;
        public bool IsLoggingEnabled { get; private set; } = true;

        // Set to true while the instructor has approved this student's
        // raised-hand request. While active, the runtime drops findings
        // whose EventType is in <see cref="_handRaiseSuppressedEventTypes"/>
        // — alt-tab / focus / process / idle — so the student can use
        // the meeting app without generating violations. Hardware (VAC),
        // clipboard, and screenshot detections still fire because those
        // represent academic-integrity risks unaffected by a Q&A pause.
        // The SAC window flips this in response to the server's
        // OnHandRaiseApproved / HandLowered hub events.
        public bool IsHandRaised { get; set; } = false;

        private static readonly HashSet<string> _handRaiseSuppressedEventTypes =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "ALT_TAB",
                "WINDOW_SWITCH",
                "FOCUS_LOST",
                "RTFM",
                "IDLE",
                "INACTIVITY",
                "PROCESS_DETECTED"
            };

        private bool IsSuppressedByRaisedHand(string eventType) =>
            IsHandRaised
            && !string.IsNullOrEmpty(eventType)
            && _handRaiseSuppressedEventTypes.Contains(eventType);

        public SacDetectorRuntime(DetectorRuntimeOptions options)
        {
            _options = options;

            // Strict Mode (per spec v3/v4/v5):
            //  - Idle threshold is halved (tighter inactivity gating).
            //  - DecisionEngine treats every passive event as S2 (20 pts) instead
            //    of S1 (10 pts), and bypasses the 3-event escalation counter.
            int rawIdle = Math.Max(1, _options.IdleThresholdSeconds);
            int idleViolation = _options.StrictMode
                ? Math.Max(5, rawIdle / 2)
                : rawIdle;

            // Split AllowedAppsCsv into process-name tokens vs domain
            // tokens. Tokens containing a "." go into the domain set
            // (matched against browser window titles); the rest go
            // into the process-name set.
            var allowedProcs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var allowedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(_options.AllowedAppsCsv))
            {
                foreach (var raw in _options.AllowedAppsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var token = raw.ToLowerInvariant();
                    if (token.EndsWith(".exe", StringComparison.Ordinal))
                        token = token[..^4];
                    if (token.Length == 0) continue;
                    if (token.Contains('.'))
                        allowedDomains.Add(token);
                    else
                        allowedProcs.Add(token);
                }
            }

            var settings = new DetectionSettings
            {
                EnableFocusDetection = _options.EnableFocusDetection,
                EnableClipboardMonitoring = _options.EnableClipboardMonitoring,
                EnableIdleDetection = _options.EnableIdleDetection,
                EnableProcessDetection = _options.EnableProcessDetection,
                IdleWarningThresholdSeconds = Math.Max(5, idleViolation / 2),
                IdleViolationThresholdSeconds = idleViolation,
                IdleCriticalThresholdSeconds = Math.Max(idleViolation + 10, idleViolation * 2),
                // REQUIRED — LMS-anchored focus detection. Pass through so
                // BehavioralMonitoringService can extract the domain on
                // StartMonitoring and anchor a browser window to it.
                LmsExamUrl = _options.LmsExamUrl,
                AllowedAppProcessNames = allowedProcs,
                AllowedAppTitleKeywords = allowedDomains
            };

            _behavioralMonitoringService = new BehavioralMonitoringService(settings, _options.BlacklistedProcessNames);
            _environmentIntegrityService = new EnvironmentIntegrityService();
            _decisionEngineService = new DecisionEngineService(_options.StrictMode);
            _keyboardHookService = new KeyboardHookService();
            _keyboardHookService.ScreenshotKeyDetected += OnScreenshotKeyDetected;
            _keyboardHookService.SnippingToolComboDetected += OnSnippingToolComboDetected;
            _keyboardHookService.PasteCombinationDetected += OnPasteCombinationDetected;

            _mouseHookService = new MouseHookService();
            _mouseHookService.RightClickContextDetected += OnRightClickContextDetected;

            _hardwareSoftwareArtifactService = new HardwareSoftwareArtifactService();
            _hardwareSoftwareArtifactService.ArtifactDetected += OnHasArtifactDetected;
        }

        private void OnHasArtifactDetected(string eventType, string description)
        {
            if (!_isStarted || IsPaused)
                return;

            var ev = new MonitoringDetectionEvent
            {
                EventType = eventType,
                Description = description,
                Timestamp = DateTime.UtcNow
            };
            EmitSyntheticFinding(ev);
        }

        private void OnScreenshotKeyDetected()
        {
            if (!_isStarted || IsPaused)
                return;

            // Synthesise a CSAD finding and push it through the same pipeline
            // as the polled clipboard-based events, so dedup + scoring apply.
            var ev = new MonitoringDetectionEvent
            {
                EventType = "PRINTSCREEN",
                Description = "PrintScreen key pressed (low-level keyboard hook).",
                Timestamp = DateTime.UtcNow
            };
            EmitSyntheticFinding(ev);
        }

        private void OnSnippingToolComboDetected()
        {
            if (!_isStarted || IsPaused)
                return;

            var ev = new MonitoringDetectionEvent
            {
                EventType = "SNIP_TOOL",
                Description = "Win+Shift+S pressed (Snipping Tool overlay invoked).",
                Timestamp = DateTime.UtcNow
            };
            EmitSyntheticFinding(ev);
        }

        // Hook → dispatcher → here. Offload EmitSyntheticFinding
        // (which runs the decision engine and ultimately triggers
        // ReportViolationAsync's HTTP / SignalR work) onto the
        // thread pool so the dispatcher tick is released
        // immediately. The hook itself was already released the
        // moment InvokeOnDispatcherSafe BeginInvoke'd this handler;
        // the Task.Run hop is defence in depth so a slow violation
        // dispatch can never block UI either.
        private void OnPasteCombinationDetected()
        {
            if (!_isStarted || IsPaused)
                return;

            var ev = new MonitoringDetectionEvent
            {
                EventType = DetectionConstants.EventClipboardPaste,
                Description = "Paste combination (Ctrl+V) detected via low-level keyboard hook.",
                Timestamp = DateTime.UtcNow
            };

            // Fire-and-forget on the thread pool. Swallow exceptions
            // — this is an out-of-band signal and must NEVER take the
            // process down. EmitSyntheticFinding already marshals
            // back to the dispatcher for the consumer callback, so
            // ObservableCollection updates remain safe.
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    if (_isDisposed) return;
                    EmitSyntheticFinding(ev);
                }
                catch
                {
                    // Intentional: out-of-band finding path.
                }
            });
        }

        // Mouse-hook path. Right-button-up is the OS-level signal
        // that *might* be opening a context menu containing Paste —
        // we cannot see inside the menu, so this is logged as
        // RIGHT_CLICK_CONTEXT (a paste *vector*, not a confirmed
        // paste). Same Task.Run offload as OnPasteCombinationDetected
        // so neither the hook thread NOR the dispatcher is held by
        // the violation dispatch. Hook is strictly passive — the
        // right-click still reaches the focused app exactly as
        // before this code existed.
        private void OnRightClickContextDetected()
        {
            if (!_isStarted || IsPaused)
                return;

            var ev = new MonitoringDetectionEvent
            {
                EventType = DetectionConstants.EventRightClickContextMenu,
                Description = "Right-click detected during active monitoring (potential paste vector via context menu).",
                Timestamp = DateTime.UtcNow
            };

            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    if (_isDisposed) return;
                    EmitSyntheticFinding(ev);
                }
                catch
                {
                    // Intentional: out-of-band finding path.
                }
            });
        }

        private void EmitSyntheticFinding(MonitoringDetectionEvent rawEvent)
        {
            // Raised-hand suppression — drop the event before it reaches
            // the decision engine so the cumulative risk score is not
            // inflated by allowed Q&A behaviour. The check is scoped to
            // alt-tab / focus / idle / process events only; screenshot
            // hooks (PRINTSCREEN / SNIP_TOOL) and hardware artifacts
            // still fire because they are not whitelisted during Q&A.
            if (IsSuppressedByRaisedHand(rawEvent.EventType))
                return;

            // Refuse to run the decision engine after the runtime has
            // been torn down — a late event from the hardware-artifact
            // watcher could otherwise reach a disposed consumer callback
            // and crash the dispatcher.
            if (_isDisposed)
                return;

            var assessment = _decisionEngineService.EvaluateEvent(rawEvent);
            // Run every description — even from out-of-band sources like the
            // hardware-artifact watcher and the keyboard hook — through the
            // sanitizer so no "Now viewing" payload or stale "| CumulativeScore="
            // trailer can ever leave this class.
            var description = SanitizeDescription(rawEvent.Description ?? string.Empty);
            var finding = new DetectorFinding(rawEvent.EventType, rawEvent.SeverityScore, description);

            // Dispatch onto the WPF UI thread before invoking the consumer
            // callback.  The keyboard-hook path already marshals via
            // KeyboardHookService.InvokeOnDispatcherSafe, but the hardware-
            // artifact path (OnHasArtifactDetected → here) can arrive on a
            // background WMI/timer thread.  Since the callback ultimately
            // mutates an ObservableCollection in the SAC window, marshalling
            // here is the only place that protects every entry path
            // uniformly.
            //
            // Reuse the preflight callback as a generic "out-of-band finding"
            // channel — the SAC window already routes that to ReportViolationAsync.
            InvokeOnDispatcherSafe(() => _options.OnPreFlightViolationDetected?.Invoke(finding));
        }

        /// <summary>
        /// Defensive log-description scrubber.  Runs on EVERY description
        /// that leaves this class (both the polled <see cref="EvaluateAndMapFindings"/>
        /// path and the out-of-band <see cref="EmitSyntheticFinding"/> path),
        /// so the server / DB never receives a private payload — even if a
        /// future detector accidentally re-introduces a noisy format, or
        /// an event from before this patch is replayed from a queue.
        ///
        /// Two rules, applied in order:
        ///   1. "Now viewing" anywhere in the text → REPLACE the entire
        ///      description with a generic, privacy-safe message.  This
        ///      catches legacy WINDOW_SWITCH violations whose body
        ///      included the destination tab/window title — those titles
        ///      leak video / document / chat-channel names and must never
        ///      reach the database.
        ///   2. "| CumulativeScore=" anywhere → truncate at the pipe
        ///      (TrimEnd to avoid trailing whitespace).  Catches any
        ///      legacy event that still carries the old runtime trailer
        ///      "<msg> | CumulativeScore=N; RiskLevel=L".
        ///
        /// Strict ordering: rule 1 fires first because a "Now viewing"
        /// payload should be wholly replaced regardless of any trailing
        /// score metadata.
        /// </summary>
        private static string SanitizeDescription(string description)
        {
            if (string.IsNullOrEmpty(description))
                return description ?? string.Empty;

            // Rule 1 — wipe descriptions that leak the destination URL/title.
            if (description.IndexOf("Now viewing", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Browser navigated to a non-exam URL.";

            // Rule 2 — strip legacy cumulative-score trailer.
            int pipeIdx = description.IndexOf("| CumulativeScore=", StringComparison.OrdinalIgnoreCase);
            if (pipeIdx >= 0)
                return description.Substring(0, pipeIdx).TrimEnd();

            return description;
        }

        /// <summary>
        /// Posts <paramref name="action"/> onto the WPF UI thread when
        /// invoked from a background thread; runs it synchronously if
        /// already on the UI thread.  Mirrors KeyboardHookService's helper
        /// of the same name so every entry point into the consumer
        /// callback observes identical thread-affinity guarantees.
        /// </summary>
        private static void InvokeOnDispatcherSafe(Action action)
        {
            if (action == null) return;
            try
            {
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher != null && !dispatcher.CheckAccess())
                {
                    dispatcher.BeginInvoke(DispatcherPriority.Normal, action);
                }
                else
                {
                    action();
                }
            }
            catch
            {
                // Swallow — the consumer callback owns its own error
                // reporting; we just don't want a late background event
                // to bring down the dispatcher.
            }
        }

        public IReadOnlyList<DetectorFinding> Poll(bool isWindowActive)
        {
            if (!_isStarted)
                return Array.Empty<DetectorFinding>();

            return EvaluateAndMapFindings(_behavioralMonitoringService.Poll(isWindowActive));
        }

        public IReadOnlyList<DetectorFinding> RunStartupChecks()
        {
            return Array.Empty<DetectorFinding>();
        }

        public IReadOnlyList<DetectorFinding> OnWindowDeactivated()
        {
            if (!_isStarted)
                return Array.Empty<DetectorFinding>();

            return EvaluateAndMapFindings(_behavioralMonitoringService.Poll(false));
        }

        public async Task SetMonitoringEnabledAsync(bool enabled)
        {
            if (enabled)
            {
                if (_isStarted)
                    return;

                _isStarted = true;
                IsLoggingEnabled = true;
                _behavioralMonitoringService.StartMonitoring();
                _keyboardHookService.Install();
                _mouseHookService.Install();
                _hardwareSoftwareArtifactService.Start();

                var hardwareState = await _environmentIntegrityService.PerformFullScanAsync();

                if (_options.OnHardwareStateDetected != null)
                {
                    await _options.OnHardwareStateDetected(hardwareState.IsVm, hardwareState.IsRemote);
                }

                if (hardwareState.IsVm || hardwareState.IsRemote)
                {
                    var description = $"Critical Environment Violation: VM: {hardwareState.IsVm}, Remote: {hardwareState.IsRemote}";
                    _options.OnPreFlightViolationDetected?.Invoke(new DetectorFinding("VAC_HAS_VIOLATION", 50, description));
                }

                return;
            }

            if (!_isStarted)
                return;

            _isStarted = false;
            IsLoggingEnabled = false;
            _behavioralMonitoringService.StopMonitoring();
            _keyboardHookService.Uninstall();
            _mouseHookService.Uninstall();
            _hardwareSoftwareArtifactService.Stop();
        }

        public void SetMonitoringEnabled(bool enabled)
        {
            _ = SetMonitoringEnabledAsync(enabled);
        }

        public async Task StopMonitoringAsync()
        {
            IsLoggingEnabled = false;

            if (!_isStarted)
                return;

            _isStarted = false;
            _behavioralMonitoringService.StopMonitoring();
            _keyboardHookService.Uninstall();
            _mouseHookService.Uninstall();
            _hardwareSoftwareArtifactService.Stop();
            await Task.CompletedTask;
        }

        public void Stop()
        {
            IsLoggingEnabled = false;

            if (!_isStarted)
                return;

            _isStarted = false;
            _behavioralMonitoringService.StopMonitoring();
            _keyboardHookService.Uninstall();
            _mouseHookService.Uninstall();
            _hardwareSoftwareArtifactService.Stop();
        }

        public void Dispose()
        {
            if (_isDisposed)
                return;

            _isDisposed = true;

            // Step 1 — Stop the monitoring loop.  This synchronously
            // detaches the WinEvent hook and the WMI watcher inside
            // BehavioralMonitoringService and uninstalls the low-level
            // keyboard hook.  Wrapped in try/catch so a partial failure
            // doesn't prevent the other teardown steps below from running.
            try { Stop(); } catch { /* swallow — best-effort shutdown */ }

            // Step 2 — Explicitly unsubscribe before disposing the child
            // services.  Each child's Dispose already nulls its own event
            // delegates, but doing it here too breaks the closure-held
            // references to `this` immediately — important when the
            // runtime is being torn down from the WPF dispatcher and
            // background callbacks may still be in flight.
            try
            {
                _keyboardHookService.ScreenshotKeyDetected      -= OnScreenshotKeyDetected;
                _keyboardHookService.SnippingToolComboDetected  -= OnSnippingToolComboDetected;
                _keyboardHookService.PasteCombinationDetected   -= OnPasteCombinationDetected;
                _mouseHookService.RightClickContextDetected     -= OnRightClickContextDetected;
                _hardwareSoftwareArtifactService.ArtifactDetected -= OnHasArtifactDetected;
            }
            catch { /* swallow — handler list may already be cleared */ }

            // Step 3 — Dispose every child service that owns OS-level
            // resources.  BehavioralMonitoringService was missed in the
            // pre-Phase-5 implementation — its Dispose now invokes
            // StopMonitoring idempotently to detach the WinEvent hook,
            // dispose the WMI watcher, and clear the BrowserUrlReader
            // UIA cache.  Without this, rapid Start/Stop toggles followed
            // by Dispose could leak an OS hook handle on every cycle.
            try { _behavioralMonitoringService.Dispose(); }     catch { }
            try { _keyboardHookService.Dispose(); }             catch { }
            try { _mouseHookService.Dispose(); }                catch { }
            try { _hardwareSoftwareArtifactService.Dispose(); } catch { }

            // Step 4 — Detach the option callbacks so the captured closures
            // (which hold a reference to the SAC window) cannot fire after
            // disposal.  Belt-and-suspenders for any late event that
            // squeezes through before the child services finish their own
            // teardown.
            _options.OnHardwareStateDetected      = null;
            _options.OnPreFlightViolationDetected = null;

            IsPaused = true;
        }

        private IReadOnlyList<DetectorFinding> EvaluateAndMapFindings(IReadOnlyList<MonitoringDetectionEvent> events)
        {
            if (IsPaused)
                return Array.Empty<DetectorFinding>();

            if (events == null || events.Count == 0)
                return Array.Empty<DetectorFinding>();

            var mapped = new List<DetectorFinding>(events.Count);
            foreach (var rawEvent in events)
            {
                // Same scoped suppression as the synthetic-finding path.
                if (IsSuppressedByRaisedHand(rawEvent.EventType))
                    continue;

                var assessment = _decisionEngineService.EvaluateEvent(rawEvent);
                var severityScore = rawEvent.SeverityScore;
                // Same sanitization gate as EmitSyntheticFinding so the
                // polled-detector path and the hook path are guaranteed to
                // emit identical privacy-scrubbed strings to the consumer.
                var description = SanitizeDescription(rawEvent.Description ?? string.Empty);

                mapped.Add(new DetectorFinding(rawEvent.EventType, severityScore, description));
            }

            return mapped;
        }
    }

    internal sealed class DetectorRuntimeOptions
    {
        public bool EnableFocusDetection { get; set; }
        public bool EnableClipboardMonitoring { get; set; }
        public bool EnableIdleDetection { get; set; }
        public int IdleThresholdSeconds { get; set; }
        public bool EnableProcessDetection { get; set; }
        public bool EnableVirtualizationCheck { get; set; }
        public bool StrictMode { get; set; }
        // REQUIRED — LMS exam URL for anchored focus detection (per-room).
        public string LmsExamUrl { get; set; } = string.Empty;
        public HashSet<string> BlacklistedProcessNames { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // OPTIONAL — raw "Allowed Apps During Exam" CSV from the
        // instructor's session-setup. Split by SacDetectorRuntime into
        // process-name tokens and browser-title domain tokens before
        // being handed to BehavioralMonitoringService. Empty / null
        // means the feature is disabled and behaviour is unchanged.
        public string AllowedAppsCsv { get; set; } = string.Empty;
        public Func<bool, bool, Task> OnHardwareStateDetected { get; set; }
        public Action<DetectorFinding> OnPreFlightViolationDetected { get; set; }
    }

    internal sealed class DetectorFinding
    {
        public DetectorFinding(string eventType, int severityScore, string description)
        {
            EventType = eventType;
            SeverityScore = severityScore;
            Description = description;
        }

        public string EventType { get; }
        public int SeverityScore { get; }
        public string Description { get; }
    }
}
