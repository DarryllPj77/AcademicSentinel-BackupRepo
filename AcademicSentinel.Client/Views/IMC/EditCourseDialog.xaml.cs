using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using AcademicSentinel.Client.Constants;
using AcademicSentinel.Client.Services;
using Microsoft.Win32;

namespace AcademicSentinel.Client.Views.IMC
{
    public partial class EditCourseDialog : Window
    {
        private readonly int _roomId;
        private readonly string _originalSubjectName;
        private readonly string _originalCourseImagePath;

        // Local state — null means "no change", non-null means "use this new file path".
        private string _newImagePath;
        private bool _clearImageRequested;

        public EditCourseDialog(int roomId, string subjectName, string existingImageUrl)
        {
            InitializeComponent();

            _roomId = roomId;
            _originalSubjectName = subjectName ?? string.Empty;
            _originalCourseImagePath = existingImageUrl ?? string.Empty;

            TxtCourseHeader.Text = $"Editing: {_originalSubjectName}";
            TxtSubjectName.Text = _originalSubjectName;

            if (!string.IsNullOrWhiteSpace(_originalCourseImagePath))
            {
                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.UriSource = new Uri(_originalCourseImagePath, UriKind.Absolute);
                    bitmap.EndInit();
                    CoursePreviewImage.Source = bitmap;
                    CoursePreviewImage.Visibility = Visibility.Visible;
                    NoImagePlaceholder.Visibility = Visibility.Collapsed;
                }
                catch
                {
                    // Fall back to placeholder if the URL is unreachable.
                }
            }
        }

        private void BtnPickImage_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Select Course Picture",
                // Spec v4: course logos restricted to .png/.jpg/.jpeg only.
                Filter = "Image Files (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg"
            };

            if (dialog.ShowDialog() != true)
                return;

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(dialog.FileName, UriKind.Absolute);
                bitmap.EndInit();

                CoursePreviewImage.Source = bitmap;
                CoursePreviewImage.Visibility = Visibility.Visible;
                NoImagePlaceholder.Visibility = Visibility.Collapsed;

                _newImagePath = dialog.FileName;
                _clearImageRequested = false;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not load that image: {ex.Message}", "Image Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnClearImage_Click(object sender, RoutedEventArgs e)
        {
            CoursePreviewImage.Source = null;
            CoursePreviewImage.Visibility = Visibility.Collapsed;
            NoImagePlaceholder.Visibility = Visibility.Visible;

            _newImagePath = null;
            _clearImageRequested = !string.IsNullOrWhiteSpace(_originalCourseImagePath);
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private async void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            string newSubject = (TxtSubjectName.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(newSubject))
            {
                MessageBox.Show("Subject name cannot be empty.", "Validation",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            BtnSave.IsEnabled = false;
            BtnCancel.IsEnabled = false;
            BtnSave.Content = "Saving...";

            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", SessionManager.JwtToken);

                bool subjectChanged = !string.Equals(newSubject, _originalSubjectName, StringComparison.Ordinal);
                if (subjectChanged)
                {
                    var nameUpdate = new { SubjectName = newSubject };
                    var nameResp = await client.PutAsJsonAsync($"{ApiEndpoints.Rooms}/{_roomId}", nameUpdate);
                    if (!nameResp.IsSuccessStatusCode)
                    {
                        var detail = await nameResp.Content.ReadAsStringAsync();
                        MessageBox.Show(
                            $"Could not update the subject name.\n\nStatus: {nameResp.StatusCode}\nDetails: {detail}",
                            "Update Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                        BtnSave.IsEnabled = true;
                        BtnCancel.IsEnabled = true;
                        BtnSave.Content = "Save Changes";
                        return;
                    }
                }

                if (!string.IsNullOrWhiteSpace(_newImagePath))
                {
                    using var content = new MultipartFormDataContent();
                    var fileBytes = File.ReadAllBytes(_newImagePath);
                    var fileContent = new ByteArrayContent(fileBytes);
                    fileContent.Headers.ContentType =
                        MediaTypeHeaderValue.Parse(GetImageMimeType(_newImagePath));
                    content.Add(fileContent, "image", Path.GetFileName(_newImagePath));

                    var imgResp = await client.PostAsync(
                        $"{ApiEndpoints.BaseUrl}/api/images/room/{_roomId}", content);
                    if (!imgResp.IsSuccessStatusCode)
                    {
                        var detail = await imgResp.Content.ReadAsStringAsync();
                        MessageBox.Show(
                            $"The picture upload was rejected by the server.\n\nStatus: {imgResp.StatusCode}\nDetails: {detail}",
                            "Image Upload Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                        BtnSave.IsEnabled = true;
                        BtnCancel.IsEnabled = true;
                        BtnSave.Content = "Save Changes";
                        return;
                    }
                }
                else if (_clearImageRequested)
                {
                    var clearResp = await client.DeleteAsync($"{ApiEndpoints.BaseUrl}/api/images/room/{_roomId}");
                    if (!clearResp.IsSuccessStatusCode
                        && clearResp.StatusCode != System.Net.HttpStatusCode.NotFound)
                    {
                        var detail = await clearResp.Content.ReadAsStringAsync();
                        MessageBox.Show(
                            $"Could not remove the existing picture.\n\nStatus: {clearResp.StatusCode}\nDetails: {detail}",
                            "Clear Image Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }

                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Unexpected error while saving: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                BtnSave.IsEnabled = true;
                BtnCancel.IsEnabled = true;
                BtnSave.Content = "Save Changes";
            }
        }

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
}
