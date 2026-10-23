using SkiaSharp;

namespace FCommerce.DemoSeeder;

/// <summary>
/// Draws simple product illustrations so the demo has images: an overview, a close-up and the packaging,
/// shaped by category and coloured by SKU (deterministic, no external assets).
/// </summary>
internal static class DemoImages
{
    private const int Width = 1200;
    private const int Height = 900;

    public static IReadOnlyList<byte[]> For(string sku, string category)
    {
        var accent = Accent(sku);
        return [Render(canvas => Draw(canvas, category, accent, scale: 1f)), Render(canvas => Draw(canvas, category, accent, scale: 1.9f)), Render(canvas => Package(canvas, category, accent))];
    }

    private static byte[] Render(Action<SKCanvas> draw)
    {
        using var surface = SKSurface.Create(new SKImageInfo(Width, Height));
        var canvas = surface.Canvas;
        using (var background = new SKPaint())
        {
            background.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, Height), [new SKColor(0xF7, 0xF8, 0xFA), new SKColor(0xE3, 0xE7, 0xEC)], SKShaderTileMode.Clamp);
            canvas.DrawRect(0, 0, Width, Height, background);
        }

        draw(canvas);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 88);
        return data.ToArray();
    }

    private static void Draw(SKCanvas canvas, string category, SKColor accent, float scale)
    {
        canvas.Save();
        canvas.Translate(Width / 2f, Height / 2f);
        canvas.Scale(scale);
        using var fill = new SKPaint { Color = accent, IsAntialias = true };
        using var shade = new SKPaint { Color = Darker(accent), IsAntialias = true };
        using var line = new SKPaint { Color = Darker(Darker(accent)), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 6 };

        switch (category)
        {
            case "bolts":
                canvas.DrawPath(Hexagon(-260, 0, 110), shade);
                canvas.DrawRect(-170, -45, 430, 90, fill);
                for (var x = -120; x < 260; x += 26)
                {
                    canvas.DrawLine(x, -45, x + 14, 45, line);
                }

                break;
            case "nuts":
                canvas.DrawPath(Hexagon(0, 0, 220), fill);
                canvas.DrawCircle(0, 0, 105, new SKPaint { Color = new SKColor(0xEE, 0xF0, 0xF3), IsAntialias = true });
                canvas.DrawCircle(0, 0, 105, line);
                break;
            case "washers":
                canvas.DrawCircle(0, 0, 230, fill);
                canvas.DrawCircle(0, 0, 230, line);
                canvas.DrawCircle(0, 0, 95, new SKPaint { Color = new SKColor(0xEE, 0xF0, 0xF3), IsAntialias = true });
                canvas.DrawCircle(0, 0, 95, line);
                break;
            case "anchors":
                canvas.DrawRoundRect(-300, -70, 560, 140, 40, 40, fill);
                for (var x = -240; x < 220; x += 60)
                {
                    canvas.DrawRect(x, -90, 22, 180, shade);
                }

                break;
            case "drill-bits":
                canvas.DrawRect(-340, -30, 220, 60, shade);
                canvas.DrawRect(-120, -40, 400, 80, fill);
                for (var x = -110; x < 270; x += 40)
                {
                    canvas.DrawLine(x, -40, x + 30, 40, line);
                }

                canvas.DrawPath(Triangle(280, -40, 280, 40, 350, 0), fill);
                break;
            case "cable-ties":
                canvas.DrawOval(0, 0, 220, 150, line.WithWidth(34).WithColor(accent));
                canvas.DrawRect(180, -30, 180, 60, fill);
                break;
            case "safety":
                canvas.DrawArc(new SKRect(-240, -200, 240, 220), 180, 180, true, fill);
                canvas.DrawRoundRect(-280, 0, 560, 50, 20, 20, shade);
                break;
            default:
                canvas.DrawRoundRect(-60, -260, 120, 380, 30, 30, shade);
                canvas.DrawRoundRect(-200, 100, 400, 160, 40, 40, fill);
                break;
        }

        canvas.Restore();
    }

    private static void Package(SKCanvas canvas, string category, SKColor accent)
    {
        using var box = new SKPaint { Color = new SKColor(0xC8, 0xA4, 0x72), IsAntialias = true };
        using var side = new SKPaint { Color = new SKColor(0xA9, 0x86, 0x58), IsAntialias = true };
        using var label = new SKPaint { Color = SKColors.White, IsAntialias = true };
        canvas.DrawRect(300, 260, 520, 420, box);
        using (var path = Polygon([new(820, 260), new(920, 200), new(920, 620), new(820, 680)]))
        {
            canvas.DrawPath(path, side);
        }

        canvas.DrawRect(360, 340, 400, 260, label);
        canvas.Save();
        canvas.ClipRect(new SKRect(360, 340, 760, 600));
        canvas.Translate(560 - (Width / 2f * 0.38f), 470 - (Height / 2f * 0.38f));
        canvas.Scale(0.38f);
        Draw(canvas, category, accent, 1f);
        canvas.Restore();
    }

    private static SKColor Accent(string sku)
    {
        var hash = sku.Aggregate(7u, (h, c) => (h * 31) + c);
        return SKColor.FromHsl(hash % 360, 45 + (hash / 360 % 25), 45 + (hash / 9000 % 12));
    }

    private static SKColor Darker(SKColor colour) => new((byte)(colour.Red * 0.75), (byte)(colour.Green * 0.75), (byte)(colour.Blue * 0.75));

    private static SKPath Hexagon(float cx, float cy, float radius) =>
        Polygon([.. Enumerable.Range(0, 6).Select(i =>
        {
            var angle = (Math.PI / 3 * i) + (Math.PI / 6);
            return new SKPoint(cx + (radius * (float)Math.Cos(angle)), cy + (radius * (float)Math.Sin(angle)));
        })]);

    private static SKPath Triangle(float x1, float y1, float x2, float y2, float x3, float y3) =>
        Polygon([new(x1, y1), new(x2, y2), new(x3, y3)]);

    private static SKPath Polygon(IReadOnlyList<SKPoint> points) =>
        SKPath.ParseSvgPathData(
            "M " + string.Join(" L ", points.Select(p => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{p.X} {p.Y}"))) + " Z");

    private static SKPaint WithWidth(this SKPaint paint, float width)
    {
        var copy = paint.Clone();
        copy.StrokeWidth = width;
        return copy;
    }

    private static SKPaint WithColor(this SKPaint paint, SKColor colour)
    {
        paint.Color = colour;
        return paint;
    }
}
