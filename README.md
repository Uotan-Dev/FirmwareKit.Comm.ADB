# FirmwareKit.Comm.ADB

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

A high-performance .NET implementation of the Android Debug Bridge (ADB) protocol over USB, strictly aligned with AOSP logic. Part of the FirmwareKit ecosystem.

## Overview

Unlike the traditional Google `adb` client/server architecture, this library talks ADB **directly** to `adbd` — over USB (via `FirmwareKit.Comm`, like the fastboot implementation) or over TCP (`AdbTcpTransport`, equivalent to `adb connect host:port`). There is no `adb` server process, no `host:5037` endpoint, and no `adb.exe` dependency. This makes it suitable for tools that need a self-contained, dependency-light ADB implementation for firmware flashing, factory automation, and device bring-up.

## Architecture: direct-write (no client/server)

This project intentionally does **not** implement Google's client/server split. Every command opens its own transport to a device and speaks the ADB wire protocol directly to `adbd`:

```
┌──────────────┐    ADB wire protocol (CNXN/AUTH/OPEN/.../SYNC)    ┌──────┐
│ Library/CLI  │ ─────────────────────────────────────────────────▶│ adbd │
└──────────────┘          over USB or TCP (direct)                 └──────┘
```

Consequences of the direct-write design (vs. official `adb`):

| Official `adb` feature | Direct-write behavior here |
|---|---|
| `adb start-server` / `adb kill-server` | Not applicable — no server to start or kill. |
| `-H` / `-P` point at the adb **server** (5037) | `-H` / `-P` point **directly at an `adbd` endpoint** (e.g. `-H 127.0.0.1 -P 16416`). |
| `adb connect host:port` registers the device with the server | `connect` validates the endpoint and stores it (see CLI below); every command then targets it directly. |
| `adb forward` (host-side listener) | Requires a persistent server process, so it is **not** supported in direct-write mode. Device-side `reverse` is supported (`reverse:` is handled by `adbd` itself). |
| `adb devices` lists everything the server knows | Lists USB devices found by enumeration, plus any endpoint saved via `connect`. |

### Self-contained transport layer

The transport layer is fully self-contained: it never shells out to an external
`adb` binary, never connects to an adb server, and never proxies through a third
process. Interaction with devices is performed by the library/CLI's own
capabilities:

| Transport | How it works |
|---|---|
| USB | `UsbManager` scans the host's USB ports directly (libusb / native API via `FirmwareKit.Comm`), claims the ADB interface (vendor class `0xFF`, subclass `0x42`, protocol `0x01`), and performs raw bulk read/write on it. |
| TCP | `AdbTcpTransport` opens a native `System.Net.Sockets.TcpClient` straight to the device's `adbd` port (equivalent of `adb connect host:port`). |
| UDP | `AdbMdnsDiscovery` performs mDNS service discovery (`_adb-tls-connect._tcp`, `_adb._tcp`) with a native `UdpClient` multicast socket — the `adb mdns services` equivalent, without external tooling. |

## Features

- Direct, self-contained transports: USB (direct port scan + interface takeover + raw bulk I/O), TCP (native `TcpClient`), UDP (native `UdpClient` mDNS discovery). No external `adb` binary, no `host:5037` server, no proxy process.
- Full ADB wire protocol: `CNXN` / `AUTH` / `OPEN` / `OKAY` / `WRTE` / `CLSE` / `SYNC`.
- RSA-SHA1 authentication with ADB-format public keys (2048-bit); reuses the user's `~/.android/adbkey` and every key in `$ADB_VENDOR_KEYS` (AOSP `get_vendor_keys()`), falling back to a freshly generated key.
- Multi-key rotation on AUTH token challenges (AOSP `NextKey()` semantics): each token is answered with the next key's signature before advertising the public key.
- Stream multiplexing (`AdbStream`) over a single transport.
- Shell v2 protocol (`shell,v2`): stdout / stderr / exit code streaming, PTY and TERM support.
- Sync protocol (`sync:`): push / pull / stat / list (v1 wire format, which `adbd` accepts even when `sendrecv_v2` is negotiated).
- Device services: `reboot:`, `remount:`, `root:`, `unroot:`, `usb:`, `tcpip:`, `getprop`.
- `adb`-compatible CLI (`adb devices`, `adb shell`, `adb push`, `adb pull`, ...) with matching parameters, interface, and return values.

## Libraries

| Project | Description |
|---------|-------------|
| FirmwareKit.Comm.ADB | Core ADB protocol library (netstandard2.0 / net8.0 / net10.0). |
| FirmwareKit.Comm.ADB.Cli | `adb`-compatible command-line tool. |
| FirmwareKit.Comm.ADB.Tests | Unit tests for the protocol core. |

## USB backend selection

The backend is selected per platform, aligned with Google adb's
`is_libusb_enabled` semantics: **macOS and other non-Windows platforms default
to libusb, Windows defaults to the native backend** (WinUSB on Windows, IOKit
on macOS, usbfs on Linux). The native backend acts as a fallback / enumeration
path elsewhere.

- Library: set `UsbManager.ForceLibUsb = true` to force libusb explicitly.
- CLI: pass `--libusb` to force libusb; without it, the platform default
  applies.
- Environment overrides (official adb semantics):
  - `ADB_LIBUSB=1` → libusb first, native fallback; `ADB_LIBUSB=0` → native only
  - `FIRMWAREKIT_USB_BACKEND=native|libusb|auto` → project-specific selector

<para>后端按平台选择，与谷歌 adb 的 <c>is_libusb_enabled</c> 语义对齐：
macOS 及其他非 Windows 平台默认 libusb，Windows 默认原生后端（Windows 用
WinUSB、macOS 用 IOKit、Linux 用 usbfs）。原生后端在其余平台仅作回退/枚举。
库侧可设 <see cref="UsbManager.ForceLibUsb"/> 为 true 强制 libusb；CLI 侧传
<c>--libusb</c> 强制，否则应用平台默认。环境变量覆盖遵循官方 adb 语义：
<c>ADB_LIBUSB=1</c> 优先 libusb（原生回退）、<c>ADB_LIBUSB=0</c> 仅原生；
<c>FIRMWAREKIT_USB_BACKEND=native|libusb|auto</c> 为项目专属选择器。</para>

## Quick Start

```csharp
using FirmwareKit.Comm.ADB;
using FirmwareKit.Comm.ADB.Backend.Usb;
using FirmwareKit.Comm.ADB.Protocol;
using FirmwareKit.Comm.ADB.Services;

// 1. Find a device (standard ADB USB interface).
var devices = UsbManager.GetAllDevices();
UsbDevice device = devices[0];

// 2. Connect and authenticate. AdbConnection owns the authentication list and
//    disposes every key in Dispose(); do not wrap it in `using` here.
var auth = AdbAuthentication.CreateNew();
var connection = new AdbConnection(device, auth);
connection.Connect();

// 3. Run a shell command.
var shell = new AdbShellClient(connection, "getprop ro.product.model");
var result = shell.Execute();
Console.WriteLine(System.Text.Encoding.UTF8.GetString(result.Stdout));
```

## CLI

```
adb devices          # list attached devices (platform default backend)
adb --libusb devices # list devices forcing the libusb backend
adb shell <cmd>      # run a remote command
adb push <l> <r>     # push a file
adb pull <r> [l]     # pull a file
adb logcat -d -t 5   # dump device logcat (arguments passed through)
adb reboot           # reboot the device
adb mdns services    # discover ADB devices on the LAN (native UDP mDNS)
adb version          # show version
```

Exit codes, parameters, and output formatting follow the stock `adb` tool.

## License

MIT
