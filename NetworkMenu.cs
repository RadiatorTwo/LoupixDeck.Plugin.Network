using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Network;

/// <summary>
/// Builds the command picker tree. The SDK has no dropdown for a parameter, so a profile is chosen
/// by walking to it: type, then the profile — labelled with its name, type, interface and state —
/// then the action. Every leaf bakes the profile's UUID into the command's parameter and is named
/// "profile — action", which becomes the button's caption.
/// <para>
/// The host gives a menu contributor only a few seconds, so everything comes from the snapshot.
/// </para>
/// </summary>
internal sealed class NetworkMenu(
    NetworkStateStore store,
    ConnectionSelectionService selection,
    NetworkSettings settings,
    IPluginHost host)
{
    private static readonly NetworkConnectionKind[] KindOrder =
    [
        NetworkConnectionKind.Ethernet,
        NetworkConnectionKind.Wifi,
        NetworkConnectionKind.Vpn,
        NetworkConnectionKind.Other
    ];

    public IReadOnlyList<MenuNode> Build()
    {
        NetworkSnapshot snapshot = store.Current;

        if (!snapshot.IsAvailable)
        {
            return
            [
                Folder(NetworkCommands.Group,
                [
                    new MenuNode { Name = host.Tr("NetworkManager unavailable"), CommandName = string.Empty }
                ], translate: false)
            ];
        }

        List<MenuNode> children =
        [
            Leaf("Toggle Wi-Fi", NetworkCommands.Wifi, NetworkCommands.ModeParameter, NetworkCommands.ModeToggle),
            Leaf("Wi-Fi On", NetworkCommands.Wifi, NetworkCommands.ModeParameter, NetworkCommands.ModeOn),
            Leaf("Wi-Fi Off", NetworkCommands.Wifi, NetworkCommands.ModeParameter, NetworkCommands.ModeOff),
            Leaf("Toggle Networking", NetworkCommands.Networking, NetworkCommands.ModeParameter, NetworkCommands.ModeToggle),
            Leaf("Networking On", NetworkCommands.Networking, NetworkCommands.ModeParameter, NetworkCommands.ModeOn),
            Leaf("Networking Off", NetworkCommands.Networking, NetworkCommands.ModeParameter, NetworkCommands.ModeOff)
        ];

        MenuNode? connections = BuildConnections();
        if (connections is not null)
        {
            children.Add(connections);
        }

        children.Add(Folder("VPN",
        [
            Leaf("Connect Preferred VPN", NetworkCommands.ConnectVpn, NetworkCommands.ConnectionParameter, NetworkCommands.Auto),
            Leaf("Disconnect All VPNs", NetworkCommands.DisconnectVpn, NetworkCommands.ConnectionParameter, NetworkCommands.Auto)
        ]));

        MenuNode? devices = BuildDevices(snapshot);
        if (devices is not null)
        {
            children.Add(devices);
        }

        MenuNode? slots = BuildSlots(snapshot);
        if (slots is not null)
        {
            children.Add(slots);
        }

        // The root keeps the group name untranslated: the host merges it with the group by name.
        return [Folder(NetworkCommands.Group, children, translate: false)];
    }

    /// <summary>The profiles, split by type when more than one type exists.</summary>
    private MenuNode? BuildConnections()
    {
        IReadOnlyList<NetworkProfile> profiles = selection.Visible();

        if (profiles.Count == 0)
        {
            return null;
        }

        List<IGrouping<NetworkConnectionKind, NetworkProfile>> groups =
        [
            .. profiles
                .GroupBy(profile => profile.Kind == NetworkConnectionKind.WireGuard ? NetworkConnectionKind.Vpn : profile.Kind)
                .OrderBy(group => Array.IndexOf(KindOrder, group.Key))
        ];

        if (groups.Count == 1)
        {
            return Folder("Connections", [.. profiles.Select(BuildProfile)]);
        }

        return Folder("Connections",
        [
            .. groups.Select(group => Folder(
                $"{host.Tr(KindText(group.Key))} ({group.Count()})",
                [.. group.Select(BuildProfile)]))
        ]);
    }

    /// <summary>One profile and everything a button can do with it.</summary>
    private MenuNode BuildProfile(NetworkProfile profile)
    {
        string id = profile.StableId;
        string name = profile.Name;
        List<MenuNode> actions = [ProfileLeaf($"{name} — {host.Tr("Toggle")}", NetworkCommands.Toggle, id)];

        if (profile.IsVpn)
        {
            actions.Add(ProfileLeaf($"{name} — {host.Tr("Connect VPN")}", NetworkCommands.ConnectVpn, id));
            actions.Add(ProfileLeaf($"{name} — {host.Tr("Disconnect VPN")}", NetworkCommands.DisconnectVpn, id));
        }
        else
        {
            actions.Add(ProfileLeaf($"{name} — {host.Tr("Activate")}", NetworkCommands.Activate, id));
            actions.Add(ProfileLeaf($"{name} — {host.Tr("Deactivate")}", NetworkCommands.Deactivate, id));
        }

        actions.Add(ProfileLeaf($"{name} — {host.Tr("Status")}", NetworkDisplayCommands.ProfileStatus, id));
        actions.Add(ProfileLeaf($"{name} — {host.Tr("Active")}", NetworkDisplayCommands.ProfileActive, id));

        bool favorite = settings.Favorites.Contains(id, StringComparer.OrdinalIgnoreCase);
        actions.Add(favorite
            ? ProfileLeaf($"{name} — {host.Tr("Remove from Favorites")}", NetworkCommands.RemoveFavorite, id)
            : ProfileLeaf($"{name} — {host.Tr("Add to Favorites")}", NetworkCommands.AddFavorite, id));

        return Folder(selection.Label(profile, host.Tr), actions, translate: false);
    }

    /// <summary>Every device with connect, disconnect and its own displays.</summary>
    private MenuNode? BuildDevices(NetworkSnapshot snapshot)
    {
        if (snapshot.Devices.Count == 0)
        {
            return null;
        }

        List<MenuNode> devices = [];

        foreach (NetworkDevice device in snapshot.Devices.OrderBy(device => device.Interface, StringComparer.Ordinal))
        {
            string name = device.Interface;
            List<MenuNode> leaves =
            [
                DeviceLeaf($"{name} — {host.Tr("Connect")}", NetworkCommands.ConnectDevice, name),
                DeviceLeaf($"{name} — {host.Tr("Disconnect")}", NetworkCommands.DisconnectDevice, name)
            ];

            leaves.AddRange(NetworkDisplayCommands.DeviceDisplays
                .Where(display => device.Type == "Wi-Fi" || !NetworkDisplayCommands.IsWifiDisplay(display.CommandName))
                .Select(display => DeviceLeaf($"{name} — {host.Tr(display.DisplayName)}", display.CommandName, name)));

            string label = $"{name} · {host.Tr(device.Type)} · {host.Tr(ConnectionStates.ToEnglishText(device.State))}";
            devices.Add(Folder(label, leaves, translate: false));
        }

        return Folder("Devices", devices);
    }

    /// <summary>The slot commands, named after the favorite each one currently shows.</summary>
    private MenuNode? BuildSlots(NetworkSnapshot snapshot)
    {
        IReadOnlyList<string> favorites = settings.Favorites;

        if (favorites.Count == 0)
        {
            return null;
        }

        List<MenuNode> children = [];

        for (int index = 0; index < favorites.Count && index < NetworkSettings.FavoriteSlotCount; index++)
        {
            string number = ConnectionSlotCommands.Number(index);
            string name = NetworkSettings.Resolve(favorites[index], snapshot)?.Name ?? host.Tr("Connection not found");

            children.Add(new MenuNode
            {
                Name = $"{number} — {name}",
                CommandName = NetworkCommands.SlotPrefix + number
            });
        }

        return Folder("Connection Slots", children);
    }

    private static string KindText(NetworkConnectionKind kind) => kind switch
    {
        NetworkConnectionKind.Ethernet => "Ethernet",
        NetworkConnectionKind.Wifi => "Wi-Fi",
        NetworkConnectionKind.Vpn => "VPN",
        _ => "Other"
    };

    private MenuNode Folder(string name, IReadOnlyList<MenuNode> children, bool translate = true) => new()
    {
        Name = translate ? host.Tr(name) : name,
        CommandName = string.Empty,
        Children = children
    };

    private MenuNode Leaf(string name, string commandName) => new()
    {
        Name = host.Tr(name),
        CommandName = commandName
    };

    private MenuNode Leaf(string name, string commandName, string parameter, string value) => new()
    {
        Name = host.Tr(name),
        CommandName = commandName,
        Parameters = new Dictionary<string, string> { [parameter] = value }
    };

    private static MenuNode ProfileLeaf(string name, string commandName, string profileId) => new()
    {
        Name = name,
        CommandName = commandName,
        Parameters = new Dictionary<string, string> { [NetworkCommands.ConnectionParameter] = profileId }
    };

    private static MenuNode DeviceLeaf(string name, string commandName, string interfaceName) => new()
    {
        Name = name,
        CommandName = commandName,
        Parameters = new Dictionary<string, string> { [NetworkCommands.DeviceParameter] = interfaceName }
    };
}
