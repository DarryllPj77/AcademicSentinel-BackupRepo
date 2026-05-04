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
        public int StartDelaySeconds { get; private set; } = 1;

        // UPDATED: Now requires RoomId!
        public CreateSessionSetupWindow(int roomId, string roomTitle)
        {
            InitializeComponent();
            _currentRoomId = roomId;
            ChkIdle_CheckedChanged(this, new RoutedEventArgs());
            ChkEnableMonitoringTimer_CheckedChanged(this, new RoutedEventArgs());
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

            var settingsPayload = new
            {
                EnableFocusDetection = ChkTabSwitch.IsChecked == true,
                EnableVirtualizationCheck = ChkVirtualMachine.IsChecked == true,
                EnableClipboardMonitoring = ChkClipboard.IsChecked == true,
                EnableProcessDetection = ChkProcess.IsChecked == true,
                EnableIdleDetection = ChkIdle.IsChecked == true,
                IdleThresholdSeconds = idleSeconds,
                StrictMode = ChkStrictMode.IsChecked == true
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
            delaySeconds = 1;
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

    }
}