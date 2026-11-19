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
        public const string EventClipboardPaste = "CLIPBOARD_PASTE";        // Ctrl+V detected by the WH_KEYBOARD_LL hook
        public const string EventRightClickContextMenu = "RIGHT_CLICK_CONTEXT"; // WM_RBUTTONUP — potential paste vector via context menu
        public const string EventScreenshot = "SCREENSHOT";
        public const string EventPrintScreen = "PRINTSCREEN";
        public const string EventSnipTool = "SNIP_TOOL";
        public const string EventIdle = "IDLE";              // S1 — warning tier
        public const string EventInactivity = "INACTIVITY";  // S2 / S4 — violation / critical tiers
        public const string EventProcessDetected = "PROCESS_DETECTED";
        // Phase 3 — Environment integrity
        public const string EventMultiMonitor = "MULTI_MONITOR";   // S3 — student has > 1 display attached
        // HAS sub-events
        public const string EventHasDebugger = "HAS_DEBUGGER";
        public const string EventHasTimeTamper = "HAS_TIME_TAMPER";
        public const string EventHasClockDrift = "HAS_CLOCK_DRIFT";
        // LMS-anchored focus detection
        public const string EventCanvasNotFound  = "CANVAS_NOT_FOUND";   // S1 = 10 — LMS window not open at session start
        public const string EventCanvasFocusLost = "CANVAS_FOCUS_LOST";  // S2 = 20 — focus moved away from LMS browser window
        public const string EventCanvasClosed    = "CANVAS_CLOSED";      // S4 = 40 — LMS browser window closed mid-session
        public const string EventCanvasReturned  = "CANVAS_RETURNED";    // 0  pts — informational: student returned focus to LMS

        // Allowed-apps allowlist (per-session, teacher-configured).
        // Fires INSTEAD of WINDOW_SWITCH when the student tabs to an
        // app the instructor explicitly permitted. 0 points so it
        // never inflates the cumulative risk score; both the SAC log
        // and the IMC log render it as an informational entry.
        public const string EventAllowedApp      = "ALLOWED_APP";        // 0  pts — informational: student switched to an instructor-allowed app
    }
}
