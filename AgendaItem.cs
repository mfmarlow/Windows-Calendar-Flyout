using System.Diagnostics;
using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace CalendarFlyout;

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
        bool allDay = e.Start?.DateTimeDateTimeOffset is null;
        DateTimeOffset? start = e.Start?.DateTimeDateTimeOffset?.ToLocalTime();
        DateTimeOffset? end = e.End?.DateTimeDateTimeOffset?.ToLocalTime();

        if (allDay)
        {
            // All-day events carry plain dates; the end date is exclusive.
            if (DateTime.TryParse(e.Start?.Date, CultureInfo.InvariantCulture, out var s)) start = new DateTimeOffset(s);
            if (DateTime.TryParse(e.End?.Date, CultureInfo.InvariantCulture, out var en)) end = new DateTimeOffset(en);
        }

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

    private static Windows.UI.Color ParseColor(string? hex)
    {
        if (hex is { Length: 7 } && hex[0] == '#' &&
            int.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
        {
            return ColorHelper.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        }
        return Colors.SteelBlue;
    }
}
