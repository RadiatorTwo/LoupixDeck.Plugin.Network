namespace LoupixDeck.Plugin.Network;

/// <summary>
/// The platform side of the plugin. Everything above it — commands, displays, the folder and the
/// menu — only sees <see cref="NetworkSnapshot"/> and stable profile ids, so a second backend (for
/// example native Windows) can be added without touching them.
/// <para>
/// Actions only report whether the backend accepted the request. Waiting for the connection to come
/// up or go down is done on the snapshots, in <see cref="NetworkStateStore"/>.
/// </para>
/// </summary>
internal interface INetworkBackend : IDisposable
{
    /// <summary>The latest snapshot. Never null; <see cref="NetworkSnapshot.Unavailable"/> while not connected.</summary>
    NetworkSnapshot Current { get; }

    /// <summary>Per-call timeout for the backend's own calls.</summary>
    int CallTimeoutMilliseconds { get; set; }

    /// <summary>Raised on a background thread whenever <see cref="Current"/> was replaced.</summary>
    event Action? Changed;

    /// <summary>Raised when an activation ended in a failure, with the profile id and an English reason.</summary>
    event Action<string, string>? ActivationFailed;

    /// <summary>Connects and loads the first snapshot. Never throws.</summary>
    Task StartAsync();

    Task<NetworkCallOutcome> ActivateAsync(string profileId);

    Task<NetworkCallOutcome> DeactivateAsync(string profileId);

    Task<NetworkCallOutcome> SetWifiEnabledAsync(bool enabled);

    Task<NetworkCallOutcome> SetNetworkingEnabledAsync(bool enabled);

    Task<NetworkCallOutcome> ConnectDeviceAsync(string interfaceName);

    Task<NetworkCallOutcome> DisconnectDeviceAsync(string interfaceName);
}

/// <summary>The backend for a platform the plugin has no implementation for yet.</summary>
internal sealed class UnsupportedNetworkBackend : INetworkBackend
{
    public NetworkSnapshot Current => NetworkSnapshot.Unavailable;

    public int CallTimeoutMilliseconds { get; set; }

    public event Action? Changed
    {
        add { }
        remove { }
    }

    public event Action<string, string>? ActivationFailed
    {
        add { }
        remove { }
    }

    public Task StartAsync() => Task.CompletedTask;

    public Task<NetworkCallOutcome> ActivateAsync(string profileId) => Unavailable();

    public Task<NetworkCallOutcome> DeactivateAsync(string profileId) => Unavailable();

    public Task<NetworkCallOutcome> SetWifiEnabledAsync(bool enabled) => Unavailable();

    public Task<NetworkCallOutcome> SetNetworkingEnabledAsync(bool enabled) => Unavailable();

    public Task<NetworkCallOutcome> ConnectDeviceAsync(string interfaceName) => Unavailable();

    public Task<NetworkCallOutcome> DisconnectDeviceAsync(string interfaceName) => Unavailable();

    public void Dispose()
    {
    }

    private static Task<NetworkCallOutcome> Unavailable() => Task.FromResult(NetworkCallOutcome.Unavailable);
}
