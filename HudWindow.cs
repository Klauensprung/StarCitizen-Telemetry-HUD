using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace StarCitizenOverlay;

sealed class HudWindow : Window
{
    private readonly HudVisual visual;
    private readonly Widget widget;
    private bool editing;
    private bool dragging;
    private bool moveGroup;
    private Point lastPosition;
    public event Action<double, double, bool>? Moving;
    public event Action? DragFinished;

    public HudWindow(Widget widget)
    {
        this.widget = widget;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Width = widget.Width;
        Height = widget.Height;
        Left = widget.X;
        Top = widget.Y;
        Content = visual = new HudVisual(widget);
        SourceInitialized += (_, _) => ApplyWindowStyle();
        LocationChanged += (_, _) => ReportMovement();
        MouseLeftButtonDown += (_, e) =>
        {
            if (!editing || e.ButtonState != MouseButtonState.Pressed) return;
            lastPosition = new Point(Left, Top);
            moveGroup = (Keyboard.Modifiers & ModifierKeys.Shift) == 0;
            dragging = true;
            try { DragMove(); }
            finally
            {
                ReportMovement();
                dragging = false;
                DragFinished?.Invoke();
            }
        };
    }

    private void ReportMovement()
    {
        if (!dragging) return;
        var position = new Point(Left, Top);
        var displacement = position - lastPosition;
        if (displacement.X == 0 && displacement.Y == 0) return;
        lastPosition = position;
        Moving?.Invoke(displacement.X, displacement.Y, moveGroup);
    }

    public void SyncPosition()
    {
        Left = widget.X;
        Top = widget.Y;
    }

    public void SetEditing(bool value)
    {
        editing = value;
        visual.Editing = value;
        Cursor = value ? Cursors.SizeAll : null;
        ApplyWindowStyle();
        visual.InvalidateVisual();
    }

    private void ApplyWindowStyle()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        const int toolWindow = 0x80, noActivate = 0x08000000, transparent = 0x20;
        int style = Native.GetWindowLong(handle, -20) | toolWindow | noActivate;
        Native.SetWindowLong(handle, -20, editing ? style & ~transparent : style | transparent);
    }

    public void Refresh(object? value)
    {
        if (Equals(visual.Value, value)) return;
        visual.Value = value;
        visual.InvalidateVisual();
    }

    public static bool IsOnScreen(Widget widget)
    {
        var desktop = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var bounds = new Rect(widget.X, widget.Y, widget.Width, widget.Height);
        bounds.Intersect(desktop);
        return !bounds.IsEmpty && bounds.Width >= 32 && bounds.Height >= 24;
    }
}

