namespace SpecDesk.Host;

/// <summary>
/// The version/staleness guard for the live preview — the discipline that keeps a fast typist's
/// preview correct (docs/design/09-ipc-protocol.md, "Ordering & correctness"). Pure and
/// thread-safe: <see cref="ShouldRender"/> is called on the message thread when an edit arrives;
/// <see cref="ShouldEmit"/> on the background thread when a render completes. No Photino, no I/O,
/// so the core risk is unit-testable in isolation.
/// </summary>
public sealed class PreviewCoordinator
{
	private readonly object _gate = new();
	private long _latest = -1;

	/// <summary>The newest editor version observed so far (-1 before any edit).</summary>
	public long Latest
	{
		get
		{
			lock (_gate)
			{
				return _latest;
			}
		}
	}

	/// <summary>
	/// Record an incoming edit version and report whether it is the newest seen (so a render is
	/// worth starting). An older or duplicate version — an out-of-order frame — is rejected.
	/// </summary>
	public bool ShouldRender(long version)
	{
		lock (_gate)
		{
			if (version <= _latest)
			{
				return false;
			}

			_latest = version;
			return true;
		}
	}

	/// <summary>
	/// Report whether a just-finished render for <paramref name="version"/> is still the newest
	/// and should be sent. A render superseded by a newer edit is dropped.
	/// </summary>
	public bool ShouldEmit(long version)
	{
		lock (_gate)
		{
			return version >= _latest;
		}
	}
}
