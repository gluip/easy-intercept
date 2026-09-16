using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using EasyIntercept.Certificates;

namespace EasyIntercept.Hosting;

/// <summary>Implements <c>EasyIntercept --install-ca</c>: generate the root CA if needed and trust it.</summary>
public static class CaInstaller
{
    public static int Run(AppPaths paths)
    {
        var certs = new CertificateService(paths);
        var crtPath = certs.CaCertPath;
        Console.WriteLine($"Root CA: {crtPath}");

        if (OperatingSystem.IsWindows()) return RunWindows(crtPath);
        if (OperatingSystem.IsMacOS()) return RunMac(crtPath);

        Console.Error.WriteLine("Automatic trust-store import is only implemented on Windows and macOS.");
        Console.Error.WriteLine("Import the .crt above into your system's trust store manually.");
        return 1;
    }

    private static int RunWindows(string crtPath)
    {
        var cert = X509CertificateLoader.LoadCertificateFromFile(crtPath);

        // Elevated (installer) → machine-wide; otherwise fall back to the current user's store
        // (Windows shows a one-time confirmation dialog for that).
        foreach (var location in new[] { StoreLocation.LocalMachine, StoreLocation.CurrentUser })
        {
            try
            {
                using var store = new X509Store(StoreName.Root, location);
                store.Open(OpenFlags.ReadWrite);
                store.Add(cert);
                Console.WriteLine($"Installed '{cert.Subject}' into {location}\\Root.");
                return 0;
            }
            catch (CryptographicException ex)
            {
                Console.Error.WriteLine($"Could not add to {location}\\Root: {ex.Message}");
            }
        }

        return 1;
    }

    // macOS: per-user trust settings, so no sudo. The `security` tool makes macOS show its own password
    // dialog ("security wants to make changes to your Certificate Trust Settings"), also when started
    // from the menu bar app. System-wide trust stays available through install-ca.sh.
    private static int RunMac(string crtPath)
    {
        var psi = MacTrustCommand(crtPath, DefaultMacKeychain());
        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                Console.Error.WriteLine("Could not start the `security` tool.");
                return 1;
            }
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                Console.Error.WriteLine($"`security add-trusted-cert` exited with code {process.ExitCode}; the CA was not trusted.");
                return process.ExitCode;
            }

            Console.WriteLine("Trusted the EasyIntercept root CA for the current user (login keychain).");
            Console.WriteLine($"To undo later: security remove-trusted-cert \"{crtPath}\"");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not run the `security` tool: {ex.Message}");
            return 1;
        }
    }

    /// <summary>The user's login keychain, or null when it isn't where macOS normally keeps it.</summary>
    public static string? DefaultMacKeychain()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var path = Path.Combine(home, "Library", "Keychains", "login.keychain-db");
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// <c>security add-trusted-cert -r trustRoot [-k keychain] cert</c>: without <c>-d</c> this edits the
    /// per-user trust settings, which needs the user's password but not admin rights.
    /// </summary>
    public static ProcessStartInfo MacTrustCommand(string crtPath, string? keychainPath)
    {
        var psi = new ProcessStartInfo("security") { ArgumentList = { "add-trusted-cert", "-r", "trustRoot" } };
        if (keychainPath is not null)
        {
            psi.ArgumentList.Add("-k");
            psi.ArgumentList.Add(keychainPath);
        }
        psi.ArgumentList.Add(crtPath);
        return psi;
    }
}
