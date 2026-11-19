using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using AcademicSentinel.Client.Constants;

namespace AcademicSentinel.Client.Views.Shared
{
    /// <summary>
    /// Email verification screen shown after registration succeeds.
    /// The student enters the 6-digit code that was emailed to their
    /// institutional address. Verification flips the server-side
    /// IsEmailVerified flag, after which Login will accept the account.
    /// </summary>
    public partial class EmailVerificationWindow : Window
    {
        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
        private readonly DispatcherTimer _resendCooldownTimer = new() { Interval = TimeSpan.FromSeconds(1) };
        private int _resendCooldownSeconds;
        private const int ResendCooldownSeconds = 30; // mirrors server-side cooldown

        public EmailVerificationWindow(string email)
        {
            InitializeComponent();
            TxtEmail.Text = email ?? string.Empty;

            _resendCooldownTimer.Tick += ResendCooldownTimer_Tick;
            // Disable resend for the first 30 s — the user JUST received
            // the code from the registration flow, so they should try
            // entering it before requesting a new one.
            StartResendCooldown(ResendCooldownSeconds);
        }

        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) DragMove();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        private async void BtnVerify_Click(object sender, RoutedEventArgs e)
        {
            string code = (TxtCode.Text ?? string.Empty).Trim();
            if (code.Length != 6)
            {
                SetStatus("Enter the 6-digit code from your email.", isError: true);
                return;
            }

            BtnVerify.IsEnabled = false;
            BtnVerify.Content = "Verifying...";

            try
            {
                var response = await _http.PostAsJsonAsync(
                    ApiEndpoints.AuthVerifyEmailCode,
                    new { Email = TxtEmail.Text, Code = code });

                if (response.IsSuccessStatusCode)
                {
                    MessageBox.Show("Email verified. You can now sign in.",
                        "Account Verified", MessageBoxButton.OK, MessageBoxImage.Information);
                    new LoginWindow().Show();
                    Close();
                    return;
                }

                // Surface the generic "invalid or expired" message; the
                // server intentionally does not differentiate so a
                // brute-force attacker can't tell which guesses were
                // closer than others.
                string body = await response.Content.ReadAsStringAsync();
                SetStatus(string.IsNullOrWhiteSpace(body)
                    ? "Invalid or expired verification code."
                    : body.Trim('"'), isError: true);
            }
            catch (Exception ex)
            {
                SetStatus($"Network error: {ex.Message}", isError: true);
            }
            finally
            {
                BtnVerify.IsEnabled = true;
                BtnVerify.Content = "Verify Code";
            }
        }

        private async void BtnResend_Click(object sender, RoutedEventArgs e)
        {
            if (_resendCooldownSeconds > 0) return;

            BtnResend.IsEnabled = false;
            try
            {
                var response = await _http.PostAsJsonAsync(
                    ApiEndpoints.AuthResendVerificationCode,
                    new { Email = TxtEmail.Text });

                // Server returns the same generic message whether the
                // email exists or not, so we don't try to interpret;
                // just tell the user we sent and start the cooldown.
                SetStatus("A new code has been sent if the account exists. Check your inbox.", isError: false);
            }
            catch (Exception ex)
            {
                SetStatus($"Network error: {ex.Message}", isError: true);
            }
            finally
            {
                StartResendCooldown(ResendCooldownSeconds);
            }
        }

        private void StartResendCooldown(int seconds)
        {
            _resendCooldownSeconds = seconds;
            UpdateResendButton();
            if (!_resendCooldownTimer.IsEnabled) _resendCooldownTimer.Start();
        }

        private void ResendCooldownTimer_Tick(object? sender, EventArgs e)
        {
            _resendCooldownSeconds--;
            if (_resendCooldownSeconds <= 0)
            {
                _resendCooldownSeconds = 0;
                _resendCooldownTimer.Stop();
            }
            UpdateResendButton();
        }

        private void UpdateResendButton()
        {
            if (_resendCooldownSeconds > 0)
            {
                BtnResend.IsEnabled = false;
                BtnResend.Content = $"Resend in {_resendCooldownSeconds}s";
            }
            else
            {
                BtnResend.IsEnabled = true;
                BtnResend.Content = "Resend code";
            }
        }

        private void SetStatus(string message, bool isError)
        {
            TxtStatus.Text = message;
            TxtStatus.Foreground = isError
                ? System.Windows.Media.Brushes.IndianRed
                : System.Windows.Media.Brushes.SeaGreen;
        }
    }
}
