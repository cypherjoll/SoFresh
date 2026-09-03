using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Media;
using SoFresh.App.Models;

namespace SoFresh.App.Controls;

public sealed class DonutChart : FrameworkElement
{
    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource),
        typeof(IEnumerable),
        typeof(DonutChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnItemsSourceChanged));

    private INotifyCollectionChanged? observedCollection;

    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        var diameter = Math.Min(ActualWidth, ActualHeight);
        if (diameter <= 0)
        {
            return;
        }

        var thickness = Math.Clamp(diameter * 0.09, 10, 16);
        var radius = Math.Max(0, (diameter - thickness) / 2);
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var trackBrush = FindResourceBrush("SurfacePressedBrush", Brushes.DimGray);
        var trackPen = new Pen(trackBrush, thickness);
        trackPen.Freeze();
        drawingContext.DrawEllipse(null, trackPen, center, radius, radius);

        var categories = ItemsSource?
            .OfType<StorageCategory>()
            .Where(static item => item.Percentage > 0)
            .ToArray() ?? [];
        if (categories.Length == 0)
        {
            return;
        }

        var total = categories.Sum(static item => item.Percentage);
        if (total <= 0)
        {
            return;
        }

        const double gapDegrees = 2.2;
        var startAngle = -90d;
        foreach (var category in categories)
        {
            var rawSweep = Math.Clamp(category.Percentage / total * 360d, 0, 360);
            var gap = categories.Length > 1 ? Math.Min(gapDegrees, rawSweep * 0.2) : 0;
            var visibleSweep = Math.Max(0, rawSweep - gap);
            if (visibleSweep > 0.1)
            {
                var brush = FindResourceBrush(category.BrushResourceKey, Brushes.CornflowerBlue);
                DrawArc(drawingContext, center, radius, startAngle + gap / 2, visibleSweep, brush, thickness);
            }

            startAngle += rawSweep;
        }
    }

    private static void DrawArc(
        DrawingContext drawingContext,
        Point center,
        double radius,
        double startAngle,
        double sweepAngle,
        Brush brush,
        double thickness)
    {
        if (sweepAngle >= 359.9)
        {
            var fullPen = new Pen(brush, thickness);
            fullPen.Freeze();
            drawingContext.DrawEllipse(null, fullPen, center, radius, radius);
            return;
        }

        var start = PointOnCircle(center, radius, startAngle);
        var end = PointOnCircle(center, radius, startAngle + sweepAngle);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(start, isFilled: false, isClosed: false);
            context.ArcTo(
                end,
                new Size(radius, radius),
                rotationAngle: 0,
                isLargeArc: sweepAngle > 180,
                SweepDirection.Clockwise,
                isStroked: true,
                isSmoothJoin: false);
        }

        geometry.Freeze();
        var pen = new Pen(brush, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        pen.Freeze();
        drawingContext.DrawGeometry(null, pen, geometry);
    }

    private static Point PointOnCircle(Point center, double radius, double angleDegrees)
    {
        var radians = angleDegrees * Math.PI / 180d;
        return new Point(
            center.X + radius * Math.Cos(radians),
            center.Y + radius * Math.Sin(radians));
    }

    private Brush FindResourceBrush(string key, Brush fallback) =>
        TryFindResource(key) as Brush ?? fallback;

    private static void OnItemsSourceChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var chart = (DonutChart)dependencyObject;
        if (chart.observedCollection is not null)
        {
            chart.observedCollection.CollectionChanged -= chart.OnCollectionChanged;
        }

        chart.observedCollection = args.NewValue as INotifyCollectionChanged;
        if (chart.observedCollection is not null)
        {
            chart.observedCollection.CollectionChanged += chart.OnCollectionChanged;
        }

        chart.InvalidateVisual();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args) =>
        InvalidateVisual();
}
