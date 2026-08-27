using System.Buffers;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Turborama.UiPreview;

internal static class PreviewBuildInfo
{
    public const string Purpose = "Turborama.UI.Preview";

    public static bool IsCanonicalCommit(string? value)
        => value is { Length: 40 }
           && value.All(character => character is >= '0' and <= '9'
                                      or >= 'a' and <= 'f');
}

internal sealed record PreviewCredentialVerification(
    bool IsValid,
    string FailureCode,
    DateTimeOffset ExpiresAtUtc,
    string ManifestSha256)
{
    public static PreviewCredentialVerification Denied(string code)
        => new(false, code, DateTimeOffset.MinValue, string.Empty);

    public static PreviewCredentialVerification Allowed(
        DateTimeOffset expiresAtUtc,
        string manifestSha256)
        => new(true, string.Empty, expiresAtUtc, manifestSha256);
}

internal static class PreviewCredentialVerifier
{
    public const string CredentialFileName = "ui-preview.credential";
    internal const int Pbkdf2Iterations = 600_000;
    private const int MaximumProtectedFileBytes = 16 * 1024;
    private const int MaximumPayloadBytes = 8 * 1024;
    private const int SaltBytes = 32;
    private const int HashBytes = 32;
    private static readonly TimeSpan MaximumCredentialLifetime = TimeSpan.FromHours(72);
    private static readonly TimeSpan ClockTolerance = TimeSpan.FromMinutes(5);

    public static PreviewCredentialVerification VerifyFile(
        string credentialPath,
        SecureString password,
        string expectedCommit,
        DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialPath);
        ArgumentNullException.ThrowIfNull(password);
        if (!PreviewBuildInfo.IsCanonicalCommit(expectedCommit))
            return PreviewCredentialVerification.Denied("BUILD_ID_INVALID");

        byte[] protectedBytes = [];
        byte[] payloadBytes = [];
        try
        {
            var fullPath = Path.GetFullPath(credentialPath);
            var directory = Path.GetDirectoryName(fullPath)
                            ?? throw new SecurityException("Missing credential directory.");
            if (!Path.GetFileName(fullPath).Equals(
                    CredentialFileName,
                    StringComparison.Ordinal))
                return PreviewCredentialVerification.Denied("CREDENTIAL_UNAVAILABLE");

            var canonicalPath = LocalAssetPolicy.ResolvePackageFile(
                directory,
                CredentialFileName,
                MaximumProtectedFileBytes);
            if (!canonicalPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase))
                return PreviewCredentialVerification.Denied("CREDENTIAL_UNAVAILABLE");

            protectedBytes = LocalAssetPolicy.ReadBoundedFile(
                canonicalPath,
                MaximumProtectedFileBytes);
            payloadBytes = CurrentUserDpapi.Unprotect(protectedBytes);
            if (payloadBytes.Length is <= 0 or > MaximumPayloadBytes)
                return PreviewCredentialVerification.Denied("CREDENTIAL_INVALID");

