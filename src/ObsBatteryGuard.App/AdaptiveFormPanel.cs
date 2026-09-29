using System.Windows;
using System.Windows.Controls;

namespace ObsBatteryGuard.App;

/// <summary>Fluid, content-sized rows. Column count follows the actual viewport, not window width guesses.</summary>
public sealed class AdaptiveFormPanel : Panel
{
    public int MaximumColumns { get; set; } = 2;
    public double MinimumColumnWidth { get; set; } = 240;
    private const double Gap = 16;
    private int _columns;
    private double _cellWidth;
    private readonly List<double> _heights = new();
    protected override Size MeasureOverride(Size available)
    {
        var width = double.IsInfinity(available.Width) ? 800 : available.Width;
        _columns = Math.Clamp((int)((width + Gap) / (MinimumColumnWidth + Gap)), 1, MaximumColumns);
        _cellWidth = Math.Max(0, (width - (_columns - 1) * Gap) / _columns);
        _heights.Clear();
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            child.Measure(new Size(_cellWidth, double.PositiveInfinity));
            var row = i / _columns;
            if (row == _heights.Count) _heights.Add(0);
            _heights[row] = Math.Max(_heights[row], child.DesiredSize.Height);
        }
        return new Size(width, _heights.Sum() + Math.Max(0, _heights.Count - 1) * Gap);
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        double y = 0;
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var row = i / _columns;
            InternalChildren[i].Arrange(new Rect((i % _columns) * (_cellWidth + Gap), y, _cellWidth, _heights[row]));
            if (i % _columns == _columns - 1) y += _heights[row] + Gap;
        }
        return finalSize;
    }
}
