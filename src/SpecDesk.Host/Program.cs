using Photino.NET;
using SpecDesk.Contracts;

namespace SpecDesk.Host;

internal static class Program
{
	[STAThread]
	private static void Main()
	{
		// PoC-0 routes a single kind: an "echo" request is answered with an "echo.reply"
		// carrying the same id and payload back. Later PoCs register real handlers here.
		IpcRouter router = new IpcRouter()
			.Register("echo", static request =>
				new IpcMessage("echo.reply", Id: request.Id, Payload: request.Payload));

		PhotinoWindow window = new PhotinoWindow()
			.SetTitle("SpecDesk")
			.SetUseOsDefaultSize(false)
			.SetSize(1024, 768)
			.Center()
			.RegisterWebMessageReceivedHandler((sender, message) =>
			{
				string? reply = router.Handle(message);
				if (reply is not null && sender is PhotinoWindow source)
				{
					source.SendWebMessage(reply);
				}
			})
			.Load("wwwroot/index.html");

		window.WaitForClose();
	}
}
