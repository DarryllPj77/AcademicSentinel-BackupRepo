using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Windows;
using System.Linq;
using System.Collections.Generic;
using AcademicSentinel.Client.Constants;
using AcademicSentinel.Client.Services;

namespace AcademicSentinel.Client.Views.IMC
{
    public partial class CreateSessionSetupWindow : Window
    {
        private int _currentRoomId;
        public int CreatedSessionId { get; private set; }
        public int MonitoringDurationSeconds { get; private set; } = 3600;
        public bool EndSessionWhenTimerEnds { get; private set; } = true;
        public int StartDelaySeconds { get; private set; } = 10;

        // Cache for the latest extracted LMS domain — used to gate the
        // Save button and to display the "Anchoring to: ..." preview.
        private string _validatedLmsDomain = string.Empty;
        private bool _isLmsUrlValid;

        // UPDATED: Now requires RoomId!
        public CreateSessionSetupWindow(int roomId, string roomTitle)
        {
            InitializeComponent();
            _currentRoomId = roomId;
            ChkIdle_CheckedChanged(this, new RoutedEventArgs());
            ChkEnableMonitoringTimer_CheckedChanged(this, new RoutedEventArgs());

            // Run validation once at startup so the empty field shows the
            // red border + "required" message and the Save button starts
            // disabled rather than appearing valid by default.
            ValidateLmsUrlAndUpdateUi();
        }

        // Real-time validation for the LMS Exam URL field.
        //   - empty       → red border + "LMS Exam URL is required"
        //   - bad URL     → red border + "Please enter a valid HTTPS URL"
        //   - valid URL   → green border + "✓ Anchoring to: <domain>"
        // Save button is disabled unless the URL is valid.
        private void TxtLmsExamUrl_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            ValidateLmsUrlAndUpdateUi();
        }

