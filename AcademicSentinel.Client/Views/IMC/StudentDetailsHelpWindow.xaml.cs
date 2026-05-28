using System.Windows;
using System.Windows.Input;

namespace AcademicSentinel.Client.Views.IMC
{
    /// <summary>
    /// Replica of the Student Details panel that appears in the live
    /// console when an instructor clicks a connected student. Opened
    /// from InstructorMonitoringHelpWindow when the user clicks the
    /// 4th tutorial student (dolfo). Visual mock only — no Remove
    /// from Session call, no monitoring state mutation.
    /// </summary>
    public partial class StudentDetailsHelpWindow : Window
    {
        public StudentDetailsHelpWindow()
        {
            InitializeComponent();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        // Borderless window — re-implement drag-to-move on the
        // root Border.
        private void Window_DragMove(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) DragMove();
        }
    }
}
