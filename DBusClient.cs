using System.Collections.Concurrent;
using System.Diagnostics;
using LoupixDeck.PluginSdk;
using Tmds.DBus.Protocol;

namespace LoupixDeck.Plugin.Network;

/// <summary>
/// Writes the arguments of a D-Bus method call into the message body.
/// A dedicated delegate is required because <see cref="MessageWriter"/> is a ref struct
/// and therefore cannot be used as a generic type argument.
/// </summary>
internal delegate void DBusArgumentWriter(ref MessageWriter writer);

/// <summary>The value a call produced together with how the call ended.</summary>
internal readonly record struct DBusResult<T>(T Value, NetworkCallOutcome Outcome)
{
    public bool IsSuccess => Outcome == NetworkCallOutcome.Ok;
}

/// <summary>
/// Thin helper around the raw D-Bus protocol API: it builds the calls, applies the call timeout
/// and rate-limits the resulting log output so an unresponsive NetworkManager cannot flood the log.
/// <para>
/// It does not swallow failures: every call reports a <see cref="NetworkCallOutcome"/>, because
/// telling "denied" apart from "failed" is what makes the PolicyKit handling work.
/// </para>
/// </summary>
internal sealed class DBusClient(DBusConnection connection, IPluginLogger logger)
{
    public const string PropertiesInterface = "org.freedesktop.DBus.Properties";

    private const int MinimumTimeoutMilliseconds = 250;
    private const int MaximumTimeoutMilliseconds = 10000;

    private static readonly TimeSpan WarnInterval = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, long> _lastWarnTimestamps = new(StringComparer.Ordinal);

    private int _timeoutMilliseconds = 2000;

    public DBusConnection Connection { get; } = connection;

    /// <summary>Per-call timeout. Values outside the supported range are clamped.</summary>
    public int TimeoutMilliseconds
    {
        get => _timeoutMilliseconds;
        set => _timeoutMilliseconds = Math.Clamp(value, MinimumTimeoutMilliseconds, MaximumTimeoutMilliseconds);
    }

    /// <summary>
    /// Calls a method and ignores the reply body. <paramref name="timeout"/> replaces the per-call
    /// timeout for the rare call that legitimately takes longer, such as a daemon-reload.
    /// </summary>
    public async Task<NetworkCallOutcome> CallAsync(
        string destination,
        string path,
        string @interface,
        string member,
        string? signature = null,
        DBusArgumentWriter? writeArguments = null,
        TimeSpan? timeout = null)
    {
        try
        {
            MessageBuffer message = CreateCall(destination, path, @interface, member, signature, writeArguments);
            await Connection.CallMethodAsync(message).WaitAsync(timeout ?? Timeout).ConfigureAwait(false);
            return NetworkCallOutcome.Ok;
        }
        catch (Exception ex)
        {
            return Fail(destination, path, member, ex);
        }
    }

    /// <summary>Calls a method and reads its reply.</summary>
    public async Task<DBusResult<T>> CallAsync<T>(
        string destination,
        string path,
        string @interface,
        string member,
        MessageValueReader<T> reader,
        T fallback,
        string? signature = null,
        DBusArgumentWriter? writeArguments = null)
    {
        try
        {
            MessageBuffer message = CreateCall(destination, path, @interface, member, signature, writeArguments);
            T value = await Connection.CallMethodAsync(message, reader, null).WaitAsync(Timeout).ConfigureAwait(false);
            return new DBusResult<T>(value, NetworkCallOutcome.Ok);
        }
        catch (Exception ex)
        {
            return new DBusResult<T>(fallback, Fail(destination, path, member, ex));
        }
    }

    /// <summary>Reads every property of an interface through org.freedesktop.DBus.Properties.GetAll.</summary>
    public Task<DBusResult<Dictionary<string, VariantValue>>> GetAllPropertiesAsync(
        string destination,
        string path,
        string @interface)
    {
        return CallAsync(
            destination,
            path,
            PropertiesInterface,
            "GetAll",
            ReadPropertyDictionary,
            [],
            "s",
            (ref MessageWriter writer) => writer.WriteString(@interface));
    }

