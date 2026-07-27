using System.Windows;
using System.Windows.Media;

namespace ZapretGui.Controls;

/// <summary>
/// Дуга постоянного радиуса, начинающаяся в 12 часов и растущая по часовой стрелке.
/// Нужна потому, что <see cref="ArcSegment"/> нельзя анимировать раскадровкой:
/// без собственной DP с AffectsRender «взвод» силового диска выродился бы в fade.
/// </summary>
public class ArcSegmentShape : System.Windows.Shapes.Shape
{
    public static readonly DependencyProperty AngleProperty = DependencyProperty.Register(
        nameof(Angle), typeof(double), typeof(ArcSegmentShape),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender, OnVisualChanged));

    public static readonly DependencyProperty RadiusProperty = DependencyProperty.Register(
        nameof(Radius), typeof(double), typeof(ArcSegmentShape),
        new FrameworkPropertyMetadata(
            100.0,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
            OnVisualChanged));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(ArcSegmentShape),
        new FrameworkPropertyMetadata(
            2.0,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
            OnThicknessChanged));

    public ArcSegmentShape()
    {
        // Толщина пера — тот же параметр, что и заявленная толщина дуги.
        StrokeThickness = Thickness;
    }

    /// <summary>Раскрытие дуги в градусах, 0..360.</summary>
    public double Angle
    {
        get => (double)GetValue(AngleProperty);
        set => SetValue(AngleProperty, value);
    }

    /// <summary>Радиус осевой линии дуги.</summary>
    public double Radius
    {
        get => (double)GetValue(RadiusProperty);
        set => SetValue(RadiusProperty, value);
    }

    /// <summary>Толщина дуги; синхронизируется со <see cref="System.Windows.Shapes.Shape.StrokeThickness"/>.</summary>
    public double Thickness
    {
        get => (double)GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ArcSegmentShape)d).InvalidateVisual();

    private static void OnThicknessChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var shape = (ArcSegmentShape)d;
        shape.StrokeThickness = (double)e.NewValue;
        shape.InvalidateVisual();
    }

    // Размер не должен зависеть от Angle: иначе при Angle=0 элемент схлопнется в точку
    // и, поскольку AffectsRender не пересчитывает Measure, дуга поедет от центра.
    protected override Size MeasureOverride(Size constraint)
    {
        double side = Math.Max(0.0, 2.0 * Radius + Thickness);
        return new Size(side, side);
    }

    protected override Geometry DefiningGeometry
    {
        get
        {
            double r = Radius;
            double t = Thickness;
            double angle = Angle;

            if (r <= 0.0 || angle <= 0.0) return Geometry.Empty;

            double c = r + t / 2.0;
            var top = new Point(c, c - r);

            var geometry = new StreamGeometry();
            using (StreamGeometryContext ctx = geometry.Open())
            {
                ctx.BeginFigure(top, false, false);

                if (angle >= 360.0)
                {
                    // Полный круг одной ArcTo схлопнулся бы в точку — рисуем двумя полудугами.
                    var bottom = new Point(c, c + r);
                    ctx.ArcTo(bottom, new Size(r, r), 0.0, false, SweepDirection.Clockwise, true, false);
                    ctx.ArcTo(top, new Size(r, r), 0.0, false, SweepDirection.Clockwise, true, false);
                }
                else
                {
                    double rad = angle * Math.PI / 180.0;
                    var end = new Point(c + r * Math.Sin(rad), c - r * Math.Cos(rad));
                    ctx.ArcTo(end, new Size(r, r), 0.0, angle > 180.0, SweepDirection.Clockwise, true, false);
                }
            }

            geometry.Freeze();
            return geometry;
        }
    }
}
