# Unreleased — Complete desktop localization

- Fix **Exit → No (keep protection)** never closing the window, and the same
  failure when restoring networking had nothing left to wait for. WPF rejects
  `Close()` while its Closing event is still running.
- Fix a crash when the notification-area **Open** item or icon was used while
  the exit confirmation was showing.
- Fix repeated tray warning balloons on every automatic-retry cycle. A recovery
  episode is announced once; terminal failures and new episodes still notify.
- Fix redaction mangling the app's own translated status, health and help text
  (e.g. a profile named "WARP" displayed "[REDACTED] verified"). Backend
  messages, format arguments and logs are still redacted.
- Launching Usque again brings the running window forward (also from the
  notification area) instead of reporting that it is already running. This only
  applies within the same Windows session; recovery launches are unchanged.
- **Minimize and close to notification area** can be changed while connected.
- The log view follows new entries, but keeps a reader's place when scrolled up
  (previously every new line jumped back to the top).
- The busy spinner animates only while busy, so the app no longer keeps WPF's
  render loop running while idle. State brushes are cached and frozen.
- Hide empty diagnostic advice lines; use invariant digits in report file names.
- Add 12 regression tests (7 bug fixes, 5 improvements).
- Installer build: new `desktop/build-installer.ps1` runs tests, recreates a clean
  versioned package, verifies it, finds Inno Setup, compiles, and writes a SHA-256
  file. The version comes from the project file. `installer.iss` now refuses
  incomplete packages, takes its version from the packaged executable, supports
  ARM64, never force-closes a running app (`CloseApplications=no`), removes stale
  runtime DLLs on upgrade, and adds setup version metadata and logging. CI uses
  the same script. Adds 4 packaging-script tests.

- Add a prominent, localized connection-help panel for failed connections and
  health warnings, with cause-specific advice from structured health/diagnostic
  results, safe retry/recheck actions, log access and expandable redacted details.
  Keep restoration explicit, explain its protection implications, and distinguish
  unknown kill-switch status and proxy-only coverage. Add 10 recovery UI tests.

- Keep protected startup package installation and Task Scheduler access off the
  UI thread; display the actual task state on load and window activation. Preserve
  rollback on settings failures and prevent exit during an in-flight install.
- Relocalize existing built-in connection and health messages without another
  network check. Retain backend error context, historical logs and redaction.
- Show unavailable dashboard traffic rates as **—**, dim the disabled power
  button, and validate profile names inline without discarding drafts on failure.
- Handle per-monitor DPI, monitor/work-area changes and window moves; constrain
  the window to its current display and restore normal minimums on larger screens.
- Add 14 UI regression tests covering these fixes, including dispatcher
  responsiveness, startup rollback and localization/redaction.

- Disable notification-area hiding by default. Minimize and ordinary close keep
  the application minimized in the taskbar without disconnecting; explicit Exit
  still runs the existing exit/cleanup flow. Preserve saved tray preferences and
  respect them during sign-in startup.
- Enlarge exit labels from 10 to 40 and add a second exit button below the logo.
  Keep the sidebar scrollable on smaller displays.

- Complete the English-based dictionaries for Chinese, Hindi, Spanish, French,
  Arabic, Bengali, Russian, Portuguese, Urdu and Turkish.
- Review Turkish wording and translate startup/recovery notices and file filters.
- Apply right-to-left layouts for Arabic and Urdu while retaining left-to-right
  technical addresses, counters and logs. Wrap navigation labels.
- Verify every language's keys, format placeholders and file filters; render
  settings snapshots for all 11 languages. All 138 desktop tests pass.
- Keep the Go engine unchanged. The separate `dist/desktop-localized` build is
  not the previously published 1.3.2 installer.

# 1.3.2 — Connection notifications and live tray speeds

- Notify after a successful connection using the first fresh download/upload
  traffic sample. Notify once on normal disconnection or immediately on tunnel
  interruption; retain reconnecting and kill-switch context.
- Show connection state and both live traffic rates when hovering the Windows
  notification-area icon, including in the hidden-icons overflow. Update once
  per statistics tick and refresh on hover, without per-tick balloon spam.
- Explicitly show disconnected/transitional states without stale rates. Proxy
  and unavailable adapter counters display **—**, never fabricated speeds.
  Rates describe current traffic, not a speed test or maximum internet speed.
- Keep health warnings distinct from connection loss, cancel pending connected
  notices on disconnect, and open the main window when a notification is clicked.
- Bound tooltip/notification text to Windows limits without splitting Unicode.
  New text is translated into English and Turkish. Add 14 non-interacting tests.

The Go engine is unchanged. Windows notification settings/Do Not Disturb may
suppress notifications; the tray tooltip remains available.

# 1.3.1 — Desktop UI fixes and maintainability

- Correct initial state when attaching the UI to an existing session, display
  existing crash protection independently of the saved preference, and redact
  health text as well as connection/error/log text.
- Disable duplicate restore actions during disconnect. Gate tray actions during
  exit confirmation/cleanup and restore usability after cancellation or failure.
  Dispose the view-model idempotently and ignore queued updates after disposal.
- Validate proxy-port text inline before focus changes or connect/copy commands;
  never persist invalid drafts or silently use a previous valid port.
- Discard diagnostic results after session/profile changes or disposal. Leave
  disconnect available during online diagnostics; unchanged settings no longer
  erase existing diagnostic results.
