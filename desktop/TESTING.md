# Elevated VM acceptance checklist

Do these tests on a disposable local Windows VM with console access and a
snapshot. They deliberately interrupt internet access. Never run on a remote
machine without an independent recovery path.

1. Build with `build.ps1 -DownloadWintun`. Start `Usque.Desktop.exe`, accept UAC,
   and press power. Verify the terms prompt appears only for a missing profile,
   `%ProgramData%\UsqueDesktop\config.json` is created, and WARP connects.
2. Check `curl.exe --noproxy "*" https://www.cloudflare.com/cdn-cgi/trace` reports
   `warp=on` or `warp=plus`. Independently verify IPv4, IPv6, DNS and UDP traffic.
   A successful handshake alone does not prove all applications are tunneled.
3. Capture traffic on every physical adapter. While connected, application
   traffic must appear only inside endpoint UDP/443 (TCP/443 for HTTP/2), apart
   from the documented DHCP/neighbor-discovery exceptions. Test long-lived TCP,
   QUIC, raw DNS, DoH, and IPv6 flows created before and after connecting.
4. Interrupt connectivity to the endpoint. UI should report reconnecting after
   the engine detects loss. No application packets may fall back outside the
   tunnel in the detection window. Restore connectivity and verify reconnection.
5. Terminate `engine/usque.exe` using Task Manager. With automatic recovery on,
   the UI must show a retry countdown and reconnect using the same profile;
   physical IPv4/IPv6/DNS must remain blocked throughout. Disable recovery and
   repeat: the UI must remain blocked until an explicit action.
6. Terminate the desktop process. Confirm its child engine exits and filters
   remain. Reopen: reconnect or explicitly restore internet access. Test restart
   with a locked session as well (no early-boot protection is claimed).
7. Repeat with Wi-Fi/Ethernet switching, a newly enabled adapter, sleep/wake,
   DHCP renewal, and other VPN software. Enterprise NRPT/firewall policy may
   conflict; validate the effective DNS policy and record unsupported setups.
8. Disconnect normally. Verify original defaults and physical-adapter DNS remain,
   owned split routes/NRPT are removed, and no owned block filters remain.
9. Repeat both QUIC and TCP fallback with the kill switch disabled. Confirm the
   warning is accurate: this mode provides no fail-closed guarantee.
10. Cancel registration/connection and induce route/DNS setup failures. No false
    Connected state is allowed. With protection armed, failures must stay blocked.
    Run elevated `Usque.Desktop.exe --restore-network` to test recovery even when
    the engine or Wintun DLL is missing.

## Desktop feature acceptance

- Save preferences and restart: transport, protection, selected profile and tray
  behavior should persist. Startup must remain off until explicitly enabled.
- In Automatic mode, block only QUIC endpoint access. Verify HTTP/2 takes over,
  the original engine is gone, and packet capture shows no unprotected interval.
  Blocking TCP as well must leave the client blocked, not repeatedly disarm it.
- A handshake without a successful WARP/DNS check must not display **WARP
  verified**. Interrupt trace/hostname access after connecting: the health warning
  should appear and **Check now** should recover when access returns. Check IPv6
  availability independently; reachability checks do not prove DNS leak freedom.
- Generate a known amount of traffic. Verify adapter counters, rates and totals;
  fallback must retain session totals and a new connection must reset them.
- Close/minimize to the notification area, reopen, connect/disconnect from its
  menu and explicitly exit. Explorer restarting should not strand a hidden app.
- Create two profiles; import, rename, switch and delete one. Their credential
  files must remain separate. Profile changes must be disabled during a session
  or protection/recovery state, and imported originals must remain unchanged.
- Inspect an exported diagnostic JSON for private keys, tokens, device IDs,
  addresses, personal profile names and user paths before sharing it.
- Enable sign-in startup explicitly in the VM. Check that Task Scheduler uses
  the correct user, highest privileges, interactive-token logon, the correct
  ACL-protected Program Files executable path and `--startup`, with no stored
  password. The installed runtime must not contain profiles/settings/journals.
  A normal, unelevated user must not be able to modify it. Restart/sign in;
  the app should launch in the tray and connect only if auto-connect is enabled.
  Disable startup and verify the owned task is removed.

