using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;
using Org.BouncyCastle.OpenSsl;
using X509Certificate = Org.BouncyCastle.X509.X509Certificate;

namespace EasyIntercept.Certificates;

public class CertificateService
{
    private const string CaDir = "certs";
    private const string CaPfxFile = "easyntercept-ca.pfx";
    private const string CaCrtFile = "easyntercept-ca.crt";
    private const string CaPassword = "easyintercept";

    private readonly AsymmetricCipherKeyPair _caKeyPair;
    private readonly X509Certificate _caCert;
    private readonly X509Certificate2 _caX509;
    private readonly ConcurrentDictionary<string, X509Certificate2> _cache = new();

    public CertificateService()
    {
        Directory.CreateDirectory(CaDir);
        var pfxPath = Path.Combine(CaDir, CaPfxFile);

        if (File.Exists(pfxPath))
        {
            // Load existing CA
            _caX509 = new X509Certificate2(pfxPath, CaPassword, X509KeyStorageFlags.Exportable);
            var pkcs12Store = new Pkcs12StoreBuilder().Build();
            using var fs = File.OpenRead(pfxPath);
            pkcs12Store.Load(fs, CaPassword.ToCharArray());
            var alias = pkcs12Store.Aliases.First();
            _caKeyPair = new AsymmetricCipherKeyPair(
                pkcs12Store.GetCertificate(alias).Certificate.GetPublicKey(),
                pkcs12Store.GetKey(alias).Key);
            _caCert = pkcs12Store.GetCertificate(alias).Certificate;
        }
        else
        {
            // Generate new root CA
            (_caKeyPair, _caCert) = GenerateRootCa();
            SaveCa(pfxPath);
            _caX509 = new X509Certificate2(pfxPath, CaPassword, X509KeyStorageFlags.Exportable);
        }
    }

    public string CaCertPath => Path.Combine(CaDir, CaCrtFile);

    public X509Certificate2 GetCertificateForHost(string host)
    {
        return _cache.GetOrAdd(host, h => GenerateHostCert(h));
    }

    private (AsymmetricCipherKeyPair keyPair, X509Certificate cert) GenerateRootCa()
    {
        var keyGen = new RsaKeyPairGenerator();
        keyGen.Init(new KeyGenerationParameters(new SecureRandom(), 2048));
        var keyPair = keyGen.GenerateKeyPair();

        var certGen = new X509V3CertificateGenerator();
        var subject = new X509Name("CN=EasyIntercept Root CA, O=EasyIntercept");

        certGen.SetSerialNumber(BigInteger.ProbablePrime(120, new SecureRandom()));
        certGen.SetIssuerDN(subject);
        certGen.SetSubjectDN(subject);
        certGen.SetNotBefore(DateTime.UtcNow.AddDays(-1));
        certGen.SetNotAfter(DateTime.UtcNow.AddYears(10));
        certGen.SetPublicKey(keyPair.Public);

        certGen.AddExtension(X509Extensions.BasicConstraints, true,
            new BasicConstraints(true));
        certGen.AddExtension(X509Extensions.KeyUsage, true,
            new KeyUsage(KeyUsage.KeyCertSign | KeyUsage.CrlSign));

        var signer = new Asn1SignatureFactory("SHA256WithRSA", keyPair.Private);
        var cert = certGen.Generate(signer);

        return (keyPair, cert);
    }

    private X509Certificate2 GenerateHostCert(string host)
    {
        var keyGen = new RsaKeyPairGenerator();
        keyGen.Init(new KeyGenerationParameters(new SecureRandom(), 2048));
        var keyPair = keyGen.GenerateKeyPair();

        var certGen = new X509V3CertificateGenerator();
        var subject = new X509Name($"CN={host}");

        certGen.SetSerialNumber(BigInteger.ProbablePrime(120, new SecureRandom()));
        certGen.SetIssuerDN(_caCert.SubjectDN);
        certGen.SetSubjectDN(subject);
        certGen.SetNotBefore(DateTime.UtcNow.AddDays(-1));
        certGen.SetNotAfter(DateTime.UtcNow.AddYears(1));
        certGen.SetPublicKey(keyPair.Public);

        // SAN extension — required by modern browsers/curl
        var sanBuilder = new GeneralNames(new GeneralName(GeneralName.DnsName, host));
        certGen.AddExtension(X509Extensions.SubjectAlternativeName, false, sanBuilder);

        var signer = new Asn1SignatureFactory("SHA256WithRSA", _caKeyPair.Private);
        var cert = certGen.Generate(signer);

        // Convert to X509Certificate2 with private key via PFX roundtrip
        var store = new Pkcs12StoreBuilder().Build();
        var certEntry = new X509CertificateEntry(cert);
        var keyEntry = new AsymmetricKeyEntry(keyPair.Private);
        store.SetKeyEntry(host, keyEntry, new[] { certEntry });

        using var ms = new MemoryStream();
        store.Save(ms, CaPassword.ToCharArray(), new SecureRandom());
        return new X509Certificate2(ms.ToArray(), CaPassword, X509KeyStorageFlags.Exportable);
    }

    private void SaveCa(string pfxPath)
    {
        // Save PFX (private key + cert)
        var store = new Pkcs12StoreBuilder().Build();
        var certEntry = new X509CertificateEntry(_caCert);
        var keyEntry = new AsymmetricKeyEntry(_caKeyPair.Private);
        store.SetKeyEntry("ca", keyEntry, new[] { certEntry });

        using (var fs = File.Create(pfxPath))
            store.Save(fs, CaPassword.ToCharArray(), new SecureRandom());

        // Save CRT (PEM, public only — for user to install)
        var crtPath = Path.Combine(CaDir, CaCrtFile);
        using var writer = new StreamWriter(crtPath);
        var pemWriter = new PemWriter(writer);
        pemWriter.WriteObject(_caCert);
    }
}
