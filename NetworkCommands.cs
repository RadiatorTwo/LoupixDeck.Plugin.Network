using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Network;

/// <summary>Whether a switch command turns its radio on, off or flips it.</summary>
internal enum SwitchMode
{
    Toggle,
    On,
    Off
}

/// <summary>
/// The names the plugin publishes. A command name is a stable public identifier: a button stores
/// it, so renaming one would break every deck that uses it.
/// </summary>
internal static class NetworkCommands
{
    public const string Prefix = "Network.";
    public const string Group = "Network";

    public const string Wifi = Prefix + "Wifi";
    public const string Networking = Prefix + "Networking";
    public const string Activate = Prefix + "Activate";
    public const string Deactivate = Prefix + "Deactivate";
    public const string Toggle = Prefix + "Toggle";
    public const string ConnectVpn = Prefix + "ConnectVpn";
    public const string DisconnectVpn = Prefix + "DisconnectVpn";
    public const string ConnectDevice = Prefix + "ConnectDevice";
    public const string DisconnectDevice = Prefix + "DisconnectDevice";
    public const string OpenConnections = Prefix + "OpenConnections";
    public const string AddFavorite = Prefix + "AddFavorite";
    public const string RemoveFavorite = Prefix + "RemoveFavorite";

    /// <summary>The prefix of the slot commands, followed by a two-digit number.</summary>
    public const string SlotPrefix = Prefix + "Connection";

    /// <summary>The profile parameter: a UUID, written by the menu.</summary>
    public const string ConnectionParameter = "Connection";
    public const string ConnectionTemplate = "({Connection})";

    /// <summary>The device parameter: an interface name such as wlan0.</summary>
    public const string DeviceParameter = "Device";
    public const string DeviceTemplate = "({Device})";

    /// <summary>The mode parameter of the radio commands: toggle, on or off.</summary>
    public const string ModeParameter = "Mode";
    public const string ModeTemplate = "({Mode})";

    public const string ModeToggle = "toggle";
    public const string ModeOn = "on";
    public const string ModeOff = "off";

    /// <summary>Reads a positional parameter; trailing empty values are dropped by the host.</summary>
    public static string? ReadParameter(CommandContext ctx, int index)
    {
        string? value = ctx.Parameters.Length > index ? ctx.Parameters[index]?.Trim() : null;
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>Reads the mode parameter. Anything unknown, including no value, toggles.</summary>
    public static SwitchMode ReadMode(CommandContext ctx)
    {
        return ReadParameter(ctx, 0)?.ToLowerInvariant() switch
        {
            ModeOn or "1" or "true" or "enable" => SwitchMode.On,
            ModeOff or "0" or "false" or "disable" => SwitchMode.Off,
            _ => SwitchMode.Toggle
        };
    }

    /// <summary>
    /// The descriptor of a command that acts on the profile in its parameter. The caption is left to
    /// the host, because a menu leaf carries the profile name there.
    /// </summary>
    public static CommandDescriptor ProfileDescriptor(
        string commandName,
        string displayName,
        string description,
        string glyph,
        bool hiddenFromMenu = true) => new()
    {
        CommandName = commandName,
        DisplayName = displayName,
        Group = Group,
        Icon = glyph,
        ButtonLayout = NetworkButtonLayouts.IconWithCaption(glyph, caption: null, tall: true),
        Description = description,
        ParameterTemplate = ConnectionTemplate,
        Parameters = [new CommandParameter(ConnectionParameter, typeof(string))],
        HiddenFromMenu = hiddenFromMenu
    };

    /// <summary>The descriptor of a command that acts on the device in its parameter.</summary>
    public static CommandDescriptor DeviceDescriptor(
        string commandName,
        string displayName,
        string description,
        string glyph) => new()
    {
        CommandName = commandName,
        DisplayName = displayName,
        Group = Group,
        Icon = glyph,
        ButtonLayout = NetworkButtonLayouts.IconWithCaption(glyph, caption: null, tall: true),
        Description = description,
        ParameterTemplate = DeviceTemplate,
        Parameters = [new CommandParameter(DeviceParameter, typeof(string))],
        HiddenFromMenu = true
    };
}
