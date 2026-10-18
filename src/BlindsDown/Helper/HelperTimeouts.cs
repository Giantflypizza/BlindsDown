namespace BlindsDown.Helper;

/// <summary>
/// How long the helper and its clients wait for each other, in one place so the two sides can't drift apart.
/// </summary>
/// <remarks>
/// The helper answers every request within <see cref="AnswerWithin"/> of reading it, with one of:
///  * the result;
///  * <see cref="BusyAnswer"/>: the request was still waiting for its turn (other requests ran first). It is dropped
///    and never carried out;
///  * <see cref="StillWorkingAnswer"/>: the request started and isn't finished. It runs to the end (stopping half
///    way would leave devices half changed), and the next status shows the result.
/// A request whose client hangs up before its turn is never carried out either: the helper drops it as soon as it
/// notices, and checks once more just before the request would start. So nothing a client gave up on happens later.
/// Clients of Lock and Unlock therefore wait <see cref="ChangeTimeout"/>, longer than <see cref="AnswerWithin"/>
/// plus time to connect, and always hear which of the three it was. Status and Hello clients wait only a few
/// seconds; when they give up, their request is simply dropped.
/// </remarks>
internal static class HelperTimeouts
{
    /// <summary>The helper's longest wait for a request to get its turn and finish (a lock or unlock of many devices,
    /// each of which Windows may take a moment to change).</summary>
    public static readonly TimeSpan AnswerWithin = TimeSpan.FromMinutes(2);

    /// <summary>What a client of Lock or Unlock waits: <see cref="AnswerWithin"/>, plus time to connect while the
    /// helper's pipe is busy with other clients.</summary>
    public static readonly TimeSpan ChangeTimeout = AnswerWithin + TimeSpan.FromSeconds(30);

    /// <summary>The answer to a request that was dropped before it started: nothing was done.</summary>
    public const string BusyAnswer = "The helper was busy with other requests and did not do this one; please try again.";

    /// <summary>The answer to a request that started and is still running: it finishes on its own.</summary>
    public const string StillWorkingAnswer = "The helper is still working on this request; its status shows the result when it is done.";
}
