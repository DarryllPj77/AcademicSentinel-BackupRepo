using System;
using System.Collections.Generic;
using System.Linq;
using AcademicSentinel.Client.Services.SAC.DetectionService;
using AcademicSentinel.Client.Services.SAC.Models;

namespace AcademicSentinel.Client.Services.SAC
{
    internal sealed class SacDetectorRuntime
    {
        private readonly DetectorRuntimeOptions _options;
        private readonly BehavioralMonitoringService _behavioralMonitoringService;
        private readonly EnvironmentIntegrityService _environmentIntegrityService;
        private readonly DecisionEngineService _decisionEngineService;
        private bool _isStarted;
        private bool _preFlightCompleted;
        public bool IsPaused { get; set; } = false;
        public bool IsLoggingEnabled { get; private set; } = true;

        public SacDetectorRuntime(DetectorRuntimeOptions options)
        {
            _options = options;

            int idleViolation = Math.Max(1, _options.IdleThresholdSeconds);

            var settings = new DetectionSettings
            {
                EnableFocusDetection = _options.EnableFocusDetection,
                EnableClipboardMonitoring = _options.EnableClipboardMonitoring,
                EnableIdleDetection = _options.EnableIdleDetection,
                EnableProcessDetection = _options.EnableProcessDetection,
                IdleWarningThresholdSeconds = Math.Max(10, idleViolation / 2),
                IdleViolationThresholdSeconds = idleViolation,
                IdleCriticalThresholdSeconds = Math.Max(idleViolation + 10, idleViolation * 2)
            };

            _behavioralMonitoringService = new BehavioralMonitoringService(settings, _options.BlacklistedProcessNames);
            _environmentIntegrityService = new EnvironmentIntegrityService();
            _decisionEngineService = new DecisionEngineService();
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

                if (!_preFlightCompleted)
                {
                    _preFlightCompleted = true;

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
                }

                return;
            }

            if (!_isStarted)
                return;

            _isStarted = false;
            IsLoggingEnabled = false;
            _behavioralMonitoringService.StopMonitoring();
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
            await Task.CompletedTask;
        }

        public void Stop()
        {
            IsLoggingEnabled = false;

            if (!_isStarted)
                return;

            _isStarted = false;
            _behavioralMonitoringService.StopMonitoring();
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
