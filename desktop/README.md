# Usque Desktop for Windows

A native .NET 8 / WPF front end for the included usque MASQUE engine. Windows
10/11, x64 or ARM64. **Administrator privileges are required.** This is an
independent, unofficial client, not a Cloudflare product.

## Build and run

Install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) and the
Go version required by the root `go.mod`, then run from the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File .\desktop\build.ps1 -DownloadWintun
.\dist\desktop\Usque.Desktop.exe
```

The output is self-contained (no .NET installation needed on the destination
computer). Keep the **entire** `dist/desktop` directory together. Accept the UAC
prompt. The script downloads official Wintun 0.14.1, verifies its published
SHA-256, and includes its distribution license. It does **not** install the
driver or change networking during the build.

## Installer

`desktop/build.ps1` produces a `dist/` folder for the version; the included
Inno Setup script wraps it into a single setup program:

```powershell
powershell -ExecutionPolicy Bypass -File .\desktop\build.ps1 -OutputDirectory dist\desktop-1.3.2
# then compile the setup program (Inno Setup 6 required):
& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" .\desktop\installer.iss /DPackageDir=..\dist\desktop-1.3.2
```

The output is `dist\UsqueDesktop-Setup-1.3.2.exe`. It installs under Program
Files, registers Add/Remove Programs, creates a Start Menu shortcut and an
optionally-selected Desktop shortcut (the U icon), and offers English/Turkish
wizard pages. It never installs a Windows service or a startup task, and does
not create or copy WARP profiles — those are created by the app on first use.

**Before uninstalling** with an active connection, use the app's
**Restore internet access** (or `--restore-network`) first. The kill-switch WFP
filters and profiles live in `%ProgramData%\UsqueDesktop` and are intentionally
left in place by the uninstaller; deleting them by hand while locked down can
leave the machine with no application installed to recover from a filter.

### GitHub Actions

The workflow in [`.github/workflows/desktop.yml`](../.github/workflows/desktop.yml)
runs the full test suite, builds the package, downloads verified Wintun, and
assembles the Inno Setup installer on a `windows-latest` runner so you can attach
`UsqueDesktop-Setup-1.3.2.exe` to a GitHub Release.

Options: `-Runtime win-arm64`, `-FrameworkDependent` (requires the .NET 8 Windows
Desktop Runtime), or `-WintunDll C:\path\wintun.dll` instead of downloading.
For a supplied DLL, you are responsible for its architecture, trust, and license.
The WPF app itself has no third-party NuGet dependencies.

The builder rejects Wintun DLLs with the wrong architecture, including a stale
DLL left over after switching build runtimes. The app also checks the engine and
driver PE headers before arming protection, rather than failing after lockdown.
Quit with **Exit Usque** before overwriting a running distribution. The builder
checks locked/read-only output files and never stops a VPN automatically. To
prepare a separate package while the app is running, use for example
`-OutputDirectory dist\desktop-1.1.1`.

## Connect

1. Press the large power button.
2. If no profile exists, accept the first-run Cloudflare terms notice. The app
   registers and enrolls the device automatically; there is no terminal setup.
3. The app arms protection, creates the native tunnel, pins the remote endpoint
   to the original adapter, and sets IPv4/IPv6 split-default routes and DNS.
4. Later connections reuse the saved profile. **Import profile** accepts an
   existing usque `config.json`. Invalid files are never silently overwritten.

**Kill switch** is on by default. Preferences are saved locally and selected
before connecting. **Automatic** transport tries QUIC first, then switches to
TCP/HTTP/2 after a failed or timed-out attempt. The old engine is stopped before
its replacement starts; kill-switch blocks are never disarmed during fallback.
QUIC-only and TCP-only modes are also available. With automatic recovery enabled,
TCP failures retry TCP rather than endlessly switching protocols. Protection
remains armed during the wait. Disable automatic recovery to stop after failure.

After the handshake, the app verifies Cloudflare's `warp=on`/`warp=plus` trace
through a socket bound to the tunnel. It also checks hostname resolution and
IPv6 connectivity, bypassing HTTP proxy settings. Checks run every 30 seconds
and on demand. **Connected / WARP verified** requires WARP and DNS success;
IPv6 availability is shown separately. Later failures produce a health warning.
These reachability checks are **not packet-capture leak tests**, proof that all
DNS packets use WARP, or an anonymity guarantee.

## Desktop features

- **Languages:** complete English, Chinese (Simplified), Hindi, Spanish, French,
  Arabic, Bengali, Russian, Portuguese, Urdu and Turkish UI dictionaries.
  Language changes apply immediately without changing connection selections.
  Arabic and Urdu use right-to-left layouts; addresses, counters and technical
  logs retain left-to-right direction. Tests check key and placeholder coverage
  for every language and render off-screen localized settings snapshots.

- **Network recovery:** enabled by default in settings, independently of
  launch-time auto-connect. Physical adapter/address/gateway changes are
  debounced; sleep stops the old engine and resume rebuilds the connection.
  Failed attempts back off from 2 to 30 seconds with a visible countdown.
  Disconnect cancels pending retries; offline network events never start a VPN.
  Old engines and probes finish before route replacement, and filters are never
  disarmed by automatic recovery. Cleanup failures stop in a protected error state.
- **Connection panel:** active QUIC/TCP transport, HTTPS probe duration (not ICMP
  ping), last successful health-check time and observed reconnection count.
  A successful numeric-IP probe with failed hostname access reports a specific
  DNS/hostname warning rather than a generic connection failure.
- **One-click diagnostics:** available from Overview and Settings, including while
  disconnected. Separately checks runtime-file/driver architecture, local adapter
  availability, active MASQUE handshake, internet, DNS/hostname, WARP and IPv6,
  with an outcome and suggested next action for each. Driver checks validate
  files, not live driver loading; a gateway alone is not internet verification.
  Offline online checks are skipped, not bypassed over the physical connection.
  Diagnostics never install drivers, release protection or change networking.
  Reports include these results, the traffic mode and reconnection count.
- **Window behavior:** hiding to the notification area is off by default.
  Minimizing or closing the window minimizes it to the taskbar without ending
  the connection. Use **Exit Usque** to quit. If you explicitly enable
  **Minimize and close to notification area**, both actions hide the window
  instead. Double-click the notification-area icon to reopen. Its menu
  offers connect/disconnect, recovery and **Exit**. An explicit **Exit Usque**
  button is also in the sidebar. Existing saved preferences are preserved.
  Hover the icon (including inside Windows' hidden-icons overflow) to see current
  status plus **↓ download / ↑ upload** rates. Rates update every second and are
  actual current traffic, not an active speed test or maximum line speed.
  After connecting, a notification uses the first fresh statistics sample.
  Disconnection/tunnel interruption is also announced once; health warnings do
  not produce false disconnect/reconnect messages. Missing counters and proxy
  mode show **—** with an explanation instead of invented speeds. Clicking a
  notification opens Usque. Windows notification/Do Not Disturb settings can
  suppress balloons; the live hover tooltip is independent of those settings.
- **Saved preferences:** kill switch, transport, tray behavior, selected profile,
  startup choices and terms consent survive restart. Invalid settings are not
  silently overwritten or reset to less-safe values.
- **Live statistics:** actual tunnel-adapter download/upload rates, session totals
  and elapsed time. Totals survive adapter replacement during fallback and are
  reset for a new session. They include health-check traffic, exclude outer VPN
  transport overhead, and are not persisted to disk.
- **Profiles:** create, select, rename, import into and delete named profiles under
  **Settings & profiles**. Empty profiles register on first connect. Changes are
  blocked while connected, during recovery, or while protection remains active.
  The original `config.json` is preserved as the Default profile; additional
  profiles have generated IDs and separate credential files. Imported source
  files are never modified. At least one profile is retained.
- **Diagnostics:** the log and exported JSON report redact known profile secrets,
  token fields, bearer/JWT tokens, private-key blocks, identifiers, IP addresses
  and Windows user paths. Reports contain whitelisted runtime/health/traffic
  metadata and the latest 200 sanitized events, never profile or journal files.
  Always review reports before sharing; no redactor can identify every possible
  sensitive value in an arbitrary error message.

## Local proxy modes

Choose **Traffic mode → SOCKS5 proxy / HTTP proxy** before connecting. The default
local port is `1080`; Settings accepts any port from 1 to 65535. **Copy proxy
address** copies `socks5://127.0.0.1:1080` or `http://127.0.0.1:1080`.
Invalid port drafts show an inline error immediately, are not saved, and disable
connect/copy until corrected. They never cause a connection using a stale port.
Configure each consuming application manually; the app never changes the Windows
system proxy. For example, after connecting:

