using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Network;

public sealed class NetworkPlugin : LoupixPlugin
{
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

    public override IEnumerable<IPluginCommand> GetCommands() => [];

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
