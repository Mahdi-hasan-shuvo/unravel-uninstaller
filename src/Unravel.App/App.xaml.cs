using System.Windows;
using System.Windows.Threading;
using Unravel.App.Infrastructure;

namespace Unravel.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        // Failures in background work must not vanish silently: they are logged with the stack trace.
        TaskScheduler.UnobservedTaskException += (_, args) => { LogCrash("Background task failed", args.Exception); args.SetObserved(); };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => { if (args.ExceptionObject is Exception ex) LogCrash("Unhandled error", ex); };

        Ui.Store.Load();
        ThemeManager.Apply(Ui.Settings.Theme);
        if (Ui.Store.LoadError is { } err)
            Ui.Error("Settings were reset", "Your settings file could not be read and was replaced with defaults. A copy was kept as settings.json.corrupt.\n\n" + err);

        var main = new MainWindow();
        MainWindow = main;
        main.Closed += (_, _) => Shutdown();
        if (e.Args.Any(a => a.Equals("--hunter", StringComparison.OrdinalIgnoreCase)))
            main.EnterHunterMode(showMainWhenDone: false);
        else
            main.Show();
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        LogCrash("Something went wrong", e.Exception);
        Ui.Error("Something went wrong", e.Exception.Message + "\n\n" + e.Exception.GetType().Name + "\n\nDetails were written to the activity log (Activity & recovery).");
    }

    private static void LogCrash(string what, Exception e)
    {
        try { Core.Logging.ActivityLog.Write(Core.Logging.LogLevel.Error, "Application", $"{what}: {e.Message}", e.ToString()); } catch { }
    }
}
