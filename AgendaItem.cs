using System.Diagnostics;
using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace CalendarFlyout;

/// <summary>A day heading and its events, for the grouped "Week" list.</summary>
public sealed class AgendaGroup(string header, IEnumerable<AgendaItem> items) : List<AgendaItem>(items)
{
    public string Header { get; } = header;
}

/// <summary>One row in the agenda list. Create on the UI thread (it holds a brush).</summary>
public sealed class AgendaItem
{
    public required string Title { get; init; }
    public required string Subtitle { get; init; }
    public required SolidColorBrush ColorBrush { get; init; }
    public bool IsAllDay { get; init; }
    public DateTimeOffset? Start { get; init; }
    public DateTimeOffset? End { get; init; }
    public string? MeetLink { get; init; }
    public string? HtmlLink { get; init; }

    public Visibility JoinVisibility =>
        MeetLink is not null && (End is null || End > DateTimeOffset.Now) ? Visibility.Visible : Visibility.Collapsed;

    public double ItemOpacity => End is { } end && end <= DateTimeOffset.Now ? 0.5 : 1.0;

    // Bound with {x:Bind Join} / used by ListView.ItemClick
    public void Join() => Launch(MeetLink);
    public void OpenInBrowser() => Launch(HtmlLink);

    private static void Launch(string? url)
    {
        if (!string.IsNullOrEmpty(url))
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    public static AgendaItem FromEvent(CalendarEventInfo info, DateTime day)
    {
        var e = info.Event;
        var (allDay, start, end) = GetTimes(e);

        string meet = e.HangoutLink
            ?? e.ConferenceData?.EntryPoints?.FirstOrDefault(p => p.EntryPointType == "video")?.Uri
            ?? "";

        return new AgendaItem
        {
            Title = string.IsNullOrWhiteSpace(e.Summary) ? "(No title)" : e.Summary,
            Subtitle = BuildSubtitle(allDay, start, end, day, e.Location),
            ColorBrush = new SolidColorBrush(ParseColor(info.CalendarColor)),
            IsAllDay = allDay,
            Start = start,
            End = end,
            MeetLink = meet.Length > 0 ? meet : null,
            HtmlLink = e.HtmlLink,
        };
    }

    /// <summary>The events that overlap a local day, as rows: all-day first, then by start time.</summary>
    public static List<AgendaItem> ForDay(IEnumerable<CalendarEventInfo> events, DateTime day)
    {
        var next = day.Date.AddDays(1);
        return events
            .Where(e =>
            {
                var (_, start, end) = GetTimes(e.Event);
                return start is { } s && s < next && (end ?? s) > day.Date;
            })
            .Select(e => FromEvent(e, day))
            .OrderByDescending(i => i.IsAllDay)
            .ThenBy(i => i.Start)
            .ToList();
    }

    /// <summary>Local start/end of an event. For all-day events the end date is exclusive.</summary>
    internal static (bool AllDay, DateTimeOffset? Start, DateTimeOffset? End) GetTimes(Google.Apis.Calendar.v3.Data.Event e)
    {
        bool allDay = e.Start?.DateTimeDateTimeOffset is null;
        DateTimeOffset? start = e.Start?.DateTimeDateTimeOffset?.ToLocalTime();
        DateTimeOffset? end = e.End?.DateTimeDateTimeOffset?.ToLocalTime();

        if (allDay)
        {
            // All-day events carry plain dates; the end date is exclusive.
            if (DateTime.TryParse(e.Start?.Date, CultureInfo.InvariantCulture, out var s)) start = new DateTimeOffset(s);
            if (DateTime.TryParse(e.End?.Date, CultureInfo.InvariantCulture, out var en)) end = new DateTimeOffset(en);
        }
        return (allDay, start, end);
    }

    private static string BuildSubtitle(bool allDay, DateTimeOffset? start, DateTimeOffset? end, DateTime day, string? location)
    {
        string when;
        if (allDay || start is null || end is null)
        {
            when = "All day";
        }
        else
        {
            bool startsEarlier = start.Value.Date < day.Date;
            bool endsLater = end.Value.Date > day.Date && end.Value.TimeOfDay > TimeSpan.Zero
                             || end.Value.Date > day.Date.AddDays(1);
            when = (startsEarlier, endsLater) switch
            {
                (true, true) => "All day",
                (true, false) => $"Until {end.Value:t}",
                (false, true) => $"From {start.Value:t}",
                _ => $"{start.Value:t} – {end.Value:t}",
            };
        }

        if (!string.IsNullOrWhiteSpace(location))
        {
            // Long addresses: keep the first line only.
            var first = location.Split(',', '\n')[0].Trim();
            when += " · " + first;
        }
        return when;
    }

    internal static Windows.UI.Color ParseColor(string? hex)
    {
        if (hex is { Length: 7 } && hex[0] == '#' &&
            int.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
        {
            return ColorHelper.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        }
        return Colors.SteelBlue;
    }
}
