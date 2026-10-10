namespace LoupixDeck.Plugin.Network;

/// <summary>
/// Decides which profiles the menu, the folder and the settings offer and how they are labelled:
/// hidden profiles are left out, active ones come first, and a label names the profile by what the
/// user called it — never by its UUID.
/// </summary>
internal sealed class ConnectionSelectionService(NetworkStateStore store, NetworkSettings settings)
{
    /// <summary>The profiles to offer, active ones first, then by name.</summary>
    public IReadOnlyList<NetworkProfile> Visible(bool vpnOnly = false)
    {
        NetworkSnapshot snapshot = store.Current;
        IReadOnlyList<string> hidden = settings.Hidden;

        return
        [
            .. snapshot.Profiles
                .Where(profile => !vpnOnly || profile.IsVpn)
                .Where(profile => !IsHidden(profile, hidden))
                .OrderBy(profile => snapshot.FindActive(profile.StableId) is null)
                .ThenBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase)
        ];
    }

    /// <summary>The profile a button parameter or a setting names, by UUID or by name.</summary>
    public NetworkProfile? Resolve(string? entry) => NetworkSettings.Resolve(entry, store.Current);

    /// <summary>"Mullvad · VPN · wg0 · Connected": name, type, interface and current state.</summary>
    public string Label(NetworkProfile profile, Func<string, string> translate)
    {
        List<string> parts = [profile.Name, translate(ConnectionStates.TypeText(profile.Type))];

        string interfaceName = InterfaceOf(profile);
        if (interfaceName.Length > 0)
        {
            parts.Add(interfaceName);
        }

        parts.Add(translate(ConnectionStates.ToEnglishText(store.StateOf(profile.StableId))));
        return string.Join(" · ", parts);
    }

    /// <summary>The interface a profile runs on now, or the one it is bound to.</summary>
    public string InterfaceOf(NetworkProfile profile)
    {
        ActiveNetworkConnection? active = store.Current.FindActive(profile.StableId);

        return active is { Interfaces.Count: > 0 }
            ? active.Interfaces[0]
            : profile.InterfaceName;
    }

    private static bool IsHidden(NetworkProfile profile, IReadOnlyList<string> hidden) =>
        hidden.Any(entry =>
            string.Equals(entry, profile.StableId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(entry, profile.Name, StringComparison.Ordinal));
}
