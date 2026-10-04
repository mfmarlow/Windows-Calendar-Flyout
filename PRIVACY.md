# Privacy Policy

_Last updated: October 4, 2026_

CalendarFlyout is a personal, open-source Windows tray app that shows your Google Calendar
agenda. This page explains what it does with your data.

## What the app accesses

- **Your Google Calendar, read-only.** The app requests the
  `https://www.googleapis.com/auth/calendar.readonly` scope so it can list your calendars and
  their events. It can't create, change, or delete anything.

## Where your data goes

- **Only to Google.** The app talks directly to Google's sign-in and Calendar APIs from your
  computer. There is no server run by the developer, and no analytics, telemetry, or
  advertising.
- **Nothing is sold or shared** with anyone.

## What is stored on your computer

Everything stays in `%LOCALAPPDATA%\CalendarFlyout` on your own PC:

- Your Google sign-in token, so you don't have to sign in every time.
- `client_secret.json`, the OAuth client configuration you provide.
- `settings.json`, your preferences (currently just the flyout's height, if you've resized it).
- `error.log`, written only if the app crashes. It contains technical error details.

If you turn on **Start with Windows**, the app also adds an entry under
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run` so it launches at sign-in; turning it
off removes the entry.

Calendar events are held in memory while the app is running and aren't saved to disk.

## Removing your data

- In the app, right-click the tray icon and choose **Sign out**, which deletes the stored token.
- Delete the `%LOCALAPPDATA%\CalendarFlyout` folder to remove everything.
- Revoke the app's access at any time at <https://myaccount.google.com/permissions>.

## Google API Services User Data Policy

CalendarFlyout's use of information received from Google APIs adheres to the
[Google API Services User Data Policy](https://developers.google.com/terms/api-services-user-data-policy),
including the Limited Use requirements.

## Contact

Questions? Open an issue at <https://github.com/mfmarlow/Windows-Calendar-Flyout/issues>.
