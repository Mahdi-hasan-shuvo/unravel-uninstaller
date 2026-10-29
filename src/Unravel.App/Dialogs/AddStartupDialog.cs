using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Unravel.App.Infrastructure;
using Unravel.Core.Autorun;

namespace Unravel.App.Dialogs;

/// <summary>Collects a new startup entry: name, program, arguments, scope and where it is registered.</summary>
public sealed class AddStartupDialog : Window
{
    private readonly TextBox _name = new() { Tag = "Name shown in the list" };
    private readonly TextBox _path = new() { Tag = "Program to start" };
    private readonly TextBox _args = new() { Tag = "Optional command-line arguments" };
    private readonly RadioButton _currentUser = new() { Content = "Only me (current user)", IsChecked = true, GroupName = "scope" };
    private readonly RadioButton _allUsers = new() { Content = "Everyone on this PC (needs administrator)", GroupName = "scope" };
    private readonly RadioButton _run = new() { Content = "Registry: Run – every time Windows starts", IsChecked = true, GroupName = "src" };
    private readonly RadioButton _runOnce = new() { Content = "Registry: RunOnce – only at the next start", GroupName = "src" };
    private readonly RadioButton _folder = new() { Content = "Startup folder – a shortcut, every time Windows starts", GroupName = "src" };

    public string EntryName => _name.Text.Trim();
    public string TargetPath => _path.Text.Trim().Trim('"');
    public string Arguments => _args.Text.Trim();
    public AutorunScope Scope => _allUsers.IsChecked == true ? AutorunScope.AllUsers : AutorunScope.CurrentUser;
    public AutorunSource Source => _runOnce.IsChecked == true ? AutorunSource.RegistryRunOnce : _folder.IsChecked == true ? AutorunSource.StartupFolder : AutorunSource.RegistryRun;

    public AddStartupDialog()
    {
        Title = "Add startup item";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "SurfaceBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        FontFamily = (FontFamily)FindResource("UiFont");
        FontSize = 13;
        ThemeManager.ApplyChrome(this);

        var browse = new Button { Content = "Browse…", Margin = new Thickness(8, 0, 0, 0) };
        browse.Click += (_, _) =>
        {
            var d = new OpenFileDialog { Filter = "Programs|*.exe;*.bat;*.cmd;*.com|All files|*.*" };
            if (d.ShowDialog(this) != true) return;
            _path.Text = d.FileName;
            if (_name.Text.Length == 0) _name.Text = Path.GetFileNameWithoutExtension(d.FileName);
        };
        var pathRow = new DockPanel();
        DockPanel.SetDock(browse, Dock.Right);
        pathRow.Children.Add(browse);
        pathRow.Children.Add(_path);

        var form = new StackPanel { Margin = new Thickness(24, 20, 24, 16) };
        form.Children.Add(Label("Name"));
        form.Children.Add(_name);
        form.Children.Add(Label("Program"));
        form.Children.Add(pathRow);
        form.Children.Add(Label("Arguments"));
        form.Children.Add(_args);
        form.Children.Add(Label("Start for"));
        form.Children.Add(_currentUser);
        form.Children.Add(_allUsers);
        form.Children.Add(Label("Register in"));
        form.Children.Add(_run);
        form.Children.Add(_runOnce);
        form.Children.Add(_folder);

        var ok = new Button { Content = "Add", Style = (Style)FindResource("PrimaryButton"), MinWidth = 96, IsDefault = true };
        ok.Click += (_, _) =>
        {
            if (EntryName.Length == 0) { Ui.Error("Name required", "Enter a name for the startup item."); return; }
            if (!File.Exists(TargetPath)) { Ui.Error("Program not found", "Choose an existing program file."); return; }
            DialogResult = true;
        };
        var cancel = new Button { Content = "Cancel", MinWidth = 96, IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var footer = new Border { Padding = new Thickness(24, 14, 24, 14), Child = buttons };
        footer.SetResourceReference(Border.BackgroundProperty, "SurfaceAltBrush");

        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(form);
        Content = root;
    }

    private static TextBlock Label(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 6) };
}