```powershell
curl.exe --proxy socks5h://127.0.0.1:1080 https://www.cloudflare.com/cdn-cgi/trace
# HTTP mode:
curl.exe --proxy http://127.0.0.1:1080 https://www.cloudflare.com/cdn-cgi/trace
```

SOCKS clients should resolve names remotely (for curl, `socks5h`, not `socks5`).
The backend defaults to DNS through its userspace tunnel; desktop does not enable
the engine's physical/local DNS options. Health probes explicitly use the chosen
local proxy and never fall back to a direct connection. Readiness requires both
the local listener and the MASQUE handshake; a successful proxy health check is
still not a guarantee about applications that ignore their proxy configuration.

**Proxy mode protects only configured applications, not the full device.** It
does not create an OS tunnel or change routes, DNS or WFP policy. The full-device
kill switch is unavailable in this mode; the user's tunnel kill-switch preference
is preserved. Existing filters or a stale network journal must be explicitly
recovered before starting a proxy, never silently removed. Listeners are bound to
IPv4 loopback only, not exposed to the LAN. Wintun is not needed for proxy mode,
although the desktop app still uses its existing administrator-only launch path.

Transport fallback, health checks and automatic recovery also work for proxies.
Adapter traffic rates/totals show **—** because userspace proxy traffic cannot be
measured using the native tunnel adapter's counters.

