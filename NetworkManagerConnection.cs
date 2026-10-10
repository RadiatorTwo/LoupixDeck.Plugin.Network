using LoupixDeck.PluginSdk;
using Tmds.DBus.Protocol;

namespace LoupixDeck.Plugin.Network;

/// <summary>
/// Owns the system bus connection and keeps it alive. Nothing here knows anything about networks.
/// <para>
/// A missing bus or a stopped NetworkManager is not an error: the backend simply reports itself as
/// unavailable until NetworkManager appears on the bus.
/// </para>
/// </summary>
internal sealed class NetworkManagerConnection(IPluginLogger logger) : IDisposable
{
    public const string ManagerService = "org.freedesktop.NetworkManager";

    private const string BusService = "org.freedesktop.DBus";
    private const string BusPath = "/org/freedesktop/DBus";
    private const string BusInterface = "org.freedesktop.DBus";

    private static readonly TimeSpan[] ReconnectDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(30)
    ];

    private readonly CancellationTokenSource _shutdown = new();

    private DBusConnection? _connection;
    private IDisposable? _nameOwnerWatch;

    /// <summary>The D-Bus helper, or null while the bus is not connected.</summary>
    public DBusClient? Client { get; private set; }

    /// <summary>True while the system bus is connected. NetworkManager itself may still be absent.</summary>
    public bool IsConnected { get; private set; }

    /// <summary>
    /// Raised once the bus is usable and again whenever NetworkManager (re)appears on it. Everything
    /// read from the bus has to be rebuilt here: object paths belong to the old NetworkManager.
    /// </summary>
    public event Action? ConnectionReady;

    /// <summary>Raised when the bus or NetworkManager went away. The cached state is stale from then on.</summary>
    public event Action? ConnectionLost;

    /// <summary>Connects to the system bus. Safe to call once, from the backend's start.</summary>
    public async Task<bool> ConnectAsync()
    {
        string? address = DBusAddress.System;

        if (string.IsNullOrEmpty(address))
        {
            logger.Info("Network: no system bus address, NetworkManager stays unavailable.");
            return false;
        }

        try
        {
            DBusConnection connection = new(address);
            await connection.ConnectAsync().ConfigureAwait(false);

            _connection = connection;
            Client = new DBusClient(connection, logger);
            _nameOwnerWatch = await WatchManagerOwnerAsync().ConfigureAwait(false);
            IsConnected = true;

            _ = Task.Run(() => MonitorConnectionAsync(connection), _shutdown.Token);
            RaiseConnectionReady();
            return true;
        }
        catch (Exception ex)
        {
            logger.Info($"Network: cannot use the system bus ({ex.Message}), NetworkManager stays unavailable.");
            Disconnect();
            return false;
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _nameOwnerWatch?.Dispose();
        _nameOwnerWatch = null;
        Disconnect();
        _shutdown.Dispose();
    }

    private void RaiseConnectionReady()
    {
        try
        {
            ConnectionReady?.Invoke();
        }
        catch (Exception ex)
        {
            logger.Warn($"Network: a connection handler failed: {ex.Message}");
        }
    }

    private void RaiseConnectionLost()
    {
        try
        {
            ConnectionLost?.Invoke();
        }
        catch (Exception ex)
        {
            logger.Warn($"Network: a disconnect handler failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Watches NetworkManager's bus name. A restart of NetworkManager leaves the bus connection
    /// intact, so this is the only way to notice it.
    /// </summary>
    private Task<IDisposable?> WatchManagerOwnerAsync()
    {
        DBusClient? client = Client;

        if (client is null)
        {
            return Task.FromResult<IDisposable?>(null);
        }

        return client.WatchSignalAsync(
            BusService,
            BusPath,
            BusInterface,
            "NameOwnerChanged",
            ReadNameOwnerChanged,
            OnManagerOwnerChanged);
    }

    private static (string Name, string NewOwner) ReadNameOwnerChanged(Message message, object? state)
    {
        Reader reader = message.GetBodyReader();
        string name = reader.ReadString();
        reader.ReadString(); // old owner
        string newOwner = reader.ReadString();
        return (name, newOwner);
    }

    private void OnManagerOwnerChanged((string Name, string NewOwner) change)
    {
        if (!string.Equals(change.Name, ManagerService, StringComparison.Ordinal))
        {
            return;
        }

        if (string.IsNullOrEmpty(change.NewOwner))
        {
            logger.Info("Network: NetworkManager went away.");
            RaiseConnectionLost();
            return;
        }

        logger.Info("Network: NetworkManager is back, the cached state is rebuilt.");
        RaiseConnectionReady();
    }

    private async Task MonitorConnectionAsync(DBusConnection connection)
    {
        int attempt = 0;

        while (!_shutdown.IsCancellationRequested)
        {
            Exception? error;
            try
            {
                error = await connection.DisconnectedAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                error = ex;
            }

            if (_shutdown.IsCancellationRequested)
            {
                return;
            }

            logger.Warn($"Network: the system bus connection was lost ({error?.Message ?? "unknown reason"}), reconnecting.");
            IsConnected = false;
            RaiseConnectionLost();

            TimeSpan delay = ReconnectDelays[Math.Min(attempt, ReconnectDelays.Length - 1)];
            attempt++;

            try
            {
                await Task.Delay(delay, _shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // A successful reconnect starts its own monitor, so this loop hands over and ends.
            if (await ReconnectAsync().ConfigureAwait(false))
            {
                return;
            }
        }
    }

    private async Task<bool> ReconnectAsync()
    {
        _nameOwnerWatch?.Dispose();
        _nameOwnerWatch = null;
        _connection?.Dispose();
        _connection = null;
        Client = null;

        return await ConnectAsync().ConfigureAwait(false);
    }

    private void Disconnect()
    {
        IsConnected = false;
        Client = null;
        _connection?.Dispose();
        _connection = null;
    }
}
