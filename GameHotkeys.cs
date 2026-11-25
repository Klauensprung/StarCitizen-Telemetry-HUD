using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Grpc.Core;
using StarCitizen.LocalTelemetry;

namespace StarCitizenOverlay;

enum GameKeyMode { Toggle, Trigger }

sealed record GameKey(GameKeyMode Mode, string Action, ModifierKeys Modifiers, Key Key)
{
    public string Shortcut => (Modifiers.HasFlag(ModifierKeys.Control) ? "Ctrl+" : "") +
        (Modifiers.HasFlag(ModifierKeys.Alt) ? "Alt+" : "") +
        (Modifiers.HasFlag(ModifierKeys.Shift) ? "Shift+" : "") + Key;

    public string Description => Mode == GameKeyMode.Toggle
        ? GameToggles.Known.FirstOrDefault(item => item.Id == Action)?.Name ?? Action
        : Action;
}

sealed class GameHotkeys
{
    private const int FirstId = 100;
    private readonly Dictionary<int, GameKey> registered = [];
    private readonly List<GameKey> bindings = [];
    private readonly Feed feed;
    private readonly bool demo;
    private readonly string path;
    private readonly CancellationToken token;
    private IReadOnlyList<GameAction>? advertised;
    private long discoveredAt;
    private IntPtr window;
    private bool executing;
    private bool discovering;
    public string Status { get; private set; } = "Action availability has not been checked.";
    public string LastResult { get; private set; } = "";
    public IReadOnlyList<GameKey> Bindings => bindings;
    public IReadOnlyList<GameAction> Choices => advertised ?? GameActions.Known;
    public bool ActionsAvailable => advertised is { Count: > 0 };
    public bool IsRegistered(GameKey binding) => registered.ContainsValue(binding);

