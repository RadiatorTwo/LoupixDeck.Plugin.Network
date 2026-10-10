namespace LoupixDeck.Plugin.Network;

/// <summary>The normalized state of a connection profile or a device.</summary>
public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Disconnecting,
    Failed,
    Unavailable
}

/// <summary>Texts, glyphs and backend mappings for <see cref="ConnectionState"/>.</summary>
internal static class ConnectionStates
{
    /// <summary>The English text a button shows for a state; the host translates it.</summary>
    public static string ToEnglishText(ConnectionState state) => state switch
    {
        ConnectionState.Connected => "Connected",
        ConnectionState.Connecting => "Connecting",
        ConnectionState.Disconnecting => "Disconnecting",
        ConnectionState.Failed => "Failed",
        ConnectionState.Unavailable => "Unavailable",
        _ => "Disconnected"
    };

    /// <summary>A single character that reads at a glance on a small key.</summary>
    public static string Glyph(ConnectionState state) => state switch
    {
        ConnectionState.Connected => "●",
        ConnectionState.Connecting or ConnectionState.Disconnecting => "◐",
        ConnectionState.Failed => "✕",
        ConnectionState.Unavailable => "–",
        _ => "○"
    };

    /// <summary>
    /// The button state a slot shows. The button states are the five from the issue, so a profile
    /// on its way down reads as disconnected rather than as connecting.
    /// </summary>
    public static string ToButtonState(ConnectionState state) => state switch
    {
        ConnectionState.Connected => "Connected",
        ConnectionState.Connecting => "Connecting",
        ConnectionState.Failed => "Failed",
        ConnectionState.Unavailable => "Unavailable",
        _ => "Disconnected"
    };

    /// <summary>NMActiveConnectionState: unknown, activating, activated, deactivating, deactivated.</summary>
    public static ConnectionState FromActiveConnectionState(uint state) => state switch
    {
        1 => ConnectionState.Connecting,
        2 => ConnectionState.Connected,
        3 => ConnectionState.Disconnecting,
        _ => ConnectionState.Disconnected
    };

    /// <summary>NMDeviceState, which counts in steps of ten from unmanaged (10) to failed (120).</summary>
    public static ConnectionState FromDeviceState(uint state) => state switch
    {
        < 30 => ConnectionState.Unavailable,
        30 => ConnectionState.Disconnected,
        < 100 => ConnectionState.Connecting,
        100 => ConnectionState.Connected,
        110 => ConnectionState.Disconnecting,
        120 => ConnectionState.Failed,
        _ => ConnectionState.Disconnected
    };

    /// <summary>NMState: unknown, asleep, disconnected, disconnecting, connecting, local, site, global.</summary>
    public static NetworkOverallState FromManagerState(uint state) => state switch
    {
        10 => NetworkOverallState.Asleep,
        20 => NetworkOverallState.Disconnected,
        30 => NetworkOverallState.Disconnecting,
        40 => NetworkOverallState.Connecting,
        50 => NetworkOverallState.ConnectedLocal,
        60 => NetworkOverallState.ConnectedSite,
        70 => NetworkOverallState.Connected,
        _ => NetworkOverallState.Unknown
    };

    /// <summary>NMConnectivityState: unknown, none, portal, limited, full.</summary>
    public static NetworkConnectivity FromConnectivity(uint state) => state switch
    {
        1 => NetworkConnectivity.None,
        2 => NetworkConnectivity.Portal,
        3 => NetworkConnectivity.Limited,
        4 => NetworkConnectivity.Full,
        _ => NetworkConnectivity.Unknown
    };

    public static string ToEnglishText(NetworkOverallState state) => state switch
    {
        NetworkOverallState.Asleep => "Asleep",
        NetworkOverallState.Disconnected => "Disconnected",
        NetworkOverallState.Disconnecting => "Disconnecting",
        NetworkOverallState.Connecting => "Connecting",
        NetworkOverallState.ConnectedLocal => "Local only",
        NetworkOverallState.ConnectedSite => "Site only",
        NetworkOverallState.Connected => "Connected",
        _ => "Unknown"
    };

    public static string ToEnglishText(NetworkConnectivity connectivity) => connectivity switch
    {
        NetworkConnectivity.None => "No connectivity",
        NetworkConnectivity.Portal => "Captive portal",
        NetworkConnectivity.Limited => "Limited",
        NetworkConnectivity.Full => "Full",
        _ => "Unknown"
    };

    /// <summary>The profile kind a NetworkManager connection type maps to.</summary>
    public static NetworkConnectionKind KindFromType(string type) => type switch
    {
        "802-3-ethernet" => NetworkConnectionKind.Ethernet,
        "802-11-wireless" => NetworkConnectionKind.Wifi,
        "vpn" => NetworkConnectionKind.Vpn,
        "wireguard" => NetworkConnectionKind.WireGuard,
        _ => NetworkConnectionKind.Other
    };

    /// <summary>A short English name for a NetworkManager connection type.</summary>
    public static string TypeText(string type) => type switch
    {
        "802-3-ethernet" => "Ethernet",
        "802-11-wireless" => "Wi-Fi",
        "vpn" => "VPN",
        "wireguard" => "WireGuard",
        "bridge" => "Bridge",
        "bond" => "Bond",
        "vlan" => "VLAN",
        "bluetooth" => "Bluetooth",
        "gsm" or "cdma" => "Mobile",
        "tun" => "TUN",
        "dummy" => "Dummy",
        "loopback" => "Loopback",
        "" => "Unknown",
        _ => type
    };

    /// <summary>A short English name for an NMDeviceType.</summary>
    public static string DeviceTypeText(uint deviceType) => deviceType switch
    {
        1 => "Ethernet",
        2 => "Wi-Fi",
        5 => "Bluetooth",
        8 => "Mobile",
        10 => "Bond",
        11 => "VLAN",
        13 => "Bridge",
        16 => "TUN",
        22 => "Dummy",
        29 => "WireGuard",
        30 => "Wi-Fi P2P",
        32 => "Loopback",
        _ => "Other"
    };
}