Inspect WFP state read-only with `netsh wfp show state`. Inspect routes with
`Get-NetRoute` and DNS policy with `Get-DnsClientNrptPolicy -Effective`. Keep
packet captures and diagnostics private: they can contain network identifiers.

## Recovery and proxy acceptance (1.3)

- Switch Wi-Fi → Ethernet → Wi-Fi and renew DHCP. Confirm the endpoint host route
  is re-pinned to the new underlying adapter, DNS is restored through the new TUN,
  the reconnection counter increases, and packet capture shows no leak interval.
  Changes to Usque's own adapter must not cause a reconnect loop.
- Suspend while connected or counting down. The old engine must stop and no new
  engine should start until resume. Verify cancellation by disconnecting during
  the countdown. Subsequent network changes must not reconnect an offline app.
- Block QUIC, then TCP. Recovery must remain protected, back off to at most
  30 seconds, and not oscillate back to QUIC after TCP fallback. Induce cleanup
  failure: recovery must stop without releasing protection.
- Run diagnostics online, offline, with DNS/hostname blocked and with IPv6
  unavailable. Verify distinct outcomes/advice, accurate timestamps and safe JSON
  export. Observe that offline diagnostics sends no direct online probe and
  never changes routes, DNS, proxy settings or filters.
- Explicitly recover the tunnel, then select each local proxy mode. Verify
  listener addresses are only `127.0.0.1`, the selected port works, port collisions
  never report Connected, and no machine-wide route/NRPT/WFP changes occur.
- Verify `curl --proxy socks5h://127.0.0.1:1080` and the HTTP proxy equivalent
  report WARP; a separate app not using the proxy must remain on its normal path.
  Confirm DNS for remote SOCKS names travels inside WARP, IPv6 literals work,
  TCP fallback and sleep/wake recover, and the panel never claims device coverage.
- Leave crash filters/journal from a tunnel session, then attempt proxy mode.
  It must refuse to start until explicit recovery, without lowering protection.

## UI acceptance (1.3.1)

- At 800×480 logical pixels/high DPI, verify the complete window fits the work
  area and every settings section remains reachable by scrolling. Check English
  and Turkish labels, navigation alignment and accessible names.
- Switch language with non-default transport/mode selections. Saved selections
  must survive; failure to save must not change the visible language.
- Type blank/nonnumeric/out-of-range proxy ports without leaving the field.
  Expect inline validation, disabled connect/copy, and unchanged saved settings.
  Correct the port and confirm the copied address uses the new value.
- While a diagnostic check is pending, disconnect or switch sessions; old results
  must not appear on the new session. Selecting another profile must clear them.
- During exit confirmation and awaited cleanup, tray connect/restore actions must
  be disabled. Cancel or simulate failed cleanup: the app must remain usable and
  the normal close-to-tray preference must still work.
- Confirm live health messages, error dialogs and exported reports mask secrets.
  Existing crash protection must not be labeled off merely because a saved
  preference is off. Empty-log copy/clear actions should be disabled.

## Tray notification acceptance (1.3.2)

- Enable Windows notifications and disable Do Not Disturb for the acceptance
  test. Connect and verify exactly one connected notification, after the first
  post-connect traffic sample, containing download/upload rates. This must not
  initiate an external speed test. Idle rates can legitimately be zero.
- Generate upload/download traffic. Hover the icon both on the main taskbar and
  inside the hidden-icons overflow: both directions and connection state must
  match the app's live counters. Long text must not hide one direction.
- Disconnect normally; expect a disconnected notification only after cleanup.
  Interrupt a connected tunnel; expect an immediate warning with reconnection
  context. Retry ticks must not repeatedly show balloons; a successful new
  connection should show its own fresh speeds once.
- Fail a DNS/health check without losing the tunnel. Expect a health warning,
  not a false disconnection; recovery must not repeat the connected notification.
  Disconnect before the first rate tick: no delayed connected balloon may follow.
- Verify proxy/missing-counter states display unavailable speeds, disconnected
  states never retain old rates, and notification clicks reopen the main window.
  Repeat in Turkish and English. With Do Not Disturb enabled, suppressed balloons
  are expected but the hover tooltip must still reflect the latest state/rates.
