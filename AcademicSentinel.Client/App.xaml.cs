using System.Windows;
using AcademicSentinel.Client.Views.Shared;

namespace AcademicSentinel.Client
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var consent = new ConsentWindow();
            bool? result = consent.ShowDialog();

            if (result != true)
            {
                Shutdown();
                return;
            }

            ShutdownMode = ShutdownMode.OnLastWindowClose;

            var login = new LoginWindow();
            login.Show();
        }
    }
}