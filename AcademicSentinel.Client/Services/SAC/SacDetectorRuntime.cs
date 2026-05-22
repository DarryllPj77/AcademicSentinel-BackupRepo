using System;
using System.Collections.Generic;
using System.Linq;
using AcademicSentinel.Client.Services.SAC.DetectionService;
using AcademicSentinel.Client.Services.SAC.Models;

namespace AcademicSentinel.Client.Services.SAC
{
    internal sealed class SacDetectorRuntime : IDisposable
    {
        private readonly DetectorRuntimeOptions _options;
        private readonly BehavioralMonitoringService _behavioralMonitoringService;
        private readonly EnvironmentIntegrityService _environmentIntegrityService;
        private readonly DecisionEngineService _decisionEngineService;
        private readonly KeyboardHookService _keyboardHookService;
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
                LmsExamUrl = _options.LmsExamUrl
            };

            _behavioralMonitoringService = new BehavioralMonitoringService(settings, _options.BlacklistedProcessNames);
            _environmentIntegrityService = new EnvironmentIntegrityService();
            _decisionEngineService = new DecisionEngineService(_options.StrictMode);
            _keyboardHookService = new KeyboardHookService();
            _keyboardHookService.ScreenshotKeyDetected += OnScreenshotKeyDetected;
            _keyboardHookService.SnippingToolComboDetected += OnSnippingToolComboDetected;

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

            var assessment = _decisionEngineService.EvaluateEvent(rawEvent);
            var description = $"{rawEvent.Description} | CumulativeScore={assessment.CurrentScore}; RiskLevel={assessment.CurrentLevel}";
            var finding = new DetectorFinding(rawEvent.EventType, rawEvent.SeverityScore, description);

            // Reuse the preflight callback as a generic "out-of-band finding"
            // channel — the SAC window already routes that to ReportViolationAsync.
            _options.OnPreFlightViolationDetected?.Invoke(finding);
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
            _hardwareSoftwareArtifactService.Stop();
        }

        public void Dispose()
        {
            if (_isDisposed)
                return;

            _isDisposed = true;
            try { Stop(); } catch { }
            try { _keyboardHookService.Dispose(); } catch { }
            try { _hardwareSoftwareArtifactService.Dispose(); } catch { }

            // Detach the option callbacks so the captured closures (which hold a
            // reference to the SAC window) cannot fire after disposal.
            _options.OnHardwareStateDetected = null;
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
                var description = string.IsNullOrWhiteSpace(rawEvent.Description)
                    ? $"CumulativeScore={assessment.CurrentScore}; RiskLevel={assessment.CurrentLevel}"
                    : $"{rawEvent.Description} | CumulativeScore={assessment.CurrentScore}; RiskLevel={assessment.CurrentLevel}";

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
