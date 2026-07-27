using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ZapretGui.Core;

/// <summary>
/// Тонкая обёртка над DWM: тёмный заголовок, скруглённые углы, Mica/Acrylic.
/// На Windows 10 все атрибуты просто возвращают ошибку HRESULT — падать нельзя.
/// </summary>
public static class NativeInterop
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    private const int DWMWCP_ROUND = 2;

    private const int DWMSBT_MAINWINDOW = 2;   // Mica
    private const int DWMSBT_TRANSIENTWINDOW = 3;   // Acrylic

    [DllImport("dwmapi.dll", SetLastError = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    private enum AccentState
    {
        ACCENT_DISABLED = 0,
        ACCENT_ENABLE_GRADIENT = 1,
        ACCENT_ENABLE_TRANSPARENTGRADIENT = 2,
        ACCENT_ENABLE_BLURBEHIND = 3,
        ACCENT_ENABLE_ACRYLICBLURBEHIND = 4,
        ACCENT_INVALID_STATE = 5
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public AccentState AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    private const int WCA_ACCENT_POLICY = 19;

    /// <summary>true, если Mica или Acrylic реально применились хотя бы к одному окну.</summary>
    public static bool IsBackdropSupported { get; private set; }

    /// <summary>Вызывать ПОСЛЕ SourceInitialized — до этого HWND ещё не существует.</summary>
    public static void ApplyModernWindow(Window w)
    {
        if (w is null) return;

        IntPtr hwnd;
        try
        {
            hwnd = new WindowInteropHelper(w).Handle;
        }
        catch
        {
            return;
        }

        if (hwnd == IntPtr.Zero) return;

        TrySetAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, 1);
        // Windows 10 1809 использует другой индекс атрибута тёмной темы.
        TrySetAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, 1);

        TrySetAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_ROUND);

        // Mica требует прозрачного фона окна, иначе подложка будет перекрыта.
        var applied = TrySetAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, DWMSBT_MAINWINDOW)
                      || TrySetAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, DWMSBT_TRANSIENTWINDOW);

        if (applied) IsBackdropSupported = true;
    }

    private static bool TrySetAttribute(IntPtr hwnd, int attribute, int value)
    {
        try
        {
            var v = value;
            return DwmSetWindowAttribute(hwnd, attribute, ref v, sizeof(int)) == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Запасной вариант размытия для Windows 10, где Mica недоступна.</summary>
    public static void EnableBlurBehind(Window w)
    {
        if (w is null) return;

        IntPtr hwnd;
        try
        {
            hwnd = new WindowInteropHelper(w).Handle;
        }
        catch
        {
            return;
        }

        if (hwnd == IntPtr.Zero) return;

        var accentPtr = IntPtr.Zero;
        try
        {
            var accent = new AccentPolicy
            {
                AccentState = AccentState.ACCENT_ENABLE_BLURBEHIND,
                AccentFlags = 2,
                GradientColor = unchecked((int)0x99000000),
                AnimationId = 0
            };

            var size = Marshal.SizeOf<AccentPolicy>();
            accentPtr = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(accent, accentPtr, false);

            var data = new WindowCompositionAttributeData
            {
                Attribute = WCA_ACCENT_POLICY,
                SizeOfData = size,
                Data = accentPtr
            };

            SetWindowCompositionAttribute(hwnd, ref data);
        }
        catch
        {
            // Windows 10 без нужного экспорта — просто игнорируем.
        }
        finally
        {
            if (accentPtr != IntPtr.Zero) Marshal.FreeHGlobal(accentPtr);
        }
    }
}

/// <summary>Состояние, которое отражает значок в трее.</summary>
public enum TrayState
{
    Stopped,
    Starting,
    Running,
    Failed
}

/// <summary>Иконка в трее с русским контекстным меню. Значок рисуется в рантайме и меняет цвет по состоянию.</summary>
public sealed class TrayIconService : IDisposable
{
    // Пропорции монограммы сняты с Assets/app.ico (256×256) и переведены в доли стороны.
    private const float PlateRadius = 0.210f;
    private const float ZLeft = 0.324f;
    private const float ZRight = 0.680f;
    private const float ZTop = 0.297f;
    private const float ZBottom = 0.703f;
    private const float ZBar = 0.105f;
    private const float ZShear = 0.133f;

    private static readonly System.Drawing.Color GlyphColor = System.Drawing.Color.FromArgb(0x0B, 0x0D, 0x12);
    private static readonly System.Drawing.Color PlateStopped = System.Drawing.Color.FromArgb(0x6E, 0x7A, 0x85);
    private static readonly System.Drawing.Color PlateStarting = System.Drawing.Color.FromArgb(0xF0, 0xB4, 0x41);
    private static readonly System.Drawing.Color PlateFailed = System.Drawing.Color.FromArgb(0xFF, 0x5F, 0x6D);
    private static readonly System.Drawing.Color PlateAccentFallback = System.Drawing.Color.FromArgb(0x8B, 0x57, 0xFF);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    private readonly Window _owner;
    private readonly System.Windows.Forms.NotifyIcon _icon;
    private readonly System.Windows.Forms.ToolStripMenuItem _stateItem;
    private readonly System.Windows.Forms.ToolStripMenuItem _openItem;
    private readonly System.Windows.Forms.ToolStripMenuItem _startItem;
    private readonly System.Windows.Forms.ToolStripMenuItem _stopItem;
    private readonly System.Windows.Forms.ToolStripMenuItem _exitItem;

    // Ключ — «состояние + цвет плашки + размер»: перерисовывать значок на каждый тик состояния незачем.
    private readonly Dictionary<string, System.Drawing.Icon> _iconCache = new(StringComparer.Ordinal);

    private System.Drawing.Icon? _fallbackIcon;
    private bool _disposed;

    public event EventHandler? ShowRequested;
    public event EventHandler? StartRequested;
    public event EventHandler? StopRequested;
    public event EventHandler? ExitRequested;

    public TrayIconService(Window owner)
    {
        _owner = owner;

        _stateItem = new System.Windows.Forms.ToolStripMenuItem("Обход остановлен") { Enabled = false };

        _openItem = new System.Windows.Forms.ToolStripMenuItem("Открыть");
        _openItem.Click += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);

        _startItem = new System.Windows.Forms.ToolStripMenuItem("Запустить");
        _startItem.Click += (_, _) => StartRequested?.Invoke(this, EventArgs.Empty);

        _stopItem = new System.Windows.Forms.ToolStripMenuItem("Остановить");
        _stopItem.Click += (_, _) => StopRequested?.Invoke(this, EventArgs.Empty);

        _exitItem = new System.Windows.Forms.ToolStripMenuItem("Выход");
        _exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add(_stateItem);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(_openItem);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(_startItem);
        menu.Items.Add(_stopItem);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(_exitItem);

        _icon = new System.Windows.Forms.NotifyIcon
        {
            Icon = ResolveIcon(TrayState.Stopped),
            Text = "Zapret — остановлен",
            Visible = true,
            ContextMenuStrip = menu
        };
        _icon.DoubleClick += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);

        SetState(TrayState.Stopped, string.Empty);
    }

    /// <summary>Старая перегрузка: сохранена, чтобы не ломать существующие вызовы.</summary>
    public void SetState(bool running, string strategyName)
        => SetState(running ? TrayState.Running : TrayState.Stopped, strategyName);

    /// <summary>Обновляет значок, подсказку и пункты меню под состояние обхода.</summary>
    public void SetState(TrayState state, string strategyName)
    {
        if (_disposed) return;

        try
        {
            var name = string.IsNullOrWhiteSpace(strategyName) ? "стратегия не выбрана" : strategyName;

            var text = state switch
            {
                TrayState.Running => $"Zapret — работает\n{name}",
                TrayState.Starting => $"Zapret — запуск\n{name}",
                TrayState.Failed => "Zapret — не удалось запустить",
                _ => "Zapret — остановлен",
            };

            // NotifyIcon.Text ограничен 63 символами.
            if (text.Length > 63) text = text.Substring(0, 60) + "…";
            _icon.Text = text;

            _stateItem.Text = state switch
            {
                TrayState.Running => "Обход активен: " + name,
                TrayState.Starting => "Запуск обхода…",
                TrayState.Failed => "Обход не запустился",
                _ => "Обход остановлен",
            };

            _startItem.Text = state == TrayState.Running ? "Перезапустить" : "Запустить";
            _startItem.Enabled = state is not TrayState.Starting;
            _stopItem.Enabled = state is TrayState.Running or TrayState.Starting;

            var icon = ResolveIcon(state);
            if (icon is not null && !ReferenceEquals(_icon.Icon, icon))
                _icon.Icon = icon;
        }
        catch
        {
            // подсказка в трее не стоит падения приложения
        }
    }

    public void Notify(string title, string message)
    {
        if (_disposed) return;

        try
        {
            _icon.BalloonTipTitle = title;
            _icon.BalloonTipText = message;
            _icon.BalloonTipIcon = System.Windows.Forms.ToolTipIcon.Info;
            _icon.ShowBalloonTip(5000);
        }
        catch
        {
            // ignore
        }
    }

    // ──────────────────────────────────────────────────────── отрисовка значка

    private System.Drawing.Icon? ResolveIcon(TrayState state)
    {
        try
        {
            var size = IconSize();
            var plate = PlateColor(state);
            var key = state + "|" + plate.ToArgb().ToString("X8") + "|" + size;

            if (_iconCache.TryGetValue(key, out var cached))
                return cached;

            var drawn = Draw(plate, size);
            if (drawn is not null)
            {
                _iconCache[key] = drawn;
                return drawn;
            }
        }
        catch
        {
            // падаем на запасной значок из ресурсов
        }

        if (_fallbackIcon is not null)
            return _fallbackIcon;

        // SystemIcons.Application общий и не наш — в _fallbackIcon его не кладём, иначе Dispose испортит его всем.
        _fallbackIcon = LoadEmbeddedIcon();
        return _fallbackIcon ?? System.Drawing.SystemIcons.Application;
    }

    private static int IconSize()
    {
        try
        {
            var s = System.Windows.Forms.SystemInformation.SmallIconSize;
            var side = Math.Max(s.Width, s.Height);
            if (side >= 8 && side <= 256)
                return side;
        }
        catch
        {
            // ignore
        }

        return 16;
    }

    private static System.Drawing.Color PlateColor(TrayState state) => state switch
    {
        TrayState.Starting => PlateStarting,
        TrayState.Running => AccentColor(),
        TrayState.Failed => PlateFailed,
        _ => PlateStopped,
    };

    private static System.Drawing.Color AccentColor()
    {
        try
        {
            var app = Application.Current;
            if (app is not null
                && app.Dispatcher.CheckAccess()
                && app.TryFindResource("BrushAccentMid") is System.Windows.Media.SolidColorBrush brush)
            {
                var c = brush.Color;
                return System.Drawing.Color.FromArgb(c.R, c.G, c.B);
            }
        }
        catch
        {
            // ignore
        }

        return PlateAccentFallback;
    }

    private static System.Drawing.Icon? Draw(System.Drawing.Color plate, int size)
    {
        System.Drawing.Bitmap? bitmap = null;
        var handle = IntPtr.Zero;

        try
        {
            bitmap = new System.Drawing.Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

            using (var g = System.Drawing.Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.Clear(System.Drawing.Color.Transparent);

                using (var path = RoundedSquare(size))
                using (var brush = new System.Drawing.SolidBrush(plate))
                    g.FillPath(brush, path);

                using (var path = Monogram(size))
                using (var brush = new System.Drawing.SolidBrush(GlyphColor))
                    g.FillPath(brush, path);
            }

            handle = bitmap.GetHicon();
            using var borrowed = System.Drawing.Icon.FromHandle(handle);
            return (System.Drawing.Icon)borrowed.Clone(); // копия владеет своим HICON
        }
        catch
        {
            return null;
        }
        finally
        {
            // FromHandle не владеет хэндлом — иначе течёт GDI.
            if (handle != IntPtr.Zero)
            {
                try { DestroyIcon(handle); } catch { }
            }

            bitmap?.Dispose();
        }
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundedSquare(int size)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();

        var side = size - 1f; // −1 пиксель: иначе правый и нижний края срезаются
        var d = Math.Max(2f, side * PlateRadius * 2f);

        path.AddArc(0f, 0f, d, d, 180f, 90f);
        path.AddArc(side - d, 0f, d, d, 270f, 90f);
        path.AddArc(side - d, side - d, d, d, 0f, 90f);
        path.AddArc(0f, side - d, d, d, 90f, 90f);
        path.CloseFigure();

        return path;
    }

    /// <summary>Монограмма «Z»: два бруса и диагональ с вертикальными срезами.</summary>
    private static System.Drawing.Drawing2D.GraphicsPath Monogram(int size)
    {
        var s = size - 1f;
        var l = ZLeft * s;
        var r = ZRight * s;
        var t = ZTop * s;
        var b = ZBottom * s;
        var bar = Math.Max(1f, ZBar * s);
        var shear = ZShear * s;

        var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddPolygon(new[]
        {
            new System.Drawing.PointF(l, t),
            new System.Drawing.PointF(r, t),
            new System.Drawing.PointF(l + shear, b - bar),
            new System.Drawing.PointF(r, b - bar),
            new System.Drawing.PointF(r, b),
            new System.Drawing.PointF(l, b),
            new System.Drawing.PointF(r - shear, t + bar),
            new System.Drawing.PointF(l, t + bar),
        });

        return path;
    }

    private static System.Drawing.Icon? LoadEmbeddedIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute);
            var info = Application.GetResourceStream(uri);
            if (info?.Stream is { } stream)
            {
                using (stream)
                {
                    return new System.Drawing.Icon(stream);
                }
            }
        }
        catch
        {
            // ресурс отсутствует — вызывающий возьмёт системную иконку
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _icon.Visible = false;
            _icon.Icon = null;
            _icon.ContextMenuStrip?.Dispose();
            _icon.Dispose();
        }
        catch
        {
            // ignore
        }

        foreach (var icon in _iconCache.Values)
        {
            try { icon.Dispose(); } catch { }
        }
        _iconCache.Clear();

        try { _fallbackIcon?.Dispose(); } catch { }
        _fallbackIcon = null;

        _ = _owner;
    }
}
