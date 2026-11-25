using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace StarCitizenOverlay;

sealed class ColorPickerButton : Button
{
    public static readonly DependencyProperty HexColorProperty = DependencyProperty.Register(
        nameof(HexColor), typeof(string), typeof(ColorPickerButton),
        new FrameworkPropertyMetadata("#64E4DB", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (element, _) => ((ColorPickerButton)element).RefreshSwatch()));

    public static readonly RoutedEvent ColorPickedEvent = EventManager.RegisterRoutedEvent(
        nameof(ColorPicked), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(ColorPickerButton));

    private readonly Border swatch = new()
    {
        Width = 16, Height = 16, CornerRadius = new CornerRadius(3),
        BorderThickness = new Thickness(1), BorderBrush = HudVisual.BrushFrom("#8496A6"),
        Margin = new Thickness(0, 0, 6, 0)
    };

    public ColorPickerButton()
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(swatch);
        content.Children.Add(new TextBlock { Text = "Choose color", VerticalAlignment = VerticalAlignment.Center });
        Content = content;
        System.Windows.Automation.AutomationProperties.SetName(this, "Choose color");
        RefreshSwatch();
    }

    public string HexColor
    {
        get => (string)GetValue(HexColorProperty);
        set => SetValue(HexColorProperty, value);
    }

    public event RoutedEventHandler ColorPicked
    {
        add => AddHandler(ColorPickedEvent, value);
        remove => RemoveHandler(ColorPickedEvent, value);
    }

    internal static bool IsHexColor(string? value) => value is { Length: 7 } && value[0] == '#' &&
        uint.TryParse(value.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out _);

    private void RefreshSwatch()
    {
        string color = IsHexColor(HexColor) ? HexColor : "#64E4DB";
        swatch.Background = HudVisual.BrushFrom(color);
        ToolTip = color;
    }

    protected override void OnClick()
    {
        base.OnClick();
        string previous = IsHexColor(HexColor) ? HexColor : "#64E4DB";
        using var dialog = new Forms.ColorDialog
        {
            Color = System.Drawing.ColorTranslator.FromHtml(previous),
            FullOpen = true,
            AnyColor = true
        };

        var window = Window.GetWindow(this);
        var result = window is null ? dialog.ShowDialog()
            : dialog.ShowDialog(new DialogOwner(new WindowInteropHelper(window).Handle));
        if (result != Forms.DialogResult.OK) return;

        ApplyColor(dialog.Color);
    }

    internal void ApplyColor(System.Drawing.Color color)
    {
        string chosen = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        SetCurrentValue(HexColorProperty, chosen);
        GetBindingExpression(HexColorProperty)?.UpdateSource();
        RaiseEvent(new RoutedEventArgs(ColorPickedEvent, this));
    }

    private sealed class DialogOwner(IntPtr handle) : Forms.IWin32Window
    {
        public IntPtr Handle { get; } = handle;
    }
}
