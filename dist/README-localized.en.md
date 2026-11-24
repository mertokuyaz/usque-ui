# Usque Desktop — Multilingual Windows Installer

Usque Desktop is an independent, open-source Windows client for managing
Cloudflare WARP connections. It is not an official Cloudflare application and
is not affiliated with Cloudflare.

## Package

- **Installer:** `UsqueDesktop-Setup-1.3.2-localized.exe`
- **Version:** 1.3.2 — separately packaged with updated translations
- **Platform:** Windows 10 (1809 or later) / Windows 11, x64
- **Requirements:** Administrator privileges and internet access to connect
- The .NET runtime is included; no separate .NET installation is required.

This package includes the updated desktop translations. It is not the same file
as the previously published `UsqueDesktop-Setup-1.3.2.exe`. The Go engine is
unchanged.

## Installation

1. Close any running Usque instance using **Exit Usque**.
2. Open `UsqueDesktop-Setup-1.3.2-localized.exe`.
3. Approve the Windows administrator prompt.
4. Complete the installer. The desktop shortcut is optional.
5. Launch Usque from the Start menu or shortcut.

**Note:** The installer is not digitally signed. Windows SmartScreen may display
a warning. Obtain the file only from a trusted source and verify its SHA-256
checksum below before running it.

## First connection

1. Optionally change the language under **Settings & profiles → Language**.
2. Press the power button on the main screen.
3. If a new profile is needed, read and accept the Cloudflare WARP terms of service.
4. Follow the connection and verification status on the main screen.

Initial profile registration uses your regular internet connection before the
kill switch is armed. You can also import an existing `usque config.json`.
Do not share your private keys or profile files.

## Languages

English, Simplified Chinese, Hindi, Spanish, French, Arabic, Bengali, Russian,
Portuguese, Urdu and Turkish are supported.

Language changes apply immediately within the application. Arabic and Urdu use
right-to-left layouts; technical addresses, counters and logs retain left-to-right
direction. The installer wizard itself offers English and Turkish.

## Connection modes and security

- **Full-device tunnel:** Routes the device's IPv4 and IPv6 traffic through WARP.
  The kill switch is enabled by default.
- **SOCKS5 / HTTP proxy:** Covers only applications explicitly configured to use
  the proxy. It does not protect other applications or change Windows system
  proxy settings. The full-device kill switch is unavailable in this mode.
- **Automatic transport:** Tries QUIC first, then TCP / HTTP/2 after a failed attempt.

Hiding to the notification area is off by default. Minimizing or closing the
window minimizes the application to the taskbar without disconnecting.
You can enable notification-area hiding in settings. Existing saved preferences
are preserved. Use **Exit Usque** to quit completely.

### If internet access is blocked

The kill switch may remain active after an unexpected shutdown or connection
loss. Reopen the application and reconnect, or select **Restore internet access**.
Restoring access removes protection and returns to your regular, unprotected
internet connection.

**Restore internet access before uninstalling.** The uninstaller does not
automatically remove profiles or active network protection. Do not manually
delete application data while protection is active.

If the main window will not open, run this in an administrator PowerShell window:

```powershell
& "$env:ProgramFiles\UsqueDesktop\Usque.Desktop.exe" --restore-network
```

If you selected a custom installation folder, adjust the path accordingly.

## Data and privacy

Profiles, preferences and recovery information are stored in
`%ProgramData%\UsqueDesktop`. Access is restricted to Administrators and SYSTEM;
profile files are not additionally encrypted at rest.

Diagnostic reports mask known sensitive information. Always review reports
before sharing them. Connection verification is not an anonymity guarantee or
a packet-level leak test.

## File verification

Installer SHA-256:

```text
fbb1cc43d38a682c8b178136e33681930ad93b997ce8ec9ef152e326cc6e5df9
```

Calculate the checksum in PowerShell from the folder containing the installer:

```powershell
Get-FileHash .\UsqueDesktop-Setup-1.3.2-localized.exe -Algorithm SHA256
```

The result must match the value above. The checksum is also provided in
`UsqueDesktop-Setup-1.3.2-localized.exe.sha256`.

## Validation and licenses

All **138/138 desktop tests** passed for the package's source code. These tests
do not modify real networking and do not guarantee operation in every network
environment.

Application and component licenses are included in the installation folder:
`USQUE-LICENSE.md`, `WINTUN-LICENSE.txt` and the `Licenses` directory.
