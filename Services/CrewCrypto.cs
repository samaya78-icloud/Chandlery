using System.Security.Cryptography;
using System.Text;

namespace Chandlery.Services;

/// <summary>
/// Encrypts crew ID numbers (passport, NRIC, seaman's book) so they are unreadable in the database, in backups and while queued
/// for sync. AES-256-GCM; the stored text is "v1:" and base64 of nonce, tag and ciphertext. The office and every vessel must hold the
/// same key (configuration <c>Crew:IdKey</c>, 32 random bytes as base64); without it a vessel shows the masked number only.
/// </summary>
public sealed class CrewCrypto
{
    private readonly byte[]? _key;

    public CrewCrypto(byte[]? key) => _key = key is { Length: 32 } ? key : key is null ? null : throw new ArgumentException("The ID key must be 32 bytes.");

    /// <summary>Reads <c>Crew:IdKey</c>. In Development a fixed key is used when none is set, so demos work without set-up; elsewhere there is no key until one is configured.</summary>
    public static CrewCrypto From(IConfiguration config, IHostEnvironment env)
    {
        var configured = config["Crew:IdKey"];
        if (!string.IsNullOrWhiteSpace(configured)) return new CrewCrypto(Convert.FromBase64String(configured));
        return new CrewCrypto(env.IsDevelopment() ? SHA256.HashData(Encoding.UTF8.GetBytes("chandlery-development-crew-id-key")) : null);
    }

    public bool HasKey => _key is not null;

    public string Encrypt(string plain)
    {
        if (plain == "") return "";
        var key = _key ?? throw new InvalidOperationException("No crew ID encryption key is configured (Crew:IdKey).");
        var nonce = RandomNumberGenerator.GetBytes(12);
        var data = Encoding.UTF8.GetBytes(plain);
        var cipher = new byte[data.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, data, cipher, tag);
        return "v1:" + Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    public string Decrypt(string stored)
    {
        if (stored == "") return "";
        var key = _key ?? throw new InvalidOperationException("No crew ID encryption key is configured (Crew:IdKey).");
        if (!stored.StartsWith("v1:")) throw new InvalidOperationException("Unknown ID number format.");
        var all = Convert.FromBase64String(stored[3..]);
        var data = new byte[all.Length - 28];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(all.AsSpan(0, 12), all.AsSpan(28), all.AsSpan(12, 16), data);
        return Encoding.UTF8.GetString(data);
    }

    /// <summary>The last four characters, for showing the number masked.</summary>
    public static string Last4(string plain) => plain.Length <= 4 ? plain : plain[^4..];

    public static string Mask(string last4) => last4 == "" ? "" : "••••" + last4;
}
