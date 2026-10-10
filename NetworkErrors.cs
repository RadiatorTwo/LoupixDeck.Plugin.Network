using Tmds.DBus.Protocol;

namespace LoupixDeck.Plugin.Network;

/// <summary>How a call to the network backend ended. Anything but <see cref="Ok"/> is shown to the user.</summary>
public enum NetworkCallOutcome
{
    Ok,

    /// <summary>PolicyKit refused the call. The connection itself is untouched and must not look failed.</summary>
    PermissionDenied,

    /// <summary>The backend does not know this connection or device.</summary>
    NotFound,

    /// <summary>The profile exists, but no device it could run on is usable (no cable, radio off).</summary>
    NoDevice,

    /// <summary>The bus or NetworkManager is not reachable right now.</summary>
    Unavailable,

    /// <summary>The request was accepted but did not finish in time.</summary>
    Timeout,

    /// <summary>The call reached the backend and was rejected, or the activation failed.</summary>
    Failed
}

/// <summary>Maps a D-Bus error onto a <see cref="NetworkCallOutcome"/>.</summary>
internal static class NetworkErrors
{
    public const string AccessDenied = "org.freedesktop.DBus.Error.AccessDenied";
    public const string InteractiveAuthorizationRequired = "org.freedesktop.DBus.Error.InteractiveAuthorizationRequired";
    public const string PolicyKitNotAuthorized = "org.freedesktop.PolicyKit1.Error.NotAuthorized";
    public const string ManagerPermissionDenied = "org.freedesktop.NetworkManager.PermissionDenied";
    public const string SettingsPermissionDenied = "org.freedesktop.NetworkManager.Settings.PermissionDenied";

    public const string UnknownConnection = "org.freedesktop.NetworkManager.UnknownConnection";
    public const string UnknownDevice = "org.freedesktop.NetworkManager.UnknownDevice";
    public const string UnknownObject = "org.freedesktop.DBus.Error.UnknownObject";
    public const string UnknownMethod = "org.freedesktop.DBus.Error.UnknownMethod";

    /// <summary>
    /// Answers to a deactivation of something that is already down. The caller already has what it
    /// asked for, so they count as success.
    /// </summary>
    public const string ConnectionNotActive = "org.freedesktop.NetworkManager.ConnectionNotActive";
    public const string DeviceNotActive = "org.freedesktop.NetworkManager.Device.NotActive";

    public const string ServiceUnknown = "org.freedesktop.DBus.Error.ServiceUnknown";
    public const string NameHasNoOwner = "org.freedesktop.DBus.Error.NameHasNoOwner";
    public const string NoReply = "org.freedesktop.DBus.Error.NoReply";
    public const string Disconnected = "org.freedesktop.DBus.Error.Disconnected";

    /// <summary>Classifies an exception raised by a D-Bus call.</summary>
    public static NetworkCallOutcome Map(Exception? exception)
    {
        return exception switch
        {
            null => NetworkCallOutcome.Ok,
            DBusErrorReplyException reply => Map(reply.ErrorName),
            TimeoutException => NetworkCallOutcome.Unavailable,
            OperationCanceledException => NetworkCallOutcome.Unavailable,
            ObjectDisposedException => NetworkCallOutcome.Unavailable,
            _ => NetworkCallOutcome.Failed
        };
    }

    /// <summary>Classifies a D-Bus error name.</summary>
    public static NetworkCallOutcome Map(string? errorName)
    {
        return errorName switch
        {
            null or "" => NetworkCallOutcome.Failed,
            ConnectionNotActive or DeviceNotActive => NetworkCallOutcome.Ok,
            AccessDenied or InteractiveAuthorizationRequired or PolicyKitNotAuthorized
                or ManagerPermissionDenied or SettingsPermissionDenied => NetworkCallOutcome.PermissionDenied,
            UnknownConnection or UnknownObject or UnknownMethod => NetworkCallOutcome.NotFound,
            UnknownDevice => NetworkCallOutcome.NoDevice,
            ServiceUnknown or NameHasNoOwner or NoReply or Disconnected => NetworkCallOutcome.Unavailable,
            _ => NetworkCallOutcome.Failed
        };
    }

    /// <summary>The English text a button or the log shows for an outcome.</summary>
    public static string ToEnglishText(NetworkCallOutcome outcome)
    {
        return outcome switch
        {
            NetworkCallOutcome.Ok => "Done",
            NetworkCallOutcome.PermissionDenied => "Not allowed",
            NetworkCallOutcome.NotFound => "Connection not found",
            NetworkCallOutcome.NoDevice => "No usable device",
            NetworkCallOutcome.Unavailable => "NetworkManager unavailable",
            NetworkCallOutcome.Timeout => "Timed out",
            _ => "Failed"
        };
    }
}
