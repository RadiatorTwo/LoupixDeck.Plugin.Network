using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Network;

/// <summary>What a profile command does.</summary>
internal enum ProfileAction
{
    Activate,
    Deactivate,
    Toggle
}

/// <summary>Activates, deactivates or toggles the profile in its parameter.</summary>
internal sealed class ProfileActionCommand(
    CommandDescriptor descriptor,
    ProfileAction action,
    NetworkStateStore store,
    NetworkSettings settings) : ConnectionCommandBase(descriptor, store, settings)
{
    protected override Task ExecuteCore(CommandContext ctx)
    {
        string? profileId = ResolveProfileId(ctx);
        ConnectionState state = Store.StateOf(profileId);

        switch (action)
        {
            case ProfileAction.Activate when state == ConnectionState.Connected && Settings.WhenActive == WhenActiveBehavior.Nothing:
                ShowOverlay(ctx, ctx.Host.Tr("Already connected"));
                return Task.CompletedTask;

            case ProfileAction.Activate:
                return RunAsync(ctx, profileId, activate: true);

            case ProfileAction.Deactivate:
                return RunAsync(ctx, profileId, activate: false);

            default:
                // A profile on its way up is cancelled by the second press, as a user expects.
                bool up = state is ConnectionState.Connected or ConnectionState.Connecting;
                return RunAsync(ctx, profileId, activate: !up);
        }
    }
}

/// <summary>
/// Connects or disconnects a VPN. Without a parameter, Connect VPN uses the preferred VPN from the
/// settings and Disconnect VPN takes down every active VPN.
/// </summary>
internal sealed class VpnCommand(
    CommandDescriptor descriptor,
    bool connect,
    NetworkStateStore store,
    NetworkSettings settings) : ConnectionCommandBase(descriptor, store, settings)
{
    protected override async Task ExecuteCore(CommandContext ctx)
    {
        NetworkSnapshot snapshot = Store.Current;
        string? parameter = NetworkCommands.ReadParameter(ctx, 0);

        if (connect)
        {
            NetworkProfile? vpn = NetworkSettings.Resolve(parameter ?? Settings.PreferredVpn, snapshot);

            if (vpn is null && parameter is null && Settings.PreferredVpn.Length == 0)
            {
                ShowOverlay(ctx, ctx.Host.Tr("No VPN selected"));
                return;
            }

            if (vpn is { IsVpn: false })
            {
                ShowOverlay(ctx, ctx.Host.Tr("Not a VPN"));
                return;
            }

            await RunAsync(ctx, vpn?.StableId ?? parameter ?? Settings.PreferredVpn, activate: true).ConfigureAwait(false);
            return;
        }

        if (parameter is not null)
        {
            await RunAsync(ctx, NetworkSettings.Resolve(parameter, snapshot)?.StableId ?? parameter, activate: false).ConfigureAwait(false);
            return;
        }

        List<ActiveNetworkConnection> vpns = [.. snapshot.ActiveConnections.Where(active => active.IsVpn)];

        if (vpns.Count == 0)
        {
            ShowOverlay(ctx, ctx.Host.Tr(snapshot.IsAvailable ? "No VPN connected" : NetworkErrors.ToEnglishText(NetworkCallOutcome.Unavailable)));
            return;
        }

        foreach (ActiveNetworkConnection active in vpns)
        {
            await RunAsync(ctx, active.ProfileId, activate: false).ConfigureAwait(false);
        }
    }
}

