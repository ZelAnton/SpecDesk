namespace SpecDesk.Contracts.Tests;

[TestFixture]
public sealed class IpcRouterTests
{
	private static IpcRouter EchoRouter() =>
		new IpcRouter().Register("echo", static request =>
			new IpcMessage("echo.reply", Id: request.Id, Payload: request.Payload));

	[Test]
	public void Handle_Echo_ReturnsReplyWithSameIdAndPayload()
	{
		string? reply = EchoRouter()
			.Handle("{\"kind\":\"echo\",\"id\":\"r-1\",\"payload\":{\"text\":\"hi\"}}");

		Assert.That(reply, Is.Not.Null);
		IpcMessage? message = IpcSerializer.TryDeserialize(reply!);
		Assert.Multiple(() =>
		{
			Assert.That(message!.Kind, Is.EqualTo("echo.reply"));
			Assert.That(message.Id, Is.EqualTo("r-1"));
			Assert.That(message.Payload!.Value.GetProperty("text").GetString(), Is.EqualTo("hi"));
		});
	}

	[Test]
	public void Handle_UnknownKind_ReturnsNull()
	{
		Assert.That(EchoRouter().Handle("{\"kind\":\"nope\"}"), Is.Null);
	}

	[Test]
	public void Handle_MalformedJson_ReturnsNull()
	{
		Assert.That(EchoRouter().Handle("{bad"), Is.Null);
	}

	[Test]
	public void Handle_HandlerReturningNull_ReturnsNull()
	{
		IpcRouter router = new IpcRouter().Register("ready", static _ => null);
		Assert.That(router.Handle("{\"kind\":\"ready\"}"), Is.Null);
	}
}