        private void ValidateLmsUrlAndUpdateUi()
        {
            if (TxtLmsExamUrl == null || LmsUrlBorder == null || TxtLmsUrlValidation == null)
                return;

            var raw = (TxtLmsExamUrl.Text ?? string.Empty).Trim();
            var (isValid, message, domain) = ValidateLmsUrl(raw);

            _isLmsUrlValid = isValid;
            _validatedLmsDomain = domain;

            if (isValid)
            {
                LmsUrlBorder.BorderBrush = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(34, 197, 94));   // green
                TxtLmsUrlValidation.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(22, 101, 52));
                TxtLmsUrlValidation.Text = $"✓ Anchoring to: {domain}";
            }
            else
            {
                LmsUrlBorder.BorderBrush = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(220, 38, 38));   // red
                TxtLmsUrlValidation.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(220, 38, 38));
                TxtLmsUrlValidation.Text = message;
            }

            // Reflect validity on the Save / Continue button.
            if (BtnStartSession != null)
            {
                BtnStartSession.IsEnabled = isValid;
            }
        }

        private static (bool valid, string message, string domain) ValidateLmsUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return (false, "LMS Exam URL is required", string.Empty);

            if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
                return (false, "Please enter a valid HTTPS URL", string.Empty);

            if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                return (false, "Please enter a valid HTTPS URL", string.Empty);

            if (string.IsNullOrWhiteSpace(parsed.Host) || !parsed.Host.Contains('.'))
                return (false, "Please enter a valid HTTPS URL", string.Empty);

            return (true, string.Empty, parsed.Host.ToLowerInvariant());
        }

        // Toggles the Idle Time textbox on and off
        private void ChkIdle_CheckedChanged(object sender, RoutedEventArgs e)
        {
            if (IdleTimePanel != null && ChkIdle != null)
            {
                bool isChecked = ChkIdle.IsChecked == true;
                IdleTimePanel.IsEnabled = isChecked;
                IdleTimePanel.Opacity = isChecked ? 1.0 : 0.5;
            }
        }

        private void ChkEnableMonitoringTimer_CheckedChanged(object sender, RoutedEventArgs e)
        {
            bool enabled = (FindName("ChkEnableMonitoringTimer") as System.Windows.Controls.CheckBox)?.IsChecked == true;

            var monitoringTimerOptions = FindName("MonitoringTimerOptions") as FrameworkElement;
            if (monitoringTimerOptions != null)
            {
                monitoringTimerOptions.IsEnabled = enabled;
                monitoringTimerOptions.Opacity = enabled ? 1.0 : 0.5;
            }

            var stopBehaviorPanel = FindName("MonitoringStopBehaviorPanel") as FrameworkElement;
            if (stopBehaviorPanel != null)
            {
                stopBehaviorPanel.IsEnabled = enabled;
                stopBehaviorPanel.Opacity = enabled ? 1.0 : 0.5;
            }
        }

        private async void BtnStartSession_Click(object sender, RoutedEventArgs e)
        {
            // 0. REQUIRED — LMS Exam URL must be a valid HTTPS URL.
            ValidateLmsUrlAndUpdateUi();
            if (!_isLmsUrlValid)
            {
                MessageBox.Show(
                    "Please enter a valid HTTPS LMS Exam URL before starting the session.",
                    "Missing LMS Exam URL", MessageBoxButton.OK, MessageBoxImage.Warning);
                TxtLmsExamUrl?.Focus();
                return;
            }

            // 1. Validate Idle Time if checked
            int idleSeconds = 0;
            if (ChkIdle.IsChecked == true)
            {
                if (!int.TryParse(TxtIdleSeconds.Text, out idleSeconds) || idleSeconds < 10)
                {
                    MessageBox.Show("Please enter a valid idle threshold in seconds (minimum 10).", "Invalid Input", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            // 2. Package the rules matching your Server's RoomDetectionSettings model
            string selectedExamType = "Summative";
            if (RbMidterm.IsChecked == true) selectedExamType = "Midterm";
            else if (RbFinal.IsChecked == true) selectedExamType = "Final";

            int durationSeconds = 0;
            bool timerEnabled = (FindName("ChkEnableMonitoringTimer") as System.Windows.Controls.CheckBox)?.IsChecked == true;
            if (timerEnabled && !TryGetMonitoringDurationFromUi(out durationSeconds))
            {
                MessageBox.Show("Please enter a valid monitoring duration (hours/minutes/seconds).", "Invalid Duration", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            MonitoringDurationSeconds = timerEnabled ? durationSeconds : 0;
            EndSessionWhenTimerEnds = timerEnabled && GetEndSessionBehaviorFromUi();

            if (!TryGetStartDelaySeconds(out int startDelaySeconds))
            {
                MessageBox.Show("Please enter a valid start delay in seconds (0-120).", "Invalid Start Delay", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            StartDelaySeconds = startDelaySeconds;

            // Allowed Apps During Exam — collect the per-session
            // allowlist. Empty payload when the feature is toggled off
            // or no tokens are selected. Server-side
            // NormaliseAllowedAppsCsv re-canonicalises whatever we
            // send.
            string allowedAppsCsv = ChkAllowedApps.IsChecked == true
                ? BuildAllowedAppsCsvFromUi()
                : string.Empty;

            var settingsPayload = new
            {
                EnableFocusDetection = ChkTabSwitch.IsChecked == true,
                EnableVirtualizationCheck = ChkVirtualMachine.IsChecked == true,
                EnableClipboardMonitoring = ChkClipboard.IsChecked == true,
                EnableProcessDetection = ChkProcess.IsChecked == true,
                EnableIdleDetection = ChkIdle.IsChecked == true,
                IdleThresholdSeconds = idleSeconds,
                StrictMode = ChkStrictMode.IsChecked == true,
                // REQUIRED — LMS Exam URL for anchored focus detection.
                // Server enforces the same validation rules; this is the
                // happy-path payload after client-side validation passed.
                LmsExamUrl = (TxtLmsExamUrl?.Text ?? string.Empty).Trim(),
                AllowedAppsCsv = allowedAppsCsv
            };

            // 3. Send to Server
            BtnStartSession.IsEnabled = false;
            BtnStartSession.Content = "Saving...";

            try
            {
                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", SessionManager.JwtToken);

                    // Call the POST api/rooms/{roomId}/settings endpoint
                    var response = await client.PostAsJsonAsync($"{ApiEndpoints.Rooms}/{_currentRoomId}/settings", settingsPayload);

                    if (response.IsSuccessStatusCode)
                    {
                        // Start session immediately after setup so SAC can join
                        var startResponse = await client.PostAsJsonAsync($"{ApiEndpoints.Rooms}/{_currentRoomId}/start-session", new
                        {
                            ExamType = selectedExamType
                        });

                        if (!startResponse.IsSuccessStatusCode)
                        {
                            string startError = await startResponse.Content.ReadAsStringAsync();
                            MessageBox.Show($"Rules saved, but failed to start session.\n\nServer said: {startError}", "Start Session Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                            BtnStartSession.IsEnabled = true;
                            BtnStartSession.Content = "Save Rules & Continue";
                            return;
                        }

                        var started = await startResponse.Content.ReadFromJsonAsync<StartSessionResponse>();
                        CreatedSessionId = started?.SessionId ?? 0;

                        MessageBox.Show("Session setup saved and session started successfully!", "Setup Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                        this.DialogResult = true; // Tell the previous window it was successful
                        this.Close();
                    }
                    else
                    {
                        string error = await response.Content.ReadAsStringAsync();
                        MessageBox.Show($"Failed to save rules.\n\nServer said: {error}", "API Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                        BtnStartSession.IsEnabled = true;
                        BtnStartSession.Content = "Save Rules & Continue";
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Network Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                BtnStartSession.IsEnabled = true;
                BtnStartSession.Content = "Save Rules & Continue";
            }
        }

        private void BtnBack_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private bool TryGetMonitoringDurationFromUi(out int durationSeconds)
        {
            durationSeconds = 3600;

            try
            {
                if (FindName("TxtDurationHours") is not System.Windows.Controls.TextBox txtHours ||
                    FindName("TxtDurationMinutes") is not System.Windows.Controls.TextBox txtMinutes ||
                    FindName("TxtDurationSeconds") is not System.Windows.Controls.TextBox txtSeconds)
                {
                    return false;
                }

                if (!int.TryParse(txtHours.Text, out int hours) || hours < 0) return false;
                if (!int.TryParse(txtMinutes.Text, out int minutes) || minutes < 0 || minutes > 59) return false;
                if (!int.TryParse(txtSeconds.Text, out int seconds) || seconds < 0 || seconds > 59) return false;

                int totalSeconds = (hours * 3600) + (minutes * 60) + seconds;
                if (totalSeconds < 1) return false;

                durationSeconds = totalSeconds;
                return true;
            }
            catch { }

            return false;
        }

        private bool TryGetStartDelaySeconds(out int delaySeconds)
        {
            delaySeconds = 10;
            try
            {
                if (FindName("TxtStartDelaySeconds") is not System.Windows.Controls.TextBox txtDelay)
                    return false;

                if (!int.TryParse(txtDelay.Text, out var parsed))
                    return false;

                if (parsed < 0 || parsed > 120)
                    return false;

                delaySeconds = parsed;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool GetEndSessionBehaviorFromUi()
        {
            try
            {
                var endNowOption = FindVisualChildren<System.Windows.Controls.RadioButton>(this)
                    .FirstOrDefault(r => string.Equals(r.Content?.ToString(), "End session immediately", StringComparison.OrdinalIgnoreCase));

                return endNowOption?.IsChecked == true;
            }
            catch { }

            return true;
        }

        private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) yield break;

            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T match)
                    yield return match;

                foreach (var descendant in FindVisualChildren<T>(child))
                    yield return descendant;
            }
        }

        public class StartSessionResponse
        {
            public int SessionId { get; set; }
        }

        // ====================================================================
        // ALLOWED APPS DURING EXAM — UI HELPERS
        // ====================================================================

        // ChkAllowedApps enables/disables the entire body; mirrors the
        // ChkIdle / IdleTimePanel pattern already used in this view.
        private void ChkAllowedApps_CheckedChanged(object sender, RoutedEventArgs e)
        {
            if (AllowedAppsBody == null) return;
            bool enabled = ChkAllowedApps?.IsChecked == true;
            AllowedAppsBody.IsEnabled = enabled;
            AllowedAppsBody.Opacity = enabled ? 1.0 : 0.55;
        }

        // Preset checkboxes carry their canonical token list in the Tag
        // property as a comma-separated string. On toggle we splice
        // those tokens in or out of TxtAllowedAppsCustom, so the
        // textbox stays the single source of truth — the user can also
        // type tokens directly and the presets won't fight them.
        private void AllowedAppPreset_CheckedChanged(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.CheckBox cb) return;
            if (cb.Tag is not string tagCsv) return;
            if (TxtAllowedAppsCustom == null) return;

            var presetTokens = ParseTokens(tagCsv);
            var current = ParseTokens(TxtAllowedAppsCustom.Text);

            if (cb.IsChecked == true)
            {
                foreach (var t in presetTokens)
                    if (!current.Contains(t)) current.Add(t);
            }
            else
            {
                foreach (var t in presetTokens)
                    current.Remove(t);
            }

            TxtAllowedAppsCustom.Text = string.Join(", ", current);
        }

        // Final payload assembly: take the textbox tokens (which by now
        // include every preset toggle the user touched) and return a
        // canonicalised CSV. The server runs the same canonicalisation
        // again before persisting.
        private string BuildAllowedAppsCsvFromUi()
        {
            if (TxtAllowedAppsCustom == null) return string.Empty;
            var tokens = ParseTokens(TxtAllowedAppsCustom.Text);
            return string.Join(",", tokens);
        }

        // Trim + lowercase + strip ".exe" + dedupe. Preserves token
        // order so the textbox doesn't reshuffle on every preset
        // toggle (better UX when the instructor is editing).
        private static List<string> ParseTokens(string csv)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(csv)) return result;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var token = raw.ToLowerInvariant();
                if (!token.Contains('.') && token.EndsWith(".exe", StringComparison.Ordinal))
                    token = token[..^4];
                if (token.Length == 0) continue;
                if (seen.Add(token)) result.Add(token);
            }
            return result;
        }

    }
}