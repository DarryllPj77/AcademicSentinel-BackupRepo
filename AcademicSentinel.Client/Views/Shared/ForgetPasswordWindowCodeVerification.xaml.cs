using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AcademicSentinel.Client.Services;

namespace AcademicSentinel.Client.Views.Shared
{
    /// <summary>
    /// Interaction logic for ForgetPasswordWindowCodeVerification.xaml
    /// </summary>
    public partial class ForgetPasswordWindowCodeVerification : Window
    {
        private readonly string _email;
        private readonly AuthService _authService;
        private TextBox[] _codeBoxes = Array.Empty<TextBox>();

        public ForgetPasswordWindowCodeVerification(string email)
        {
            InitializeComponent();
            _authService = new AuthService();
            _email = email;
            TxtCodeHint.Text = $"Sent to {_email}";

            // Cache the box array once so paste / backspace handlers
            // don't have to reconstruct it on every keystroke.
            _codeBoxes = new[] { TxtCode1, TxtCode2, TxtCode3, TxtCode4, TxtCode5, TxtCode6 };

            // Hook the clipboard-paste pipeline on every box. The boxes
            // have MaxLength="1" so the default WPF paste behaviour
            // would truncate a 6-digit paste to a single character —
            // OnCodeBoxPasting intercepts that, extracts digits, and
            // distributes them across all six boxes.
            foreach (var box in _codeBoxes)
            {
                DataObject.AddPastingHandler(box, OnCodeBoxPasting);
            }

            TxtCode1.Focus();
        }

        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                DragMove();
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            new LoginWindow().Show();
            Close();
        }

        private async void BtnVerifyCode_Click(object sender, RoutedEventArgs e)
        {
            string enteredCode = string.Concat(TxtCode1.Text, TxtCode2.Text, TxtCode3.Text, TxtCode4.Text, TxtCode5.Text, TxtCode6.Text);
            if (enteredCode.Length != 6)
            {
                MessageBox.Show("Please enter the 6-digit code.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var resetToken = await _authService.VerifyPasswordResetCodeAsync(_email, enteredCode);
            if (string.IsNullOrWhiteSpace(resetToken))
            {
                MessageBox.Show("Invalid or expired code. Please try again.", "Verification Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            new ForgetPasswordWindowChangePass(_email, resetToken).Show();
            Close();
        }

        private async void LinkResendCode_Click(object sender, MouseButtonEventArgs e)
        {
            bool sent = await _authService.RequestPasswordResetCodeAsync(_email);
            if (sent)
            {
                MessageBox.Show("A new code has been sent to your email.", "Resend Code", MessageBoxButton.OK, MessageBoxImage.Information);
                ClearCodeFields();
                TxtCode1.Focus();
            }
            else
            {
                MessageBox.Show("Failed to resend code.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CodeBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox current && current.Text.Length == 1)
            {
                if (!char.IsDigit(current.Text[0]))
                {
                    current.Text = string.Empty;
                    return;
                }

                MoveToNextCodeBox(current);
            }
        }

        private void MoveToNextCodeBox(TextBox current)
        {
            int currentIndex = Array.IndexOf(_codeBoxes, current);
            if (currentIndex >= 0 && currentIndex < _codeBoxes.Length - 1)
            {
                _codeBoxes[currentIndex + 1].Focus();
            }
        }

        // Backspace UX: an empty box swallowing Backspace is dead-end
        // friction. When the user hits Backspace in an empty box we
        // jump to the previous box and clear it, mimicking the
        // standard OTP-entry pattern in iOS / Android / web auth
        // forms. Backspace in a non-empty box keeps default behaviour
        // (delete the digit, stay put).
        private void CodeBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (sender is not TextBox current) return;

            if (e.Key == Key.Back && string.IsNullOrEmpty(current.Text))
            {
                int currentIndex = Array.IndexOf(_codeBoxes, current);
                if (currentIndex > 0)
                {
                    var previous = _codeBoxes[currentIndex - 1];
                    previous.Clear();
                    previous.Focus();
                    e.Handled = true;
                }
            }
        }

        // Paste handler: the boxes are MaxLength=1, so a naive paste
        // of "420052" into TxtCode1 would silently truncate to "4".
        // We intercept the Pasting command, pull every digit out of
        // the clipboard text (ignoring spaces, dashes, letters), and
        // distribute up to six digits across the boxes starting at
        // whichever box the user pasted into. Cancelling the
        // command on the way out prevents WPF's default paste from
        // also writing the (truncated) string into the source box.
        private void OnCodeBoxPasting(object sender, DataObjectPastingEventArgs e)
        {
            if (!e.DataObject.GetDataPresent(DataFormats.Text))
            {
                e.CancelCommand();
                return;
            }

            var pasted = (string)e.DataObject.GetData(DataFormats.Text);
            var digits = new string((pasted ?? string.Empty).Where(char.IsDigit).Take(_codeBoxes.Length).ToArray());

            if (digits.Length == 0)
            {
                e.CancelCommand();
                return;
            }

            int startIndex = sender is TextBox originBox ? Array.IndexOf(_codeBoxes, originBox) : 0;
            if (startIndex < 0) startIndex = 0;

            // Clear from the paste origin onward so a partial paste
            // doesn't leave stale digits past the new value.
            for (int i = startIndex; i < _codeBoxes.Length; i++)
            {
                _codeBoxes[i].Clear();
            }

            for (int i = 0; i < digits.Length && (startIndex + i) < _codeBoxes.Length; i++)
            {
                _codeBoxes[startIndex + i].Text = digits[i].ToString();
            }

            int focusIndex = Math.Min(startIndex + digits.Length, _codeBoxes.Length - 1);
            _codeBoxes[focusIndex].Focus();
            _codeBoxes[focusIndex].SelectAll();

            e.CancelCommand();
        }

        private void ClearCodeFields()
        {
            TxtCode1.Clear();
            TxtCode2.Clear();
            TxtCode3.Clear();
            TxtCode4.Clear();
            TxtCode5.Clear();
            TxtCode6.Clear();
        }
    }
}
