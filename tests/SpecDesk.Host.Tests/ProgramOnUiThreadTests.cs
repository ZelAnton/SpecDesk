using Microsoft.Extensions.Logging.Abstractions;

namespace SpecDesk.Host.Tests;

// M-20: Photino's native Invoke() (Photino.Windows.cpp) blocks the calling thread on an untimed
// condition variable and never checks whether its PostMessage actually reached a still-alive
// window — if the window is destroyed first, the posted callback never runs and nothing ever
// wakes the waiter. PhotinoFileDialogs.InvokeOrAbandon moves that native call to a background
// task and returns when `closing` cancels; the seams below pin both the blocked-Invoke and
// missing-callback cases without a real native window.
[TestFixture]
public sealed class ProgramOnUiThreadTests
{
    [Test]
    public void WaitOrAbandon_CompletesBeforeClosing_ReturnsTheResult()
    {
        TaskCompletionSource<string?> completion = new();
        completion.SetResult("chosen.md");

        string? result = PhotinoFileDialogs.WaitOrAbandon(
            completion.Task, "ShowOpenFile", NullLogger.Instance, CancellationToken.None);

        Assert.That(result, Is.EqualTo("chosen.md"));
    }

    [Test]
    public void WaitOrAbandon_NullResult_IsPassedThroughUnchanged()
    {
        TaskCompletionSource<string?> completion = new();
        completion.SetResult(null);

        string? result = PhotinoFileDialogs.WaitOrAbandon(
            completion.Task, "ShowOpenFile", NullLogger.Instance, CancellationToken.None);

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task InvokeOrAbandon_BlockedInvoke_ReturnsOnceClosingCancels()
    {
        // This substitutes the actual native Invoke call, which has begun but cannot return until
        // after closing. It pins the primary race rather than only a missing callback.
        using CancellationTokenSource closing = new();
        using ManualResetEventSlim invokeEntered = new();
        using ManualResetEventSlim allowInvokeReturn = new();
        using ManualResetEventSlim invokeReturned = new();
        TaskCompletionSource<string?> completion = new();
        Task cancelWhenInvokeBlocks = Task.Run(() =>
        {
            Assert.That(invokeEntered.Wait(TimeSpan.FromSeconds(2)), Is.True, "Invoke did not begin.");
            closing.Cancel();
        });

        string? result = PhotinoFileDialogs.InvokeOrAbandon(
            () =>
            {
                invokeEntered.Set();
                allowInvokeReturn.Wait();
                invokeReturned.Set();
            },
            completion,
            "ShowOpenFile",
            NullLogger.Instance,
            closing.Token);

        await cancelWhenInvokeBlocks;
        Assert.That(result, Is.Null);
        allowInvokeReturn.Set();
        Assert.That(invokeReturned.Wait(TimeSpan.FromSeconds(2)), Is.True, "Blocked Invoke did not return.");
    }

    [Test]
    public void WaitOrAbandon_NeverCompletes_AbandonsOnceClosingCancels()
    {
        // Simulates the race the finding describes: the native Invoke() callback never runs (the
        // window was torn down first), so this TaskCompletionSource is never completed. Without a
        // bound this would hang the calling thread forever.
        TaskCompletionSource<string?> completion = new();
        using CancellationTokenSource closing = new();
        closing.Cancel();

        string? result = null;
        Assert.That(
            () => result = PhotinoFileDialogs.WaitOrAbandon(
                completion.Task, "ShowOpenFile", NullLogger.Instance, closing.Token),
            Throws.Nothing);
        Assert.That(result, Is.Null);
    }

    [Test]
    public void WaitOrAbandon_ClosingCancelsAfterCompletion_StillReturnsTheResult()
    {
        // The window can keep tearing down after a dialog already finished; a late cancellation
        // must not override a result that already arrived.
        TaskCompletionSource<string?> completion = new();
        completion.SetResult("already-done.md");
        using CancellationTokenSource closing = new();
        closing.Cancel();

        string? result = PhotinoFileDialogs.WaitOrAbandon(
            completion.Task, "ShowOpenFile", NullLogger.Instance, closing.Token);

        Assert.That(result, Is.EqualTo("already-done.md"));
    }
}
