using System.Windows;

namespace AcademicSentinel.Client.Views.Shared
{
    public partial class ConsentWindow : Window
    {
        public bool Accepted { get; private set; } = false;

        public ConsentWindow()
        {
            InitializeComponent();
        }

        private void BtnAgree_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void BtnDecline_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}