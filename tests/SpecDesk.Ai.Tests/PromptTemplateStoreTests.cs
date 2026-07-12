using SpecDesk.Ai;
using SpecDesk.Contracts;

namespace SpecDesk.Ai.Tests;

[TestFixture]
public sealed class PromptTemplateStoreTests
{
	private string _dir = string.Empty;
	private string _path = string.Empty;

	[SetUp]
	public void SetUp()
	{
		_dir = Path.Combine(Path.GetTempPath(), "specdesk-tmpl-" + Guid.NewGuid().ToString("N"));
		_path = Path.Combine(_dir, "prompt-templates.json");
	}

	[TearDown]
	public void TearDown()
	{
		if (Directory.Exists(_dir))
		{
			Directory.Delete(_dir, recursive: true);
		}
	}

	[Test]
	public void Save_ThenLoad_RoundTripsTheTemplates()
	{
		PromptTemplateStore store = new(_path);
		IReadOnlyList<PromptTemplate> templates =
		[
			new("t1", "First", "First body"),
			new("t2", "Second", "Second body"),
		];

		store.Save(templates);
		IReadOnlyList<PromptTemplate> loaded = store.Load();

		Assert.Multiple(() =>
		{
			Assert.That(File.Exists(_path), Is.True, "Save must create the file (and its directory)");
			Assert.That(loaded, Has.Count.EqualTo(2));
			Assert.That(loaded[1].Title, Is.EqualTo("Second"));
			Assert.That(loaded[0].Body, Is.EqualTo("First body"));
		});
	}

	[Test]
	public void Load_WhenTheFileIsMissing_ReturnsEmpty()
	{
		Assert.That(new PromptTemplateStore(_path).Load(), Is.Empty);
	}

	[Test]
	public void Load_WhenTheFileIsCorrupt_ReturnsEmptyRatherThanThrowing()
	{
		Directory.CreateDirectory(_dir);
		File.WriteAllText(_path, "{ this is not valid json ]");

		Assert.That(new PromptTemplateStore(_path).Load(), Is.Empty);
	}

	[Test]
	public void Load_DropsEntriesMissingAnIdOrTitle()
	{
		Directory.CreateDirectory(_dir);
		// A valid entry, one missing its title, and one missing its id — only the first survives.
		File.WriteAllText(
			_path,
			"""[{"id":"ok","title":"Keep me","body":"b"},{"id":"x","title":"","body":"b"},{"id":"","title":"y","body":"b"}]""");

		IReadOnlyList<PromptTemplate> loaded = new PromptTemplateStore(_path).Load();
		Assert.Multiple(() =>
		{
			Assert.That(loaded, Has.Count.EqualTo(1));
			Assert.That(loaded[0].Id, Is.EqualTo("ok"));
		});
	}
}
