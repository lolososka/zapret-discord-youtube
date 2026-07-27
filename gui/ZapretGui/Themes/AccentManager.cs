using System.Windows;
using System.Windows.Media;

namespace ZapretGui.Themes;

/// <summary>Один из пяти акцентов §2 «Selectable accent gradients».</summary>
public sealed record AccentPreset(string Name, string Title, string Start, string Mid, string End);

/// <summary>
/// Подменяет акцентные ключи в <see cref="Application.Current"/>.Resources.
/// Ключи, положенные напрямую в Resources приложения, перекрывают одноимённые из
/// MergedDictionaries, поэтому Palette.xaml трогать не нужно — но потребители обязаны
/// брать акцент через DynamicResource, иначе подмену они не увидят.
/// </summary>
public static class AccentManager
{
    private const string DefaultName = "Cyan";

    public static IReadOnlyList<AccentPreset> Presets { get; } = new[]
    {
        new AccentPreset("Cyan",    "Циан",      "#FF26E0F2", "#FF29C4FA", "#FF2FA8FF"),
        new AccentPreset("Violet",  "Фиолетовый", "#FF6E5CFF", "#FF8B57FF", "#FFA855F7"),
        new AccentPreset("Emerald", "Изумруд",   "#FF35E8A6", "#FF23D3A2", "#FF14B8A6"),
        new AccentPreset("Rose",    "Роза",      "#FFFF7A9C", "#FFFF5479", "#FFF0356A"),
        new AccentPreset("Amber",   "Янтарь",    "#FFFFC94B", "#FFFFA644", "#FFFF853D"),
    };

    public static string CurrentName { get; private set; } = DefaultName;

    public static void Apply(string name)
    {
        var preset = Resolve(name);
        CurrentName = preset.Name;

        var app = Application.Current;
        if (app is null)
            return;

        try
        {
            var start = Parse(preset.Start, Color.FromRgb(0x26, 0xE0, 0xF2));
            var mid = Parse(preset.Mid, Color.FromRgb(0x29, 0xC4, 0xFA));
            var end = Parse(preset.End, Color.FromRgb(0x2F, 0xA8, 0xFF));

            var res = app.Resources;

            res["ColorAccentStart"] = start;
            res["ColorAccentMid"] = mid;
            res["ColorAccentEnd"] = end;

            res["BrushAccentStart"] = Brush(start);
            res["BrushAccentMid"] = Brush(mid);
            res["BrushAccentEnd"] = Brush(end);

            // §2: Glow = Mid @8C, Wash = Mid @1A, Dim = Mid @52
            res["BrushAccentGlow"] = Brush(WithAlpha(mid, 0x8C));
            res["BrushAccentWash"] = Brush(WithAlpha(mid, 0x1A));
            res["BrushAccentDim"] = Brush(WithAlpha(mid, 0x52));

            res["BrushStateRunning"] = Brush(mid);

            res["BrushAccentGradient"] = Gradient(new Point(0, 0), new Point(1, 1),
                (0.0, start), (0.5, mid), (1.0, end));

            res["BrushNavIndicator"] = Gradient(new Point(0, 0), new Point(0, 1),
                (0.0, start), (1.0, end));
        }
        catch
        {
            // повреждённый ресурс не повод ронять приложение — акцент просто останется прежним
        }
    }

    private static AccentPreset Resolve(string? name)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            foreach (var preset in Presets)
            {
                if (string.Equals(preset.Name, name, StringComparison.OrdinalIgnoreCase))
                    return preset;
            }
        }

        return Presets[0];
    }

    private static Color Parse(string hex, Color fallback)
    {
        try
        {
            return ColorConverter.ConvertFromString(hex) is Color c ? c : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static Color WithAlpha(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

    private static SolidColorBrush Brush(Color c)
    {
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        return brush;
    }

    private static LinearGradientBrush Gradient(Point from, Point to, params (double Offset, Color Color)[] stops)
    {
        var brush = new LinearGradientBrush { StartPoint = from, EndPoint = to };
        foreach (var (offset, color) in stops)
            brush.GradientStops.Add(new GradientStop(color, offset));
        brush.Freeze();
        return brush;
    }
}
