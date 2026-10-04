using System.Diagnostics;
using Microsoft.UI.Xaml;

namespace CalendarFlyout;

public partial class App : Application
{
    public static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CalendarFlyout");

    private enum MenuId { Refresh = 1, OpenWeb, StartWithWindows, SignOut, Exit }

    private Mutex? _singleInstance;
    private FlyoutWindow? _window;
    private TrayIcon? _tray;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            try { File.AppendAllText(Path.Combine(DataDir, "error.log"), $"{DateTime.Now:u} {e.Exception}\n\n"); } catch { }
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _singleInstance = new Mutex(true, "CalendarFlyout.SingleInstance", out bool isFirstInstance);
        if (!isFirstInstance)
        {
            Exit();
            return;
        }

        Directory.CreateDirectory(DataDir);
        NativeMethods.EnableDarkContextMenus();

        var window = _window = new FlyoutWindow();
        var tray = _tray = new TrayIcon(TrayIconPath, "Calendar");

        tray.LeftClick += window.Toggle;
        tray.BuildMenu = () =>
        [
            new TrayMenuItem((int)MenuId.Refresh, "Refresh"),
            new TrayMenuItem((int)MenuId.OpenWeb, "Open Google Calendar"),
            TrayMenuItem.Separator,
            new TrayMenuItem((int)MenuId.StartWithWindows, "Start with Windows", Checked: StartupManager.IsEnabled),
            new TrayMenuItem((int)MenuId.SignOut, "Sign out"),
            TrayMenuItem.Separator,
            new TrayMenuItem((int)MenuId.Exit, "Exit"),
        ];
        tray.MenuCommand += OnMenuCommand;
        window.TooltipChanged += tray.SetTooltip;

        bool launchedAtLogin = Environment.GetCommandLineArgs().Contains("--background");
        if (!launchedAtLogin)
            window.ShowFlyout();

        _ = window.InitializeAsync();
    }

    private async void OnMenuCommand(int id)
    {
        switch ((MenuId)id)
        {
            case MenuId.Refresh:
                await _window!.RefreshAllAsync(userRequested: true);
                break;
            case MenuId.OpenWeb:
                Process.Start(new ProcessStartInfo("https://calendar.google.com/") { UseShellExecute = true });
                break;
            case MenuId.StartWithWindows:
                StartupManager.SetEnabled(!StartupManager.IsEnabled);
                break;
            case MenuId.SignOut:
                await _window!.SignOutAsync();
                break;
            case MenuId.Exit:
                _tray?.Dispose();
                _window?.Shutdown();
                Exit();
                break;
        }
    }

    // White glyph for a dark taskbar, black glyph for a light one.
    private static string TrayIconPath() => Path.Combine(
        AppContext.BaseDirectory, "Assets",
        NativeMethods.TaskbarUsesLightTheme() ? "tray-black.ico" : "tray-white.ico");
}
