using System.Collections.ObjectModel;
using System.Diagnostics;
using Google;
using Google.Apis.Auth.OAuth2.Responses;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
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
    private const int HeightDip = 660; // default and minimum; you can drag the top edge taller
    private const int MarginDip = 12;

    private enum View { Setup, SignIn, Agenda }
    private enum AgendaMode { Day, Week }
    private const int WeekDays = 7;

    private readonly GoogleCalendarService _calendar = new();
    private readonly ObservableCollection<AgendaItem> _items = [];
    private readonly CollectionViewSource _weekSource = new() { IsSourceGrouped = true };
    private AgendaMode _mode = AgendaMode.Day;
    private readonly DispatcherQueueTimer _timer;
    private readonly OverlappedPresenter _presenter;
    private readonly Settings _settings = Settings.Load();
    private double _scale = 1.0;

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
        var presenter = _presenter = OverlappedPresenter.Create();
        presenter.IsResizable = true; // height only: ShowFlyout pins the width with min = max
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
        AppWindow.SetPresenter(presenter);
        AppWindow.Changed += (_, e) =>
        {
            if (e.DidSizeChange && AppWindow.IsVisible) _settings.FlyoutHeight = AppWindow.Size.Height / _scale;
        };
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
        double scale = _scale = ScaleForPoint(cursor);
        var display = DisplayArea.GetFromPoint(new PointInt32(cursor.X, cursor.Y), DisplayAreaFallback.Primary);
        var work = display.WorkArea;
        var outer = display.OuterBounds;

        // Sit in the corner next to the taskbar, wherever it is docked.
        bool taskbarLeft = work.X > outer.X;
        bool taskbarTop = work.Y > outer.Y;

        // An auto-hide taskbar isn't excluded from the work area, but it's showing whenever you
        // click the tray icon. Keep clear of it as if it were always there.
        if (GetTaskbar() is { AutoHide: true } taskbar && Overlaps(taskbar.Rect, outer))
        {
            var r = taskbar.Rect;
            int right = work.X + work.Width, bottom = work.Y + work.Height;
            switch (taskbar.Edge)
            {
                case TaskbarEdge.Bottom: bottom = Math.Min(bottom, outer.Y + outer.Height - (r.Bottom - r.Top)); break;
                case TaskbarEdge.Top: work.Y = Math.Max(work.Y, outer.Y + (r.Bottom - r.Top)); taskbarTop = true; break;
                case TaskbarEdge.Right: right = Math.Min(right, outer.X + outer.Width - (r.Right - r.Left)); break;
                case TaskbarEdge.Left: work.X = Math.Max(work.X, outer.X + (r.Right - r.Left)); taskbarLeft = true; break;
            }
            work.Width = right - work.X;
            work.Height = bottom - work.Y;
        }

        int margin = (int)(MarginDip * scale);
        int width = (int)(WidthDip * scale);
        int maxHeight = work.Height - 2 * margin;
        int minHeight = Math.Min((int)(HeightDip * scale), maxHeight);
        int height = Math.Clamp((int)((_settings.FlyoutHeight ?? HeightDip) * scale), minHeight, maxHeight);
        _presenter.PreferredMinimumWidth = _presenter.PreferredMaximumWidth = width;
        _presenter.PreferredMinimumHeight = minHeight;
        _presenter.PreferredMaximumHeight = maxHeight;
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

    private static bool Overlaps(RECT r, RectInt32 area) =>
        r.Left < area.X + area.Width && r.Right > area.X && r.Top < area.Y + area.Height && r.Bottom > area.Y;

    public void HideFlyout()
    {
        if (!AppWindow.IsVisible) return;
        _lastHidden = DateTime.Now;
        AppWindow.Hide();
        _settings.Save();
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
        ClearAgenda();
        ClearDensity();
        TooltipChanged?.Invoke("Calendar");
        SetView(_calendar.HasClientSecret ? View.SignIn : View.Setup);
    }

    // ------------------------------------------------------------------ data

    /// <summary>
    /// Reloads the agenda and the month-grid dots. A refresh you ask for also re-fetches the
    /// calendar list, so colour or visibility changes in Google Calendar show up straight away.
    /// </summary>
    public async Task RefreshAllAsync(bool userRequested = false)
    {
        if (userRequested) _calendar.ForgetCalendars();
        ReloadDensity();
        await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (!_calendar.IsSignedIn || _refreshing) return;
        _refreshing = true;
        Progress.Visibility = Visibility.Visible;
        var mode = _mode;
        var day = mode == AgendaMode.Week ? DateTime.Today : _selectedDay;
        try
        {
            if (mode == AgendaMode.Day)
            {
                var events = await _calendar.GetDayAsync(day);
                if (_mode == mode && day == _selectedDay) ShowDay(day, events);
            }
            else
            {
                var events = await _calendar.GetRangeAsync(day, day.AddDays(WeekDays));
                if (_mode == mode) ShowWeek(day, events);
            }
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

        // The selected day or mode changed while we were loading: load the new one now.
        if (mode != _mode || (mode == AgendaMode.Day && day != _selectedDay)) await RefreshAsync();
        else if (!AppWindow.IsVisible) TrimMemory(); // background refresh: don't hold on to what it allocated
    }

    private void ShowDay(DateTime day, List<CalendarEventInfo> events)
    {
        var items = AgendaItem.ForDay(events, day);
        _items.Clear();
        foreach (var item in items) _items.Add(item);
        AgendaList.ItemsSource = _items;
        ShowLoaded(items.Count == 0, "No events");

        if (day == DateTime.Today) UpdateTooltip(items);
    }

    /// <summary>Upcoming events for the next 7 days, under a heading per day; empty days are skipped.</summary>
    private void ShowWeek(DateTime from, List<CalendarEventInfo> events)
    {
        var now = DateTimeOffset.Now;
        var groups = Enumerable.Range(0, WeekDays)
            .Select(i => from.AddDays(i))
            .Select(d => new AgendaGroup(DayLabel(d), AgendaItem.ForDay(events, d).Where(i => i.End is null || i.End > now)))
            .Where(g => g.Count > 0)
            .ToList();
        _weekSource.Source = groups;
        AgendaList.ItemsSource = _weekSource.View;
        ShowLoaded(groups.Count == 0, "Nothing in the next 7 days");

        UpdateTooltip(AgendaItem.ForDay(events, DateTime.Today));
    }

    private void ShowLoaded(bool empty, string emptyText)
    {
        EmptyText.Text = emptyText;
        EmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ErrorBar.IsOpen = false;
        _lastRefresh = DateTime.Now;
    }

    private void ClearAgenda()
    {
        _items.Clear();
        _weekSource.Source = null;
        EmptyText.Visibility = Visibility.Collapsed;
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
        AgendaHeader.Text = _mode == AgendaMode.Week ? "Next 7 days" : DayLabel(_selectedDay);
    }

    private static string DayLabel(DateTime day) => (day.Date - DateTime.Today).Days switch
    {
        0 => "Today",
        1 => "Tomorrow",
        -1 => "Yesterday",
        _ => day.ToString("dddd, MMMM d"),
    };

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
        if (_mode == AgendaMode.Week)
        {
            // Picking a date means "show me that day".
            _selectedDay = day;
            ModeBar.SelectedItem = DayModeItem; // ModeBar_SelectionChanged reloads
            return;
        }
        if (day == _selectedDay && _items.Count > 0) return;
        _selectedDay = day;
        ClearAgenda();
        UpdateDateHeader();
        await RefreshAsync();
    }

    private async void ModeBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var mode = sender.SelectedItem == WeekModeItem ? AgendaMode.Week : AgendaMode.Day;
        if (mode == _mode) return;
        _mode = mode;
        ClearAgenda();
        UpdateDateHeader();
        await RefreshAsync();
    }

    private void MonthView_DayItemChanging(CalendarView sender, CalendarViewDayItemChangingEventArgs args)
    {
        if (args.Item is { } item) ApplyDensity(item);
    }

    private void Today_Click(object sender, RoutedEventArgs e) => SelectDay(DateTime.Today);

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAllAsync(userRequested: true);

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
