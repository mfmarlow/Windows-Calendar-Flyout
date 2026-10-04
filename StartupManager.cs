using Microsoft.Win32;

namespace CalendarFlyout;

/// <summary>Per-user "start with Windows" via the HKCU Run key (no admin needed).</summary>
internal static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CalendarFlyout";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled)
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --background");
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
