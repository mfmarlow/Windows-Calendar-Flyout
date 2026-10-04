using System.Collections.ObjectModel;
using System.Diagnostics;
using Google;
using Google.Apis.Auth.OAuth2.Responses;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.System;
using static CalendarFlyout.NativeMethods;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;
using DispatcherQueuePriority = Microsoft.UI.Dispatching.DispatcherQueuePriority;

namespace CalendarFlyout;

public sealed partial class FlyoutWindow : Window
{
    private const int WidthDip = 360;
    private const int HeightDip = 660;
    private const int MarginDip = 12;

    private enum View { Setup, SignIn, Agenda }

    private readonly GoogleCalendarService _calendar = new();
    private readonly ObservableCollection<AgendaItem> _items = [];
    private readonly DispatcherQueueTimer _timer;

    private DateTime _selectedDay = DateTime.Today;
    private DateTime _today = DateTime.Today;
    private DateTime _lastHidden = DateTime.MinValue;
    private DateTime _lastRefresh = DateTime.MinValue;
    private bool _refreshing;
    private bool _shuttingDown;
    private bool _suppressSelectionChanged;

    // Month-grid dots: calendar colours per local day, loaded a month at a time.
    private readonly Dictionary<DateTime, List<Windows.UI.Color>> _dayColors = [];
    private readonly HashSet<DateTime> _loadedMonths = [];
    private readonly HashSet<DateTime> _loadingMonths = [];
    private int _densityGeneration;

    public event Action<string>? TooltipChanged;

