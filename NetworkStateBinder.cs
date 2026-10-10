using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Network;

/// <summary>
/// Pushes the cached network state onto the deck: it sets the active state of the stateful buttons
/// and asks the host to redraw every display command.
/// <para>
/// This is the only type that calls back into the host from a background thread, so every call is
/// guarded — a host that throws must not take the D-Bus read loop with it.
/// </para>
/// </summary>
internal sealed class NetworkStateBinder(
    IPluginHost host,
    NetworkStateStore store,
    IReadOnlyList<ConnectionSlotCommand> slots,
    IReadOnlyList<string> displayCommandNames) : IDisposable
{
    private bool _started;

    public void Start()
    {
        if (_started)
        {
            return;
        }

        store.Changed += ReplayAll;
        _started = true;

        // Buttons created before the first read have to be brought up to date once.
        ReplayAll();
    }

    /// <summary>Applies the current state to every stateful button and redraws the displays.</summary>
    public void ReplayAll()
    {
        NetworkSnapshot snapshot = store.Current;

        foreach (ConnectionSlotCommand slot in slots)
        {
            // An empty slot reads as unavailable rather than keeping the state of its old favorite.
            ConnectionState state = slot.ProfileId is { } profileId
                ? store.StateOf(profileId, snapshot)
                : ConnectionState.Unavailable;

            SetState(slot.Descriptor.CommandName, ConnectionStates.ToButtonState(state));
        }

        SetState(NetworkCommands.Wifi, NetworkActionCommands.SwitchStateFor(snapshot, wifi: true));
        SetState(NetworkCommands.Networking, NetworkActionCommands.SwitchStateFor(snapshot, wifi: false));
        SetState(NetworkDisplayCommands.VpnConnected, VpnStateFor(snapshot));

        foreach (string commandName in displayCommandNames)
        {
            Refresh(commandName);
        }
    }

    public void Dispose()
    {
        if (!_started)
        {
            return;
        }

        store.Changed -= ReplayAll;
        _started = false;
    }

    private static string VpnStateFor(NetworkSnapshot snapshot)
    {
        return snapshot.IsAvailable && snapshot.ActiveConnections.Any(active => active.IsVpn && active.State == ConnectionState.Connected) ? "Yes" : "No";
    }

    private void SetState(string commandName, string stateName)
    {
        try
        {
            // False only means no button offers the state or it is already active; the host
            // repaints the button itself when the state does switch.
            host.SetActiveButtonState(commandName, stateName);
        }
        catch (Exception ex)
        {
            host.Logger.Warn($"Network: cannot update {commandName}: {ex.Message}");
        }
    }

    private void Refresh(string commandName)
    {
        try
        {
            host.RequestButtonRefresh(commandName);
        }
        catch (Exception ex)
        {
            host.Logger.Warn($"Network: cannot refresh {commandName}: {ex.Message}");
        }
    }
}
