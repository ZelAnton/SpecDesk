using SpecDesk.Ai;
using SpecDesk.Contracts;

namespace SpecDesk.Host.Tests;

/// <summary>
/// A fake <see cref="ITemplateLibrary"/> returning a preset personal + remote set, so the controller's
/// <c>templates.request</c> → <c>templates</c> reply can be exercised without a file or the network.
/// </summary>
internal sealed class FakeTemplateLibrary : ITemplateLibrary
{
	public TemplatesPayload Result { get; init; } = new(
		[new PromptTemplate("p1", "Personal one", "Personal prompt body")],
		[new PromptTemplate("r1", "Remote one", "Remote prompt body")]);

	public int Calls { get; private set; }

	public Task<TemplatesPayload> GetTemplatesAsync(CancellationToken cancellationToken = default)
	{
		Calls++;
		return Task.FromResult(Result);
	}
}
