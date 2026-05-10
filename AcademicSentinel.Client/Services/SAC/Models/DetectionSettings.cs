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
    }
}