    public GameHotkeys(Feed feed, bool demo, CancellationToken token, string? customPath = null)
    {
        this.feed = feed;
        this.demo = demo;
        this.token = token;
        path = customPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StarCitizenOverlay",
            demo ? "demo-hotkeys.json" : "hotkeys.json");
        try
        {
            if (File.Exists(path))
                bindings.AddRange(JsonSerializer.Deserialize<List<GameKey>>(File.ReadAllText(path)) ?? []);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            LastResult = "Couldn’t load game hotkeys. " + error.Message;
        }
    }

    public void Attach(IntPtr handle)
    {
        window = handle;
    }

    public bool IsAdvertised(GameKey binding)
    {
        if (advertised is null) return false;
        bool Available(string name) => advertised.Any(action => action.Name == name);
        if (binding.Mode == GameKeyMode.Trigger) return Available(binding.Action);
        if (DirectToggle(binding.Action) is string direct && Available(direct)) return true;
        var toggle = GameToggles.Known.FirstOrDefault(item => item.Id == binding.Action);
        return toggle is not null && Available(toggle.OnAction) && Available(toggle.OffAction);
    }

    public string? Add(GameKey binding)
    {
        if ((binding.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == 0)
            return "Include Ctrl or Alt in the shortcut.";
        if (bindings.Any(item => item.Key == binding.Key && item.Modifiers == binding.Modifiers))
            return "That shortcut already has a game action.";
        if (binding.Key is Key.F9 or Key.F10 && binding.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt))
            return "That shortcut is reserved for the overlay.";
        if (IsAdvertised(binding) && !Register(binding)) return "Windows could not register that shortcut. Choose a different combination.";
        bindings.Add(binding);
        try { Save(); return null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            bindings.Remove(binding);
            Unregister(binding);
            return "Couldn’t save the shortcut. " + error.Message;
        }
    }

    public string? Remove(GameKey binding)
    {
        int index = bindings.IndexOf(binding);
        if (index < 0) return null;
        bindings.RemoveAt(index);
        try { Save(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            bindings.Insert(index, binding);
            return "Couldn’t save the shortcut. " + error.Message;
        }
        Unregister(binding);
        return null;
    }

    private bool Register(GameKey binding)
    {
        int virtualKey = KeyInterop.VirtualKeyFromKey(binding.Key);
        if (virtualKey == 0 || window == IntPtr.Zero || demo) return false;
        int id = FirstId;
        while (registered.ContainsKey(id)) id++;
        if (!Native.RegisterHotKey(window, id, (uint)binding.Modifiers | 0x4000, (uint)virtualKey)) return false;
        registered.Add(id, binding);
        return true;
    }

    private void Unregister(GameKey binding)
    {
        int id = registered.FirstOrDefault(pair => pair.Value == binding).Key;
        if (id < FirstId) return;
        Native.UnregisterHotKey(window, id);
        registered.Remove(id);
    }

    public void Detach()
    {
        foreach (int id in registered.Keys) Native.UnregisterHotKey(window, id);
        registered.Clear();
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(bindings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, true);
    }

    public async Task DiscoverAsync(bool force = false)
    {
        if (demo) { Status = "Demo mode · game shortcuts are inactive."; return; }
        if (token.IsCancellationRequested || discovering ||
            (!force && Stopwatch.GetElapsedTime(discoveredAt) < TimeSpan.FromSeconds(8))) return;
        discovering = true;
        discoveredAt = Stopwatch.GetTimestamp();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var client = new StarCitizenTelemetryClient();
            advertised = await client.GetActionsAsync(timeout.Token);
            Status = advertised.Count == 0 ? "The game advertises no actions." : $"{advertised.Count} actions advertised by Star Citizen.";
        }
        catch (RpcException error) when (error.StatusCode == StatusCode.Unimplemented)
        {
            advertised = null;
            Status = "Action discovery is unavailable on this game build. Saved game shortcuts are inactive.";
        }
        catch (Exception error) when (error is RpcException or OperationCanceledException)
        {
            advertised = null;
            Status = "Game actions are unavailable. Refresh to check the connection.";
        }
        finally
        {
            discovering = false;
            if (!ActionsAvailable || token.IsCancellationRequested) Detach();
            else
            {
                foreach (var binding in registered.Values.Where(binding => !IsAdvertised(binding)).ToArray())
                    Unregister(binding);
                foreach (var binding in bindings.Where(binding => IsAdvertised(binding) && !IsRegistered(binding)))
                    if (!Register(binding)) LastResult = "Some saved shortcuts are unavailable. Check the bindings below.";
            }
        }
    }

    public bool Handle(int id)
    {
        if (!registered.TryGetValue(id, out var binding)) return false;
        if (!executing && IsGameForeground()) _ = ExecuteAsync(binding);
        return true;
    }

    public async Task<string> RunVoiceActionAsync(GameKeyMode mode, string action)
    {
        if (demo) return "Game actions are inactive in demo mode.";
        if (!IsGameForeground()) return "Focus Star Citizen to use game voice actions.";
        if (executing) return "Another game action is in progress.";
        await ExecuteAsync(new GameKey(mode, action, ModifierKeys.None, Key.None));
        return LastResult;
    }

    private async Task ExecuteAsync(GameKey binding)
    {
        executing = true;
        try
        {
            await DiscoverAsync();
            if (advertised is null) { LastResult = Status; return; }
            if (!IsGameForeground()) return;
            string? action = ResolveAction(binding, advertised, feed.Read("Vehicle") as VehicleTelemetry,
                feed.Read("Turret") as TurretTelemetry);
            if (action is null)
            {
                LastResult = "The game does not advertise this action, or the current toggle state is unavailable.";
                return;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var client = new StarCitizenTelemetryClient();
            var result = await client.TriggerActionAsync(action, cancellationToken: timeout.Token);
            LastResult = result.IsSuccess ? "Game accepted " + binding.Description + "."
                : $"Game rejected {binding.Description} ({result.Status}). {result.Message}".TrimEnd();
        }
        catch (RpcException error)
        {
            LastResult = error.StatusCode == StatusCode.Unimplemented ? "Game actions are unavailable on this build."
                : $"The game did not acknowledge the action ({error.StatusCode}).";
        }
        catch (OperationCanceledException) { LastResult = "The game did not acknowledge the action."; }
        finally { executing = false; }
    }

    internal static string? ResolveAction(GameKey binding, IReadOnlyList<GameAction> advertised,
        VehicleTelemetry? vehicle, TurretTelemetry? turret)
    {
        bool Available(string name) => advertised.Any(action => action.Name == name);
        if (binding.Mode == GameKeyMode.Trigger) return Available(binding.Action) ? binding.Action : null;
        string? direct = DirectToggle(binding.Action);
        if (direct is not null && Available(direct)) return direct;
        var toggle = GameToggles.Known.FirstOrDefault(item => item.Id == binding.Action);
        if (toggle?.GetState(vehicle, turret) is not bool on) return null;
        string target = on ? toggle.OffAction : toggle.OnAction;
        return Available(target) ? target : null;
    }

    private static string? DirectToggle(string id) => id switch
    {
        "lights" => GameActions.LightsController.Lights,
        "shields" => GameActions.SpaceshipPower.PowerToggleShields,
        _ => null
    };

    private static bool IsGameForeground()
    {
        Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out uint processId);
        if (processId == 0) return false;
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName.Equals("StarCitizen", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
