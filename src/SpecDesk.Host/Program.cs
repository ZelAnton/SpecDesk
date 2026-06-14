using Photino.NET;

namespace SpecDesk.Host;

internal static class Program
{
	[STAThread]
	private static void Main()
	{
		// Reference one symbol from each library so the full project graph is
		// exercised at compile time while the app is still a scaffold.
		string modules = string.Join(
			", ",
			SpecDesk.Contracts.Placeholder.Module,
			SpecDesk.Core.Placeholder.moduleName,
			SpecDesk.Markdown.Placeholder.moduleName,
			SpecDesk.Diff.Placeholder.moduleName,
			SpecDesk.Git.Placeholder.Module,
			SpecDesk.GitHub.Placeholder.Module,
			SpecDesk.Ai.Placeholder.Module);

		string html =
			$"<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>SpecDesk</title></head>"
			+ $"<body><h1>SpecDesk</h1><p>Loaded modules: {modules}</p></body></html>";

		var window = new PhotinoWindow()
			.SetTitle("SpecDesk")
			.SetUseOsDefaultSize(false)
			.SetSize(1024, 768)
			.Center()
			.LoadRawString(html);

		window.WaitForClose();
	}
}
