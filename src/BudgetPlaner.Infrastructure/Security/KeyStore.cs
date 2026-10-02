using System.Security.Cryptography;
using System.Text.Json;
using BudgetPlaner.Application.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BudgetPlaner.Infrastructure.Security;

/// <summary>
/// Schlüsselverwaltung: Werte aus der Konfiguration (Produktion, z. B. Umgebungsvariablen) oder beim ersten Start
/// erzeugte Dateien unter <c>{DataDirectory}/keys</c>. Diese Dateien gehören ins Backup – ohne Schlüssel sind die
/// verschlüsselten Finanzdaten nicht mehr lesbar.
/// </summary>
public sealed class KeyStore
{
    public KeyStore(IConfiguration config, string dataDirectory, ILogger<KeyStore>? logger = null)
    {
        var dir = Path.Combine(dataDirectory, "keys");
        Directory.CreateDirectory(dir);
        EncryptionKey = LoadBytes(config["Security:EncryptionKey"], Path.Combine(dir, "encryption.key"), 32, logger);
        JwtSigningKey = LoadBytes(config["Security:JwtSigningKey"], Path.Combine(dir, "jwt.key"), 64, logger);
        (VapidPublicKey, VapidPrivateKey) = LoadVapid(config, Path.Combine(dir, "vapid.json"));
    }

    public byte[] EncryptionKey { get; }
    public byte[] JwtSigningKey { get; }
    public string VapidPublicKey { get; }
    public string VapidPrivateKey { get; }

    private static byte[] LoadBytes(string? configured, string file, int length, ILogger? logger)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var bytes = Convert.FromBase64String(configured);
            if (bytes.Length < length) throw new InvalidOperationException($"Schlüssel zu kurz (mind. {length} Bytes).");
            return bytes[..length];
        }
        if (File.Exists(file)) return Convert.FromBase64String(File.ReadAllText(file).Trim());
        var key = RandomNumberGenerator.GetBytes(length);
        File.WriteAllText(file, Convert.ToBase64String(key));
        logger?.LogWarning("Neuer Schlüssel erzeugt: {File} – bitte sichern!", file);
        return key;
    }

    private static (string Public, string Private) LoadVapid(IConfiguration config, string file)
    {
        var pub = config["Push:VapidPublicKey"];
        var priv = config["Push:VapidPrivateKey"];
        if (!string.IsNullOrWhiteSpace(pub) && !string.IsNullOrWhiteSpace(priv)) return (pub, priv);
        if (File.Exists(file))
        {
            var stored = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file))!;
            return (stored["publicKey"], stored["privateKey"]);
        }
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var p = ecdsa.ExportParameters(true);
        var publicKey = new byte[65];
        publicKey[0] = 0x04;
        p.Q.X!.CopyTo(publicKey, 1);
        p.Q.Y!.CopyTo(publicKey, 33);
        var keys = (Base64Url(publicKey), Base64Url(p.D!));
        File.WriteAllText(file, JsonSerializer.Serialize(new Dictionary<string, string> { ["publicKey"] = keys.Item1, ["privateKey"] = keys.Item2 }));
        return keys;
    }

    public static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>AES-256-GCM für Daten im Ruhezustand (NFR „verschlüsselte Speicherung“, B-17). Format: „v1:“ + Base64(Nonce|Tag|Chiffre).</summary>
public sealed class AesDataProtector(KeyStore keys) : IDataProtector
{
    private const string Prefix = "v1:";
    private readonly byte[] _key = keys.EncryptionKey;

    public string Protect(string plain)
    {
        var data = System.Text.Encoding.UTF8.GetBytes(plain);
        var buffer = new byte[12 + 16 + data.Length];
        var nonce = buffer.AsSpan(0, 12);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, data, buffer.AsSpan(28), buffer.AsSpan(12, 16));
        return Prefix + Convert.ToBase64String(buffer);
    }

    public string Unprotect(string protectedText)
    {
        if (!protectedText.StartsWith(Prefix, StringComparison.Ordinal)) return protectedText;
        var buffer = Convert.FromBase64String(protectedText[Prefix.Length..]);
        var plain = new byte[buffer.Length - 28];
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(buffer.AsSpan(0, 12), buffer.AsSpan(28), buffer.AsSpan(12, 16), plain);
        return System.Text.Encoding.UTF8.GetString(plain);
    }
}
