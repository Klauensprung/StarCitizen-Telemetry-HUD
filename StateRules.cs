using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace StarCitizenOverlay;

enum StatePresentation { Text, Circle }

// DataGrid tracks selected rows by hash. Editing a rule must keep the row's identity stable.
sealed class StateRule
{
    public string State { get; set; } = "";
    public string Text { get; set; } = "";
    public string Color { get; set; } = "#64E4DB";
    public StatePresentation Presentation { get; set; }
    public bool ShowText { get; set; } = true;

    public (string State, string Text, string Color, StatePresentation Presentation, bool ShowText) GetValues() =>
        (State, Text, Color, Presentation, ShowText);

    public StateRule Copy() => new()
    {
        State = State.Trim(), Text = Text.Trim(), Color = Color,
        Presentation = Presentation, ShowText = ShowText
    };

    public static string? Key(object? value) => value switch
    {
        bool enabled => enabled ? "On" : "Off",
        Enum state => state.ToString(),
        string text when !string.IsNullOrWhiteSpace(text) => text.Trim(),
        _ => null
    };

    public static StateRule? Match(IEnumerable<StateRule>? rules, object? value)
    {
        string? key = Key(value);
        return key is null ? null : rules?.FirstOrDefault(rule =>
            string.Equals(rule.State, key, StringComparison.OrdinalIgnoreCase));
    }
}

sealed class StateCatalog
{
    private const int MaxValues = 48;
    private readonly object gate = new();
    private readonly Dictionary<string, HashSet<string>> observed = new(StringComparer.Ordinal);
    private readonly string path;
    private long lastFlush = Stopwatch.GetTimestamp();
    private bool dirty;

    public StateCatalog(bool demo, string? customPath = null)
    {
        path = customPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "StarCitizenOverlay", demo ? "demo-observed-states.json" : "observed-states.json");
        try
        {
            if (!File.Exists(path)) return;
            var stored = JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(path));
            if (stored is null) return;
            foreach (var (field, states) in stored)
                observed[field] = new HashSet<string>(states.Take(MaxValues), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            ReadError = "Couldn’t read remembered telemetry states. " + ex.Message;
        }
    }

    public string? ReadError { get; }

    public void Observe(string field, object? value)
    {
        string? state = StateRule.Key(value);
        if (state is null || state.Length > 128) return;
        lock (gate)
        {
            if (!observed.TryGetValue(field, out var states))
            {
                states = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                observed[field] = states;
            }
            if (states.Count < MaxValues && states.Add(state)) dirty = true;
        }
    }

    public string[] Suggestions(string field, Type? type, object? current)
    {
        string[] remembered;
        lock (gate) remembered = observed.TryGetValue(field, out var states) ? states.ToArray() : [];
        string[] declared = type == typeof(bool) ? ["On", "Off"] : type?.IsEnum == true ? Enum.GetNames(type) : [];
        string[] actual = StateRule.Key(current) is string state ? [state] : [];
        return actual.Concat(remembered.Order(StringComparer.OrdinalIgnoreCase)).Concat(declared)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public void Flush(bool force = false)
    {
        Dictionary<string, string[]> snapshot;
        lock (gate)
        {
            if (!dirty || (!force && Stopwatch.GetElapsedTime(lastFlush) < TimeSpan.FromSeconds(5))) return;
            lastFlush = Stopwatch.GetTimestamp();
            snapshot = observed.ToDictionary(pair => pair.Key, pair => pair.Value.Order().ToArray(), StringComparer.Ordinal);
            dirty = false;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, path, true);
        }
        catch
        {
            lock (gate) dirty = true;
            throw;
        }
    }
}
