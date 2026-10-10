using System.Collections.Concurrent;
using System.Net;
using System.Text;
using LoupixDeck.PluginSdk;
using Tmds.DBus.Protocol;

namespace LoupixDeck.Plugin.Network;

/// <summary>
/// The Linux backend: NetworkManager over its D-Bus API on the system bus. It never starts nmcli,
/// never reads secrets and never asks for the applied connection — only the <c>connection</c>
/// section of each profile is read.
/// <para>
/// State is event-driven: one match rule covers every PropertiesChanged NetworkManager publishes,
/// plus the settings and activation signals. Signals only mark the snapshot dirty; a short debounce
/// coalesces a burst into one rebuild, so a reconnect does not repaint the deck a dozen times.
/// </para>
/// </summary>
internal sealed class NetworkManagerBackend : INetworkBackend
{
    private const string Service = NetworkManagerConnection.ManagerService;
    private const string ManagerPath = "/org/freedesktop/NetworkManager";
    private const string ManagerInterface = "org.freedesktop.NetworkManager";
    private const string SettingsPath = "/org/freedesktop/NetworkManager/Settings";
    private const string SettingsInterface = "org.freedesktop.NetworkManager.Settings";
    private const string ProfileInterface = "org.freedesktop.NetworkManager.Settings.Connection";
    private const string ActivePathNamespace = "/org/freedesktop/NetworkManager/ActiveConnection";
    private const string ActiveInterface = "org.freedesktop.NetworkManager.Connection.Active";
    private const string DeviceInterface = "org.freedesktop.NetworkManager.Device";
    private const string WirelessInterface = "org.freedesktop.NetworkManager.Device.Wireless";
    private const string AccessPointInterface = "org.freedesktop.NetworkManager.AccessPoint";
    private const string AccessPointPrefix = "/org/freedesktop/NetworkManager/AccessPoint/";
    private const string Ip4Interface = "org.freedesktop.NetworkManager.IP4Config";
    private const string Ip6Interface = "org.freedesktop.NetworkManager.IP6Config";
    private const string NoObject = "/";

    private const uint WifiDeviceType = 2;
    private const uint WifiP2PDeviceType = 30;
    private const uint LoopbackDeviceType = 32;
    private const uint ActiveStateDeactivated = 4;

    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(150);

    private readonly NetworkManagerConnection _connection;
    private readonly IPluginLogger _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly Timer _debounce;
    private readonly List<IDisposable> _watches = [];
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Active connection paths returned by ActivateConnection that no snapshot has seen yet.</summary>
    private readonly ConcurrentDictionary<string, string> _pendingActivations = new(StringComparer.Ordinal);

    /// <summary>The profiles by settings path. Only touched while <see cref="_refreshLock"/> is held.</summary>
    private Dictionary<string, NetworkProfile> _profilesByPath = new(StringComparer.Ordinal);

    private volatile NetworkSnapshot _current = NetworkSnapshot.Unavailable;
    private volatile ObjectIndex _index = ObjectIndex.Empty;
    private int _profilesDirty = 1;
    private int _timeoutMilliseconds = 2000;
    private bool _wasAvailable;
    private volatile bool _disposed;

