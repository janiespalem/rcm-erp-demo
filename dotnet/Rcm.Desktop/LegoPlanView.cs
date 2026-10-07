using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Rcm.Contracts;

namespace Rcm.Desktop;

public sealed class LegoPlanView : FrameworkElement
{
    public static readonly DependencyProperty PlanProperty = DependencyProperty.Register(nameof(Plan), typeof(LegoPlan), typeof(LegoPlanView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LayerProperty = DependencyProperty.Register(nameof(Layer), typeof(int), typeof(LegoPlanView), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));
    public LegoPlan? Plan { get => (LegoPlan?)GetValue(PlanProperty); set => SetValue(PlanProperty, value); }
    public int Layer { get => (int)GetValue(LayerProperty); set => SetValue(LayerProperty, value); }
    internal static Color ColorFor(int length) => (Color)ColorConverter.ConvertFromString(length switch { 240 => "#3498DB", 200 => "#2ECC71", 180 => "#E74C3C", 160 => "#1ABC9C", 120 => "#F39C12", 90 => "#9B59B6", 80 => "#2980B9", 60 => "#95A5A6", _ => "#7F8C8D" });
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(12, 30, 24)), null, new Rect(RenderSize));
        if (Plan is not { Blocks.Length: > 0 } plan || ActualWidth < 1 || ActualHeight < 1) return;
        var maxX = plan.Blocks.Max(block => block.Xcm + block.WidthCm); var maxZ = plan.Blocks.Max(block => block.Zcm + block.DepthCm);
        var scale = Math.Min((ActualWidth - 42) / Math.Max(1, maxX), (ActualHeight - 42) / Math.Max(1, maxZ)); if (scale <= 0) return;
        var ox = (ActualWidth - maxX * scale) / 2; var oz = (ActualHeight - maxZ * scale) / 2;
        foreach (var block in plan.Blocks.Where(block => block.Row == Layer))
        {
            var rect = new Rect(ox + block.Xcm * scale, oz + block.Zcm * scale, block.WidthCm * scale, block.DepthCm * scale);
            dc.DrawRectangle(new SolidColorBrush(ColorFor(block.LengthCm)), new Pen(Brushes.Black, 1), rect);
            if (rect.Width > 30 && rect.Height > 16)
            {
                var text = new FormattedText(block.LengthCm.ToString(), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                dc.DrawText(text, new Point(rect.X + (rect.Width - text.Width) / 2, rect.Y + (rect.Height - text.Height) / 2));
            }
        }
    }
}
