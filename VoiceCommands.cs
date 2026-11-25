using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Media;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Speech.Recognition;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using System.Windows.Input;
using NAudio.Wave;
using StarCitizen.LocalTelemetry;

namespace StarCitizenOverlay;

enum VoiceBackend { Windows, Voicebox }

sealed record VoiceBinding(string Phrase, string Action);
sealed record MicrophoneChoice(string? Id, string Name, int DeviceNumber);

sealed class VoiceSettings
{
    public bool Enabled { get; set; } = true;
    public VoiceBackend Backend { get; set; }
    public string Language { get; set; } = "en";
    public string? Microphone { get; set; }
    public Key PushToTalk { get; set; } = Key.F8;
    public ModifierKeys PushToTalkModifiers { get; set; }
    public List<VoiceBinding> Bindings { get; set; } =
    [
        new("show overlay", "hud:show"), new("hide overlay", "hud:hide"), new("toggle overlay", "hud:toggle"),
        new("toggle lights", "toggle:lights"), new("toggle shields", "toggle:shields"),
        new("shields on", "trigger:" + GameActions.SpaceshipPower.PowerSetShieldsOn),
        new("shields off", "trigger:" + GameActions.SpaceshipPower.PowerSetShieldsOff),
        new("lights on", "trigger:" + GameActions.LightsController.LightsOn),
        new("lights off", "trigger:" + GameActions.LightsController.LightsOff)
    ];
}

sealed class VoiceCommands
{

