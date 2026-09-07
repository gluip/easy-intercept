using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using EasyIntercept.Certificates;

namespace EasyIntercept.Hosting;

/// <summary>Implements <c>EasyIntercept.exe --install-ca</c>: generate the root CA if needed and trust it.</summary>
public static class CaInstaller
{
    public static int Run(AppPaths paths)
    {
        var certs = new CertificateService(paths);
        var crtPath = certs.CaCertPath;
        Console.WriteLine($"Root CA: {crtPath}");

        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Automatic trust-store import is only implemented on Windows.");
            Console.Error.WriteLine("On macOS run install-ca.sh, or import the .crt above manually.");
            return 1;
        }

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
}
