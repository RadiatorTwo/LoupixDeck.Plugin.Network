using System.Globalization;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Network;

/// <summary>
/// A touch button that shows one live value. <see cref="GetText"/> is polled on a timer and must
/// not wait for D-Bus, so every value comes from the cached snapshot. A press toggles the profile
/// the button names, if it names one.
/// </summary>
internal sealed class NetworkDisplayCommand(
    CommandDescriptor descriptor,
    Func<NetworkDisplayContext, string> format,
    NetworkStateStore store,
    NetworkSettings settings) : ConnectionCommandBase(descriptor, store, settings), IDisplayCommand
{
    public override ButtonTargets SupportedTargets => ButtonTargets.TouchButton;

    /// <summary>Changes are pushed; the poll only covers the short window before the first read.</summary>
    public TimeSpan UpdateInterval => TimeSpan.FromSeconds(2);

    private bool TakesProfile => Descriptor.Parameters.Any(parameter => parameter.Name == NetworkCommands.ConnectionParameter);

    public string GetText(CommandContext ctx)
    {
        NetworkSnapshot snapshot = Store.Current;

        if (!snapshot.IsAvailable)
        {
            return ctx.Host.Tr(NetworkErrors.ToEnglishText(NetworkCallOutcome.Unavailable));
        }

        return format(new NetworkDisplayContext(ctx, snapshot, Store, Settings));
    }

    protected override Task ExecuteCore(CommandContext ctx)
    {
        if (!TakesProfile)
        {
            return Task.CompletedTask;
        }

        string? profileId = ResolveProfileId(ctx);
        bool up = Store.StateOf(profileId) is ConnectionState.Connected or ConnectionState.Connecting;
        return RunAsync(ctx, profileId, activate: !up);
    }
}

/// <summary>Everything a display formatter needs, resolved once per render.</summary>
internal sealed record NetworkDisplayContext(
    CommandContext Ctx,
    NetworkSnapshot Snapshot,
    NetworkStateStore Store,
    NetworkSettings Settings)
{
    public string Tr(string english) => Ctx.Host.Tr(english);

    /// <summary>
    /// The device a device display shows: the button's Device parameter, then the interface from
    /// the settings, then the device of the primary connection.
    /// </summary>
    public NetworkDevice? Device
    {
        get
        {
            string? requested = NetworkCommands.ReadParameter(Ctx, 0);

            if (requested is null && Settings.PrimaryInterface.Length > 0)
            {
                requested = Settings.PrimaryInterface;
            }

            if (requested is not null)
            {
                return Snapshot.FindDevice(requested);
            }

            return Snapshot.Primary is { Interfaces.Count: > 0 } primary
                ? Snapshot.FindDevice(primary.Interfaces[0])
                : null;
        }
    }

    /// <summary>
    /// The addresses an address display shows. Without an explicit device they come from the primary
    /// connection, which for a VPN are the tunnel's addresses rather than the uplink's.
    /// </summary>
    public NetworkAddresses Addresses
    {
        get
        {
            bool explicitDevice = NetworkCommands.ReadParameter(Ctx, 0) is not null || Settings.PrimaryInterface.Length > 0;

            if (!explicitDevice && Snapshot.Primary is { } primary)
            {
                return primary.Addresses;
            }

            return Device?.Addresses ?? NetworkAddresses.Empty;
        }
    }

    /// <summary>The profile a profile display shows: its parameter, or the first favorite.</summary>
    public NetworkProfile? Profile =>
        NetworkSettings.Resolve(NetworkCommands.ReadParameter(Ctx, 0) ?? Settings.FavoriteAt(0), Snapshot);
}

/// <summary>Builds the display commands the plugin publishes.</summary>
internal static class NetworkDisplayCommands
{
    /// <summary>The states of the VPN Connected button. Persisted in the button config, never rename.</summary>
    public static readonly IReadOnlyList<ButtonStateDescriptor> VpnStates =
    [
        new ButtonStateDescriptor { Name = "No", Description = "No VPN is connected" },
        new ButtonStateDescriptor { Name = "Yes", Description = "At least one VPN is connected" },
        new ButtonStateDescriptor { Name = "Unavailable", Description = "NetworkManager is not available" }
    ];

