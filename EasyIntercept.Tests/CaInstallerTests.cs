using EasyIntercept.Hosting;
using Xunit;

namespace EasyIntercept.Tests;

public class CaInstallerTests
{
    private const string Crt = "/Users/me/Library/Application Support/EasyIntercept/certs/easyntercept-ca.crt";
    private const string Keychain = "/Users/me/Library/Keychains/login.keychain-db";

    [Fact]
    public void Mac_trust_command_adds_the_cert_to_the_login_keychain_as_a_trusted_root()
    {
        var psi = CaInstaller.MacTrustCommand(Crt, Keychain);

        Assert.Equal("security", psi.FileName);
        Assert.Equal(["add-trusted-cert", "-r", "trustRoot", "-k", Keychain, Crt], psi.ArgumentList);
    }

    [Fact]
    public void Mac_trust_command_without_a_keychain_uses_the_default_one()
    {
        var psi = CaInstaller.MacTrustCommand(Crt, null);

        Assert.Equal(["add-trusted-cert", "-r", "trustRoot", Crt], psi.ArgumentList);
    }

    [Fact]
    public void Mac_trust_command_stays_in_the_user_domain()
    {
        // -d would target the admin trust settings, which needs root; the app must never ask for that
        var psi = CaInstaller.MacTrustCommand(Crt, Keychain);

        Assert.DoesNotContain("-d", psi.ArgumentList);
    }
}
