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
                MessageBox.Show("Registration successful! You can now log in.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                new LoginWindow().Show();
                this.Close();
            }
            else
            {
                MessageBox.Show("Registration failed. Email might be taken.", "Registration Error", MessageBoxButton.OK, MessageBoxImage.Error);
                BtnRegister.IsEnabled = true;
                BtnRegister.Content = "Create Account";
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

        private void LinkLoginHere_Click(object sender, MouseButtonEventArgs e)
        {
            new LoginWindow().Show();
            this.Close();
        }

        private void RoleToggle_Changed(object sender, RoutedEventArgs e) { }
    }
}