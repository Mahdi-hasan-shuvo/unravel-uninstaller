using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;
using Unravel.App.Dialogs;
using Unravel.Core.Settings;
using Unravel.Core.Util;

namespace Unravel.App.Infrastructure;

/// <summary>Shared application state and themed message helpers.</summary>
public static class Ui
{
    public static SettingsStore Store { get; } = new();
    public static AppSettings Settings => Store.Current;

    public static Window? Owner => Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current.MainWindow;

    public static void SaveSettings()
    {
        try { Store.Save(); }
        catch (Exception e) { Error("Settings could not be saved.", e.Message); }
    }

    /// <summary>Information messages can be turned off in Settings.</summary>
    public static void Info(string title, string message)
    {
        if (Settings.SkipInformationMessages) return;
        MessageDialog.Show(Owner, title, message, DialogKind.Info, ["OK"]);
    }

    public static void Error(string title, string message) =>
        MessageDialog.Show(Owner, title, message, DialogKind.Error, ["OK"]);

    /// <summary>Asks for confirmation. When warnings are turned off in Settings, the action proceeds without asking.</summary>
    public static bool Confirm(string title, string message, string confirmText = "Continue", bool danger = false)
    {
        if (Settings.SkipWarningMessages) return true;
        return MessageDialog.Show(Owner, title, message, danger ? DialogKind.Danger : DialogKind.Warning, [confirmText, "Cancel"]) == 0;
    }

    /// <summary>Always asks, regardless of the skip-warnings setting (used for irreversible actions).</summary>
    public static bool ConfirmAlways(string title, string message, string confirmText, bool danger = true) =>
        MessageDialog.Show(Owner, title, message, danger ? DialogKind.Danger : DialogKind.Warning, [confirmText, "Cancel"]) == 0;

    public static int Ask(string title, string message, params string[] buttons) =>
        MessageDialog.Show(Owner, title, message, DialogKind.Question, buttons);

    /// <summary>Runs an action and shows a friendly error dialog if it throws.</summary>
    public static bool Try(string what, Action action)
    {
        try { action(); return true; }
        catch (OperationCanceledException) { return false; }
        catch (Exception e) { Error(what, e.Message); return false; }
    }

    public static void RestartAsAdministrator(string? args = null)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, args ?? "") { UseShellExecute = true, Verb = "runas" });
            Application.Current.Shutdown();
        }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223) { }
    }

    public static string? SaveFile(string title, string fileName, string filter)
    {
        var d = new SaveFileDialog { Title = title, FileName = fileName, Filter = filter, AddExtension = true };
        return d.ShowDialog(Owner) == true ? d.FileName : null;
    }

    public static void ExportAndOpen(string title, string fileName, string filter, Func<string, string> contentForPath)
    {
        var path = SaveFile(title, fileName, filter);
        if (path is null) return;
        if (Try("Export failed", () => File.WriteAllText(path, contentForPath(path))))
        {
            if (Ask("Export complete", $"Saved to:\n{path}", "Open file", "Close") == 0) Try("Could not open the file", () => Shell.OpenPath(path));
        }
    }
}
