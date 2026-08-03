using System.Windows;
using System.Windows.Controls;

namespace EpicSetup.Controls;

public sealed class MasonryPanel : Panel
{
    public static readonly DependencyProperty MaxItemWidthProperty =
        DependencyProperty.Register(nameof(MaxItemWidth), typeof(double), typeof(MasonryPanel),
            new FrameworkPropertyMetadata(440.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty ColumnGapProperty =
        DependencyProperty.Register(nameof(ColumnGap), typeof(double), typeof(MasonryPanel),
            new FrameworkPropertyMetadata(22.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public double MaxItemWidth
    {
        get => (double)GetValue(MaxItemWidthProperty);
        set => SetValue(MaxItemWidthProperty, value);
    }

    public double ColumnGap
    {
        get => (double)GetValue(ColumnGapProperty);
        set => SetValue(ColumnGapProperty, value);
    }

    private sealed class Layout
    {
        public List<List<UIElement>> Cols = new();
        public List<double> ColWidths = new();
        public double MaxHeight;
        public double TotalWidth;
    }

    private Layout? _layout;

    protected override Size MeasureOverride(Size availableSize)
    {
        double maxItemW = MaxItemWidth;
        double gap = ColumnGap;
        bool infiniteW = double.IsInfinity(availableSize.Width);
        double availW = infiniteW ? double.MaxValue : availableSize.Width;

        // Measure each child at its natural width (capped at MaxItemWidth) and infinite height.
        foreach (UIElement child in InternalChildren)
            child.Measure(new Size(maxItemW, double.PositiveInfinity));

        int childCount = InternalChildren.Count;
        if (childCount == 0) { _layout = new Layout(); return new Size(); }

        int cMax = Math.Min(childCount, 8);
        Layout? chosen = null;
        int chosenC = 1;

        for (int c = cMax; c >= 1; c--)
        {
            var layout = Distribute(c);
            double total = layout.TotalWidth + (c - 1) * gap;
            if (total <= availW) { chosen = layout; chosenC = c; break; }
        }
        if (chosen == null) { chosen = Distribute(1); chosenC = 1; }

        _layout = chosen;
        double desiredW = Math.Min(chosen.TotalWidth + (chosenC - 1) * gap, availW);
        if (infiniteW) desiredW = chosen.TotalWidth + (chosenC - 1) * gap;
        return new Size(desiredW, chosen.MaxHeight);
    }

    private Layout Distribute(int columnCount)
    {
        var layout = new Layout();
        for (int i = 0; i < columnCount; i++) { layout.Cols.Add(new List<UIElement>()); layout.ColWidths.Add(0); }
        var heights = new double[columnCount];

        foreach (UIElement child in InternalChildren)
        {
            int target = 0;
            for (int i = 1; i < columnCount; i++)
                if (heights[i] < heights[target]) target = i;

            layout.Cols[target].Add(child);
            heights[target] += child.DesiredSize.Height;
            double w = child.DesiredSize.Width;
            if (w > layout.ColWidths[target]) layout.ColWidths[target] = w;
        }

        double total = 0;
        foreach (var w in layout.ColWidths) total += w;
        layout.TotalWidth = total;
        layout.MaxHeight = heights.Length > 0 ? heights.Max() : 0;
        return layout;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // Clamp to non-negative: WPF can pass (0,0) or negative sizes during
        // window setup / empty-content layout passes. Size/Rect ctor throw on negatives.
        double finalW = Math.Max(0, finalSize.Width);
        double finalH = Math.Max(0, finalSize.Height);

        if (_layout == null || _layout.Cols.Count == 0)
            return new Size(finalW, finalH);

        double gap = ColumnGap;
        double x = 0;

        for (int c = 0; c < _layout.Cols.Count; c++)
        {
            double cw = _layout.ColWidths[c];
            if (cw > finalW) cw = finalW; // narrow window: clamp so the star-trim header handles it
            cw = Math.Max(0, cw);
            double y = 0;
            foreach (var child in _layout.Cols[c])
            {
                double h = Math.Max(0, child.DesiredSize.Height);
                child.Arrange(new Rect(x, y, cw, h));
                y += h;
            }
            x += cw + gap;
        }

        double used = x - gap;
        return new Size(Math.Max(0, Math.Min(used, finalW)), finalH);
    }
}