    public const string Status = NetworkCommands.Prefix + "Status";
    public const string Connectivity = NetworkCommands.Prefix + "Connectivity";
    public const string PrimaryConnection = NetworkCommands.Prefix + "PrimaryConnection";
    public const string ActiveVpns = NetworkCommands.Prefix + "ActiveVpns";
    public const string VpnConnected = NetworkCommands.Prefix + "VpnConnected";
    public const string WifiSsid = NetworkCommands.Prefix + "WifiSsid";
    public const string WifiSignal = NetworkCommands.Prefix + "WifiSignal";
    public const string Interface = NetworkCommands.Prefix + "Interface";
    public const string Ipv4 = NetworkCommands.Prefix + "Ipv4";
    public const string Ipv6 = NetworkCommands.Prefix + "Ipv6";
    public const string Gateway = NetworkCommands.Prefix + "Gateway";
    public const string Dns = NetworkCommands.Prefix + "Dns";
    public const string ConnectionType = NetworkCommands.Prefix + "ConnectionType";
    public const string ProfileStatus = NetworkCommands.Prefix + "ProfileStatus";
    public const string ProfileActive = NetworkCommands.Prefix + "ProfileActive";

    /// <summary>The device displays, which take an optional Device parameter.</summary>
    public static readonly IReadOnlyList<(string CommandName, string DisplayName)> DeviceDisplays =
    [
        (WifiSsid, "Wi-Fi Network"),
        (WifiSignal, "Wi-Fi Signal"),
        (Interface, "Interface"),
        (Ipv4, "IPv4 Address"),
        (Ipv6, "IPv6 Address"),
        (Gateway, "Gateway"),
        (Dns, "DNS Servers")
    ];

    /// <summary>True for the displays that only make sense on a Wi-Fi device.</summary>
    public static bool IsWifiDisplay(string commandName) => commandName is WifiSsid or WifiSignal;

    public static IEnumerable<IPluginCommand> Create(NetworkStateStore store, NetworkSettings settings)
    {
        yield return Global(Status, "Network Status", "Show the overall network state", NetworkButtonLayouts.Status,
            d => d.Tr(ConnectionStates.ToEnglishText(d.Snapshot.State)), store, settings);

        yield return Global(Connectivity, "Connectivity", "Show how far the machine reaches: full, limited, captive portal or none", NetworkButtonLayouts.Connectivity,
            d => d.Tr(ConnectionStates.ToEnglishText(d.Snapshot.Connectivity)), store, settings);

        yield return Global(PrimaryConnection, "Primary Connection", "Show the connection that carries the default route", NetworkButtonLayouts.Primary,
            d => d.Snapshot.Primary?.Name ?? d.Tr("Not connected"), store, settings);

        yield return Global(ActiveVpns, "Active VPNs", "Show the names of the connected VPNs", NetworkButtonLayouts.Vpn,
            FormatActiveVpns, store, settings);

        yield return Global(VpnConnected, "VPN Connected", "Show whether a VPN is connected. The button follows that state.", NetworkButtonLayouts.Vpn,
            d => d.Tr(d.Snapshot.ActiveConnections.Any(active => active.IsVpn && active.State == ConnectionState.Connected) ? "Yes" : "No"),
            store, settings, VpnStates);

        yield return Global(ConnectionType, "Connection Type", "Show the type of the primary connection, for example Ethernet, Wi-Fi or VPN", NetworkButtonLayouts.ConnectionType,
            d => d.Snapshot.Primary is { } primary ? d.Tr(ConnectionStates.TypeText(primary.Type)) : d.Tr("Not connected"), store, settings);

        yield return Device(WifiSsid, "Wi-Fi Network", "Show the name of the Wi-Fi network", NetworkButtonLayouts.Ssid,
            d => WifiOf(d)?.Ssid is { Length: > 0 } ssid ? ssid : d.Tr("Not connected"), store, settings);

        yield return Device(WifiSignal, "Wi-Fi Signal", "Show the Wi-Fi signal strength", NetworkButtonLayouts.Signal,
            d => WifiOf(d) is { } wifi ? $"{SignalBars(wifi.Strength)} {wifi.Strength.ToString(CultureInfo.InvariantCulture)}%" : d.Tr("Not connected"),
            store, settings);

        yield return Device(Interface, "Interface", "Show the network interface in use", NetworkButtonLayouts.Interface,
            d => d.Device?.Interface ?? d.Tr("None"), store, settings);

        yield return Device(Ipv4, "IPv4 Address", "Show the IPv4 address", NetworkButtonLayouts.Ipv4,
            d => Addresses(d, d.Addresses.Ipv4), store, settings);

        yield return Device(Ipv6, "IPv6 Address", "Show the IPv6 address", NetworkButtonLayouts.Ipv6,
            d => Addresses(d, d.Addresses.Ipv6), store, settings);

        yield return Device(Gateway, "Gateway", "Show the default gateway", NetworkButtonLayouts.Gateway,
            d => Addresses(d, [.. new[] { d.Addresses.Gateway4, d.Addresses.Gateway6 }.Where(gateway => gateway.Length > 0)]), store, settings);

        yield return Device(Dns, "DNS Servers", "Show the DNS servers", NetworkButtonLayouts.Dns,
            d => Addresses(d, d.Addresses.Dns), store, settings);

        yield return new NetworkDisplayCommand(
            NetworkCommands.ProfileDescriptor(ProfileStatus, "Connection Status",
                "Show the connection's name, state and address. A press toggles the connection.", NetworkButtonLayouts.Status),
            FormatProfileStatus, store, settings);

        yield return new NetworkDisplayCommand(
            NetworkCommands.ProfileDescriptor(ProfileActive, "Connection Active",
                "Show whether the connection is up. A press toggles the connection.", NetworkButtonLayouts.ProfileActive),
            d => d.Profile is { } profile
                ? d.Tr(d.Store.StateOf(profile.StableId, d.Snapshot) == ConnectionState.Connected ? "Yes" : "No")
                : d.Tr("Connection not found"),
            store, settings);
    }

