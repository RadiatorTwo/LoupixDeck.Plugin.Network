namespace LoupixDeck.Plugin.Network;

/// <summary>What a connection profile connects through, in platform-neutral terms.</summary>
public enum NetworkConnectionKind
{
    Ethernet,
    Wifi,
    Vpn,
    WireGuard,
    Other
}

/// <summary>The overall state of the machine's networking.</summary>
public enum NetworkOverallState
{
    Unknown,
    Asleep,
    Disconnected,
    Disconnecting,
    Connecting,
    ConnectedLocal,
    ConnectedSite,
    Connected
}

/// <summary>How far the machine reaches, as the backend's connectivity check sees it.</summary>
public enum NetworkConnectivity
{
    Unknown,
    None,
    Portal,
    Limited,
    Full
}

/// <summary>
/// A stored connection profile. <see cref="StableId"/> is what buttons and settings persist: names
/// can be renamed and backend object paths change on every restart, the id does not. For
/// NetworkManager it is the profile UUID.
/// </summary>
public sealed record NetworkProfile(
    string StableId,
    string Name,
    string Type,
    string InterfaceName,
    NetworkConnectionKind Kind)
{
    public bool IsVpn => Kind is NetworkConnectionKind.Vpn or NetworkConnectionKind.WireGuard;
}

/// <summary>Addresses, gateways and name servers of a device or an active connection.</summary>
public sealed record NetworkAddresses(
    IReadOnlyList<string> Ipv4,
    IReadOnlyList<string> Ipv6,
    string Gateway4,
    string Gateway6,
    IReadOnlyList<string> Dns)
{
    public static NetworkAddresses Empty { get; } = new([], [], string.Empty, string.Empty, []);
}

/// <summary>The access point a Wi-Fi device is associated with.</summary>
public sealed record WifiInfo(string Ssid, int Strength);

/// <summary>A network interface the backend manages.</summary>
public sealed record NetworkDevice(
    string Interface,
    string Type,
    ConnectionState State,
    string? ActiveProfileId,
    WifiInfo? Wifi,
    NetworkAddresses Addresses);

/// <summary>A profile that is currently up, or on its way up or down.</summary>
public sealed record ActiveNetworkConnection(
    string ProfileId,
    string Name,
    string Type,
    NetworkConnectionKind Kind,
    ConnectionState State,
    IReadOnlyList<string> Interfaces,
    bool IsDefault,
    NetworkAddresses Addresses)
{
    public bool IsVpn => Kind is NetworkConnectionKind.Vpn or NetworkConnectionKind.WireGuard;
}

/// <summary>
/// One consistent picture of the backend's state. It is replaced as a whole on every change, so a
/// reader never sees half of an update.
/// </summary>
public sealed record NetworkSnapshot(
    bool IsAvailable,
    NetworkOverallState State,
    NetworkConnectivity Connectivity,
    bool NetworkingEnabled,
    bool WifiEnabled,
    bool WifiHardwareEnabled,
    string? PrimaryProfileId,
    IReadOnlyList<NetworkProfile> Profiles,
    IReadOnlyList<NetworkDevice> Devices,
    IReadOnlyList<ActiveNetworkConnection> ActiveConnections)
{
    public static NetworkSnapshot Unavailable { get; } =
        new(false, NetworkOverallState.Unknown, NetworkConnectivity.Unknown, false, false, false, null, [], [], []);

    public NetworkProfile? FindProfile(string id) =>
        Profiles.FirstOrDefault(profile => string.Equals(profile.StableId, id, StringComparison.OrdinalIgnoreCase));

    public ActiveNetworkConnection? FindActive(string profileId) =>
        ActiveConnections.FirstOrDefault(active => string.Equals(active.ProfileId, profileId, StringComparison.OrdinalIgnoreCase));

    public NetworkDevice? FindDevice(string interfaceName) =>
        Devices.FirstOrDefault(device => string.Equals(device.Interface, interfaceName, StringComparison.Ordinal));

    public ActiveNetworkConnection? Primary =>
        PrimaryProfileId is { } id ? FindActive(id) : ActiveConnections.FirstOrDefault(active => active.IsDefault);
}
