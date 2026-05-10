using AcademicSentinel.Client.Services.SAC.Models;
using System;

namespace AcademicSentinel.Client.Services.SAC.DetectionService
{
    public class DecisionEngineService
    {
        private readonly object _syncRoot = new();
        private readonly bool _strictMode;
        private int _cumulativeScore = 0;
        private int _passiveEventCount = 0;
        private RiskLevel _currentLevel = RiskLevel.Safe;

        public DecisionEngineService(bool strictMode = false)
        {
            _strictMode = strictMode;
        }

        public RiskAssessment EvaluateEvent(MonitoringDetectionEvent newEvent)
        {
            if (newEvent == null)
                throw new ArgumentNullException(nameof(newEvent));

            lock (_syncRoot)
            {
                var normalized = (newEvent.EventType ?? string.Empty).Trim().ToUpperInvariant();

                // Capture the AddEvent severity tier before the type-driven
                // override below clobbers it. INACTIVITY uses this tier to
                // distinguish violation (2 → 20 pts) from critical (3 → 40 pts).
                int inputTier = newEvent.SeverityScore;

                // Updated BRBDE scoring tiers:
                //   S1 — Focus / Idle warning   = 10 pts  (escalates to 20 after 3rd S1)
                //   S2 — Clipboard / Inactivity = 20 pts
                //   S3 — Process Blacklist      = 30 pts
                //   S4 — VM / HAS / Canvas Closed / Inactivity-Critical = 40 pts
                switch (normalized)
                {
                    // ---- RTFM (S1) ----
                    case "RTFM":
                    case "ALT_TAB":
                    case "WINDOW_SWITCH":
                    case "FOCUS":
                    case "FOCUS_LOST":
                        newEvent.SeverityScore = 10;
                        break;

                    // ---- LMS-anchored focus (RTFM family — S1) ----
                    case "CANVAS_NOT_FOUND":
                    case "CANVAS_FOCUS_LOST":
                        newEvent.SeverityScore = 10;
                        break;

                    // CANVAS_CLOSED is the only Canvas event that escalates
                    // to S4 — the student tore down the exam window mid-test.
                    case "CANVAS_CLOSED":
                        newEvent.SeverityScore = 40;
                        break;

                    // CANVAS_RETURNED is purely informational — the student
                    // came back to the LMS exam window. No score impact, but
                    // the IMC renders it as a positive log entry so the
                    // instructor can see when focus returned to Canvas.
                    case "CANVAS_RETURNED":
                        newEvent.SeverityScore = 0;
                        break;

                    // ---- IDLE (tiered) ----
                    case "IDLE":
                        // Always the warning tier, fixed 10 pts.
                        newEvent.SeverityScore = 10;
                        break;
                    case "INACTIVITY":
                        // Use the input severity to differentiate:
                        //   tier 3 → critical → S4 (40 pts)
                        //   tier 2 → violation → S2 (20 pts)
                        newEvent.SeverityScore = inputTier >= 3 ? 40 : 20;
                        break;

                    // ---- CSAD (S2) ----
                    case "CSAD":
                    case "CLIPBOARD":
                    case "CLIPBOARD_COPY":
                    case "CLIPBOARD_PASTE":
                    case "COPY":
                    case "PASTE":
                    case "SCREENSHOT":
                    case "PRINTSCREEN":
                    case "SNIP_TOOL":
                        newEvent.SeverityScore = 20;
                        break;

                    // ---- PBD (S3 — 30 pts) ----
                    case "PBD":
                    case "PROCESS":
                    case "PROCESS_DETECTED":
                        newEvent.SeverityScore = 30;
                        break;

                    // ---- VAC / HAS (S4 — 40 pts) ----
                    case "VAC":
                    case "HAS":
                    case "VAC_HAS_VIOLATION":
                    case "HAS_DEBUGGER":
                    case "HAS_TIME_TAMPER":
                    case "VM":
                    case "REMOTE":
                    case "REMOTE_DESKTOP_DETECTED":
                        newEvent.SeverityScore = 40;
                        break;

                    case "HAS_CLOCK_DRIFT":
                        // Mid-tier: 30–90s drift may be legitimate NTP skew.
                        // Score as S2 so it surfaces as suspicious without
                        // auto-failing the student.
                        newEvent.SeverityScore = 20;
                        break;

                    default:
                        if (normalized.Contains("RTFM") || normalized.Contains("ALT_TAB") || normalized.Contains("WINDOW_SWITCH") || normalized.Contains("FOCUS"))
                            newEvent.SeverityScore = 10;
                        else if (normalized.Contains("IDLE") || normalized.Contains("INACTIVITY"))
                            newEvent.SeverityScore = 10;
                        else if (normalized.Contains("CSAD") || normalized.Contains("CLIPBOARD") || normalized.Contains("COPY") || normalized.Contains("PASTE") || normalized.Contains("SCREENSHOT") || normalized.Contains("PRINTSCREEN"))
                            newEvent.SeverityScore = 20;
                        else if (normalized.Contains("PBD") || normalized.Contains("PROCESS"))
                            newEvent.SeverityScore = 30;
                        else if (normalized.Contains("VAC") || normalized.Contains("HAS") || normalized.Contains("VM") || normalized.Contains("REMOTE"))
                            newEvent.SeverityScore = 40;
                        else
                            newEvent.SeverityScore = 10;
                        break;
                }

                // S1 → S2 escalation. Normally requires 3 S1 events; in
                // strict mode every S1 event is treated as S2 immediately.
                bool isS1 = newEvent.SeverityScore == 10;
                if (isS1)
                {
                    _passiveEventCount++;
                    if (_strictMode || _passiveEventCount >= 3)
                        newEvent.SeverityScore = 20;
                }

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
        /// Maps the cumulative BRBDE score onto a risk band.
        ///   <see cref="RiskLevel.Safe"/>       — score &lt; 20
        ///   <see cref="RiskLevel.Suspicious"/> — 20 ≤ score &lt; 60
        ///   <see cref="RiskLevel.Cheating"/>   — score ≥ 60
        /// </summary>
        private static RiskLevel ResolveRiskLevel(int cumulativeScore)
        {
            if (cumulativeScore < 20)
                return RiskLevel.Safe;

            if (cumulativeScore < 60)
                return RiskLevel.Suspicious;

            return RiskLevel.Cheating;
        }
    }
}
