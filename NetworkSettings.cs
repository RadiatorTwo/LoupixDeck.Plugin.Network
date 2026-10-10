using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Network;

/// <summary>What an activate command does when its profile is already connected.</summary>
internal enum WhenActiveBehavior
{
    Nothing,
    Reactivate
}

/// <summary>
/// Typed access to the plugin's settings. The keys are persisted in
/// <c>plugins/network/settings.json</c> and must not change: a file written by an older version
/// has to keep loading unchanged. Every key is optional and falls back to its default.
/// <para>
/// No credentials ever go in here. Profiles are referenced by their UUID; the secrets stay with
/// NetworkManager.
/// </para>
/// </summary>
internal sealed class NetworkSettings(IPluginSettings settings)
{
    public const string PrimaryInterfaceKey = "interface:primary";
    public const string PreferredVpnKey = "vpn:preferred";
    public const string HiddenKey = "connections:hidden";
    public const string FavoritesKey = "connections:favorites";
    public const string ShowIpKey = "display:showIp";
    public const string WhenActiveKey = "behavior:whenActive";
    public const string ActionTimeoutKey = "behavior:timeoutMs";
    public const string KeepErrorStateKey = "behavior:keepErrorState";
    public const string DBusTimeoutKey = "behavior:dbusTimeoutMs";

    public const string WhenActiveNothing = "nothing";
    public const string WhenActiveReactivate = "reactivate";

    public const bool DefaultShowIp = true;
    public const bool DefaultKeepErrorState = false;
    public const int DefaultActionTimeoutMilliseconds = 30000;
    public const int DefaultDBusTimeoutMilliseconds = 2000;

    /// <summary>How many favorites the slot commands can address.</summary>
    public const int FavoriteSlotCount = 10;

    private const int MinimumActionTimeoutMilliseconds = 1000;
    private const int MaximumActionTimeoutMilliseconds = 120000;
    private const int MinimumDBusTimeoutMilliseconds = 250;
    private const int MaximumDBusTimeoutMilliseconds = 10000;

    private static readonly char[] ListSeparators = [',', ';', '\n', '\r'];

    /// <summary>The interface the interface displays show, or empty to follow the primary connection.</summary>
    public string PrimaryInterface => (settings.Get(PrimaryInterfaceKey, string.Empty) ?? string.Empty).Trim();

    /// <summary>The VPN that Connect VPN uses when its button names none: a UUID, or a name until the next save.</summary>
    public string PreferredVpn => (settings.Get(PreferredVpnKey, string.Empty) ?? string.Empty).Trim();

    /// <summary>Profiles kept out of the menu and the folder, by UUID or name.</summary>
    public IReadOnlyList<string> Hidden => ParseList(settings.Get(HiddenKey, string.Empty));

    /// <summary>The profiles behind the slot commands, in slot order.</summary>
    public IReadOnlyList<string> Favorites => ParseList(settings.Get(FavoritesKey, string.Empty));

    public bool ShowIpAddresses => settings.Get(ShowIpKey, DefaultShowIp);

    public WhenActiveBehavior WhenActive =>
        string.Equals((settings.Get(WhenActiveKey, WhenActiveNothing) ?? string.Empty).Trim(), WhenActiveReactivate, StringComparison.OrdinalIgnoreCase)
            ? WhenActiveBehavior.Reactivate
            : WhenActiveBehavior.Nothing;

    /// <summary>How long a command waits for a connection to come up or go down.</summary>
    public TimeSpan ActionTimeout => TimeSpan.FromMilliseconds(
        ReadClamped(ActionTimeoutKey, DefaultActionTimeoutMilliseconds, MinimumActionTimeoutMilliseconds, MaximumActionTimeoutMilliseconds));

    /// <summary>On: a failed activation stays Failed until the next action. Off: it falls back to the real state.</summary>
    public bool KeepErrorState => settings.Get(KeepErrorStateKey, DefaultKeepErrorState);

    public int DBusTimeoutMilliseconds =>
        ReadClamped(DBusTimeoutKey, DefaultDBusTimeoutMilliseconds, MinimumDBusTimeoutMilliseconds, MaximumDBusTimeoutMilliseconds);

