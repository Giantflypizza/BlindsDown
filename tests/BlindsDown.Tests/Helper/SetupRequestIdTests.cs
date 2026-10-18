using BlindsDown.Core.Helper;
using BlindsDown.Helper;
using BlindsDown.Tests.Core.Pnp;

namespace BlindsDown.Tests.Helper;

/// <summary>
/// The request ID the tray app hands an elevated setup run, so the unlock of its own install or removal isn't
/// announced as another program's: reading it from the command line, the IDs derived from it, and the unlock requests
/// that carry them. Nothing here starts a setup run or talks to the real helper.
/// </summary>
public sealed class SetupRequestIdTests
{
    /// <summary>A request ID of the form the tray app hands a setup run (Guid.ToString("N")).</summary>
    private const string Id = "0123456789abcdef0123456789abcdef";

    public static TheoryData<string[]> ArgumentsWithoutAnId => new()
    {
        new[] { "--install-helper" },
        new[] { "--install-helper", "--request-id" },
        new[] { "--install-helper", "--request-id", string.Empty },
        new[] { "--install-helper", "--request-id", Id.ToUpperInvariant() },
        new[] { "--install-helper", "--request-id", Id[..31] },
        new[] { "--install-helper", "--request-id", Id + "0" },
        new[] { "--install-helper", "--request-id", Id + "-2" },
        new[] { "--install-helper", "--request-id", " " + Id },
        new[] { "--install-helper", "--request-id", "01234567-89ab-cdef-0123-456789abcdef" },
        new[] { "--install-helper", "--REQUEST-ID", Id },
        new[] { "--install-helper", "--request-id=" + Id },
        new[] { "--install-helper", Id },
        new[] { "--install-helper", "--request-id", Id, "--service" },
        new[] { "--install-helper", "--request-id", Id, "--request-id", Id },
        new[] { "--install-helper", "--other", "--request-id", Id },
    };

    [Theory]
    [InlineData("--install-helper")]
    [InlineData("--update-helper")]
    [InlineData("--uninstall-helper")]
    public void TheIdFollowsTheOption(string option) =>
        Assert.Equal(Id, HelperSetup.RequestIdFromArguments([option, "--request-id", Id]));

    [Fact]
    public void WhatTheLauncherPassesIsWhatTheSetupRunReads()
    {
        // Windows splits the parameters at the single spaces between them (no quotes or escapes are involved).
        Assert.All(Enum.GetValues<ElevatedTask>(), task =>
            Assert.Equal(Id, HelperSetup.RequestIdFromArguments(ElevatedLauncher.Arguments(task, Id).Split(' '))));
    }

    [Theory]
    [MemberData(nameof(ArgumentsWithoutAnId))]
    public void AMissingOrMalformedIdIsNoId(string[] args) => Assert.Null(HelperSetup.RequestIdFromArguments(args));

    [Fact]
    public void ASetupRunHasThreeDistinctIdsForItsUnlocks()
    {
        IReadOnlyList<string> ids = HelperSetup.SetupRequestIds(Id);

        Assert.Equal([Id, Id + "-2", Id + "-3"], ids);
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(ids.Count, ids.Select(HelperProtocol.DigestOf).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void EveryDerivedIdIsOneTheHelperAccepts()
    {
        for (int i = 0; i < 20; i++)
        {
            foreach (string id in HelperSetup.SetupRequestIds(Guid.NewGuid().ToString("N")))
            {
                Assert.InRange(id.Length, 1, HelperProtocol.MaxRequestIdLength);
                var request = new HelperRequest { Command = HelperCommand.Unlock, Source = "setup", RequestId = id };

                HelperRequest? parsed = HelperMessages.ParseRequest(HelperMessages.SerializeRequest(request), out string? error);

                Assert.True(parsed is not null, error);
                Assert.Equal(id, parsed.RequestId);
            }
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("0123456789abcdef0123456789abcdef-2")]
    [InlineData("app-1")]
    public void OnlyAnIdOfTheTrayAppsFormHasDerivedIds(string id) =>
        Assert.Throws<ArgumentException>(() => HelperSetup.SetupRequestIds(id));

    [Fact]
    public void EachUnlockOfASetupRunTakesTheNextId()
    {
        var system = new WindowsHelperSystem(Id);

        HelperRequest[] requests = [.. Enumerable.Range(0, 5).Select(_ => system.NextUnlockRequest())];

        Assert.All(requests, r => Assert.Equal((HelperCommand.Unlock, "setup"), (r.Command, r.Source)));
        Assert.Equal([Id, Id + "-2", Id + "-3", null, null], requests.Select(r => r.RequestId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("0123456789abcdef0123456789abcdef-2")]
    public void WithoutAnIdTheUnlocksCarryNone(string? requestId)
    {
        // As before IDs existed (a removal from "Installed apps", or a run started by hand).
        var system = new WindowsHelperSystem(requestId);

        Assert.All(Enumerable.Range(0, 4), _ => Assert.Null(system.NextUnlockRequest().RequestId));
    }

    [Fact]
    public void ARemovalThatAsksTwiceIsNotRefusedAndBothAreTheTrayAppsOwn()
    {
        // A removal asks the helper to unlock, and asks again after starting a helper that didn't answer. The first
        // request may have reached the helper anyway (its answer timed out), and the helper refuses an ID it has seen.
        var world = new PnpWorld();
        world.Devices.Add("cam1");
        world.Engine.Initialize();
        var handler = new HelperRequestHandler(world.Engine, AppVersion.Text);
        handler.Handle(new HelperRequest { Command = HelperCommand.Lock, Lock = PnpWorld.CamerasOnly, RequestId = "app-1" });
        var system = new WindowsHelperSystem(Id);

        HelperResponse first = handler.Handle(system.NextUnlockRequest());
        HelperResponse second = handler.Handle(system.NextUnlockRequest());
        HelperResponse replayed = handler.Handle(new HelperRequest { Command = HelperCommand.Unlock, Source = "setup", RequestId = Id });

        Assert.True(first.Ok, first.Error);
        Assert.True(second.Ok, second.Error);
        Assert.False(replayed.Ok); // One ID for every attempt would have been refused like this.
        Assert.False(world.Devices["cam1"].Disabled);

        // The tray app registered every ID of the run as its own, so the unlock is recognised.
        HelperChange unlocked = Assert.Single(first.Changes, c => c.Kind == HelperChangeKind.Unlocked);
        Assert.Contains(unlocked.RequestDigest, HelperSetup.SetupRequestIds(Id).Select(HelperProtocol.DigestOf));
    }
}
