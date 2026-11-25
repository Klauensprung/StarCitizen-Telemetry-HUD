using System;
using System.Linq;
using System.Speech.Recognition;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using StarCitizen.LocalTelemetry;

namespace StarCitizenOverlay;

sealed class VoiceWindow : Window
{
    private sealed record Choice(string Key, string Title);
    private sealed record Row(VoiceBinding Binding, string Phrase, string Action);
    private readonly VoiceCommands voice;
    private readonly TextBlock instructions = new() { Margin = new Thickness(0, 4, 0, 5) };
    private readonly TextBox hotkey = new() { IsReadOnly = true, MinWidth = 85 };
    private Key desiredKey;
    private ModifierKeys desiredModifiers;
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock notice = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar level = new() { Minimum = 0, Maximum = 100, Height = 8, Width = 130 };
    private readonly Button testInput;
    private readonly ComboBox backend = new() { ItemsSource = Enum.GetValues<VoiceBackend>(), MinWidth = 130 };
    private readonly ComboBox language = new() { MinWidth = 120, IsEditable = true };
    private readonly ComboBox microphone = new() { MinWidth = 250, DisplayMemberPath = nameof(MicrophoneChoice.Name) };
    private readonly CheckBox enabled = new() { Content = "Enable voice commands" };
    private readonly TextBox phrase = new() { MinWidth = 215 };
    private readonly ComboBox action = new() { DisplayMemberPath = nameof(Choice.Title), IsTextSearchEnabled = true, MinWidth = 250 };
    private readonly ListView bindings = new();
    private readonly Choice[] choices;