    /// <summary>
    /// Subscribes to a signal of one object. The handler runs on the connection read loop, so it
    /// must return quickly and must never block. Returns null when the subscription failed.
    /// </summary>
    public async Task<IDisposable?> WatchSignalAsync<T>(
        string sender,
        string path,
        string @interface,
        string signal,
        MessageValueReader<T> reader,
        Action<T> handler)
    {
        try
        {
            return await Connection.WatchSignalAsync(
                sender,
                path,
                @interface,
                signal,
                reader,
                CreateNotificationHandler(handler, $"{@interface}.{signal}", sender, path, signal),
                flags: ObserverFlags.None,
                emitOnCapturedContext: false).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Fail(sender, path, signal, ex);
            return null;
        }
    }

    /// <summary>
    /// Subscribes with an explicit match rule. NetworkManager publishes property changes on one
    /// object per device, connection and address, so only a rule with a path namespace can cover
    /// all of them with a single match.
    /// </summary>
    public async Task<IDisposable?> WatchMatchAsync<T>(
        MatchRule rule,
        MessageValueReader<T> reader,
        Action<T> handler)
    {
        string description = $"{rule.Interface}.{rule.Member}";

        try
        {
            return await Connection.AddMatchAsync(
                rule,
                reader,
                CreateNotificationHandler(handler, description, rule.Sender ?? string.Empty, rule.PathNamespace ?? rule.Path ?? string.Empty, rule.Member ?? string.Empty),
                emitOnCapturedContext: false,
                flags: ObserverFlags.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Fail(rule.Sender ?? string.Empty, rule.PathNamespace ?? rule.Path ?? string.Empty, rule.Member ?? string.Empty, ex);
            return null;
        }
    }

    /// <summary>Reads an a{sv} reply, the shape of org.freedesktop.DBus.Properties.GetAll.</summary>
    public static Dictionary<string, VariantValue> ReadPropertyDictionary(Message message, object? state)
    {
        Reader reader = message.GetBodyReader();
        return reader.ReadDictionaryOfStringToVariantValue();
    }

    /// <summary>Reads a reply that carries a single object path.</summary>
    public static string ReadObjectPath(Message message, object? state)
    {
        Reader reader = message.GetBodyReader();
        return reader.ReadObjectPathAsString();
    }

    private TimeSpan Timeout => TimeSpan.FromMilliseconds(_timeoutMilliseconds);

    private Action<Notification<T>> CreateNotificationHandler<T>(
        Action<T> handler,
        string description,
        string sender,
        string path,
        string member)
    {
        return notification =>
        {
            // Exception is only readable on a completion notification, and reading it on a
            // value notification throws inside the read loop, which tears down the connection.
            if (notification.IsCompletion)
            {
                if (notification.Exception is not null)
                {
                    Fail(sender, path, member, notification.Exception);
                }

                return;
            }

            if (!notification.HasValue)
            {
                return;
            }

            try
            {
                handler(notification.Value);
            }
            catch (Exception ex)
            {
                logger.Error($"Network: signal handler for {description} failed.", ex);
            }
        };
    }

    private MessageBuffer CreateCall(
        string destination,
        string path,
        string @interface,
        string member,
        string? signature,
        DBusArgumentWriter? writeArguments)
    {
        MessageWriter writer = Connection.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(destination, path, @interface, member, signature);
            writeArguments?.Invoke(ref writer);
            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
    }

    private NetworkCallOutcome Fail(string destination, string path, string member, Exception exception)
    {
        NetworkCallOutcome outcome = NetworkErrors.Map(exception);

        // A few D-Bus errors mean the caller already has what it asked for. They are not worth a
        // warning, and they are not a failure either.
        if (outcome == NetworkCallOutcome.Ok)
        {
            return outcome;
        }

        string key = string.Concat(destination, path, member, outcome.ToString());
        long now = Stopwatch.GetTimestamp();

        if (_lastWarnTimestamps.TryGetValue(key, out long last) && Stopwatch.GetElapsedTime(last, now) < WarnInterval)
        {
            return outcome;
        }

        _lastWarnTimestamps[key] = now;
        logger.Warn($"Network: D-Bus call {destination}{path} {member} failed: {exception.Message}");
        return outcome;
    }
}
