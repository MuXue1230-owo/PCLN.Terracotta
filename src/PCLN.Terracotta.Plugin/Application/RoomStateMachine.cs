using Cn.Pcln.Terracotta.Contracts;

namespace Cn.Pcln.Terracotta.Application;

public sealed class RoomStateMachine
{
    private static readonly Dictionary<TerracottaRoomState, HashSet<TerracottaRoomState>> AllowedTransitions =
        new Dictionary<TerracottaRoomState, HashSet<TerracottaRoomState>>
        {
            [TerracottaRoomState.Idle] =
            [
                TerracottaRoomState.WaitingForGame,
                TerracottaRoomState.WaitingForLan,
                TerracottaRoomState.Creating,
                TerracottaRoomState.Joining,
                TerracottaRoomState.Faulted
            ],
            [TerracottaRoomState.WaitingForGame] =
            [
                TerracottaRoomState.WaitingForLan,
                TerracottaRoomState.Creating,
                TerracottaRoomState.Joining,
                TerracottaRoomState.Idle,
                TerracottaRoomState.Faulted
            ],
            [TerracottaRoomState.WaitingForLan] =
            [
                TerracottaRoomState.Creating,
                TerracottaRoomState.Idle,
                TerracottaRoomState.Faulted
            ],
            [TerracottaRoomState.Creating] =
            [
                TerracottaRoomState.Connected,
                TerracottaRoomState.Leaving,
                TerracottaRoomState.Faulted
            ],
            [TerracottaRoomState.Joining] =
            [
                TerracottaRoomState.Connected,
                TerracottaRoomState.Leaving,
                TerracottaRoomState.Faulted
            ],
            [TerracottaRoomState.Connected] =
            [
                TerracottaRoomState.Reconnecting,
                TerracottaRoomState.Diagnosing,
                TerracottaRoomState.Leaving,
                TerracottaRoomState.Faulted
            ],
            [TerracottaRoomState.Reconnecting] =
            [
                TerracottaRoomState.Connected,
                TerracottaRoomState.Diagnosing,
                TerracottaRoomState.Leaving,
                TerracottaRoomState.Faulted
            ],
            [TerracottaRoomState.Leaving] =
            [
                TerracottaRoomState.Idle,
                TerracottaRoomState.Faulted
            ],
            [TerracottaRoomState.Faulted] =
            [
                TerracottaRoomState.Diagnosing,
                TerracottaRoomState.Idle
            ],
            [TerracottaRoomState.Diagnosing] =
            [
                TerracottaRoomState.Connected,
                TerracottaRoomState.Reconnecting,
                TerracottaRoomState.Faulted,
                TerracottaRoomState.Idle
            ]
        };

    private readonly Lock _gate = new();
    private TerracottaRoomState _state = TerracottaRoomState.Idle;

    public event EventHandler<TerracottaRoomState>? StateChanged;

    public TerracottaRoomState State
    {
        get
        {
            lock (_gate)
                return _state;
        }
    }

    public bool CanTransitionTo(TerracottaRoomState next)
    {
        lock (_gate)
            return next == _state || AllowedTransitions[_state].Contains(next);
    }

    public void TransitionTo(TerracottaRoomState next)
    {
        EventHandler<TerracottaRoomState>? changed;
        lock (_gate)
        {
            if (next == _state)
                return;
            if (!AllowedTransitions[_state].Contains(next))
                throw new InvalidOperationException($"Invalid Terracotta room transition: {_state} -> {next}.");
            _state = next;
            changed = StateChanged;
        }

        changed?.Invoke(this, next);
    }

    /// <summary>
    /// Best-effort path to Idle used when Helper reports the room is already gone
    /// (for example after a remote leave or process recycle).
    /// </summary>
    public void ResetToIdle()
    {
        EventHandler<TerracottaRoomState>? changed;
        lock (_gate)
        {
            if (_state == TerracottaRoomState.Idle)
                return;
            if (AllowedTransitions[_state].Contains(TerracottaRoomState.Leaving))
                _state = TerracottaRoomState.Leaving;
            if (_state != TerracottaRoomState.Idle && AllowedTransitions[_state].Contains(TerracottaRoomState.Idle))
                _state = TerracottaRoomState.Idle;
            else
                _state = TerracottaRoomState.Idle;
            changed = StateChanged;
        }

        changed?.Invoke(this, TerracottaRoomState.Idle);
    }
}