            return VerifyPayload(payloadBytes, password, expectedCommit, nowUtc);
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or SecurityException
                                           or CryptographicException
                                           or JsonException
                                           or FormatException
                                           or ArgumentException
                                           or OverflowException)
        {
            return PreviewCredentialVerification.Denied("CREDENTIAL_INVALID");
        }
        finally
        {
            if (protectedBytes.Length != 0)
                CryptographicOperations.ZeroMemory(protectedBytes);
            if (payloadBytes.Length != 0)
                CryptographicOperations.ZeroMemory(payloadBytes);
        }
    }

    internal static PreviewCredentialVerification VerifyPayload(
        ReadOnlyMemory<byte> payloadBytes,
        SecureString password,
        string expectedCommit,
        DateTimeOffset nowUtc)
    {
        byte[] passwordBytes = [];
        byte[] salt = [];
        byte[] expectedHash = [];
        byte[] actualHash = [];
        try
        {
            using var document = StrictJson.Parse(payloadBytes, maximumDepth: 8);
            var root = document.RootElement;
            StrictJson.RequireExactMembers(
                root,
                "schemaVersion",
                "purpose",
                "commit",
                "issuedAtUtc",
                "expiresAtUtc",
                "iterations",
                "salt",
                "passwordHash",
                "manifestSha256");

            if (root.GetProperty("schemaVersion").GetInt32() != 1
                || !string.Equals(
                    root.GetProperty("purpose").GetString(),
                    PreviewBuildInfo.Purpose,
                    StringComparison.Ordinal)
                || !string.Equals(
                    root.GetProperty("commit").GetString(),
                    expectedCommit,
                    StringComparison.Ordinal)
                || root.GetProperty("iterations").GetInt32() != Pbkdf2Iterations)
                return PreviewCredentialVerification.Denied("CREDENTIAL_INVALID");

            var manifestSha256 = root.GetProperty("manifestSha256").GetString();
            if (!IsCanonicalSha256(manifestSha256))
                return PreviewCredentialVerification.Denied("CREDENTIAL_INVALID");

            var issuedAtUtc = ParseCanonicalUtc(
                root.GetProperty("issuedAtUtc").GetString());
            var expiresAtUtc = ParseCanonicalUtc(
                root.GetProperty("expiresAtUtc").GetString());
            var canonicalNow = nowUtc.ToUniversalTime();
            if (issuedAtUtc > canonicalNow + ClockTolerance
                || expiresAtUtc <= canonicalNow
                || expiresAtUtc <= issuedAtUtc
                || expiresAtUtc - issuedAtUtc > MaximumCredentialLifetime
                || issuedAtUtc < canonicalNow - MaximumCredentialLifetime - ClockTolerance)
                return PreviewCredentialVerification.Denied("CREDENTIAL_EXPIRED");

            salt = ParseCanonicalBase64(root.GetProperty("salt").GetString(), SaltBytes);
            expectedHash = ParseCanonicalBase64(
                root.GetProperty("passwordHash").GetString(),
                HashBytes);
            passwordBytes = SecureStringUtf8.Encode(password);
            if (passwordBytes.Length is < 16 or > 256)
                return PreviewCredentialVerification.Denied("PASSWORD_INVALID");

            actualHash = Rfc2898DeriveBytes.Pbkdf2(
                passwordBytes,
                salt,
                Pbkdf2Iterations,
                HashAlgorithmName.SHA256,
                HashBytes);
            return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash)
                ? PreviewCredentialVerification.Allowed(
                    expiresAtUtc,
                    manifestSha256!)
                : PreviewCredentialVerification.Denied("PASSWORD_INVALID");
        }
        catch (Exception exception) when (exception is JsonException
                                           or FormatException
                                           or ArgumentException
                                           or InvalidOperationException
                                           or OverflowException
                                           or CryptographicException)
        {
            return PreviewCredentialVerification.Denied("CREDENTIAL_INVALID");
        }
        finally
        {
            if (passwordBytes.Length != 0)
                CryptographicOperations.ZeroMemory(passwordBytes);
            if (salt.Length != 0)
                CryptographicOperations.ZeroMemory(salt);
            if (expectedHash.Length != 0)
                CryptographicOperations.ZeroMemory(expectedHash);
            if (actualHash.Length != 0)
                CryptographicOperations.ZeroMemory(actualHash);
        }
    }

    private static bool IsCanonicalSha256(string? value)
        => value is { Length: 64 }
           && value.All(character => character is >= '0' and <= '9'
                                      or >= 'a' and <= 'f');

    private static DateTimeOffset ParseCanonicalUtc(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !DateTimeOffset.TryParseExact(
                value,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed)
            || !parsed.ToString("O", CultureInfo.InvariantCulture)
                .Equals(value, StringComparison.Ordinal))
            throw new FormatException("Invalid timestamp.");
        return parsed;
    }

    private static byte[] ParseCanonicalBase64(string? value, int expectedLength)
    {
        if (string.IsNullOrEmpty(value) || value.Any(char.IsWhiteSpace))
            throw new FormatException("Invalid Base64.");
        var bytes = Convert.FromBase64String(value);
        if (bytes.Length != expectedLength
            || !Convert.ToBase64String(bytes).Equals(value, StringComparison.Ordinal))
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new FormatException("Non-canonical Base64.");
        }
        return bytes;
    }
}

