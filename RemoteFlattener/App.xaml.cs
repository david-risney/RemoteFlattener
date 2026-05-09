using System;
using System.Threading;
using System.Windows;

namespace RemoteFlattener;

public partial class App : Application
{
    private const string MutexName = "Global\\RemoteFlattener_SingleInstance";
    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "RemoteFlattener is already running.\n\nCheck the system tray for the existing instance.",
                "RemoteFlattener",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
