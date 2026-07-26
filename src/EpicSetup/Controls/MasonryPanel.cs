using System.Windows;
using System.Windows.Controls;

namespace EpicSetup.Controls;

public sealed class MasonryPanel : Panel
{
    public static readonly DependencyProperty ItemWidthProperty =
        DependencyProperty.Register(nameof(ItemWidth), typeof(double), typeof(MasonryPanel),
            new FrameworkPropertyMetadata(322.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public double ItemWidth
    {
        get => (double)GetValue(ItemWidthProperty);
        set => SetValue(ItemWidthProperty, value);
    }

    private List<List<UIElement>>? _columns;

    protected override Size MeasureOverride(Size availableSize)
    {
        double itemWidth = ItemWidth;
        int columnCount = Math.Max(1, (int)Math.Floor((availableSize.Width > 0 ? availableSize.Width : itemWidth) / itemWidth));
        columnCount = Math.Min(columnCount, InternalChildren.Count > 0 ? InternalChildren.Count : 1);

        _columns = new List<List<UIElement>>(columnCount);
        for (int i = 0; i < columnCount; i++) _columns.Add(new List<UIElement>());

        // Balance by assigning each child to the currently-shortest column.
        var colHeights = new double[columnCount];
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(itemWidth, double.PositiveInfinity));
            double h = child.DesiredSize.Height;

            int target = 0;
            for (int i = 1; i < columnCount; i++)
                if (colHeights[i] < colHeights[target]) target = i;

            _columns[target].Add(child);
            colHeights[target] += h;
        }

        double maxH = 0;
        foreach (var h in colHeights) if (h > maxH) maxH = h;

        double totalW = columnCount * itemWidth;
        if (!double.IsInfinity(availableSize.Width) && totalW > availableSize.Width)
            totalW = columnCount * itemWidth; // keep requested width even if rounded

        return new Size(Math.Min(totalW, double.IsInfinity(availableSize.Width) ? totalW : availableSize.Width), maxH);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double itemWidth = ItemWidth;
        if (_columns == null) return finalSize;

        for (int c = 0; c < _columns.Count; c++)
        {
            double x = c * itemWidth;
            double y = 0;
            foreach (var child in _columns[c])
            {
                double h = child.DesiredSize.Height;
                child.Arrange(new Rect(x, y, itemWidth, h));
                y += h;
            }
        }

        double totalW = _columns.Count * itemWidth;
        return new Size(Math.Min(totalW, finalSize.Width), finalSize.Height);
    }
}