    /// <summary>The favorite behind a slot command, or null when the slot is empty.</summary>
    public string? FavoriteAt(int slotIndex)
    {
        IReadOnlyList<string> favorites = Favorites;
        return slotIndex >= 0 && slotIndex < favorites.Count ? favorites[slotIndex] : null;
    }

    /// <summary>Adds a profile to the favorites. Returns false when it was already there.</summary>
    public bool AddFavorite(string profileId)
    {
        List<string> favorites = [.. Favorites];

        if (favorites.Contains(profileId, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        favorites.Add(profileId);
        WriteList(FavoritesKey, favorites);
        return true;
    }

    /// <summary>Removes a profile from the favorites. Returns false when it was not there.</summary>
    public bool RemoveFavorite(string profileId)
    {
        List<string> favorites = [.. Favorites];

        if (favorites.RemoveAll(entry => string.Equals(entry, profileId, StringComparison.OrdinalIgnoreCase)) == 0)
        {
            return false;
        }

        WriteList(FavoritesKey, favorites);
        return true;
    }

    public void ClearFavorites() => WriteList(FavoritesKey, []);

    /// <summary>
    /// Replaces profile names typed into the settings by their UUID, so a later rename does not
    /// break them. Entries that match no profile are kept as they are: the profile may simply not
    /// be loaded yet. Returns true when anything was rewritten.
    /// </summary>
    public bool NormalizeProfileReferences(NetworkSnapshot snapshot)
    {
        if (!snapshot.IsAvailable)
        {
            return false;
        }

        bool changed = false;

        string vpn = PreferredVpn;
        string resolvedVpn = ToStableId(vpn, snapshot);
        if (!string.Equals(vpn, resolvedVpn, StringComparison.Ordinal))
        {
            settings.Set(PreferredVpnKey, resolvedVpn);
            changed = true;
        }

        changed |= NormalizeList(HiddenKey, Hidden, snapshot);
        changed |= NormalizeList(FavoritesKey, Favorites, snapshot);

        if (changed)
        {
            settings.Save();
        }

        return changed;
    }

    /// <summary>Finds the profile an entry names, by UUID first and by exact name second.</summary>
    public static NetworkProfile? Resolve(string? entry, NetworkSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(entry))
        {
            return null;
        }

        return snapshot.FindProfile(entry) ??
               snapshot.Profiles.FirstOrDefault(profile => string.Equals(profile.Name, entry, StringComparison.Ordinal));
    }

    private bool NormalizeList(string key, IReadOnlyList<string> entries, NetworkSnapshot snapshot)
    {
        List<string> resolved = [.. entries.Select(entry => ToStableId(entry, snapshot)).Distinct(StringComparer.OrdinalIgnoreCase)];

        if (resolved.SequenceEqual(entries, StringComparer.Ordinal))
        {
            return false;
        }

        settings.Set(key, string.Join(", ", resolved));
        return true;
    }

    private static string ToStableId(string entry, NetworkSnapshot snapshot) =>
        Resolve(entry, snapshot)?.StableId ?? entry;

    private void WriteList(string key, IEnumerable<string> entries)
    {
        settings.Set(key, string.Join(", ", entries));
        settings.Save();
    }

    /// <summary>
    /// The SDK has no list editor, so a list is a single text field; commas, semicolons and line
    /// breaks all separate entries. Blanks and duplicates are dropped.
    /// </summary>
    private static IReadOnlyList<string> ParseList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        List<string> entries = [];

        foreach (string part in value.Split(ListSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!entries.Contains(part, StringComparer.OrdinalIgnoreCase))
            {
                entries.Add(part);
            }
        }

        return entries;
    }

    /// <summary>
    /// Reads a number setting and keeps it inside its supported range. The host stores it as a
    /// JSON integer and falls back to the default when a stored value cannot be read as one.
    /// </summary>
    private int ReadClamped(string key, int fallback, int minimum, int maximum) =>
        (int)Math.Clamp(settings.Get(key, (long)fallback), minimum, maximum);
}
