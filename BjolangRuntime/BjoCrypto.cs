/* This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 *
 * As a special exception to the Mozilla Public License, version 2.0, if you
 * compile your application source code and portions of this software are
 * embedded into the generated object code or executable form as a normal
 * consequence of the compilation process (such as inline functions,
 * templates, generics, or macros), you may redistribute such embedded portions
 * in such object code or executable form without complying with the source code
 * availability requirements or notice obligations of Section 3 of the MPL 2.0.
 */

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Bjolang.Runtime;

/// <summary>
/// The shim under <c>(std crypto hash)</c>.
/// </summary>
///
/// <remarks>
/// <para>
/// An algorithm crosses as its .NET name: "SHA256", "SHA3-256" and so on. The
/// Bjolang side maps its union to the name, because a
/// <see cref="HashAlgorithmName"/> is a struct that the module would otherwise
/// have to import and construct.
/// </para>
/// <para>
/// The .NET methods that take a <c>ReadOnlySpan</c> are called from here,
/// because Bjolang cannot pass a span.
/// </para>
/// </remarks>
public static class CryptoModule
{
    static HashAlgorithmName Name(string algorithm) => new HashAlgorithmName(algorithm);

    /// <summary>False when the platform does not have the algorithm. SHA-3 needs OpenSSL 1.1.1 or Windows 11.</summary>
    public static bool IsSupported(string algorithm) => algorithm switch
    {
        "SHA3-256" => SHA3_256.IsSupported,
        "SHA3-384" => SHA3_384.IsSupported,
        "SHA3-512" => SHA3_512.IsSupported,
        _ => true,
    };

    public static byte[] Hash(string algorithm, byte[] data) =>
        CryptographicOperations.HashData(Name(algorithm), data);

    public static byte[] HashString(string algorithm, string text) =>
        CryptographicOperations.HashData(Name(algorithm), Encoding.UTF8.GetBytes(text));

    public static byte[] HashFile(string algorithm, string path)
    {
        using var file = File.OpenRead(path);
        return CryptographicOperations.HashData(Name(algorithm), file);
    }

