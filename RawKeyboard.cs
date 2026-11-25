using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Input;

namespace StarCitizenOverlay;

internal static class RawKeyboard
{
    public const int InputMessage = 0x00FF;
    private const uint InputSink = 0x00000100, Remove = 0x00000001, ReadInput = 0x10000003;
    private const ushort Break = 1, Extended = 2;
    private static readonly uint HeaderSize = (uint)Marshal.SizeOf<InputHeader>();
    private static readonly uint KeyboardSize = (uint)Marshal.SizeOf<KeyboardInput>();

    internal readonly record struct KeyEvent(IntPtr Device, int VirtualKey, bool Released);

    public static bool SetListener(IntPtr window, bool enabled)
    {
        var device = new InputDevice { UsagePage = 1, Usage = 6, Flags = enabled ? InputSink : Remove,
            Window = enabled ? window : IntPtr.Zero };
        return RegisterRawInputDevices(ref device, 1, (uint)Marshal.SizeOf<InputDevice>());
    }

    public static bool TryRead(IntPtr handle, out KeyEvent key)
    {
        key = default;
        uint length = KeyboardSize;
        uint read = GetRawInputData(handle, ReadInput, out var input, ref length, HeaderSize);
        if (read != KeyboardSize || input.Header.Type != 1 || input.VirtualKey is 0 or >= 255) return false;
        bool extended = (input.Flags & Extended) != 0;
        int virtualKey = input.VirtualKey switch
        {
            0x10 => input.ScanCode == 0x36 ? 0xA1 : 0xA0,
            0x11 => extended ? 0xA3 : 0xA2,
            0x12 => extended ? 0xA5 : 0xA4,
            _ => input.VirtualKey
        };
        key = new KeyEvent(input.Header.Device, virtualKey, (input.Flags & Break) != 0);
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InputDevice
    {
        public ushort UsagePage, Usage;
        public uint Flags;
        public IntPtr Window;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InputHeader
    {
        public uint Type, Size;
        public IntPtr Device, WParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public InputHeader Header;
        public ushort ScanCode, Flags, Reserved, VirtualKey;
        public uint Message, ExtraInformation;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices(ref InputDevice devices, uint count, uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(IntPtr handle, uint command, out KeyboardInput data, ref uint size, uint headerSize);
}

internal enum PushToTalkEdge { None, Pressed, Released }

internal sealed class PushToTalkInput
{
    private readonly HashSet<(IntPtr Device, int Key)> modifiers = [], heldBindings = [];
    private (IntPtr Device, int Key)? active;

    public PushToTalkEdge Read(RawKeyboard.KeyEvent key, int binding, ModifierKeys required, bool allowStart)
    {
        var id = (key.Device, key.VirtualKey);
        if (Modifier(key.VirtualKey) != ModifierKeys.None)
        {
            if (key.Released) modifiers.Remove(id);
            else modifiers.Add(id);
            return PushToTalkEdge.None;
        }

        if (key.Released)
        {
            heldBindings.Remove(id);
            if (active != id) return PushToTalkEdge.None;
            active = null;
            return PushToTalkEdge.Released;
        }

        if (key.VirtualKey != binding || !heldBindings.Add(id) || active is not null || !allowStart)
            return PushToTalkEdge.None;
        var down = modifiers.Aggregate(ModifierKeys.None, (result, entry) => result | Modifier(entry.Key));
        if (down != required) return PushToTalkEdge.None;
        active = id;
        return PushToTalkEdge.Pressed;
    }

    public void Reset()
    {
        active = null;
        heldBindings.Clear();
        modifiers.Clear();
    }

    private static ModifierKeys Modifier(int key) => key switch
    {
        0xA0 or 0xA1 => ModifierKeys.Shift,
        0xA2 or 0xA3 => ModifierKeys.Control,
        0xA4 or 0xA5 => ModifierKeys.Alt,
        0x5B or 0x5C => ModifierKeys.Windows,
        _ => ModifierKeys.None
    };
}