    public VoiceWindow(VoiceCommands voice)
    {
        this.voice = voice;
        choices = new[]
        {
            new Choice("hud:show", "Show overlay"), new("hud:hide", "Hide overlay"), new("hud:toggle", "Toggle overlay")
        }.Concat(GameToggles.Known.Select(toggle => new Choice("toggle:" + toggle.Id, "Toggle · " + toggle.Name)))
            .Concat(GameActions.Known.Select(trigger => new Choice("trigger:" + trigger.Name, "Trigger · " + trigger.Name))).ToArray();

        Title = "Voice commands";
        Width = 780;
        Height = 610;
        MinWidth = 620;
        MinHeight = 525;
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
        header.Children.Add(Controller.Heading("Voice commands", 24));
        header.Children.Add(instructions);
        status.Foreground = HudVisual.BrushFrom("#83E4DA");
        status.Margin = new Thickness(0, 0, 0, 12);
        header.Children.Add(status);

        var options = new WrapPanel();
        header.Children.Add(options);
        AddField(options, "Speech recognizer", backend);
        AddField(options, "Language code", language);
        AddField(options, "Push-to-talk key", hotkey);
        desiredKey = voice.Settings.PushToTalk;
        desiredModifiers = voice.Settings.PushToTalkModifiers;
        hotkey.Text = VoiceCommands.ShortcutLabel(desiredKey, desiredModifiers);
        hotkey.ToolTip = "Click here, then press a key by itself or with Shift or Alt.";
        hotkey.PreviewKeyDown += CaptureHotkey;
        enabled.Margin = new Thickness(6, 15, 10, 5);
        options.Children.Add(enabled);
        var save = Controller.AddButton(options, "Apply settings", SaveSettings);
        save.VerticalAlignment = VerticalAlignment.Bottom;
        backend.SelectedItem = voice.Settings.Backend;
        try
        {
            language.ItemsSource = new[] { "en", "de" }.Concat(SpeechRecognitionEngine.InstalledRecognizers()
                .Select(item => item.Culture.Name)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (Exception) { language.ItemsSource = new[] { "en", "de" }; }
        language.Text = voice.Settings.Language;
        enabled.IsChecked = voice.Settings.Enabled;

        var audio = new WrapPanel { Margin = new Thickness(0, 0, 0, 9) };
        header.Children.Add(audio);
        AddField(audio, "Microphone", microphone);
        var refreshInputs = Controller.AddButton(audio, "Refresh microphones", () => RefreshMicrophones(
            (microphone.SelectedItem as MicrophoneChoice)?.Id));
        refreshInputs.VerticalAlignment = VerticalAlignment.Bottom;
        testInput = Controller.AddButton(audio, "Test microphone", () => voice.ToggleMicrophoneTest(
            (microphone.SelectedItem as MicrophoneChoice)?.Id,
            backend.SelectedItem is VoiceBackend current ? current : voice.Settings.Backend, language.Text.Trim()));
        testInput.VerticalAlignment = VerticalAlignment.Bottom;
        var meter = new StackPanel { Margin = new Thickness(3, 7, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        meter.Children.Add(new TextBlock { Text = "Input level", Margin = new Thickness(0, 0, 0, 4) });
        meter.Children.Add(level);
        audio.Children.Add(meter);
        RefreshMicrophones(voice.Settings.Microphone);

        header.Children.Add(Controller.Heading("Phrase bindings", 17));
        var editor = new WrapPanel { Margin = new Thickness(0, 6, 0, 8) };
        header.Children.Add(editor);
        AddField(editor, "Spoken phrase", phrase);
        AddField(editor, "Action", action);
        var add = Controller.AddButton(editor, "Bind phrase", AddBinding);
        add.VerticalAlignment = VerticalAlignment.Bottom;
        action.ItemsSource = choices;
        action.SelectedIndex = 0;

        var footer = new StackPanel { Margin = new Thickness(0, 9, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        Controller.AddButton(footer, "Remove selected binding", RemoveBinding);
        notice.Foreground = HudVisual.BrushFrom("#FFC18A");
        footer.Children.Add(notice);
        footer.Children.Add(new TextBlock
        {
            Text = "HUD commands work independently of game action support. Game actions require Star Citizen to be in focus.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0)
        });

        var columns = new GridView();
        columns.Columns.Add(new GridViewColumn { Header = "Phrase", Width = 230, DisplayMemberBinding = new System.Windows.Data.Binding(nameof(Row.Phrase)) });
        columns.Columns.Add(new GridViewColumn { Header = "Action", Width = 350, DisplayMemberBinding = new System.Windows.Data.Binding(nameof(Row.Action)) });
        bindings.View = columns;
        root.Children.Add(bindings);
        voice.Changed += RefreshStatus;
        Closed += (_, _) =>
        {
            voice.Changed -= RefreshStatus;
            voice.CancelMicrophoneTest();
        };
        RefreshBindings();
        RefreshStatus();
    }

    private static void AddField(Panel panel, string label, Control control)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 9, 5) };
        stack.Children.Add(new Label { Content = label, Target = control, Padding = new Thickness(0, 0, 0, 3) });
        stack.Children.Add(control);
        panel.Children.Add(stack);
    }

    public bool IsChoosingHotkey => hotkey.IsKeyboardFocusWithin;

    private void RefreshStatus()
    {
        status.Text = voice.Status;
        instructions.Text = $"Hold {VoiceCommands.ShortcutLabel(voice.Settings.PushToTalk, voice.Settings.PushToTalkModifiers)}, say a configured phrase, then release the key.";
        testInput.Content = voice.IsTesting ? "Finish test" : "Test microphone";
        double peak = voice.InputLevel;
        level.Value = peak <= .001 ? 0 : Math.Clamp((20 * Math.Log10(peak) + 60) / 60 * 100, 0, 100);
    }

    private void RefreshBindings() => bindings.ItemsSource = voice.Settings.Bindings.Select(binding =>
        new Row(binding, binding.Phrase, choices.FirstOrDefault(choice => choice.Key == binding.Action)?.Title ?? binding.Action)).ToArray();

    private void RefreshMicrophones(string? selection)
    {
        MicrophoneChoice[] available;
        try { available = VoiceCommands.Microphones().ToArray(); }
        catch (Exception)
        {
            available = [new MicrophoneChoice(null, "Windows default microphone", -1)];
            notice.Text = "Couldn’t list microphones. Refresh to try again.";
        }
        var selected = available.FirstOrDefault(item => string.Equals(item.Id, selection, StringComparison.OrdinalIgnoreCase));
        if (selected is null)
        {
            selected = new MicrophoneChoice(selection, "Unavailable · " + selection?.Split('|')[0], -2);
            available = [.. available, selected];
        }
        microphone.ItemsSource = available;
        microphone.SelectedItem = selected;
    }

    private void CaptureHotkey(object sender, KeyEventArgs args)
    {
        if (args.Key == Key.Tab) return;
        args.Handled = true;
        Key key = args.Key == Key.System ? args.SystemKey : args.Key;
        var modifiers = Keyboard.Modifiers;
        if (!VoiceCommands.IsPushToTalkKey(key, modifiers))
        {
            notice.Text = "Press a key by itself or with Shift or Alt.";
            return;
        }
        desiredKey = key;
        desiredModifiers = modifiers;
        hotkey.Text = VoiceCommands.ShortcutLabel(key, modifiers);
        notice.Text = "";
    }

    private void SaveSettings()
    {
        if (backend.SelectedItem is not VoiceBackend recognizer || string.IsNullOrWhiteSpace(language.Text))
        {
            notice.Text = "Choose a recognizer and enter a language code.";
            return;
        }
        var settings = voice.Settings;
        var previous = (settings.Enabled, settings.Backend, settings.Language, settings.Microphone, settings.PushToTalk, settings.PushToTalkModifiers);
        settings.Enabled = enabled.IsChecked == true;
        settings.Backend = recognizer;
        settings.Language = language.Text.Trim();
        settings.Microphone = (microphone.SelectedItem as MicrophoneChoice)?.Id;
        settings.PushToTalk = desiredKey;
        settings.PushToTalkModifiers = desiredModifiers;
        string? error = voice.Save();
        if (error is not null) (settings.Enabled, settings.Backend, settings.Language, settings.Microphone, settings.PushToTalk, settings.PushToTalkModifiers) = previous;
        notice.Text = error ?? "";
    }

    private void AddBinding()
    {
        string text = phrase.Text.Trim();
        if (VoiceCommands.Normalize(text).Length == 0 || action.SelectedItem is not Choice choice)
        {
            notice.Text = "Enter a phrase and choose an action.";
            return;
        }
        if (VoiceCommands.Match(text, voice.Settings.Bindings) is not null)
        {
            notice.Text = "That phrase already has a binding.";
            return;
        }
        var binding = new VoiceBinding(text, choice.Key);
        voice.Settings.Bindings.Add(binding);
        string? error = voice.Save();
        if (error is null) phrase.Clear();
        else voice.Settings.Bindings.Remove(binding);
        notice.Text = error ?? "";
        RefreshBindings();
    }

    private void RemoveBinding()
    {
        if (bindings.SelectedItem is not Row row) return;
        int index = voice.Settings.Bindings.IndexOf(row.Binding);
        voice.Settings.Bindings.RemoveAt(index);
        string? error = voice.Save();
        if (error is not null) voice.Settings.Bindings.Insert(index, row.Binding);
        notice.Text = error ?? "";
        RefreshBindings();
    }
}
