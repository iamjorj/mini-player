using System;
using Microsoft.Win32;

namespace MiniPlayer;

/// <summary>
/// "Start with Windows" via the per-user Run key. Always an explicit choice (the welcome
/// window's checkbox or the tray menu) — a portable exe silently registering itself is how
/// malware behaves, and antivirus heuristics treat it that way too.
/// </summary>
internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MiniPlayer";

    private static string Entry => $"\"{Environment.ProcessPath}\"";

    public static bool Enabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) != null;
            }
            catch (Exception) { return false; }
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKey);
                if (value)
                    key.SetValue(ValueName, Entry);
                else
                    key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            catch (Exception) { }
        }
    }

    /// <summary>If registered but the exe has moved since, point the entry at the new place.</summary>
    public static void Heal()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(ValueName) is string current && current != Entry)
                key.SetValue(ValueName, Entry);
        }
        catch (Exception) { }
    }
}
