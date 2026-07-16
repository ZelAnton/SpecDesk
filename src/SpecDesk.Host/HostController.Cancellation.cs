namespace SpecDesk.Host;

public sealed partial class HostController
{
	// Cancels a snapshot of a reusable CancellationTokenSource field that was read under _sync and is now
	// being cancelled OUTSIDE that lock (a new operation superseding the previous one, or teardown/sign-out
	// retiring in-flight work). Each such field's background owner nulls the field under _sync in its
	// terminal finally and then disposes the source outside the lock, so between this snapshot's read and
	// this Cancel() the owner can reach that finally and dispose it. A bare Cancel() would then throw
	// ObjectDisposedException on the message thread and abort the handler mid-way — e.g. skipping
	// _auth.SignOut() during sign-out, leaving the persisted GitHub token in place though the user asked to
	// disconnect. Routing every reusable-field snapshot cancel through the one guarded path CancelForDispose
	// already uses (which swallows exactly that ObjectDisposedException, because a source disposed by its
	// completed owner needs no cancellation) keeps that race from ever interrupting a handler. Null is a
	// no-op so callers can pass an optional field snapshot directly.
	private static void GuardedCancel(CancellationTokenSource? cancellation)
	{
		if (cancellation is null)
		{
			return;
		}
		CancelForDispose(cancellation);
	}
}
