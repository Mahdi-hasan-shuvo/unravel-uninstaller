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
        Ui.Error("Something went wrong", e.Exception.Message + "\n\n" + e.Exception.GetType().Name);
    }
}
