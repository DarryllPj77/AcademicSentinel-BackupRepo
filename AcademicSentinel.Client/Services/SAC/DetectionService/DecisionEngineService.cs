using AcademicSentinel.Client.Services.SAC.Models;
using System;
using System.Collections.Generic;

namespace AcademicSentinel.Client.Services.SAC.DetectionService
{
    /// <summary>
    /// Centralised scoring engine.
    ///
    /// Fixed-severity policy (no per-event escalation):
    ///   * Passive   = 10 pts  → IDLE / INACTIVITY, CSAD family (clipboard /
    ///                            screenshot / snip), RTFM / WINDOW_SWITCH /
    ///                            ALT_TAB / FOCUS, CANVAS focus events.
    ///   * Aggressive = 30 pts → PBD / PROCESS_DETECTED, VAC (virtualization),
    ///                            HAS family (hardware/software scan),
    ///                            CANVAS_CLOSED, REMOTE.
    ///   * Informational = 0 pts → CANVAS_RETURNED, anything explicitly
    ///                              flagged by the caller as zero-score.
    ///
    /// The engine still maintains a per-student running total and resolves
    /// the risk band against the new thresholds:
    ///   Safe       (cumulative &lt;   20)
    ///   Suspicious (cumulative 20..49)
    ///   Cheating   (cumulative &gt;= 50)
    /// </summary>
    public class DecisionEngineService
    {
        private readonly object _syncRoot = new();
        private int _cumulativeScore = 0;
        private RiskLevel _currentLevel = RiskLevel.Safe;

        // Strict-mode flag retained as a constructor argument so existing
        // callers compile, but it no longer has any effect — per-event
        // escalation was removed by request.
        public DecisionEngineService(bool strictMode = false) { /* strictMode ignored */ }

        // -------------------------------------------------------------
        // Fixed-severity lookup tables. Adding a new event type here is
        // the only place a future severity needs to be wired in.
        // -------------------------------------------------------------

        private const int PassivePoints = 10;
        private const int AggressivePoints = 30;

        private static readonly HashSet<string> _passiveEventTypes =
            new(StringComparer.OrdinalIgnoreCase)
        {
            // RTFM family — window/focus switches
            "RTFM", "ALT_TAB", "WINDOW_SWITCH", "FOCUS", "FOCUS_LOST",
            "CANVAS_NOT_FOUND", "CANVAS_FOCUS_LOST",
            // IDLE family
            "IDLE", "INACTIVITY",
            // CSAD family — clipboard / screenshot / snipping
            "CSAD", "CLIPBOARD", "CLIPBOARD_COPY", "CLIPBOARD_PASTE",
            "COPY", "PASTE",
            "SCREENSHOT", "PRINTSCREEN", "SNIP_TOOL",
            // Clock-drift mid-tier downgraded to passive — possible legit NTP skew.
            "HAS_CLOCK_DRIFT"
        };

        private static readonly HashSet<string> _aggressiveEventTypes =
            new(StringComparer.OrdinalIgnoreCase)
        {
            // PBD family — unauthorized processes
            "PBD", "PROCESS", "PROCESS_DETECTED",
            // VAC — virtualization / emulator
            "VAC", "VM", "VAC_HAS_VIOLATION",
            // HAS family — hardware/software scan
            "HAS", "HAS_DEBUGGER", "HAS_TIME_TAMPER",
            // Remote-desktop session
            "REMOTE", "REMOTE_DESKTOP_DETECTED",
            // Canvas exam window torn down mid-test
            "CANVAS_CLOSED"
        };

        // Zero-score informational events — surfaced in the feed for context
        // but never add to CumulativeScore. ALLOWED_APP belongs here because
        // the instructor explicitly permitted the target app for the session
        // (per-room allowlist); without this entry the soft-match fallback
        // bumps it to passive 10 pts and the SAC softlock UI mis-renders it
        // as "Violation sent:" instead of the informational "Switch
        // detected:" wording.
        private static readonly HashSet<string> _informationalEventTypes =
            new(StringComparer.OrdinalIgnoreCase)
        {
            "CANVAS_RETURNED",
            "ALLOWED_APP"
        };

        public RiskAssessment EvaluateEvent(MonitoringDetectionEvent newEvent)
        {
            if (newEvent == null)
                throw new ArgumentNullException(nameof(newEvent));

            lock (_syncRoot)
            {
                var normalized = (newEvent.EventType ?? string.Empty).Trim().ToUpperInvariant();

                // -------------------------------------------------------------
                // Fixed-severity assignment. No escalation, no per-call tier
                // recomputation — every occurrence of a given event type adds
                // the same number of points to the cumulative score.
                // -------------------------------------------------------------
                if (_informationalEventTypes.Contains(normalized))
                {
                    newEvent.SeverityScore = 0;
                }
                else if (_aggressiveEventTypes.Contains(normalized))
                {
                    newEvent.SeverityScore = AggressivePoints;
                }
                else if (_passiveEventTypes.Contains(normalized))
                {
                    newEvent.SeverityScore = PassivePoints;
                }
                else
                {
                    // Soft-match fallback for legacy / un-mapped event names.
                    // Same bucketing rules as the explicit lists above.
                    if (normalized.Contains("RTFM") || normalized.Contains("ALT_TAB")
                        || normalized.Contains("WINDOW_SWITCH") || normalized.Contains("FOCUS")
                        || normalized.Contains("IDLE") || normalized.Contains("INACTIVITY")
                        || normalized.Contains("CSAD") || normalized.Contains("CLIPBOARD")
                        || normalized.Contains("COPY") || normalized.Contains("PASTE")
                        || normalized.Contains("SCREENSHOT") || normalized.Contains("PRINTSCREEN"))
                    {
                        newEvent.SeverityScore = PassivePoints;
                    }
                    else if (normalized.Contains("PBD") || normalized.Contains("PROCESS")
                             || normalized.Contains("VAC") || normalized.Contains("HAS")
                             || normalized.Contains("VM") || normalized.Contains("REMOTE"))
                    {
                        newEvent.SeverityScore = AggressivePoints;
                    }
                    else
                    {
                        // Unknown event — treat as passive to keep behavior
                        // safe-by-default rather than zero-scoring something
                        // that should have been a violation.
                        newEvent.SeverityScore = PassivePoints;
                    }
                }

                // -------------------------------------------------------------
                // Cumulative tracking. SacDetectorRuntime appends the suffix
                //   | CumulativeScore={CurrentScore}; RiskLevel={CurrentLevel}
                // onto the event Description using the returned assessment.
                // -------------------------------------------------------------
                _cumulativeScore += Math.Max(0, newEvent.SeverityScore);

                var previousLevel = _currentLevel;
                _currentLevel = ResolveRiskLevel(_cumulativeScore);

                return new RiskAssessment
                {
                    CurrentScore = _cumulativeScore,
                    CurrentLevel = _currentLevel,
                    HasThresholdChanged = previousLevel != _currentLevel
                };
            }
        }

        /// <summary>
        /// Maps the cumulative score onto a risk band.
        ///   Safe       — score &lt;  20
        ///   Suspicious — 20 ≤ score &lt; 50
        ///   Cheating   — score ≥ 50
        /// </summary>
        private static RiskLevel ResolveRiskLevel(int cumulativeScore)
        {
            if (cumulativeScore < 20) return RiskLevel.Safe;
            if (cumulativeScore < 50) return RiskLevel.Suspicious;
            return RiskLevel.Cheating;
        }
    }
}
