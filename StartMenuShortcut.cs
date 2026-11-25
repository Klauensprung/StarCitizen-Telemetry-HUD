using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.CSharp.RuntimeBinder;

namespace StarCitizenOverlay;

internal static class StartMenuShortcut
{
    private const string ExecutableName = "StarCitizenOverlay.exe";
    internal const string EntryName = "Star Citizen Telemetry HUD.lnk";

    internal static void EnsureForCurrentUser()
    {
        string? executable = Environment.ProcessPath;
        if (!string.Equals(Path.GetFileName(executable), ExecutableName, StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            Ensure(executable!, Environment.GetFolderPath(Environment.SpecialFolder.Programs));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or COMException
            or RuntimeBinderException or InvalidOperationException)
        {
            Trace.TraceWarning("Unable to add Start menu shortcut: {0}", error.Message);
        }
    }

    internal static void Ensure(string executable, string programs)
    {
        if (string.IsNullOrWhiteSpace(programs) || !File.Exists(executable)) return;
        Directory.CreateDirectory(programs);

        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("Windows Script Host is unavailable.");
        dynamic shell = Activator.CreateInstance(shellType)
            ?? throw new InvalidOperationException("Windows Script Host could not start.");
        try
        {
            string path = Path.Combine(programs, EntryName);
            dynamic shortcut = shell.CreateShortcut(path);
            try
            {
                string existingTarget = shortcut.TargetPath;
                if (File.Exists(existingTarget) &&
                    string.Equals(Path.GetFileName(existingTarget), ExecutableName, StringComparison.OrdinalIgnoreCase)) return;

                shortcut.TargetPath = executable;
                shortcut.WorkingDirectory = Path.GetDirectoryName(executable);
                shortcut.IconLocation = executable + ",0";
                shortcut.Description = "Open the Star Citizen telemetry overlay.";
                shortcut.Save();
            }
            finally { Marshal.ReleaseComObject(shortcut); }
        }
        finally { Marshal.ReleaseComObject(shell); }
    }
}
