using System;
using System.Windows;
using System.Windows.Input;
using System.Text.RegularExpressions;
using AcademicSentinel.Client.Services;
using AcademicSentinel.Client.Models;
using AcademicSentinel.Client.Constants;

namespace AcademicSentinel.Client.Views.Shared
{
    public partial class RegisterWindow : Window
    {
        private readonly AuthService _authService;

        public RegisterWindow()
        {
            InitializeComponent();
            _authService = new AuthService();

            // Apply AppMode role restriction
            if (AppMode.Role == "Teacher")
            {
                RbTeacher.IsChecked = true;
                RbStudent.Visibility = Visibility.Collapsed;
            }
            else if (AppMode.Role == "Student")
            {
                RbStudent.IsChecked = true;
                RbTeacher.Visibility = Visibility.Collapsed;
            }
            // "All" → both tabs stay visible
        }

        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) this.DragMove();
        }

        private async void BtnRegister_Click(object sender, RoutedEventArgs e)
        {
            string fullName = TxtRegFullName.Text.Trim();
            string email = TxtRegEmail.Text.Trim();
            string password = TxtRegPassword.Password;
            string confirmPassword = TxtRegConfirmPassword.Password;

            if (string.IsNullOrWhiteSpace(fullName) || string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            {
                MessageBox.Show("Please fill in all required fields.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string emailPattern = @"^[^@\s]+@[^@\s]+\.[^@\s]+$";
            if (!Regex.IsMatch(email, emailPattern))
            {
                MessageBox.Show("Please enter a valid email address.", "Invalid Email", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // PROTOTYPE: allowlist mirrors the server. gmail.com is
            // included so testers can register without an
            // institutional account while outbound email is being
            // set up. Tighten back to @fit.edu.ph + @feutech.edu.ph
            // when email delivery is restored.
            string domain = email.Split('@').Length > 1
                ? email.Split('@')[1].ToLowerInvariant()
                : string.Empty;
            if (domain != "fit.edu.ph" && domain != "feutech.edu.ph" && domain != "gmail.com")
            {
                MessageBox.Show(
                    "Registration is restricted to:\n\n" +
                    "  • @fit.edu.ph (Student)\n" +
                    "  • @feutech.edu.ph (Instructor)\n" +
                    "  • @gmail.com (prototype testers)\n\n" +
                    "Please use one of these to continue.",
                    "Email Domain Required",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (password != confirmPassword)
            {
                MessageBox.Show("Passwords do not match.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (password.Length < 6)
            {
                MessageBox.Show("Password must be at least 6 characters long.", "Weak Password", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string selectedRole = RbTeacher.IsChecked == true ? "Instructor" : "Student";

            var registerData = new UserRegisterDto
            {
                FullName = fullName,
                Email = email,
                Password = password,
                Role = selectedRole
            };

            BtnRegister.IsEnabled = false;
            BtnRegister.Content = "Creating Account...";

            bool isSuccess = await _authService.RegisterAsync(registerData);

            if (isSuccess)
            {
                // PROTOTYPE: email verification is auto-completed on
                // the server (no SMTP available yet), so we skip the
                // verification-code screen and go straight to login.
                // Restore the EmailVerificationWindow flow below when
                // SMTP delivery is configured.
                MessageBox.Show(
                    "Account created. You can sign in now.",
                    "Account Ready", MessageBoxButton.OK, MessageBoxImage.Information);
                new LoginWindow(AppMode.Role).Show();
                this.Close();
            }
            else
            {
                // Show the actual server reason if AuthService captured
                // one (e.g. "Cannot reach server", "This email is already
                // registered", "wait N seconds"). Fall back to the
                // generic explanation only when no detail was captured.
                var detail = _authService.LastErrorMessage;
                var msg = string.IsNullOrWhiteSpace(detail)
                    ? "Registration failed. The email might be taken, or the domain isn't allowed " +
                      "(only @fit.edu.ph, @feutech.edu.ph, and @gmail.com are accepted in this build)."
                    : $"Registration failed.\n\n{detail}";

                MessageBox.Show(msg, "Registration Error", MessageBoxButton.OK, MessageBoxImage.Error);
                BtnRegister.IsEnabled = true;
                BtnRegister.Content = "Create Account";
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

        // FIXED — passes AppMode.Role to LoginWindow
        private void LinkLoginHere_Click(object sender, MouseButtonEventArgs e)
        {
            new LoginWindow(AppMode.Role).Show();
            this.Close();
        }

        private void RoleToggle_Changed(object sender, RoutedEventArgs e) { }
    }
}