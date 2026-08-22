using System;
using System.Threading;
using System.Windows;

namespace EpicSetup;

public partial class App : Application
{
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Single instance: a second launch brings the existing window to the
        // front instead of starting another app (which used to run the wrong,
        // stale remote catalog alongside the first one).
        _singleInstance = new Mutex(true, "epic-setup_single-instance", out bool createdNew);
        if (!createdNew)
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);
        Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        Thread.CurrentThread.CurrentUICulture = System.Globalization.CultureInfo.InvariantCulture;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}