# Huawei E303 Modem Manager

A Windows desktop companion for a Huawei E303 USB modem: make voice calls, send SMS, dial USSD codes, manage SIM contacts, and control mobile data from one place.

[![Build](https://github.com/razibit/huawei-e303-modem-manager/actions/workflows/build.yml/badge.svg)](https://github.com/razibit/huawei-e303-modem-manager/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)
[![Windows](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D6)](https://github.com/razibit/huawei-e303-modem-manager/releases)

> Community project, unaffiliated with Huawei, Grameenphone, or Banglalink. Device features depend on firmware, drivers, SIM, and network.

## Features

- **Voice calls:** dial, answer, reject and hang up; select a microphone and speakers; keep local call history.
- **SMS:** read, send, reply and delete messages on the SIM or modem, with Unicode and multipart support.
- **USSD:** send codes, reply to interactive menus and save shortcuts.
- **Contacts:** manage the SIM or device phonebook when exposed by the modem.
- **Internet:** connect with an APN, view signal and registration, scan networks, and inspect session traffic.
- **Device tools:** diagnostics and SIM security controls when supported.

## Get started

1. Install a working Huawei E303 Windows driver and connect the modem. Close any other modem manager using its ports.
2. Download the latest Windows release from [Releases](https://github.com/razibit/huawei-e303-modem-manager/releases). Install the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) if needed.
3. Run the app, select the modem control and PCM audio ports, and choose a microphone and speakers. Click **Check modem**.
4. Use the tabs for calls, SMS, USSD, contacts, internet and device settings. Enter your carrier APN for mobile data.

The E303 identifies itself through several USB interfaces; port numbers can differ across computers. Voice requires firmware that exposes PC voice audio. Calls, messages, USSD and data can incur carrier charges.

## Original GP files and local archive

The [v0.4.0 release](https://github.com/razibit/huawei-e303-modem-manager/releases/tag/v0.4.0) also provides `GP-E303-Voice-complete-archive.zip`, an exact snapshot of the original local GP-E303-Voice folder. It includes the Grameenphone Internet executable and its supporting files, drivers, documentation, original dialer builds, a community-hosted firmware candidate, and personal settings including call history. The file list and SHA-256 hashes are in [ARCHIVE-MANIFEST.sha256](ARCHIVE-MANIFEST.sha256).

The GP software, drivers, firmware and other bundled third-party components retain their respective owners' copyright and license terms; the project's MIT license applies to the project's own source. The firmware candidate is an unsigned, community-hosted package; see its included README for provenance. Its inclusion is not a recommendation to flash it.

On the original machine, `local-gp-e303-voice` contains the consolidated files. The separate Program Files installation and Windows drivers remain managed by Windows.

## Status and limitations

Outgoing calling was reported working on the original E303 setup. The expanded SMS, USSD, incoming-call, contacts and data controls were built but have not yet been verified end to end on physical hardware. Please report your modem revision, firmware and Windows version when sharing results. MMS, video calling, WLAN/VPN, SIM Toolkit and firmware updates are not implemented.

Messages and contacts are managed in modem/SIM storage. Audio choices, the last APN, USSD shortcuts and up to 300 call-history entries are stored in `dialer-settings.json` in the project root when the app runs within the source tree, or beside the executable for a standalone download. Put the app in a writable folder. The project owner's historical settings and call history were deliberately included in the public archive. PINs and PUKs are not intentionally saved or logged by the app. The app must remain open to monitor incoming calls and messages.

## Build from source

Install the .NET 8 SDK and run in PowerShell:

```powershell
dotnet restore
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained false -o publish
```

The app uses WinForms, System.IO.Ports and NAudio. See [CONTRIBUTING.md](CONTRIBUTING.md) for development and [SECURITY.md](SECURITY.md) for reporting security issues. Historical binaries are provided for reference only; their matching source revisions were not retained.

## License

[MIT](LICENSE). Third-party dependencies retain their own licenses.
