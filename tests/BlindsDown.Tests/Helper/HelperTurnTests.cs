using System.Diagnostics;
using System.Security.Principal;
using BlindsDown.Core.Helper;
using BlindsDown.Helper;
using BlindsDown.Infrastructure;

namespace BlindsDown.Tests.Helper;

/// <summary>
/// Who goes next: a queued pipe request either runs or is dropped (never both, and never after its client gave up),
/// and only one setup run at a time. The engine thread here is a plain test thread that touches no device. The setup
/// tests use a per-test session-local name, never the real setup lock.
/// </summary>
public sealed class HelperTurnTests : IDisposable
{
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CheckEvery = TimeSpan.FromMilliseconds(20);

    private readonly string _name = @"Local\BlindsDownTests-" + Guid.NewGuid().ToString("N");
    private readonly EngineThread _engine = new("BlindsDown test engine", _ => { });
    private readonly SetupLockSecurity _testSecurity;
    private int _ran;

    public HelperTurnTests()
    {
        using WindowsIdentity me = WindowsIdentity.GetCurrent();
        _testSecurity = SetupLockSecurity.ForTests(me.User!);
    }

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public void Dispose() => _engine.Dispose();

    [Fact]
    public void RequestThatStartedCanNoLongerBeDropped()
    {
        var turn = new PipeRequestTurn();

        Assert.True(turn.TryStart());
        Assert.False(turn.TryDrop());
        Assert.False(turn.TryStart());
    }

    [Fact]
    public void DroppedRequestNeverRuns()
    {
        var turn = new PipeRequestTurn();

        Assert.True(turn.TryDrop());
        Assert.False(turn.TryStart());
        Assert.False(turn.TryDrop());
    }

    [Fact]
    public async Task ExactlyOneSideWinsARace()
    {
        CancellationToken cancel = TestContext.Current.CancellationToken;
        for (int i = 0; i < 200; i++)
        {
            var turn = new PipeRequestTurn();
            using var go = new ManualResetEventSlim();
            Task<bool> start = Task.Run(
                () =>
                {
                    go.Wait(cancel);
                    return turn.TryStart();
                },
                cancel);
            Task<bool> drop = Task.Run(
                () =>
                {
                    go.Wait(cancel);
                    return turn.TryDrop();
                },
                cancel);
            go.Set();

            bool[] won = await Task.WhenAll(start, drop);
            Assert.True(won[0] ^ won[1]);
        }
    }

    [Fact]
    public void RequestWhoseTurnComesRunsAndAnswers()
    {
        TurnResult<int> result = PipeRequestTurn.Run(_engine, Work, () => true, Long, CheckEvery);

        Assert.Equal(TurnOutcome.Done, result.Outcome);
        Assert.Equal(42, result.Value);
        Assert.Equal(1, Volatile.Read(ref _ran));
    }

    [Fact]
    public async Task RequestWhoseClientLeftWhileItWaitedNeverRuns()
    {
        // A lock or unlock queued behind a slow one; the user's app stops waiting for it. It must not happen later.
        using var busy = new ManualResetEventSlim();
        _engine.Post(() => busy.Wait(Long));
        bool connected = true;

        Task<TurnResult<int>> waiting = Task.Run(() => PipeRequestTurn.Run(_engine, Work, () => Volatile.Read(ref connected), Long, CheckEvery), Cancel);
        await Task.Delay(100, Cancel);
        var left = Stopwatch.StartNew();
        Volatile.Write(ref connected, false);
        TurnResult<int> result = await waiting.WaitAsync(Long, Cancel);
        left.Stop();
        busy.Set();
        await DrainAsync();

        Assert.Equal(TurnOutcome.Dropped, result.Outcome);
        Assert.True(result.ClientLeft);
        Assert.True(left.Elapsed < TimeSpan.FromSeconds(2), $"Noticing the client had gone took {left.Elapsed}.");
        Assert.Equal(0, Volatile.Read(ref _ran));
    }

    [Fact]
    public async Task RequestWhoseClientLeftIsNotRunWhenItsTurnComes()
    {
        // The waiting side looks only now and then; the engine thread looks again right before the request would run.
        using var busy = new ManualResetEventSlim();
        _engine.Post(() => busy.Wait(Long));
        bool connected = true;

        Task<TurnResult<int>> waiting = Task.Run(() => PipeRequestTurn.Run(_engine, Work, () => Volatile.Read(ref connected), Long, TimeSpan.FromHours(1)), Cancel);
        await Task.Delay(100, Cancel);
        Volatile.Write(ref connected, false);
        busy.Set();
        TurnResult<int> result = await waiting.WaitAsync(Long, Cancel);

        Assert.Equal(TurnOutcome.Dropped, result.Outcome);
        Assert.True(result.ClientLeft);
        Assert.Equal(0, Volatile.Read(ref _ran));
    }

