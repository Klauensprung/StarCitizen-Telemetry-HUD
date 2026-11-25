using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using StarCitizen.LocalTelemetry;

namespace StarCitizenOverlay;

sealed class HotkeyWindow : Window
{
    private sealed record Choice(string Id, string Label);
    private sealed record Row(GameKey Binding, string Shortcut, string Mode, string Action, string Status);
    private readonly GameHotkeys hotkeys;
    private readonly ComboBox mode = new() { ItemsSource = Enum.GetValues<GameKeyMode>(), MinWidth = 95 };
    private readonly ComboBox action = new() { DisplayMemberPath = "Label", IsTextSearchEnabled = true, MinWidth = 250 };
    private readonly TextBox chord = new() { IsReadOnly = true, MinWidth = 150 };
    private readonly ListView bindings = new();
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock notice = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock result = new() { TextWrapping = TextWrapping.Wrap };
    private ModifierKeys modifiers;
    private Key key;

    public HotkeyWindow(GameHotkeys hotkeys, Action? openVoice = null)
    {
        this.hotkeys = hotkeys;
        Title = "Hotkeys";
        Width = 795;
        Height = 570;
        MinWidth = 590;
        MinHeight = 410;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
#pragma warning disable WPF0001
        ThemeMode = ThemeMode.Dark;
#pragma warning restore WPF0001
        Background = HudVisual.BrushFrom("#101824");
        Foreground = HudVisual.BrushFrom("#E2F1F6");
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        var root = new DockPanel { Background = Background, Margin = new Thickness(22, 16, 22, 14) };
        Content = root;
        var header = new StackPanel();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        header.Children.Add(Controller.Heading("Hotkeys", 24));
        if (openVoice is not null) Controller.AddButton(header, "Voice commands", openVoice);
        status.Foreground = HudVisual.BrushFrom("#83E4DA");
        status.Margin = new Thickness(0, 6, 0, 7);
        header.Children.Add(status);
        Controller.AddButton(header, "Refresh game actions", async () =>
        {
            await hotkeys.DiscoverAsync(true);
            LoadChoices();
            Refresh();
        });
        header.Children.Add(new TextBlock
        {
            Text = "Overlay controls   Ctrl+Alt+F9  Toggle drag  ·  Ctrl+Alt+F10  Toggle visibility",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 12)
        });
        header.Children.Add(Controller.Heading("Game actions", 17));
        var editor = new WrapPanel { Margin = new Thickness(0, 6, 0, 8) };
        header.Children.Add(editor);
        AddField(editor, "Mode", mode);
        AddField(editor, "Action", action);
        AddField(editor, "Press a key combination", chord);
        var add = Controller.AddButton(editor, "Add binding", Add);
        add.VerticalAlignment = VerticalAlignment.Bottom;
        chord.ToolTip = "Focus this field and press Ctrl or Alt with another key.";
        chord.PreviewKeyDown += Capture;
        mode.SelectionChanged += (_, _) => LoadChoices();
        mode.SelectedItem = GameKeyMode.Toggle;

        var footer = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        Controller.AddButton(footer, "Remove selected binding", () =>
        {
            if (bindings.SelectedItem is not Row row) return;
            notice.Text = hotkeys.Remove(row.Binding) ?? "";
            Refresh();
        });
        notice.Foreground = HudVisual.BrushFrom("#FFC18A");
        footer.Children.Add(notice);
        result.Foreground = HudVisual.BrushFrom("#B9D0DB");
        footer.Children.Add(result);
        footer.Children.Add(new TextBlock
        {
            Text = "Game actions run only while Star Citizen is the foreground window.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0)
        });

        var columns = new GridView();
        AddColumn(columns, "Shortcut", nameof(Row.Shortcut), 140);
        AddColumn(columns, "Mode", nameof(Row.Mode), 75);
        AddColumn(columns, "Action", nameof(Row.Action), 285);
        AddColumn(columns, "Status", nameof(Row.Status), 115);
        bindings.View = columns;
        root.Children.Add(bindings);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        timer.Tick += (_, _) => Refresh();
        Loaded += async (_, _) =>
        {
            await hotkeys.DiscoverAsync(true);
            LoadChoices();
            Refresh();
        };
        Closed += (_, _) => timer.Stop();
        Refresh();
        timer.Start();
    }

    private static void AddColumn(GridView grid, string title, string property, double width) =>
        grid.Columns.Add(new GridViewColumn { Header = title, Width = width, DisplayMemberBinding = new System.Windows.Data.Binding(property) });

    private static void AddField(Panel panel, string title, Control control)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 9, 5) };
        stack.Children.Add(new Label { Content = title, Target = control, Padding = new Thickness(0, 0, 0, 4) });
        stack.Children.Add(control);
        panel.Children.Add(stack);
    }

    private void LoadChoices()
    {
        string? previous = (action.SelectedItem as Choice)?.Id;
        Choice[] choices = (mode.SelectedItem is GameKeyMode.Toggle
            ? GameToggles.Known.Select(toggle => new Choice(toggle.Id, toggle.Name))
            : hotkeys.Choices.Select(item => new Choice(item.Name, item.Name))).OrderBy(item => item.Label).ToArray();
        action.ItemsSource = choices;
        action.SelectedItem = choices.FirstOrDefault(item => item.Id == previous) ?? choices.FirstOrDefault();
    }

    private void Capture(object sender, KeyEventArgs args)
    {
        Key pressed = args.Key == Key.System ? args.SystemKey : args.Key;
        args.Handled = true;
        if (pressed is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.None) return;
        if (pressed == Key.Escape)
        {
            chord.Clear();
            key = Key.None;
            return;
        }
        var held = Keyboard.Modifiers;
        if ((held & (ModifierKeys.Control | ModifierKeys.Alt)) == 0 || held.HasFlag(ModifierKeys.Windows))
        {
            notice.Text = "Use Ctrl or Alt with another key. Shift is optional.";
            return;
        }
        key = pressed;
        modifiers = held;
        chord.Text = new GameKey(GameKeyMode.Toggle, "", modifiers, key).Shortcut;
        notice.Text = "";
    }

    private void Add()
    {
        if (action.SelectedItem is not Choice choice || mode.SelectedItem is not GameKeyMode selected || key == Key.None)
        {
            notice.Text = "Choose an action and enter a key combination.";
            return;
        }
        string? error = hotkeys.Add(new GameKey(selected, choice.Id, modifiers, key));
        notice.Text = error ?? "";
        if (error is null)
        {
            chord.Clear();
            key = Key.None;
        }
        Refresh();
    }

    private void Refresh()
    {
        status.Text = hotkeys.Status;
        result.Text = hotkeys.LastResult;
        var selection = (bindings.SelectedItem as Row)?.Binding;
        var rows = hotkeys.Bindings.Select(binding => new Row(binding, binding.Shortcut, binding.Mode.ToString(),
            binding.Description, hotkeys.IsRegistered(binding) ? "Registered" :
            hotkeys.IsAdvertised(binding) ? "Key unavailable" :
            hotkeys.ActionsAvailable ? "Not advertised" : "Inactive")).ToArray();
        if (rows.SequenceEqual(bindings.Items.OfType<Row>())) return;
        bindings.ItemsSource = rows;
        bindings.SelectedItem = rows.FirstOrDefault(row => row.Binding == selection);
    }
}
