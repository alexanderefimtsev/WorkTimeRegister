using SkiaSharp;

namespace WorkTimeRegister.Services;

internal static class MacOsDockIconRenderer
{
    private const int IconSize = 512;

    public static byte[] Render(SKBitmap baseIcon, DockIconPresentation presentation)
    {
        using var surface = SKSurface.Create(new SKImageInfo(
            IconSize,
            IconSize,
            SKColorType.Rgba8888,
            SKAlphaType.Premul));
        if (surface is null)
            throw new InvalidOperationException("Unable to create the Dock icon surface.");

        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        using var imagePaint = new SKPaint
        {
            IsAntialias = true,
            FilterQuality = SKFilterQuality.High
        };
        canvas.DrawBitmap(baseIcon, new SKRect(0, 0, IconSize, IconSize), imagePaint);

        var modeRect = new SKRect(46, 220, 466, 310);
        var timeRect = new SKRect(34, 316, 478, 408);
        var totalRect = new SKRect(34, 414, 478, 506);
        using var badgePaint = new SKPaint { IsAntialias = true, Color = ModeColor(presentation.Mode) };
        canvas.DrawRoundRect(modeRect, 40, 40, badgePaint);
        badgePaint.Color = new SKColor(31, 41, 55, 238);
        canvas.DrawRoundRect(timeRect, 42, 42, badgePaint);
        badgePaint.Color = new SKColor(17, 24, 39, 238);
        canvas.DrawRoundRect(totalRect, 42, 42, badgePaint);

        using var typeface = SKTypeface.FromFamilyName("Helvetica Neue", SKFontStyle.Bold);
        DrawCenteredText(canvas, presentation.ModeText, modeRect, 68, typeface);
        DrawCenteredText(canvas, presentation.TimeText, timeRect, 80, typeface);
        DrawCenteredText(canvas, $"Σ {presentation.TotalTimeText}", totalRect, 76, typeface);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static void DrawCenteredText(
        SKCanvas canvas,
        string text,
        SKRect bounds,
        float preferredSize,
        SKTypeface typeface)
    {
        using var textPaint = new SKPaint
        {
            IsAntialias = true,
            Color = SKColors.White,
            TextAlign = SKTextAlign.Center,
            TextSize = preferredSize,
            Typeface = typeface
        };
        while (textPaint.TextSize > 18 && textPaint.MeasureText(text) > bounds.Width - 28)
            textPaint.TextSize -= 2;

        var metrics = textPaint.FontMetrics;
        var baseline = bounds.MidY - (metrics.Ascent + metrics.Descent) / 2;
        canvas.DrawText(text, bounds.MidX, baseline, textPaint);
    }

    private static SKColor ModeColor(DockIconMode mode) => mode switch
    {
        DockIconMode.Work => new SKColor(22, 163, 74, 238),
        DockIconMode.Break => new SKColor(217, 119, 6, 238),
        DockIconMode.Pause => new SKColor(100, 116, 139, 238),
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };
}