    public static async Task<byte[]> HashFileAsync(string algorithm, string path, CancellationToken token)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                                              4096, useAsync: true);
        return await CryptographicOperations.HashDataAsync(Name(algorithm), file, token);
    }

    public static byte[] Hmac(string algorithm, byte[] key, byte[] data) =>
        CryptographicOperations.HmacData(Name(algorithm), key, data);

    public static byte[] HmacString(string algorithm, byte[] key, string text) =>
        CryptographicOperations.HmacData(Name(algorithm), key, Encoding.UTF8.GetBytes(text));

    public static IncrementalHash MakeHasher(string algorithm) =>
        IncrementalHash.CreateHash(Name(algorithm));

    public static IncrementalHash MakeHmacHasher(string algorithm, byte[] key) =>
        IncrementalHash.CreateHMAC(Name(algorithm), key);

    public static void HasherAdd(IncrementalHash hasher, byte[] data) => hasher.AppendData(data);

    public static void HasherAddString(IncrementalHash hasher, string text) =>
        hasher.AppendData(Encoding.UTF8.GetBytes(text));

    /// <summary>The digest. The hasher is reset, so it can be used again.</summary>
    public static byte[] HasherFinish(IncrementalHash hasher) => hasher.GetHashAndReset();

    /// <summary>
    /// The time this takes depends on the lengths and not on the contents, so
    /// a caller that compares a secret cannot leak where the first difference is.
    /// </summary>
    public static bool FixedTimeEquals(byte[] a, byte[] b) =>
        CryptographicOperations.FixedTimeEquals(a, b);

    public static string ToHex(byte[] bytes) => Convert.ToHexStringLower(bytes);

    public static bool TryFromHex(string hex, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (hex.Length % 2 != 0) return false;
        var result = new byte[hex.Length / 2];
        var status = Convert.FromHexString(hex, result, out _, out var written);
        if (status != System.Buffers.OperationStatus.Done || written != result.Length) return false;
        bytes = result;
        return true;
    }

    // -----------------------------------------------------------------------
    // Passwords
    // -----------------------------------------------------------------------
    //
    // A stored password is a PHC string: $pbkdf2-sha256$i=600000$<salt>$<hash>.
    // The salt and the hash are standard base64 without padding, as the PHC
    // format specifies. The iteration count and the algorithm are in the
    // string, so a hash made with old parameters still verifies after the
    // defaults change.

    static string PhcId(string algorithm) => algorithm switch
    {
        "SHA256" => "pbkdf2-sha256",
        "SHA384" => "pbkdf2-sha384",
        "SHA512" => "pbkdf2-sha512",
        _ => throw new ArgumentException(
                 $"Passwords are hashed with SHA256, SHA384 or SHA512, not {algorithm}."),
    };

    static string? AlgorithmOfPhcId(string id) => id switch
    {
        "pbkdf2-sha256" => "SHA256",
        "pbkdf2-sha384" => "SHA384",
        "pbkdf2-sha512" => "SHA512",
        _ => null,
    };

    static string ToB64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=');

    static byte[] FromB64(string text)
    {
        var padded = (text.Length % 4) switch
        {
            0 => text,
            2 => text + "==",
            3 => text + "=",
            _ => throw new FormatException(),
        };
        return Convert.FromBase64String(padded);
    }

    static byte[] Derive(string password, byte[] salt, string algorithm, int iterations, int length) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations,
                                  Name(algorithm), length);

    public static string PasswordHash(string password, byte[] salt, string algorithm, int iterations)
    {
        var id = PhcId(algorithm);
        if (iterations < 1)
            throw new ArgumentException("The iteration count must be at least 1.");
        if (salt.Length < 8)
            throw new ArgumentException("The salt must be at least 8 bytes.");
        var hash = Derive(password, salt, algorithm, iterations,
                          HashAlgorithmLength(algorithm));
        return $"${id}$i={iterations}${ToB64(salt)}${ToB64(hash)}";
    }

    static int HashAlgorithmLength(string algorithm) => algorithm switch
    {
        "SHA256" => 32,
        "SHA384" => 48,
        "SHA512" => 64,
        _ => throw new ArgumentException($"Not a password algorithm: {algorithm}."),
    };

    readonly record struct Phc(string Algorithm, int Iterations, byte[] Salt, byte[] Hash);

    static Phc Parse(string encoded)
    {
        var fail = new ArgumentException(
            "Not a password hash: expected $pbkdf2-<sha256|sha384|sha512>$i=<n>$<salt>$<hash>.");
        // The leading `$` makes the first part empty.
        var parts = encoded.Split('$');
        if (parts.Length != 5 || parts[0] != "") throw fail;
        var algorithm = AlgorithmOfPhcId(parts[1]) ?? throw fail;
        if (!parts[2].StartsWith("i=", StringComparison.Ordinal)
            || !int.TryParse(parts[2].AsSpan(2), System.Globalization.NumberStyles.None,
                             System.Globalization.CultureInfo.InvariantCulture, out var iterations)
            || iterations < 1)
            throw fail;
        byte[] salt, hash;
        try
        {
            salt = FromB64(parts[3]);
            hash = FromB64(parts[4]);
        }
        catch (FormatException)
        {
            throw fail;
        }
        if (salt.Length == 0 || hash.Length == 0) throw fail;
        return new Phc(algorithm, iterations, salt, hash);
    }

    public static bool PasswordVerify(string password, string encoded)
    {
        var phc = Parse(encoded);
        var candidate = Derive(password, phc.Salt, phc.Algorithm, phc.Iterations, phc.Hash.Length);
        return CryptographicOperations.FixedTimeEquals(candidate, phc.Hash);
    }

    public static bool PasswordNeedsRehash(string encoded, string algorithm, int iterations)
    {
        var phc = Parse(encoded);
        return phc.Algorithm != algorithm
            || phc.Iterations < iterations
            || phc.Hash.Length != HashAlgorithmLength(algorithm);
    }
}
