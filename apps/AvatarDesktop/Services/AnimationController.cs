using AvatarDesktop.Models;
using AvatarDesktop.Rendering;

namespace AvatarDesktop.Services;

public sealed class AnimationController
{
    private readonly IAvatarRenderer _renderer;
    private readonly Action<string> _log;

    public AvatarState State { get; private set; } = AvatarState.Idle;

    public event Action<AvatarState, AvatarState>? StateChanged;

    public AnimationController(IAvatarRenderer renderer, Action<string> log)
    {
        _renderer = renderer;
        _log = log;
    }

    public void SetState(AvatarState nextState)
    {
        if (State == nextState)
        {
            return;
        }

        var previous = State;
        State = nextState;
        _log($"[Animation] {previous} -> {nextState}");
        StateChanged?.Invoke(previous, nextState);
        ApplyRendererState(nextState);
    }

    public async Task ApplyCommandAsync(AvatarCommand command, CancellationToken cancellationToken = default)
    {
        command ??= new AvatarCommand();
        _log($"[Animation] Command action={command.Action}, mood={command.Mood}, duration={command.DurationMs}ms");

        ApplyMood(command.Mood);
        SetState(AvatarState.Speaking);
        _renderer.SetAnimation("speaking");

        if (!string.Equals(command.Action, "idle", StringComparison.OrdinalIgnoreCase))
        {
            SetState(AvatarState.Acting);
            _renderer.SetAnimation(command.Action);
            await Task.Delay(command.DurationMs, cancellationToken);
        }
        else
        {
            await Task.Delay(Math.Min(command.DurationMs, 400), cancellationToken);
        }

        SetState(AvatarState.Idle);
    }

    public void ResetToIdle()
    {
        SetState(AvatarState.Idle);
    }

    private void ApplyMood(string mood)
    {
        _renderer.SetBlendshape("smile", 0);
        _renderer.SetBlendshape("frown", 0);
        _renderer.SetBlendshape("brow_raise", 0);
        _renderer.SetBlendshape("anger", 0);

        switch ((mood ?? "neutral").ToLowerInvariant())
        {
            case "happy":
                _renderer.SetBlendshape("smile", 1);
                break;
            case "sad":
                _renderer.SetBlendshape("frown", 1);
                break;
            case "angry":
                _renderer.SetBlendshape("anger", 1);
                break;
            case "curious":
                _renderer.SetBlendshape("brow_raise", 1);
                break;
            default:
                break;
        }
    }

    private void ApplyRendererState(AvatarState state)
    {
        switch (state)
        {
            case AvatarState.Idle:
                _renderer.SetAnimation("idle");
                break;
            case AvatarState.Listening:
                _renderer.SetAnimation("listening");
                break;
            case AvatarState.Thinking:
                _renderer.SetAnimation("think");
                break;
            case AvatarState.Speaking:
                _renderer.SetAnimation("speaking");
                break;
            case AvatarState.Acting:
                // Action animation is set by ApplyCommandAsync.
                break;
        }
    }
}
