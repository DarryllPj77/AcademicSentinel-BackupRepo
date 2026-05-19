using Microsoft.Win32;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Net.Http;
using System.Net.Http.Headers;
using System.IO;
using AcademicSentinel.Client.Models;
using AcademicSentinel.Client.Views.Shared;
using AcademicSentinel.Client.Services;
using AcademicSentinel.Client.Constants;
using System.Net.Http.Json; // Required to easily read the JSON response from the server

namespace AcademicSentinel.Client.Views.IMC
{
    public partial class TeacherDashboard : Window
    {
        public ObservableCollection<CourseItem> Courses { get; set; }

        // Cross-window persistence of the Maximized / Normal flag. The
        // teacher's dashboard closes when they click a course tile (the
        // RoomDetailWindow opens in its place); when they navigate back,
        // a fresh TeacherDashboard is constructed. Without this cache it
        // would always open at the default 1280x720 — even if the user
        // had it maximized before. RoomDetailWindow reads/writes the
        // same field so the chain teacher → room → teacher preserves the
        // window state on every hop.
        internal static WindowState LastWindowState = WindowState.Normal;

        public TeacherDashboard() : this(landOnProfile: false) { }

        public TeacherDashboard(bool landOnProfile)
        {
            InitializeComponent();

            // Apply the cached state on every construction so the back-nav
            // from RoomDetailWindow lands in the same Maximized/Normal mode
            // the teacher was previously in.
            this.WindowState = LastWindowState;

            Courses = new ObservableCollection<CourseItem>();
            CoursesItemsControl.ItemsSource = Courses;

            // Load the Logged-in User's Data
            LoadUserData();

            if (landOnProfile)
            {
                BtnAccountProfile.IsChecked = true;
                ShowAccountProfile();
            }
            else
            {
                BtnRoomCourses.IsChecked = true;
                ShowRoomCourses();
            }
        }

        private async void LoadUserData()
        {
            if (SessionManager.IsLoggedIn && SessionManager.CurrentUser != null)
            {
                TxtEmail.Text = SessionManager.CurrentUser.Email;
                string defaultName = !string.IsNullOrWhiteSpace(SessionManager.CurrentUser.FullName)
                    ? SessionManager.CurrentUser.FullName
                    : SessionManager.CurrentUser.Email.Split('@')[0];
                TxtTeacherName.Text = defaultName;
                TxtFullName.Text = defaultName;
                TxtSidebarName.Text = defaultName;

                await FetchProfilePictureFromServer();

                // NEW: Fetch our saved rooms!
                await LoadCoursesFromServer();
            }
        }