/// <summary>Switches Wi-Fi or networking as a whole on, off or flips it.</summary>
internal sealed class RadioCommand(
    CommandDescriptor descriptor,
    bool wifi,
    NetworkStateStore store,
    NetworkSettings settings) : ConnectionCommandBase(descriptor, store, settings)
{
    protected override async Task ExecuteCore(CommandContext ctx)
    {
        NetworkSnapshot snapshot = Store.Current;

        if (!snapshot.IsAvailable)
        {
            ShowOverlay(ctx, ctx.Host.Tr(NetworkErrors.ToEnglishText(NetworkCallOutcome.Unavailable)));
            return;
        }

        bool current = wifi ? snapshot.WifiEnabled : snapshot.NetworkingEnabled;
        bool target = NetworkCommands.ReadMode(ctx) switch
        {
            SwitchMode.On => true,
            SwitchMode.Off => false,
            _ => !current
        };

        if (wifi && target && !snapshot.WifiHardwareEnabled)
        {
            // The hardware switch wins; turning the software switch on would change nothing visible.
            ShowOverlay(ctx, ctx.Host.Tr("Wi-Fi is off by hardware switch"));
            return;
        }

        NetworkCallOutcome outcome = wifi
            ? await Store.Backend.SetWifiEnabledAsync(target).ConfigureAwait(false)
            : await Store.Backend.SetNetworkingEnabledAsync(target).ConfigureAwait(false);

        Report(ctx, new NetworkActionResult(outcome), wifi ? "Wi-Fi" : "networking");
    }
}

/// <summary>Connects or disconnects the device in its parameter, as "nmcli device connect" does.</summary>
internal sealed class DeviceCommand(
    CommandDescriptor descriptor,
    bool connect,
    NetworkStateStore store,
    NetworkSettings settings) : ConnectionCommandBase(descriptor, store, settings)
{
    protected override async Task ExecuteCore(CommandContext ctx)
    {
        string? device = NetworkCommands.ReadParameter(ctx, 0);

        if (device is null)
        {
            ShowOverlay(ctx, ctx.Host.Tr("No device"));
            return;
        }

        NetworkCallOutcome outcome = connect
            ? await Store.Backend.ConnectDeviceAsync(device).ConfigureAwait(false)
            : await Store.Backend.DisconnectDeviceAsync(device).ConfigureAwait(false);

        NetworkActionResult result = outcome == NetworkCallOutcome.NotFound
            ? new NetworkActionResult(outcome, "Device not found")
            : new NetworkActionResult(outcome);

        Report(ctx, result, device);
    }
}

/// <summary>
/// Adds or removes the profile in its parameter from the favorites. These are how the favorites
/// list is edited without typing UUIDs: the command menu offers them next to every profile.
/// </summary>
internal sealed class FavoriteCommand(
    CommandDescriptor descriptor,
    bool add,
    NetworkStateStore store,
    NetworkSettings settings) : ConnectionCommandBase(descriptor, store, settings)
{
    protected override Task ExecuteCore(CommandContext ctx)
    {
        string? profileId = NetworkCommands.ReadParameter(ctx, 0) is { } parameter
            ? NetworkSettings.Resolve(parameter, Store.Current)?.StableId ?? parameter
            : null;

        if (profileId is null)
        {
            ShowOverlay(ctx, ctx.Host.Tr("No connection"));
            return Task.CompletedTask;
        }

        string message = add
            ? Settings.AddFavorite(profileId) ? "Added to favorites" : "Already a favorite"
            : Settings.RemoveFavorite(profileId) ? "Removed from favorites" : "Not a favorite";

        ShowOverlay(ctx, ctx.Host.Tr(message));
        return Task.CompletedTask;
    }
}

/// <summary>Builds the action commands.</summary>
internal static class NetworkActionCommands
{
    /// <summary>
    /// The states the Wi-Fi and networking buttons carry. Index 0 is the resting state. These names
    /// are persisted in the button config and must never change.
    /// </summary>
    public static readonly IReadOnlyList<ButtonStateDescriptor> SwitchStates =
    [
        new ButtonStateDescriptor { Name = "Off", Description = "Switched off" },
        new ButtonStateDescriptor { Name = "On", Description = "Switched on" },
        new ButtonStateDescriptor { Name = "Unavailable", Description = "NetworkManager or the hardware is not available" }
    ];