    public NetworkManagerBackend(IPluginLogger logger)
    {
        _logger = logger;
        _connection = new NetworkManagerConnection(logger);
        _debounce = new Timer(_ => _ = RefreshAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public NetworkSnapshot Current => _current;

    public Task Ready => _ready.Task;

    public int CallTimeoutMilliseconds
    {
        get => _timeoutMilliseconds;
        set => _timeoutMilliseconds = value;
    }

    public event Action? Changed;

    public event Action<string, string>? ActivationFailed;

    public async Task StartAsync()
    {
        _connection.ConnectionReady += OnConnectionReady;
        _connection.ConnectionLost += OnConnectionLost;

        if (!await _connection.ConnectAsync().ConfigureAwait(false))
        {
            Publish(NetworkSnapshot.Unavailable, ObjectIndex.Empty);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _connection.ConnectionReady -= OnConnectionReady;
        _connection.ConnectionLost -= OnConnectionLost;
        _debounce.Dispose();
        DisposeWatches();
        _connection.Dispose();
    }

    public async Task<NetworkCallOutcome> ActivateAsync(string profileId)
    {
        DBusClient? client = GetClient();

        if (client is null)
        {
            return NetworkCallOutcome.Unavailable;
        }

        if (!_index.ProfilePaths.TryGetValue(profileId, out string? profilePath))
        {
            return NetworkCallOutcome.NotFound;
        }

        DBusResult<string> result = await client.CallAsync(
            Service,
            ManagerPath,
            ManagerInterface,
            "ActivateConnection",
            DBusClient.ReadObjectPath,
            string.Empty,
            "ooo",
            (ref MessageWriter writer) =>
            {
                writer.WriteObjectPath(profilePath);
                writer.WriteObjectPath(NoObject);
                writer.WriteObjectPath(NoObject);
            }).ConfigureAwait(false);

        if (result.IsSuccess && result.Value.Length > 0)
        {
            _pendingActivations[result.Value] = profileId;
        }

        ScheduleRefresh(profiles: false);
        return result.Outcome;
    }

    public async Task<NetworkCallOutcome> DeactivateAsync(string profileId)
    {
        DBusClient? client = GetClient();

        if (client is null)
        {
            return NetworkCallOutcome.Unavailable;
        }

        if (!_index.ActivePaths.TryGetValue(profileId, out IReadOnlyList<string>? activePaths))
        {
            // Nothing is up for this profile, which is what the caller asked for.
            return NetworkCallOutcome.Ok;
        }

        NetworkCallOutcome outcome = NetworkCallOutcome.Ok;

        foreach (string activePath in activePaths)
        {
            NetworkCallOutcome single = await client.CallAsync(
                Service,
                ManagerPath,
                ManagerInterface,
                "DeactivateConnection",
                "o",
                (ref MessageWriter writer) => writer.WriteObjectPath(activePath)).ConfigureAwait(false);

            if (single != NetworkCallOutcome.Ok)
            {
                outcome = single;
            }
        }

        ScheduleRefresh(profiles: false);
        return outcome;
    }

    public async Task<NetworkCallOutcome> SetWifiEnabledAsync(bool enabled)
    {
        DBusClient? client = GetClient();

        if (client is null)
        {
            return NetworkCallOutcome.Unavailable;
        }

        NetworkCallOutcome outcome = await client.CallAsync(
            Service,
            ManagerPath,
            DBusClient.PropertiesInterface,
            "Set",
            "ssv",
            (ref MessageWriter writer) =>
            {
                writer.WriteString(ManagerInterface);
                writer.WriteString("WirelessEnabled");
                writer.WriteVariantBool(enabled);
            }).ConfigureAwait(false);

        ScheduleRefresh(profiles: false);
        return outcome;
    }

    public async Task<NetworkCallOutcome> SetNetworkingEnabledAsync(bool enabled)
    {
        DBusClient? client = GetClient();

        if (client is null)
        {
            return NetworkCallOutcome.Unavailable;
        }

        NetworkCallOutcome outcome = await client.CallAsync(
            Service,
            ManagerPath,
            ManagerInterface,
            "Enable",
            "b",
            (ref MessageWriter writer) => writer.WriteBool(enabled)).ConfigureAwait(false);

        ScheduleRefresh(profiles: false);
        return outcome;
    }

    public async Task<NetworkCallOutcome> ConnectDeviceAsync(string interfaceName)
    {
        DBusClient? client = GetClient();

        if (client is null)
        {
            return NetworkCallOutcome.Unavailable;
        }

        if (!_index.DevicePaths.TryGetValue(interfaceName, out string? devicePath))
        {
            return NetworkCallOutcome.NotFound;
        }

        // No profile: NetworkManager picks the best one for the device, as "nmcli device connect" does.
        DBusResult<string> result = await client.CallAsync(
            Service,
            ManagerPath,
            ManagerInterface,
            "ActivateConnection",
            DBusClient.ReadObjectPath,
            string.Empty,
            "ooo",
            (ref MessageWriter writer) =>
            {
                writer.WriteObjectPath(NoObject);
                writer.WriteObjectPath(devicePath);
                writer.WriteObjectPath(NoObject);
            }).ConfigureAwait(false);

        ScheduleRefresh(profiles: false);
        return result.Outcome;
    }

    public async Task<NetworkCallOutcome> DisconnectDeviceAsync(string interfaceName)
    {
        DBusClient? client = GetClient();

        if (client is null)
        {
            return NetworkCallOutcome.Unavailable;
        }

        if (!_index.DevicePaths.TryGetValue(interfaceName, out string? devicePath))
        {
            return NetworkCallOutcome.NotFound;
        }

        NetworkCallOutcome outcome = await client.CallAsync(Service, devicePath, DeviceInterface, "Disconnect").ConfigureAwait(false);

        ScheduleRefresh(profiles: false);
        return outcome;
    }

    private DBusClient? GetClient()
    {
        DBusClient? client = _connection.Client;

        if (client is not null)
        {
            client.TimeoutMilliseconds = _timeoutMilliseconds;
        }

        return client;
    }

    private void OnConnectionReady()
    {
        // Subscribing talks to the bus, so it must not run on the read loop that raised this.
        _ = Task.Run(async () =>
        {
            await SubscribeAsync().ConfigureAwait(false);
            Interlocked.Exchange(ref _profilesDirty, 1);
            await RefreshAsync().ConfigureAwait(false);
        });
    }

    private void OnConnectionLost()
    {
        _pendingActivations.Clear();
        Publish(NetworkSnapshot.Unavailable, ObjectIndex.Empty);
    }

    private void ScheduleRefresh(bool profiles)
    {
        if (profiles)
        {
            Interlocked.Exchange(ref _profilesDirty, 1);
        }

        if (_disposed)
        {
            return;
        }

        try
        {
            _debounce.Change(Debounce, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Shutting down; nothing left to refresh.
        }
    }

    private async Task SubscribeAsync()
    {
        DBusClient? client = GetClient();

        if (client is null)
        {
            return;
        }

        await _refreshLock.WaitAsync().ConfigureAwait(false);
        try
        {
            DisposeWatches();

            // One rule for every NetworkManager object: devices, active connections, addresses and
            // access points all publish their changes through the standard properties interface.
            Add(await client.WatchMatchAsync(
                new MatchRule
                {
                    Type = MessageType.Signal,
                    Sender = Service,
                    PathNamespace = ManagerPath,
                    Interface = DBusClient.PropertiesInterface,
                    Member = "PropertiesChanged"
                },
                ReadPropertiesChanged,
                OnPropertiesChanged).ConfigureAwait(false));

            Add(await client.WatchSignalAsync(
                Service, SettingsPath, SettingsInterface, "NewConnection",
                DBusClient.ReadObjectPath,
                _ => ScheduleRefresh(profiles: true)).ConfigureAwait(false));

            Add(await client.WatchSignalAsync(
                Service, SettingsPath, SettingsInterface, "ConnectionRemoved",
                DBusClient.ReadObjectPath,
                _ => ScheduleRefresh(profiles: true)).ConfigureAwait(false));

            // A renamed profile only announces itself through Updated on its own object.
            Add(await client.WatchMatchAsync(
                new MatchRule
                {
                    Type = MessageType.Signal,
                    Sender = Service,
                    PathNamespace = SettingsPath,
                    Interface = ProfileInterface,
                    Member = "Updated"
                },
                ReadSignalPath,
                _ => ScheduleRefresh(profiles: true)).ConfigureAwait(false));

            // The reason a connection went down is only carried by this signal.
            Add(await client.WatchMatchAsync(
                new MatchRule
                {
                    Type = MessageType.Signal,
                    Sender = Service,
                    PathNamespace = ActivePathNamespace,
                    Interface = ActiveInterface,
                    Member = "StateChanged"
                },
                ReadActiveStateChanged,
                OnActiveStateChanged).ConfigureAwait(false));
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private void Add(IDisposable? watch)
    {
        if (watch is not null)
        {
            _watches.Add(watch);
        }
    }

    private void DisposeWatches()
    {
        foreach (IDisposable watch in _watches)
        {
            watch.Dispose();
        }

        _watches.Clear();
    }

    private void OnPropertiesChanged((string Path, string Interface) change)
    {
        // Every access point in range reports its signal strength; only the ones in use matter.
        if (change.Path.StartsWith(AccessPointPrefix, StringComparison.Ordinal) &&
            !_index.AccessPoints.Contains(change.Path))
        {
            return;
        }

        bool profiles = change.Interface is SettingsInterface or ProfileInterface;
        ScheduleRefresh(profiles);
    }

    private void OnActiveStateChanged((string Path, uint State, uint Reason) change)
    {
        if (change.State == ActiveStateDeactivated)
        {
            string? profileId = _index.ActiveProfiles.GetValueOrDefault(change.Path);

            if (_pendingActivations.TryRemove(change.Path, out string? pending))
            {
                profileId ??= pending;
            }

            if (profileId is not null && ActivationFailureReasons.TryGetValue(change.Reason, out string? reason))
            {
                _logger.Info($"Network: activation of {profileId} failed: {reason}.");
                RaiseActivationFailed(profileId, reason);
            }
        }

        ScheduleRefresh(profiles: false);
    }

    /// <summary>NMActiveConnectionStateReason values that mean the activation failed rather than was ended.</summary>
    private static readonly Dictionary<uint, string> ActivationFailureReasons = new()
    {
        [5] = "IP configuration failed",
        [6] = "Connection timed out",
        [7] = "VPN service did not start in time",
        [8] = "VPN service failed to start",
        [9] = "Secrets were required",
        [10] = "Login failed",
        [12] = "A dependency failed",
        [13] = "Device could not be created"
    };

    private async Task RefreshAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _refreshLock.WaitAsync().ConfigureAwait(false);
        try
        {
            DBusClient? client = GetClient();

            if (client is null)
            {
                Publish(NetworkSnapshot.Unavailable, ObjectIndex.Empty);
                return;
            }

            bool reloadProfiles = Interlocked.Exchange(ref _profilesDirty, 0) == 1;
            (NetworkSnapshot Snapshot, ObjectIndex Index)? built = await BuildAsync(client, reloadProfiles).ConfigureAwait(false);

            if (built is null)
            {
                // NetworkManager is not on the bus (yet). The profiles are read again once it is.
                Interlocked.Exchange(ref _profilesDirty, 1);
                Publish(NetworkSnapshot.Unavailable, ObjectIndex.Empty);
                return;
            }

            Publish(built.Value.Snapshot, built.Value.Index);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Network: reading the NetworkManager state failed: {ex.Message}");
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private void Publish(NetworkSnapshot snapshot, ObjectIndex index)
    {
        _index = index;
        _current = snapshot;
        _ready.TrySetResult();

        if (snapshot.IsAvailable != _wasAvailable)
        {
            _wasAvailable = snapshot.IsAvailable;
            _logger.Info(snapshot.IsAvailable
                ? $"Network: connected to NetworkManager ({snapshot.Profiles.Count} profiles, {snapshot.Devices.Count} devices)."
                : "Network: NetworkManager is unavailable.");
        }

        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.Warn($"Network: a change handler failed: {ex.Message}");
        }
    }

    private void RaiseActivationFailed(string profileId, string reason)
    {
        try
        {
            ActivationFailed?.Invoke(profileId, reason);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Network: a failure handler failed: {ex.Message}");
        }
    }

    private async Task<(NetworkSnapshot, ObjectIndex)?> BuildAsync(DBusClient client, bool reloadProfiles)
    {
        DBusResult<Dictionary<string, VariantValue>> root =
            await client.GetAllPropertiesAsync(Service, ManagerPath, ManagerInterface).ConfigureAwait(false);

        if (!root.IsSuccess)
        {
            return null;
        }

        if (reloadProfiles || _profilesByPath.Count == 0)
        {
            Dictionary<string, NetworkProfile>? profiles = await LoadProfilesAsync(client).ConfigureAwait(false);

            if (profiles is null)
            {
                return null;
            }

            _profilesByPath = profiles;
        }

        Dictionary<string, NetworkAddresses> addressCache = new(StringComparer.Ordinal);
        HashSet<string> accessPoints = new(StringComparer.Ordinal);

        // Devices first, because active connections name their devices by path.
        Dictionary<string, string> interfaceByDevicePath = new(StringComparer.Ordinal);
        List<(string Path, Dictionary<string, VariantValue> Properties, uint Type)> rawDevices = [];

        foreach (string devicePath in ReadPaths(root.Value, "Devices"))
        {
            DBusResult<Dictionary<string, VariantValue>> device =
                await client.GetAllPropertiesAsync(Service, devicePath, DeviceInterface).ConfigureAwait(false);

            if (!device.IsSuccess)
            {
                // A device that disappears between the listing and the read (a dock unplugged) is skipped.
                continue;
            }

            uint type = ReadUInt32(device.Value, "DeviceType");

            // Loopback and the Wi-Fi P2P helpers that shadow every Wi-Fi card are nothing to switch.
            if (type is LoopbackDeviceType or WifiP2PDeviceType)
            {
                continue;
            }

            interfaceByDevicePath[devicePath] = ReadString(device.Value, "Interface");
            rawDevices.Add((devicePath, device.Value, type));
        }

        Dictionary<string, string> profileByActivePath = new(StringComparer.Ordinal);
        Dictionary<string, List<string>> activePathsByProfile = new(StringComparer.OrdinalIgnoreCase);
        List<ActiveNetworkConnection> actives = [];

        foreach (string activePath in ReadPaths(root.Value, "ActiveConnections"))
        {
            DBusResult<Dictionary<string, VariantValue>> active =
                await client.GetAllPropertiesAsync(Service, activePath, ActiveInterface).ConfigureAwait(false);

            if (!active.IsSuccess)
            {
                continue;
            }

            Dictionary<string, VariantValue> properties = active.Value;
            string profileId = ReadString(properties, "Uuid");
            string type = ReadString(properties, "Type");

            if (profileId.Length == 0 || type == "loopback")
            {
                continue;
            }

            profileByActivePath[activePath] = profileId;
            _pendingActivations.TryRemove(activePath, out _);

            if (!activePathsByProfile.TryGetValue(profileId, out List<string>? paths))
            {
                paths = [];
                activePathsByProfile[profileId] = paths;
            }

            paths.Add(activePath);

            List<string> interfaces = [];
            foreach (string devicePath in ReadPaths(properties, "Devices"))
            {
                if (interfaceByDevicePath.TryGetValue(devicePath, out string? name))
                {
                    interfaces.Add(name);
                }
            }

            NetworkAddresses addresses = await ReadAddressesAsync(
                client,
                ReadPath(properties, "Ip4Config"),
                ReadPath(properties, "Ip6Config"),
                addressCache).ConfigureAwait(false);

            actives.Add(new ActiveNetworkConnection(
                profileId,
                ReadString(properties, "Id"),
                type,
                ConnectionStates.KindFromType(type),
                ConnectionStates.FromActiveConnectionState(ReadUInt32(properties, "State")),
                interfaces,
                ReadBool(properties, "Default") || ReadBool(properties, "Default6"),
                addresses));
        }

        List<NetworkDevice> devices = [];
        Dictionary<string, string> devicePathByInterface = new(StringComparer.Ordinal);

        foreach ((string devicePath, Dictionary<string, VariantValue> properties, uint type) in rawDevices)
        {
            string interfaceName = interfaceByDevicePath[devicePath];
            devicePathByInterface[interfaceName] = devicePath;

            WifiInfo? wifi = type == WifiDeviceType
                ? await ReadWifiAsync(client, devicePath, accessPoints).ConfigureAwait(false)
                : null;

            NetworkAddresses addresses = await ReadAddressesAsync(
                client,
                ReadPath(properties, "Ip4Config"),
                ReadPath(properties, "Ip6Config"),
                addressCache).ConfigureAwait(false);

            devices.Add(new NetworkDevice(
                interfaceName,
                ConnectionStates.DeviceTypeText(type),
                ConnectionStates.FromDeviceState(ReadUInt32(properties, "State")),
                profileByActivePath.GetValueOrDefault(ReadPath(properties, "ActiveConnection")),
                wifi,
                addresses));
        }

        List<NetworkProfile> profilesList = [.. _profilesByPath.Values.OrderBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase)];
        Dictionary<string, string> profilePaths = new(StringComparer.OrdinalIgnoreCase);

        foreach ((string path, NetworkProfile profile) in _profilesByPath)
        {
            profilePaths[profile.StableId] = path;
        }

        NetworkSnapshot snapshot = new(
            true,
            ConnectionStates.FromManagerState(ReadUInt32(root.Value, "State")),
            ConnectionStates.FromConnectivity(ReadUInt32(root.Value, "Connectivity")),
            ReadBool(root.Value, "NetworkingEnabled"),
            ReadBool(root.Value, "WirelessEnabled"),
            ReadBool(root.Value, "WirelessHardwareEnabled"),
            profileByActivePath.GetValueOrDefault(ReadPath(root.Value, "PrimaryConnection")),
            profilesList,
            devices,
            actives);

        ObjectIndex index = new(
            profilePaths,
            devicePathByInterface,
            activePathsByProfile.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value, StringComparer.OrdinalIgnoreCase),
            profileByActivePath,
            accessPoints);

        return (snapshot, index);
    }

    /// <summary>
    /// Lists the stored profiles. Only the <c>connection</c> section is kept: it carries the name, the
    /// UUID, the type and the interface, and nothing secret.
    /// </summary>
    private async Task<Dictionary<string, NetworkProfile>?> LoadProfilesAsync(DBusClient client)
    {
        DBusResult<string[]> list = await client.CallAsync(
            Service,
            SettingsPath,
            SettingsInterface,
            "ListConnections",
            ReadObjectPaths,
            []).ConfigureAwait(false);

        if (!list.IsSuccess)
        {
            return null;
        }

        Dictionary<string, NetworkProfile> profiles = new(StringComparer.Ordinal);

        foreach (string path in list.Value)
        {
            DBusResult<Dictionary<string, VariantValue>> settings = await client.CallAsync(
                Service,
                path,
                ProfileInterface,
                "GetSettings",
                ReadConnectionSection,
                []).ConfigureAwait(false);

            if (!settings.IsSuccess)
            {
                // Removed between the listing and the read, or not readable by this user.
                continue;
            }

            string id = ReadString(settings.Value, "uuid");
            string type = ReadString(settings.Value, "type");

            if (id.Length == 0 || type == "loopback")
            {
                continue;
            }

            profiles[path] = new NetworkProfile(
                id,
                ReadString(settings.Value, "id"),
                type,
                ReadString(settings.Value, "interface-name"),
                ConnectionStates.KindFromType(type));
        }

        return profiles;
    }

    private async Task<WifiInfo?> ReadWifiAsync(DBusClient client, string devicePath, HashSet<string> accessPoints)
    {
        DBusResult<Dictionary<string, VariantValue>> wireless =
            await client.GetAllPropertiesAsync(Service, devicePath, WirelessInterface).ConfigureAwait(false);

        string accessPoint = wireless.IsSuccess ? ReadPath(wireless.Value, "ActiveAccessPoint") : NoObject;

        if (accessPoint == NoObject)
        {
            return null;
        }

        accessPoints.Add(accessPoint);

        DBusResult<Dictionary<string, VariantValue>> properties =
            await client.GetAllPropertiesAsync(Service, accessPoint, AccessPointInterface).ConfigureAwait(false);

        if (!properties.IsSuccess)
        {
            return null;
        }

        string ssid = properties.Value.TryGetValue("Ssid", out VariantValue raw)
            ? Encoding.UTF8.GetString(raw.GetArray<byte>())
            : string.Empty;

        int strength = properties.Value.TryGetValue("Strength", out VariantValue value) ? value.GetByte() : 0;
        return new WifiInfo(ssid, strength);
    }

    private async Task<NetworkAddresses> ReadAddressesAsync(
        DBusClient client,
        string ip4Path,
        string ip6Path,
        Dictionary<string, NetworkAddresses> cache)
    {
        if (ip4Path == NoObject && ip6Path == NoObject)
        {
            return NetworkAddresses.Empty;
        }

        string key = ip4Path + "|" + ip6Path;

        if (cache.TryGetValue(key, out NetworkAddresses? cached))
        {
            return cached;
        }

        List<string> ipv4 = [];
        List<string> ipv6 = [];
        List<string> dns = [];
        string gateway4 = string.Empty;
        string gateway6 = string.Empty;

        if (ip4Path != NoObject)
        {
            DBusResult<Dictionary<string, VariantValue>> ip4 =
                await client.GetAllPropertiesAsync(Service, ip4Path, Ip4Interface).ConfigureAwait(false);

            if (ip4.IsSuccess)
            {
                ipv4.AddRange(ReadAddressData(ip4.Value, "AddressData"));
                gateway4 = ReadString(ip4.Value, "Gateway");
                dns.AddRange(ReadAddressData(ip4.Value, "NameserverData"));
            }
        }

        if (ip6Path != NoObject)
        {
            DBusResult<Dictionary<string, VariantValue>> ip6 =
                await client.GetAllPropertiesAsync(Service, ip6Path, Ip6Interface).ConfigureAwait(false);

            if (ip6.IsSuccess)
            {
                ipv6.AddRange(ReadAddressData(ip6.Value, "AddressData"));
                gateway6 = ReadString(ip6.Value, "Gateway");
                dns.AddRange(ReadIpv6Nameservers(ip6.Value));
            }
        }

        NetworkAddresses addresses = new(ipv4, ipv6, gateway4, gateway6, dns);
        cache[key] = addresses;
        return addresses;
    }

    /// <summary>Reads the <c>address</c> entry of every dictionary in an aa{sv} property.</summary>
    private static IEnumerable<string> ReadAddressData(Dictionary<string, VariantValue> properties, string key)
    {
        if (!properties.TryGetValue(key, out VariantValue list) || list.Count <= 0)
        {
            yield break;
        }

        for (int index = 0; index < list.Count; index++)
        {
            Dictionary<string, VariantValue> entry = Unwrap(list.GetItem(index)).GetDictionary<string, VariantValue>();

            if (entry.TryGetValue("address", out VariantValue address))
            {
                string text = Unwrap(address).GetString();

                if (text.Length > 0)
                {
                    yield return text;
                }
            }
        }
    }

    /// <summary>IPv6 name servers come as an array of 16-byte arrays.</summary>
    private static IEnumerable<string> ReadIpv6Nameservers(Dictionary<string, VariantValue> properties)
    {
        if (!properties.TryGetValue("Nameservers", out VariantValue list) || list.Count <= 0)
        {
            yield break;
        }

        for (int index = 0; index < list.Count; index++)
        {
            byte[] bytes = Unwrap(list.GetItem(index)).GetArray<byte>();

            if (bytes.Length == 16)
            {
                yield return new IPAddress(bytes).ToString();
            }
        }
    }

    private static VariantValue Unwrap(VariantValue value) =>
        value.Type == VariantValueType.Variant ? value.GetVariantValue() : value;

    private static string ReadString(Dictionary<string, VariantValue> properties, string key) =>
        properties.TryGetValue(key, out VariantValue value) ? Unwrap(value).GetString() : string.Empty;

    private static bool ReadBool(Dictionary<string, VariantValue> properties, string key) =>
        properties.TryGetValue(key, out VariantValue value) && Unwrap(value).GetBool();

    private static uint ReadUInt32(Dictionary<string, VariantValue> properties, string key) =>
        properties.TryGetValue(key, out VariantValue value) ? Unwrap(value).GetUInt32() : 0u;

    private static string ReadPath(Dictionary<string, VariantValue> properties, string key) =>
        properties.TryGetValue(key, out VariantValue value) ? Unwrap(value).GetObjectPathAsString() : NoObject;

    private static IEnumerable<string> ReadPaths(Dictionary<string, VariantValue> properties, string key)
    {
        if (!properties.TryGetValue(key, out VariantValue list) || list.Count <= 0)
        {
            yield break;
        }

        for (int index = 0; index < list.Count; index++)
        {
            yield return list.GetItem(index).GetObjectPathAsString();
        }
    }

    private static string[] ReadObjectPaths(Message message, object? state)
    {
        Reader reader = message.GetBodyReader();
        return [.. reader.ReadArrayOfObjectPath().Select(path => path.ToString())];
    }

    /// <summary>Reads the a{sa{sv}} reply of GetSettings and keeps only its <c>connection</c> section.</summary>
    private static Dictionary<string, VariantValue> ReadConnectionSection(Message message, object? state)
    {
        Reader reader = message.GetBodyReader();
        Dictionary<string, VariantValue> connection = [];

        ArrayEnd end = reader.ReadDictionaryStart();
        while (reader.HasNext(end))
        {
            string section = reader.ReadString();
            Dictionary<string, VariantValue> values = reader.ReadDictionaryOfStringToVariantValue();

            if (section == "connection")
            {
                connection = values;
            }
        }

        return connection;
    }

    /// <summary>PropertiesChanged carries the interface first; the values are re-read afterwards.</summary>
    private static (string Path, string Interface) ReadPropertiesChanged(Message message, object? state)
    {
        Reader reader = message.GetBodyReader();
        return (message.PathAsString ?? string.Empty, reader.ReadString());
    }

    private static string ReadSignalPath(Message message, object? state) => message.PathAsString ?? string.Empty;

    private static (string Path, uint State, uint Reason) ReadActiveStateChanged(Message message, object? state)
    {
        Reader reader = message.GetBodyReader();
        uint newState = reader.ReadUInt32();
        uint reason = reader.ReadUInt32();
        return (message.PathAsString ?? string.Empty, newState, reason);
    }

    /// <summary>Maps the stable ids and interface names onto the object paths of the current NetworkManager.</summary>
    private sealed record ObjectIndex(
        IReadOnlyDictionary<string, string> ProfilePaths,
        IReadOnlyDictionary<string, string> DevicePaths,
        IReadOnlyDictionary<string, IReadOnlyList<string>> ActivePaths,
        IReadOnlyDictionary<string, string> ActiveProfiles,
        IReadOnlySet<string> AccessPoints)
    {
        public static ObjectIndex Empty { get; } = new(
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            new Dictionary<string, IReadOnlyList<string>>(),
            new Dictionary<string, string>(),
            new HashSet<string>());
    }
}
