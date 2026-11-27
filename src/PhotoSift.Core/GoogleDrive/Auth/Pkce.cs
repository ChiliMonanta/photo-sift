using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace PhotoSift.Core.GoogleDrive.Auth;

/// <summary>Proof Key for Code Exchange (RFC 7636) with the S256 method, and random OAuth <c>state</c> values.</summary>
public static class Pkce
{
    /// <summary>A new random code verifier (43 characters, 256 bits of entropy).</summary>
    public static string CreateVerifier() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    public static string ChallengeFor(string verifier) =>
        Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    /// <summary>A random value that ties the browser's redirect to this sign-in attempt.</summary>
    public static string CreateState() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));
}
