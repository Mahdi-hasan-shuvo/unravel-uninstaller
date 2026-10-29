using System.Windows;
using System.Windows.Media;

namespace Unravel.App.Infrastructure;

/// <summary>A lightweight filled line chart for the last N values (0..Maximum).</summary>
public sealed class Sparkline : FrameworkElement
{
    private readonly List<double> _values = [];

    public int Capacity { get; set; } = 60;

    public static readonly DependencyProperty MaximumProperty =
        DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(Sparkline), new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }

    public static readonly DependencyProperty StrokeProperty =
        DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(Sparkline), new FrameworkPropertyMetadata(Brushes.SteelBlue, FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    /// <summary>When true the vertical scale follows the largest visible value (for throughput charts).</summary>
    public bool AutoScale { get; set; }

    public void Push(double value)
    {
        _values.Add(double.IsFinite(value) ? Math.Max(0, value) : 0);
        while (_values.Count > Capacity) _values.RemoveAt(0);
        InvalidateVisual();
    }

    public void Clear() { _values.Clear(); InvalidateVisual(); }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var grid = TryFindResource("BorderBrush") as Brush ?? Brushes.LightGray;
        var gridPen = new Pen(grid, 1);
        for (int i = 1; i < 4; i++) dc.DrawLine(gridPen, new Point(0, h * i / 4), new Point(w, h * i / 4));
        if (_values.Count < 2) return;
        var max = AutoScale ? Math.Max(_values.Max() * 1.15, 1) : Math.Max(Maximum, 1e-9);
        double step = w / (Capacity - 1);
        double x0 = w - step * (_values.Count - 1);
        var line = new StreamGeometry();
        var fill = new StreamGeometry();
        using (var g = line.Open())
        using (var f = fill.Open())
        {
            for (int i = 0; i < _values.Count; i++)
            {
                var p = new Point(x0 + i * step, h - Math.Min(1, _values[i] / max) * (h - 2) - 1);
                if (i == 0) { g.BeginFigure(p, false, false); f.BeginFigure(new Point(p.X, h), true, true); f.LineTo(p, false, false); }
                else { g.LineTo(p, true, true); f.LineTo(p, false, false); }
            }
            f.LineTo(new Point(w, h), false, false);
        }
        var fillBrush = Stroke.Clone();
        fillBrush.Opacity = 0.15;
        dc.DrawGeometry(fillBrush, null, fill);
        dc.DrawGeometry(null, new Pen(Stroke, 1.8) { LineJoin = PenLineJoin.Round }, line);
    }
}
