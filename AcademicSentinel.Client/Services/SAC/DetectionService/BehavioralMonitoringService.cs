using AcademicSentinel.Client.Services.SAC.Models;
using AcademicSentinel.Client.Services.SAC.Utilities;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using Microsoft.Win32;

namespace AcademicSentinel.Client.Services.SAC.DetectionService
{
    internal sealed class BehavioralMonitoringService
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern uint GetClipboardSequenceNumber();

        [DllImport("user32.dll")]
        private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        [DllImport("kernel32.dll")]
        private static extern uint GetTickCount();

        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        private const int VK_CONTROL = 0x11;
        private const int VK_C = 0x43;
        private const int VK_V = 0x56;

        private static readonly string[] _blacklistedApps =
        {
            "discord",
            "obs32",
            "taskmgr",
            "processhacker",
            "procmon",
            "procexp",
            "windbg",
            "x64dbg",
            "ollydbg",
            "dnspy",
            "ida",
            "fiddler",
            "cheatengine",
            "teamviewer",
            "anydesk",
            "ultravnc",
            "gotomypc"
        };

        private readonly DetectionSettings _settings;
        private readonly HashSet<string> _blacklistedProcessNames;
        private readonly Dictionary<string, DateTime> _lastReportedAtByEvent = new(StringComparer.OrdinalIgnoreCase);

        private IntPtr _lastForegroundWindow;
        private bool _lastForegroundWasSac;
        private string _lastWindowName = "Unknown";
        private IntPtr _temporarilyExemptWindow;
        private DateTime _lastProcessScanAt = DateTime.MinValue;
        private HashSet<string> _lastReportedProcesses = new(StringComparer.OrdinalIgnoreCase);

        // RTFM rate-window: spec says 3+ focus-loss events within 60 seconds
        // escalates from passive to aggressive. Track each loss timestamp.
        private readonly Queue<DateTime> _focusLossTimestamps = new();
        private const int FocusLossWindowSeconds = 60;
        private const int FocusLossEscalationCount = 3;

        // RTFM sustained-loss timer: spec says focus loss > 10 seconds is its
        // own aggressive trigger. Track when SAC lost the foreground.
        private DateTime? _focusLostAtUtc;
        private bool _sustainedLossReportedThisLoss;
        private const int SustainedFocusLossSeconds = 10;

        private bool _copyDown;
        private bool _pasteDown;
        private uint _lastClipboardSequenceNumber;

        private int _lastReportedIdleLevel;
        private bool _isMonitoring;
        private DateTime _monitoringStartedAtUtc;

        public BehavioralMonitoringService(DetectionSettings settings, IEnumerable<string> blacklistedProcessNames)
        {
            _settings = settings ?? new DetectionSettings();
            _blacklistedProcessNames = blacklistedProcessNames?.ToHashSet(StringComparer.OrdinalIgnoreCase)
                ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _lastForegroundWindow = GetForegroundWindow();
            _lastClipboardSequenceNumber = GetClipboardSequenceNumber();
        }

        public void StartMonitoring()
        {
            DisableTaskManager();
            _isMonitoring = true;
            _lastForegroundWindow = GetForegroundWindow();
            _lastWindowName = GetWindowName(_lastForegroundWindow);
            _lastForegroundWasSac = false;
            _temporarilyExemptWindow = IntPtr.Zero;
            _lastReportedIdleLevel = 0;
            _monitoringStartedAtUtc = DateTime.UtcNow;
            _lastClipboardSequenceNumber = GetClipboardSequenceNumber();
            _lastProcessScanAt = DateTime.MinValue;
            _lastReportedProcesses.Clear();
            _lastReportedAtByEvent.Clear();
            _copyDown = false;
            _pasteDown = false;
            _focusLossTimestamps.Clear();
            _focusLostAtUtc = null;
            _sustainedLossReportedThisLoss = false;
        }

