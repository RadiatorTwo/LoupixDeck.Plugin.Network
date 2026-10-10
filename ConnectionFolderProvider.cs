using System.Globalization;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Network;

/// <summary>
/// The touch-screen folder that lists the known connections with their state. Pressing an entry
/// brings the connection up or down. The back slot is reserved by the host, so it is skipped here.
/// </summary>
internal sealed class ConnectionFolderProvider(
    NetworkStateStore store,
    ConnectionSelectionService selection,
    IPluginHost host) : FolderProviderBase
{
    private static readonly PluginColor ConnectedColor = PluginColor.FromRgb(0x20, 0x60, 0x30);
    private static readonly PluginColor DisconnectedColor = PluginColor.FromRgb(0x20, 0x20, 0x40);
    private static readonly PluginColor VpnColor = PluginColor.FromRgb(0x18, 0x30, 0x58);
    private static readonly PluginColor FailedColor = PluginColor.FromRgb(0x70, 0x20, 0x20);
    private static readonly PluginColor BusyColor = PluginColor.FromRgb(0x70, 0x55, 0x10);
    private static readonly PluginColor UnavailableColor = PluginColor.FromRgb(0x20, 0x20, 0x20);

    public override string Title => host.Tr("Connections");

    public override IReadOnlyList<FolderEntry> BuildEntries()
    {
        FolderGridInfo grid = host.FolderGrid;
        NetworkSnapshot snapshot = store.Current;

        if (!snapshot.IsAvailable)
        {
            return [Message(grid, NetworkErrors.ToEnglishText(NetworkCallOutcome.Unavailable))];
        }

        IReadOnlyList<NetworkProfile> profiles = selection.Visible();

        if (profiles.Count == 0)
        {
            return [Message(grid, "No connections")];
        }

        List<FolderEntry> entries = [];

        for (int index = 0; index < profiles.Count; index++)
        {
            int slot = grid.SlotForIndex(index);

            if (slot < 0)
            {
                // The grid is full; the remaining connections stay reachable through the command menu.
                break;
            }

            NetworkProfile profile = profiles[index];
            ConnectionState state = store.StateOf(profile.StableId, snapshot);

            entries.Add(new FolderEntry
            {
                SlotIndex = slot,
                Text = BuildText(profile, state, snapshot),
                BackColor = ColorFor(profile, state),
                TextSize = 13,
                Bold = state == ConnectionState.Connected,
                OnPress = () => PressAsync(profile.StableId)
            });
        }

        return entries;
    }

    public override void OnEnter() => store.Changed += RaiseEntriesChanged;

    public override void OnExit() => store.Changed -= RaiseEntriesChanged;

    private FolderEntry Message(FolderGridInfo grid, string english) => new()
    {
        SlotIndex = grid.SlotForIndex(0),
        Text = host.Tr(english),
        BackColor = UnavailableColor,
        TextSize = 13
    };

    private async Task PressAsync(string profileId)
    {
        try
        {
            bool up = store.StateOf(profileId) is ConnectionState.Connected or ConnectionState.Connecting;
            NetworkActionResult result = up
                ? await store.DeactivateAsync(profileId).ConfigureAwait(false)
                : await store.ActivateAsync(profileId).ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                host.Logger.Info($"Network: folder action on {profileId}: {result.Text}.");
            }
        }
        catch (Exception ex)
        {
            host.Logger.Warn($"Network: folder action on {profileId} failed: {ex.Message}");
        }

        RaiseEntriesChanged();
    }

    /// <summary>Name, state, and a third line for what sets the profile apart: VPN, or Wi-Fi signal.</summary>
    private string BuildText(NetworkProfile profile, ConnectionState state, NetworkSnapshot snapshot)
    {
        List<string> lines = [profile.Name, $"{ConnectionStates.Glyph(state)} {host.Tr(ConnectionStates.ToEnglishText(state))}"];

        if (profile.IsVpn)
        {
            lines.Add(host.Tr(ConnectionStates.TypeText(profile.Type)));
        }
        else if (profile.Kind == NetworkConnectionKind.Wifi &&
                 snapshot.Devices.FirstOrDefault(device => string.Equals(device.ActiveProfileId, profile.StableId, StringComparison.OrdinalIgnoreCase))?.Wifi is { } wifi)
        {
            lines.Add(host.Tr("Signal") + " " + wifi.Strength.ToString(CultureInfo.InvariantCulture) + "%");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static PluginColor ColorFor(NetworkProfile profile, ConnectionState state) => state switch
    {
        ConnectionState.Connected => ConnectedColor,
        ConnectionState.Connecting or ConnectionState.Disconnecting => BusyColor,
        ConnectionState.Failed => FailedColor,
        ConnectionState.Unavailable => UnavailableColor,
        _ => profile.IsVpn ? VpnColor : DisconnectedColor
    };
}

/// <summary>Opens the connections folder.</summary>
internal sealed class OpenConnectionsCommand(
    NetworkStateStore store,
    ConnectionSelectionService selection,
    IPluginHost host) : IPluginCommand
{
    public CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = NetworkCommands.OpenConnections,
        DisplayName = "Open Connections Folder",
        Group = NetworkCommands.Group,
        Icon = NetworkButtonLayouts.ConnectionsFolder,
        ButtonLayout = NetworkButtonLayouts.IconWithCaption(NetworkButtonLayouts.ConnectionsFolder, "Connections"),
        Description = "Open a folder with the known connections and their state. A press on an entry toggles it."
    };

    public ButtonTargets SupportedTargets => ButtonTargets.TouchButton | ButtonTargets.SimpleButton;

    public Task Execute(CommandContext ctx)
    {
        try
        {
            ctx.Host.OpenFolder(new ConnectionFolderProvider(store, selection, host));
        }
        catch (Exception ex)
        {
            ctx.Host.Logger.Warn($"{Descriptor.CommandName} failed: {ex.Message}");
        }

        return Task.CompletedTask;
    }
}