    private static readonly HttpClient Http = new() { BaseAddress = new Uri("http://127.0.0.1:17493/"), Timeout = TimeSpan.FromSeconds(15) };
    private static readonly WaveFormat AudioFormat = new(16000, 16, 1);
    private static readonly Lazy<SoundPlayer> MatchedTone = new(() => CreateCue(620, 880));
    private static readonly Lazy<SoundPlayer> MissedTone = new(() => CreateCue(450, 280));
    private readonly CancellationToken shutdown;
    private readonly Func<string, Task<string>> dispatch;
    private readonly string path;
    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(35) };
    private IntPtr window;
    private bool listeningForKeys;
    private readonly PushToTalkInput input = new();
    private bool busy;
    private long started;
    private WaveIn? capture;
    private MemoryStream? audio;
    public VoiceSettings Settings { get; }
    public string Status { get; private set; } = "Hold F8 to talk.";
    public event Action? Changed;

    public VoiceCommands(bool demo, CancellationToken shutdown, Func<string, Task<string>> dispatch, string? file = null)
    {
        this.shutdown = shutdown;
        this.dispatch = dispatch;
        path = file ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "StarCitizenOverlay", demo ? "demo-voice.json" : "voice.json");
        try { Settings = File.Exists(path) ? JsonSerializer.Deserialize<VoiceSettings>(File.ReadAllText(path)) ?? new() : new(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            Settings = new();
            Status = "Couldn’t load voice settings. " + error.Message;
        }
        timer.Tick += (_, _) =>
        {
            Changed?.Invoke();
            if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(8))
                StopRecording();
        };
    }

    public void Attach(IntPtr handle)
    {
        window = handle;
        string? error = ApplyRegistration(Settings.Enabled);
        SetStatus(error ?? IdleStatus());
    }

    private string IdleStatus() => !Settings.Enabled ? "Voice commands are off."
        : $"Hold {ShortcutLabel(Settings.PushToTalk, Settings.PushToTalkModifiers)} to talk.";

    private string? ApplyRegistration(bool enabled)
    {
        if (window == IntPtr.Zero || enabled == listeningForKeys) return null;
        if (!RawKeyboard.SetListener(window, enabled))
            return enabled ? "Windows could not attach background keyboard input." : "Windows could not detach keyboard input.";
        listeningForKeys = enabled;
        input.Reset();
        return null;
    }

    public string? Save()
    {
        if (!IsPushToTalkKey(Settings.PushToTalk, Settings.PushToTalkModifiers)) return "Choose a key, optionally with Shift or Alt.";
        bool previous = listeningForKeys;
        string? registrationError = ApplyRegistration(Settings.Enabled);
        if (registrationError is not null) return registrationError;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, true);
            if (!Settings.Enabled && !testing) StopRecording();
            SetStatus(IdleStatus());
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ApplyRegistration(previous);
            return "Couldn’t save voice settings. " + error.Message;
        }
    }

    internal static bool IsPushToTalkKey(Key key, ModifierKeys modifiers = ModifierKeys.None) =>
        (modifiers & ~(ModifierKeys.Shift | ModifierKeys.Alt)) == 0 && Enum.IsDefined(key) && KeyInterop.VirtualKeyFromKey(key) != 0 &&
        key is not (Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or
            Key.RightShift or Key.LWin or Key.RWin or Key.System or Key.ImeProcessed or Key.DeadCharProcessed);

    internal static string ShortcutLabel(Key key, ModifierKeys modifiers) =>
        (modifiers.HasFlag(ModifierKeys.Shift) ? "Shift+" : "") +
        (modifiers.HasFlag(ModifierKeys.Alt) ? "Alt+" : "") + KeyLabel(key);

    private static string KeyLabel(Key key) => key switch
    {
        Key.Pause => "Pause",
        Key.Next => "Page Down",
        Key.Prior => "Page Up",
        Key.OemPlus => "+",
        Key.OemMinus => "-",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        _ => new KeyConverter().ConvertToInvariantString(key) ?? key.ToString()
    };


    public double InputLevel => Volatile.Read(ref latestPeak);
    public bool IsTesting => capture is not null && testing;

    private double latestPeak, maximumPeak;
    private bool testing, discardTest;
    private VoiceBackend recordingBackend;
    private string recordingLanguage = "en";

    public void HandleKeyboardInput(IntPtr handle, bool editingBinding)
    {
        if (!listeningForKeys || !RawKeyboard.TryRead(handle, out var key)) return;
        var edge = input.Read(key, KeyInterop.VirtualKeyFromKey(Settings.PushToTalk), Settings.PushToTalkModifiers,
            !editingBinding && !busy && Settings.Enabled);
        if (edge == PushToTalkEdge.Pressed)
            BeginCapture(false, Settings.Microphone, Settings.Backend, Settings.Language);
        else if (edge == PushToTalkEdge.Released && capture is not null && !testing)
            StopRecording();
    }

    public void ToggleMicrophoneTest(string? microphone, VoiceBackend backend, string language)
    {
        if (IsTesting) StopRecording();
        else BeginCapture(true, microphone, backend, language);
    }

    public void CancelMicrophoneTest()
    {
        if (!IsTesting) return;
        discardTest = true;
        StopRecording();
    }

    private void BeginCapture(bool test, string? microphone, VoiceBackend backend, string language)
    {
        if (busy) return;
        if (Settings.Bindings.Count == 0) { SetStatus("Add a phrase binding before recording."); return; }
        busy = true;
        testing = test;
        discardTest = false;
        recordingBackend = backend;
        recordingLanguage = language;
        Volatile.Write(ref latestPeak, 0);
        maximumPeak = 0;
        try
        {
            int? selected = ResolveMicrophone(microphone, Microphones());
            if (selected is null)
            {
                busy = false;
                SetStatus("The selected microphone is unavailable. Choose another input.");
                return;
            }
            audio = new MemoryStream();
            capture = new WaveIn { DeviceNumber = selected.Value, WaveFormat = AudioFormat, BufferMilliseconds = 50 };
            var buffer = audio;
            capture.DataAvailable += (_, args) =>
            {
                lock (buffer)
                {
                    buffer.Write(args.Buffer, 0, args.BytesRecorded);
                    double peak = MeasurePeak(args.Buffer.AsSpan(0, args.BytesRecorded));
                    maximumPeak = Math.Max(maximumPeak, peak);
                    Volatile.Write(ref latestPeak, peak);
                }
            };
            capture.RecordingStopped += RecordingStopped;
            capture.StartRecording();
            started = Stopwatch.GetTimestamp();
            timer.Start();
            SetStatus(test ? "Testing microphone · say a configured phrase, then click Finish test."
                : "Listening · release the push-to-talk key to run the command.");
        }
        catch (Exception error)
        {
            CleanupRecording();
            SetStatus("Couldn’t start the microphone. " + error.Message);
        }
    }

    private void StopRecording()
    {
        timer.Stop();
        try { capture?.StopRecording(); }
        catch (Exception error)
        {
            CleanupRecording();
            SetStatus("Couldn’t stop the microphone. " + error.Message);
        }
    }

    private async void RecordingStopped(object? sender, StoppedEventArgs args)
    {
        if (!dispatcher.CheckAccess())
        {
            if (!dispatcher.HasShutdownStarted) _ = dispatcher.BeginInvoke(() => RecordingStopped(sender, args));
            return;
        }
        if (!ReferenceEquals(sender, capture) || audio is not { } buffer) return;

        byte[] samples;
        double peak;
        bool test = testing, canceled = discardTest;
        lock (buffer)
        {
            samples = buffer.ToArray();
            peak = maximumPeak;
        }
        CleanupRecording();
        if (shutdown.IsCancellationRequested || canceled || (!test && !Settings.Enabled)) return;
        if (args.Exception is not null) { ReportResult(false, "Microphone error. " + args.Exception.Message); return; }
        if (samples.Length < AudioFormat.AverageBytesPerSecond / 5)
        {
            ReportResult(false, test ? "The microphone returned too little audio. Try again and speak before finishing." : IdleStatus());
            return;
        }
        if (peak < .002)
        {
            ReportResult(false, "No usable microphone signal. Check the selected input, mute switch, and Windows input volume.");
            return;
        }

        busy = true;
        SetStatus("Audio received · recognizing…");
        try
        {
            using var wav = new MemoryStream();
            using (var writer = new WaveFileWriter(wav, AudioFormat))
                writer.Write(samples, 0, samples.Length);
            byte[] clip = wav.ToArray();
            var backend = recordingBackend;
            var language = recordingLanguage;
            var phrases = Settings.Bindings.Select(binding => binding.Phrase).ToArray();
            string? text = backend == VoiceBackend.Voicebox
                ? await TranscribeVoiceboxAsync(clip, language, shutdown)
                : await Task.Run(() => RecognizeWindows(clip, language, phrases, test ? 0f : .70f), shutdown);
            if (shutdown.IsCancellationRequested || (!test && !Settings.Enabled)) return;
            var matched = Match(text, Settings.Bindings);
            if (matched is null)
            {
                ReportResult(false, string.IsNullOrWhiteSpace(text) ? "Audio received, but no configured phrase was recognized."
                    : $"Heard “{text.Trim()}” · no matching phrase.");
                return;
            }
            ReportResult(true, test ? $"Recognized “{matched.Phrase}” · test only." : await dispatch(matched.Action));
        }
        catch (HttpRequestException error)
        {
            ReportResult(false, "Voicebox unavailable on 127.0.0.1:17493. " + error.Message);
        }
        catch (OperationCanceledException)
        {
            if (!shutdown.IsCancellationRequested) ReportResult(false, "Voice recognition timed out.");
        }
        catch (Exception error) { ReportResult(false, "Voice recognition failed. " + error.Message); }
        finally { busy = false; }
    }

    internal static double MeasurePeak(ReadOnlySpan<byte> pcm)
    {
        int peak = 0;
        for (int index = 0; index + 1 < pcm.Length; index += 2)
        {
            short value = (short)(pcm[index] | pcm[index + 1] << 8);
            peak = Math.Max(peak, Math.Abs((int)value));
        }
        return peak / 32768d;
    }


    private void CleanupRecording()
    {
        timer.Stop();
        if (capture is not null) capture.RecordingStopped -= RecordingStopped;
        capture?.Dispose();
        capture = null;
        audio?.Dispose();
        audio = null;
        testing = false;
        Volatile.Write(ref latestPeak, 0);
        busy = false;
    }

    public void Detach()
    {
        ApplyRegistration(false);
        CleanupRecording();
    }

    private void ReportResult(bool recognized, string status)
    {
        try { (recognized ? MatchedTone : MissedTone).Value.Play(); }
        catch (Exception error) when (error is InvalidOperationException or TimeoutException or IOException) { }
        SetStatus(status);
    }

    private static SoundPlayer CreateCue(double firstFrequency, double secondFrequency)
    {
        const int sampleRate = 16000, noteLength = 1280, gapLength = 320;
        var stream = new MemoryStream();
        using (var writer = new WaveFileWriter(stream, new WaveFormat(sampleRate, 16, 1)))
        {
            var bytes = new byte[(noteLength * 2 + gapLength) * 2];
            WriteNote(firstFrequency, 0);
            WriteNote(secondFrequency, noteLength + gapLength);
            writer.Write(bytes, 0, bytes.Length);

            void WriteNote(double frequency, int offset)
            {
                for (int i = 0; i < noteLength; i++)
                {
                    double fade = Math.Min(1, Math.Min(i, noteLength - 1 - i) / 100.0);
                    int sample = (int)(4600 * fade * Math.Sin(2 * Math.PI * frequency * i / sampleRate));
                    bytes[(offset + i) * 2] = (byte)sample;
                    bytes[(offset + i) * 2 + 1] = (byte)(sample >> 8);
                }
            }
        }
        stream.Position = 0;
        return new SoundPlayer(stream);
    }

    private void SetStatus(string status)
    {
        Status = status;
        Changed?.Invoke();
    }

    internal static IReadOnlyList<MicrophoneChoice> Microphones()
    {
        var names = Enumerable.Range(0, WaveIn.DeviceCount)
            .Select(index => (Device: index, Name: WaveIn.GetCapabilities(index).ProductName)).ToArray();
        var microphones = new List<MicrophoneChoice> { new(null, "Windows default microphone", -1) };
        foreach (var group in names.GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            int duplicate = 0;
            foreach (var item in group)
            {
                duplicate++;
                string id = item.Name + "|" + duplicate;
                string label = group.Count() > 1 ? $"{item.Name} ({duplicate})" : item.Name;
                microphones.Add(new MicrophoneChoice(id, label, item.Device));
            }
        }
        return microphones;
    }

    internal static int? ResolveMicrophone(string? selection, IEnumerable<MicrophoneChoice> devices) =>
        devices.FirstOrDefault(item => string.Equals(item.Id, selection, StringComparison.OrdinalIgnoreCase))?.DeviceNumber;

    internal static VoiceBinding? Match(string? input, IEnumerable<VoiceBinding> bindings)
    {
        string key = Normalize(input);
        return key.Length == 0 ? null : bindings.FirstOrDefault(binding => Normalize(binding.Phrase) == key);
    }

    internal static string Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";
        var builder = new StringBuilder(input.Length);
        foreach (char character in input)
            builder.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : ' ');
        return string.Join(" ", builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    internal static string? RecognizeWindows(byte[] clip, string language, string[] phrases, float minimumConfidence = .70f)
    {
        var recognizers = SpeechRecognitionEngine.InstalledRecognizers();
        var recognizer = recognizers.FirstOrDefault(item => item.Culture.Name.Equals(language, StringComparison.OrdinalIgnoreCase))
            ?? recognizers.FirstOrDefault(item => item.Culture.TwoLetterISOLanguageName.Equals(language, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("No Windows recognizer for " + language + ".");
        using var engine = new SpeechRecognitionEngine(recognizer);
        var choices = new Choices(phrases.Select(Normalize).Where(phrase => phrase.Length > 0).Distinct().ToArray());
        var grammar = new GrammarBuilder { Culture = recognizer.Culture };
        grammar.Append(choices);
        engine.LoadGrammar(new Grammar(grammar));
        using var audio = new MemoryStream(clip);
        engine.SetInputToWaveStream(audio);
        var result = engine.Recognize(TimeSpan.FromSeconds(2));
        return result is not null && result.Confidence >= minimumConfidence ? result.Text : null;
    }

    internal static async Task<string?> TranscribeVoiceboxAsync(byte[] clip, string language, CancellationToken token,
        HttpClient? transport = null)
    {
        var client = transport ?? Http;
        async Task<HttpResponseMessage> Post(string fileField, string modelField, string model)
        {
            using var form = new MultipartFormDataContent();
            using var file = new ByteArrayContent(clip);
            file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(file, fileField, "command.wav");
            form.Add(new StringContent(model), modelField);
            if (!string.IsNullOrWhiteSpace(language)) form.Add(new StringContent(language.Split('-')[0]), "language");
            return await client.PostAsync("transcribe", form, token);
        }

        using var standard = await Post("file", "model_size", "base");
        if (standard.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            using var alternate = await Post("audio", "model", "whisper-base");
            return await Read(alternate);
        }
        return await Read(standard);

        async Task<string?> Read(HttpResponseMessage response)
        {
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            return json.RootElement.GetProperty("text").GetString();
        }
    }
}
