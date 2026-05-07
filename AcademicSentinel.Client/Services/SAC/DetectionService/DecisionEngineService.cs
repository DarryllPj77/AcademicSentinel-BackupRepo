using AcademicSentinel.Client.Services.SAC.Models;
using System;

namespace AcademicSentinel.Client.Services.SAC.DetectionService
{
    public class DecisionEngineService
    {
        private readonly object _syncRoot = new();
        private int _cumulativeScore = 0;
        private int _passiveEventCount = 0;
        private RiskLevel _currentLevel = RiskLevel.Safe;

        public RiskAssessment EvaluateEvent(MonitoringDetectionEvent newEvent)
        {
            if (newEvent == null)
                throw new ArgumentNullException(nameof(newEvent));

            lock (_syncRoot)
            {
                var normalized = (newEvent.EventType ?? string.Empty).Trim().ToUpperInvariant();

                // Spec-compliant scoring (BRBDE):
                //   S1 single passive       = 10  (RTFM, IDLE, single CSAD)
                //   S2 repeated passive     = 20  (3+ CSAD/RTFM events — handled by repeat counter below)
                //   S3 aggressive           = 50  (PBD, VAC, HAS, REMOTE — every occurrence)
                switch (normalized)
                {
                    case "RTFM":
                    case "ALT_TAB":
                    case "WINDOW_SWITCH":
                    case "FOCUS":
                        newEvent.SeverityScore = 10;
                        break;
                    case "IDLE":
                    case "INACTIVITY":
                        newEvent.SeverityScore = 10;
                        break;
                    case "CSAD":
                    case "CLIPBOARD":
                    case "CLIPBOARD_COPY":
                    case "CLIPBOARD_PASTE":
                    case "COPY":
                    case "PASTE":
                    case "SCREENSHOT":
                    case "PRINTSCREEN":
                        // S1 passive on first hit; the engine bumps to S2 (20) once
                        // we've recorded 3+ passive events of any type — see below.
                        newEvent.SeverityScore = 10;
                        break;
                    case "PBD":
                    case "PROCESS":
                    case "PROCESS_DETECTED":
                        // PBD is ALWAYS aggressive per spec.
                        newEvent.SeverityScore = 50;
                        break;
                    case "VAC":
                    case "HAS":
                    case "VAC_HAS_VIOLATION":
                    case "VM":
                    case "REMOTE":
                        newEvent.SeverityScore = 50;
                        break;
                    default:
                        if (normalized.Contains("RTFM") || normalized.Contains("ALT_TAB") || normalized.Contains("WINDOW_SWITCH") || normalized.Contains("FOCUS"))
                            newEvent.SeverityScore = 10;
                        else if (normalized.Contains("IDLE") || normalized.Contains("INACTIVITY"))
                            newEvent.SeverityScore = 10;
                        else if (normalized.Contains("CSAD") || normalized.Contains("CLIPBOARD") || normalized.Contains("COPY") || normalized.Contains("PASTE") || normalized.Contains("SCREENSHOT") || normalized.Contains("PRINTSCREEN"))
                            newEvent.SeverityScore = 10;
                        else if (normalized.Contains("PBD") || normalized.Contains("PROCESS"))
                            newEvent.SeverityScore = 50;
                        else if (normalized.Contains("VAC") || normalized.Contains("HAS") || normalized.Contains("VM") || normalized.Contains("REMOTE"))
                            newEvent.SeverityScore = 50;
                        else
                            newEvent.SeverityScore = 10;
                        break;
                }

                // S1 → S2 escalation: once we've seen 3+ passive events in the
                // session, every subsequent passive event scores 20 instead of 10.
                bool isPassive = newEvent.SeverityScore == 10;
                if (isPassive)
                {
                    _passiveEventCount++;
                    if (_passiveEventCount >= 3)
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

        private static RiskLevel ResolveRiskLevel(int cumulativeScore)
        {
            if (cumulativeScore < 20)
                return RiskLevel.Safe;

            if (cumulativeScore < 50)
                return RiskLevel.Suspicious;

            return RiskLevel.Cheating;
        }
    }
}
