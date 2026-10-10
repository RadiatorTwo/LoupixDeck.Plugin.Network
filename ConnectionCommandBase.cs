using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Network;

/// <summary>
/// Shared behaviour of the network commands: it resolves which profile is meant, runs the action
/// through the store and reports how it ended on the touch slot. It never throws — a failing
/// command must not take the host's command pipeline with it.
/// </summary>
internal abstract class ConnectionCommandBase(
    CommandDescriptor descriptor,
    NetworkStateStore store,
    NetworkSettings settings) : IPluginCommand
{
    /// <summary>How long a short confirmation stays on the touch slot.</summary>
    private static readonly TimeSpan OverlayDuration = TimeSpan.FromSeconds(2);

    public CommandDescriptor Descriptor { get; } = descriptor;

    public virtual ButtonTargets SupportedTargets => ButtonTargets.All;

    protected NetworkStateStore Store { get; } = store;

    protected NetworkSettings Settings { get; } = settings;

    public async Task Execute(CommandContext ctx)
    {
        try
        {
            await ExecuteCore(ctx).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ctx.Host.Logger.Warn($"{Descriptor.CommandName} failed: {ex.Message}");
        }
    }

    protected abstract Task ExecuteCore(CommandContext ctx);

    /// <summary>
    /// The profile a press acts on: the button's own parameter, or the first favorite when the
    /// parameter is empty, so a command dropped on a button without configuring it still works.
    /// </summary>
    protected string? ResolveProfileId(CommandContext ctx)
    {
        string? parameter = NetworkCommands.ReadParameter(ctx, 0) ?? Settings.FavoriteAt(0);

        // A name typed by hand is accepted too; a profile that is gone keeps its stored id, so the
        // press can say so instead of acting on something else.
        return NetworkSettings.Resolve(parameter, Store.Current)?.StableId ?? parameter;
    }

    /// <summary>Activates or deactivates a profile and reports a failure on the touch slot.</summary>
    protected async Task RunAsync(CommandContext ctx, string? profileId, bool activate)
    {
        if (!Store.Current.IsAvailable)
        {
            ShowOverlay(ctx, ctx.Host.Tr(NetworkErrors.ToEnglishText(NetworkCallOutcome.Unavailable)));
            return;
        }

        if (profileId is null || Store.Current.FindProfile(profileId) is null)
        {
            ShowOverlay(ctx, ctx.Host.Tr(NetworkErrors.ToEnglishText(NetworkCallOutcome.NotFound)));
            ctx.Host.Logger.Info($"{Descriptor.CommandName}: connection {profileId ?? "(none)"} not found.");
            return;
        }

        NetworkActionResult result = activate
            ? await Store.ActivateAsync(profileId).ConfigureAwait(false)
            : await Store.DeactivateAsync(profileId).ConfigureAwait(false);

        Report(ctx, result, profileId);
    }

    /// <summary>Shows how an action ended. Success shows itself on the button state.</summary>
    protected void Report(CommandContext ctx, NetworkActionResult result, string? subject)
    {
        if (result.IsSuccess)
        {
            return;
        }

        ShowOverlay(ctx, ctx.Host.Tr(result.Text));
        ctx.Host.Logger.Info($"{Descriptor.CommandName} on {subject ?? "(none)"}: {result.Text}.");
    }

    /// <summary>
    /// Shows a short message on the touch slot that was pressed. A rotary press has no slot of its
    /// own, so the host is asked for the slot next to it; a plain button has none at all.
    /// </summary>
    protected static void ShowOverlay(CommandContext ctx, string text)
    {
        if (ctx.SourceIndex is not int index || ctx.Target == ButtonTargets.SimpleButton)
        {
            return;
        }

        int slot = ctx.Target == ButtonTargets.RotaryEncoder
            ? ctx.Host.GetTouchSlotForRotary(index)
            : index;

        if (slot < 0)
        {
            return;
        }

        ctx.Host.OverlayTouchText(slot, text, OverlayDuration);
    }
}
