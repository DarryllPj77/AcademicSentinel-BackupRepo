using System.Windows;

namespace AcademicSentinel.Client.Views.Shared
{
    /// <summary>
    /// Help Guide Window - shows instructions for both Teacher and Student sides.
    /// </summary>
    public partial class HelpGuideWindow : Window
    {
        public enum GuideMode { Teacher, Student }

        public HelpGuideWindow(GuideMode mode = GuideMode.Teacher)
        {
            InitializeComponent();

            if (mode == GuideMode.Student)
            {
                TxtHelpTitle.Text = "Student Help Guide";
                TxtHelpSubtitle.Text = "Learn how to use the Academic Sentinel system as a student";
                TeacherGuidePanel.Visibility = Visibility.Collapsed;
                StudentGuidePanel.Visibility = Visibility.Visible;
            }
            else
            {
                TxtHelpTitle.Text = "Teacher Help Guide";
                TxtHelpSubtitle.Text = "Learn how to use the Academic Sentinel Monitoring System";
                TeacherGuidePanel.Visibility = Visibility.Visible;
                StudentGuidePanel.Visibility = Visibility.Collapsed;
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}