    [Fact]
    public async Task RequestThatWaitedTooLongIsDroppedAndNeverRunsLater()
    {
        using var busy = new ManualResetEventSlim();
        _engine.Post(() => busy.Wait(Long));

        TurnResult<int> result = PipeRequestTurn.Run(_engine, Work, () => true, TimeSpan.FromMilliseconds(200), CheckEvery);
        busy.Set();
        await DrainAsync();

        Assert.Equal(TurnOutcome.Dropped, result.Outcome);
        Assert.False(result.ClientLeft);
        Assert.Equal(0, Volatile.Read(ref _ran));
    }

    [Fact]
    public async Task RequestThatStartedRunsToTheEndAfterTheWait()
    {
        using var release = new ManualResetEventSlim();
        int Slow()
        {
            release.Wait(Long);
            return Work();
        }

        TurnResult<int> result = PipeRequestTurn.Run(_engine, Slow, () => true, TimeSpan.FromMilliseconds(200), CheckEvery);
        release.Set();
        await DrainAsync();

        Assert.Equal(TurnOutcome.StillRunning, result.Outcome);
        Assert.Equal(1, Volatile.Read(ref _ran));
    }

    [Fact]
    public async Task ClientThatLeavesWhileItsRequestRunsFreesTheConnectionAtOnce()
    {
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int Slow()
        {
            started.Set();
            release.Wait(Long);
            return Work();
        }

        bool connected = true;
        Task<TurnResult<int>> waiting = Task.Run(() => PipeRequestTurn.Run(_engine, Slow, () => Volatile.Read(ref connected), Long, CheckEvery), Cancel);
        Assert.True(started.Wait(Long, Cancel));
        Volatile.Write(ref connected, false);
        TurnResult<int> result = await waiting.WaitAsync(TimeSpan.FromSeconds(2), Cancel);
        release.Set();
        await DrainAsync();

        Assert.Equal(TurnOutcome.StillRunning, result.Outcome);
        Assert.True(result.ClientLeft);
        Assert.Equal(1, Volatile.Read(ref _ran));
    }

    [Fact]
    public async Task RequestAnsweredStillWorkingAlwaysRuns()
    {
        // The engine thread's last look at the client and the end of the wait can meet. The client is told "still
        // working" (it will finish) or "busy" (it never runs), and that must come true: here the engine thread looks
        // just as the wait ends, and by the time its look returns, the client has read the answer and hung up.
        using var looking = new ManualResetEventSlim();
        using var answered = new ManualResetEventSlim();
        bool Connected()
        {
            if (!_engine.IsCurrentThread)
            {
                return true;
            }

            looking.Set();
            answered.Wait(Long);
            return false;
        }

        Task<TurnResult<int>> waiting = Task.Run(() => PipeRequestTurn.Run(_engine, Work, Connected, TimeSpan.FromMilliseconds(200), CheckEvery), Cancel);
        Assert.True(looking.Wait(Long, Cancel));
        TurnResult<int> result = await waiting.WaitAsync(Long, Cancel);
        answered.Set();
        await DrainAsync();

        Assert.Equal(result.Outcome == TurnOutcome.StillRunning ? 1 : 0, Volatile.Read(ref _ran));
        Assert.Equal(TurnOutcome.Dropped, result.Outcome);
    }

    [Fact]
    public void ExceptionFromTheRequestReachesTheCaller() =>
        Assert.Throws<InvalidOperationException>(() =>
            PipeRequestTurn.Run<int>(_engine, () => throw new InvalidOperationException("test"), () => true, Long, CheckEvery));

