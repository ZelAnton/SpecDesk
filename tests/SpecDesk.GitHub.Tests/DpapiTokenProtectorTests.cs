using System.Text;

namespace SpecDesk.GitHub.Tests;

// The real DPAPI protector is Windows-only; this smoke-tests the actual crypto on the Windows CI leg.
[TestFixture]
[Platform("Win")]
public sealed class DpapiTokenProtectorTests
{
    [Test]
    public void Protect_then_Unprotect_round_trips_the_bytes()
    {
        DpapiTokenProtector protector = new();
        byte[] plaintext = Encoding.UTF8.GetBytes("gho_secret_token");

        byte[] cipher = protector.Protect(plaintext);

        Assert.Multiple(() =>
        {
            Assert.That(cipher, Is.Not.EqualTo(plaintext)); // actually encrypted, not stored in the clear
            Assert.That(protector.Unprotect(cipher), Is.EqualTo(plaintext));
        });
    }
}