    public FlyoutWindow()
    {
        InitializeComponent();

        AgendaList.ItemsSource = _items;
        SecretPathText.Text = GoogleCalendarService.ClientSecretPath;

        // Flyout chrome: acrylic like the system flyouts, thin border + rounded corners, no title bar.
        SystemBackdrop = new DesktopAcrylicBackdrop();
        var presenter = OverlappedPresenter.Create();
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false; // no taskbar button, not in Alt+Tab
        AppWindow.Title = "Calendar";

        AppWindow.Closing += (_, e) =>
        {
            if (_shuttingDown) return;
            e.Cancel = true; // Alt+F4 just hides it
            HideFlyout();
        };
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated) HideFlyout();
        };

        MonthView.SelectedDates.Add(DateTimeOffset.Now);

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMinutes(5);
        _timer.Tick += async (_, _) =>
        {
            RollOverIfNewDay();
            await RefreshAllAsync();
        };
        _timer.Start();

        UpdateDateHeader();
    }

    // ------------------------------------------------------------------ show / hide

    public void Toggle()
    {
        if (AppWindow.IsVisible) { HideFlyout(); return; }
        // Clicking the tray icon while open deactivates (hides) us first, then delivers the
        // click. Without this check the flyout would immediately reopen.
        if ((DateTime.Now - _lastHidden).TotalMilliseconds < 350) return;
        ShowFlyout();
    }

    public void ShowFlyout()
    {
        GetCursorPos(out var cursor);
        double scale = ScaleForPoint(cursor);
        var display = DisplayArea.GetFromPoint(new PointInt32(cursor.X, cursor.Y), DisplayAreaFallback.Primary);
        var work = display.WorkArea;
        var outer = display.OuterBounds;

        int margin = (int)(MarginDip * scale);
        int width = (int)(WidthDip * scale);
        int height = Math.Min((int)(HeightDip * scale), work.Height - 2 * margin);

        // Sit in the corner next to the taskbar, wherever it is docked.
        bool taskbarLeft = work.X > outer.X;
        bool taskbarTop = work.Y > outer.Y;
        int x = taskbarLeft ? work.X + margin : work.X + work.Width - width - margin;
        int y = taskbarTop ? work.Y + margin : work.Y + work.Height - height - margin;

        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));

        RollOverIfNewDay();
        UpdateDateHeader();
        Activate();
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));

        if (_calendar.IsSignedIn && DateTime.Now - _lastRefresh > TimeSpan.FromMinutes(1))
            _ = RefreshAsync();
    }

    public void HideFlyout()
    {
        if (!AppWindow.IsVisible) return;
        _lastHidden = DateTime.Now;
        AppWindow.Hide();
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, TrimMemory);
    }

    public void Shutdown()
    {
        _shuttingDown = true;
        _timer.Stop();
        Close();
    }

    // ------------------------------------------------------------------ auth flow

    public async Task InitializeAsync()
    {
        if (!_calendar.HasClientSecret) { SetView(View.Setup); return; }
        if (!_calendar.HasSavedToken) { SetView(View.SignIn); return; }
        await SignInAndLoadAsync(interactive: false);
    }

    private async Task SignInAndLoadAsync(bool interactive)
    {
        // If the saved token was revoked, the library falls back to the browser flow;
        // give that a time limit so we never hang forever on an abandoned browser tab.
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(interactive ? 5 : 2));
        SignInButton.IsEnabled = false;
        SignInButton.Content = "Waiting for browser…";
        try
        {
            await _calendar.SignInAsync(cts.Token);
            SetView(View.Agenda);
            ReloadDensity();
            await RefreshAsync();
            if (interactive) ShowFlyout(); // bring it back after the browser had focus
        }
        catch (OperationCanceledException)
        {
            SetView(View.SignIn);
        }
        catch (Exception ex)
        {
            SetView(View.SignIn);
            ShowError("Sign-in failed", ex.Message);
        }
        finally
        {
            SignInButton.IsEnabled = true;
            SignInButton.Content = "Sign in with Google";
        }
    }

    public async Task SignOutAsync()
    {
        await _calendar.SignOutAsync();
        _items.Clear();
        ClearDensity();
        TooltipChanged?.Invoke("Calendar");
        SetView(_calendar.HasClientSecret ? View.SignIn : View.Setup);
    }

    // ------------------------------------------------------------------ data

    /// <summary>Reloads the agenda and the month-grid dots.</summary>
    public async Task RefreshAllAsync()
    {
        ReloadDensity();
        await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (!_calendar.IsSignedIn || _refreshing) return;
        _refreshing = true;
        Progress.Visibility = Visibility.Visible;
        var day = _selectedDay;
        try
        {
            var events = await _calendar.GetDayAsync(day);
            if (day == _selectedDay) ShowItems(day, events);
        }
        catch (TokenResponseException)
        {
            // Refresh token expired or revoked (e.g. 7-day limit while the OAuth app is in Testing).
            await _calendar.SignOutAsync();
            SetView(View.SignIn);
            ShowError("Signed out", "Your Google sign-in expired. Please sign in again.");
        }
        catch (GoogleApiException ex)
        {
            ShowError("Google Calendar error", ex.Error?.Message ?? ex.Message);
        }
        catch (HttpRequestException)
        {
            ShowError("Offline", "Couldn't reach Google. Will retry automatically.");
        }
        catch (Exception ex)
        {
            ShowError("Something went wrong", ex.Message);
        }
        finally
        {
            _refreshing = false;
            Progress.Visibility = Visibility.Collapsed;
        }

        // The selected day changed while we were loading: load the new one now.
        if (day != _selectedDay) await RefreshAsync();
        else if (!AppWindow.IsVisible) TrimMemory(); // background refresh: don't hold on to what it allocated
    }

    private void ShowItems(DateTime day, List<CalendarEventInfo> events)
    {
        var items = events
            .Select(e => AgendaItem.FromEvent(e, day))
            .OrderByDescending(i => i.IsAllDay)
            .ThenBy(i => i.Start)
            .ToList();

        _items.Clear();
        foreach (var item in items) _items.Add(item);
        EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ErrorBar.IsOpen = false;
        _lastRefresh = DateTime.Now;

        if (day == DateTime.Today) UpdateTooltip(items);
    }

    private void UpdateTooltip(List<AgendaItem> todays)
    {
        var next = todays
            .Where(i => !i.IsAllDay && i.End > DateTimeOffset.Now)
            .OrderBy(i => i.Start)
            .FirstOrDefault();
        string text = next is null
            ? $"{DateTime.Now:dddd, MMMM d}\nNothing else today"
            : next.Start <= DateTimeOffset.Now
                ? $"Now: {next.Title}"
                : $"Next: {next.Title} at {next.Start:t}";
        TooltipChanged?.Invoke(text);
    }

    private void RollOverIfNewDay()
    {
        if (DateTime.Today == _today) return;
        // If you were looking at "today", follow it to the new day.
        if (_selectedDay == _today) SelectDay(DateTime.Today);
        _today = DateTime.Today;
        UpdateDateHeader();
    }

    // ------------------------------------------------------------------ month-grid dots

    private void ApplyDensity(CalendarViewDayItem item)
    {
        var day = item.Date.Date;
        item.SetDensityColors(_dayColors.TryGetValue(day, out var colors) ? colors : null);
        if (_calendar.IsSignedIn) _ = LoadMonthAsync(new DateTime(day.Year, day.Month, 1));
    }

    private async Task LoadMonthAsync(DateTime month)
    {
        if (_loadedMonths.Contains(month) || !_loadingMonths.Add(month)) return;
        int generation = _densityGeneration;
        var next = month.AddMonths(1);
        try
        {
            var events = await _calendar.GetRangeAsync(month, next);
            if (generation != _densityGeneration) return; // signed out or reloaded meanwhile

            for (var d = month; d < next; d = d.AddDays(1)) _dayColors.Remove(d);
            foreach (var info in events)
            {
                var (_, start, end) = AgendaItem.GetTimes(info.Event);
                if (start is null) continue;
                var first = start.Value.Date;
                // End is exclusive: an event ending at midnight doesn't mark the next day.
                var last = end is { } e && e > start ? e.AddTicks(-1).Date : first;
                var color = AgendaItem.ParseColor(info.CalendarColor);

                for (var d = first < month ? month : first; d <= last && d < next; d = d.AddDays(1))
                {
                    if (!_dayColors.TryGetValue(d, out var colors)) _dayColors[d] = colors = [];
                    if (colors.Count < 10 && !colors.Contains(color)) colors.Add(color); // CalendarView shows at most 10
                }
            }
            _loadedMonths.Add(month);
            UpdateRealizedDayItems();
        }
        catch
        {
            // The agenda reports errors; this month is retried the next time it's shown.
        }
        finally
        {
            if (generation == _densityGeneration) _loadingMonths.Remove(month);
        }
    }

    /// <summary>Marks every month stale and reloads the visible ones, keeping current dots until then.</summary>
    private void ReloadDensity()
    {
        _densityGeneration++;
        _loadedMonths.Clear();
        _loadingMonths.Clear();
        UpdateRealizedDayItems();
    }

    private void ClearDensity()
    {
        _dayColors.Clear();
        ReloadDensity();
    }

    // CalendarViewDayItemChanging only fires as cells scroll into view, so push updates to visible cells.
    private void UpdateRealizedDayItems()
    {
        foreach (var item in FindDescendants<CalendarViewDayItem>(MonthView)) ApplyDensity(item);
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var d in FindDescendants<T>(child)) yield return d;
        }
    }

    // ------------------------------------------------------------------ UI helpers

    private void SetView(View view)
    {
        SetupPanel.Visibility = view == View.Setup ? Visibility.Visible : Visibility.Collapsed;
        SignInPanel.Visibility = view == View.SignIn ? Visibility.Visible : Visibility.Collapsed;
        AgendaList.Visibility = view == View.Agenda ? Visibility.Visible : Visibility.Collapsed;
        if (view != View.Agenda) EmptyText.Visibility = Visibility.Collapsed;
    }

    private void ShowError(string title, string message)
    {
        ErrorBar.Title = title;
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }

    private void UpdateDateHeader()
    {
        DateHeader.Text = DateTime.Now.ToString("dddd, MMMM d");
        var diff = (_selectedDay - DateTime.Today).Days;
        AgendaHeader.Text = diff switch
        {
            0 => "Today",
            1 => "Tomorrow",
            -1 => "Yesterday",
            _ => _selectedDay.ToString("dddd, MMMM d"),
        };
    }

    private void SelectDay(DateTime day)
    {
        _suppressSelectionChanged = true;
        MonthView.SelectedDates.Clear();
        _suppressSelectionChanged = false;
        MonthView.SelectedDates.Add(new DateTimeOffset(day.Date));
        MonthView.SetDisplayDate(new DateTimeOffset(day.Date));
    }

    // ------------------------------------------------------------------ event handlers

    private async void MonthView_SelectedDatesChanged(CalendarView sender, CalendarViewSelectedDatesChangedEventArgs args)
    {
        if (_suppressSelectionChanged) return;
        if (args.AddedDates.Count == 0)
        {
            // Clicking the selected date deselects it; keep a selection instead.
            if (sender.SelectedDates.Count == 0) sender.SelectedDates.Add(new DateTimeOffset(_selectedDay));
            return;
        }
        var day = args.AddedDates[0].Date;
        if (day == _selectedDay && _items.Count > 0) return;
        _selectedDay = day;
        _items.Clear();
        EmptyText.Visibility = Visibility.Collapsed;
        UpdateDateHeader();
        await RefreshAsync();
    }

    private void MonthView_DayItemChanging(CalendarView sender, CalendarViewDayItemChangingEventArgs args)
    {
        if (args.Item is { } item) ApplyDensity(item);
    }

    private void Today_Click(object sender, RoutedEventArgs e) => SelectDay(DateTime.Today);

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAllAsync();

    private void OpenWeb_Click(object sender, RoutedEventArgs e)
    {
        var d = _selectedDay;
        Process.Start(new ProcessStartInfo($"https://calendar.google.com/calendar/r/day/{d.Year}/{d.Month}/{d.Day}")
        {
            UseShellExecute = true,
        });
        HideFlyout();
    }

    private void AgendaList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is AgendaItem item)
        {
            item.OpenInBrowser();
            HideFlyout();
        }
    }

    private async void SignIn_Click(object sender, RoutedEventArgs e) => await SignInAndLoadAsync(interactive: true);

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(App.DataDir);
        Process.Start("explorer.exe", App.DataDir);
    }

    private async void CheckAgain_Click(object sender, RoutedEventArgs e) => await InitializeAsync();

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape) HideFlyout();
    }
}
