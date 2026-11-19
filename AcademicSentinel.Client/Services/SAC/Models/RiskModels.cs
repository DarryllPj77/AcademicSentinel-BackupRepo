namespace AcademicSentinel.Client.Services.SAC.Models
{
    // Renamed from `Cheating` to `PossibleDishonesty` per defense-panel
    // guidance — the monitoring system must NOT make a definitive
    // accusation. The enum identifier itself is also the C# token used
    // throughout the codebase, so renaming it here cascades through
    // every consumer at compile time.
    public enum RiskLevel
    {
        Safe = 0,
        Suspicious = 1,
        PossibleDishonesty = 2
    }

    public sealed class RiskAssessment
    {
        public int CurrentScore { get; set; }
        public RiskLevel CurrentLevel { get; set; }
        public bool HasThresholdChanged { get; set; }
    }

    /// <summary>
    /// Canonical display strings + a legacy-aware normalizer for any
    /// raw <c>RiskLevel</c> string coming from the database, the server
    /// API, or older client builds. Old rows stored as
    /// <c>"Cheating"</c> / <c>"CHEATING"</c> are mapped to the
    /// panel-approved wording <c>"Possible Dishonesty"</c> /
    /// <c>"POSSIBLE DISHONESTY"</c> at display time. Storage layers
    /// are still allowed to hold legacy values; the normalizer is the
    /// single read-side conversion point.
    /// </summary>
    public static class RiskLevelDisplay
    {
        public const string Safe               = "Safe";
        public const string Suspicious         = "Suspicious";
        public const string PossibleDishonesty = "Possible Dishonesty";

        public const string SafeUpper               = "SAFE";
        public const string SuspiciousUpper         = "SUSPICIOUS";
        public const string PossibleDishonestyUpper = "POSSIBLE DISHONESTY";

        /// <summary>Returns the display string for an in-process enum value.</summary>
        public static string Format(RiskLevel level, bool upper = false) => level switch
        {
            RiskLevel.PossibleDishonesty => upper ? PossibleDishonestyUpper : PossibleDishonesty,
            RiskLevel.Suspicious         => upper ? SuspiciousUpper         : Suspicious,
            _                            => upper ? SafeUpper               : Safe,
        };

        /// <summary>
        /// Re-maps a raw risk-level string (any casing, possibly the legacy
        /// "Cheating" / "CHEATING") onto the panel-approved wording while
        /// preserving the caller's existing case style. Unknown values pass
        /// through unchanged so callers can still surface server data they
        /// don't recognize.
        /// </summary>
        public static string Normalize(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

            var trimmed = raw.Trim();
            if (string.Equals(trimmed, "Cheating", System.StringComparison.OrdinalIgnoreCase))
            {
                // Preserve case style: ALL CAPS → upper variant; otherwise mixed.
                return IsUpper(trimmed) ? PossibleDishonestyUpper : PossibleDishonesty;
            }
            return trimmed;
        }

        private static bool IsUpper(string s)
        {
            foreach (var c in s)
            {
                if (char.IsLetter(c) && !char.IsUpper(c)) return false;
            }
            return true;
        }
    }
}