    /// <summary>"Mullvad / ● Connected / 10.64.12.8", the example from the issue.</summary>
    private static string FormatProfileStatus(NetworkDisplayContext d)
    {
        if (d.Profile is not { } profile)
        {
            return d.Tr("Connection not found");
        }

        ConnectionState state = d.Store.StateOf(profile.StableId, d.Snapshot);
        List<string> lines = [profile.Name, $"{ConnectionStates.Glyph(state)} {d.Tr(ConnectionStates.ToEnglishText(state))}"];

        if (d.Settings.ShowIpAddresses && d.Snapshot.FindActive(profile.StableId) is { Addresses.Ipv4.Count: > 0 } active)
        {
            lines.Add(active.Addresses.Ipv4[0]);
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatActiveVpns(NetworkDisplayContext d)
    {
        List<string> names = [.. d.Snapshot.ActiveConnections.Where(active => active.IsVpn).Select(active => active.Name)];
        return names.Count > 0 ? string.Join(Environment.NewLine, names) : d.Tr("None");
    }

    /// <summary>The Wi-Fi of the display's device, or of the first associated Wi-Fi device.</summary>
    private static WifiInfo? WifiOf(NetworkDisplayContext d) =>
        d.Device?.Wifi ?? d.Snapshot.Devices.FirstOrDefault(device => device.Wifi is not null)?.Wifi;

    /// <summary>Addresses are the one value the user may want off the deck, for screen sharing.</summary>
    private static string Addresses(NetworkDisplayContext d, IReadOnlyList<string> addresses)
    {
        if (!d.Settings.ShowIpAddresses)
        {
            return d.Tr("Hidden");
        }

        return addresses.Count > 0 ? string.Join(Environment.NewLine, addresses.Take(2)) : d.Tr("None");
    }

    private static string SignalBars(int strength) => strength switch
    {
        >= 75 => "▂▄▆█",
        >= 50 => "▂▄▆",
        >= 25 => "▂▄",
        _ => "▂"
    };

    private static NetworkDisplayCommand Global(
        string commandName,
        string displayName,
        string description,
        string glyph,
        Func<NetworkDisplayContext, string> format,
        NetworkStateStore store,
        NetworkSettings settings,
        IReadOnlyList<ButtonStateDescriptor>? states = null)
    {
        CommandDescriptor descriptor = new()
        {
            CommandName = commandName,
            DisplayName = displayName,
            Group = NetworkCommands.Group,
            Icon = glyph,
            ButtonLayout = NetworkButtonLayouts.IconWithCaption(glyph, caption: null, tall: true),
            Description = description,
            States = states ?? []
        };

        return new NetworkDisplayCommand(descriptor, format, store, settings);
    }

    /// <summary>A display about one device. Without a Device parameter it follows the primary connection.</summary>
    private static NetworkDisplayCommand Device(
        string commandName,
        string displayName,
        string description,
        string glyph,
        Func<NetworkDisplayContext, string> format,
        NetworkStateStore store,
        NetworkSettings settings)
    {
        CommandDescriptor descriptor = new()
        {
            CommandName = commandName,
            DisplayName = displayName,
            Group = NetworkCommands.Group,
            Icon = glyph,
            ButtonLayout = NetworkButtonLayouts.IconWithCaption(glyph, caption: null, tall: true),
            Description = description + ". Leave the device empty to follow the primary connection.",
            ParameterTemplate = NetworkCommands.DeviceTemplate,
            Parameters = [new CommandParameter(NetworkCommands.DeviceParameter, typeof(string))]
        };

        return new NetworkDisplayCommand(descriptor, format, store, settings);
    }
}