## Optional Windows startup

Both startup options default to **off**. Enable them explicitly in settings:

1. **Auto-connect when Usque opens** uses the selected profile on launch. Enabling
   this requires Cloudflare terms consent, since an empty selected profile may
   need registration. Consent does not silently lower an existing kill switch.
2. **Launch at Windows sign-in** asks permission to copy the runtime into a
   versioned, ACL-protected directory under `%ProgramFiles%\UsqueDesktop-57d78a76`
   and register a Task Scheduler task for the current user, using an interactive
   logon token and highest available privileges. Only runtime files are copied,
   not profiles, settings or journals. No password is stored. The task launches
    the protected executable with `--startup`, minimized to the taskbar by default
    or hidden when the notification-area preference is enabled; connection
   is controlled by option 1. An elevated task never points into a user-writable
   Desktop or Downloads directory.

No service or startup task is installed merely by launching or building the app.
Disabling sign-in startup removes the application's current-user task; protected
runtime copies are retained (they may be in use). Disable it before uninstalling.
To update the startup copy after a new build, turn the option off and on again
from the new distribution. Moving the portable folder does not break a task
already pointing at its protected copy.
Startup copies now use complete file manifests: every runtime file contributes
to package identity, staged copies are verified, and missing, modified or
unexpected cached runtime files prevent reuse. To update a legacy startup copy
without a manifest, enable startup from a fresh published distribution.
This is a sign-in feature, not a pre-login VPN or Windows service.

Registration is a direct HTTPS bootstrap request, made **before** arming the kill
switch. It requires normal internet access. An existing crash-protection lock
will never be silently lowered to register a missing profile.

## Kill switch and recovery — read this first

This feature deliberately affects **all users and applications on this machine**.
Test in a disposable VM before relying on it. Do not first try it over Remote
Desktop, SSH, or another connection you cannot afford to lose.

- A dedicated WFP provider/sublayer installs persistent, transactional filters
  at outbound ALE authorization and outbound transport layers for IPv4 and IPv6.
- The default policy blocks outbound traffic. Exceptions are loopback, the
  actual tunnel interface LUID, the engine's exact IPv4 WARP endpoint on port
  443 using the selected transport, and DHCP/IPv6 neighbor-discovery control
  traffic. The endpoint exception is application-restricted at ALE; transport
  filters also prevent existing flows from falling back to physical adapters.
- There is no physical DNS or general LAN exception. DNS uses a session-owned
  NRPT rule pointing to `1.1.1.1`/`1.0.0.1` through WARP. Existing default routes
  and physical-adapter DNS settings are not rewritten. More-specific existing
  routes may be blocked rather than usable; this is not split tunneling.
- Filters remain after sudden engine/UI termination. A Windows job object kills
  the child engine if its parent exits. Filters are persistent across restarts
  once Windows Base Filtering Engine loads them; this is **not an early-boot
  firewall** or a substitute for Windows Firewall. Other administrators, kernel
  drivers, security products, or enterprise policy can interfere with protection.
- An unexpected failure never intentionally removes protection. Normal
  disconnect restores owned routes/DNS **before** removing the filters. Failed
  cleanup leaves the lock in place and can be retried.

If connectivity is blocked, reopen the app and either reconnect or press
**Restore internet access**. To recover without the main UI, run elevated:

```powershell
.\dist\desktop\Usque.Desktop.exe --restore-network
```

