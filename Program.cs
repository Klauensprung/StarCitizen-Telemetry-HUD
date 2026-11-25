using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using StarCitizen.LocalTelemetry;
using Forms = System.Windows.Forms;

namespace StarCitizenOverlay;

static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        using var wake = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\StarCitizenOverlay.OpenSettings");
        using var singleton = new Mutex(true, @"Local\StarCitizenOverlay", out bool first);
        if (!first)
        {
            wake.Set();
            return 0;
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var window = new Controller(args.Contains("--demo"), args.Contains("--explorer"), args.Contains("--hotkeys"), args.Contains("--voice"));
        app.MainWindow = window;
        var registration = ThreadPool.RegisterWaitForSingleObject(wake, (_, _) =>
        {
            if (!window.Dispatcher.HasShutdownStarted)
                _ = window.Dispatcher.BeginInvoke(window.OpenSettings);
        }, null, Timeout.Infinite, false);
        try
        {
            window.Show();
            return app.Run();
        }
        finally { registration.Unregister(null); }
    }
}

sealed class Controller : Window
{
    private readonly Feed feed;
    private readonly StateCatalog stateCatalog;
    private readonly GameHotkeys gameHotkeys;
    private readonly VoiceCommands voice;
    private readonly Layout layout;
    private readonly CancellationTokenSource stop = new();
    private readonly DispatcherTimer timer;
    private readonly List<(Widget Widget, HudWindow Window)> hud = [];
    private readonly Forms.NotifyIcon tray;
    private readonly System.Drawing.Icon trayIcon;
    private readonly ListBox widgets = new() { MinHeight = 155 };
    private readonly ComboBox field = new() { IsEditable = true, IsTextSearchEnabled = true };
    private readonly ComboBox display = new() { ItemsSource = Enum.GetValues<Display>() };
    private readonly ComboBox group = new() { IsEditable = true, IsTextSearchEnabled = true };
    private readonly TextBox label = new(), headerText = new(), min = new(), max = new(), unit = new(), decimals = new();
    private readonly ColorPickerButton color = new();
    private readonly TextBox opacity = new(), width = new(), height = new();
    private readonly CheckBox enabled = new() { Content = "Show widget" };
    private readonly CheckBox automatic = new() { Content = "Use gauge maximum where available" };
    private readonly CheckBox percent = new() { Content = "Show value as a percentage of the range" };
    private readonly TextBlock connection = new(), mode = new(), notice = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button editButton, hideButton, applyButton, discardButton, removeButton;
    private readonly Button stateRulesButton;
    private StateRule[] draftRules = [];
    private readonly Border preview = new();
    private Widget? previewWidget;
    private HudVisual? previewVisual;
    private TelemetryExplorer? explorer;
    private HotkeyWindow? hotkeyWindow;
    private VoiceWindow? voiceWindow;
    private bool loadingWidget;
    private string? validationNotice;
    private readonly FrameworkElement editor;
    private readonly HashSet<string> collapsedGroups = new(StringComparer.OrdinalIgnoreCase);
    private Widget? selectedWidget, dragCandidate, draggedWidget;
    private Point dragOrigin;
    private bool rebuildingList;
    private bool editing, hidden, quitting, hotkeysAvailable;
    private readonly bool demo;
    private IntPtr handle;

