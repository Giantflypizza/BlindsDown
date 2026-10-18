using BlindsDown.Infrastructure;

namespace BlindsDown.Helper;

/// <summary>What became of a request handed to the engine thread by <see cref="PipeRequestTurn.Run"/>.</summary>
internal enum TurnOutcome
{
    /// <summary>It ran and finished; its result is there.</summary>
    Done,

    /// <summary>It never ran and never will: the wait ended before its turn came, or its client hung up first.</summary>
    Dropped,

    /// <summary>It started and isn't finished (the wait ended, or its client hung up): it runs to the end.</summary>
    StillRunning,
}

/// <summary>The outcome, the result when it is <see cref="TurnOutcome.Done"/>, and whether the client had hung up.</summary>
internal readonly record struct TurnResult<T>(TurnOutcome Outcome, T? Value, bool ClientLeft);

/// <summary>
/// Decides, once, whether a queued pipe request runs or is dropped: the engine thread calls <see cref="TryStart"/>
/// when the request's turn comes, the waiting pipe connection calls <see cref="TryDrop"/> when it gives up or its
/// client hangs up. Whichever comes first wins, so a request is never both answered "busy" and carried out later.
/// </summary>
internal sealed class PipeRequestTurn
{
    private const int Waiting = 0;
    private const int Started = 1;
    private const int Dropped = 2;

    private int _state;

    /// <summary>
    /// Hands <paramref name="run"/> to <paramref name="engineThread"/> and waits for it, at most <paramref name="limit"/>,
    /// checking every <paramref name="checkEvery"/> whether the client is still waiting for the answer.
    ///  * Its turn comes in time and the client is still there: it runs, and the result is returned.
    ///  * The client hung up before its turn (seen while waiting, or when its turn comes): it is dropped and never runs.
    ///  * <paramref name="limit"/> passes before its turn: it is dropped and never runs.
    ///  * It started but isn't finished when the limit passes or the client hangs up: it runs to the end on its own;
    ///    this returns without waiting for it, so the pipe instance is free for the next client.
    /// Whatever it returns comes true: a request answered <see cref="TurnOutcome.StillRunning"/> runs, one answered
    /// <see cref="TurnOutcome.Dropped"/> never does.
    /// <paramref name="clientConnected"/> must not throw, and may still be called (on the engine thread) a moment
    /// after this returns. An exception from <paramref name="run"/> is rethrown.
    /// </summary>
    public static TurnResult<T> Run<T>(EngineThread engineThread, Func<T> run, Func<bool> clientConnected, TimeSpan limit, TimeSpan checkEvery)
    {
        var turn = new PipeRequestTurn();

        // Checked again on the engine thread itself: a client that left while the request waited in the queue may not
        // have been noticed yet by the loop below. The client is looked at BEFORE the turn is taken: once TryStart
        // wins, the loop below may already have answered "still working", so nothing may stop the request after it.
        Task<(bool Ran, T? Value)> work = engineThread.InvokeAsync<(bool, T?)>(
            () => clientConnected() && turn.TryStart() ? (true, run()) : (false, default));

        long deadline = Environment.TickCount64 + (long)limit.TotalMilliseconds;
        long slice = Math.Max(1, (long)checkEvery.TotalMilliseconds);
        while (true)
        {
            int wait = (int)Math.Clamp(deadline - Environment.TickCount64, 0, slice);
            if (Task.WaitAny([work], wait) >= 0)
            {
                // GetResult rethrows run's own exception (not an AggregateException). Nothing but the engine's own
                // check drops a request while this still waits, so "didn't run" means its client had left.
                (bool ran, T? value) = work.GetAwaiter().GetResult();
                return ran ? new(TurnOutcome.Done, value, ClientLeft: false) : new(TurnOutcome.Dropped, default, ClientLeft: true);
            }

            bool clientLeft = !clientConnected();
            if (clientLeft || Environment.TickCount64 >= deadline)
            {
                return new(turn.TryDrop() ? TurnOutcome.Dropped : TurnOutcome.StillRunning, default, clientLeft);
            }
        }
    }

    /// <summary>True when the request may run now; false when its client already gave up on it.</summary>
    public bool TryStart() => Interlocked.CompareExchange(ref _state, Started, Waiting) == Waiting;

    /// <summary>True when the request will never run; false when it has already started.</summary>
    public bool TryDrop() => Interlocked.CompareExchange(ref _state, Dropped, Waiting) == Waiting;
}
