using System;
using System.Collections.Generic;

namespace AcademicSentinel.Client.Services.SAC.Models
{
    internal sealed class DetectionSettings
    {
        public bool EnableClipboardMonitoring { get; set; }
        public bool EnableProcessDetection { get; set; }
        public bool EnableIdleDetection { get; set; }
        public bool EnableFocusDetection { get; set; }

        public int IdleWarningThresholdSeconds { get; set; } = 30;
        public int IdleViolationThresholdSeconds { get; set; } = 120;
        public int IdleCriticalThresholdSeconds { get; set; } = 300;

        // REQUIRED — LMS exam URL anchored focus detection.
        // Always populated from the server's RoomDetectionSettings; no
        // default value is provided so a misconfigured room is loud
        // rather than silently degrading to "no anchor". An empty value
        // means the SAC will not enforce LMS anchoring (legacy behaviour).
        public string LmsExamUrl { get; set; }

        // OPTIONAL — instructor-defined per-session allowlist. Tokens
        // populated by SacDetectorRuntime from the server's
        // AllowedAppsCsv (split + canonicalised). Process tokens
        // (no ".") match Process.ProcessName of the foreground window;
        // domain tokens (containing ".") match a case-insensitive
        // substring of the foreground BROWSER window's title. Empty
        // sets disable the feature entirely.
        public HashSet<string> AllowedAppProcessNames { get; set; }
            = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> AllowedAppTitleKeywords { get; set; }
            = new(StringComparer.OrdinalIgnoreCase);
    }
}
