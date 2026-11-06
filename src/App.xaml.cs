using System.Windows;

namespace BorgionSystemInfo
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            DispatcherUnhandledException += (_, args) =>
            {
                MessageBox.Show("Er is een onverwachte fout opgetreden:\n\n" + args.Exception.Message,
                    "Borgion System Information", MessageBoxButton.OK, MessageBoxImage.Error);
                args.Handled = true;
            };

            base.OnStartup(e);
            new MainWindow().Show();
        }
    }
}
