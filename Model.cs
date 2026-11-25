using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StarCitizen.LocalTelemetry;

namespace StarCitizenOverlay;

enum Display { Text, Bar, Ring, Range }

sealed record Widget
{
    public string Field { get; set; } = "Actor.Vitals.Health";
    public string Label { get; set; } = "Health";
    public string Header { get; set; } = "";
    public string Group { get; set; } = "";
    public Display Display { get; set; } = Display.Bar;
    public bool AutomaticRange { get; set; } = true;
    public double Min { get; set; }
    public double Max { get; set; } = 100;
    public string Unit { get; set; } = "";
    public bool ShowPercent { get; set; }
    public int Decimals { get; set; }
    public string Color { get; set; } = "#64E4DB";
    public double Opacity { get; set; } = 1;
    public int Width { get; set; } = 240;
    public int Height { get; set; } = 68;
    public double X { get; set; } = 80;
    public double Y { get; set; } = 130;
    public bool Enabled { get; set; } = true;
    public StateRule[] StateRules { get; set; } = [];
}

sealed class Layout
{
    public List<Widget> Widgets { get; set; } =
    [
        new(),
        new() { Field = Feed.Ammo, Label = "Countermeasure ammo", Display = Display.Text, Y = 210 }
    ];

    public static bool SameGroup(string? first, string? second) =>
        string.Equals(first?.Trim() ?? "", second?.Trim() ?? "", StringComparison.OrdinalIgnoreCase);

    public IEnumerable<Widget> DragMembers(Widget anchor, bool moveGroup) =>
        moveGroup && !string.IsNullOrWhiteSpace(anchor.Group)
            ? Widgets.Where(widget => SameGroup(widget.Group, anchor.Group))
            : [anchor];

    private static string FilePath(bool demo) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StarCitizenOverlay", demo ? "demo-layout.json" : "layout.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static Layout Load(bool demo)
    {
        string path = FilePath(demo);
        if (!File.Exists(path)) return new();
        return JsonSerializer.Deserialize<Layout>(File.ReadAllText(path), JsonOptions) ?? new();
    }

    public void Save(bool demo)
    {
        string path = FilePath(demo);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temporary, path, true);
    }
}

sealed class Feed
{
    public const string Ammo = "Weapons.Countermeasures.TotalAmount";
    public const string Decoys = "Weapons.Countermeasures.DecoyAmount";
    public const string Noise = "Weapons.Countermeasures.NoiseAmount";
    private static readonly PropertyInfo[] Sections = typeof(TelemetryFrame).GetProperties();
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> Properties = new();
    private readonly ConcurrentDictionary<string, (object Value, long Timestamp)> sections = new();
    private readonly ConcurrentDictionary<string, Type> fields = new(StringComparer.Ordinal);
    private volatile string status = "Waiting for Star Citizen";
    private volatile string? schemaWarning;
    private long lastFrame;
    private readonly StateCatalog? stateCatalog;
    public string Status => schemaWarning is null ? status : status + "  |  Schema differs from package";
    public IEnumerable<string> Fields => fields.Keys.OrderBy(key => key, StringComparer.Ordinal);
    public IEnumerable<KeyValuePair<string, Type>> Catalog => fields;

    public Feed(StateCatalog? stateCatalog = null)
    {
        this.stateCatalog = stateCatalog;
        foreach (var section in Sections)
            Discover(section.Name, section.PropertyType, null);
        fields.TryAdd(Ammo, typeof(double));
        fields.TryAdd(Decoys, typeof(double));
        fields.TryAdd(Noise, typeof(double));
    }

    public async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var client = new StarCitizenTelemetryClient();
                await foreach (var update in client.ObserveTelemetryAsync(cancellationToken: token).ConfigureAwait(false))
                {
                    switch (update.Kind)
                    {
                        case TelemetryStreamEventKind.Frame when update.Frame is { } frame:
                            Ingest(frame);
                            break;
                        case TelemetryStreamEventKind.Connected:
                            status = "Connected to Star Citizen";
                            break;
                        case TelemetryStreamEventKind.Disconnected:
                            sections.Clear();
                            status = "Disconnected · retrying";
                            break;
                        case TelemetryStreamEventKind.SchemaMismatch:
                            schemaWarning = update.Mismatch?.ToString();
                            break;
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                sections.Clear();
                status = $"Telemetry error · retrying ({error.GetType().Name})";
            }

            try { await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    internal void Ingest(TelemetryFrame frame)
    {
        Volatile.Write(ref lastFrame, Stopwatch.GetTimestamp());
        foreach (var section in Sections)
        {
            var value = section.GetValue(frame);
            if (value is null) continue;
            sections[section.Name] = (value, Stopwatch.GetTimestamp());
            Discover(section.Name, section.PropertyType, value);
        }
    }

    private static PropertyInfo[] Members(Type type) => Properties.GetOrAdd(type, item =>
        item.GetProperties().Where(property => property.GetIndexParameters().Length == 0).ToArray());

    private void Discover(string path, Type type, object? value)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ValueList<>))
        {
            var element = type.GetGenericArguments()[0];
            fields.TryAdd(path + ".Count", typeof(int));
            Discover(path + "[0]", element, null);
            if (value is IEnumerable list)
            {
                int index = 0;
                foreach (var item in list)
                    Discover($"{path}[{index++}]", element, item);
            }
            return;
        }

        if (type == typeof(Gauge)) fields.TryAdd(path, type);
        if (type.IsEnum || type.IsPrimitive || type == typeof(string) || type == typeof(DateTimeOffset))
        {
            fields.TryAdd(path, type);
            if (value is bool or Enum or string) stateCatalog?.Observe(path, value);
            return;
        }

        foreach (var property in Members(type))
            Discover(path + "." + property.Name, property.PropertyType, value is null ? null : property.GetValue(value));
    }

    public object? Read(string path)
    {
        if (Stopwatch.GetElapsedTime(Volatile.Read(ref lastFrame)) > TimeSpan.FromSeconds(15)) return null;
        if (path is Ammo or Decoys or Noise)
        {
            if (ReadSection("Weapons") is not WeaponsTelemetry telemetry) return null;
            var type = path == Decoys ? CountermeasureType.Decoy : CountermeasureType.Noise;
            return telemetry.Countermeasures.Where(item => path == Ammo || item.Type == type)
                .Sum(item => (double)item.Amount);
        }

        object? current = null;
        bool first = true;
        foreach (string part in path.Split('.'))
        {
            int bracket = part.IndexOf('[');
            string name = bracket < 0 ? part : part[..bracket];
            if (first)
            {
                current = ReadSection(name);
                first = false;
            }
            else
            {
                if (current is null) return null;
                current = Members(current.GetType()).FirstOrDefault(property => property.Name == name)?.GetValue(current);
            }
            if (bracket >= 0)
            {
                if (current is not IEnumerable list || !part.EndsWith(']') ||
                    !int.TryParse(part.AsSpan(bracket + 1, part.Length - bracket - 2), out int index) || index < 0)
                    return null;
                current = list.Cast<object>().ElementAtOrDefault(index);
            }
        }
        return current;
    }

    private object? ReadSection(string name) =>
        sections.TryGetValue(name, out var entry) &&
        Stopwatch.GetElapsedTime(entry.Timestamp) < TimeSpan.FromSeconds(15) ? entry.Value : null;

    internal static double? Number(object? value)
    {
        if (value is Gauge gauge) return Number(gauge.Current);
        if (value is null || value is bool || value.GetType().IsEnum ||
            value is not (byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal))
            return null;
        double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
        return double.IsFinite(number) ? number : null;
    }
}
