using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace StarCitizenOverlay;

sealed class StateRuleEditor : Window
{
    private readonly ObservableCollection<StateRule> rules;
    private readonly DataGrid table = new();
    private readonly ComboBox state = new() { IsEditable = true, IsTextSearchEnabled = true, MinWidth = 175 };
    private readonly TextBlock notice = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Viewbox preview = new() { StretchDirection = StretchDirection.DownOnly, Height = 76 };
    private readonly Widget widget;
    private readonly Func<(string[] Suggestions, object? Current)> readStates;
    private readonly ObservableCollection<string> stateOptions = [];
    private readonly HashSet<string> knownStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly TextBlock currentState = new() { Foreground = HudVisual.BrushFrom("#83E4DA") };
    private object? live;
    public StateRule[] Result { get; private set; } = [];

    public StateRuleEditor(Widget widget, Func<(string[] Suggestions, object? Current)> readStates)
    {
        this.widget = widget;
        this.readStates = readStates;
        Title = "State rules";
        Width = 760;
        Height = 550;
        MinWidth = 640;
        MinHeight = 430;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
#pragma warning disable WPF0001
        ThemeMode = ThemeMode.Dark;
#pragma warning restore WPF0001
        Background = HudVisual.BrushFrom("#101824");
        Foreground = HudVisual.BrushFrom("#E2F1F6");
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        rules = new ObservableCollection<StateRule>((widget.StateRules ?? []).Select(rule => rule.Copy()));
        var root = new DockPanel { Background = Background, LastChildFill = true, Margin = new Thickness(20, 15, 20, 14) };
        Content = root;
        var header = new StackPanel();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        header.Children.Add(Controller.Heading("State rules", 23));
        header.Children.Add(new TextBlock
        {
            Text = widget.Field,
            Foreground = HudVisual.BrushFrom("#B9D0DB"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 9)
        });
        header.Children.Add(currentState);
        var addRow = new WrapPanel { Margin = new Thickness(0, 8, 0, 7) };
        header.Children.Add(addRow);
        state.ItemsSource = stateOptions;
        state.Margin = new Thickness(0, 0, 8, 5);
        System.Windows.Automation.AutomationProperties.SetName(state, "Known or custom state");
        addRow.Children.Add(state);
        Controller.AddButton(addRow, "Add state", () => AddState(state.Text));
        Controller.AddButton(addRow, "Add green/red presets", AddPresets);
        Controller.AddButton(addRow, "Remove selected", () =>
        {
            if (table.SelectedItem is StateRule rule) rules.Remove(rule);
            RefreshPreview();
        });

        var footer = new StackPanel { Margin = new Thickness(0, 9, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        footer.Children.Add(Controller.Heading("Rule preview", 14));
        footer.Children.Add(Controller.PanelFrame(preview, "#0B131E", new Thickness(1)));
        notice.Foreground = HudVisual.BrushFrom("#FFC18A");
        notice.Margin = new Thickness(0, 5, 0, 4);
        footer.Children.Add(notice);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        footer.Children.Add(buttons);
        var save = Controller.AddButton(buttons, "Use rules", Commit);
        save.Background = HudVisual.BrushFrom("#83E4DA");
        save.Foreground = Background;
        Controller.AddButton(buttons, "Cancel", () => DialogResult = false);

        table.ItemsSource = rules;
        table.AutoGenerateColumns = false;
        table.CanUserAddRows = false;
        table.CanUserDeleteRows = false;
        table.CanUserReorderColumns = false;
        table.SelectionMode = DataGridSelectionMode.Single;
        table.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal;
        table.HeadersVisibility = DataGridHeadersVisibility.Column;
        table.Columns.Add(Column("When state is", nameof(StateRule.State), 1.15));
        table.Columns.Add(Column("Display text", nameof(StateRule.Text), 1.35));
        var colorPicker = new FrameworkElementFactory(typeof(ColorPickerButton));
        colorPicker.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 2, 4, 2));
        colorPicker.SetBinding(ColorPickerButton.HexColorProperty, new Binding(nameof(StateRule.Color)) { Mode = BindingMode.TwoWay });
        table.Columns.Add(new DataGridTemplateColumn
        {
            Header = "Color", Width = new DataGridLength(126), IsReadOnly = true,
            CellTemplate = new DataTemplate { VisualTree = colorPicker }
        });
        table.Columns.Add(new DataGridComboBoxColumn
        {
            Header = "Display",
            Width = new DataGridLength(.9, DataGridLengthUnitType.Star),
            ItemsSource = Enum.GetValues<StatePresentation>(),
            SelectedItemBinding = new Binding(nameof(StateRule.Presentation)) { Mode = BindingMode.TwoWay }
        });
        var showText = new FrameworkElementFactory(typeof(CheckBox));
        showText.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        showText.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        showText.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, new Binding(nameof(StateRule.ShowText))
        {
            Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
        });
        table.Columns.Add(new DataGridTemplateColumn
        {
            Header = "Show text", Width = new DataGridLength(82), IsReadOnly = true,
            CellTemplate = new DataTemplate { VisualTree = showText }
        });
        table.AddHandler(ColorPickerButton.ColorPickedEvent, new RoutedEventHandler(OnRuleControlChanged));
        table.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler((sender, args) =>
        {
            if (args.OriginalSource is CheckBox) OnRuleControlChanged(sender, args);
        }));
        table.SelectionChanged += (_, _) => RefreshPreview();
        table.CellEditEnding += (_, _) => Dispatcher.BeginInvoke(RefreshPreview, DispatcherPriority.Background);
        root.Children.Add(table);
        RefreshStates();
        state.Text = stateOptions.FirstOrDefault() ?? "";
        if (rules.Count > 0) table.SelectedIndex = 0;
        RefreshPreview();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        timer.Tick += (_, _) => RefreshStates();
        Closed += (_, _) => timer.Stop();
        timer.Start();
    }

    private void RefreshStates()
    {
        var (suggestions, current) = readStates();
        foreach (string suggestion in suggestions)
            if (knownStates.Add(suggestion)) stateOptions.Add(suggestion);

        currentState.Text = "Current state · " + (StateRule.Key(current) ?? "No discrete value");
        if (Equals(live, current)) return;
        live = current;
        if (table.SelectedItem is null) RefreshPreview();
    }

    private static DataGridTextColumn Column(string title, string property, double width) => new()
    {
        Header = title, Width = new DataGridLength(width, DataGridLengthUnitType.Star),
        Binding = new Binding(property) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }
    };

    private void OnRuleControlChanged(object sender, RoutedEventArgs args)
    {
        if (args.OriginalSource is FrameworkElement { DataContext: StateRule rule })
            table.SelectedItem = rule;
        RefreshPreview();
    }

    private void AddState(string expression)
    {
        string key = expression.Trim();
        if (key.Length == 0)
        {
            notice.Text = "Choose a known state or type a state name.";
            state.Focus();
            return;
        }

        var rule = rules.FirstOrDefault(candidate => key.Equals(candidate.State, StringComparison.OrdinalIgnoreCase));
        if (rule is null)
        {
            rule = new StateRule { State = key, Color = widget.Color, Presentation = StatePresentation.Circle };
            rules.Add(rule);
        }
        table.SelectedItem = rule;
        table.ScrollIntoView(rule);
        notice.Text = "";
    }

    private void AddPresets()
    {
        foreach (string key in new[] { "Nominal", "On", "Off" })
        {
            if (rules.Any(rule => key.Equals(rule.State, StringComparison.OrdinalIgnoreCase))) continue;
            rules.Add(new StateRule
            {
                State = key, Presentation = StatePresentation.Circle, ShowText = false,
                Color = key == "Off" ? "#FF676D" : "#5EE4A2"
            });
        }
        table.SelectedIndex = 0;
        RefreshPreview();
    }

    private void RefreshPreview()
    {
        var rule = table.SelectedItem as StateRule;
        var staged = widget with
        {
            StateRules = rule is not null && ColorPickerButton.IsHexColor(rule.Color) ? [rule.Copy()] : []
        };
        preview.Child = new HudVisual(staged) { Width = staged.Width, Height = staged.Height, Value = rule?.State.Trim() ?? live };
    }

    private void Commit()
    {
        if (!table.CommitEdit(DataGridEditingUnit.Cell, true) || !table.CommitEdit(DataGridEditingUnit.Row, true)) return;
        if (rules.Any(rule => string.IsNullOrWhiteSpace(rule.State) || !ColorPickerButton.IsHexColor(rule.Color)))
        {
            notice.Text = "Each rule needs a state and a selected color.";
            return;
        }
        if (rules.Select(rule => rule.State.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != rules.Count)
        {
            notice.Text = "Use one rule per state.";
            return;
        }
        Result = rules.Select(rule => rule.Copy()).ToArray();
        DialogResult = true;
    }
}
