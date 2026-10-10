using System.Collections.Concurrent;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Network;

/// <summary>How a profile action ended, with the backend's reason when it failed.</summary>
internal readonly record struct NetworkActionResult(NetworkCallOutcome Outcome, string? Reason = null)
{
    public bool IsSuccess => Outcome == NetworkCallOutcome.Ok;

    /// <summary>The English text a button shows for the result.</summary>
    public string Text => Reason ?? NetworkErrors.ToEnglishText(Outcome);
}

/// <summary>
/// The plugin's view of the network: the backend's latest snapshot plus the failures the plugin
/// remembers itself. Commands act through it, because it is what waits for a profile to actually
/// come up or go down; the backend only says whether it accepted the request.
/// <para>
/// Everything here reads cached data, so it is safe to call from a render path.
/// </para>
/// </summary>
internal sealed class NetworkStateStore : IDisposable
{
    /// <summary>How long a failure stays visible when the error state is not kept.</summary>
    private static readonly TimeSpan BriefFailure = TimeSpan.FromSeconds(5);

    private readonly INetworkBackend _backend;
    private readonly NetworkSettings _settings;
    private readonly IPluginLogger _logger;
    private readonly ConcurrentDictionary<string, Failure> _failures = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The wait of the latest action per profile; a newer action cancels the older wait.</summary>
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _actions = new(StringComparer.OrdinalIgnoreCase);

    public NetworkStateStore(INetworkBackend backend, NetworkSettings settings, IPluginLogger logger)
    {
        _backend = backend;
        _settings = settings;
        _logger = logger;

        _backend.Changed += OnBackendChanged;
        _backend.ActivationFailed += OnActivationFailed;
    }

    /// <summary>Raised on a background thread after the snapshot or a remembered failure changed.</summary>
    public event Action? Changed;

    public NetworkSnapshot Current => _backend.Current;

    public INetworkBackend Backend => _backend;

    public void Dispose()
    {
        _backend.Changed -= OnBackendChanged;
        _backend.ActivationFailed -= OnActivationFailed;
    }

    /// <summary>The state a profile is in, with remembered failures taking precedence.</summary>
    public ConnectionState StateOf(string? profileId) => StateOf(profileId, Current);

    public ConnectionState StateOf(string? profileId, NetworkSnapshot snapshot)
    {
        if (!snapshot.IsAvailable || string.IsNullOrEmpty(profileId))
        {
            return ConnectionState.Unavailable;
        }

        if (_failures.TryGetValue(profileId, out Failure failure) && !failure.IsExpired)
        {
            return ConnectionState.Failed;
        }

        NetworkProfile? profile = snapshot.FindProfile(profileId);

        if (profile is null)
        {
            // Deleted, or not readable by this user.
            return ConnectionState.Unavailable;
        }

        if (snapshot.FindActive(profileId) is { } active)
        {
            return active.State;
        }

        // Nothing comes up while networking is off, and no Wi-Fi profile while the radio is off.
        if (!snapshot.NetworkingEnabled ||
            (profile.Kind == NetworkConnectionKind.Wifi && (!snapshot.WifiEnabled || !snapshot.WifiHardwareEnabled)))
        {
            return ConnectionState.Unavailable;
        }

        // A profile bound to an interface that cannot be used (no cable, radio off) cannot come up.
        if (profile.InterfaceName.Length > 0 &&
            snapshot.FindDevice(profile.InterfaceName) is { State: ConnectionState.Unavailable })
        {
            return ConnectionState.Unavailable;
        }

        return ConnectionState.Disconnected;
    }

    /// <summary>The reason of a remembered failure, or null.</summary>
    public string? FailureOf(string profileId) =>
        _failures.TryGetValue(profileId, out Failure failure) && !failure.IsExpired ? failure.Reason : null;

    /// <summary>
    /// Raises <see cref="Changed"/> after something outside the snapshot changed what the buttons
    /// show, such as the favorites list behind the slots.
    /// </summary>
    public void NotifySettingsChanged() => RaiseChanged();

    /// <summary>Brings a profile up and waits until it is connected, failed or the timeout ran out.</summary>
    public async Task<NetworkActionResult> ActivateAsync(string profileId)
    {
        CancellationToken superseded = BeginAction(profileId);

        // A reactivation starts from Connected, so the current state must not count as done.
        bool wasConnected = StateOf(profileId) == ConnectionState.Connected;

        NetworkCallOutcome outcome = await _backend.ActivateAsync(profileId).ConfigureAwait(false);

        if (outcome != NetworkCallOutcome.Ok)
        {
            return new NetworkActionResult(outcome);
        }

        NetworkActionResult result = await WaitForAsync(
            profileId,
            state => state == ConnectionState.Connected,
            checkNow: !wasConnected,
            superseded).ConfigureAwait(false);

        if (result.Outcome == NetworkCallOutcome.Timeout && StateOf(profileId) == ConnectionState.Connecting)
        {
            RememberFailure(profileId, "Timed out");
        }

        return result;
    }