        public void StopMonitoring()
        {
            EnableTaskManager();
            _isMonitoring = false;
            _copyDown = false;
            _pasteDown = false;
            _temporarilyExemptWindow = IntPtr.Zero;
            _lastReportedIdleLevel = 0;
            _lastReportedProcesses.Clear();
            _lastReportedAtByEvent.Clear();
        }

        public IReadOnlyList<MonitoringDetectionEvent> Poll(bool isSacWindowActive)
        {
            if (!_isMonitoring)
                return Array.Empty<MonitoringDetectionEvent>();

            var findings = new List<MonitoringDetectionEvent>();

            DetectFocus(isSacWindowActive, findings);
            DetectClipboardAndScreenshot(findings);
            DetectIdle(findings);
            ScanAndHandleBlacklistedProcesses(findings);

            _lastForegroundWasSac = isSacWindowActive;

            return findings;
        }

        private void DetectFocus(bool isSacWindowActive, ICollection<MonitoringDetectionEvent> findings)
        {
            if (!_settings.EnableFocusDetection)
                return;

            // RTFM sustained-loss check disabled per QA feedback — the rate
            // window (3 in 60s) is sufficient and the sustained-loss event
            // was firing too noisily for normal exam workflow.
            // EvaluateSustainedFocusLoss(isSacWindowActive, findings);

            var foreground = GetForegroundWindow();
            if (foreground != _lastForegroundWindow)
            {
                IntPtr previousForeground = _lastForegroundWindow;
                bool previousWasSac = _lastForegroundWasSac;
                string previous = _lastWindowName;
                string current = GetWindowName(foreground);

                if (isSacWindowActive && !previousWasSac)
                {
                    _temporarilyExemptWindow = previousForeground;
                }
                else if (!isSacWindowActive && previousWasSac)
                {
                    bool isReturningToExemptWindow = _temporarilyExemptWindow != IntPtr.Zero && foreground == _temporarilyExemptWindow;
                    if (isReturningToExemptWindow)
                    {
                        ClearTemporaryExemptWindow();
                    }
                    else
                    {
                        AddEvent(findings, DetectionConstants.EventWindowSwitch, 2,
                            $"Window switched from '{previous}' to '{current}' while monitoring is active.", 0);
                        RecordFocusLoss(findings);
                        ClearTemporaryExemptWindow();
                    }
                }
                else if (!isSacWindowActive)
                {
                    AddEvent(findings, DetectionConstants.EventWindowSwitch, 2,
                        $"Window switched from '{previous}' to '{current}' while monitoring is active.", 0);
                    RecordFocusLoss(findings);
                    ClearTemporaryExemptWindow();
                }

                _lastForegroundWindow = foreground;
                _lastWindowName = current;
                _lastForegroundWasSac = isSacWindowActive;
            }
        }

        // Add a focus-loss timestamp to the rolling 60-second window. If 3+
        // losses occurred in the past 60s, emit an aggressive RTFM_RATE event
        // (spec: "Focus lost 3 times within 60 seconds").
        private void RecordFocusLoss(ICollection<MonitoringDetectionEvent> findings)
        {
            var now = DateTime.UtcNow;
            _focusLossTimestamps.Enqueue(now);

            // Drop entries older than the window so the queue size = lossesInWindow.
            while (_focusLossTimestamps.Count > 0
                   && (now - _focusLossTimestamps.Peek()).TotalSeconds > FocusLossWindowSeconds)
            {
                _focusLossTimestamps.Dequeue();
            }

            if (_focusLossTimestamps.Count >= FocusLossEscalationCount)
            {
                AddEvent(findings, DetectionConstants.EventFocusRate, 5,
                    $"Repeated focus losses: {_focusLossTimestamps.Count} within {FocusLossWindowSeconds}s window.",
                    cooldownSeconds: 30);

                // Reset the window so the next breach requires a fresh batch.
                _focusLossTimestamps.Clear();
            }
        }

