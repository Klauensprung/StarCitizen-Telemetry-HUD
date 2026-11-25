using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using StarCitizen.LocalTelemetry;

namespace StarCitizenOverlay;

sealed class TelemetryExplorer : Window
{
    private static readonly Brush Muted = HudVisual.BrushFrom("#B9D0DB");
    private static readonly Brush Accent = HudVisual.BrushFrom("#83E4DA");
    private readonly Feed feed;
    private readonly bool demo;
    private readonly bool pickField;
    private readonly Func<string, object?> sample;
    private readonly StateCatalog? states;
    private readonly TextBox search = new() { MinWidth = 265 };
    private readonly ComboBox category = new() { MinWidth = 140 };
    private readonly CheckBox receivedOnly = new() { Content = "Receiving only" };
    private readonly ListBox list = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent };
    private readonly TextBlock status = new(), count = new(), name = Controller.Heading("", 22), path = new();
    private readonly TextBlock reading = Controller.Heading("—", 30), availability = new(), chartCaption = new(), exampleLabel = new();
    private readonly TextBlock knownStates = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox format = new() { MinWidth = 92 };
    private readonly Viewbox widgetPreview = new() { StretchDirection = StretchDirection.DownOnly, Margin = new Thickness(10) };
    private readonly HistoryChart chart = new() { Height = 82 };
    private readonly Button action;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly Dictionary<string, FieldRow> rows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object?> values = new(StringComparer.Ordinal);
    private Widget? proposed;
    private HudVisual? widgetVisual;
    private bool updating;
    private string? preferred;
    private FieldRow? Selected => list.SelectedItem as FieldRow;
    public event Action<Widget>? Picked;

    public TelemetryExplorer(Feed feed, bool demo, Func<string, object?> sample, bool pickField, string? initialField,
        StateCatalog? states = null)
    {
        this.feed = feed;
        this.demo = demo;
        this.sample = sample;
        this.pickField = pickField;
        this.states = states;
        preferred = initialField ?? "Actor.Vitals.Health";
        Title = "Telemetry explorer";
        Width = 1030;
        Height = 730;
        MinWidth = 790;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
#pragma warning disable WPF0001
        ThemeMode = ThemeMode.Dark;
#pragma warning restore WPF0001
        Background = HudVisual.BrushFrom("#101824");
        Foreground = HudVisual.BrushFrom("#E2F1F6");
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;

        var root = new DockPanel { Background = Background };
        Content = root;
        var heading = new StackPanel { Margin = new Thickness(24, 17, 24, 12) };
        DockPanel.SetDock(heading, Dock.Top);
        root.Children.Add(heading);
        heading.Children.Add(Controller.Heading(Title, 25));
        status.Foreground = Accent;
        status.Margin = new Thickness(0, 3, 0, 0);
        heading.Children.Add(status);

        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition());
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(390) });
        root.Children.Add(columns);
        var browser = new DockPanel { Margin = new Thickness(18, 13, 18, 14) };
        columns.Children.Add(Controller.PanelFrame(browser, "#131E2B", new Thickness(0, 1, 1, 0)));
        var filters = new WrapPanel { Margin = new Thickness(0, 0, 0, 9) };
        DockPanel.SetDock(filters, Dock.Top);
        browser.Children.Add(filters);
        AddFilter(filters, "Search fields", search);
        AddFilter(filters, "Category", category);
        receivedOnly.Margin = new Thickness(0, 22, 0, 6);
        filters.Children.Add(receivedOnly);
        count.Foreground = Muted;
        count.Margin = new Thickness(3, 0, 3, 7);
        DockPanel.SetDock(count, Dock.Top);
        browser.Children.Add(count);
        browser.Children.Add(list);

        var detail = new DockPanel();
        var detailFrame = Controller.PanelFrame(detail, "#101824", new Thickness(0, 1, 0, 0));
        Grid.SetColumn(detailFrame, 1);
        columns.Children.Add(detailFrame);
        var footer = new StackPanel { Margin = new Thickness(20, 9, 20, 11) };
        DockPanel.SetDock(footer, Dock.Bottom);
        detail.Children.Add(footer);
        action = Controller.AddButton(footer, pickField ? "Use this field" : "Add as widget", () =>
        {
            if (proposed is not null) Picked?.Invoke(proposed with { });
        });
        action.Background = Accent;
        action.Foreground = Background;

        var details = new StackPanel { Margin = new Thickness(20, 12, 20, 6) };
        detail.Children.Add(new ScrollViewer { Content = details, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        details.Children.Add(name);
        path.Foreground = Muted;
        path.TextWrapping = TextWrapping.Wrap;
        path.FontFamily = new FontFamily("Consolas");
        path.FontSize = 11;
        path.Margin = new Thickness(0, 3, 0, 14);
        details.Children.Add(path);
        details.Children.Add(reading);
        availability.Foreground = Muted;
        availability.TextWrapping = TextWrapping.Wrap;
        details.Children.Add(availability);
        knownStates.Foreground = Muted;
        knownStates.Margin = new Thickness(0, 6, 0, 0);
        details.Children.Add(knownStates);
        chartCaption.Foreground = Muted;
        chartCaption.Margin = new Thickness(0, 14, 0, 6);
        details.Children.Add(chartCaption);
        details.Children.Add(chart);

        var previewHeader = new DockPanel { Margin = new Thickness(0, 15, 0, 7) };
        details.Children.Add(previewHeader);
        DockPanel.SetDock(format, Dock.Right);
        previewHeader.Children.Add(format);
        previewHeader.Children.Add(Controller.Heading("Widget preview", 16));
        var frame = Controller.PanelFrame(widgetPreview, "#0B131E", new Thickness(1));
        frame.CornerRadius = new CornerRadius(6);
        frame.Height = 106;
        details.Children.Add(frame);
        exampleLabel.Foreground = Muted;
        exampleLabel.Margin = new Thickness(0, 6, 0, 0);
        details.Children.Add(exampleLabel);

        search.TextChanged += (_, _) => FilterRows();
        category.SelectionChanged += (_, _) => FilterRows();
        receivedOnly.Checked += (_, _) => FilterRows();
        receivedOnly.Unchecked += (_, _) => FilterRows();
        list.SelectionChanged += (_, _) => { if (!updating) SelectField(); };
        format.SelectionChanged += (_, _) => { if (!updating) BuildPreview(); };
        timer.Tick += (_, _) => Refresh();
        Closed += (_, _) => timer.Stop();
        Refresh();
        timer.Start();
    }

    private static void AddFilter(Panel filters, string title, Control control)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 9, 4) };
        stack.Children.Add(new Label { Content = title, Target = control, Padding = new Thickness(0, 0, 0, 4) });
        stack.Children.Add(control);
        filters.Children.Add(stack);
    }

    private void Refresh()
    {
        status.Text = demo ? "Demo mode · sample telemetry" : feed.Status;
        bool added = false;
        foreach (var (key, type) in feed.Catalog)
        {
            if (rows.ContainsKey(key)) continue;
            rows[key] = new FieldRow(key, type);
            added = true;
        }

        values.Clear();
        foreach (var row in rows.Values)
        {
            var value = demo ? sample(row.Path) : feed.Read(row.Path);
            values[row.Path] = value;
            row.Update(value);
        }

        if (added)
        {
            var active = category.SelectedItem as string ?? "All fields";
            updating = true;
            category.ItemsSource = new[] { "All fields" }.Concat(rows.Values.Select(row => row.Category).Distinct().Order()).ToArray();
            category.SelectedItem = active;
            updating = false;
        }
        FilterRows();
        UpdateDetail(true);
    }

    private void FilterRows()
    {
        if (updating) return;
        var selected = Selected;
        string active = category.SelectedItem as string ?? "All fields";
        string[] terms = search.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var visible = rows.Values.Where(row =>
                (active == "All fields" || row.Category == active) &&
                (receivedOnly.IsChecked != true || values.GetValueOrDefault(row.Path) is not null) &&
                terms.All(term => row.SearchText.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(row => row.Path, StringComparer.Ordinal).ToArray();
        int receiving = visible.Count(row => values.GetValueOrDefault(row.Path) is not null);
        count.Text = visible.Length == 0 ? "No matching fields"
            : receivedOnly.IsChecked == true ? $"{receiving} fields receiving"
            : $"{visible.Length} fields  ·  {receiving} receiving";
        if (visible.SequenceEqual(list.Items.OfType<FieldRow>())) return;

        updating = true;
        list.ItemsSource = visible;
        var desired = visible.FirstOrDefault(row => row.Path == preferred)
            ?? visible.FirstOrDefault(row => values.GetValueOrDefault(row.Path) is not null)
            ?? visible.FirstOrDefault();
        list.SelectedItem = desired;
        if (desired is not null && !ReferenceEquals(selected, desired)) list.ScrollIntoView(desired);
        updating = false;
        if (!ReferenceEquals(selected, desired)) SelectField();
    }

    private void SelectField()
    {
        chart.Clear();
        if (Selected is { } row) preferred = row.Path;
        updating = true;
        format.ItemsSource = Selected is { IsNumeric: true } ? Enum.GetValues<Display>() : new[] { Display.Text };
        format.SelectedItem = Selected is { Type: var type } && type == typeof(Gauge) ? Display.Bar : Display.Text;
        updating = false;
        BuildPreview();
        UpdateDetail(true);
    }

    private void BuildPreview()
    {
        if (Selected is not { } row || format.SelectedItem is not Display style)
        {
            proposed = null;
            widgetVisual = null;
            widgetPreview.Child = null;
            action.IsEnabled = false;
            return;
        }

        var live = values.GetValueOrDefault(row.Path);
        double maximum = row.Path.EndsWith(".Fraction", StringComparison.Ordinal) ? 1 : 100;
        double minimum = 0;
        if (maximum == 100 && Feed.Number(live) is double value)
        {
            if (value < 0) minimum = Math.Min(-100, value * 1.2);
            if (value > 100) maximum = value * 1.2;
        }

        proposed = new Widget
        {
            Field = row.Path, Label = row.Label, Display = style, Min = minimum, Max = maximum,
            ShowPercent = style != Display.Text && (row.Type == typeof(Gauge) || maximum == 1),
            AutomaticRange = row.Type == typeof(Gauge)
        };
        widgetVisual = new HudVisual(proposed) { Width = proposed.Width, Height = proposed.Height };
        widgetPreview.Child = widgetVisual;
        action.IsEnabled = true;
        UpdateDetail(false);
    }

    private void UpdateDetail(bool takeSample)
    {
        var selected = Selected;
        name.Text = selected?.Label ?? "Select a field";
        path.Text = selected?.Path ?? "";
        object? value = selected is null ? null : values.GetValueOrDefault(selected.Path);
        reading.Text = FormatValue(value);
        availability.Text = selected is null ? "Choose a field to inspect its value and widget formats."
            : selected.Kind + "  ·  " + (value is null ? "No value in the current stream" : demo ? "Demo value" : "Receiving");
        var names = selected is null || selected.IsNumeric ? [] : states?.Suggestions(selected.Path, selected.Type, value) ?? [];
        knownStates.Visibility = names.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        knownStates.Text = "Known states · " + string.Join(", ", names.Take(8)) + (names.Length > 8 ? " …" : "");
        knownStates.ToolTip = string.Join(", ", names);
        chart.Visibility = chartCaption.Visibility = selected is { IsNumeric: true } ? Visibility.Visible : Visibility.Collapsed;
        if (selected is { IsNumeric: true })
        {
            if (takeSample) chart.Add(Feed.Number(value));
            chartCaption.Text = chart.HasSamples ? "Recent samples · up to 60 seconds" : "Recent samples · waiting for values";
        }

        if (widgetVisual is null || selected is null) { exampleLabel.Text = ""; return; }
        exampleLabel.Text = value is null ? "Preview uses an example value" : demo ? "Preview uses demo telemetry" : "Preview uses live telemetry";
        object? rendered = value ?? selected.Example();
        if (Equals(widgetVisual.Value, rendered)) return;
        widgetVisual.Value = rendered;
        widgetVisual.InvalidateVisual();
    }

    internal static string FormatValue(object? value) => value switch
    {
        null => "—",
        Gauge gauge => $"{gauge.Current:G6} / {gauge.Max:G6}",
        DateTimeOffset time => time.ToLocalTime().ToString("T", CultureInfo.CurrentCulture),
        bool state => state ? "On" : "Off",
        IFormattable integer when value is byte or sbyte or short or ushort or int or uint or long or ulong =>
            integer.ToString("N0", CultureInfo.CurrentCulture),
        _ when Feed.Number(value) is double number => number.ToString("G6", CultureInfo.CurrentCulture),
        _ => value.ToString() ?? "—"
    };

    private sealed class FieldRow : ListBoxItem
    {
        private readonly TextBlock current = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 3, 0) };
        public string Path { get; }
        public Type Type { get; }
        public string Label { get; }
        public string Category { get; }
        public string SearchText => Path + " " + Label + " " + Category + " " + Kind;
        public bool IsNumeric => Type == typeof(Gauge) || (Type.IsPrimitive && Type != typeof(bool) && Type != typeof(char));
        public string Kind => Type == typeof(Gauge) ? "Gauge" : Type == typeof(bool) ? "Switch" : Type.IsEnum ? "State"
            : Type == typeof(DateTimeOffset) ? "Time" : IsNumeric ? "Number" : "Text";

        public FieldRow(string path, Type type)
        {
            Path = path;
            Type = type;
            string[] parts = path.Split('.');
            Category = parts[0] switch
            {
                "Actor" => "Player", "Vehicle" => "Ship", "Weapons" => "Weapons", "Turret" => "Turret",
                "ForceFeedback" => "Force feedback", "Events" => "Events", _ => "Stream"
            };
            Label = path switch
            {
                Feed.Ammo => "Countermeasure ammo",
                Feed.Decoys => "Decoy ammo",
                Feed.Noise => "Noise ammo",
                _ when parts.Length >= 3 && parts[^1] is "Current" or "Max" or "Fraction" =>
                    Humanize(parts[^2]) + " · " + Humanize(parts[^1]),
                _ => Humanize(parts[^1])
            };
            HorizontalContentAlignment = HorizontalAlignment.Stretch;
            ToolTip = Path;
            var row = new DockPanel { Margin = new Thickness(6, 5, 4, 5) };
            DockPanel.SetDock(current, Dock.Right);
            current.Foreground = Accent;
            current.MaxWidth = 126;
            current.TextTrimming = TextTrimming.CharacterEllipsis;
            row.Children.Add(current);
            var titles = new StackPanel();
            titles.Children.Add(new TextBlock { Text = Label, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            titles.Children.Add(new TextBlock { Text = Path, Foreground = Muted, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis });
            row.Children.Add(titles);
            Content = row;
        }

        public void Update(object? value)
        {
            string content = FormatValue(value);
            if (current.Text != content) current.Text = content;
        }

        public object Example() => Type == typeof(Gauge) ? new Gauge(72, 100)
            : Path.EndsWith(".Fraction", StringComparison.Ordinal) ? .72
            : IsNumeric ? 42 : Type == typeof(bool) ? true : Type.IsEnum ? Enum.GetValues(Type).GetValue(0) ?? "Example"
            : Type == typeof(DateTimeOffset) ? DateTimeOffset.Now : "Example";

        private static string Humanize(string part) =>
            Regex.Replace(part, "(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ");
    }
}

sealed class HistoryChart : FrameworkElement
{
    private const int Capacity = 120;
    private readonly Queue<double?> samples = new();
    private static readonly Brush ChartPanel = HudVisual.BrushFrom("#0B131E");
    private static readonly Brush GridLine = HudVisual.BrushFrom("#293B4B");
    private static readonly Brush ChartLine = HudVisual.BrushFrom("#83E4DA");
    private static readonly Brush LabelBrush = HudVisual.BrushFrom("#B9D0DB");
    public bool HasSamples => samples.Any(value => value.HasValue);

    public void Clear() { samples.Clear(); InvalidateVisual(); }
    public void Add(double? value)
    {
        if (samples.Count == Capacity) samples.Dequeue();
        samples.Enqueue(value);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawing)
    {
        if (ActualWidth < 70 || ActualHeight < 24) return;
        drawing.DrawRoundedRectangle(ChartPanel, new Pen(GridLine, 1),
            new Rect(.5, .5, ActualWidth - 1, ActualHeight - 1), 6, 6);
        if (!HasSamples) return;

        var numbers = samples.OfType<double>().ToArray();
        double min = numbers.Min(), max = numbers.Max();
        string minimum = min.ToString("G4", CultureInfo.CurrentCulture);
        string maximum = max.ToString("G4", CultureInfo.CurrentCulture);
        double width = ActualWidth - 65, top = 10, bottom = ActualHeight - 10;
        if (min == max)
            DrawLabel(drawing, minimum, ActualWidth - 47, (top + bottom) / 2 - 7);
        else
        {
            DrawLabel(drawing, maximum, ActualWidth - 47, top - 5);
            DrawLabel(drawing, minimum, ActualWidth - 47, bottom - 9);
        }

        foreach (double y in new[] { top, (top + bottom) / 2, bottom })
            drawing.DrawLine(new Pen(GridLine, 1), new Point(9, y), new Point(9 + width, y));

        Point? previous = null;
        int index = Capacity - samples.Count;
        var pen = new Pen(ChartLine, 1.6);
        foreach (double? sample in samples)
        {
            double x = 9 + width * index++ / (Capacity - 1);
            if (sample is not double value) { previous = null; continue; }
            double y = min == max ? (top + bottom) / 2 : bottom - (value - min) / (max - min) * (bottom - top);
            var point = new Point(x, y);
            if (previous is Point last) drawing.DrawLine(pen, last, point);
            else drawing.DrawEllipse(ChartLine, null, point, 1.6, 1.6);
            previous = point;
        }
    }

    private void DrawLabel(DrawingContext drawing, string label, double x, double y) =>
        drawing.DrawText(new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 10, LabelBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(x, y));
}