- Keep localized selector identities stable, save language before refreshing
  labels, and cache a private preference snapshot instead of repeatedly copying
  profile catalogues during binding evaluation.
- Fit window minimum dimensions to small/high-DPI working areas, fix navigation
  alignment and profile-note overlap, wrap action rows, and localize accessible
  control names. Avoid redundant per-second native tray updates.
- Preserve elevation arguments correctly, including dotnet-hosted DLL launches.
- Split settings and diagnostics into focused view-model partials. Isolate OS
  dialogs, clipboard and browser effects behind an injectable UI interface.
  Move network-observer ownership to application lifetime (behavior unchanged).
- Add 23 independently selectable UI regressions using a shared off-screen STA
  test host, plus normal/small-screen, validation and Turkish rendering checks.

The Go engine and networking logic are unchanged in this release.

# 1.3.0 — Connection recovery, proxy modes and diagnostics

- Recover from physical network changes and sleep/wake without disarming existing
  kill-switch protection. Debounce adapter changes, stop/join old engines before
  rebuilding routes, and retry failures with a cancellable 2–30 second backoff.
  Recovery is enabled by default and can be disabled in settings. TCP retries
  stay on TCP after automatic fallback; cleanup failures still stop fail-closed.
- Show HTTPS-probe latency, last successful health check, reconnection count and
  the next-attempt countdown alongside the active transport.
- Add read-only one-click diagnostics with separate runtime/driver, local network,
  endpoint handshake, internet, DNS/hostname, WARP and IPv6 results and repair
  advice. Offline online-checks are explicitly skipped, never sent directly.
  Diagnostic export schema 2 includes mode, recovery count and individual checks.
- Add loopback-only SOCKS5 and HTTP proxy modes, configurable port and copyable
  address. They never alter machine routes, DNS, system proxy or firewall policy;
  existing network protection must be explicitly recovered before using them.
  Health checks go through the selected proxy with no direct fallback. Clearly
  label app-only coverage and unavailable adapter traffic statistics.
- Add structured lifecycle events to both Go proxy commands. Readiness is
  published only after local listeners bind successfully.
- Translate new controls, statuses and diagnostic advice into English and Turkish.

## Desktop bug fixes

- Centralize common .NET project settings and add a three-project Visual Studio
  solution for consistent command-line and IDE builds.
- Separate test suites, shared fakes and execution logic. Add test listing,
  case-insensitive filtering, explicit CLI exit codes and timeout isolation,
  with tests for the runner itself.
- Run desktop CI for changes to all included engine source directories, and
  bound the packaging job's execution time.

- Keep engine-event handling and connection deadlines responsive while initial
  or periodic health checks are pending. Cancel and join old probes before
  transport fallback, and require fresh verification after tunnel loss.
- Discard manual health checks queued before a tunnel-loss event.
- Apply UI-thread status updates immediately so starting a connection cannot
  briefly leave profile editing enabled.
- Reject shorthand IPv4 and scoped/bracketed IPv6 tunnel addresses before
  configuring protection or starting the engine.

# 1.2.1 — Elevation fix

- Fixes Windows error 740 (CreateProcess: the requested operation requires
  elevation) when launching the installed app from a shortcut, terminal or other
  launcher. The executable is now `asInvoker` and relaunches itself elevated with
  ShellExecute's `runas` verb when needed, so any launcher works and shows the UAC
  prompt instead of failing. `--preview` remains unelevated by design.

# 1.2.0 — Localization and release installer

- Language selection in Settings with the world's ten most spoken languages plus
  **Türkçe**. English and Turkish are fully translated; selecting any of the other
  nine currently falls back to English until translations land. The preference
  is saved and remembered after restart.
- All UI labels, status text, tray menu, dialogs and health messages are now
  sourced from a single localization service in `Usque.Core`, so adding a
  language is one enum value plus a translation dictionary.
- Inno Setup installer (`dist/UsqueDesktop-Setup-1.2.0.exe`) installs to Program
  Files, creates Start Menu and optional Desktop shortcuts with the U icon,
  registers Add/Remove Programs, and includes English + Turkish wizard pages.
- The build script now embeds `UsqueDesktop.ico`/`.png` in the published package
  so installed shortcuts keep the U icon.

# 1.1.1 — Reliability and security fixes

- Reject profile IDs that alias the same Windows filename through casing.
- Isolate preference snapshots from callers' mutable profile arrays.
- Roll back catalogue and credentials together if profile removal fails.
- Fix masking order and JSON-escaped secrets/Windows user paths in diagnostics.
- Clear verified health after disconnect or engine failure; reject ambiguous traces.
- Keep disconnect/recovery available while a manual health check is pending.
- Restore the actual startup-task enabled state after a failed settings save.
- Verify complete startup packages, including framework DLLs, cached payloads and
  files changed during copying. Do not reuse damaged or incomplete startup caches.
- Reject mismatched engine/driver architectures before VPN protection is armed;
  detect stale wrong-architecture Wintun DLLs during packaging as well.
- Check locked output before publishing and support a separate output directory
  instead of stopping a running VPN or attempting to overwrite loaded binaries.
- Use one Go TUN reader across reconnects, preserve the first activity packet,
  normalize pooled buffer lengths, and allow idle/connected cancellation.

Regression tests use fakes, temporary directories and off-screen rendering.
They do not change host routing, firewall rules, drivers or startup tasks.
