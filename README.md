# LoupixDeck.Plugin.Network

Controls network connections, Wi-Fi and VPNs from a Loupedeck device. On Linux it talks to
`org.freedesktop.NetworkManager` over D-Bus directly. It never runs `nmcli`, never stores or logs
credentials and only uses the connection profiles that already exist in NetworkManager.

Linux only for now. It needs NetworkManager and LoupixDeck with Plugin SDK 1.31 or newer. The
plugin is built on a platform-neutral backend interface, so a Windows backend can be added later
without changing the commands.

## What it does

- **Connections:** activate, deactivate or toggle any existing profile. A button waits for
  NetworkManager to report the result, so it shows what actually happened, not just that the
  request was accepted. A second press while a connection is still coming up cancels it.
- **VPN:** connect or disconnect a VPN or WireGuard profile. Without a profile, **Connect VPN**
  uses the preferred VPN from the settings and **Disconnect VPN** takes down every active VPN.
- **Wi-Fi and networking:** switch them on, off, or toggle. Both buttons follow the real state
  through the button states `Off`, `On` and `Unavailable`.
- **Devices:** connect or disconnect a single interface, as `nmcli device connect` does.
- **Live values:**
  - overall state, connectivity, primary connection and connection type
  - active VPNs and *VPN connected* (with the button states `No`, `Yes` and `Unavailable`)
  - Wi-Fi network and signal strength, interface
  - IPv4 and IPv6 addresses, gateway and DNS servers
  - per profile: **Connection Status** (name, state, address) and **Connection Active**
- **Connection slots:** ten favorite slots whose button state follows the connection:
  `Disconnected`, `Connecting`, `Connected`, `Failed` and `Unavailable`. Each state can have its own
  design.
- **Connections folder:** a touch folder that lists the known profiles. It highlights active ones,
  labels VPNs and shows the Wi-Fi signal. A press toggles the profile.

Everything updates from NetworkManager's D-Bus signals. Bursts of changes are combined into a
single redraw, and the plugin rebuilds its state by itself after NetworkManager restarts.

## Choosing a connection

Buttons store a profile's **UUID**, never its name or D-Bus path, so renaming a profile does not
break a button. The command menu goes from type to profile to action:

```text
Network
  Connections
    VPN (1)
      Mullvad · VPN · wg0 · Connected
        Mullvad — Toggle
        Mullvad — Connect VPN
        …
```

The profile entry shows the name, type, interface and current state. Each leaf is named
`profile — action`, and a button dropped from it uses that as its caption. Every profile also
offers **Add to Favorites**, which is how you fill the connection slots without typing UUIDs.
**Connection Slots** in the menu lists the slots with the profile each one currently shows.

## Why slots for the button states

The host binds button states to a command name, not to its parameters. If two **Toggle
Connection** buttons for different profiles carried states, they would share one state. Each
slot is therefore a command of its own (`Network.Connection01` … `Network.Connection10`) that
takes its profile from the favorites list.

## Permissions

The plugin never asks for an authorization. It calls NetworkManager without the
interactive-authorization flag, so PolicyKit refuses instead of opening a prompt. In that case the
button shows `Not allowed` and the connection's own state is left as it is. Profiles that belong
to the user, and the default rules of most desktop distributions, need no extra permission.

## Not included

Entering Wi-Fi passwords, creating or importing profiles (including WireGuard configurations),
802.1X, hotspots and firewall rules are not included. Profiles stay under NetworkManager's
control.

## Build

```bash
dotnet build -c Release
```

Copy `LoupixDeck.Plugin.Network.dll`, `Tmds.DBus.Protocol.dll`,
`LoupixDeck.Plugin.Network.deps.json` and the `strings.*.json` files from `bin/Release/`, plus
`plugin.json` from the repository root, into `<LoupixDeck>/plugins/network/`.

## Settings

| Setting | Meaning |
|---|---|
| Favorite connections | Comma-separated list of the profiles behind the connection slots, in slot order. Names are replaced by UUIDs when saved. |
| Hidden connections | Profiles kept out of the menu and the folder |
| Preferred VPN | The VPN **Connect VPN** uses when its button names none |
| Primary interface | The interface the address and Wi-Fi displays show; empty means they follow the primary connection |
| Show IP addresses | Off hides addresses, gateways and DNS servers on the deck |
| When the connection is already active | `nothing` or `reactivate` |
| Activation timeout | How long a button waits for a connection (1000–120000 ms) |
| Keep the error state | On: `Failed` stays until the next action. Off: it shows briefly, then falls back to the real state. |
| D-Bus timeout | How long one call to NetworkManager may take (250–10000 ms) |

**List connections** prints every profile with its UUID. **Check NetworkManager** shows the
overall state and connectivity.
