using SpecDesk.Contracts;

namespace SpecDesk.Host;

public sealed partial class HostController
{
	// T-077: read/write the persisted UI preferences (theme/wrap/view mode) — see PreferencesStore.
	private void RegisterPreferencesHandlers()
	{
		_messageHandlers.Register(MessageKinds.PreferencesRequest, OnPreferencesRequest);
		_messageHandlers.Register(MessageKinds.PreferencesUpdate, OnPreferencesUpdate);
	}

	// Returned when no PreferencesStore is wired (a test harness that doesn't pass one) or nothing has ever
	// been saved: Theme absent (the webview falls back to the OS colour scheme, exactly as before this store
	// existed), Wrap on, and Split — the same defaults the webview already assumed without this IPC.
	private static readonly PreferencesPayload DefaultPreferences = new(Theme: null, Wrap: true, ViewMode: "split");

	private void OnPreferencesRequest(IpcMessage message) => SendPreferencesState();

	private void SendPreferencesState() =>
		Emit(IpcSerializer.SerializeEvent(MessageKinds.PreferencesState, _preferences?.State() ?? DefaultPreferences));

	// Write-only: unlike workspace.state (one store fanned out to several panels), the webview already holds
	// the values it just changed, so there is no broadcast reply.
	private void OnPreferencesUpdate(IpcMessage message)
	{
		PreferencesPayload? payload = SafeGetPayload<PreferencesPayload>(message);
		if (payload is not null)
		{
			_preferences?.Update(payload.Theme, payload.Wrap, payload.ViewMode);
		}
	}
}
