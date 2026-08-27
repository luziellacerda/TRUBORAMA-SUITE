using System.Windows;
using System.Windows.Controls;

namespace Turborama.UiPreview;

internal sealed class SquareMediaHost : Decorator
{
    protected override Size MeasureOverride(Size constraint)
    {
        var width = Normalize(constraint.Width, 320);
        var height = Normalize(constraint.Height, width);
        var side = Math.Min(width, height);
        Child?.Measure(new Size(side, side));
        return new Size(side, side);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        var side = Math.Max(0, Math.Min(arrangeSize.Width, arrangeSize.Height));
        Child?.Arrange(new Rect(
            (arrangeSize.Width - side) / 2,
            (arrangeSize.Height - side) / 2,
            side,
            side));
        return arrangeSize;
    }

    private static double Normalize(double value, double fallback)
        => double.IsNaN(value) || double.IsInfinity(value)
            ? fallback
            : Math.Max(0, value);
}
