# Harmony Remote

A system-tray remote for Logitech Harmony hubs on Linux. It talks to the hub directly over your local network, so
there's no Logitech account, cloud service or phone involved. Click the tray icon and the remote pops up.

## Features

- **Activities:** start any activity, see which one is running, and turn everything off.
- **Devices:** pick any device on the hub and use all of its keys: power, inputs, menus and the rest.
- **Remote layout:** volume and channel rockers, a d-pad with OK, menu keys and playback keys.
  Every other key the hub knows is one click away.
- **Hold to repeat:** holding a key repeats it, like the physical remote.
- **Tray menu:** right-click to start an activity, turn everything off, or quit.
- **Finding the hub:** search the network for hubs, or type the hub's IP address.
- **Reconnects** on its own if the hub restarts or the network drops.
- **Start at login:** optional, from the tray menu or settings.

## Requirements

- A Logitech Harmony Hub with recent firmware. It was tested on 4.15.600, the final release.
- A Linux desktop with a StatusNotifierItem system tray.
  - Tested on KDE Plasma 6 on Wayland. The app runs through XWayland.
  - Other desktops with a tray should work.
  - GNOME needs the *AppIndicator and KStatusNotifierItem Support* extension.
- To build: the [.NET 10 SDK](https://dotnet.microsoft.com/download).

## Install

### Flatpak (recommended)

Each [release](https://github.com/mprovenz/harmony-remote/releases) has a `harmony-remote-<version>-x86_64.flatpak` bundle.
Download it and install it for your user:

```sh
flatpak install --user ./harmony-remote-<version>-x86_64.flatpak   # fetches the Freedesktop runtime from Flathub if needed
```

To build it yourself instead:

```sh
flatpak install flathub org.freedesktop.Platform//26.08 org.freedesktop.Sdk//26.08 \
    org.freedesktop.Sdk.Extension.dotnet10//26.08      # once
packaging/flatpak/build.sh                              # build + install for your user
flatpak run io.github.mprovenz.HarmonyRemote
```

`build.sh --bundle` also writes a single-file `harmony-remote.flatpak` you can copy to another machine.

### Without Flatpak

```sh
scripts/install.sh              # publishes to ~/.local/share/harmony-remote, adds a menu entry and icon
scripts/install.sh --uninstall
```

Or run it straight from the source tree:

```sh
dotnet run --project src/HarmonyRemote.App -- --show
```

## Usage

On first start the remote opens on its settings. Enter the hub's IP address, or press **Find hubs on this network**.

- **Left-click** the tray icon to open or close the remote. Click elsewhere or press Esc to close it.
- **Right-click** the tray icon for the menu.
- Launching the app again opens the remote of the copy that's already running.
- `--show` opens the remote at startup.

Settings are saved in `~/.config/harmony-remote/settings.json`. Inside Flatpak they're under
`~/.var/app/io.github.mprovenz.HarmonyRemote/config/harmony-remote/`.

### Network notes

- **Searching** sends a UDP probe to port 5224. The hub then connects back to the app on TCP port 5446.
  - A broadcast only reaches your own subnet. If the hub is on another subnet, type its IP address instead.
  - A firewall blocking inbound TCP 5446 stops the search from working.
- **Connecting** by IP address only makes outgoing connections to the hub on port 8088 (HTTP and WebSocket),
  so no inbound ports are needed.

## How it works

Harmony Hubs serve a local WebSocket API on port 8088, the same one the official app uses on your home network.
Logitech never documented it. The client here was written from observing the official Android app and a real hub's traffic.

## Development

| Path | What |
|---|---|
| `src/HarmonyRemote.Core` | The hub protocol: discovery, connecting by IP, the WebSocket client, config/state models, and `HubSession` (reconnects, activities, keys). No UI dependency. |
| `src/HarmonyRemote.App` | The Avalonia 12 tray app: tray icon and menu, popup remote, settings, single instance |
| `tests/HarmonyRemote.Core.Tests` | xUnit tests, with a loopback fake hub |
| `packaging/flatpak` | Flatpak manifest, desktop entry, metainfo, offline NuGet sources and `build.sh` |

```sh
dotnet test                                      # offline
HARMONY_LIVE_HUB=192.168.1.20 dotnet test        # also runs read-only checks against your hub
```

After changing NuGet package references, regenerate the Flatpak's offline sources with `packaging/flatpak/build.sh --sources`.

### CI and releases

[`.github/workflows/flatpak.yml`](.github/workflows/flatpak.yml) runs on every push to `main` and on every pull request.
It runs the tests and then builds the Flatpak. Each run's bundle is attached to it under **Artifacts**.

To release:

1. Bump `<Version>` in `src/HarmonyRemote.App/HarmonyRemote.App.csproj`.
2. Add a `<release>` entry to the metainfo file in `packaging/flatpak/`.
3. Push a tag such as `v0.2.0`.

The workflow then creates a **draft** release with the bundle attached. Check it, then publish it from the Releases page.

## License

Copyright © 2026 mprovenz

Harmony Remote is free software: you can redistribute it and/or modify it under the terms of the
[GNU General Public License](LICENSE) as published by the Free Software Foundation, either version 3 of the License,
or (at your option) any later version. It is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY;
see the license for details.

Bundled third-party files keep their own licences:
- The Barlow font in `src/HarmonyRemote.App/Assets/Fonts` is under the SIL Open Font License (`OFL.txt` there).
- `packaging/flatpak/flatpak-dotnet-generator.py` is vendored from
  [flatpak-builder-tools](https://github.com/flatpak/flatpak-builder-tools) (MIT).

---

Not affiliated with or endorsed by Logitech. Logitech and Harmony are trademarks of Logitech.