sealed class HudVisual(Widget widget) : FrameworkElement
{
    public object? Value { get; set; }
    public bool Editing { get; set; }
    private static readonly Brush White = BrushFrom("#F3FCFF");
    private static readonly Brush Muted = BrushFrom("#B9D0DB");
    private static readonly Brush Track = BrushFrom("#AB354957");
    private static readonly Brush EditFill = BrushFrom("#D0132231");
    private static readonly Brush EditBorder = BrushFrom("#8796C2CD");
    private static readonly Typeface Body = new("Segoe UI");
    private static readonly Typeface Strong = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    internal static Brush BrushFrom(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    internal static (double Min, double Max) Bounds(Widget widget, object? value) =>
        widget.AutomaticRange && value is StarCitizen.LocalTelemetry.Gauge { Max: > 0 } gauge &&
        float.IsFinite(gauge.Max) ? (0, gauge.Max) : (widget.Min, widget.Max);

    internal static double? Fraction(double? number, double min, double max)
    {
        if (number is not double actual || max <= min || !double.IsFinite(max - min)) return null;
        return Math.Clamp((actual - min) / (max - min), 0, 1);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth, height = ActualHeight;
        if (width < 30 || height < 30) return;
        if (Editing)
            dc.DrawRoundedRectangle(EditFill, new Pen(EditBorder, 1), new Rect(0.5, 0.5, width - 1, height - 1), 8, 8);
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, width, height)));
        dc.PushOpacity(widget.Opacity);

        Brush accent = BrushFrom(ColorPickerButton.IsHexColor(widget.Color) ? widget.Color : "#64E4DB");
        bool hasHeader = !string.IsNullOrWhiteSpace(widget.Header);
        string title = hasHeader ? widget.Header : widget.Label;
        double? number = Feed.Number(Value);
        var (min, max) = Bounds(widget, Value);
        double? fraction = Fraction(number, min, max);
        string suffix = string.IsNullOrWhiteSpace(widget.Unit) ? "" : " " + widget.Unit;
        string current = number is double amount ? Format(amount) + suffix : Value switch
        {
            null => "—",
            StarCitizen.LocalTelemetry.Gauge => "—",
            DateTimeOffset time => time.ToLocalTime().ToString("T", CultureInfo.CurrentCulture),
            bool enabled => enabled ? "On" : "Off",
            _ => Value.ToString() ?? "—"
        };

        if (widget.ShowPercent && number is not null)
            current = fraction is double normalized ? Format(normalized * 100) + "%" : "—";

        StateRule? rule = StateRule.Match(widget.StateRules, Value);
        if (rule is not null)
        {
            if (ColorPickerButton.IsHexColor(rule.Color)) accent = BrushFrom(rule.Color);
            string stateText = string.IsNullOrWhiteSpace(rule.Text) ? current : rule.Text;
            double left = 8;
            if (rule.Presentation == StatePresentation.Circle || !rule.ShowText)
            {
                double radius = Math.Min(18, height / 4);
                left = 20 + radius * 2;
                if (!rule.ShowText && hasHeader)
                {
                    const double fontSize = 17, gap = 10;
                    double labelWidth = Math.Max(0, Math.Min(MeasureText(title, fontSize, Strong), width - radius * 2 - gap - 18));
                    double start = (width - labelWidth - gap - radius * 2) / 2;
                    DrawText(dc, title, start, height / 2 - 12, labelWidth + 1, fontSize, White, true);
                    dc.DrawEllipse(accent, null, new Point(start + labelWidth + gap + radius, height / 2), radius, radius);
                }
                else
                {
                    double center = rule.ShowText ? radius + 8 : width / 2;
                    dc.DrawEllipse(accent, null, new Point(center, height / 2), radius, radius);
                }
            }
            if (rule.ShowText)
            {
                DrawText(dc, title, left, Math.Max(4, height / 2 - 22), width - left - 6, 12, Muted);
                DrawText(dc, stateText, left, height / 2 - 5, width - left - 6, 22,
                    rule.Presentation == StatePresentation.Text ? accent : White, true);
            }
        }
        else if (widget.Display == Display.Ring)
        {
            double radius = Math.Min(height - 16, Math.Min(width * .37, 120)) / 2 - 3;
            var center = new Point(radius + 10, height / 2);
            DrawRing(dc, center, radius, fraction ?? 0, accent);
            double x = center.X + radius + 12;
            DrawText(dc, title, x, Math.Max(5, height / 2 - 22), width - x - 5, 12, Muted);
            DrawText(dc, current, x, height / 2 - 5, width - x - 5, 22, White, true);
        }
        else
        {
            DrawText(dc, title, 8, 4, width - 16, 12, Muted);
            DrawText(dc, current, 8, 18, width - 16, widget.Display == Display.Text ? 25 : 19, White, true);
            if (widget.Display is Display.Bar or Display.Range)
            {
                double barWidth = width - 16;
                double y = widget.Display == Display.Range ? height - 27 : height - 14;
                dc.DrawRoundedRectangle(Track, null, new Rect(8, y, barWidth, 5), 2.5, 2.5);
                if (fraction is double position)
                {
                    if (widget.Display == Display.Bar)
                        dc.DrawRoundedRectangle(accent, null, new Rect(8, y, barWidth * position, 5), 2.5, 2.5);
                    else
                        dc.DrawEllipse(accent, null, new Point(8 + position * barWidth, y + 2.5), 5, 5);
                }
                if (widget.Display == Display.Range)
                {
                    DrawText(dc, Format(min), 8, y + 8, barWidth / 2, 11, Muted);
                    DrawText(dc, Format(max), 8 + barWidth / 2, y + 8, barWidth / 2, 11, Muted, right: true);
                }
            }
        }
        dc.Pop();
        dc.Pop();
    }

    private string Format(double number) => number.ToString("F" + widget.Decimals, CultureInfo.CurrentCulture);

    private double MeasureText(string text, double size, Typeface typeface) =>
        new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface,
            size, White, VisualTreeHelper.GetDpi(this).PixelsPerDip).Width;

    private void DrawText(DrawingContext dc, string text, double x, double y, double width, double size, Brush color,
        bool strong = false, bool right = false)
    {
        if (width < 2 || text.Length == 0) return;
        var line = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            strong ? Strong : Body, size, color, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = width,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
            TextAlignment = right ? TextAlignment.Right : TextAlignment.Left
        };
        var shadow = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            strong ? Strong : Body, size, Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = width, MaxLineCount = 1, Trimming = line.Trimming, TextAlignment = line.TextAlignment
        };
        dc.DrawText(shadow, new Point(x + 1, y + 1));
        dc.DrawText(line, new Point(x, y));
    }

    private static void DrawRing(DrawingContext dc, Point center, double radius, double fraction, Brush accent)
    {
        if (radius <= 0) return;
        dc.DrawEllipse(null, new Pen(Track, 5), center, radius, radius);
        if (fraction >= 1)
            dc.DrawEllipse(null, new Pen(accent, 5), center, radius, radius);
        else if (fraction > 0)
        {
            double angle = fraction * Math.Tau;
            Point end = new(center.X + Math.Sin(angle) * radius, center.Y - Math.Cos(angle) * radius);
            var arc = new StreamGeometry();
            using (var context = arc.Open())
            {
                context.BeginFigure(new Point(center.X, center.Y - radius), false, false);
                context.ArcTo(end, new Size(radius, radius), 0, fraction > .5, SweepDirection.Clockwise, true, false);
            }
            dc.DrawGeometry(null, new Pen(accent, 5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, arc);
        }
    }
}

static class Native
{
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    public static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    public static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
}