    /// <summary>Takes a profile down and waits until it is down or the timeout ran out.</summary>
    public async Task<NetworkActionResult> DeactivateAsync(string profileId)
    {
        CancellationToken superseded = BeginAction(profileId);

        NetworkCallOutcome outcome = await _backend.DeactivateAsync(profileId).ConfigureAwait(false);

        if (outcome != NetworkCallOutcome.Ok)
        {
            return new NetworkActionResult(outcome);
        }

        return await WaitForAsync(
            profileId,
            state => state is ConnectionState.Disconnected or ConnectionState.Unavailable,
            checkNow: true,
            superseded).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts an action on a profile: forgets its old failure and ends the wait of any earlier
    /// action, so cancelling a connection that hangs does not later report the first press as
    /// timed out.
    /// </summary>
    private CancellationToken BeginAction(string profileId)
    {
        ClearFailure(profileId);

        CancellationTokenSource current = new();
        CancellationTokenSource? previous = null;

        _actions.AddOrUpdate(
            profileId,
            current,
            (_, old) =>
            {
                previous = old;
                return current;
            });

        previous?.Cancel();
        return current.Token;
    }

    /// <summary>
    /// Waits for a profile to reach a state. A failure reported by the backend ends the wait early,
    /// because NetworkManager drops a failed activation instead of keeping it in a failed state. A
    /// newer action on the same profile ends it quietly.
    /// </summary>
    private async Task<NetworkActionResult> WaitForAsync(
        string profileId,
        Func<ConnectionState, bool> reached,
        bool checkNow,
        CancellationToken superseded)
    {
        TaskCompletionSource<NetworkActionResult> done = new(TaskCreationOptions.RunContinuationsAsynchronously);

        void Check()
        {
            if (_failures.TryGetValue(profileId, out Failure failure) && !failure.IsExpired)
            {
                done.TrySetResult(new NetworkActionResult(NetworkCallOutcome.Failed, failure.Reason));
                return;
            }

            if (reached(StateOf(profileId)))
            {
                done.TrySetResult(new NetworkActionResult(NetworkCallOutcome.Ok));
            }
        }

        Changed += Check;
        try
        {
            if (checkNow)
            {
                Check();
            }

            using CancellationTokenRegistration registration =
                superseded.Register(() => done.TrySetResult(new NetworkActionResult(NetworkCallOutcome.Ok)));

            Task finished = await Task.WhenAny(done.Task, Task.Delay(_settings.ActionTimeout)).ConfigureAwait(false);
            return finished == done.Task ? done.Task.Result : new NetworkActionResult(NetworkCallOutcome.Timeout);
        }
        finally
        {
            Changed -= Check;
        }
    }

    private void OnBackendChanged()
    {
        // A profile that came up after all no longer counts as failed.
        foreach (string profileId in _failures.Keys)
        {
            if (Current.FindActive(profileId) is { State: ConnectionState.Connected })
            {
                _failures.TryRemove(profileId, out _);
            }
        }

        RaiseChanged();
    }

    private void OnActivationFailed(string profileId, string reason) => RememberFailure(profileId, reason);

    private void RememberFailure(string profileId, string reason)
    {
        bool keep = _settings.KeepErrorState;
        DateTimeOffset? expires = keep ? null : DateTimeOffset.UtcNow + BriefFailure;
        _failures[profileId] = new Failure(reason, expires);
        RaiseChanged();

        if (!keep)
        {
            // Without a kept error state the button falls back to the real state on its own.
            _ = Task.Delay(BriefFailure).ContinueWith(_ =>
            {
                if (_failures.TryGetValue(profileId, out Failure failure) && failure.IsExpired)
                {
                    _failures.TryRemove(profileId, out Failure _);
                    RaiseChanged();
                }
            }, TaskScheduler.Default);
        }
    }

    private void ClearFailure(string profileId)
    {
        if (_failures.TryRemove(profileId, out _))
        {
            RaiseChanged();
        }
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.Warn($"Network: a state handler failed: {ex.Message}");
        }
    }

    private readonly record struct Failure(string Reason, DateTimeOffset? Expires)
    {
        public bool IsExpired => Expires is { } expires && DateTimeOffset.UtcNow >= expires;
    }
}
