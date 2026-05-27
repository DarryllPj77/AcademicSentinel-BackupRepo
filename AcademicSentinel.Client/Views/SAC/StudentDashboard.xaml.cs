using AcademicSentinel.Client.Constants;
using AcademicSentinel.Client.Models;
using AcademicSentinel.Client.Services;
using AcademicSentinel.Client.Views.Shared;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace AcademicSentinel.Client.Views.SAC
{
    public partial class StudentDashboard : Window
    {
        public ObservableCollection<StudentCourseItem> StudentCourses { get; set; }
        private int _activeRoomId;
        private string _activeRoomTitle = string.Empty;
        private bool _isActiveRoomJoinable;
        private readonly DispatcherTimer _autoSyncTimer;
        private bool _isSyncInProgress;

        public StudentDashboard()
        {
            InitializeComponent();

            StudentCourses = new ObservableCollection<StudentCourseItem>();
            StudentCoursesControl.ItemsSource = StudentCourses;

            _autoSyncTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(8)
            };
            _autoSyncTimer.Tick += AutoSyncTimer_Tick;
            _autoSyncTimer.Start();

            LoadUserData();
            _ = LoadCoursesFromServer();
            BtnCourses.IsChecked = true;
            ShowCourses();
        }

        private async void AutoSyncTimer_Tick(object sender, EventArgs e)
        {
            await LoadCoursesFromServer(false);
        }

        // ======================== LOAD USER DATA & IMAGE ========================

        private async void LoadUserData()
        {
            if (SessionManager.CurrentUser != null)
            {
                // If FullName came back empty for any reason (older
                // session, transient network read of the login
                // response), fetch /api/auth/profile to recover the
                // canonical value from the User row before falling
                // back to the email prefix. This makes the dashboard
                // resilient to gaps in the login payload without
                // ever displaying a derived/truncated label.
                if (string.IsNullOrWhiteSpace(SessionManager.CurrentUser.FullName))
                {
                    await TryRefreshFullNameFromProfileAsync();
                }

                var displayName = !string.IsNullOrWhiteSpace(SessionManager.CurrentUser.FullName)
                    ? SessionManager.CurrentUser.FullName
                    : SessionManager.CurrentUser.Email.Split('@')[0];

                TxtStudentName.Text = displayName;
                TxtEmail.Text = SessionManager.CurrentUser.Email;
                TxtFullName.Text = displayName;
                if (FindName("TxtSidebarStudentName") is TextBlock sidebarName)
                    sidebarName.Text = displayName;

                if (!string.IsNullOrEmpty(SessionManager.CurrentUser.ProfileImageUrl))
                {
                    await LoadProfileImageFromServer(SessionManager.CurrentUser.ProfileImageUrl);
                }
            }
        }

        // One-shot recovery for empty CurrentUser.FullName. Best-effort:
        // a failure just leaves the field as-is and the email-prefix
        // fallback below takes over for this render.
        private async Task TryRefreshFullNameFromProfileAsync()
        {
            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", SessionManager.JwtToken);
                var response = await client.GetAsync(ApiEndpoints.AuthProfile);
                if (!response.IsSuccessStatusCode) return;
                var profile = await response.Content.ReadFromJsonAsync<ProfileLookupDto>();
                if (profile == null || string.IsNullOrWhiteSpace(profile.FullName)) return;
                SessionManager.CurrentUser.FullName = profile.FullName;
            }
            catch
            {
                // Silent — the caller still has the email-prefix fallback.
            }
        }

        private class ProfileLookupDto
        {
            public string FullName { get; set; } = string.Empty;
        }

        private async Task LoadProfileImageFromServer(string url)
        {
            try
            {
                string fullUrl = url.StartsWith("http") ? url : $"{ApiEndpoints.BaseUrl}{url}";
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", SessionManager.JwtToken);

                var imageBytes = await client.GetByteArrayAsync(fullUrl);
                using var ms = new MemoryStream(imageBytes);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = ms;
                bitmap.EndInit();

                ProfileImageBrush.ImageSource = bitmap;
                ProfileImageContainer.Visibility = Visibility.Visible;
                DefaultProfileIcon.Visibility = Visibility.Collapsed;

                SidebarProfileBrush.ImageSource = bitmap;
                SidebarProfileImage.Visibility = Visibility.Visible;
                SidebarDefaultProfileIcon.Visibility = Visibility.Collapsed;
            }
            catch { /* Keep default icon if error occurs */ }
        }

        // ======================== PROFILE PICTURE UPLOAD ========================

        private async void BtnChangePicture_Click(object sender, RoutedEventArgs e)
        {
            // Spec v4: profile pictures restricted to .png/.jpg/.jpeg only.
            OpenFileDialog openFileDialog = new OpenFileDialog
            {
                Filter = "Image Files (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                try
                {
                    BitmapImage bitmap = new BitmapImage(new Uri(openFileDialog.FileName));
                    ProfileImageBrush.ImageSource = bitmap;
                    ProfileImageContainer.Visibility = Visibility.Visible;
                    DefaultProfileIcon.Visibility = Visibility.Collapsed;

                    SidebarProfileBrush.ImageSource = bitmap;
                    SidebarProfileImage.Visibility = Visibility.Visible;
                    SidebarDefaultProfileIcon.Visibility = Visibility.Collapsed;

                    using var client = new HttpClient();
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", SessionManager.JwtToken);

                    var content = new MultipartFormDataContent();
                    var fileStream = File.OpenRead(openFileDialog.FileName);
                    var streamContent = new StreamContent(fileStream);
                    // Derive the real MIME type from the file extension. Hard-coding
                    // image/jpeg made every PNG/GIF/WebP upload fail server-side
                    // because the bytes didn't match the declared content type.
                    streamContent.Headers.ContentType = new MediaTypeHeaderValue(GetImageMimeType(openFileDialog.FileName));

                    content.Add(streamContent, "image", Path.GetFileName(openFileDialog.FileName));

                    var response = await client.PostAsync($"{ApiEndpoints.BaseUrl}/api/images/profile", content);

                    if (response.IsSuccessStatusCode)
                    {
                        var result = await response.Content.ReadFromJsonAsync<SacImageUploadResponse>();
                        if (result != null)
                        {
                            SessionManager.CurrentUser.ProfileImageUrl = result.Url;
                            MessageBox.Show("Profile picture saved successfully!", "Success");
                        }
                    }
                    else
                    {
                        MessageBox.Show("Saved locally, but failed to sync with the server.", "Sync Warning");
                    }
                }
                catch (Exception ex) { MessageBox.Show($"Upload Error: {ex.Message}"); }
            }
        }

        // ======================== NAVIGATION & SIDEBAR ========================

        private void BtnProfile_Checked(object sender, RoutedEventArgs e) => ShowProfile();
        private void BtnCourses_Checked(object sender, RoutedEventArgs e) => ShowCourses();

        private void BtnHelp_Checked(object sender, RoutedEventArgs e)
        {
            new HelpGuideWindow(HelpGuideWindow.GuideMode.Student) { Owner = this }.ShowDialog();
            BtnProfile.IsChecked = true;
        }

        private void BtnLogout_Checked(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("Are you sure you want to logout?", "Confirm", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            {
                SessionManager.Logout();
                new LoginWindow().Show();
                this.Close();
            }
            else BtnProfile.IsChecked = true;
        }

        private void ShowProfile()
        {
            if (ProfilePanel != null)
            {
                ProfilePanel.Visibility = Visibility.Visible;
                CoursesPanel.Visibility = Visibility.Collapsed;
                if (WaitingRoomPanel != null) WaitingRoomPanel.Visibility = Visibility.Collapsed;
            }
        }

        private void ShowCourses()
        {
            if (CoursesPanel != null)
            {
                ProfilePanel.Visibility = Visibility.Collapsed;
                CoursesPanel.Visibility = Visibility.Visible;
                if (WaitingRoomPanel != null) WaitingRoomPanel.Visibility = Visibility.Collapsed;
                UpdateEmptyState();
            }
        }

        private void UpdateEmptyState()
        {
            if (EmptyCoursesState != null)
                EmptyCoursesState.Visibility = StudentCourses.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // ======================== WAITING ROOM PANEL LOGIC ========================

        private void ShowWaitingRoom(StudentCourseItem item)
        {
            _activeRoomId = item.Id;
            _activeRoomTitle = item.CourseDescription;
            _isActiveRoomJoinable = item.IsJoinable;

            TxtWaitingRoomTitle.Text = item.CourseDescription;
            if (FindName("TxtRoomDescription") is TextBlock roomDescription)
                roomDescription.Text = $"Description: {item.RoomDescription}";
            if (FindName("TxtRoomCreatedBy") is TextBlock roomCreatedBy)
                roomCreatedBy.Text = $"Created by: {item.CreatedBy}";

            if (item.IsJoinable)
            {
                TxtSessionState.Text = "Status: Joinable";
                TxtSessionState.Foreground = new SolidColorBrush(Color.FromRgb(46, 125, 50));
                TxtSessionStateHint.Text = "Your instructor has started the session. You can now join.";
                BtnJoinSession.IsEnabled = true;
                BtnJoinSession.Opacity = 1;
            }
            else
            {
                TxtSessionState.Text = "Status: Not Joinable Yet";
                TxtSessionState.Foreground = new SolidColorBrush(Color.FromRgb(211, 47, 47));
                TxtSessionStateHint.Text = "You can only join after your instructor starts the session.";
                BtnJoinSession.IsEnabled = false;
                BtnJoinSession.Opacity = 0.6;
            }

            ProfilePanel.Visibility = Visibility.Collapsed;
            CoursesPanel.Visibility = Visibility.Collapsed;
            WaitingRoomPanel.Visibility = Visibility.Visible;
        }

        private void BtnBackToCourses_Click(object sender, RoutedEventArgs e)
        {
            ShowCourses();
        }

        private async void BtnJoinSession_Click(object sender, RoutedEventArgs e)
        {
            await LoadCoursesFromServer(false);

            if (!StudentCourses.Any(c => c.Id == _activeRoomId))
            {
                MessageBox.Show("You are no longer enrolled in this room or the room was removed.", "Room Access Removed", MessageBoxButton.OK, MessageBoxImage.Warning);
                ShowCourses();
                return;
            }

            await RefreshSelectedRoomJoinStatusAsync();

            if (!_isActiveRoomJoinable)
            {
                MessageBox.Show("Session is not active yet. Please wait for your instructor to start it.", "Not Joinable", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var assessmentClient = new SecureAssessmentClientWindow(_activeRoomId, _activeRoomTitle);
            assessmentClient.Show();
            this.Close();
        }

        // ======================== COURSE MANAGEMENT ========================

        private async void BtnAddCourse_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new AddCourseCodeDialog { Owner = this };
            if (dialog.ShowDialog() == true)
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", SessionManager.JwtToken);
                var response = await client.PostAsJsonAsync($"{ApiEndpoints.Rooms}/enroll-code", new { EnrollmentCode = dialog.CourseCode });
                if (response.IsSuccessStatusCode) { MessageBox.Show("Enrolled!"); await LoadCoursesFromServer(true); }
                else MessageBox.Show("Invalid code or already enrolled.");
            }
        }

        private async void BtnRefreshCourses_Click(object sender, RoutedEventArgs e)
        {
            var refreshButton = FindName("BtnRefreshCourses") as Button;
            if (refreshButton != null)
            {
                refreshButton.IsEnabled = false;
                refreshButton.Content = "Refreshing...";
            }

            await LoadCoursesFromServer(true);

            if (refreshButton != null)
            {
                refreshButton.Content = "Refresh";
                refreshButton.IsEnabled = true;
            }
        }

        private void CourseCard_Click(object sender, MouseButtonEventArgs e)
        {
            if ((sender as Border)?.DataContext is StudentCourseItem item)
            {
                ShowWaitingRoom(item);
            }
        }

        private async Task RefreshSelectedRoomJoinStatusAsync()
        {
            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", SessionManager.JwtToken);

                var response = await client.GetAsync($"{ApiEndpoints.Rooms}/{_activeRoomId}");
                if (!response.IsSuccessStatusCode) return;

                var room = await response.Content.ReadFromJsonAsync<RoomStatusDto>();
                if (room == null) return;

                _isActiveRoomJoinable = string.Equals(room.Status, "Active", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                // Keep last known state when refresh fails
            }
        }

        private async Task LoadCoursesFromServer(bool showErrors = false)
        {
            if (_isSyncInProgress) return;
            _isSyncInProgress = true;

            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", SessionManager.JwtToken);
                var response = await client.GetAsync($"{ApiEndpoints.Rooms}/student");

                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized || response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    MessageBox.Show("Your session expired. Please login again.", "Authentication", MessageBoxButton.OK, MessageBoxImage.Information);
                    SessionManager.Logout();
                    new LoginWindow().Show();
                    Close();
                    return;
                }

                if (response.IsSuccessStatusCode)
                {
                    var courses = await response.Content.ReadFromJsonAsync<List<StudentCourseItem>>();
                    StudentCourses.Clear();
                    if (courses != null)
                    {
                        foreach (var c in courses)
                        {
                            if (!string.IsNullOrEmpty(c.CourseImagePath) && !c.CourseImagePath.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                            {
                                c.CourseImagePath = $"{ApiEndpoints.BaseUrl}{c.CourseImagePath}";
                            }

                            c.UpdateJoinStatus();
                            StudentCourses.Add(c);
                        }
                    }

                    if (WaitingRoomPanel.Visibility == Visibility.Visible)
                    {
                        var activeRoom = StudentCourses.FirstOrDefault(c => c.Id == _activeRoomId);
                        if (activeRoom == null)
                        {
                            MessageBox.Show("This room no longer exists or you were removed from it.", "Room Update", MessageBoxButton.OK, MessageBoxImage.Warning);
                            ShowCourses();
                        }
                        else if (!activeRoom.HasActiveSession)
                        {
                            // Teacher ended the session while the student was
                            // sitting on the waiting screen. Auto-return to
                            // courses so the student doesn't keep staring at
                            // a "Joinable" prompt for a session that no
                            // longer exists. Their participant row will be
                            // flagged Disconnected in the Session Archive by
                            // the EndExamSession flush.
                            MessageBox.Show(
                                "The instructor has ended the session. Returning to your courses.",
                                "Session Ended", MessageBoxButton.OK, MessageBoxImage.Information);
                            ShowCourses();
                        }
                        else
                        {
                            _activeRoomTitle = activeRoom.CourseDescription;
                            ShowWaitingRoom(activeRoom);
                        }
                    }
                }
                else if (showErrors)
                {
                    MessageBox.Show("Unable to refresh courses from server.", "Refresh Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch
            {
                if (showErrors)
                {
                    MessageBox.Show("Connection error while refreshing courses.", "Connection Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            finally
            {
                _isSyncInProgress = false;
                UpdateEmptyState();
            }
        }

        private async void BtnUpdateProfile_Click(object sender, RoutedEventArgs e)
        {
            string fullName = TxtFullName.Text.Trim();
            string email = TxtEmail.Text.Trim();

            if (string.IsNullOrWhiteSpace(fullName) || string.IsNullOrWhiteSpace(email))
            {
                MessageBox.Show("Full name and email are required.", "Update Profile", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", SessionManager.JwtToken);

                var payload = new UpdateProfileDto
                {
                    FullName = fullName,
                    Email = email
                };

                var response = await client.PutAsJsonAsync(ApiEndpoints.AuthProfile, payload);
                if (!response.IsSuccessStatusCode)
                {
                    string error = await response.Content.ReadAsStringAsync();
                    MessageBox.Show($"Failed to update profile.\n\n{error}", "Update Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (SessionManager.CurrentUser != null)
                {
                    SessionManager.CurrentUser.FullName = fullName;
                    SessionManager.CurrentUser.Email = email;
                }

                TxtStudentName.Text = fullName;
                TxtSidebarStudentName.Text = fullName;
                TxtEmail.Text = email;

                MessageBox.Show("Profile updated successfully.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Update failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void BtnChangePassword_Click(object sender, RoutedEventArgs e)
        {
            string currentPassword = TxtCurrentPassword.Password;
            string newPassword = TxtNewPassword.Password;
            string confirmPassword = TxtConfirmPassword.Password;

            if (string.IsNullOrWhiteSpace(currentPassword) || string.IsNullOrWhiteSpace(newPassword) || string.IsNullOrWhiteSpace(confirmPassword))
            {
                MessageBox.Show("Please fill in all password fields.", "Change Password", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (newPassword != confirmPassword)
            {
                MessageBox.Show("New password and confirmation do not match.", "Password Mismatch", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (newPassword.Length < 6)
            {
                MessageBox.Show("Password must be at least 6 characters long.", "Weak Password", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", SessionManager.JwtToken);

                var payload = new ChangePasswordDto
                {
                    CurrentPassword = currentPassword,
                    NewPassword = newPassword
                };

                var response = await client.PostAsJsonAsync(ApiEndpoints.AuthChangePassword, payload);
                if (!response.IsSuccessStatusCode)
                {
                    string error = await response.Content.ReadAsStringAsync();
                    MessageBox.Show($"Failed to change password.\n\n{error}", "Change Password", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                MessageBox.Show("Password changed successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                TxtCurrentPassword.Clear();
                TxtNewPassword.Clear();
                TxtConfirmPassword.Clear();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to change password: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        // Custom window-control handlers removed — Student Dashboard now
        // uses the WPF default title-bar chrome (minimize/maximize/close
        // are handled by the OS).

        protected override void OnClosing(CancelEventArgs e)
        {
            _autoSyncTimer.Stop();
            base.OnClosing(e);
        }

        // Build a correct MIME type from a file path. Concatenating "image/" + extension
        // produces invalid types (e.g. "image/jpg"; the standard is "image/jpeg") and
        // hard-coding "image/jpeg" mis-tags every other format. Both routes cause the
        // server to reject uploads and the picture to silently never appear.
        // Spec v4: only .png/.jpg/.jpeg are accepted by the server.
        private static string GetImageMimeType(string filePath)
        {
            var extension = Path.GetExtension(filePath)?.ToLowerInvariant();
            return extension switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png"            => "image/png",
                _                 => "application/octet-stream"
            };
        }
    }

    // ======================== DATA MODELS ========================

    public class StudentCourseItem : INotifyPropertyChanged
    {
        public int Id { get; set; }
        public string SubjectName { get; set; } = string.Empty;
        public string Section { get; set; } = string.Empty;
        public string EnrollmentCode { get; set; } = string.Empty;
        public string Status { get; set; } = "Pending";
        public string CourseImagePath { get; set; } = string.Empty;
        public string RoomDescription { get; set; } = string.Empty;
        public string CreatedBy { get; set; } = string.Empty;
        // Server-derived state — IsJoinable, HasActiveSession,
        // StudentWasDisconnected all raise PropertyChanged on the
        // computed tile labels (JoinStatusText / JoinStatusColor) so the
        // WPF bindings repaint immediately after each 8 s auto-sync.
        // Without these notifications the tile would keep displaying a
        // stale "Joinable Now" label even after the server response
        // flipped the underlying flags.
        private bool _isJoinable;
        public bool IsJoinable
        {
            get => _isJoinable;
            set { _isJoinable = value; OnPropertyChanged(); OnPropertyChanged(nameof(JoinStatusText)); OnPropertyChanged(nameof(JoinStatusColor)); }
        }
        private bool _hasActiveSession;
        public bool HasActiveSession
        {
            get => _hasActiveSession;
            set { _hasActiveSession = value; OnPropertyChanged(); OnPropertyChanged(nameof(JoinStatusText)); OnPropertyChanged(nameof(JoinStatusColor)); }
        }
        private bool _studentWasDisconnected;
        public bool StudentWasDisconnected
        {
            get => _studentWasDisconnected;
            set { _studentWasDisconnected = value; OnPropertyChanged(); OnPropertyChanged(nameof(JoinStatusText)); OnPropertyChanged(nameof(JoinStatusColor)); }
        }

        // Status text precedence:
        //   1. Student was disconnected from a still-active session →
        //      orange "In Progress, Reconnect NOW!" so they know to
        //      rejoin immediately (subject to instructor approval).
        //   2. Active session exists and student is fresh → green
        //      "Joinable Now".
        //   3. No active session → red "Not Joinable Yet" (this also
        //      covers the case where the teacher already ended the
        //      session — the room is no longer joinable even if
        //      room.Status happens to still say "Active" briefly).
        public string JoinStatusText =>
            HasActiveSession && StudentWasDisconnected
                ? "In Progress, Reconnect NOW!"
                : HasActiveSession
                    ? "Joinable Now"
                    : "Not Joinable Yet";
        public string JoinStatusColor =>
            HasActiveSession && StudentWasDisconnected
                ? "#E65100"
                : HasActiveSession
                    ? "#2E7D32"
                    : "#D32F2F";

        public Visibility HasNoImageVisibility => string.IsNullOrWhiteSpace(CourseImagePath) ? Visibility.Visible : Visibility.Collapsed;
        public Visibility HasImageVisibility => string.IsNullOrWhiteSpace(CourseImagePath) ? Visibility.Collapsed : Visibility.Visible;

        public string CourseLogo { get => EnrollmentCode; set { EnrollmentCode = value; OnPropertyChanged(); } }
        public string CourseDescription => string.IsNullOrWhiteSpace(Section) ? SubjectName : $"{SubjectName} - {Section}";

        public void UpdateJoinStatus()
        {
            // IsJoinable now keys off the real existence of an Active
            // ExamSession, not room.Status. Once the teacher ends the
            // session, IsJoinable goes false even if room.Status takes
            // a moment to propagate.
            IsJoinable = HasActiveSession;
        }

        private bool _isSelected;
        public bool IsSelected { get => _isSelected; set { _isSelected = value; OnPropertyChanged(); } }
        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public class RoomStatusDto
    {
        public string Status { get; set; } = string.Empty;
    }

    // Renamed to avoid clashing with other files!
    public class SacImageUploadResponse { public bool Success { get; set; } public string Url { get; set; } }
}