        private void EvaluateSustainedFocusLoss(bool isSacWindowActive, ICollection<MonitoringDetectionEvent> findings)
        {
            if (isSacWindowActive)
            {
                _focusLostAtUtc = null;
                _sustainedLossReportedThisLoss = false;
                return;
            }

            if (!_focusLostAtUtc.HasValue)
            {
                _focusLostAtUtc = DateTime.UtcNow;
                _sustainedLossReportedThisLoss = false;
                return;
            }

            if (_sustainedLossReportedThisLoss)
                return;

            var elapsed = (DateTime.UtcNow - _focusLostAtUtc.Value).TotalSeconds;
            if (elapsed >= SustainedFocusLossSeconds)
            {
                _sustainedLossReportedThisLoss = true;
                AddEvent(findings, DetectionConstants.EventFocusSustained, 5,
                    $"Focus has been outside SAC for {(int)elapsed} seconds (threshold {SustainedFocusLossSeconds}s).",
                    cooldownSeconds: 30);
            }
        }

        private void ClearTemporaryExemptWindow()
        {
            _temporarilyExemptWindow = IntPtr.Zero;
        }

        private void DetectClipboardAndScreenshot(ICollection<MonitoringDetectionEvent> findings)
        {
            if (!_settings.EnableClipboardMonitoring)
                return;

            uint currentSequence = GetClipboardSequenceNumber();
            bool clipboardChanged = currentSequence != _lastClipboardSequenceNumber;
            if (clipboardChanged)
            {
                _lastClipboardSequenceNumber = currentSequence;

                if (TryClipboardContainsImage())
                {
                    AddEvent(findings, DetectionConstants.EventScreenshot, 3,
                        "Screenshot or image capture detected in clipboard while monitoring is active.", 1);
                }
                else
                {
                    AddEvent(findings, DetectionConstants.EventClipboardCopy, 2,
                        "Clipboard content changed while monitoring is active.", 1);
                }
            }

            bool ctrlPressed = IsKeyDown(VK_CONTROL);

            bool copyPressed = ctrlPressed && IsKeyDown(VK_C);
            if (copyPressed && !_copyDown)
            {
                AddEvent(findings, DetectionConstants.EventClipboardCopy, 2,
                    "Copy command (Ctrl+C) detected while monitoring is active.", 2);
            }
            _copyDown = copyPressed;

            bool pastePressed = ctrlPressed && IsKeyDown(VK_V);
            if (pastePressed && !_pasteDown)
            {
                AddEvent(findings, DetectionConstants.EventClipboardPaste, 2,
                    "Paste command (Ctrl+V) detected while monitoring is active.", 2);
            }
            _pasteDown = pastePressed;
        }

        private void DetectIdle(ICollection<MonitoringDetectionEvent> findings)
        {
            if (!_settings.EnableIdleDetection)
                return;

            int idleSecondsFromSystem = GetSystemIdleSeconds();
            int monitoringElapsedSeconds = (int)Math.Max(0, (DateTime.UtcNow - _monitoringStartedAtUtc).TotalSeconds);
            int idleSeconds = Math.Min(idleSecondsFromSystem, monitoringElapsedSeconds);
            int warningThreshold = Math.Max(5, _settings.IdleWarningThresholdSeconds);
            int violationThreshold = Math.Max(warningThreshold + 1, _settings.IdleViolationThresholdSeconds);
            int criticalThreshold = Math.Max(violationThreshold + 1, _settings.IdleCriticalThresholdSeconds);

            if (idleSeconds < warningThreshold)
            {
                _lastReportedIdleLevel = 0;
                return;
            }

            if (idleSeconds >= criticalThreshold && _lastReportedIdleLevel < 3)
            {
                _lastReportedIdleLevel = 3;
                AddEvent(findings, DetectionConstants.EventIdle, 3,
                    $"Critical inactivity detected ({idleSeconds}s). Mouse and keyboard appear idle.", 10);
                return;
            }

            if (idleSeconds >= violationThreshold && _lastReportedIdleLevel < 2)
            {
                _lastReportedIdleLevel = 2;
                AddEvent(findings, DetectionConstants.EventIdle, 2,
                    $"Inactivity detected ({idleSeconds}s). Mouse and keyboard appear idle.", 10);
                return;
            }

            if (idleSeconds >= warningThreshold && _lastReportedIdleLevel < 1)
            {
                _lastReportedIdleLevel = 1;
                AddEvent(findings, DetectionConstants.EventIdle, 1,
                    $"Idle warning: no keyboard/mouse input for {idleSeconds}s.", 10);
            }
        }

