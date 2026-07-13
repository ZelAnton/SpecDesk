using SpecDesk.GitHub;

namespace SpecDesk.GitHub.Tests;

[TestFixture]
public sealed class GitHubAuthOptionsTests
{
	[Test]
	public void Default_scopes_include_organization_memberships()
	{
		Assert.That(GitHubAuthOptions.DefaultScopes, Does.Contain("read:org"));
	}
}
