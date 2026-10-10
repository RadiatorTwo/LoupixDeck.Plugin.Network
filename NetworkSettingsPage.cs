using System.Text;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Network;

/// <summary>
/// The plugin's settings form. The host renders the descriptors, so only the values are declared
/// here. Labels and descriptions are English keys the host translates.
/// <para>
/// The SDK has no pick list, so profiles are entered as UUIDs or names; "List connections" prints
/// both, and a saved name is replaced by its UUID so a later rename does not break it.
/// </para>
/// </summary>
internal sealed class NetworkSettingsPage(
    NetworkSettings settings,
    NetworkStateStore store,
    ConnectionSelectionService selection,
    IPluginHost host)
{
    public IReadOnlyList<PluginSettingDescriptor> BuildSchema() =>
    [
        new PluginSettingDescriptor
        {
            Key = "heading:connections",
            Label = "Connections",
            Kind = PluginSettingKind.Heading
        },
        new PluginSettingDescriptor
        {
            Key = NetworkSettings.FavoritesKey,
            Label = "Favorite connections",
            Kind = PluginSettingKind.Text,
            Description = "The connections behind the connection slots, in slot order, separated by commas. Names or UUIDs; the command menu fills this in for you.",
            DefaultValue = string.Empty
        },
        new PluginSettingDescriptor
        {
            Key = NetworkSettings.HiddenKey,
            Label = "Hidden connections",
            Kind = PluginSettingKind.Text,
            Description = "Connections kept out of the command menu and the connections folder, separated by commas. Names or UUIDs.",
            DefaultValue = string.Empty
        },
        new PluginSettingDescriptor
        {
            Key = NetworkSettings.PreferredVpnKey,
            Label = "Preferred VPN",
            Kind = PluginSettingKind.Text,
            Description = "The VPN that Connect VPN uses when its button names none. A name or a UUID.",
            DefaultValue = string.Empty
        },
        new PluginSettingDescriptor
        {
            Key = "heading:display",
            Label = "Display",
            Kind = PluginSettingKind.Heading
        },
        new PluginSettingDescriptor
        {
            Key = NetworkSettings.PrimaryInterfaceKey,
            Label = "Primary interface",
            Kind = PluginSettingKind.Text,
            Description = "The interface the address and Wi-Fi displays show, for example wlan0. Empty follows the primary connection automatically.",
            DefaultValue = string.Empty
        },
        new PluginSettingDescriptor
        {
            Key = NetworkSettings.ShowIpKey,
            Label = "Show IP addresses",
            Kind = PluginSettingKind.Toggle,
            Description = "Off hides addresses, gateways and DNS servers on the deck, for example while sharing the screen.",
            DefaultValue = NetworkSettings.DefaultShowIp
        },
        new PluginSettingDescriptor
        {
            Key = "heading:behavior",
            Label = "Behaviour",
            Kind = PluginSettingKind.Heading
        },
        new PluginSettingDescriptor
        {
            Key = NetworkSettings.WhenActiveKey,
            Label = "When the connection is already active",
            Kind = PluginSettingKind.Text,
            Description = "What Activate Connection does when the connection is already up: nothing, or reactivate.",
            DefaultValue = NetworkSettings.WhenActiveNothing
        },
        new PluginSettingDescriptor
        {
            Key = NetworkSettings.ActionTimeoutKey,
            Label = "Activation timeout (ms)",
            Kind = PluginSettingKind.Number,
            Description = "How long a button waits for a connection to come up or go down (1000 to 120000). NetworkManager keeps trying after the wait runs out.",
            DefaultValue = NetworkSettings.DefaultActionTimeoutMilliseconds
        },
        new PluginSettingDescriptor
        {
            Key = NetworkSettings.KeepErrorStateKey,
            Label = "Keep the error state",
            Kind = PluginSettingKind.Toggle,
            Description = "On: a failed or timed-out connection stays Failed until its next action. Off: it shows Failed briefly and then falls back to its actual state.",
            DefaultValue = NetworkSettings.DefaultKeepErrorState
        },
        new PluginSettingDescriptor
        {
            Key = NetworkSettings.DBusTimeoutKey,
            Label = "D-Bus timeout (ms)",
            Kind = PluginSettingKind.Number,
            Description = "How long a single call to NetworkManager may take before it is given up (250 to 10000).",
            DefaultValue = NetworkSettings.DefaultDBusTimeoutMilliseconds
        }
    ];

    public IReadOnlyList<PluginSettingAction> BuildActions() =>
    [
        new PluginSettingAction
        {
            Label = "List connections",
            Invoke = ListConnectionsAsync
        },
        new PluginSettingAction
        {
            Label = "Check NetworkManager",
            Invoke = CheckAsync
        },
        new PluginSettingAction
        {
            Label = "Clear favorite connections",
            Invoke = ClearFavoritesAsync
        }
    ];

    /// <summary>Prints every profile with its UUID, which is what the text fields above accept.</summary>
    private Task<string> ListConnectionsAsync()
    {
        NetworkSnapshot snapshot = store.Current;

        if (!snapshot.IsAvailable)
        {
            return Task.FromResult(host.Tr(NetworkErrors.ToEnglishText(NetworkCallOutcome.Unavailable)));
        }

        if (snapshot.Profiles.Count == 0)
        {
            return Task.FromResult(host.Tr("No connections"));
        }

        StringBuilder builder = new();

        foreach (NetworkProfile profile in snapshot.Profiles)
        {
            builder.AppendLine($"{selection.Label(profile, host.Tr)} — {profile.StableId}");
        }

        return Task.FromResult(builder.ToString());
    }

    private Task<string> CheckAsync()
    {
        NetworkSnapshot snapshot = store.Current;

        if (!snapshot.IsAvailable)
        {
            return Task.FromResult(host.Tr(NetworkErrors.ToEnglishText(NetworkCallOutcome.Unavailable)));
        }

        string text =
            $"{host.Tr(ConnectionStates.ToEnglishText(snapshot.State))} · " +
            $"{host.Tr(ConnectionStates.ToEnglishText(snapshot.Connectivity))} · " +
            $"{snapshot.Profiles.Count} {host.Tr("connections")} · " +
            $"{snapshot.Devices.Count} {host.Tr("devices")}";

        return Task.FromResult(text);
    }

    private Task<string> ClearFavoritesAsync()
    {
        settings.ClearFavorites();
        return Task.FromResult(host.Tr("Favorite connections cleared"));
    }
}
