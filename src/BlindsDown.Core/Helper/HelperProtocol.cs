using System.Security.Cryptography;
using System.Text;
using BlindsDown.Core.Locking;
using BlindsDown.Core.Pnp;

namespace BlindsDown.Core.Helper;

/// <summary>
/// The tray app talks to the optional helper service over a local named pipe with this tiny protocol:
/// one JSON request, one JSON response, per connection or per message (message mode).
/// The helper never takes device IDs to act on: it only accepts "lock with these options" and "unlock",
/// and chooses the devices itself, so it can't be abused to disable anything but cameras and
/// microphone-only hardware.
/// </summary>
public static class HelperProtocol
{
    public const int Version = 1;

    /// <summary>\\.\pipe\BlindsDown.Helper</summary>
    public const string PipeName = "BlindsDown.Helper";

    public const string ServiceName = "BlindsDownHelper";

    public const string ServiceDisplayName = "BlindsDown camera helper";

    /// <summary>Requests or responses larger than this are rejected.</summary>
    public const int MaxMessageBytes = 64 * 1024;

    public const int MaxExclusions = 256;

    public const int MaxStringLength = 512;

    /// <summary>The longest <see cref="HelperRequest.RequestId"/>.</summary>
    public const int MaxRequestIdLength = 64;

    /// <summary>
    /// What the helper publishes instead of a request's ID: a one-way fingerprint (SHA-256, hex). Every pipe client can
    /// read the change log, so publishing the ID itself would let another program reuse it and pass its own unlock off
    /// as this app's; the fingerprint only lets the client that knows the ID recognise its own change.
    /// </summary>
    public static string DigestOf(string requestId) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(requestId)));
}

public enum HelperCommand
{
    /// <summary>Version handshake and current status.</summary>
    Hello,

    /// <summary>Current status plus alerts since <see cref="HelperRequest.SinceEvent"/>.</summary>
    Status,

    Lock,

    Unlock,
}

public sealed record HelperRequest
{
    public int V { get; init; } = HelperProtocol.Version;

    public HelperCommand Command { get; init; }

    /// <summary>Required for <see cref="HelperCommand.Lock"/>.</summary>
    public DeviceLockRequest? Lock { get; init; }

    /// <summary>Return alerts with an ID greater than this.</summary>
    public long SinceEvent { get; init; }

    /// <summary>
    /// The <see cref="HelperResponse.RunId"/> of the answer <see cref="SinceEvent"/> was taken from (null, 0 or less: not
    /// known). A position from another run of the helper is not one in this run's sequence: alerts are not kept across a
    /// restart, so after the clock went back a little this run may already have handed out IDs past it, and comparing
    /// would skip this run's first changes. The helper then answers with everything from this run
    /// (<see cref="HelperResponse.SinceReset"/>).
    /// </summary>
    public long? SinceRun { get; init; }

    public string? ClientVersion { get; init; }

    /// <summary>Who asked, for the helper's log only ("hotkey", "tray", "command line", ...).</summary>
    public string? Source { get; init; }

    /// <summary>
    /// The client's own ID for this Lock or Unlock (letters, digits and '-', at most
    /// <see cref="HelperProtocol.MaxRequestIdLength"/> characters). It comes back in the <see cref="HelperChange"/> the
    /// request causes, so the tray app can tell its own changes from another program's.
    /// </summary>
    public string? RequestId { get; init; }
}

/// <summary>An alert raised by the helper (e.g. "A new camera was connected while locked; it is disabled").</summary>
public sealed record HelperEvent(long Id, EngineAlertKind Kind, string DeviceName, string Message, bool Urgent);

public enum HelperChangeKind
{
    /// <summary>A lock was started (there was none before).</summary>
    Locked,

    /// <summary>A lock request made the lock cover less: it turned devices back on that the lock had turned off, or
    /// stopped turning off devices it was still trying to.</summary>
    Narrowed,

    /// <summary>The lock was ended.</summary>
    Unlocked,
}

/// <summary>
/// A change to the helper's lock and the request that made it. Any program running as a signed-in user can ask the
/// helper to unlock, so the tray app checks every change against the <see cref="HelperRequest.RequestId"/>s it sent
/// itself: anything else is another program's, and is never quiet. Shares the ID sequence of <see cref="HelperEvent"/>.
/// The helper keeps the log on disk, so it survives a restart of the helper.
/// </summary>
/// <param name="Id">Increasing, like <see cref="HelperEvent.Id"/>.</param>
/// <param name="Kind">What changed.</param>
/// <param name="RequestDigest"><see cref="HelperProtocol.DigestOf"/> the request's ID (null when the client sent none).</param>
/// <param name="Source">Who the request said it was from (the client's claim, for the log and the message).</param>
/// <param name="TurnedOn">The devices it turned back on (names).</param>
/// <param name="Released">Devices the lock no longer turns off that were not off yet (in use, unplugged).</param>
/// <param name="At">When the helper made the change.</param>
public sealed record HelperChange(
    long Id,
    HelperChangeKind Kind,
    string? RequestDigest,
    string? Source,
    IReadOnlyList<string> TurnedOn,
    IReadOnlyList<string> Released,
    DateTimeOffset At);

public sealed record HelperResponse
{
    public int V { get; init; } = HelperProtocol.Version;

    public bool Ok { get; init; }

    public string? Error { get; init; }

    public string HelperVersion { get; init; } = string.Empty;

    /// <summary>The lock state with every device the helper can see (also when unlocked, for Settings).</summary>
    public DeviceLockStatus? Status { get; init; }

    public IReadOnlyList<HelperEvent> Events { get; init; } = [];

    /// <summary>Changes to the lock after <see cref="HelperRequest.SinceEvent"/> (same ID sequence as the events).</summary>
    public IReadOnlyList<HelperChange> Changes { get; init; } = [];

    /// <summary>
    /// The request's <see cref="HelperRequest.SinceEvent"/> was not a position in this run of the helper: newer than
    /// anything it has (an earlier run's clock was ahead), or from another run (<see cref="HelperRequest.SinceRun"/>) and
    /// past where this run started. The answer then holds everything from the start of this run (<see cref="RunId"/>),
    /// all of it new to the client, and the client's position starts over at <see cref="LastEventId"/>.
    /// </summary>
    public bool SinceReset { get; init; }

    public long LastEventId { get; init; }

    /// <summary>
    /// Which run of the helper answered: the ID this run's alerts and changes come after (it changes whenever the helper
    /// starts again). The client sends it back as <see cref="HelperRequest.SinceRun"/> with the position it took from
    /// this answer, so the helper knows exactly whether that position is from this run. 0: not known (an answer the
    /// helper's request handler didn't make, or an older helper).
    /// </summary>
    public long RunId { get; init; }

    /// <summary>For <see cref="HelperCommand.Unlock"/>.</summary>
    public RestoreReport? Report { get; init; }
}
