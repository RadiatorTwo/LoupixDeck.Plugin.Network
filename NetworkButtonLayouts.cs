using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Network;

/// <summary>
/// The icon and caption every Network command brings to a touch button, set by the plugin instead
/// of the host's standard look. The geometry follows the host's own icon-and-caption look, so a
/// Network button sits next to any other button without looking out of place.
/// </summary>
internal static class NetworkButtonLayouts
{
    // Material Design Icons code points, one per role so a command and its layout cannot drift apart.
    public const string Group = "\U000F06F3";             // mdi-network
    public const string Wifi = "\U000F05A9";              // mdi-wifi
    public const string Networking = "\U000F0317";        // mdi-lan
    public const string Activate = "\U000F0318";          // mdi-lan-connect
    public const string Deactivate = "\U000F0319";        // mdi-lan-disconnect
    public const string Toggle = "\U000F04E1";            // mdi-swap-horizontal
    public const string ConnectVpn = "\U000F099D";        // mdi-shield-lock
    public const string DisconnectVpn = "\U000F099E";     // mdi-shield-off
    public const string ConnectDevice = "\U000F0201";     // mdi-ethernet-cable
    public const string DisconnectDevice = "\U000F0202";  // mdi-ethernet-cable-off
    public const string ConnectionsFolder = "\U000F0870"; // mdi-folder-network
    public const string AddFavorite = "\U000F1567";       // mdi-star-plus-outline
    public const string RemoveFavorite = "\U000F1568";    // mdi-star-minus-outline
    public const string Slot = "\U000F04CE";              // mdi-star
    public const string Status = "\U000F0C54";            // mdi-check-network-outline
    public const string Connectivity = "\U000F059F";      // mdi-web
    public const string Primary = "\U000F1087";           // mdi-router-network
    public const string Vpn = "\U000F0582";               // mdi-vpn
    public const string Ssid = "\U000F0003";              // mdi-access-point
    public const string Signal = "\U000F0928";            // mdi-wifi-strength-4
    public const string Interface = "\U000F0200";         // mdi-ethernet
    public const string Ipv4 = "\U000F0A5F";              // mdi-ip
    public const string Ipv6 = "\U000F0A60";              // mdi-ip-network
    public const string Gateway = "\U000F0299";           // mdi-gate
    public const string Dns = "\U000F01D6";               // mdi-dns
    public const string ConnectionType = "\U000F027A";    // mdi-format-list-bulleted-type
    public const string ProfileActive = "\U000F0565";     // mdi-shield-check

    // Pixel values for a 90 px key; the host scales them onto the key actually being written.
    private const double IconScale = 0.5;
    private const int IconOffsetY = -9;
    private const int CaptionSize = 11;
    private const int CaptionOffsetY = 27;
    private const int CaptionBoxWidth = 88;
    private const int CaptionBoxHeight = 22;

    // The tall variant leaves room for two lines of text: a menu leaf is named "profile — action",
    // and a display button replaces the caption with its value, which can be a name and a state.
    private const double TallIconScale = 0.36;
    private const int TallIconOffsetY = -19;
    private const int TallCaptionOffsetY = 17;
    private const int TallCaptionBoxHeight = 40;

    /// <summary>
    /// Translates a caption into the host's language. Set once the plugin is initialised; the layout
    /// is stored on the button at assignment, so it is the language at that moment that counts.
    /// </summary>
    internal static Func<string, string> Translate { get; set; } = static english => english;

    /// <summary>The icon centred above a caption.</summary>
    /// <param name="glyph">The icon.</param>
    /// <param name="caption">English caption; null keeps the name the host would show, which is the
    /// right thing for a profile command: a menu leaf carries the profile name there.</param>
    /// <param name="tall">Gives the caption two lines and shrinks the icon to make room.</param>
    /// <param name="color">The icon's tint, or null for the standard white.</param>
    public static ButtonLayoutDescriptor IconWithCaption(
        string glyph,
        string? caption,
        bool tall = false,
        string? color = null) => new()
    {
        Mode = ButtonLayoutMode.Custom,
        Layers =
        [
            new ButtonLayerDescriptor
            {
                Kind = ButtonLayerKind.Symbol,
                Glyph = glyph,
                IconScale = tall ? TallIconScale : IconScale,
                OffsetY = tall ? TallIconOffsetY : IconOffsetY,
                Color = color
            },
            new ButtonLayerDescriptor
            {
                Kind = ButtonLayerKind.Text,
                Text = caption == null ? null : Translate(caption),
                TextSize = CaptionSize,
                OffsetY = tall ? TallCaptionOffsetY : CaptionOffsetY,
                BoxWidth = CaptionBoxWidth,
                BoxHeight = tall ? TallCaptionBoxHeight : CaptionBoxHeight
            }
        ]
    };
}
