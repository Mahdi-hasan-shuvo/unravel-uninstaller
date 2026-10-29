using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Unravel.Core.Leftovers;
using Unravel.Core.Util;

namespace Unravel.App.Infrastructure;

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type t, object p, CultureInfo c) =>
        value is null || value is string s && string.IsNullOrWhiteSpace(s) ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Shows the first line of a multi-line text, so it fits a fixed-height row.</summary>
public sealed class FirstLineConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        var text = value as string ?? "";
        int nl = text.IndexOfAny(['\r', '\n']);
        return nl < 0 ? text : text[..nl] + " …";
    }
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class BytesConverter : IValueConverter
{
    public object Convert(object? value, Type t, object p, CultureInfo c) => value switch
    {
        long l when l > 0 => Format.Bytes(l),
        int i when i > 0 => Format.Bytes(i),
        _ => ""
    };
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class DateConverter : IValueConverter
{
    public object Convert(object? value, Type t, object p, CultureInfo c) =>
        value is DateTime d ? d.ToString("d", CultureInfo.CurrentCulture) : "";
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Items identified as created by the program are bold, as in the original's leftover view.</summary>
public sealed class LeftoverWeightConverter : IValueConverter
{
    public object Convert(object? value, Type t, object p, CultureInfo c) => value is true ? FontWeights.Bold : FontWeights.Normal;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Binds to LeftoverNode.State: excluded → danger colour, created → normal text, context-only → muted.</summary>
public sealed class LeftoverBrushConverter : IValueConverter
{
    public object Convert(object? value, Type t, object p, CultureInfo c)
    {
        var app = Application.Current;
        return value switch
        {
            "excluded" => app.FindResource("DangerBrush"),
            "context" => app.FindResource("MutedTextBrush"),
            _ => app.FindResource("TextBrush"),
        };
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class LeftoverGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type t, object p, CultureInfo c) => value switch
    {
        LeftoverKind.RegistryKey => "",
        LeftoverKind.RegistryValue => "",
        LeftoverKind.Folder => "",
        LeftoverKind.File => "",
        _ => ""
    };
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}


/// <summary>Confidence badge colours: High = success, Medium = warning, Low/Blocked = danger. Parameter "bg" selects the soft background.</summary>
public sealed class ConfidenceBrushConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        bool bg = p as string == "bg";
        var key = (value as string) switch
        {
            "High" => bg ? "SuccessSoftBrush" : "SuccessBrush",
            "Medium" => bg ? "WarningSoftBrush" : "WarningBrush",
            "Low" or "Blocked" => bg ? "DangerSoftBrush" : "DangerBrush",
            _ => bg ? "SurfaceAltBrush" : "MutedTextBrush",
        };
        return Application.Current.FindResource(key);
    }
    public object ConvertBack(object value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}
