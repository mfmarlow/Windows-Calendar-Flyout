# CalendarFlyout

A small WinUI 3 tray app for Windows 11. Click the calendar icon next to the clock and a native
acrylic flyout shows a month grid plus your Google Calendar agenda for the selected day, with a
**Join** button for Meet/Zoom links. Right-click the icon for Refresh, Start with Windows, Sign
out, and Exit.

Read-only, talks only to Google, stores its token in `%LOCALAPPDATA%\CalendarFlyout`.

---

## 1. Google Cloud setup (one time, ~5 minutes)

1. Go to <https://console.cloud.google.com/> → project picker → **New project** (e.g. "Calendar Flyout").
2. **APIs & Services → Library** → search **Google Calendar API** → **Enable**.
3. **Google Auth Platform** (formerly "OAuth consent screen") → **Get started**
   - App name: `Calendar Flyout`, support email: your Gmail
   - Audience: **External**
   - Contact email: your Gmail → Create
   - Publishing requires a homepage and privacy policy URL on the **Branding** page. This repo's
     GitHub Pages site works: <https://mfmarlow.github.io/Windows-Calendar-Flyout/> and
     <https://mfmarlow.github.io/Windows-Calendar-Flyout/PRIVACY>. Add `mfmarlow.github.io` under
     **Authorized domains**. Don't upload a logo (that triggers verification).
4. **Audience** page → **Publish app** (status becomes *In production*).
   - Why: in *Testing* mode Google expires refresh tokens after 7 days, forcing a weekly re-login.
   - You do **not** need to submit for verification for personal use. On first sign-in you'll see
     "Google hasn't verified this app" → **Advanced** → **Go to Calendar Flyout (unsafe)**. That
     warning is about *your own* app, so it's fine to continue.
   - (Alternative: stay in Testing, add yourself under **Test users**, and accept re-signing in weekly.)
5. **Clients → Create client** → Application type **Desktop app** → Create → **Download JSON**.
6. Rename the file to `client_secret.json` and put it in:

   ```
   %LOCALAPPDATA%\CalendarFlyout\client_secret.json
   ```

   (The app's setup screen shows this path and has an **Open folder** button.)

Treat `client_secret.json` like a password-lite: don't commit it to a public repo.

## 2. Build & run

You need the **.NET 10 SDK** (`winget install Microsoft.DotNet.SDK.10`). Visual Studio is optional;
if you use it, install the **WinUI application development** workload and open `CalendarFlyout.csproj`.

```powershell
git clone https://github.com/mfmarlow/Windows-Calendar-Flyout.git
cd Windows-Calendar-Flyout
dotnet run
```

To produce a folder you can keep somewhere permanent (needed for "Start with Windows", which
records the .exe path):

```powershell
dotnet publish -c Release -o "$env:LOCALAPPDATA\Programs\CalendarFlyout"
& "$env:LOCALAPPDATA\Programs\CalendarFlyout\CalendarFlyout.exe"
```

Then right-click the tray icon → **Start with Windows**. At login it starts silently (`--background`).

> **Tip:** Windows may tuck new tray icons into the overflow (`^`). Drag the icon onto the taskbar,
> or Settings → Personalization → Taskbar → Other system tray icons → turn CalendarFlyout on.

### Building from WSL (optional)

If you keep the repo in WSL, use `./win.sh` instead. It still needs the .NET 10 SDK installed on
Windows. Windows build tools can't handle `\\wsl.localhost` paths, so the script copies the source to
`%LOCALAPPDATA%\CalendarFlyout-build` and runs the Windows `dotnet.exe` there:

```bash
./win.sh build     # Debug build
./win.sh run       # build and launch (stops a running copy first)
./win.sh publish   # Release build to %LOCALAPPDATA%\Programs\CalendarFlyout
./win.sh stop      # close a running copy
```

## 3. Using it

| Action | Result |
|---|---|
| Click tray icon | Open / close the flyout (bottom-right, follows taskbar position and DPI) |
| Click a day | Load that day's events |
| Click the date header | Jump back to today |
| Click an event | Open it in Google Calendar |
| **Join** | Open the Meet/Zoom link |
| Esc / click elsewhere | Close |
| Hover tray icon | Shows your next event today |

Events refresh every 5 minutes (and when you open the flyout, if the data is over a minute old).
It shows every calendar that's checked in Google Calendar's sidebar, coloured by calendar, and hides
events you've declined.
Days with events get a small marker on the month grid, one per calendar (in that calendar's colour).

## Project layout

| File | What it does |
|---|---|
| `App.xaml.cs` | Startup, single-instance guard, tray menu |
| `TrayIcon.cs` | Notification-area icon via `Shell_NotifyIcon` (light/dark aware, survives Explorer restarts) |
| `FlyoutWindow.xaml(.cs)` | The popup: acrylic, borderless, positioned by the clock, hides on focus loss |
| `GoogleCalendarService.cs` | OAuth (loopback browser flow) + Calendar API queries |
| `AgendaItem.cs` | Turns an API event into a list row (times, colours, Join link) |
| `StartupManager.cs` | HKCU `Run` key for Start with Windows |
| `NativeMethods.cs` | Win32 P/Invoke |

## Troubleshooting

- **Crash on launch / nothing happens** → check `%LOCALAPPDATA%\CalendarFlyout\error.log`.
- **"Signed out — your sign-in expired"** → the OAuth app is still in *Testing*; publish it (step 1.4).
- **Want to change account** → right-click → Sign out, then sign in again.
- **Building on .NET 8 instead** → change `net10.0-…` to `net8.0-windows10.0.19041.0` in the `.csproj`.

## Ideas for later

- Show the day-of-month number on the tray icon itself.
- Toast reminders N minutes before meetings (`Microsoft.Windows.AppNotifications`).
- Create quick events (switch the scope to `CalendarService.Scope.CalendarEvents`).
