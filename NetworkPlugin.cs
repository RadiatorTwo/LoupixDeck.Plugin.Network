using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Network;

/// <summary>
/// Controls network connections, Wi-Fi and VPNs. On Linux it talks to NetworkManager over its
/// D-Bus API; it never runs nmcli, never stores credentials and only uses existing profiles.
/// </summary>
public sealed class NetworkPlugin : LoupixPlugin, IMenuContributor, IPluginSettingsPage, IPluginRequirements
{
    /// <summary>How long the menu and the requirements wait for the first NetworkManager snapshot.</summary>
    private static readonly TimeSpan FirstSnapshotWait = TimeSpan.FromSeconds(3);

    private readonly List<IPluginCommand> _commands = [];

    private IPluginHost? _host;
    private NetworkSettings? _settings;
    private INetworkBackend? _backend;
    private NetworkStateStore? _store;
    private NetworkStateBinder? _binder;
    private NetworkMenu? _menu;
    private NetworkSettingsPage? _settingsPage;

    public override PluginMetadata Metadata { get; } = new()
    {
        Id = "network",
        Name = "Network",
        Version = new Version(1, 0, 0),
        SdkVersion = new Version(1, 31, 0),
        Author = "RadiatorTwo",
        Description = "Controls NetworkManager connections, Wi-Fi and VPNs over the native D-Bus API.",
        Icon = LoadIcon()
    };

    public override void Initialize(IPluginHost host)
    {
        _host = host;
        NetworkButtonLayouts.Translate = host.Tr;

        try
        {
            NetworkSettings settings = new(host.Settings);
            _settings = settings;

            INetworkBackend backend = OperatingSystem.IsLinux()
                ? new NetworkManagerBackend(host.Logger)
                : new UnsupportedNetworkBackend();
            backend.CallTimeoutMilliseconds = settings.DBusTimeoutMilliseconds;
            _backend = backend;

            NetworkStateStore store = new(backend, settings, host.Logger);
            _store = store;

            ConnectionSelectionService selection = new(store, settings);

            _commands.AddRange(NetworkActionCommands.Create(store, settings));
            _commands.Add(new OpenConnectionsCommand(store, selection, host));
            _commands.AddRange(NetworkDisplayCommands.Create(store, settings));

            IReadOnlyList<ConnectionSlotCommand> slots = ConnectionSlotCommands.Create(store, settings);
            _commands.AddRange(slots);

            List<string> displayCommandNames =
            [
                .. _commands.OfType<IDisplayCommand>().Select(command => command.Descriptor.CommandName)
            ];

            _binder = new NetworkStateBinder(host, store, slots, displayCommandNames);
            _binder.Start();

            _menu = new NetworkMenu(store, selection, settings, host);
            _settingsPage = new NetworkSettingsPage(settings, store, selection, host);

            // Connecting talks to the bus, so it must not hold up the host's Initialize call.
            _ = Task.Run(backend.StartAsync);
        }
        catch (Exception ex)
        {
            host.Logger.Error("Network: the plugin could not be initialized.", ex);
            Shutdown();
        }
    }

    public override IEnumerable<IPluginCommand> GetCommands() => _commands;

    public override IReadOnlyList<CommandGroupDescriptor> GetCommandGroups() =>
    [
        new CommandGroupDescriptor
        {
            Group = NetworkCommands.Group,
            Icon = NetworkButtonLayouts.Group,
            Description = "Connections, Wi-Fi and VPNs through NetworkManager",
            Section = CommandGroupSection.Plugins
        }
    ];

    public async Task<IReadOnlyList<MenuNode>> GetMenuNodes(ButtonTargets target)
    {
        // The menu may be opened before the first snapshot arrived; the host allows a few seconds.
        if (_backend is { } backend)
        {
            await Task.WhenAny(backend.Ready, Task.Delay(FirstSnapshotWait)).ConfigureAwait(false);
        }

        return _menu?.Build() ?? [];
    }

    public IReadOnlyList<PluginSettingDescriptor> SettingsSchema =>
        _settingsPage?.BuildSchema() ?? [];

    public IReadOnlyList<PluginSettingAction> SettingsActions =>
        _settingsPage?.BuildActions() ?? [];

    public void OnSettingsSaved()
    {
        NetworkSettings? settings = _settings;
        NetworkStateStore? store = _store;

        if (settings is null || store is null)
        {
            return;
        }

        store.Backend.CallTimeoutMilliseconds = settings.DBusTimeoutMilliseconds;
        settings.NormalizeProfileReferences(store.Current);

        // The favorites may now point at other profiles, so every slot button is brought up to date.
        _binder?.ReplayAll();
    }

    public IReadOnlyList<PluginRequirement> GetRequirements()
    {
        if (!OperatingSystem.IsLinux())
        {
            return
            [
                new PluginRequirement
                {
                    Id = "network.platform",
                    Name = "Supported platform",
                    IsMet = false,
                    Message = "This version of the plugin only supports Linux with NetworkManager."
                }
            ];
        }

        // The host asks right after loading, off the startup path; without this wait the first
        // answer would report NetworkManager as missing only because the bus was not read yet.
        _backend?.Ready.Wait(FirstSnapshotWait);

        bool available = _store?.Current.IsAvailable == true;

        return
        [
            new PluginRequirement
            {
                Id = "network.networkmanager",
                Name = "NetworkManager",
                IsMet = available,
                Message = available
                    ? "NetworkManager is reachable on the system bus."
                    : "NetworkManager is not reachable on the system bus.",
                InstallHint = available
                    ? null
                    : "Install NetworkManager and start it with: sudo systemctl enable --now NetworkManager"
            }
        ];
    }

    public override void Shutdown()
    {
        _binder?.Dispose();
        _binder = null;
        _menu = null;
        _settingsPage = null;
        _commands.Clear();
        _store?.Dispose();
        _store = null;
        _backend?.Dispose();
        _backend = null;
        _settings = null;
        _host = null;
    }

    /// <summary>The plugin icon (icon.png, embedded). Missing data only costs the icon.</summary>
    private static byte[]? LoadIcon()
    {
        using Stream? stream = typeof(NetworkPlugin).Assembly.GetManifestResourceStream("LoupixDeck.Plugin.Network.icon.png");
        if (stream == null) return null;

        using MemoryStream buffer = new();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