        private async System.Threading.Tasks.Task LoadCoursesFromServer()
        {
            try
            {
                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", SessionManager.JwtToken);

                    // Call the API to get the instructor's rooms
                    var response = await client.GetAsync($"{ApiEndpoints.Rooms}/instructor");

                    if (response.IsSuccessStatusCode)
                    {
                        // Read the raw text first to ensure we got valid JSON
                        string rawJson = await response.Content.ReadAsStringAsync();

                        // Configure deserializer to ignore uppercase/lowercase differences
                        var options = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                        var rooms = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.List<RoomWithImageDto>>(rawJson, options);

                        if (rooms != null)
                        {
                            Courses.Clear();
                            foreach (var room in rooms)
                            {
                                Courses.Add(new CourseItem
                                {
                                    RoomId = room.Id,
                                    CourseLogo = room.EnrollmentCode ?? "N/A",
                                    CourseDescription = room.SubjectName,
                                    // Image URL handling: when the server uses Cloudinary
                                    // (production / Render), RoomImageUrl is a fully
                                    // qualified https://res.cloudinary.com/... URL and
                                    // must be used as-is. Local disk storage returns a
                                    // relative path like "/images/rooms/foo.png" that
                                    // still needs the BaseUrl prefix.
                                    CourseImagePath = string.IsNullOrEmpty(room.RoomImageUrl)
                                        ? null
                                        : (room.RoomImageUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                                            ? room.RoomImageUrl
                                            : $"{ApiEndpoints.BaseUrl}{room.RoomImageUrl}"),
                                    IsSelected = false,
                                    // IN PROGRESS pill = the instructor dropped
                                    // out of an active session without ending
                                    // it. Decoupled from room.Status and from
                                    // student state per UX requirement.
                                    IsSessionInProgress = room.InstructorDisconnected
                                });
                            }
                            UpdateEmptyState();
                        }
                    }
                    else
                    {
                        // Unmask Server Errors (e.g., 404 Not Found, 500 Internal Server Error)
                        string errorMsg = await response.Content.ReadAsStringAsync();
                        MessageBox.Show($"Server refused to fetch rooms.\n\nStatus Code: {response.StatusCode}\nDetails: {errorMsg}",
                            "Fetch Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
            }
            catch (Exception ex)
            {
                // Unmask Code Errors (e.g., JSON parsing failures, missing endpoints)
                MessageBox.Show($"A code error occurred while fetching courses:\n\n{ex.Message}",
                    "Critical Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async System.Threading.Tasks.Task FetchProfilePictureFromServer()
        {
            try
            {
                using (var client = new HttpClient())
                {
                    // Attach the JWT Token to prove who we are
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", SessionManager.JwtToken);

                    // Call the API to get our profile details
                    var response = await client.GetAsync($"{ApiEndpoints.BaseUrl}/api/images/profile");

                    if (response.IsSuccessStatusCode)
                    {
                        // Read the response into our temporary model
                        var userProfile = await response.Content.ReadFromJsonAsync<UserProfileResponse>();

                        // If the server returned a URL, it means we have a saved picture!
                        if (userProfile != null && !string.IsNullOrEmpty(userProfile.ProfileImageUrl))
                        {
                            // 1. SYNC TO SESSION: Tell the whole app about the new URL
                            // This is the "brain" fix so RoomDetailWindow can see it!
                            SessionManager.CurrentUser.ProfileImageUrl = userProfile.ProfileImageUrl;

                            // Cloudinary URLs are fully qualified; local-disk URLs
                            // are relative and need BaseUrl prefixing.
                            string fullImageUrl = userProfile.ProfileImageUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                                ? userProfile.ProfileImageUrl
                                : $"{ApiEndpoints.BaseUrl}{userProfile.ProfileImageUrl}";

                            // 2. Download and load the image
                            BitmapImage bitmap = new BitmapImage();
                            bitmap.BeginInit();
                            bitmap.UriSource = new Uri(fullImageUrl);

                            // Optional: Add this to bypass the WPF cache if you just uploaded it
                            bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;

                            bitmap.CacheOption = BitmapCacheOption.OnLoad;
                            bitmap.EndInit();

                            // 3. Update the UI 
                            ProfileImageBrush.ImageSource = bitmap;
                            ProfileImageContainer.Visibility = Visibility.Visible;
                            DefaultProfileIcon.Visibility = Visibility.Collapsed;

                            UpdateSidebarProfile();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Quietly ignore network errors on startup so it doesn't crash the dashboard
                Console.WriteLine($"Could not load profile picture: {ex.Message}");
            }
        }

        // ======================== SIDEBAR NAVIGATION ========================

        private void BtnAccountProfile_Checked(object sender, RoutedEventArgs e) => ShowAccountProfile();
        private void BtnRoomCourses_Checked(object sender, RoutedEventArgs e) => ShowRoomCourses();

        private void BtnHelp_Checked(object sender, RoutedEventArgs e)
        {
            var helpWindow = new HelpGuideWindow(HelpGuideWindow.GuideMode.Teacher);
            helpWindow.Owner = this;
            helpWindow.ShowDialog();
            BtnAccountProfile.IsChecked = true;
        }

        private void BtnLogout_Checked(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show("Are you sure you want to logout?", "Confirm Logout", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                SessionManager.Logout(); // Clear the token!
                var loginWindow = new LoginWindow();
                loginWindow.Show();
                this.Close();
            }
            else
            {
                BtnAccountProfile.IsChecked = true;
            }
        }

        private void ShowAccountProfile()
        {
            if (AccountProfilePanel != null && RoomCoursesPanel != null)
            {
                AccountProfilePanel.Visibility = Visibility.Visible;
                RoomCoursesPanel.Visibility = Visibility.Collapsed;
            }
        }

        private void ShowRoomCourses()
        {
            if (AccountProfilePanel != null && RoomCoursesPanel != null)
            {
                AccountProfilePanel.Visibility = Visibility.Collapsed;
                RoomCoursesPanel.Visibility = Visibility.Visible;
                UpdateEmptyState();
            }
        }

        // ======================== PROFILE ACTIONS (WITH API UPLOAD) ========================

        private async void BtnChangePicture_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog
            {
                Title = "Select Profile Picture",
                // Spec v4: profile pictures restricted to .png/.jpg/.jpeg only.
                Filter = "Image Files (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg",
                FilterIndex = 1
            };

            if (openFileDialog.ShowDialog() == true)
            {
                try
                {
                    var fileInfo = new FileInfo(openFileDialog.FileName);
                    if (fileInfo.Length > 5 * 1024 * 1024) // 5MB Limit from your Feature Doc!
                    {
                        MessageBox.Show("File size exceeds the 5MB limit.", "File Too Large", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    // 1. Update UI Immediately
                    BitmapImage bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.UriSource = new Uri(openFileDialog.FileName);
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.EndInit();

                    ProfileImageBrush.ImageSource = bitmap;
                    ProfileImageContainer.Visibility = Visibility.Visible;
                    DefaultProfileIcon.Visibility = Visibility.Collapsed;
                    UpdateSidebarProfile();

                    // 2. Upload to Server via API
                    BtnChangePicture.IsEnabled = false;
                    BtnChangePicture.Content = "Uploading...";

                    //using (var client = new HttpClient())
                    //{
                    //    // Attach the JWT Token to prove who we are
                    //    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", SessionManager.JwtToken);

                    //    using (var content = new MultipartFormDataContent())
                    //    {
                    //        var fileContent = new ByteArrayContent(File.ReadAllBytes(openFileDialog.FileName));
                    //        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("image/" + Path.GetExtension(openFileDialog.FileName).TrimStart('.'));
                    //        content.Add(fileContent, "image", Path.GetFileName(openFileDialog.FileName));

                    //        // Call the ImagesController
                    //        var response = await client.PostAsync($"{ApiEndpoints.BaseUrl}/api/images/profile", content);

                    //        if (!response.IsSuccessStatusCode)
                    //        {
                    //            MessageBox.Show("Image updated locally, but failed to save to server.", "Sync Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                    //        }
                    //    }
                    //}

                    // 2. Upload to Server via API
                    BtnChangePicture.IsEnabled = false;
                    BtnChangePicture.Content = "Uploading...";

                    using (var client = new HttpClient())
                    {
                        // Attach the JWT Token to prove who we are
                        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", SessionManager.JwtToken);

                        using (var content = new MultipartFormDataContent())
                        {
                            // Read the file and prepare it for HTTP transfer
                            var fileContent = new ByteArrayContent(File.ReadAllBytes(openFileDialog.FileName));
                            fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse(GetImageMimeType(openFileDialog.FileName));

                            // IMPORTANT: The name "image" here must exactly match the parameter name in your ImagesController
                            content.Add(fileContent, "image", Path.GetFileName(openFileDialog.FileName));

                            try
                            {
                                // Send to the API
                                var response = await client.PostAsync($"{ApiEndpoints.BaseUrl}/api/images/profile", content);

                                if (!response.IsSuccessStatusCode)
                                {
                                    // REVEAL THE EXACT ERROR FROM THE SERVER
                                    string serverError = await response.Content.ReadAsStringAsync();
                                    MessageBox.Show($"Server rejected the image.\n\nStatus: {response.StatusCode}\nDetails: {serverError}",
                                        "API Error", MessageBoxButton.OK, MessageBoxImage.Error);

                                    // Revert the UI since the server rejected it
                                    DefaultProfileIcon.Visibility = Visibility.Visible;
                                    ProfileImageContainer.Visibility = Visibility.Collapsed;
                                }
                                else
                                {
                                    MessageBox.Show("Profile picture successfully saved to the server!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                                }
                            }
                            catch (HttpRequestException httpEx)
                            {
                                MessageBox.Show($"Could not reach the server at {ApiEndpoints.BaseUrl}. Is the port correct?\n\n{httpEx.Message}",
                                    "Connection Error", MessageBoxButton.OK, MessageBoxImage.Error);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error loading image: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    BtnChangePicture.IsEnabled = true;
                    BtnChangePicture.Content = "Change Picture";
                }
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

                TxtTeacherName.Text = fullName;
                TxtSidebarName.Text = fullName;
                TxtEmail.Text = email;
                UpdateSidebarProfile();

                MessageBox.Show("Profile updated successfully.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Update failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void UpdateSidebarProfile()
        {
            if (TxtSidebarName != null && TxtTeacherName != null)
                TxtSidebarName.Text = TxtTeacherName.Text;

            if (ProfileImageContainer != null && ProfileImageContainer.Visibility == Visibility.Visible)
            {
                SidebarProfileBrush.ImageSource = ProfileImageBrush.ImageSource;
                SidebarProfileImage.Visibility = Visibility.Visible;
                SidebarDefaultIcon.Visibility = Visibility.Collapsed;
            }
        }

        // ======================== SECURITY / PASSWORD ========================
        // (Keeping your local validation for now)
        private async void BtnChangePassword_Click(object sender, RoutedEventArgs e)
        {
            string currentPassword = TxtCurrentPassword.Password;
            string newPassword = TxtNewPassword.Password;
            string confirmPassword = TxtConfirmPassword.Password;

            if (string.IsNullOrEmpty(currentPassword) || string.IsNullOrEmpty(newPassword) || string.IsNullOrEmpty(confirmPassword))
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

        // ======================== TEACHER ROOM/COURSE MANAGEMENT ========================

        private async void BtnCreateCourse_Click(object sender, RoutedEventArgs e)
        {
            var createRoomWindow = new CreateRoomWindow();
            createRoomWindow.Owner = this;

            if (createRoomWindow.ShowDialog() == true)
            {
                string roomCode = createRoomWindow.TxtRoomCode.Text.Trim();
                string subject = createRoomWindow.TxtRoomSubject.Text.Trim();
                string section = createRoomWindow.TxtSection.Text.Trim();
                string fullSubjectName = $"{subject} - {section}";

                string courseImagePath = null;
                var imgResult = MessageBox.Show("Would you like to add a picture for this course?", "Course Picture", MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (imgResult == MessageBoxResult.Yes)
                {
                    OpenFileDialog openFileDialog = new OpenFileDialog
                    {
                        Title = "Select Course Picture",
                        // Spec v4: course logos restricted to .png/.jpg/.jpeg only.
                        Filter = "Image Files (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg"
                    };
                    if (openFileDialog.ShowDialog() == true)
                    {
                        courseImagePath = openFileDialog.FileName;
                    }
                }

                // 1. Prepare data for the Server
                var newRoomData = new
                {
                    SubjectName = fullSubjectName,
                    EnrollmentCode = roomCode,
                    Status = "Pending"
                };

                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", SessionManager.JwtToken);

                    // 2. Send to SQLite Database!
                    var response = await client.PostAsJsonAsync(ApiEndpoints.Rooms, newRoomData);

                    if (response.IsSuccessStatusCode)
                    {
                        var createdRoom = await response.Content.ReadFromJsonAsync<RoomWithImageDto>();

                        // 3. Upload image if they selected one
                        if (!string.IsNullOrEmpty(courseImagePath) && createdRoom != null)
                        {
                            using (var content = new MultipartFormDataContent())
                            {
                                var fileContent = new ByteArrayContent(File.ReadAllBytes(courseImagePath));
                                fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse(GetImageMimeType(courseImagePath));
                                content.Add(fileContent, "image", Path.GetFileName(courseImagePath));

                                // Calls your ImagesController to save the room logo.
                                // Surface server-side rejections instead of silently saying "Success".
                                var imgResponse = await client.PostAsync($"{ApiEndpoints.BaseUrl}/api/images/room/{createdRoom.Id}", content);
                                if (!imgResponse.IsSuccessStatusCode)
                                {
                                    var imgError = await imgResponse.Content.ReadAsStringAsync();
                                    MessageBox.Show(
                                        $"Room created, but the course picture was rejected by the server.\n\nStatus: {imgResponse.StatusCode}\nDetails: {imgError}",
                                        "Image Upload Failed",
                                        MessageBoxButton.OK,
                                        MessageBoxImage.Warning);
                                }
                            }
                        }

                        // 4. Reload all courses from the server to guarantee it matches the database
                        await LoadCoursesFromServer();
                        MessageBox.Show("Room successfully created and saved!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        string error = await response.Content.ReadAsStringAsync();
                        MessageBox.Show($"Failed to save room to server.\n\nError: {error}", "API Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
        }

        // Build a correct MIME type from a file path. Concatenating "image/" + extension
        // produces invalid types like "image/jpg" (real JPEG MIME is "image/jpeg") and
        // "image/ico" (real icon MIME is "image/x-icon"), which the server's image
        // validator rejects silently — that's why uploaded course/profile pictures
        // appeared to vanish.
        // Spec v4: only .png/.jpg/.jpeg are accepted by the server's
        // ImageStorageService. Returning an unknown MIME type for anything
        // else would surface a clean rejection rather than a silent failure.
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

        private async void BtnEditCourse_Click(object sender, RoutedEventArgs e)
        {
            var selectedCourses = Courses.Where(c => c.IsSelected).ToList();

            if (selectedCourses.Count == 0)
            {
                MessageBox.Show(
                    "Please select a course to edit by checking the checkbox on the course card.",
                    "No Selection", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (selectedCourses.Count > 1)
            {
                MessageBox.Show(
                    "Please select only one course to edit at a time.",
                    "Multiple Selected", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var target = selectedCourses[0];
            var dialog = new EditCourseDialog(target.RoomId, target.CourseDescription, target.CourseImagePath)
            {
                Owner = this
            };

            if (dialog.ShowDialog() == true)
            {
                target.IsSelected = false;
                await LoadCoursesFromServer();
            }
        }

        private async void BtnDeleteCourse_Click(object sender, RoutedEventArgs e)
        {
            var selectedCourses = Courses.Where(c => c.IsSelected).ToList();

            if (selectedCourses.Count == 0)
            {
                MessageBox.Show("Please select courses to delete by checking the checkbox on each course card.", "No Selection", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var result = MessageBox.Show($"Are you sure you want to permanently delete {selectedCourses.Count} selected course(s)?", "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                using (var client = new HttpClient())
                {
                    // Attach the JWT Token
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", SessionManager.JwtToken);

                    foreach (var course in selectedCourses)
                    {
                        // Send the DELETE command to the server using the RoomId!
                        var response = await client.DeleteAsync($"{ApiEndpoints.Rooms}/{course.RoomId}");

                        if (response.IsSuccessStatusCode)
                        {
                            // Only remove it from the screen if the server successfully deleted it
                            Courses.Remove(course);
                        }
                        else
                        {
                            string error = await response.Content.ReadAsStringAsync();
                            MessageBox.Show($"Failed to delete course: {course.CourseDescription}\n\nError: {error}", "Deletion Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        }
                    }
                }
                UpdateEmptyState();
            }
        }

        private void CourseCard_Click(object sender, MouseButtonEventArgs e)
        {
            var border = sender as System.Windows.Controls.Border;
            if (border == null) return;

            var courseItem = border.DataContext as CourseItem;
            if (courseItem == null) return;

            string roomTitle = $"{courseItem.CourseLogo} - {courseItem.CourseDescription}";

            // Cache the current window state so the room detail (and the
            // dashboard when the teacher navigates back) open in the same
            // Maximized / Normal mode the teacher was using.
            LastWindowState = this.WindowState;

            // 1. Create the new Room Detail window
            var roomDetail = new RoomDetailWindow(courseItem.RoomId, roomTitle)
            {
                WindowState = this.WindowState
            };

            // 2. Show the new window
            roomDetail.Show();

            // 3. CLOSE the current dashboard so they don't pile up!
            this.Close();
        }

        private void UpdateEmptyState()
        {
            if (EmptyCoursesState != null)
            {
                EmptyCoursesState.Visibility = Courses.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    // ======================== DATA MODEL ========================

    // Temporary model to read the API response for the profile picture
    // Temporary model to read the API response for the profile picture
    public class UserProfileResponse
    {
        public string? ProfileImageUrl { get; set; }
    }

    // ADD THIS NEW CLASS HERE:
    // Blueprint to read the Room data sent from the Server
    public class RoomWithImageDto
    {
        public int Id { get; set; }
        public string SubjectName { get; set; } = string.Empty;
        public int InstructorId { get; set; }
        public string Status { get; set; } = string.Empty;
        public string? EnrollmentCode { get; set; }
        public DateTime CreatedAt { get; set; }
        public string? RoomImageUrl { get; set; }
        public DateTime? RoomImageUploadedAt { get; set; }
        // True only when the instructor's IMC dropped without End Session
        // for this room. Drives the course-tile "IN PROGRESS" pill.
        public bool InstructorDisconnected { get; set; }
    }


    public class CourseItem : INotifyPropertyChanged
    {
        public int RoomId { get; set; } 
        private string _courseLogo;
        private string _courseDescription;
        private bool _isSelected;
        private string _courseImagePath;

        public string CourseLogo
        {
            get => _courseLogo;
            set { _courseLogo = value; OnPropertyChanged(); }
        }

        public string CourseDescription
        {
            get => _courseDescription;
            set { _courseDescription = value; OnPropertyChanged(); }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); }
        }

        public string CourseImagePath
        {
            get => _courseImagePath;
            set
            {
                _courseImagePath = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasNoImage));
                OnPropertyChanged(nameof(HasImageVisibility));
            }
        }

        public Visibility HasNoImage => string.IsNullOrEmpty(_courseImagePath) ? Visibility.Visible : Visibility.Collapsed;
        public Visibility HasImageVisibility => string.IsNullOrEmpty(_courseImagePath) ? Visibility.Collapsed : Visibility.Visible;

        // Course has a live monitoring session in progress (room.Status ==
        // "Active"). Surfaced in the dashboard tile so a teacher who
        // disconnected/closed the IMC and came back can spot where to
        // rejoin without opening every course.
        private bool _isSessionInProgress;
        public bool IsSessionInProgress
        {
            get => _isSessionInProgress;
            set { _isSessionInProgress = value; OnPropertyChanged(); OnPropertyChanged(nameof(InProgressVisibility)); }
        }
        public Visibility InProgressVisibility => _isSessionInProgress ? Visibility.Visible : Visibility.Collapsed;

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}