    [Fact]
    public void ChangeClientsWaitLongerThanTheHelperTakesToAnswer()
    {
        // Otherwise a Lock or Unlock the app reported as failed could still be carried out after it gave up, and the
        // helper's "busy" and "still working" answers would never reach anyone.
        Assert.True(HelperTimeouts.ChangeTimeout >= HelperTimeouts.AnswerWithin + TimeSpan.FromSeconds(20));
        Assert.DoesNotContain("version", HelperTimeouts.BusyAnswer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("version", HelperTimeouts.StillWorkingAnswer, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(HelperTimeouts.BusyAnswer, HelperTimeouts.StillWorkingAnswer);
    }

    [Theory]
    [InlineData(1_760_000_000_123L, 5L, 1_760_000_000_123L)] // the client's own place in the alert list
    [InlineData(0L, 1_760_000_000_456L, 1_760_000_000_456L)] // a client that has none yet: the newest the helper sent
    public void HelpersOwnFailureAnswersKeepTheClientsPlaceInTheAlerts(long sinceEvent, long newestSent, long expected)
    {
        // The tray app takes its place from every answer; a 0 here would replay every alert the helper still keeps.
        var request = new HelperRequest { Command = HelperCommand.Unlock, SinceEvent = sinceEvent };

        HelperResponse answer = HelperServiceHost.Failure(HelperTimeouts.BusyAnswer, request, newestSent);

        Assert.False(answer.Ok);
        Assert.Equal(expected, answer.LastEventId);
        Assert.Empty(answer.Events);
        Assert.Null(answer.Status);
    }

    [Fact]
    public void SecondSetupRunWaitsForTheFirst()
    {
        using HelperSetupTurn? first = HelperSetupTurn.TryTake(_name, TimeSpan.FromSeconds(5), _testSecurity);
        Assert.NotNull(first);

        // The same name from another thread (in real use: another process) gets no turn while the first one runs.
        HelperSetupTurn? second = OnOtherThread(() => HelperSetupTurn.TryTake(_name, TimeSpan.FromMilliseconds(100), _testSecurity));
        Assert.Null(second);
    }

    [Fact]
    public void NextSetupRunGoesOnceTheFirstHasEnded()
    {
        HelperSetupTurn? first = HelperSetupTurn.TryTake(_name, TimeSpan.FromSeconds(5), _testSecurity);
        Assert.NotNull(first);
        first.Dispose();
        first.Dispose();

        Assert.True(OnOtherThread(() =>
        {
            using HelperSetupTurn? next = HelperSetupTurn.TryTake(_name, TimeSpan.FromSeconds(5), _testSecurity);
            return next is not null;
        }));
    }

    [Fact]
    public void SetupRunThatCrashedDoesNotBlockTheNextOne()
    {
        // A thread that ends while holding the mutex abandons it, like a setup process that is killed.
        Assert.True(OnOtherThread(() => HelperSetupTurn.TryTake(_name, TimeSpan.FromSeconds(5), _testSecurity) is not null));

        Assert.True(OnOtherThread(() =>
        {
            using HelperSetupTurn? next = HelperSetupTurn.TryTake(_name, TimeSpan.FromSeconds(5), _testSecurity);
            return next is not null;
        }));
    }

    [Fact]
    public void ALockMadeByANormalProgramDoesNotBlockSetup()
    {
        // A program in a standard account makes the mutex first (owned by its own account, open to everyone) and
        // holds it forever. Setup must not wait for it: only a mutex owned by SYSTEM or Administrators is a setup run's.
        using WindowsIdentity me = WindowsIdentity.GetCurrent();
        string user = me.User!.Value;
        using var held = new ManualResetEventSlim();
        using var letGo = new ManualResetEventSlim();
        var squatter = new Thread(() =>
        {
            using Microsoft.Win32.SafeHandles.SafeWaitHandle handle = HelperSetupTurn.CreateOrOpen(_name, $"O:{user}D:(A;;0x1f0001;;;WD)", out _);
            using var mutex = Mutex.OpenExisting(_name);
            mutex.WaitOne();
            held.Set();
            letGo.Wait(Long);
            mutex.ReleaseMutex();
        });
        squatter.Start();
        try
        {
            Assert.True(held.Wait(Long, Cancel));

            // The test's own security, so this account may open it; but only SYSTEM and Administrators are trusted.
            var adminsOnly = _testSecurity with { IsTrustedOwner = SetupLockSecurity.AdministratorsOnly.IsTrustedOwner };
            var waited = Stopwatch.StartNew();
            using HelperSetupTurn? turn = HelperSetupTurn.TryTake(_name, Long, adminsOnly);

            Assert.NotNull(turn);
            Assert.True(waited.Elapsed < TimeSpan.FromSeconds(5), $"Setup waited {waited.Elapsed} for another program's lock.");
        }
        finally
        {
            letGo.Set();
            squatter.Join();
        }
    }

    [Fact]
    public void TheSetupLocksOwnSecurityIsAdministratorsOnly()
    {
        var descriptor = new System.Security.AccessControl.RawSecurityDescriptor(SetupLockSecurity.AdministratorsOnly.Sddl);

        Assert.True(descriptor.Owner!.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid));
        Assert.All(
            descriptor.DiscretionaryAcl!.Cast<System.Security.AccessControl.CommonAce>(),
            ace => Assert.True(
                ace.SecurityIdentifier.IsWellKnown(WellKnownSidType.LocalSystemSid) || ace.SecurityIdentifier.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid),
                $"{ace.SecurityIdentifier} may use the setup lock."));
        using WindowsIdentity me = WindowsIdentity.GetCurrent();
        Assert.False(SetupLockSecurity.AdministratorsOnly.IsTrustedOwner(me.User!));
        Assert.True(SetupLockSecurity.AdministratorsOnly.IsTrustedOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)));
        Assert.True(SetupLockSecurity.AdministratorsOnly.IsTrustedOwner(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)));
    }

    /// <summary>A mutex belongs to the thread that took it, so other runs are simulated on their own threads.</summary>
    private static T OnOtherThread<T>(Func<T> work)
    {
        T result = default!;
        var thread = new Thread(() => result = work());
        thread.Start();
        thread.Join();
        return result;
    }

    private int Work()
    {
        Interlocked.Increment(ref _ran);
        return 42;
    }

    /// <summary>Waits until everything queued on the test engine thread so far has run.</summary>
    private Task DrainAsync() => _engine.InvokeAsync(() => true).WaitAsync(Long, Cancel);
}
