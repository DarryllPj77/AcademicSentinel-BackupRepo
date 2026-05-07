namespace AcademicSentinel.Client.Services.SAC.Utilities
{
    internal static class DetectionConstants
    {
        public const string EventAltTab = "ALT_TAB";
        public const string EventWindowSwitch = "WINDOW_SWITCH";
        public const string EventFocusLost = "FOCUS_LOST";
        public const string EventFocusRate = "RTFM_RATE";           // 3+ focus losses in 60s (aggressive)
        public const string EventFocusSustained = "RTFM_SUSTAINED"; // focus loss > 10s sustained (aggressive)
        public const string EventClipboardCopy = "CLIPBOARD_COPY";
        public const string EventClipboardPaste = "CLIPBOARD_PASTE";
        public const string EventScreenshot = "SCREENSHOT";
        public const string EventPrintScreen = "PRINTSCREEN";
        public const string EventSnipTool = "SNIP_TOOL";
        public const string EventIdle = "IDLE";
        public const string EventProcessDetected = "PROCESS_DETECTED";
        // HAS sub-events
        public const string EventHasDebugger = "HAS_DEBUGGER";
        public const string EventHasTimeTamper = "HAS_TIME_TAMPER";
        public const string EventHasClockDrift = "HAS_CLOCK_DRIFT";
    }
}