internal static class SecureStringUtf8
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static byte[] Encode(SecureString secureString)
    {
        ArgumentNullException.ThrowIfNull(secureString);
        if (secureString.Length == 0)
            return [];

        var pointer = IntPtr.Zero;
        char[]? rented = null;
        try
        {
            pointer = Marshal.SecureStringToBSTR(secureString);
            var byteLength = Marshal.ReadInt32(pointer, -sizeof(int));
            var characterCount = checked(byteLength / sizeof(char));
            rented = ArrayPool<char>.Shared.Rent(characterCount);
            Marshal.Copy(pointer, rented, 0, characterCount);
            var result = GC.AllocateUninitializedArray<byte>(
                StrictUtf8.GetByteCount(rented.AsSpan(0, characterCount)));
            StrictUtf8.GetBytes(
                rented.AsSpan(0, characterCount),
                result.AsSpan());
            return result;
        }
        finally
        {
            if (rented is not null)
            {
                rented.AsSpan().Clear();
                ArrayPool<char>.Shared.Return(rented);
            }
            if (pointer != IntPtr.Zero)
                Marshal.ZeroFreeBSTR(pointer);
        }
    }
}

internal static class CurrentUserDpapi
{
    private const uint CryptProtectUiForbidden = 0x1;
    private const string EntropyDomain = "TurboRama.UI.Preview.Credential/v1";

    public static byte[] Unprotect(ReadOnlySpan<byte> protectedBytes)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DPAPI requires Windows.");

        var protectedCopy = protectedBytes.ToArray();
        var entropy = Encoding.UTF8.GetBytes(EntropyDomain);
        var input = DataBlob.Allocate(protectedCopy);
        var optionalEntropy = DataBlob.Allocate(entropy);
        try
        {
            if (!CryptUnprotectData(
                    ref input,
                    out var description,
                    ref optionalEntropy,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out var output))
                throw new CryptographicException(Marshal.GetLastWin32Error());

            try
            {
                return output.CopyToManaged();
            }
            finally
            {
                if (description != IntPtr.Zero)
                    _ = LocalFree(description);
                output.ZeroAndFreeLocal();
            }
        }
        finally
        {
            input.ZeroAndFreeHeap();
            optionalEntropy.ZeroAndFreeHeap();
            CryptographicOperations.ZeroMemory(protectedCopy);
            CryptographicOperations.ZeroMemory(entropy);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public IntPtr Data;

        public static DataBlob Allocate(ReadOnlySpan<byte> bytes)
        {
            if (bytes.IsEmpty)
                return default;
            var temporary = bytes.ToArray();
            var data = Marshal.AllocHGlobal(temporary.Length);
            try
            {
                Marshal.Copy(temporary, 0, data, temporary.Length);
                return new DataBlob { Length = temporary.Length, Data = data };
            }
            catch
            {
                Marshal.FreeHGlobal(data);
                throw;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(temporary);
            }
        }

        public readonly byte[] CopyToManaged()
        {
            if (Length is <= 0 or > 8 * 1024 || Data == IntPtr.Zero)
                throw new CryptographicException("Invalid DPAPI payload.");
            var bytes = GC.AllocateUninitializedArray<byte>(Length);
            Marshal.Copy(Data, bytes, 0, Length);
            return bytes;
        }

        public void ZeroAndFreeHeap()
        {
            if (Data == IntPtr.Zero)
                return;
            ZeroUnmanaged(Data, Length);
            Marshal.FreeHGlobal(Data);
            this = default;
        }

        public void ZeroAndFreeLocal()
        {
            if (Data == IntPtr.Zero)
                return;
            ZeroUnmanaged(Data, Length);
            _ = LocalFree(Data);
            this = default;
        }
    }

    private static void ZeroUnmanaged(IntPtr data, int length)
    {
        if (data == IntPtr.Zero || length <= 0)
            return;
        var zeros = new byte[Math.Min(length, 4096)];
        for (var offset = 0; offset < length; offset += zeros.Length)
        {
            var count = Math.Min(zeros.Length, length - offset);
            Marshal.Copy(zeros, 0, IntPtr.Add(data, offset), count);
        }
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        out IntPtr dataDescription,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        uint flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll", SetLastError = false)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
