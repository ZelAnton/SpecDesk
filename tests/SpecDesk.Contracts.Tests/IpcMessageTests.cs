using System.Text.Json;

namespace SpecDesk.Contracts.Tests;

[TestFixture]
public sealed class IpcMessageTests
{
	[Test]
	public void Serialize_UsesCamelCase_AndOmitsNullIdAndVersion()
	{
		string json = IpcSerializer.Serialize(new IpcMessage("echo"));
		Assert.That(json, Is.EqualTo("{\"kind\":\"echo\"}"));
	}

	[Test]
	public void RoundTrip_PreservesKindIdVersionAndPayload()
	{
		using JsonDocument doc = JsonDocument.Parse("{\"text\":\"hi\"}");
		IpcMessage original = new("echo", Id: "r-1", Version: 7, Payload: doc.RootElement.Clone());

		IpcMessage? result = IpcSerializer.TryDeserialize(IpcSerializer.Serialize(original));

		Assert.That(result, Is.Not.Null);
		Assert.Multiple(() =>
		{
			Assert.That(result!.Kind, Is.EqualTo("echo"));
			Assert.That(result.Id, Is.EqualTo("r-1"));
			Assert.That(result.Version, Is.EqualTo(7));
			Assert.That(result.Payload!.Value.GetProperty("text").GetString(), Is.EqualTo("hi"));
		});
	}

	[Test]
	public void TryDeserialize_ReturnsNull_ForMalformedJson()
	{
		Assert.That(IpcSerializer.TryDeserialize("{not json"), Is.Null);
	}

	[Test]
	public void TryDeserialize_ReturnsNull_WhenKindMissing()
	{
		Assert.That(IpcSerializer.TryDeserialize("{\"id\":\"r-1\"}"), Is.Null);
	}
}
