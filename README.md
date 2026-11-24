# usque

🥚➡️🍏🍎

Usque is an open-source reimplementation of the Cloudflare WARP client's MASQUE mode. It leverages the [Connect-IP (RFC 9484)](https://datatracker.ietf.org/doc/rfc9484/) protocol and comes with many operation modes including a native tunnel, SOCKS5 and HTTP proxies, and faster TCP-only L4 proxy variants.

This repository also ships **Usque Desktop**, a native Windows app built on the usque engine: one-click WARP connection, a persistent kill switch, automatic recovery, local SOCKS5/HTTP proxies, live traffic statistics and built-in diagnostics, in 11 languages.

<p align="center">
  <img src="_docs/screenshots/usque-desktop.png" alt="Usque Desktop: Overview page with a verified WARP connection, live traffic statistics and HTTPS latency" width="820">
</p>

## Table of Contents

- [usque](#usque)
  - [Table of Contents](#table-of-contents)
  - [Windows desktop app](#windows-desktop-app)
    - [Requirements](#requirements)
    - [Install](#install)
    - [First connection](#first-connection)
    - [Interface tour](#interface-tour)
      - [Overview](#overview)
      - [Connection log](#connection-log)
      - [Settings \& profiles](#settings--profiles)
      - [Notification area](#notification-area)
    - [Traffic modes](#traffic-modes)
    - [Kill switch and recovery](#kill-switch-and-recovery)
    - [Languages](#languages)
    - [Command-line switches](#command-line-switches)
    - [Privacy and stored data](#privacy-and-stored-data)
    - [Building the desktop app](#building-the-desktop-app)
  - [Installation](#installation)
  - [Building from source](#building-from-source)
    - [Docker](#docker)
  - [Usage](#usage)
    - [Registration](#registration)
    - [Enrolling](#enrolling)
    - [Native Tunnel Mode (for Advanced Users, Linux and Windows only!)](#native-tunnel-mode-for-advanced-users-linux-and-windows-only)
      - [On Linux](#on-linux)
      - [On Windows](#on-windows)
      - [Routes on Linux](#routes-on-linux)
      - [Routes on Windows](#routes-on-windows)
    - [SOCKS5 Proxy Mode (easy, cross-platform)](#socks5-proxy-mode-easy-cross-platform)
    - [HTTP Proxy Mode (easy, cross-platform)](#http-proxy-mode-easy-cross-platform)
    - [L4 Proxy Modes (easy, cross-platform)](#l4-proxy-modes-easy-cross-platform)
    - [Port Forwarding Mode (for Advanced Users, cross-platform)](#port-forwarding-mode-for-advanced-users-cross-platform)
    - [Connect/Disconnect Hooks](#connectdisconnect-hooks)
      - [Example on Linux](#example-on-linux)
      - [Example on Windows](#example-on-windows)
    - [TCP and HTTP/2 Support](#tcp-and-http2-support)
      - [HTTP/2 Configuration](#http2-configuration)
    - [Configuration](#configuration)
      - [Fields](#fields)
  - [ZeroTrust support](#zerotrust-support)
  - [Performance](#performance)
    - [Performance Tuning](#performance-tuning)
      - [Linux/BSD](#linuxbsd)
      - [DNS](#dns)
  - [Using this tool as a library](#using-this-tool-as-a-library)
  - [Known Issues](#known-issues)
  - [Miscellaneous](#miscellaneous)
    - [Censorship circumvention](#censorship-circumvention)
  - [Should I replace WireGuard with this?](#should-i-replace-wireguard-with-this)
    - [Why would you still switch?](#why-would-you-still-switch)
  - [Protocol \& research details](#protocol--research-details)
  - [Why was this built?](#why-was-this-built)
  - [Why did you fork connect-ip-go?](#why-did-you-fork-connect-ip-go)
  - [Why the name?](#why-the-name)
  - [Contributing](#contributing)
  - [Acknowledgements](#acknowledgements)
  - [Disclaimer](#disclaimer)

## Windows desktop app

Usque Desktop is a .NET 8 / WPF front end for the usque engine. It runs the engine's native tunnel or proxy modes for you, configures Windows routing and DNS, and adds a machine-wide kill switch, so there is no terminal setup and no `config.json` to edit. It is an independent, unofficial client and is not affiliated with Cloudflare.

This section is a user guide. The full technical reference (security model, startup task internals, tests) is in [`desktop/README.md`](desktop/README.md).

### Requirements

- Windows 10 version 1809 or later, or Windows 11, on x64 or ARM64.
- Administrator rights. The app manages routes, DNS and Windows Filtering Platform (WFP) filters, so it always starts elevated.
- Nothing else. The package is self-contained: it includes the .NET runtime, the usque engine and the official Wintun driver DLL.

### Install

1. Download `UsqueDesktop-Setup-<version>.exe` and its `.sha256` file from the [Releases page](https://github.com/mertokuyaz/usque-ui/releases).
2. Optionally, verify the download. The hash must match the one in the `.sha256` file:
   ```powershell
   (Get-FileHash .\UsqueDesktop-Setup-1.3.2-bugfixes.exe -Algorithm SHA256).Hash
   ```
3. Run the setup program. The wizard is available in English and Turkish. It installs to `C:\Program Files\UsqueDesktop`, adds a **Usque WARP** Start Menu shortcut, and can also add a desktop shortcut.

Setup never installs a Windows service or a startup task, and it does not create WARP profiles. The app creates those on first use. Setup and uninstall wait while Usque is running and never force-close it.

> [!IMPORTANT]
> Before uninstalling, open the app and use **Restore internet access** (or [`--restore-network`](#command-line-switches)). The uninstaller intentionally leaves the kill-switch filters and `%ProgramData%\UsqueDesktop` in place, so it cannot cut you off from a recovery path.

### First connection

1. Press the large power button on the **Overview** page.
2. The first time, the app asks you to accept Cloudflare's WARP terms. It then registers and enrolls a new WARP profile automatically. Registration uses your regular connection, before the kill switch is armed.
3. The app arms the kill switch, starts the tunnel, pins the WARP endpoint to your physical adapter, and sets IPv4/IPv6 default routes. DNS goes to `1.1.1.1` / `1.0.0.1` through WARP.
4. It then checks WARP (`warp=on` / `warp=plus`), DNS and IPv6 through the tunnel. When the checks pass, the title reads **You're connected** and the badge shows **WARP VERIFIED**.

Press the button again to disconnect. Networking is restored first, then the kill switch is released. Later connections reuse the saved profile. To use an existing usque `config.json` instead of registering, choose **Import profile** before connecting.

### Interface tour

The **sidebar** has three pages: **Overview**, **Connection log** and **Settings & profiles**. It also has the **Exit Usque** button and the app version. The title bar shows the current page; its **—** and **×** buttons minimize or close the window without ending the connection (see [Notification area](#notification-area)).

#### Overview

The main card shows the connection state:

| Element | What it shows |
| --- | --- |
| Status badge (top right) | The current state (see the table below). |
| Power button | Connects or disconnects. A spinning ring is shown while the app is working. |
| Title and detail line | What is happening, for example *Connecting over QUIC…*. During automatic recovery a countdown (*Next attempt in 8 s*) is shown. |
| **Protocol** | The active transport: `MASQUE / QUIC` or `MASQUE / TCP`. |
| **Coverage** | `IPv4 + IPv6 verified`, `Full-device VPN`, or `Configured apps only` in proxy mode. |
| **Session time** | Elapsed time of the current connection. |

| Badge | Meaning |
| --- | --- |
| `NOT CONNECTED` | Idle. |
| `WORKING ON IT` | Registering, connecting, verifying, reconnecting or disconnecting. |
| `WARP VERIFIED` | The tunnel is up and the latest health check confirmed WARP and DNS. |
| `TUNNEL CONNECTED` | The tunnel is up, but WARP has not been verified (yet). |
| `PROXY ONLY` | A local SOCKS5 or HTTP proxy is running. Only apps configured to use it go through WARP. |
| `HEALTH WARNING` | Connected, but the latest health check failed. |
| `KILL SWITCH ACTIVE` | The tunnel is down and the kill switch is blocking traffic. |

The statistics panel below the card shows:

- **Download / Upload**: current traffic rates on the tunnel adapter, updated every second. These are real traffic rates, not a speed test.
- **Downloaded / Uploaded**: totals for the current session. They survive a QUIC → TCP fallback and reset on a new session.
- **HTTPS latency**: duration of an HTTPS probe through the tunnel (not an ICMP ping).
- **Reconnections**: how many times the tunnel has reconnected in this session.
- **Last successful check**, the latest health summary, and a **Check now** button. Health checks also run automatically every 30 seconds.

Further down the page are quick settings (**Traffic mode**, **Kill switch**, **Connection mode**, **Language**), the **Health & diagnostics** panel with **Diagnose connection**, the **Import profile** button, and a **WARP terms ↗** link.

If a connection or a health check fails, a **What you can do** panel appears above the card. It explains the problem, says whether kill-switch protection is still active, and offers the relevant actions: **Retry connection**, **Check now**, **Diagnose connection**, **Connection log**, **Restore internet access** and **Technical details**.

**Diagnose connection** works while connected or disconnected. It runs read-only checks and never changes network settings. Each check reports *Passed*, *Warning*, *Failed* or *Not tested*, with a suggested next step:

- Engine / driver
- Local network
- WARP endpoint / handshake
- Internet through tunnel / proxy
- Hostname / DNS access
- WARP verification
- IPv6 through tunnel / proxy

#### Connection log

The **Under the hood** page lists the latest 200 events. Secrets are redacted in memory: keys, tokens, identifiers, IP addresses and Windows user paths. The view follows new entries, but keeps your place if you scroll up. Buttons: **Export diagnostics** (a redacted JSON report), **Clear** and **Copy log**. Always review a log or report before sharing it.

#### Settings & profiles

Preferences are saved locally and kept across restarts. Connection settings can only be changed while disconnected. Language and the notification-area option can be changed at any time.

| Group | Setting | Default | Notes |
| --- | --- | --- | --- |
| Connection & protection | **Traffic mode** | Full-device tunnel | Or SOCKS5 proxy / HTTP proxy. See [Traffic modes](#traffic-modes). |
| | **Kill switch** | On | Blocks traffic if the VPN drops, even after a crash. Full-device tunnel only. |
| | **Automatically recover the connection** | On | Reconnects after network changes, sleep/wake and engine failures. Retries back off from 2 to 30 seconds; disconnecting cancels them. |
| | **Preferred transport** | Automatic (QUIC → TCP) | Or *QUIC / HTTP/3 only* or *TCP / HTTP/2 only*. Automatic tries QUIC first and switches to TCP after a failed attempt, without lowering protection. |
| | **Language** | English | Applies immediately. See [Languages](#languages). |
| Background & startup | **Minimize and close to notification area** | Off | When on, minimize and close hide the window to the notification area instead of the taskbar. |
| | **Auto-connect when Usque opens** | Off | Connects with the selected profile on launch. Requires Cloudflare terms consent. |
| | **Launch at Windows sign-in** | Off | Copies the runtime to a protected folder under Program Files and creates a sign-in task for your account. No password is stored. |
| WARP profiles | Profile list | Default | Select a profile, then **Create profile**, **Rename selected** or **Delete selected**. **Import credentials…** loads an existing usque `config.json` into the selected profile; the source file is never modified. A new, empty profile registers on its first connection. |
| Health & diagnostics | **Diagnose connection**, **Export safe report…** | | The same checks as on Overview, plus a redacted report export. |

In proxy mode, a **Local port (1–65535)** field and a **Copy proxy address** button appear under Traffic mode.

#### Notification area

Usque keeps an icon in the Windows notification area (system tray):

- **Hover** to see the current status and the live **↓ download / ↑ upload** rates.
- **Double-click** to open the window. Launching Usque again also brings the running window forward.
- **Right-click** for **Open Usque**, **Connect** / **Disconnect**, **Restore internet access** and **Exit…**.
- Notifications are shown when the connection is established, ends, or is interrupted. Clicking one opens Usque.

Closing or minimizing the window never ends the connection. To quit, use **Exit Usque** (sidebar) or **Exit…** (tray menu). Usque then asks what to do:

- **Yes**: disconnect, restore normal internet access and exit.
- **No**: exit and keep any active kill-switch protection.
- **Cancel**: stay in the app.

### Traffic modes

| Mode | What goes through WARP | Kill switch | Traffic statistics |
| --- | --- | --- | --- |
| **Full-device tunnel** (default) | All IPv4 and IPv6 traffic on the device, through a Wintun adapter | Available (on by default) | Live rates and totals |
| **SOCKS5 proxy** | Only apps you configure to use `socks5://127.0.0.1:<port>` | Not available | Shown as **—** |
| **HTTP proxy** | Only apps you configure to use `http://127.0.0.1:<port>` | Not available | Shown as **—** |

The default proxy port is `1080`. The proxies listen on `127.0.0.1` only, so they are not reachable from your LAN, and the app never changes the Windows system proxy. For example, after connecting:

```powershell
curl.exe --proxy socks5h://127.0.0.1:1080 https://www.cloudflare.com/cdn-cgi/trace
curl.exe --proxy http://127.0.0.1:1080 https://www.cloudflare.com/cdn-cgi/trace
```

> [!WARNING]
> Proxy mode protects only the apps you configure, not the whole device. SOCKS5 clients should resolve hostnames through the proxy (for curl, `socks5h://` rather than `socks5://`).

### Kill switch and recovery

The kill switch uses persistent Windows Filtering Platform filters. While it is armed, outbound traffic is allowed only through the tunnel adapter, to the WARP endpoint, on loopback, and for DHCP / IPv6 neighbor discovery. There is no exception for physical DNS or the local network. The filters stay in place if the app or the engine crashes, and after a reboot.

> [!CAUTION]
> The kill switch affects **all users and applications on the machine**. Try it in a disposable VM first, and do not try it for the first time over Remote Desktop, SSH or any other connection you cannot afford to lose.

If your internet access is blocked:

1. Open Usque and either reconnect, or press **Restore internet access**. This disconnects WARP, removes the filters and restores your regular, **unprotected** connection.
2. If the window will not open, run this from an elevated terminal:
   ```powershell
   & "C:\Program Files\UsqueDesktop\Usque.Desktop.exe" --restore-network
   ```

Recovery does not need Cloudflare or a working Wintun driver. Usque never silently restores internet access after a crash; that is always an explicit choice. Do not delete the app or `%ProgramData%\UsqueDesktop` while traffic is blocked.

The health and diagnostic checks are reachability tests. They are not packet-capture leak tests and are not an anonymity guarantee.

### Languages

The whole interface is translated into English, Chinese (Simplified, 中文), Hindi (हिन्दी), Spanish (Español), French (Français), Arabic (العربية), Bengali (বাংলা), Russian (Русский), Portuguese (Português), Urdu (اردو) and Turkish (Türkçe). Arabic and Urdu use a right-to-left layout. Addresses, counters and logs stay left-to-right.

### Command-line switches

| Switch | Purpose |
| --- | --- |
| `--restore-network` | Removes this app's kill-switch filters and session network changes, then exits. Works even if settings or profiles are corrupt. Run elevated. |
| `--preview` | UI-only preview for development. No elevation, registration, routes or firewall changes. |
| `--startup` | Used by the optional sign-in task. Starts minimized, or hidden when the notification-area option is on. |

### Privacy and stored data

- Profiles, `settings.json` and the recovery journal are stored in `%ProgramData%\UsqueDesktop`, which only Administrators and SYSTEM can access. Profiles contain private keys and API credentials. They are ACL-protected but **not encrypted at rest**.
- No telemetry. The app never changes the Windows system proxy and never overwrites a profile without asking.
- Only one instance can run on the machine.

### Building the desktop app

Install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) and the Go version required by `go.mod`, then run from the repository root:

```powershell
# Portable build in dist\desktop (keep the whole folder together)
powershell -ExecutionPolicy Bypass -File .\desktop\build.ps1 -DownloadWintun
.\dist\desktop\Usque.Desktop.exe

# Tests + clean package + installer (requires Inno Setup 6)
powershell -ExecutionPolicy Bypass -File .\desktop\build-installer.ps1
```

`build-installer.ps1` writes `dist\UsqueDesktop-Setup-<version>.exe` and a `.sha256` file. The [Windows desktop workflow](.github/workflows/desktop.yml) runs the same script on GitHub Actions. See [`desktop/README.md`](desktop/README.md) for build options (ARM64, framework-dependent builds, custom Wintun), and [`desktop/TESTING.md`](desktop/TESTING.md) for the VM acceptance checklist.

## Installation

This section and the ones that follow cover the cross-platform `usque` command-line engine that the desktop app is built on.

You can download the latest release from the [releases page](https://github.com/Diniboy1123/usque/releases). For now, Android (`arm64`), Linux (`armv5`, `armv6`, `armv7`, `arm64`, `amd64`), Windows (`arm64`, `amd64`, `386`) and Darwin (`arm64`, `amd64`) binaries are provided. **However only the Linux `amd64` binary was tested.** If you have a different platform, you can build from source.

Extract the archive and you will find a binary named `usque` in the root directory. You can move this binary to a directory in your `PATH` to make it accessible from anywhere.

## Building from source

Since the tool is written in Go, it should be rather trivial.

1. Ensure that you have Go installed on your system. You can download it from [here](https://golang.org/dl/). At least Go 1.26.3 is required.
2. Clone this repository and switch to the project's root directory
3. Build the project using the following command:
```shell
CGO_ENABLED=0 go build -ldflags="-s -w" .
```

And that will produce an `usque` binary in the current directory.

If you would rather cross compile, set the `GOOS` and `GOARCH` environment variables accordingly. For example, to build for Windows on a Linux system:
```shell
GOOS=windows GOARCH=amd64 CGO_ENABLED=0 go build -ldflags="-s -w" .
```

### Docker

You can deploy the tool using Docker. [Dockerfile](Dockerfile) is provided in the repository. To build the image, run:

```shell
docker build -t usque:latest .
```

Example usage *(spawns a SOCKS proxy and exposes it on port 1080)*:

```shell
docker run -it --rm -p 1080:1080 usque:latest socks
```

## Usage

```shell
$ ./usque --help
An unofficial Cloudflare Warp CLI that uses the MASQUE protocol and exposes the tunnel as various different services.

Usage:
  usque [command]

Available Commands:
  completion    Generate the autocompletion script for the specified shell
  enroll        Enrolls a MASQUE private key and switches mode
  help          Help about any command
  http-proxy    Expose Warp as an HTTP proxy with CONNECT support
  l4-http-proxy Expose Warp as an L4 TCP-only HTTP proxy with CONNECT support
  l4-socks      Expose Warp as an L4 TCP-only SOCKS5 proxy
  nativetun     Expose Warp as a native TUN device
  portfw        Forward ports through a MASQUE tunnel
  register      Register a new client and enroll a device key
  socks         Expose Warp as a SOCKS5 proxy
  version       Print the version number of usque

Flags:
  -c, --config string   config file (default is config.json) (default "config.json")
  -h, --help            help for usque

Use "usque [command] --help" for more information about a command.
```

Before doing anything, you need to *register*.

### Registration

There is a handy *(though not too feature-rich)* `register` subcommand that creates a fresh Warp account ready to use. It also takes care of device registration and MASQUE device enrollment. Call this once and it will create a working configuration for future use in modules.

A simple example would be:

```shell
$ ./usque register
```

> [!TIP]
> If you want to specify a name for the device, you may do so by specifying `-n <device-name>`.

> [!TIP]
> If you want to register with ZeroTrust, you need to obtain the team token and do so by specifying `--jwt <team-token>`.
> 1. Visit `https://<team-domain>/warp` and complete the authentication process.
> 2. Obtain the team token from the success page's source code, or execute the following command in the browser console: `console.log(document.querySelector("meta[http-equiv='refresh']").content.split("=")[2])`.

If you didn't get rate-limited or any other error, you should see a `Successful registration` message and a working config. In case of certain issues such as rate limiting, you may need to wait a bit and try again.

### Enrolling

While the registration command also handles device enrollment, in some cases, you may want to re-enroll the old key found in the config. This is useful when migrating from one device to another while the server still has the old client key enrolled. Or if your account had WireGuard enabled and you want to switch to MASQUE.

> [!NOTE]
> This command refreshes your config with data downloaded from Cloudflare servers, so make sure you have backups.

> [!TIP]
> When using ZeroTrust this command can update the config with the newly assigned IPv4 and IPv6 addresses. This is useful, because IPv6 doesn't seem to work there if the IPv6 in config isn't up to date. Personal WARP doesn't seem to be affected by this.

```shell
$ ./usque enroll
```

### Native Tunnel Mode (for Advanced Users, Linux and Windows only!)

The native tunnel is a good choice when you want a real network interface. It is also one of the faster modes of operation.

#### On Linux

It **requires the `TUN` device** to be available on the system. This means your kernel must support loading the `tun.ko` module. **`iproute2` is also a requirement**. While it is still userspace, traffic is directly injected into the kernel's network stack, therefore you will see a real network interface and you will be able to tunnel any IP (Layer 3) traffic that WARP supports. Since it creates a real network interface and also attempts to set IP addresses, **it will most likely require root privileges**.

#### On Windows

It requires the [wintun.dll](https://www.wintun.net/) file to be present in the same directory as the `usque.exe` binary. Then it will take care of bringing up the interface and setting the IP addresses. Normally this also requires administrative privileges.

To bring up a native tunnel, execute:

```shell
$ sudo ./usque nativetun
```

Unless otherwise specified, you should see a `tun0` (or `tun1`, `tun2`, etc.) interface appear on Linux. On Windows, the interface is typically named `usque`. If you didn't disable IPv4 and IPv6 inside the tunnel using cli flags (on Linux), you should also see the IPv4 and IPv6 address pre-assigned to this interface. This should be enough for applications that can route traffic through a specific network interface to function. For example `ping`:

```shell
$ ping -I tun0 1.1
```

Or `curl`:

```shell
$ curl --interface tun0 https://cloudflare.com/cdn-cgi/trace
```

Should just work. However **the tool doesn't set any routes**. If you need that, you have to do that manually. For example, to route all traffic to the tunnel, you need to make sure that the address used for tunnel communication is routed to your regular network interface. For that, open the `config.json` and check the endpoint address. If you plan to connect to the Cloudflare endpoint using IPv4, you will most likely see this:

```json
"endpoint_v4": "162.159.198.1"
```

Remember that for the next steps.

#### Routes on Linux

Assuming your regular network interface is `eth0` and your gateway address is `192.168.1.1`, you can add a route like this:

```shell
$ sudo ip route add 162.159.198.1/32 via 192.168.1.1 dev eth0
```

After that, you can add a default route to the `tun0` interface for both IPv4 and IPv6:

```shell
$ sudo ip route add default dev tun0 && sudo ip -6 route add default dev tun0
```

#### Routes on Windows

First, determine the interface index for your regular network adapter by running:

```cmd
route print
```

Look under the **Interface List** for the correct index number.

Before adding default routes, determine the gateway for your tunnel interface by running:

```cmd
ipconfig
```

Look for the adapter named `usque` (or whatever name you have for your tunnel interface) and note its gateway address.

Assuming:

* Tunnel endpoint: `162.159.198.1`
* Gateway: `192.168.1.1`
* Interface index: `12`
* Tunnel interface: `usque` (replace with the actual name or index of your tunnel interface)

Run the following commands in an **elevated Command Prompt** (Run as Administrator):

```cmd
route add 162.159.198.1 mask 255.255.255.255 192.168.1.1 metric 1 if 12
```

Then add default routes to route all traffic through the tunnel:

```cmd
route add 0.0.0.0 mask 0.0.0.0 [TUNNEL_GATEWAY] metric 1 if [TUN_INTERFACE_INDEX]
route add ::/0 [TUNNEL_GATEWAY] metric 1 if [TUN_INTERFACE_INDEX]
```

> [!NOTE]
> Replace `[TUNNEL_GATEWAY]` and `[TUN_INTERFACE_INDEX]` with the actual values for your tunnel adapter. You can get these by checking `ipconfig` and `route print`.

> [!CAUTION]
> Always be careful with default routes, especially if you are running this on a headless machine. It is very easy to close yourself out of your current session. I suggest [network namespaces](https://man7.org/linux/man-pages/man7/network_namespaces.7.html) on Linux as a safer playground for experiments or a spare VM with physical access or serial console.
> On Windows, you can set specific routes first such as `8.8.8.8/32` to ensure the tunnel works before adding a default route.

### SOCKS5 Proxy Mode (easy, cross-platform)

> [!TIP]
> If you are OK with using QUIC (UDP) to connect to WARP and don't need UDP support for your SOCKS proxy, [L4 mode](#l4-proxy-modes-easy-cross-platform) is more efficient.

If you just want to expose the tunnel as a quickly deployable proxy and your client supports SOCKS5, this mode is for you. It **supports both IPv4 and IPv6**. **TCP and UDP** even! It is also **cross-platform** and doesn't require any special kernel modules or root privileges. However it emulates an entire user-space network stack, so it can be resource hungry.

To start a SOCKS5 proxy, you can run:

```shell
$ ./usque socks
```

By default this will launch a SOCKS5 proxy on `0.0.0.0:1080` **with no authentication**. You can choose to bind to a specific address and port by specifying `-b` and `-p` respectively. You can also enable authentication by specifying `-u` and `-w` for username and password. For example:

```shell
$ ./usque socks -b 127.0.0.1 -p 8080 -u myuser -w mypass
```

Will start a SOCKS5 proxy accessible only on `127.0.0.1:8080` with username `myuser` and password `mypass`.

Test the proxy with `curl`:

```shell
curl -x socks5://myuser:mypass@localhost:8080 https://cloudflare.com/cdn-cgi/trace
```

> [!NOTE]
> Since the proxy emulates its own networking stack, it's generally safe to say that users won't be able to access internal IPs and services the host has access to using the proxy. However the internal WARP network is available for them unfiltered. If you have ZeroTrust and Gateway on, users of your proxy may be able to reach each other as no manual filtering is applied. **Inside the tunnel they will be able to connect to any TCP or UDP service**.

> [!CAUTION]
> Local SOCKS5 **traffic is not encrypted** since SOCKS5 does not support encryption. You probably shouldn't transport statewide secrets from one device to another on a public WiFi that has `usque` running.

> [!NOTE]
> For now only one `user:pass` is supported.

### HTTP Proxy Mode (easy, cross-platform)

> [!TIP]
> If you are OK with using QUIC (UDP) to connect to WARP, [L4 mode](#l4-proxy-modes-easy-cross-platform) is more efficient.

Another easy to "*get up and running*" mode of operation is the HTTP proxy mode. **Almost all clients support HTTP proxies**. Regular HTTP unencrypted traffic sent to this proxy will simply be forwarded to the WARP network. For HTTPS and any other **TCP traffic**, the proxy exposes a HTTP `CONNECT` method. This mode is **cross-platform** and doesn't require any special kernel modules or root privileges. However it emulates an entire user-space network stack, so it can be resource hungry.

To start a HTTP proxy, you can run:

```shell
$ ./usque http-proxy
```

By default this will launch a HTTP proxy on `0.0.0.0:8000` **with no authentication**. You can choose to bind to a specific address and port by specifying `-b` and `-p` respectively. You can also enable authentication by specifying `-u` and `-w` for username and password. For example:

```shell
$ ./usque http-proxy -b 127.0.0.1 -p 8080 -u myuser -w mypass
```

Will start a HTTP proxy accessible only on `127.0.0.1:8080` with username `myuser` and password `mypass`.

Test the proxy with `curl`:

```shell
curl -x http://myuser:mypass@localhost:8080 https://cloudflare.com/cdn-cgi/trace
```

> [!NOTE]
> Since the proxy emulates its own networking stack, it's generally safe to say that users won't be able to access internal IPs and services the host has access to using the proxy. However the internal WARP network is available for them unfiltered. If you have ZeroTrust and Gateway on, users of your proxy may be able to reach each other as no manual filtering is applied. **Inside the tunnel they will be able to connect to any TCP service.**

> [!CAUTION]
> Local HTTP **traffic is not encrypted** since HTTP does not support encryption, and HTTPS isn't implemented. It should be trivial to add, but I didn't need it yet. You probably shouldn't transport statewide secrets from one device to another on a public WiFi that has `usque` running.

> [!NOTE]
> For now only one `user:pass` is supported.

### L4 Proxy Modes (easy, cross-platform)

If your clients are fine with TCP only proxying, the L4 modes are the lighter option. They use direct HTTP/3 CONNECT streams, skip UDP/datagram handling, and avoid the extra user-space networking stack that the full SOCKS5 and HTTP proxy modes need. They are cross-platform and do not require elevated privileges.

Use `l4-http-proxy` when you want an HTTP proxy with CONNECT support, or `l4-socks` when you want SOCKS5 compatibility:

```shell
$ ./usque l4-http-proxy
```

```shell
$ ./usque l4-socks
```

They support the same bind, port, auth, DNS, and hook flags as the other proxy modes. **For TCP-only workloads, these modes should generally outperform the full proxy stack.**

More details [in the wiki](https://github.com/Diniboy1123/usque/wiki/L4-proxy-mode).

### Port Forwarding Mode (for Advanced Users, cross-platform)

While most other modes expose the tunnel in some way or another, this mode is intended for more advanced use-cases. Think of it a bit like SSH forwarding. It allows you to either forward a specific port from the host to the WARP network or from the WARP network to the host.

*Why would you do that, you may ask?* For regular WARP, this feature is pretty useless. In fact the tool's `registration` command cannot even set this up properly. However with some manual configuration and assuming you have a ZeroTrust network, you can configure WARP to WARP device communication. Each device will have a unique internal IPv4 and IPv6 address and they can reach each other that way. This is not how it works out of the box however, so [follow the offical guide](https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/private-net/warp-to-warp/) to set it up. Once you have it working, you can use this feature to forward ports to and from the WARP network.

> [!TIP]
> It is very important for this mode to function properly to have up to date IP addresses in the config. Running the [enrollment](#enrolling) process before bringing up the forwarding should set the correct IPs.

This mode is **cross-platform** and doesn't require any special kernel modules or root privileges. However it emulates an entire user-space network stack, so it can be resource hungry.

As for the setup. Let's say you have this in the config:

```json
"ipv4": "100.96.0.3"
```

Which means that your device's internal IPv4 address is `100.96.0.3`. You are running a webserver on the host machine on port `8080`. Then there is another device with internal IPv4 address `100.96.0.2` that also exposes a webserver on port `8081`. You would like to forward the host's port `8080` to the WARP network and the WARP network's port `8081` to the host. To set this up, you can run:

```shell
$ ./usque portfw -R 100.96.0.3:8080:localhost:8080 -L localhost:8081:100.96.0.2:8081
```

> [!TIP]
> The syntax isn't exactly like the one from SSH. I suggest that you specify the syntax as seen in the example preferably with IP addresses and not hosts.

> [!TIP]
> Any number of ports are supported. You can chain many ports together if you specify the flag and the corresponding argument one after another.

### Connect/Disconnect Hooks

All tunnel modes can invoke an external executable after each successful tunnel connect and after each tunnel loss. This is useful for re-applying routes, firewall rules, or notifications without hard-coding them into the tool.

- `--on-connect <path>`: executed after every successful connect (including reconnects).
- `--on-disconnect <path>`: executed after every tunnel loss.

Both flags take a path to a single executable. The path is **exececuted directly**, so no shell is spawned, and no arguments are passed. If you need a one-liner like `ip route add ...`, wrap it in a small shell script.

Hooks run **fire-and-forget** in the background, so a hung hook cannot stall the tunnel. Their stdout and stderr are captured line-by-line and forwarded to the standard log with a `hook[connect]` / `hook[disconnect]` prefix.

The hook subprocess inherits the parent environment (so `PATH`, `HOME`, etc. work normally) plus the following `USQUE_*` variables:

- `USQUE_EVENT`: `connect` or `disconnect`.
- `USQUE_MODE`: `nativetun`, `socks`, `http-proxy`, `l4-socks`, `l4-http-proxy`, or `portfw`.
- `USQUE_IFACE`: tun interface name (only set in `nativetun` mode).
- `USQUE_IPV4`: internal IPv4 from the config.
- `USQUE_IPV6`: internal IPv6 from the config.
- `USQUE_ENDPOINT`: MASQUE endpoint address the tunnel is using.

#### Example on Linux

```shell
$ sudo ./usque nativetun \
    --on-connect /etc/usque/up.sh \
    --on-disconnect /etc/usque/down.sh
```

```shell
# /etc/usque/up.sh
#!/bin/sh
set -e
ip route replace 162.159.198.1/32 via 192.168.1.1 dev eth0
ip route del default || true
ip route replace default dev "$USQUE_IFACE"
ip -6 route replace default dev "$USQUE_IFACE"
echo "nameserver 1.1.1.1" > /etc/resolv.conf
```

#### Example on Windows

On Windows you can point `--on-connect` / `--on-disconnect` at a `.bat`, `.cmd`, or `.exe`. Env vars are accessed as `%USQUE_IFACE%`, `%USQUE_IPV4%`, etc. Run `usque` in an elevated Command Prompt so the hook inherits the privileges needed to modify routes.

```cmd
> usque.exe nativetun ^
    --on-connect C:\usque\up.bat ^
    --on-disconnect C:\usque\down.bat
```

```bat
@rem C:\usque\up.bat
@echo off
netsh interface ipv4 delete route 0.0.0.0/0 "%USQUE_IFACE%" >nul 2>&1
netsh interface ipv4 add route    0.0.0.0/0 "%USQUE_IFACE%" 0.0.0.0
netsh interface ipv6 delete route ::/0      "%USQUE_IFACE%" >nul 2>&1
netsh interface ipv6 add route    ::/0      "%USQUE_IFACE%" ::
```

If you prefer PowerShell, keep the hook itself pointed at a small `.bat` stub that invokes `powershell.exe -NoProfile -ExecutionPolicy Bypass -File up.ps1`. the hook runner does not accept extra arguments, so AFAIK a wrapper is the simplest way to reach a `.ps1`.

> [!TIP]
> Because `on-connect` fires on every reconnect, write your scripts to be idempotent. Prefer `ip route replace` / `netsh ... delete` + `add` over plain `add`, check-before-add for firewall rules, and so on.

> [!NOTE]
> Hooks are not run on initial process startup before the first connect, nor on final process shutdown. They fire strictly in response to tunnel lifecycle events.

### TCP and HTTP/2 Support

While `usque` was originally designed with a focus on **QUIC** and **HTTP/3**, Cloudflare has since introduced TCP fallback support in their official clients. `usque` now supports this connection method via `--http2`.

For full details and troubleshooting, see the wiki page: [HTTP/2 support](https://github.com/Diniboy1123/usque/wiki/HTTP-2-support).

#### HTTP/2 Configuration

To connect via TCP/HTTP/2, use the `--http2` CLI flag.

Because endpoints cannot be reliably pulled from the API, you can manage HTTP/2 connections with:

- `endpoint_h2_v4`: IPv4 address for HTTP/2 mode.
- `endpoint_h2_v6`: IPv6 address for HTTP/2 mode.

Default behavior:

- If `endpoint_h2_v4` is not set, `usque` uses `162.159.198.2`.
- `endpoint_h2_v6` is intentionally empty by default and must be configured manually when you want HTTP/2 over IPv6.

### Configuration

For simplicity, the tool uses a JSON configuration file. The default file is `config.json` in the current directory. You can specify a different file using the `-c` flag. This will be respected by all subcommands. Without a configuration file only the `register` subcommand will work.

Example config:

```json
{
  "private_key": "M...redacted...==",
  "endpoint_v4": "162.159.198.1",
  "endpoint_v6": "2606:4700:103::",
  "endpoint_h2_v4": "162.159.198.2",
  "endpoint_h2_v6": "",
  "endpoint_pub_key": "-----BEGIN PUBLIC KEY-----\nMFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEIaU7MToJm9NKp8YfGxR6r+/h4mcG\n7SxI8tsW8OR1A5tv/zCzVbCRRh2t87/kxnP6lAy0lkr7qYwu+ox+k3dr6w==\n-----END PUBLIC KEY-----\n",
  "license": "A...redacted...Z",
  "id": "00000000-0000-0000-0000-000000000000",
  "access_token": "00000000-0000-0000-0000-000000000000",
  "ipv4": "172.16.0.2",
  "ipv6": "2606:redacted:1"
}
```

#### Fields

- `private_key`: Base64 encoded ECDSA private key on the NIST P-256 curve in ASN.1 DER format. **Confidential.** This is used for device authentication.
- `endpoint_v4`: IPv4 address of the Cloudflare WARP endpoint. **Public.** Used for connecting to the WARP network.
- `endpoint_v6`: IPv6 address of the Cloudflare WARP endpoint. **Public.** Used for connecting to the WARP network.
- `endpoint_h2_v4`: IPv4 address used by `--http2`. Defaults to `162.159.198.2` when empty. **Public.**
- `endpoint_h2_v6`: IPv6 address used by `--http2`. Empty by default and must be set manually for HTTP/2 over IPv6. **Public.**
- `endpoint_pub_key`: Base64 encoded ECDSA public key on the NIST P-256 curve in PEM format. **Public.** This is used to ensure that we are indeed talking to the Cloudflare WARP endpoint and not being [MiTM](https://en.wikipedia.org/wiki/Man-in-the-middle_attack)'d.
- `license`: License returned by the server for our account. **Confidential.** With this, you can pair multiple devices to the same account.
- `id`: Device ID given by the server to us. **Public.** This is used for device identification and API calls.
- `access_token`: Access token given by the server to us upon registration/login. **Confidential.** This is used for API calls.
- `ipv4`: Internal IPv4 address assigned to the device by the Cloudflare WARP network. **Public.** This is assigned to the device's interface and is also used for communication between devices in the [port forwarding mode](#port-forwarding-mode-for-advanced-users-cross-platform).
- `ipv6`: Internal IPv6 address assigned to the device by the Cloudflare WARP network. **Public.** This is assigned to the device's interface and is also used for communication between devices in the [port forwarding mode](#port-forwarding-mode-for-advanced-users-cross-platform).

## ZeroTrust support

In my view ZeroTrust is Cloudflare's enterprise version of WARP. Explaining this in depth would be beyond the scope of this README.

While the tool won't be able to log you in to ZeroTrust *(as SSO is required for login there)* practice shows that you can get connection working if you really want to. For that you need to run `./usque register --jwt <jwt>` or put together a config file manually. If you choose to put together a config file manually, I suggest using the `register` command to obtain a personal WARP config. Keep all fields unchanged except for `access_token` and `id`. As for how to obtain these, be creative. For example both of these can be carved out from `/var/lib/cloudflare-warp/reg.json` if using the official WARP client on Linux. Or existing device IDs are listed in the ZeroTrust dashboard. Once these are in place, you can use the `enroll` command to refresh the config with the new data. You will see that the `license` field is empty. This is normal. ZeroTrust doesn't use licenses *(to my knowledge)*.

Warp to warp communication is supported by all modes of this tool if you have it [correctly set up](https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/private-net/warp-to-warp/). Proxies and tunnels can reach services exposed on other devices and [port forwarding](#port-forwarding-mode-for-advanced-users-cross-platform) can be used to forward ports to and from the WARP network.

> [!TIP]
> These tunnels seem to access the better-routed `warp=plus` network. However, once Proxy is enabled—which is required for Warp-to-Warp communication—the connection is downgraded to `warp=on`. This results in degraded *(athough usually still quite generous)* performance.

> [!TIP]
> By default Cloudflare logs most requests. You might want to turn that off to preserve the uncertain belief of a little more privacy. 👀

> [!WARNING]
> **You must reconnect after making changes for them to take effect.**

> [!NOTE]
> **You should probably set SNI to `zt-masque.cloudflareclient.com`** by specifying `-s zt-masque.cloudflareclient.com` when using any mode that involves tunnel connection. The default `consumer-masque.cloudflareclient.com` also works, but discouraged.

## Performance

The project is still in early stages of development *(I am happy I even got it working)* and performance wasn't a priority. In fact I am not even too familiar with Go. The official client *(at least on Linux and Android)* is implemented in Rust with the awesome [quiche](https://github.com/cloudflare/quiche) project. In contrast, this tool is written in Go and leverages the well-maintained [quic-go](https://github.com/quic-go/quic-go) library, which offers broad support for the QUIC protocol. However it only supports `reno` congestion control and it isn't the most performant implementation out there especially for high latency network environments.

Many other features such as [happy eyeballs](https://en.wikipedia.org/wiki/Happy_Eyeballs) are also missing.

So yes, the performance might not be the best. However, I was able to squeeze out `833.60 Mbps` download and `772.88 Mbps` upload on a 1 Gbps connection with Warp+ upon the first try using the SOCKS5 proxy mode with Firefox and [speedtest.net](https://www.speedtest.net/). The test was conducted on an `AMD Ryzen 7 5700U` config with `16 GB` of RAM on `Arch Linux`. That is good enough for me. I am sure there is room for improvement. But keep in mind that this is all userspace; SOCKS mode even emulates its own network stack. CPU usage was around 26%.

I heard that Windows performance is worse. I don't have a Windows machine to test it on. If you do, please let me know about your experience.

### Performance Tuning

#### Linux/BSD

`quic-go` will nicely warn you if this is set to a too small value on your machine. But the default UDP buffer size on Linux is quite small. You can increase it by running:

```shell
$ sudo sysctl -w net.core.rmem_max=7500000
$ sudo sysctl -w net.core.wmem_max=7500000
```

Refer to the [quic-go documentation](https://github.com/quic-go/quic-go/wiki/UDP-Buffer-Sizes) for a better explanation.

#### DNS

By default all modes except for the native tunnel mode will use [Quad9](https://quad9.net/) to resolve DNS traffic. While this seems to be an odd choice for a Cloudflare client, I prefer them over `1.1.1.1` because of their privacy claims. I believe it's a decent default. However `1.1.1.1` has better performance usually. You are free to change the DNS server used by the tool by specifying the `-d` flag.

For example:

```shell
$ ./usque socks -d 1.1.1.1 -d 1.0.0.1 -d 2606:4700:4700::1111 -d 2606:4700:4700::1001
```

Native tunnels will not customize DNS. Whatever you have set on your system will be preferred. Routing of DNS packets to the tunnel or somewhere else is also entirely up to you.

## Using this tool as a library

This is primarily a CLI tool for now. However some efforts were made to document and expose certain functions that can be used to build your own applications. **I do not recommend this** as of now though, because the implementation is quite unstable and the API is subject to change. I also didn't do the best job at abstraction, because my primary goal was to get it working and the second goal was to make something easily readable. So instead of using it directly as a library, people can fork and plug in extra functionality as they wish. I am open to PRs that make the code more modular and easier to use as a library.

As a starting point, you can reach out to the [`api/`](api/) package. For examples, take a look at the [`cmd/`](cmd/) package.

## Known Issues

- **remote end disconnects**: If you are inactive for a while, the remote end might disconnect you with a `H3_NO_ERROR` error. Similar behavior was observed earlier on their well studied `WireGuard` implementation where too long open connections with not significant network activity were disconnected. The official apps just reconnect once that happens, therefore I implemented a similar behavior. Therefore if you see disconnects, don't worry, it's probably just the remote end. The tool will reconnect automatically once you generate some outgoing traffic.
- **interaction with the Cloudflare API is limited**: This one is also intended. The tool's primary focus is MASQUE. If you want better support, I suggest the official client or [wgcf](https://github.com/ViRb3/wgcf).
- **no support for WireGuard**: This is a MASQUE client. If you want WireGuard, use the official client or [wgcf](https://github.com/ViRb3/wgcf).
- **no support for DoH etc.**: Yeah, the official clients expose a lot of extra DNS related features. I wanted to keep this lightweight. Those will probably not be supported by me. If you want, you are free to use 3rd party DoH clients and configure them to use the tunnel interface. DNS over Warp should already be working on all modes except for the native tunnel mode as all DNS queries made inside the tunnel will go through the tunnel (unless you use the `-l` flag).
- **slow initial speeds**: You may experience slow speeds when opening a new connection that can gradually increase by time. This is due to the `reno` congestion control algorithm used by `quic-go`. It is not the most performant one out there, especially not for high latency environments. We have to wait for support for different congestion control algorithms and see how they compare. For instance there is an open issue for [BBR](https://github.com/quic-go/quic-go/issues/4565).
- **native tunnels only support Linux and Windows**: This is due to the fact that we depend on the `TUN` device (Wintun on Windows). While that exists on Android, without root it's hard to use in its current form. BSD and macOS support is uncertain. These platforms are unsupported for now, because I don't have the means to test them and I am not willing to share untested code. PRs are welcome. On Windows, the [desktop app](#windows-desktop-app) sets up routes and DNS for you.

## Miscellaneous

### Censorship circumvention

There is hardly a way to distinguish MASQUE traffic from other HTTP/3 traffic. However QUIC mandates TLS v1.3 so we send a ClientHello with `client-masque.cloudflareclient.com` in the SNI field. Some firewalls may block this. You can change the SNI by specifying `-s` flag to any domain *(based on my experience)* and the connection will still work. Please note that this is definitely not Cloudflare's intended use case *(just a nice side effect)*. And before doing any circumvention attempts, you should make sure you are not breaking any laws. Personally I only see this as a clear benefit for masking the fact that we are connecting to Warp from MiTMers.

## Should I replace WireGuard with this?

That depends on your needs. 😊 WireGuard is a great protocol and its modern/fast cryptography plus the ability to have kernel mode support are both great things. If it works for you, I don't believe you should switch.

MASQUE is userspace, and so is QUIC, the underlying protocol, making the implementation slower. While it also has certain benefits such as full blown TLS support *(WireGuard comes with a fixed set of ciphers)*, adjustable congestion control, more advanced tuning parameters, it is also more complex.

At the end of the day, if you are happy with WireGuard, I don't see a reason to switch. If you are happy with Warp, but you want to use it on a platform that doesn't have official support, this tool might be for you.

### Why would you still switch?

- **reports that WARP+ doesn't work anymore over WireGuard arose lately**: I saw reports that WARP+ doesn't work anymore over WireGuard. While I cannot confirm this, I read reports that it is the case for some, ([see](https://github.com/ViRb3/wgcf/issues/459#issuecomment-2645319671)). It seems to work for me with this tool, so I consider this a feasible alternative.
- **you want to use WARP on a platform that doesn't have official support**: This tool is cross-platform and open-source, so you are free to use it on any platform that supports Go. This includes Windows, macOS, BSD, Linux, Android, iOS, etc. I hope it will work on all of them.
- **certain networks block WireGuard**: Certain networks block WireGuard, but don't block QUIC.
- **you are the curious type**: You are the curious type and you want to experiment with cool new technologies. MASQUE is a new and interesting approach... So why not? 😊

## Protocol & research details

This document would be large and too horrific for the average reader if I included all the details about the protocol and the research I did. If you are one of the few people who would be interested in some details, please refer to the [RESEARCH.md](RESEARCH.md) file. That one summarizes the research I did and the protocol details I found in a blog post like format. In the future I plan to write a clear and concise protocol document as well that will be linked here. Currently only the HTTP/3 is included there.

## Why was this built?

Mostly because I wanted to experiment with cool new technology, and MASQUE caught my attention. I was shocked to see that there are not many open-source implementations out there, in fact I haven't really seen any practical uses for `connect-ip` open-sourced yet. I wanted to change that and *"advertise"* a bit.

Secondly, I rely on WARP quite frequently for Cloudflare proxied websites as peering between my ISP and regular Cloudflare protected websites is not the best. With WARP+, I get better routes and faster speeds. However, the official client's WireGuard implementation runs in userspace, so I was using `wgcf` instead to run it in kernel mode. It has been misbehaving a lot lately; for some reason, I have to reconnect multiple times whenever I want to use it. It started working after a few tries, but it was annoying. There are a few issues open on the `wgcf` repository that are related to this, like [this](https://github.com/ViRb3/wgcf/issues/158) and [this](https://github.com/ViRb3/wgcf/issues/50). WireGuard was also blocked on the local train WiFi, but MASQUE wasn't.

Then I switched to the official client and MASQUE which worked much more reliably. However the whole app is quite heavy ([260 MiB compressed for Android](https://play.google.com/store/apps/details?id=com.cloudflare.onedotonedotonedotone) by the time I am writing this). *Cloudflare One, the enterprise version is much lighter...*

But anyway, I found that the app used too much memory and that wasn't even the worst part.

I figured this while casually sitting on a train, browsing the interwebz with Warp on. Once in a while it would crash and disconnect the entire VPN profile leaving my traffic exposed to the local WiFi. That was quite unpleasant and the final straw that made me start this project.

While it doesn't exactly solve the reconnect issue yet either, it is planned. And with the leap forward to open-sourcing this, I hope that the community can help me make this tool better and more reliable.

## Why did you fork connect-ip-go?

Because Cloudflare's implementation isn't exactly RFC 9484 compliant and it's not going to work without directly monkey patching the library. Find my ugly, but hopefully working version [here](https://github.com/Diniboy1123/connect-ip-go).

## Why the name?

Obviously I didn't plan to name it `warp`, `cloudflare` or anything similar to reduce conflicts or to make users knock on the wrong doors for support because of my faulty implementation attempts.

Since the underlying technology is MASQUE, I wanted to come up with something short, memorable and unique, but still related to the underlying technology. I had a fun 4 years of Latin in school, so I figured the perfect name would be `usque`. If I can trust my rusty memory, it means `all the way`. I thought it was a good fit, because it is a tool that tunnels all the way to the Cloudflare network. We also go a long way until this becomes stable. You get the idea... 😊  It is also (hopefully) unique enough to not conflict with other projects.

## Contributing

Contributions are welcome. In fact I am a university student with very limited time and resources. For now the tool mostly implements my needs and ideas, but I would like to see it grow and become more stable with many exciting features to come. If you have any ideas, suggestions, bug reports or even code contributions, feel free to open an issue or a pull request. I will do my best to get back to you.

## Acknowledgements

This tool wouldn't exist without the following incredible projects. Please go and star them all if you like this project!

- [Cloudflare Blogs](https://blog.cloudflare.com/) - They have some interesting insights documented across their blogs. I learned some bits and pieces from there.
- [cobra](github.com/spf13/cobra) - Powerful CLI library for Go. Used for the CLI interface. Absolutely love it.
- [connect-ip-go](https://github.com/quic-go/connect-ip-go) - ~~The only open-source implementation for `RFC 9484` I could find.~~ (this is not true anymore, [see](RESEARCH.md#interesting-other-connect-ip-related-projects)) Our entire IP tunneling depends on this. We rely on my fork of this project heavily.
- [Frida](https://frida.re/) - This was a huge help for dynamic analysis of the official client. I was able to see what was going on in the app in real time and dumping certain values.
- [frida-interception-and-unpinning](https://github.com/httptoolkit/frida-interception-and-unpinning/tree/4d477da) - Very good at unpinning cert. pinning, so I could explore how API calls were made by the app.
- [friTap](https://github.com/fkie-cad/friTap) - For being an exceptionally well written and easy to use Frida script that one can use to dump TLS secrets effortlessly.
- [Go](https://go.dev/) - The Go programming language. I am not a professional developer, but I was able to get this working with Go. It is a great language.
- [go-socks5](github.com/things-go/go-socks5) - A great SOCKS5 library for Go. Used for the SOCKS5 proxy mode. Taught me a lot about the SOCKS protocol.
- [gvisor](https://gvisor.dev/) - Gvisor is a great tool for running untrusted code in a safe environment. They also implement a complete network stack in userspace. This makes it possible for all proxy modes to run without root and cross-platform.
- [IDA Free](https://hex-rays.com/ida-free) - Great stuff for dealing with rust binaries. Helped a lot with understanding the official client.
- [JADX-GUI](https://github.com/skylot/jadx) - Great stuff for dealing with Android APKs. Helped a lot with understanding the official client.
- [masque-go](https://github.com/quic-go/masque-go) - While I didn't end up using it, as this is for `connect-udp` like `RFC 9298` compliant MASQUE servers, it was a great starting point while I was researching the topic.
- [mitmproxy](https://mitmproxy.org/) - Very useful for intercepting API calls made by the official client.
- [netlink](github.com/vishvananda/netlink) - A great library for interacting with the Linux network stack using `iproute2`. Used for the native tunnel mode on Linux.
- [quic-go](https://github.com/quic-go/quic-go) - Powerful QUIC library for Go, used for many things from establishing the connection to maintaining it.
- [uritemplate](github.com/yosida95/uritemplate) - A great library for parsing URI templates. Used as a utility to pass the correct endpoint URI to `connect-ip-go`.
- [water](https://github.com/songgao/water) - A TUN/TAP library for Go. Used for the native tunnel mode. Taught me everything I know about TUN/TAP devices.
- [wireguard-go](golang.zx2c4.com/wireguard) - A WireGuard library for Go. Used very widely by the project too for rootless virtual tun networks, because its `netstack` implementation is very easy to use.
- [wireshark](https://www.wireshark.org/) - I used Wireshark to debug both the official client and this tool during initial development. A real swiss army knife for network debugging.

Special shoutout to [**@monkeywave**](https://github.com/monkeywave), one of the [friTap](https://github.com/fkie-cad/friTap) contributors who helped me decipher the last bit of information I needed to get this tool working. In fact if it wasn't for them, I would have probably given up on this project. They helped me and patiently guided me through the process of dumping TLS secrets from the official client. The issue is worth a read [if you are interested](https://github.com/fkie-cad/friTap/issues/38). One of the best experiences I had in the open-source community. Thank you!

Another shoutout goes to [**@marten-seemann**](https://github.com/marten-seemann) for maintaining the entire quic-go ecosystem. I opened a few issues there as well and always got a helpful response. Thank you!

## Disclaimer

Please do NOT use this tool for abuse. At the end of the day you hurt Cloudflare, which is probably unfair as you get this stuff even for free, secondly you will most likely get this tool sanctioned and ruin the fun for everyone.

The tool mimics certain properties of the official clients, those are mostly done for stability and compatibility reasons. I never intended to make this tool indistinguishable from the official clients. That means if they want to detect this tool, they can. I am not responsible for any consequences that may arise from using this tool. That is absolutely your own responsibility. I am not responsible for any damage that may occur to your system or your network. This tool is provided as is without any guarantees. Use at your own risk.

While the tool was made with security considerations in mind, I am not a security expert nor an IT professional. I am just a hobbyist and this is just a hobby project. Again, use at your own risk. However security reports are welcome. Feel free to open an issue with your contact details and I will get back to you, so you can share your findings **IN PRIVATE**. Once there was enough time to fix the issue, I will credit you in the release notes and the findings can be made public. I appreciate any help in making this tool more secure.

**This tool is not affiliated with Cloudflare in any way. The tool was neither endorsed nor reviewed by Cloudflare. It is an independent research project. Cloudflare Warp, Warp+, 1.1.1.1™, Cloudflare Access™, Cloudflare Gateway™ and Cloudflare One™ [are all registered trademarks/wordmarks](https://www.cloudflare.com/trademark/) of Cloudflare, Inc. If you are a Cloudflare employee and you think this project is in any way harmful, please open an issue and I will do my best to contact you and resolve the issue.**

WireGuard™ is a [registered trademark](https://www.wireguard.com/trademark-policy/) of Jason A. Donenfeld.
