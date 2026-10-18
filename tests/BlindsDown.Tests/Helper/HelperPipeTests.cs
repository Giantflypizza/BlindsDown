using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using BlindsDown.Core.Helper;
using BlindsDown.Helper;

namespace BlindsDown.Tests.Helper;

/// <summary>
/// The helper's pipe, end to end, unelevated: a test server (current user's ACL, still local-only) under a unique
/// name per test, and the real client code. The real helper's pipe is never touched.
/// </summary>
public sealed class HelperPipeTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly string _name = "BlindsDownTests-" + Guid.NewGuid().ToString("N");
    private readonly List<HelperPipeServer> _servers = [];
    private int _handled;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (HelperPipeServer server in _servers)
        {
            server.Dispose();
        }
    }

    [Fact]
    public async Task RequestAndAnswerRoundTrip()
    {
        StartServer(Echo);
        var client = new HelperClient(_name, requireSystemOwner: false);

        byte[] answer = await client.SendRawAsync("hello"u8.ToArray(), Timeout, Cancel);

        Assert.Equal("echo:hello", Encoding.UTF8.GetString(answer));
        Assert.Equal(1, Volatile.Read(ref _handled));
    }

    [Fact]
    public async Task OneClientCanSendManyRequestsOneAfterAnother()
    {
        StartServer(Echo);
        var client = new HelperClient(_name, requireSystemOwner: false);

        for (int i = 0; i < 20; i++)
        {
            byte[] answer = await client.SendRawAsync(Encoding.UTF8.GetBytes($"n{i}"), Timeout, Cancel);
            Assert.Equal($"echo:n{i}", Encoding.UTF8.GetString(answer));
        }
    }

    [Fact]
    public async Task ManyClientsAtOnceAreAllAnswered()
    {
        // More clients than pipe instances, each handled slowly: the rest wait for a free instance.
        StartServer(request =>
        {
            Thread.Sleep(50);
            return Echo(request);
        });

        Task<byte[]>[] calls =
        [
            .. Enumerable.Range(0, HelperPipeServer.MaxInstances * 3).Select(i =>
                new HelperClient(_name, requireSystemOwner: false).SendRawAsync(Encoding.UTF8.GetBytes($"c{i}"), Timeout, Cancel)),
        ];
        byte[][] answers = await Task.WhenAll(calls);

        for (int i = 0; i < answers.Length; i++)
        {
            Assert.Equal($"echo:c{i}", Encoding.UTF8.GetString(answers[i]));
        }
    }

    [Fact]
    public async Task LargestAllowedMessagesPassBothWays()
    {
        StartServer(request => request);
        var client = new HelperClient(_name, requireSystemOwner: false);
        byte[] request = new byte[HelperProtocol.MaxMessageBytes];
        Random.Shared.NextBytes(request);

        byte[] answer = await client.SendRawAsync(request, Timeout, Cancel);

        Assert.Equal(request, answer);
    }

    [Fact]
    public async Task OversizedRequestIsRejectedWithoutReachingTheHandler()
    {
        StartServer(Echo);
        var client = new HelperClient(_name, requireSystemOwner: false);

        await Assert.ThrowsAsync<HelperUnavailableException>(
            () => client.SendRawAsync(new byte[HelperProtocol.MaxMessageBytes + 1], Timeout, Cancel));

        Assert.Equal(0, Volatile.Read(ref _handled));
        await AssertStillAnswersAsync();
    }

    [Fact]
    public async Task OversizedAnswerIsNotSent()
    {
        StartServer(_ => new byte[HelperProtocol.MaxMessageBytes + 1]);
        var client = new HelperClient(_name, requireSystemOwner: false);

        await Assert.ThrowsAsync<HelperUnavailableException>(() => client.SendRawAsync("x"u8.ToArray(), Timeout, Cancel));
    }

    [Fact]
    public async Task EmptyAnswerClosesTheConnection()
    {
        StartServer(_ => []);
        var client = new HelperClient(_name, requireSystemOwner: false);

        await Assert.ThrowsAsync<HelperUnavailableException>(() => client.SendRawAsync("x"u8.ToArray(), Timeout, Cancel));
    }

    [Fact]
    public async Task ClientGivesUpAfterItsTimeoutAndTheServerCarriesOn()
    {
        using var release = new ManualResetEventSlim();
        StartServer(request =>
        {
            if (request.AsSpan().SequenceEqual("slow"u8))
            {
                release.Wait(Timeout);
            }

            return Echo(request);
        });
        var client = new HelperClient(_name, requireSystemOwner: false);

        var stopwatch = Stopwatch.StartNew();
        HelperUnavailableException error = await Assert.ThrowsAsync<HelperUnavailableException>(
            () => client.SendRawAsync("slow"u8.ToArray(), TimeSpan.FromMilliseconds(300), Cancel));
        stopwatch.Stop();
        release.Set();

        Assert.Contains("in time", error.Message, StringComparison.Ordinal);
        Assert.Equal(HelperUnavailableKind.TimedOut, error.Kind);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"The client took {stopwatch.Elapsed} to give up.");
        await AssertStillAnswersAsync();
    }

    [Fact]
    public async Task ClientThatNeverGotInSaysNothingWasSent()
    {
        // Every instance stays taken until the client gives up. Its request never reached the helper, so it can't be
        // carried out later, and the tray app must not treat it as a lock or unlock that may still happen (that would
        // keep it from reporting an unlock by another program in the meantime).
        StartServer(Echo, TimeSpan.FromSeconds(10));
        string path = HelperPaths.LocalPipePath(_name);
        var silent = new List<FileStream>();
        try
        {
            for (int i = 0; i < HelperPipeServer.MaxInstances; i++)
            {
                silent.Add(OpenRaw(path));
            }

            HelperUnavailableException error = await Assert.ThrowsAsync<HelperUnavailableException>(
                () => new HelperClient(_name, requireSystemOwner: false).SendRawAsync("x"u8.ToArray(), TimeSpan.FromMilliseconds(500), Cancel));

            Assert.Equal(HelperUnavailableKind.Busy, error.Kind);
            Assert.Equal(0, Volatile.Read(ref _handled));
        }
        finally
        {
            foreach (FileStream stream in silent)
            {
                stream.Dispose();
            }
        }
    }

    [Fact]
    public async Task RequestStuckBehindTheSameClientsPreviousOneSaysNothingWasSent()
    {
        // The tray app sends one request at a time; a lock or unlock that can't get past a slow one was never sent.
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        StartServer(request =>
        {
            if (request.AsSpan().SequenceEqual("slow"u8))
            {
                entered.Set();
                release.Wait(Timeout);
            }

            return Echo(request);
        });
        var client = new HelperClient(_name, requireSystemOwner: false);
        Task<byte[]> slow = client.SendRawAsync("slow"u8.ToArray(), Timeout, Cancel);
        Assert.True(entered.Wait(Timeout, Cancel));

        HelperUnavailableException error = await Assert.ThrowsAsync<HelperUnavailableException>(
            () => client.SendRawAsync("x"u8.ToArray(), TimeSpan.FromMilliseconds(300), Cancel));
        release.Set();

        Assert.Equal(HelperUnavailableKind.Busy, error.Kind);
        Assert.Equal("echo:slow", Encoding.UTF8.GetString(await slow.WaitAsync(Timeout, Cancel)));
        Assert.Equal(1, Volatile.Read(ref _handled));
    }

    [Fact]
    public async Task HandlerSeesWhetherTheClientStillWaits()
    {
        // The helper drops a request whose client gave up before its turn came (a lock or unlock the user was told
        // didn't happen must not happen later), and that rests on this check.
        var whileWaiting = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var afterLeaving = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        StartServer((request, clientConnected) =>
        {
            if (!request.AsSpan().SequenceEqual("lock"u8))
            {
                return Echo(request);
            }

            whileWaiting.TrySetResult(clientConnected());
            var waited = Stopwatch.StartNew();
            while (clientConnected() && waited.Elapsed < Timeout)
            {
                Thread.Sleep(20);
            }

            afterLeaving.TrySetResult(!clientConnected());
            return Echo(request);
        });
        var client = new HelperClient(_name, requireSystemOwner: false);

        await Assert.ThrowsAsync<HelperUnavailableException>(
            () => client.SendRawAsync("lock"u8.ToArray(), TimeSpan.FromMilliseconds(500), Cancel));

        Assert.True(await whileWaiting.Task.WaitAsync(Timeout, Cancel), "The client was waiting, but the check said it had gone.");
        Assert.True(await afterLeaving.Task.WaitAsync(Timeout, Cancel), "The client had gone, but the check never said so.");
        await AssertStillAnswersAsync();
    }

    [Fact]
    public async Task ClientsThatConnectAndSayNothingDoNotBlockOthersForLong()
    {
        // Every instance taken by a client that never sends anything (with the server's own short default for the
        // request): an honest client still gets in well within the tray app's few seconds.
        var server = new HelperPipeServer(_name, Echo, forTests: true);
        _servers.Add(server);
        server.Start();
        string path = HelperPaths.LocalPipePath(_name);
        var silent = new List<FileStream>();
        try
        {
            for (int i = 0; i < HelperPipeServer.MaxInstances; i++)
            {
                silent.Add(OpenRaw(path));
            }

            var waited = Stopwatch.StartNew();
            byte[] answer = await new HelperClient(_name, requireSystemOwner: false).SendRawAsync("hi"u8.ToArray(), TimeSpan.FromSeconds(6), Cancel);

            Assert.Equal("echo:hi", Encoding.UTF8.GetString(answer));
            Assert.True(server.RequestTimeout <= TimeSpan.FromSeconds(2), $"A silent client may hold an instance for {server.RequestTimeout}.");
            Assert.True(waited.Elapsed < TimeSpan.FromSeconds(5), $"The honest client waited {waited.Elapsed}.");
        }
        finally
        {
            foreach (FileStream stream in silent)
            {
                stream.Dispose();
            }
        }
    }

    [Fact]
    public void WhoHoldsThePipeNameIsReported()
    {
        // The helper logs this when it can't create its pipe: a program that took the name while the helper was
        // stopped (here: this test, as a normal program in the user's session).
        StartServer(Echo);

        string? holder = HelperPipeServer.DescribeNameHolder(_name, Timeout);

        using Process self = Process.GetCurrentProcess();
        Assert.NotNull(holder);
        Assert.Contains($"process {Environment.ProcessId} in session {self.SessionId}", holder, StringComparison.Ordinal);
        if (!ProcessElevation.IsElevated())
        {
            // Elevated, the test's pipe is owned by Administrators.
            Assert.Contains("isn't SYSTEM", holder, StringComparison.Ordinal);
        }

        Assert.Equal(0, Volatile.Read(ref _handled));
        Assert.Null(HelperPipeServer.DescribeNameHolder("BlindsDownTests-" + Guid.NewGuid().ToString("N"), Timeout));
    }

    [Fact]
    public async Task CancellationStopsTheClient()
    {
        using var release = new ManualResetEventSlim();
        StartServer(request =>
        {
            release.Wait(Timeout);
            return Echo(request);
        });
        var client = new HelperClient(_name, requireSystemOwner: false);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        cancel.CancelAfter(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SendRawAsync("x"u8.ToArray(), Timeout, cancel.Token));
        release.Set();
    }

    [Fact]
    public async Task ServerSurvivesClientsThatMisbehave()
    {
        StartServer(Echo, ioTimeout: TimeSpan.FromMilliseconds(500));
        string path = HelperPaths.LocalPipePath(_name);

        // Connects and hangs up at once.
        using (OpenRaw(path))
        {
        }

        // Sends garbage and hangs up without reading the answer.
        using (FileStream garbage = OpenRaw(path))
        {
            byte[] bytes = new byte[1000];
            Random.Shared.NextBytes(bytes);
            garbage.Write(bytes);
        }

        // Connects and never says anything (the server's read timeout ends it).
        using FileStream silent = OpenRaw(path);

        await AssertStillAnswersAsync();
    }

    [Fact]
    public async Task HandlerThatThrowsLosesOnlyThatRequest()
    {
        StartServer(request => request.AsSpan().SequenceEqual("boom"u8) ? throw new InvalidOperationException("test") : Echo(request));
        var client = new HelperClient(_name, requireSystemOwner: false);

        await Assert.ThrowsAsync<HelperUnavailableException>(() => client.SendRawAsync("boom"u8.ToArray(), Timeout, Cancel));
        await AssertStillAnswersAsync();
    }

    [Fact]
    public async Task NoServerMeansNotRunning()
    {
        var client = new HelperClient(_name, requireSystemOwner: false);

        HelperUnavailableException error = await Assert.ThrowsAsync<HelperUnavailableException>(
            () => client.SendRawAsync("x"u8.ToArray(), Timeout, Cancel));

        Assert.Contains("isn't running", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClientRefusesAPipeThatIsNotOwnedBySystem()
    {
        // The squatting defence: a pipe created by a normal program (here: this test, as the current user, in
        // the user's session) is refused before anything is sent.
        StartServer(Echo);
        var client = new HelperClient(_name, requireSystemOwner: true);

        HelperUnavailableException error = await Assert.ThrowsAsync<HelperUnavailableException>(
            () => client.SendRawAsync("secret"u8.ToArray(), Timeout, Cancel));

        Assert.Equal(0, Volatile.Read(ref _handled));

        // The tray app can say what happened and what helps: not "repair the helper" (a new helper couldn't get its
        // name either), but a restart.
        Assert.Equal(HelperUnavailableKind.Impostor, error.Kind);
        Assert.Contains($"process {Environment.ProcessId}", error.Message, StringComparison.Ordinal);
        Assert.Contains("Restart the PC", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClientSeesWhoRunsThePipe()
    {
        StartServer(Echo);
        var client = new HelperClient(_name, requireSystemOwner: false);
        using WindowsIdentity me = WindowsIdentity.GetCurrent();
        using Process self = Process.GetCurrentProcess();

        HelperPipeServerInfo server = await client.InspectServerAsync(Timeout);

        // Windows makes new objects owned by the token's default owner: the user, or Administrators when elevated (as on
        // the CI machines).
        Assert.Equal(me.Owner!.Value, server.OwnerSid);
        Assert.Equal((uint)self.SessionId, server.SessionId);
        Assert.Equal((uint)Environment.ProcessId, server.ProcessId);
        Assert.NotNull(HelperPipeAcl.ServerProblem(server.OwnerSid, server.SessionId!.Value));
    }

    [Fact]
    public void SecondServerCannotJoinAnExistingPipeName()
    {
        StartServer(Echo);
        using var second = new HelperPipeServer(_name, Echo, forTests: true);

        Assert.Throws<IOException>(second.Start);
    }

    [Fact]
    public async Task DisposeStopsTheServerAndFreesTheName()
    {
        HelperPipeServer server = StartServer(Echo);
        await AssertStillAnswersAsync();

        server.Dispose();
        var client = new HelperClient(_name, requireSystemOwner: false);
        await Assert.ThrowsAsync<HelperUnavailableException>(() => client.SendRawAsync("x"u8.ToArray(), Timeout, Cancel));

        // The name is free again.
        StartServer(Echo);
        await AssertStillAnswersAsync();
    }

    [Fact]
    public void StartTwiceOrAfterDisposeIsRefused()
    {
        HelperPipeServer server = StartServer(Echo);
        Assert.Throws<InvalidOperationException>(server.Start);

        server.Dispose();
        server.Dispose();
        Assert.Throws<ObjectDisposedException>(server.Start);
    }

    [Fact]
    public async Task NormalProgramCannotCreateAPipeOwnedBySystem()
    {
        // The other half of the squatting defence: the client only talks to a pipe owned by SYSTEM (or
        // Administrators), and Windows doesn't let a normal program create one. Elevated, it could.
        Assert.SkipWhen(ProcessElevation.IsElevated(), "Running elevated: Administrators may set any owner.");
        using var server = new HelperPipeServer(_name, Echo);

        IOException error = Assert.Throws<IOException>(server.Start);

        Assert.Contains("owner", error.Message, StringComparison.Ordinal);
        var client = new HelperClient(_name, requireSystemOwner: false);
        await Assert.ThrowsAsync<HelperUnavailableException>(() => client.SendRawAsync([1], Timeout, Cancel));
    }

    [Fact]
    public async Task ServiceAccessListLetsTheInteractiveUserConnect()
    {
        // The production access list (owner: this user, as an unelevated test can't make SYSTEM the owner) with a
        // client that skips the owner check: proves that INTERACTIVE users can talk to the helper. Not true for
        // non-interactive sessions (CI services).
        using (WindowsIdentity me = WindowsIdentity.GetCurrent())
        {
            Assert.SkipUnless(me.Groups?.Any(g => g.Value == "S-1-5-4") == true, "Needs an interactive Windows session.");
        }

        var server = new HelperPipeServer(_name, Echo) { SddlForTests = HelperPipeAcl.ServiceDacl };
        _servers.Add(server);
        server.Start();
        var client = new HelperClient(_name, requireSystemOwner: false);

        byte[] answer = await client.SendRawAsync("hi"u8.ToArray(), Timeout, Cancel);

        Assert.Equal("echo:hi", Encoding.UTF8.GetString(answer));
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData(@"..\x")]
    [InlineData("a/b")]
    [InlineData(@"a\b")]
    [InlineData("has space")]
    [InlineData(@"\\server\pipe\x")]
    public void InvalidPipeNamesAreRejected(string name)
    {
        Assert.Throws<ArgumentException>(() => new HelperClient(name, requireSystemOwner: false));
        Assert.Throws<ArgumentException>(() => new HelperPipeServer(name, Echo, forTests: true));
    }

    [Fact]
    public void TimeoutMustBePositive()
    {
        var client = new HelperClient(_name, requireSystemOwner: false);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            _ = client.SendRawAsync([1], TimeSpan.Zero, Cancel);
        });
    }

    /// <summary>A bare client (no owner check, no protocol), waiting while every instance is busy.</summary>
    private static FileStream OpenRaw(string path)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (stopwatch.Elapsed < Timeout)
            {
                Thread.Sleep(20);
            }
        }
    }

    private byte[] Echo(byte[] request)
    {
        Interlocked.Increment(ref _handled);
        return [.. "echo:"u8, .. request];
    }

    private HelperPipeServer StartServer(Func<byte[], byte[]> handle, TimeSpan? ioTimeout = null) =>
        StartServer((request, _) => handle(request), ioTimeout);

    private HelperPipeServer StartServer(Func<byte[], Func<bool>, byte[]> handle, TimeSpan? ioTimeout = null)
    {
        var server = new HelperPipeServer(_name, handle, forTests: true)
        {
            RequestTimeout = ioTimeout ?? TimeSpan.FromSeconds(10),
            AnswerTimeout = ioTimeout ?? TimeSpan.FromSeconds(10),
        };
        _servers.Add(server);
        server.Start();
        return server;
    }

    private async Task AssertStillAnswersAsync()
    {
        var client = new HelperClient(_name, requireSystemOwner: false);
        byte[] answer = await client.SendRawAsync("still-there"u8.ToArray(), Timeout, Cancel);
        Assert.EndsWith("still-there", Encoding.UTF8.GetString(answer), StringComparison.Ordinal);
    }
}
