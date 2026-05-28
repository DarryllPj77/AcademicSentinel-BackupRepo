using System.Windows;
using System.Windows.Input;
using MaterialDesignThemes.Wpf;

namespace AcademicSentinel.Client.Views.IMC
{
    /// <summary>
    /// Small modal popup that shows the Description / Purpose /
    /// Scenario for a single region clicked in the Instructor
    /// Monitoring Help replica. Opened with Owner set to the help
    /// window and shown via ShowDialog so it stays on top and
    /// blocks interaction with the replica until dismissed.
    /// Performs no monitoring actions and holds no live state.
    /// </summary>
    public partial class MonitoringHelpInfoWindow : Window
    {
        public MonitoringHelpInfoWindow(string title, PackIconKind iconKind, string description, string purpose, string scenario)
        {
            InitializeComponent();
            TxtTitle.Text       = title       ?? string.Empty;
            IcoRegion.Kind      = iconKind;
            TxtDescription.Text = description ?? string.Empty;
            TxtPurpose.Text     = purpose     ?? string.Empty;
            TxtScenario.Text    = scenario    ?? string.Empty;
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        // The window is borderless (WindowStyle=None + AllowsTransparency=True),
        // so we re-implement drag-to-move on the root Border.
        private void Window_DragMove(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) DragMove();
        }
    }
}