    public static IEnumerable<IPluginCommand> Create(NetworkStateStore store, NetworkSettings settings)
    {
        yield return new RadioCommand(SwitchDescriptor(
                NetworkCommands.Wifi,
                "Wi-Fi",
                "Switch Wi-Fi on, off or toggle it. The button follows the Wi-Fi state.",
                NetworkButtonLayouts.Wifi),
            wifi: true, store, settings);

        yield return new RadioCommand(SwitchDescriptor(
                NetworkCommands.Networking,
                "Networking",
                "Switch networking as a whole on, off or toggle it. The button follows the networking state.",
                NetworkButtonLayouts.Networking),
            wifi: false, store, settings);

        yield return new ProfileActionCommand(NetworkCommands.ProfileDescriptor(
                NetworkCommands.Activate,
                "Activate Connection",
                "Bring the connection up and wait for NetworkManager to report the result",
                NetworkButtonLayouts.Activate),
            ProfileAction.Activate, store, settings);

        yield return new ProfileActionCommand(NetworkCommands.ProfileDescriptor(
                NetworkCommands.Deactivate,
                "Deactivate Connection",
                "Take the connection down",
                NetworkButtonLayouts.Deactivate),
            ProfileAction.Deactivate, store, settings);

        yield return new ProfileActionCommand(NetworkCommands.ProfileDescriptor(
                NetworkCommands.Toggle,
                "Toggle Connection",
                "Bring the connection up when it is down and take it down when it is up or connecting",
                NetworkButtonLayouts.Toggle),
            ProfileAction.Toggle, store, settings);

        yield return new VpnCommand(NetworkCommands.ProfileDescriptor(
                NetworkCommands.ConnectVpn,
                "Connect VPN",
                "Connect a VPN. Without a connection the preferred VPN from the settings is used.",
                NetworkButtonLayouts.ConnectVpn),
            connect: true, store, settings);

        yield return new VpnCommand(NetworkCommands.ProfileDescriptor(
                NetworkCommands.DisconnectVpn,
                "Disconnect VPN",
                "Disconnect a VPN. Without a connection every active VPN is disconnected.",
                NetworkButtonLayouts.DisconnectVpn),
            connect: false, store, settings);

        yield return new DeviceCommand(NetworkCommands.DeviceDescriptor(
                NetworkCommands.ConnectDevice,
                "Connect Device",
                "Connect the device with the best available connection",
                NetworkButtonLayouts.ConnectDevice),
            connect: true, store, settings);

        yield return new DeviceCommand(NetworkCommands.DeviceDescriptor(
                NetworkCommands.DisconnectDevice,
                "Disconnect Device",
                "Disconnect the device and keep it from connecting on its own",
                NetworkButtonLayouts.DisconnectDevice),
            connect: false, store, settings);

        yield return new FavoriteCommand(NetworkCommands.ProfileDescriptor(
                NetworkCommands.AddFavorite,
                "Add to Favorites",
                "Add the connection to the favorites shown by the connection slots",
                NetworkButtonLayouts.AddFavorite),
            add: true, store, settings);

        yield return new FavoriteCommand(NetworkCommands.ProfileDescriptor(
                NetworkCommands.RemoveFavorite,
                "Remove from Favorites",
                "Remove the connection from the favorites",
                NetworkButtonLayouts.RemoveFavorite),
            add: false, store, settings);
    }

    /// <summary>The state a switch button shows for the current snapshot.</summary>
    public static string SwitchStateFor(NetworkSnapshot snapshot, bool wifi)
    {
        if (!snapshot.IsAvailable || (wifi && !snapshot.WifiHardwareEnabled))
        {
            return "Unavailable";
        }

        bool on = wifi ? snapshot.WifiEnabled : snapshot.NetworkingEnabled;
        return on ? "On" : "Off";
    }

    private static CommandDescriptor SwitchDescriptor(string commandName, string displayName, string description, string glyph) => new()
    {
        CommandName = commandName,
        DisplayName = displayName,
        Group = NetworkCommands.Group,
        Icon = glyph,
        ButtonLayout = NetworkButtonLayouts.IconWithCaption(glyph, caption: null),
        Description = description,
        ParameterTemplate = NetworkCommands.ModeTemplate,
        Parameters =
        [
            new CommandParameter(NetworkCommands.ModeParameter, typeof(string))
            {
                DefaultValue = NetworkCommands.ModeToggle
            }
        ],
        HiddenFromMenu = true,
        States = SwitchStates
    };
}