        private void ScanAndHandleBlacklistedProcesses(ICollection<MonitoringDetectionEvent> findings)
        {
            if (!_settings.EnableProcessDetection)
                return;

            var now = DateTime.UtcNow;
            if ((now - _lastProcessScanAt).TotalSeconds < 5)
                return;

            _lastProcessScanAt = now;

            var running = Process.GetProcesses()
                .Select(p =>
                {
                    try { return p.ProcessName; }
                    catch { return string.Empty; }
                })
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var detected = _blacklistedApps
                .Concat(_blacklistedProcessNames)
                .Where(running.Contains)
                .OrderBy(p => p)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (detected.Count == 0)
            {
                _lastReportedProcesses.Clear();
                return;
            }

            if (_lastReportedProcesses.SetEquals(detected))
                return;

            _lastReportedProcesses = detected;

            AddEvent(findings, DetectionConstants.EventProcessDetected, 3,
                $"Unauthorized process detected: {string.Join(", ", detected.Take(5))}", 5);
        }

        private void DetectBlacklistedProcesses(ICollection<MonitoringDetectionEvent> findings)
        {
            ScanAndHandleBlacklistedProcesses(findings);
        }

        private static void DisableTaskManager()
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Policies\System", writable: true);
                key?.SetValue("DisableTaskMgr", 1, RegistryValueKind.DWord);
            }
            catch
            {
            }
        }

        private static void EnableTaskManager()
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Policies\System", writable: true);
                key?.SetValue("DisableTaskMgr", 0, RegistryValueKind.DWord);
            }
            catch
            {
            }
        }

        private void AddEvent(ICollection<MonitoringDetectionEvent> findings, string eventType, int severity, string description, int cooldownSeconds)
        {
            var now = DateTime.UtcNow;
            if (_lastReportedAtByEvent.TryGetValue(eventType, out var lastReportedAt) && (now - lastReportedAt).TotalSeconds < cooldownSeconds)
                return;

            _lastReportedAtByEvent[eventType] = now;
            findings.Add(new MonitoringDetectionEvent
            {
                EventType = eventType,
                SeverityScore = severity,
                Description = description,
                Timestamp = now
            });
        }

        private static bool IsKeyDown(int vKey)
        {
            return (GetAsyncKeyState(vKey) & 0x8000) != 0;
        }

        private static bool TryClipboardContainsImage()
        {
            try
            {
                return Clipboard.ContainsImage();
            }
            catch
            {
                return false;
            }
        }

        private static string GetWindowName(IntPtr handle)
        {
            if (handle == IntPtr.Zero)
                return "Unknown";

            var title = new StringBuilder(256);
            if (GetWindowText(handle, title, title.Capacity) <= 0)
                return "Unknown";

            string value = title.ToString().Trim();
            return string.IsNullOrWhiteSpace(value) ? "Unknown" : value;
        }

        private static int GetSystemIdleSeconds()
        {
            LASTINPUTINFO lastInputInfo = new LASTINPUTINFO
            {
                cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>()
            };

            if (!GetLastInputInfo(ref lastInputInfo))
                return 0;

            uint elapsed = GetTickCount() - lastInputInfo.dwTime;
            return (int)(elapsed / 1000);
        }
    }
}
