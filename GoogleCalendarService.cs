using Google.Apis.Auth.OAuth2;
using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;
using Google.Apis.Services;
using Google.Apis.Util.Store;

namespace CalendarFlyout;

public sealed record CalendarEventInfo(Event Event, string? CalendarColor);

/// <summary>Read-only access to every calendar you have ticked in Google Calendar's sidebar.</summary>
public sealed class GoogleCalendarService
{
    private static readonly string[] Scopes = [CalendarService.Scope.CalendarReadonly];

    public static string ClientSecretPath => Path.Combine(App.DataDir, "client_secret.json");
    private static string TokenDir => Path.Combine(App.DataDir, "token");

    private UserCredential? _credential;
    private CalendarService? _service;
    private IList<CalendarListEntry>? _calendars;
    private DateTime _calendarsFetchedAt;

    public bool HasClientSecret => File.Exists(ClientSecretPath);
    public bool HasSavedToken => Directory.Exists(TokenDir) && Directory.EnumerateFiles(TokenDir).Any();
    public bool IsSignedIn => _service is not null;

    /// <summary>
    /// Uses the saved refresh token when there is one; otherwise opens your browser for consent
    /// (a loopback redirect on 127.0.0.1 catches the result).
    /// </summary>
    public async Task SignInAsync(CancellationToken ct)
    {
        GoogleClientSecrets secrets;
        await using (var stream = File.OpenRead(ClientSecretPath))
            secrets = await GoogleClientSecrets.FromStreamAsync(stream, ct);

        _credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            secrets.Secrets, Scopes, "user", ct, new FileDataStore(TokenDir, fullPath: true));

        _service = new CalendarService(new BaseClientService.Initializer
        {
            HttpClientInitializer = _credential,
            ApplicationName = "CalendarFlyout",
        });
        _calendars = null;
    }

    public async Task SignOutAsync()
    {
        try { if (_credential is not null) await _credential.RevokeTokenAsync(CancellationToken.None); }
        catch { /* token may already be invalid; deleting it locally is what matters */ }

        if (Directory.Exists(TokenDir)) Directory.Delete(TokenDir, recursive: true);
        _credential = null;
        _service = null;
        _calendars = null;
    }

    /// <summary>Re-fetch the calendar list (names, colours, which are checked) on the next query.</summary>
    public void ForgetCalendars() => _calendars = null;

    /// <summary>All events overlapping the given local day, across your visible calendars.</summary>
    public Task<List<CalendarEventInfo>> GetDayAsync(DateTime day, CancellationToken ct = default) =>
        GetRangeAsync(day.Date, day.Date.AddDays(1), ct);

    /// <summary>All events overlapping [from, to) in local time, across your visible calendars.</summary>
    public async Task<List<CalendarEventInfo>> GetRangeAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        if (_service is null) throw new InvalidOperationException("Not signed in.");

        var calendars = await GetCalendarsAsync(ct);
        var start = new DateTimeOffset(from, TimeZoneInfo.Local.GetUtcOffset(from));
        var end = new DateTimeOffset(to, TimeZoneInfo.Local.GetUtcOffset(to));

        var perCalendar = await Task.WhenAll(calendars.Select(async cal =>
        {
            var events = new List<CalendarEventInfo>();
            string? pageToken = null;
            do
            {
                var request = _service.Events.List(cal.Id);
                request.TimeMinDateTimeOffset = start;
                request.TimeMaxDateTimeOffset = end;
                request.SingleEvents = true; // expand recurring events into instances
                request.OrderBy = EventsResource.ListRequest.OrderByEnum.StartTime;
                request.MaxResults = 250;
                request.PageToken = pageToken;
                var result = await request.ExecuteAsync(ct);
                events.AddRange((result.Items ?? []).Select(e => new CalendarEventInfo(e, cal.BackgroundColor)));
                pageToken = result.NextPageToken;
            } while (pageToken is not null);
            return events;
        }));

        return perCalendar.SelectMany(x => x)
            .Where(x => x.Event.Status != "cancelled" && !IDeclined(x.Event))
            .ToList();
    }

    private async Task<IList<CalendarListEntry>> GetCalendarsAsync(CancellationToken ct)
    {
        if (_calendars is null || DateTime.Now - _calendarsFetchedAt > TimeSpan.FromMinutes(30))
        {
            var list = await _service!.CalendarList.List().ExecuteAsync(ct);
            _calendars = (list.Items ?? [])
                .Where(c => c.Selected == true && c.Hidden != true)
                .ToList();
            _calendarsFetchedAt = DateTime.Now;
        }
        return _calendars;
    }

    private static bool IDeclined(Event e) =>
        e.Attendees?.Any(a => a.Self == true && a.ResponseStatus == "declined") == true;
}
