using AcademicSentinel.Client.Constants;
using AcademicSentinel.Client.Views.Shared;
using System.Windows;

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

            // Add AppMode for separating student and teacher login
            var login = new LoginWindow(AppMode.Role);
            login.Show();
        }
    }
}