This intentionally removes this application's filters and session-owned network
changes, returning to an **unprotected** connection. Do not delete the app or
its state directory while locked down. Recovery does not need Cloudflare or a
working Wintun driver. On exit, the app asks whether to restore networking or
keep existing protection; it never silently restores internet after a crash.

## State and privacy

Profiles, `settings.json` and the recovery journal live in `%ProgramData%\UsqueDesktop`. That
directory is restricted to Administrators and SYSTEM. The profile contains
private keys and API credentials in upstream's JSON format (ACL-protected, **not
encrypted at rest**). Only one machine-wide instance is allowed. Logs are
bounded to the latest 200 events in memory; review them before sharing.

There is no telemetry, automatic profile overwrite, or system-proxy modification.
An optional current-user startup task is created only after explicit opt-in.
Use **Exit Usque** to quit; the usual close button minimizes to the taskbar,
or hides to the notification area when that preference is enabled. Exit still asks whether to restore networking
or keep existing protection. `--restore-network` ignores preferences and profile
selection so recovery remains available even with corrupt settings.

## Development and tests

Open `desktop/Usque.Desktop.sln` in Visual Studio, or build all three desktop
projects from the repository root. Common framework, nullable-reference and
warnings-as-errors settings live in `desktop/Directory.Build.props`.

```powershell
go test ./...
dotnet build desktop/Usque.Desktop.sln -c Release
dotnet run --project desktop/Usque.Tests -c Release
powershell -NoProfile -ExecutionPolicy Bypass -File desktop/tests/Network.Tests.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File desktop/tests/Build.Tests.ps1
# Safe UI-only preview; no elevation, registration, routes, or firewall changes:
dotnet desktop/Usque.Desktop/bin/Release/net8.0-windows/Usque.Desktop.dll --preview
```

The dependency-free test executable runs the entire suite by default (including
when invoked by `build.ps1`). To inspect or run a smaller selection:

```powershell
dotnet run --project desktop/Usque.Tests -c Release -- --list
dotnet run --project desktop/Usque.Tests -c Release -- --filter "health"
dotnet run --project desktop/Usque.Tests -c Release -- --list --filter "startup"
```

Filters match test-name substrings case-insensitively. Listing and `--help` never
execute test delegates. Exit codes are **0** for success, **1** for test failures,
and **2** for invalid arguments or no matching tests, so misspelled filters cannot
silently pass CI. Each asynchronous test has a ten-second deadline; if it expires,
the runner stops rather than overlapping unfinished work with subsequent tests.
Ordinary assertion failures are reported and the remaining tests still run.

Tests use injected fakes for registration, cancellation, reconnection, crash
handling, cleanup failure, firewall policy, native structure layout, protected
QUIC-to-TCP fallback, health gating, preference persistence, profile isolation,
redaction, statistics and startup rollback. No real startup task is registered. They
also render all three UI pages off-screen in a startup-suppressed preview host;
the tests do not show windows or start the production application. These tests
do not prove real packet-level leak prevention. Before production use, perform
the elevated [VM acceptance checklist](TESTING.md), including live packet
capture, adapter changes, sleep/wake, DNS/IPv6, and forced process termination.

Structure:
- `Usque.Desktop`: WPF UI, terms consent, diagnostics, recovery entry point.
  `MainViewModel.cs` owns connection presentation/lifetime;
  `MainViewModel.Settings.cs` owns settings/profile bindings and input validation;
  `MainViewModel.Diagnostics.cs` owns reports, checks and logs.
  `DesktopInteraction` isolates dialogs, clipboard and browser effects.
- `Usque.Core`: session state machine, engine lifecycle, WFP policy, networking.
- `Usque.Desktop/Scripts/Network.ps1`: narrowly scoped, journaled route/DNS changes.
- `Usque.Tests`: dependency-free, non-mutating test executable.
  `Program.cs` composes suites, `TestRunner.cs` handles selection and execution,
  and `TestDoubles.cs` holds shared in-memory engine/network/firewall fakes.
  `UiTestHost` shares a startup-suppressed STA dispatcher across independently
  selectable `UiRegressionTests`; tests never show windows or use a real tray.

The upstream engine adds `nativetun --status-json`: newline-delimited lifecycle
events on stdout (`ready`, `connecting`, `connected`, `disconnected`), separate
from stderr logs. The Go tunnel uses one lifetime device reader across reconnects,
retains the first triggering packet, and responds to cancellation while idle.
Library callers still own the device: close it after cancellation to release its
underlying blocking read. Existing CLI options and certificate pinning are unchanged.
