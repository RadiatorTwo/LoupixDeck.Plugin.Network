using System.Globalization;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Network;

/// <summary>
/// One favorite slot. The host binds a button state to a command name alone, so a command that
/// takes its profile from a parameter cannot carry per-profile states: two buttons would share one.
/// A slot therefore has a command name of its own and takes its profile from the favorites list,
/// which is what makes the Connected/Connecting/Disconnected/Failed/Unavailable states work.
/// </summary>
internal sealed class ConnectionSlotCommand(
    CommandDescriptor descriptor,
    int slotIndex,
    NetworkStateStore store,
    NetworkSettings settings) : ConnectionCommandBase(descriptor, store, settings), IDisplayCommand
{
    /// <summary>State changes are pushed; the poll only covers the short window before the first read.</summary>
    public TimeSpan UpdateInterval => TimeSpan.FromSeconds(2);

    /// <summary>The favorite this slot shows, as stored, or null while the slot is empty.</summary>
    public string? ProfileEntry => Settings.FavoriteAt(slotIndex);

    /// <summary>The profile id this slot shows, resolved against the current snapshot.</summary>
    public string? ProfileId =>
        ProfileEntry is { } entry ? NetworkSettings.Resolve(entry, Store.Current)?.StableId ?? entry : null;

    public string GetText(CommandContext ctx)
    {
        string? profileId = ProfileId;

        if (profileId is null)
        {
            return ctx.Host.Tr("Empty slot");
        }

        NetworkSnapshot snapshot = Store.Current;
        ConnectionState state = Store.StateOf(profileId, snapshot);
        string name = snapshot.FindProfile(profileId)?.Name ?? ctx.Host.Tr("Connection not found");
        string status = $"{ConnectionStates.Glyph(state)} {ctx.Host.Tr(ConnectionStates.ToEnglishText(state))}";
        return string.Join(Environment.NewLine, name, status);
    }

    protected override Task ExecuteCore(CommandContext ctx)
    {
        string? profileId = ProfileId;

        if (profileId is null)
        {
            ShowOverlay(ctx, ctx.Host.Tr("Empty slot"));
            return Task.CompletedTask;
        }

        bool up = Store.StateOf(profileId) is ConnectionState.Connected or ConnectionState.Connecting;
        return RunAsync(ctx, profileId, activate: !up);
    }
}

/// <summary>Builds the slot commands.</summary>
internal static class ConnectionSlotCommands
{
    /// <summary>
    /// The states a slot button carries, in the order the host materializes them. Index 0 is the
    /// resting state. These names are persisted in the button config and must never change.
    /// </summary>
    public static readonly IReadOnlyList<ButtonStateDescriptor> States =
    [
        new ButtonStateDescriptor { Name = "Disconnected", Description = "The connection is down" },
        new ButtonStateDescriptor { Name = "Connecting", Description = "The connection is coming up" },
        new ButtonStateDescriptor { Name = "Connected", Description = "The connection is up" },
        new ButtonStateDescriptor { Name = "Failed", Description = "The last activation failed" },
        new ButtonStateDescriptor { Name = "Unavailable", Description = "NetworkManager, the profile or its device is not available" }
    ];

    public static IReadOnlyList<ConnectionSlotCommand> Create(NetworkStateStore store, NetworkSettings settings)
    {
        List<ConnectionSlotCommand> commands = [];

        for (int slotIndex = 0; slotIndex < NetworkSettings.FavoriteSlotCount; slotIndex++)
        {
            string number = Number(slotIndex);

            CommandDescriptor descriptor = new()
            {
                CommandName = NetworkCommands.SlotPrefix + number,
                DisplayName = "Connection Slot " + number,
                Group = NetworkCommands.Group,
                Icon = NetworkButtonLayouts.Slot,
                // The caption is a placeholder: the slot replaces it with the profile and its state.
                ButtonLayout = NetworkButtonLayouts.IconWithCaption(NetworkButtonLayouts.Slot, caption: null, tall: true),
                Description = "Show and toggle favorite connection " + number + ". The button follows the connection's state.",
                HiddenFromMenu = true,
                States = States
            };

            commands.Add(new ConnectionSlotCommand(descriptor, slotIndex, store, settings));
        }

        return commands;
    }

    /// <summary>The two-digit slot number used in command names and the menu.</summary>
    public static string Number(int slotIndex) => (slotIndex + 1).ToString("00", CultureInfo.InvariantCulture);
}
