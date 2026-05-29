using System;
using System.Windows;
using System.Windows.Input;
using System.Net.Http.Json;
using AcademicSentinel.Client.Constants;
using AcademicSentinel.Client.Services;
using AcademicSentinel.Client.Models;
using AcademicSentinel.Client.Views.IMC;
using AcademicSentinel.Client.Views.SAC; // Added SAC reference

namespace AcademicSentinel.Client.Views.Shared
{
    public partial class LoginWindow : Window
    {
        private readonly AuthService _authService;

        // update the LoginWindow to separate teacher and student login
        public LoginWindow(string role = null)
        {
            InitializeComponent();
            _authService = new AuthService();

            // Fall back to the build-wide AppMode.Role when the
            // caller didn't pass anything explicit. Logout paths
            // (StudentDashboard, ForgetPassword*, EmailVerification,
            // Teacherdashboard) construct `new LoginWindow()` with no
            // arg — without this fallback they would re-show the
            // Teacher/Student toggle on a single-role build, which
            // is exactly the post-logout regression we hit.
            // Explicit roles passed by callers still take precedence.
            string effectiveRole = role ?? AppMode.Role;

            if (effectiveRole == "Teacher")
            {
                RbTeacher.IsChecked = true;
                RbStudent.Visibility = Visibility.Collapsed; // Hide student tab
            }
            else if (effectiveRole == "Student")
            {
                RbStudent.IsChecked = true;
                RbTeacher.Visibility = Visibility.Collapsed; // Hide teacher tab
            }
            // "All" or any other value → both tabs stay visible
        }

        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) this.DragMove();
        }

        private async void BtnLogin_Click(object sender, RoutedEventArgs e)
        {
            string email = TxtLoginEmail.Text.Trim();
            string password = TxtLoginPassword.Password;

            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            {
                MessageBox.Show("Please enter your email and password.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            BtnLogin.IsEnabled = false;
            BtnLogin.Content = "Logging in...";

            try
            {
                bool isSuccess = await _authService.LoginAsync(email, password);

                if (isSuccess)
                {
                    string userRole = SessionManager.CurrentUser?.Role;

                    // Bug fix: enforce that the role the user picked on the
                    // login form matches the role on the account. Without this,
                    // a Student account could log in via the Teacher tab and
                    // (worse) hit a TeacherDashboard route that happens to load.
                    string selectedRole =
                        (RbTeacher.IsChecked == true) ? "Instructor" :
                        (RbStudent.IsChecked == true) ? "Student" :
                        null;

                    if (selectedRole != null
                        && !string.Equals(selectedRole, userRole, StringComparison.OrdinalIgnoreCase))
                    {
                        // Wipe the just-issued session so a wrong-tab login can't
                        // be exploited by clicking around afterward.
                        SessionManager.Logout();

                        MessageBox.Show(
                            $"This account is a {userRole}. Please switch to the {userRole} tab and try again.",
                            "Wrong account type",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                        ResetLoginButton();
                        return;
                    }

                    if (userRole == "Instructor")
                    {
                        new TeacherDashboard().Show();
                    }
                    else if (userRole == "Student")
                    {
                        new StudentDashboard().Show();
                    }

                    this.Close();
                }
                else
                {
                    // Branch on the failure mode the AuthService recorded.
                    // EMAIL_NOT_VERIFIED routes the user to the verify
                    // screen so they can finish onboarding instead of
                    // staring at a misleading "wrong password" toast.
                    if (string.Equals(_authService.LastErrorMessage, "EMAIL_NOT_VERIFIED", StringComparison.Ordinal))
                    {
                        var go = MessageBox.Show(
                            "This account hasn't been verified yet. We can open the email-verification " +
                            "screen so you can enter the code we sent to your institutional inbox. Continue?",
                            "Email Verification Required",
                            MessageBoxButton.YesNo, MessageBoxImage.Information);
                        if (go == MessageBoxResult.Yes)
                        {
                            new EmailVerificationWindow(email).Show();
                            this.Close();
                            return;
                        }
                        ResetLoginButton();
                        return;
                    }

                    MessageBox.Show("Invalid email or password.", "Login Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                    ResetLoginButton();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Connection Error: {ex.Message}");
                ResetLoginButton();
            }
        }

        private void ResetLoginButton()
        {
            TxtLoginPassword.Clear();
            BtnLogin.IsEnabled = true;
            BtnLogin.Content = "Log In";
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

        private void LinkCreateAccount_Click(object sender, RoutedEventArgs e)
        {
            new RegisterWindow().Show();
            this.Close();
        }

        private void LinkForgotPassword_Click(object sender, MouseButtonEventArgs e)
        {
            // PROTOTYPE: the password-reset flow needs outbound email
            // to deliver the reset code, and email isn't configured
            // yet. Surface a clear message instead of opening
            // ForgetPasswordWindow into a dead end. When SMTP is
            // restored, replace this with:
            //   new ForgetPasswordWindow().Show();
            //   this.Close();
            MessageBox.Show(
                "Password reset is temporarily unavailable in this prototype build.\n\n" +
                "Please contact the system administrator if you need to recover an account.",
                "Password Reset Unavailable",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void RoleToggle_Changed(object sender, RoutedEventArgs e) { }
    }
}