    public Controller(bool demo, bool openExplorer = false, bool openHotkeys = false, bool openVoice = false)
    {
        this.demo = demo;
        stateCatalog = new StateCatalog(demo);
        feed = new Feed(demo ? null : stateCatalog);
        gameHotkeys = new GameHotkeys(feed, demo, stop.Token);
        voice = new VoiceCommands(demo, stop.Token, RunVoiceActionAsync);
        string? loadError = stateCatalog.ReadError;
        try { layout = Layout.Load(demo); }
        catch (Exception ex) { layout = new(); loadError = "Couldn’t load saved layout. " + ex.Message; }

        Title = "Star Citizen Overlay";
        var icon = new Uri("pack://application:,,,/Assets/StarCitizenTelemetryHud.ico");
        Icon = BitmapFrame.Create(icon);
        using (var stream = Application.GetResourceStream(icon).Stream)
        using (var resource = new System.Drawing.Icon(stream))
            trayIcon = (System.Drawing.Icon)resource.Clone();
        Width = 1080;
        Height = 744;
        MinWidth = 920;
        MinHeight = 565;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
#pragma warning disable WPF0001
        ThemeMode = ThemeMode.Dark;
#pragma warning restore WPF0001
        Background = HudVisual.BrushFrom("#101824");
        Foreground = HudVisual.BrushFrom("#E2F1F6");
        Resources[SystemColors.AccentColorBrushKey] = HudVisual.BrushFrom("#83E4DA");
        Resources[SystemColors.AccentColorLight1BrushKey] = HudVisual.BrushFrom("#83E4DA");
        Resources[SystemColors.AccentColorLight2BrushKey] = HudVisual.BrushFrom("#ADEDE6");
        Resources[SystemColors.AccentColorDark1BrushKey] = HudVisual.BrushFrom("#244356");
        Resources[SystemColors.AccentColorDark2BrushKey] = HudVisual.BrushFrom("#244356");
        Resources[SystemColors.AccentColorDark3BrushKey] = HudVisual.BrushFrom("#244356");
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;

        var body = new DockPanel { Background = Background };
        Content = body;
        var header = new DockPanel { Margin = new Thickness(24, 15, 24, 15), LastChildFill = true };
        DockPanel.SetDock(header, Dock.Top);
        body.Children.Add(header);
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(toolbar, Dock.Right);
        header.Children.Add(toolbar);
        AddButton(toolbar, "Telemetry explorer", () => OpenExplorer(false));
        AddButton(toolbar, "Hotkeys", OpenHotkeys);
        editButton = AddButton(toolbar, "", ToggleEditing);
        hideButton = AddButton(toolbar, "", ToggleVisibility);
        AddButton(toolbar, "To tray", Hide);
        AddButton(toolbar, "Exit", Exit);
        var heading = new StackPanel();
        header.Children.Add(heading);
        heading.Children.Add(Heading("Star Citizen Overlay", 23));
        connection.Foreground = HudVisual.BrushFrom("#83E4DA");
        connection.Margin = new Thickness(0, 3, 0, 0);
        heading.Children.Add(connection);

        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(214) });
        columns.ColumnDefinitions.Add(new ColumnDefinition());
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(358) });
        body.Children.Add(columns);

        var navigation = new DockPanel { Margin = new Thickness(16, 17, 16, 12) };
        columns.Children.Add(PanelFrame(navigation, "#131E2B", new Thickness(0, 1, 1, 0)));
        var navTitle = Heading("Widgets", 17);
        navTitle.Margin = new Thickness(4, 0, 0, 13);
        DockPanel.SetDock(navTitle, Dock.Top);
        navigation.Children.Add(navTitle);
        var listActions = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(listActions, Dock.Bottom);
        navigation.Children.Add(listActions);
        AddButton(listActions, "+ Add widget", AddWidget);
        removeButton = AddButton(listActions, "Remove selected", RemoveWidget);
        widgets.Background = Brushes.Transparent;
        widgets.BorderThickness = new Thickness(0);
        widgets.Padding = new Thickness(0);
        navigation.Children.Add(widgets);
        widgets.SelectionChanged += (_, _) =>
        {
            if (rebuildingList || (widgets.SelectedItem as ListBoxItem)?.Tag is not Widget selected) return;
            if (ReferenceEquals(selectedWidget, selected)) return;
            selectedWidget = selected;
            LoadWidget();
        };
        widgets.PreviewMouseMove += BeginWidgetDrag;
        widgets.PreviewMouseLeftButtonUp += (_, _) => dragCandidate = null;

        var previewPanel = new DockPanel { Margin = new Thickness(22, 18, 22, 15) };
        var previewFrame = PanelFrame(previewPanel, "#101824", new Thickness(0, 1, 1, 0));
        Grid.SetColumn(previewFrame, 1);
        columns.Children.Add(previewFrame);
        var previewHeading = Heading("Widget preview", 17);
        DockPanel.SetDock(previewHeading, Dock.Top);
        previewPanel.Children.Add(previewHeading);
        mode.TextWrapping = TextWrapping.Wrap;
        mode.Foreground = HudVisual.BrushFrom("#B9D0DB");
        mode.Margin = new Thickness(0, 12, 0, 0);
        DockPanel.SetDock(mode, Dock.Bottom);
        previewPanel.Children.Add(mode);
        var previewSurface = PanelFrame(preview, "#0B131E", new Thickness(1));
        previewSurface.Margin = new Thickness(0, 13, 0, 0);
        previewSurface.CornerRadius = new CornerRadius(6);
        previewPanel.Children.Add(previewSurface);

        var inspector = new DockPanel();
        var inspectorFrame = PanelFrame(inspector, "#131E2B", new Thickness(0, 1, 0, 0));
        Grid.SetColumn(inspectorFrame, 2);
        columns.Children.Add(inspectorFrame);
        var inspectorTitle = Heading("Widget settings", 17);
        inspectorTitle.Margin = new Thickness(18, 17, 18, 11);
        DockPanel.SetDock(inspectorTitle, Dock.Top);
        inspector.Children.Add(inspectorTitle);
        var footer = new StackPanel { Margin = new Thickness(18, 10, 18, 12) };
        DockPanel.SetDock(footer, Dock.Bottom);
        inspector.Children.Add(footer);
        notice.Foreground = HudVisual.BrushFrom("#FFC18A");
        notice.Text = loadError ?? "";
        footer.Children.Add(notice);
        var footerActions = new StackPanel { Orientation = Orientation.Horizontal };
        footer.Children.Add(footerActions);
        applyButton = AddButton(footerActions, "Apply changes", Apply);
        applyButton.Background = HudVisual.BrushFrom("#83E4DA");
        applyButton.Foreground = HudVisual.BrushFrom("#101824");
        discardButton = AddButton(footerActions, "Discard edits", LoadWidget);

        var form = new StackPanel { Margin = new Thickness(18, 0, 14, 8) };
        editor = form;
        var scroll = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        inspector.Children.Add(scroll);
        var source = AddGroup(form, "Data source", true);
        AddField(source, "Telemetry field", field);
        AddButton(source, "Browse telemetry", () => OpenExplorer(true));
        AddButton(source, "Refresh field list", RefreshFields);
        AddField(source, "Widget name", label);
        var format = AddGroup(form, "Display", true);
        AddField(format, "Display format", display);
        AddField(format, "Overlay header (optional)", headerText);
        AddField(format, "", enabled);
        AddField(format, "Overlay group (type a name or leave blank)", group);
        stateRulesButton = AddButton(format, "Edit state rules", EditStateRules);
        var range = AddGroup(form, "Range and values", false);
        AddField(range, "", automatic);
        AddPair(range, "Minimum", min, "Maximum", max);
        AddField(range, "", percent);
        AddPair(range, "Unit suffix", unit, "Decimal places (0–4)", decimals);
        var appearance = AddGroup(form, "Appearance and size", false);
        AddPair(appearance, "Accent color", color, "Opacity (10–100%)", opacity);
        AddPair(appearance, "Width (120–800)", width, "Height (64–450)", height);
        form.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => UpdateDraft()));
        display.SelectionChanged += (_, _) => UpdateDraft();
        color.ColorPicked += (_, _) => UpdateDraft();
        field.SelectionChanged += (_, _) => Dispatcher.BeginInvoke(UpdateDraft, DispatcherPriority.Background);
        group.SelectionChanged += (_, _) => Dispatcher.BeginInvoke(UpdateDraft, DispatcherPriority.Background);
        foreach (var checkBox in new[] { enabled, automatic, percent })
        {
            checkBox.Checked += (_, _) => UpdateDraft();
            checkBox.Unchecked += (_, _) => UpdateDraft();
        }

        tray = new Forms.NotifyIcon
        {
            Icon = trayIcon,
            Text = "Star Citizen Overlay",
            Visible = true,
            ContextMenuStrip = new Forms.ContextMenuStrip()
        };
        tray.ContextMenuStrip.Items.Add("Open settings", null, (_, _) => OpenSettings());
        tray.ContextMenuStrip.Items.Add("Telemetry explorer", null, (_, _) =>
        {
            OpenSettings();
            OpenExplorer(false);
        });
        tray.ContextMenuStrip.Items.Add("Hotkeys", null, (_, _) =>
        {
            OpenSettings();
            OpenHotkeys();
        });
        tray.ContextMenuStrip.Items.Add("Voice commands", null, (_, _) =>
        {
            OpenSettings();
            OpenVoice();
        });
        tray.ContextMenuStrip.Items.Add("Toggle drag mode", null, (_, _) => ToggleEditing());
        tray.ContextMenuStrip.Items.Add("Toggle overlay visibility", null, (_, _) => ToggleVisibility());
        tray.ContextMenuStrip.Items.Add("Exit", null, (_, _) => Exit());
        tray.DoubleClick += (_, _) => OpenSettings();

        RefreshFields();
        RebuildList(layout.Widgets.FirstOrDefault());
        SourceInitialized += (_, _) => RegisterHotkeys();
        Loaded += (_, _) =>
        {
            RebuildHud();
            if (!demo) _ = Task.Run(() => feed.RunAsync(stop.Token));
            if (openExplorer) OpenExplorer(false);
            if (openHotkeys) OpenHotkeys();
            if (openVoice) OpenVoice();
        };
        Closing += OnClosing;
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) => RefreshHud();
        timer.Start();
        UpdateMode();
    }

    internal static TextBlock Heading(string title, double size) => new()
    {
        Text = title, FontSize = size, FontWeight = FontWeights.SemiBold
    };

    internal static Border PanelFrame(UIElement content, string background, Thickness border) => new()
    {
        Child = content, Background = HudVisual.BrushFrom(background),
        BorderBrush = HudVisual.BrushFrom("#293B4B"), BorderThickness = border
    };

    private static StackPanel AddGroup(Panel parent, string title, bool expanded)
    {
        var group = new StackPanel();
        parent.Children.Add(new Expander
        {
            Header = title, Content = group, IsExpanded = expanded,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(10, 7, 10, 6),
            Margin = new Thickness(0, 0, 0, 7)
        });
        return group;
    }

    internal static Button AddButton(Panel panel, string title, Action action)
    {
        var button = new Button
        {
            Content = title, Padding = new Thickness(11, 6, 11, 6), MinHeight = 31,
            Margin = new Thickness(0, 0, 7, 5), HorizontalAlignment = HorizontalAlignment.Left,
            Background = HudVisual.BrushFrom("#244356"), Foreground = Brushes.White,
            BorderBrush = HudVisual.BrushFrom("#4D7586")
        };
        button.Click += (_, _) => action();
        panel.Children.Add(button);
        return button;
    }

    private static void AddField(Panel form, string title, Control control)
    {
        var label = new Label
        {
            Content = title, Target = control, Foreground = HudVisual.BrushFrom("#C2D7E1"),
            Padding = new Thickness(0, 0, 0, 3)
        };
        if (title.Length > 0) form.Children.Add(label);
        control.Margin = new Thickness(0, 0, 0, 6);
        control.Padding = new Thickness(5, 3, 5, 3);
        if (control is CheckBox) control.Foreground = label.Foreground;
        form.Children.Add(control);
    }

    private static void AddPair(Panel form, string first, Control firstControl, string second, Control secondControl)
    {
        var pair = new Grid();
        pair.ColumnDefinitions.Add(new ColumnDefinition());
        pair.ColumnDefinitions.Add(new ColumnDefinition());
        var left = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        var right = new StackPanel();
        Grid.SetColumn(right, 1);
        AddField(left, first, firstControl);
        AddField(right, second, secondControl);
        pair.Children.Add(left);
        pair.Children.Add(right);
        form.Children.Add(pair);
    }

    private Widget? Selected => selectedWidget;

    private void RebuildList(Widget? selected, bool reloadEditor = true)
    {
        rebuildingList = true;
        selectedWidget = selected;
        widgets.Items.Clear();
        var groups = layout.Widgets.ToLookup(widget => widget.Group?.Trim() ?? "", StringComparer.OrdinalIgnoreCase);
        foreach (string name in groups.Select(group => group.Key).Append("").Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(name => name.Length == 0 ? 1 : 0).ThenBy(name => name, StringComparer.CurrentCultureIgnoreCase))
        {
            bool expanded = !collapsedGroups.Contains(name);
            string title = name.Length == 0 ? "Ungrouped" : name;
            var headingButton = new Button
            {
                HorizontalContentAlignment = HorizontalAlignment.Left, MinHeight = 30,
                Padding = new Thickness(6, 3, 6, 3), Background = HudVisual.BrushFrom("#1C2B3B"),
                Foreground = Foreground, BorderBrush = HudVisual.BrushFrom("#293B4B")
            };
            var heading = new TextBlock
            {
                Text = (expanded ? "▾  " : "▸  ") + title,
                TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = FontWeights.SemiBold
            };
            headingButton.Content = heading;
            headingButton.ToolTip = title;
            System.Windows.Automation.AutomationProperties.SetName(headingButton, (expanded ? "Collapse " : "Expand ") + title);
            headingButton.Click += (_, _) =>
            {
                bool restoreFocus = headingButton.IsKeyboardFocusWithin;
                if (!collapsedGroups.Add(name)) collapsedGroups.Remove(name);
                RebuildList(Selected, false);
                var replacement = widgets.Items.OfType<ListBoxItem>()
                    .First(item => item.Tag is string key && Layout.SameGroup(key, name));
                widgets.ScrollIntoView(replacement);
                if (restoreFocus) ((Button)replacement.Content).Focus();
            };
            var headerItem = new ListBoxItem
            {
                Content = headingButton, Tag = name, Focusable = false, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(0), Margin = new Thickness(0, 5, 0, 0)
            };
            ConfigureGroupDrop(headerItem, name);
            widgets.Items.Add(headerItem);
            if (!expanded) continue;

            foreach (var widget in groups[name])
            {
                var summary = new StackPanel { Margin = new Thickness(12, 5, 4, 5) };
                summary.Children.Add(new TextBlock
                {
                    Text = widget.Label, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis
                });
                summary.Children.Add(new TextBlock
                {
                    Text = widget.Display + (widget.Enabled ? "" : " · Hidden"), FontSize = 12,
                    Foreground = HudVisual.BrushFrom("#B9D0DB"), Margin = new Thickness(0, 3, 0, 0)
                });
                var item = new ListBoxItem { Content = summary, Tag = widget, ToolTip = widget.Field };
                item.PreviewMouseLeftButtonDown += (_, args) =>
                {
                    dragCandidate = widget;
                    dragOrigin = args.GetPosition(widgets);
                };
                ConfigureGroupDrop(item, name);
                widgets.Items.Add(item);
                if (ReferenceEquals(widget, selected)) widgets.SelectedItem = item;
            }
        }
        rebuildingList = false;
        if (reloadEditor) LoadWidget();
    }

    private void BeginWidgetDrag(object sender, MouseEventArgs args)
    {
        if (dragCandidate is not Widget widget) return;
        if (args.LeftButton != MouseButtonState.Pressed)
        {
            dragCandidate = null;
            return;
        }
        Vector distance = args.GetPosition(widgets) - dragOrigin;
        if (Math.Abs(distance.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(distance.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        dragCandidate = null;
        draggedWidget = widget;
        try { DragDrop.DoDragDrop(widgets, new DataObject("StarCitizenOverlay.Widget", "Widget"), DragDropEffects.Move); }
        finally { draggedWidget = null; }
    }

    private void ConfigureGroupDrop(ListBoxItem item, string targetGroup)
    {
        item.AllowDrop = true;
        item.PreviewDragOver += (_, args) =>
        {
            args.Effects = draggedWidget is Widget source && !Layout.SameGroup(source.Group, targetGroup)
                ? DragDropEffects.Move : DragDropEffects.None;
            args.Handled = true;
        };
        item.PreviewDrop += (_, args) =>
        {
            args.Handled = true;
            args.Effects = draggedWidget is Widget source && MoveToGroup(source, targetGroup)
                ? DragDropEffects.Move : DragDropEffects.None;
        };
    }

    private bool MoveToGroup(Widget widget, string targetGroup)
    {
        if (Layout.SameGroup(widget.Group, targetGroup) || !layout.Widgets.Any(item => ReferenceEquals(item, widget)))
            return false;
        widget.Group = targetGroup;
        collapsedGroups.Remove(targetGroup);
        bool sameSelection = ReferenceEquals(Selected, widget);
        if (sameSelection) RefreshGroupChoices(targetGroup);
        RebuildList(widget, !sameSelection);
        if (widgets.SelectedItem is ListBoxItem selected) widgets.ScrollIntoView(selected);
        Save();
        if (sameSelection) UpdateDraft();
        return true;
    }

    private void RefreshGroupChoices(string text)
    {
        bool wasLoading = loadingWidget;
        loadingWidget = true;
        group.ItemsSource = layout.Widgets.Select(item => item.Group)
            .Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        group.Text = text;
        loadingWidget = wasLoading;
    }

    private void RefreshFields()
    {
        loadingWidget = true;
        string previous = field.Text;
        field.ItemsSource = feed.Fields.Concat(layout.Widgets.Select(widget => widget.Field)).Distinct().Order().ToArray();
        field.Text = previous;
        loadingWidget = false;
    }

    private void LoadWidget()
    {
        var widget = Selected;
        editor.IsEnabled = widget is not null;
        if (widget is null)
        {
            UpdateDraft();
            return;
        }

        loadingWidget = true;
        field.Text = widget.Field;
        label.Text = widget.Label;
        headerText.Text = widget.Header;
        draftRules = widget.StateRules ?? [];
        RefreshGroupChoices(widget.Group ?? "");
        display.SelectedItem = widget.Display;
        automatic.IsChecked = widget.AutomaticRange;
        min.Text = widget.Min.ToString("G", CultureInfo.CurrentCulture);
        max.Text = widget.Max.ToString("G", CultureInfo.CurrentCulture);
        unit.Text = widget.Unit;
        decimals.Text = widget.Decimals.ToString(CultureInfo.CurrentCulture);
        color.HexColor = widget.Color;
        opacity.Text = Math.Round(widget.Opacity * 100).ToString(CultureInfo.CurrentCulture);
        width.Text = widget.Width.ToString(CultureInfo.CurrentCulture);
        height.Text = widget.Height.ToString(CultureInfo.CurrentCulture);
        percent.IsChecked = widget.ShowPercent;
        enabled.IsChecked = widget.Enabled;
        loadingWidget = false;
        UpdateDraft();
    }

    private void AddWidget()
    {
        InsertWidget(new Widget());
    }

    private void InsertWidget(Widget widget)
    {
        double step = (layout.Widgets.Count % 8) * 34;
        widget.X = 100 + step;
        widget.Y = 150 + step;
        if (Selected is Widget selected && !string.IsNullOrWhiteSpace(selected.Group))
        {
            widget.Group = selected.Group;
            widget.X = selected.X;
            widget.Y = selected.Y + selected.Height + 12;
        }
        layout.Widgets.Add(widget);
        RebuildList(widget);
        RebuildHud();
        Save();
    }

    private void OpenExplorer(bool pickField)
    {
        if (explorer is not null)
        {
            explorer.Activate();
            return;
        }

        var window = new TelemetryExplorer(feed, demo, Sample, pickField, pickField ? field.Text : null, stateCatalog);
        window.Owner = this;
        window.Picked += widget =>
        {
            window.Close();
            if (pickField)
            {
                RefreshFields();
                field.Text = widget.Field;
                UpdateDraft();
            }
            else
            {
                RefreshFields();
                InsertWidget(widget);
            }
        };
        window.Closed += (_, _) => explorer = null;
        explorer = window;
        window.Show();
    }

    private void RemoveWidget()
    {
        if (Selected is not Widget widget) return;
        layout.Widgets.RemoveAll(item => ReferenceEquals(item, widget));
        RebuildList(layout.Widgets.FirstOrDefault());
        RebuildHud();
        Save();
    }

    private void Apply()
    {
        if (Selected is not Widget current) return;
        if (!TryReadDraft(current, out Widget widget, out var error))
        {
            notice.Text = error;
            return;
        }
        layout.Widgets[layout.Widgets.FindIndex(item => ReferenceEquals(item, current))] = widget;
        collapsedGroups.Remove(widget.Group);
        RebuildList(widget);
        RebuildHud();
        Save();
    }

    private void EditStateRules()
    {
        if (Selected is not Widget widget || string.IsNullOrWhiteSpace(field.Text)) return;
        string source = field.Text.Trim();
        var type = feed.Catalog.FirstOrDefault(item => item.Key == source).Value;
        var draft = widget with
        {
            Field = source, Label = label.Text.Trim(), Header = headerText.Text.Trim(), StateRules = draftRules
        };
        var editor = new StateRuleEditor(draft, () =>
        {
            var value = demo ? Sample(source) : feed.Read(source);
            return (stateCatalog.Suggestions(source, type, value), value);
        }) { Owner = this };
        if (editor.ShowDialog() != true ||
            draftRules.Select(rule => rule.GetValues()).SequenceEqual(editor.Result.Select(rule => rule.GetValues()))) return;
        draftRules = editor.Result;
        UpdateDraft();
    }

    private bool TryReadDraft(Widget widget, out Widget draft, out string? error)
    {
        draft = widget;
        error = null;
        if (string.IsNullOrWhiteSpace(field.Text) || string.IsNullOrWhiteSpace(label.Text) ||
            display.SelectedItem is not Display style)
        {
            error = "Choose a telemetry field and display format, and enter a widget name.";
            return false;
        }

        if (!double.TryParse(min.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double lower) ||
            !double.TryParse(max.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double upper) ||
            !double.IsFinite(upper - lower) || lower >= upper)
        {
            error = "Under Range and values, enter a minimum below the maximum.";
            return false;
        }

        if (!int.TryParse(decimals.Text, out int precision) || precision is < 0 or > 4)
        {
            error = "Under Range and values, enter 0 to 4 decimal places.";
            return false;
        }

        if (!int.TryParse(opacity.Text, out int alpha) || alpha is < 10 or > 100 ||
            !int.TryParse(width.Text, out int w) || w is < 120 or > 800 ||
            !int.TryParse(height.Text, out int h) || h is < 64 or > 450)
        {
            error = "Check the opacity and dimensions under Appearance and size.";
            return false;
        }

        draft = widget with
        {
            Field = field.Text.Trim(),
            Label = label.Text.Trim(),
            Header = headerText.Text.Trim(),
            Group = layout.Widgets.FirstOrDefault(item => Layout.SameGroup(item.Group, group.Text))?.Group
                ?? group.Text.Trim(),
            Display = style,
            AutomaticRange = automatic.IsChecked == true,
            Min = lower,
            Max = upper,
            Unit = unit.Text.Trim(),
            Decimals = precision,
            Color = ColorPickerButton.IsHexColor(color.HexColor) ? color.HexColor : "#64E4DB",
            Opacity = alpha / 100d,
            Width = w,
            Height = h,
            ShowPercent = percent.IsChecked == true,
            StateRules = draftRules,
            Enabled = enabled.IsChecked == true
        };
        return true;
    }

    private void UpdateDraft()
    {
        if (loadingWidget) return;
        var widget = Selected;
        stateRulesButton.Content = draftRules.Length == 0 ? "Edit state rules" : $"Edit state rules ({draftRules.Length})";
        removeButton.IsEnabled = widget is not null;
        applyButton.IsEnabled = discardButton.IsEnabled = false;
        if (widget is null)
        {
            previewWidget = null;
            previewVisual = null;
            preview.Child = new TextBlock
            {
                Text = "Add a widget to see it here.",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            return;
        }

        bool valid = TryReadDraft(widget, out var draft, out var error);
        discardButton.IsEnabled = !valid || draft != widget;
        applyButton.IsEnabled = valid && draft != widget;
        if (notice.Text == validationNotice) notice.Text = "";
        validationNotice = error;
        if (error is not null) notice.Text = error;
        if (!valid || draft == previewWidget) return;

        previewWidget = draft;
        previewVisual = new HudVisual(draft) { Width = draft.Width, Height = draft.Height };
        preview.Child = new Viewbox
        {
            Child = previewVisual, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
            Margin = new Thickness(18)
        };
        RefreshPreview();
    }

    private void RefreshPreview()
    {
        if (previewWidget is not Widget widget || previewVisual is not HudVisual visual) return;
        var value = demo ? Sample(widget.Field) : feed.Read(widget.Field);
        if (Equals(visual.Value, value)) return;
        visual.Value = value;
        visual.InvalidateVisual();
    }

    private void Save()
    {
        try { layout.Save(demo); notice.Text = ""; }
        catch (Exception ex) { notice.Text = "Couldn’t save layout. " + ex.Message; }
    }

    private void RebuildHud()
    {
        foreach (var item in hud) item.Window.Close();
        hud.Clear();
        RestoreOffScreenGroups();
        foreach (var widget in layout.Widgets.Where(widget => widget.Enabled))
        {
            var window = new HudWindow(widget);
            window.SetEditing(editing);
            window.Moving += (dx, dy, moveGroup) => MoveHudWidgets(widget, dx, dy, moveGroup);
            window.DragFinished += Save;
            hud.Add((widget, window));
            if (!hidden) window.Show();
        }
        RefreshHud();
    }

    private void RestoreOffScreenGroups()
    {
        foreach (var widget in layout.Widgets.Where(widget => widget.Enabled))
        {
            var members = layout.DragMembers(widget, true).ToArray();
            if (members.Any(member => member.Enabled && HudWindow.IsOnScreen(member))) continue;
            double dx = SystemParameters.WorkArea.Left + 28 - widget.X;
            double dy = SystemParameters.WorkArea.Top + 28 - widget.Y;
            MoveHudWidgets(widget, dx, dy, true);
        }
    }

    private void MoveHudWidgets(Widget anchor, double dx, double dy, bool moveGroup)
    {
        foreach (var member in layout.DragMembers(anchor, moveGroup))
        {
            member.X += dx;
            member.Y += dy;
            if (!ReferenceEquals(member, anchor))
                hud.Find(item => ReferenceEquals(item.Widget, member)).Window?.SyncPosition();
        }
    }

    private void RefreshHud()
    {
        connection.Text = demo ? "Demo mode · sample telemetry" : feed.Status;
        FlushRememberedStates();
        if (!demo && gameHotkeys.Bindings.Count > 0) _ = gameHotkeys.DiscoverAsync();
        RefreshPreview();
        if (hidden) return;
        foreach (var (widget, window) in hud)
            window.Refresh(demo ? Sample(widget.Field) : feed.Read(widget.Field));
    }

    private static object? Sample(string path) => path switch
    {
        "Actor.Vitals.Health" => new Gauge(78, 100),
        "Actor.Vitals.Health.Current" => 78,
        "Actor.Vitals.Health.Max" => 100,
        "Actor.Vitals.Health.Fraction" => .78,
        "Actor.Vitals.Oxygen" => new Gauge(91, 100),
        "Actor.Vitals.HeartRate" => 72,
        Feed.Ammo => 14,
        Feed.Decoys => 10,
        Feed.Noise => 4,
        "Weapons.Countermeasures[0].Amount" => 10,
        "Weapons.Countermeasures[1].Amount" => 4,
        "Vehicle.Flight.Instruments.Altitude" => 1380,
        "Vehicle.Annunciators.Shields" => AnnunciatorState.Nominal,
        "Vehicle.LightsOn" => true,
        "Vehicle.Name" => "Demo ship",
        _ => null
    };

    private void ToggleEditing()
    {
        editing = !editing;
        if (editing && hidden) ToggleVisibility();
        foreach (var (_, window) in hud) window.SetEditing(editing);
        UpdateMode();
    }

    private void ToggleVisibility()
    {
        hidden = !hidden;
        foreach (var (_, window) in hud)
        {
            if (hidden) window.Hide();
            else window.Show();
        }
        UpdateMode();
    }

    private void UpdateMode()
    {
        editButton.Content = editing ? "Lock overlay" : "Unlock to drag";
        hideButton.Content = hidden ? "Show overlay" : "Hide overlay";
        mode.Text = (hidden ? "Overlay hidden" : editing ? "Drag mode" : "Click-through mode") +
            (hotkeysAvailable ? "\nCtrl+Alt+F9  Toggle drag     Ctrl+Alt+F10  Toggle visibility"
                : "\nOverlay controls are also in the system tray.");
        if (editing && !hidden) mode.Text += "\nDrag any widget to move its group. Shift+drag moves just that widget.";
    }

    private void RegisterHotkeys()
    {
        handle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(handle)?.AddHook(HandleMessage);
        const uint modifiers = 0x4003; // Control + Alt + no repeat
        bool edit = Native.RegisterHotKey(handle, 1, modifiers, 0x78);
        bool hide = Native.RegisterHotKey(handle, 2, modifiers, 0x79);
        gameHotkeys.Attach(handle);
        voice.Attach(handle);
        hotkeysAvailable = edit && hide;
        UpdateMode();
        if (!hotkeysAvailable)
            notice.Text = "One or more hotkeys are in use. Use the settings buttons or the tray menu.";
    }

    private IntPtr HandleMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == RawKeyboard.InputMessage)
        {
            voice.HandleKeyboardInput(lParam, voiceWindow?.IsChoosingHotkey == true);
            return IntPtr.Zero;
        }
        if (msg != 0x0312) return IntPtr.Zero;
        if (wParam.ToInt32() == 1) ToggleEditing();
        else if (wParam.ToInt32() == 2) ToggleVisibility();
        else
        {
            handled = gameHotkeys.Handle(wParam.ToInt32());
            return IntPtr.Zero;
        }
        handled = true;
        return IntPtr.Zero;
    }

    internal void OpenSettings()
    {
        RefreshFields();
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void OpenHotkeys()
    {
        if (hotkeyWindow is not null)
        {
            hotkeyWindow.Activate();
            return;
        }
        var menu = new HotkeyWindow(gameHotkeys, OpenVoice) { Owner = this };
        hotkeyWindow = menu;
        menu.Closed += (_, _) => hotkeyWindow = null;
        menu.Show();
    }

    private void OpenVoice()
    {
        if (voiceWindow is not null)
        {
            voiceWindow.Activate();
            return;
        }
        var menu = new VoiceWindow(voice) { Owner = this };
        voiceWindow = menu;
        menu.Closed += (_, _) => voiceWindow = null;
        menu.Show();
    }

    private async Task<string> RunVoiceActionAsync(string action)
    {
        switch (action)
        {
            case "hud:show":
                if (hidden) ToggleVisibility();
                return "Overlay shown.";
            case "hud:hide":
                if (!hidden) ToggleVisibility();
                return "Overlay hidden.";
            case "hud:toggle":
                ToggleVisibility();
                return hidden ? "Overlay hidden." : "Overlay shown.";
        }

        int separator = action.IndexOf(':');
        if (separator <= 0 || separator == action.Length - 1) return "Unknown voice action.";
        return action[..separator] switch
        {
            "toggle" => await gameHotkeys.RunVoiceActionAsync(GameKeyMode.Toggle, action[(separator + 1)..]),
            "trigger" => await gameHotkeys.RunVoiceActionAsync(GameKeyMode.Trigger, action[(separator + 1)..]),
            _ => "Unknown voice action."
        };
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (quitting) return;
        e.Cancel = true;
        Hide();
    }

    private void Exit()
    {
        quitting = true;
        stop.Cancel();
        FlushRememberedStates(true);
        timer.Stop();
        Native.UnregisterHotKey(handle, 1);
        Native.UnregisterHotKey(handle, 2);
        gameHotkeys.Detach();
        voice.Detach();
        tray.Visible = false;
        tray.ContextMenuStrip?.Dispose();
        tray.Dispose();
        trayIcon.Dispose();
        Application.Current.Shutdown();
    }

    private void FlushRememberedStates(bool force = false)
    {
        if (demo) return;
        try { stateCatalog.Flush(force); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            notice.Text = "Couldn’t save remembered telemetry states. " + ex.Message;
        }
    }
}
