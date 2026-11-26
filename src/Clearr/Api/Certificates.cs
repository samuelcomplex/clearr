using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Clearr.Core;
using Clearr.Storage;

namespace Clearr.Api;

/// <summary>Loads the HTTPS certificate: a .pfx, a PEM cert + key, or a self-signed one Clearr creates.</summary>
public static class Certificates
{
    public static X509Certificate2 Load(HttpsSettings h, ConfigPaths paths)
    {
        var certPath = (h.CertificatePath ?? "").Trim();
        if (certPath.Length == 0)
        {
            return SelfSigned(paths);
        }

        if (!File.Exists(certPath))
        {
            throw new ArgumentException($"Certificate file not found: {certPath} (is the folder mounted into the container?)");
        }

        var ext = Path.GetExtension(certPath).ToLowerInvariant();
        X509Certificate2 cert;
        if (ext is ".pfx" or ".p12")
        {
            cert = X509CertificateLoader.LoadPkcs12FromFile(certPath, string.IsNullOrEmpty(h.CertificatePassword) ? null : h.CertificatePassword);
        }
        else
        {
            var keyPath = string.IsNullOrWhiteSpace(h.KeyPath) ? certPath : h.KeyPath.Trim();
            if (!File.Exists(keyPath))
            {
                throw new ArgumentException($"Key file not found: {keyPath}");
            }

            cert = string.IsNullOrEmpty(h.CertificatePassword)
                ? X509Certificate2.CreateFromPemFile(certPath, keyPath)
                : X509Certificate2.CreateFromEncryptedPemFile(certPath, h.CertificatePassword, keyPath);
        }

        if (!cert.HasPrivateKey)
        {
            throw new ArgumentException("The certificate has no private key. Add the key file (or use a .pfx that includes it).");
        }

        return cert;
    }

    public static string Describe(X509Certificate2 c) =>
        $"{c.GetNameInfo(X509NameType.SimpleName, false)}, valid until {c.NotAfter:yyyy-MM-dd}" + (c.Subject == c.Issuer ? " (self-signed)" : "");

    private static X509Certificate2 SelfSigned(ConfigPaths paths)
    {
        var file = paths.File("clearr-selfsigned.pfx");
        if (File.Exists(file))
        {
            var existing = X509CertificateLoader.LoadPkcs12FromFile(file, null);
            if (existing.NotAfter > DateTime.Now.AddDays(30))
            {
                return existing;
            }
        }

        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=Clearr", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddDnsName(Environment.MachineName);
        san.AddIpAddress(System.Net.IPAddress.Loopback);
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        using var cert = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(5));
        File.WriteAllBytes(file, cert.Export(X509ContentType.Pfx));
        return X509CertificateLoader.LoadPkcs12FromFile(file, null);
    }
}
