using System.IO;
using System.Reflection;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TurboBoxManager.Licensing;

public enum SuiteIdentityPolicy
{
    TpmRequired,
    TpmPreferred,
    SoftwareOnly
}

public static class SuiteIdentityPolicyCodec
{
    public static string Format(SuiteIdentityPolicy policy) => policy switch
    {
        SuiteIdentityPolicy.TpmRequired => "TPM_REQUIRED",
        SuiteIdentityPolicy.TpmPreferred => "TPM_PREFERRED",
        SuiteIdentityPolicy.SoftwareOnly => "SOFTWARE_ONLY",
        _ => throw new ArgumentOutOfRangeException(nameof(policy))
    };

    public static SuiteIdentityPolicy Parse(string? value) => value switch
    {
        "TPM_REQUIRED" => SuiteIdentityPolicy.TpmRequired,
        "TPM_PREFERRED" => SuiteIdentityPolicy.TpmPreferred,
        "SOFTWARE_ONLY" => SuiteIdentityPolicy.SoftwareOnly,
        _ => throw new SecurityException("A politica da identidade Suite e invalida.")
    };
}

public sealed record SuiteAuthorityPayload(
    int SchemaVersion,
    string Kind,
    string ProductId,
    string BaseUrl,
    string IdentityPolicy,
    string OnlineAssertionAlgorithm,
    string OnlineAssertionKeyId,
    string OnlineAssertionPublicKeySpki,
    string TlsServerSpkiSha256,
    long IssuedAtUnixSeconds,
    long ExpiresAtUnixSeconds);

public sealed record SuiteAuthorityEnvelope(
    int SchemaVersion,
    string Algorithm,
    string KeyId,
    string Payload,
    string Signature);

public sealed class SuiteAuthorityConfiguration
{
    internal SuiteAuthorityConfiguration(Uri baseUri,
        SuiteIdentityPolicy identityPolicy, string keyId,
        string onlineAssertionKeyId, byte[] onlineAssertionSpki,
        string tlsServerSpkiSha256, DateTimeOffset issuedAt, DateTimeOffset expiresAt)
    {
        BaseUri = baseUri;
        IdentityPolicy = identityPolicy;
        KeyId = keyId;
        OnlineAssertionKeyId = onlineAssertionKeyId;
        _onlineAssertionSpki = onlineAssertionSpki.ToArray();
        TlsServerSpkiSha256 = tlsServerSpkiSha256;
        IssuedAt = issuedAt;
        ExpiresAt = expiresAt;
    }

    private readonly byte[] _onlineAssertionSpki;

    public Uri BaseUri { get; }
    public SuiteIdentityPolicy IdentityPolicy { get; }
    public string KeyId { get; }
    public string OnlineAssertionKeyId { get; }
    public string TlsServerSpkiSha256 { get; }
    public DateTimeOffset IssuedAt { get; }
    public DateTimeOffset ExpiresAt { get; }

    internal byte[] ExportOnlineAssertionSpki() => _onlineAssertionSpki.ToArray();
}

/// <summary>
/// Verifies the signed, non-secret authority coordinates embedded by the release
/// owner. No production trust key or license value is supplied by this source.
/// </summary>
public static class SuiteAuthorityConfigurationVerifier
{
    public const string ConfigurationMetadataKey =
        "TurboRama.Suite.AuthorityConfigurationBase64";
    public const string ConfigurationSha256MetadataKey =
        "TurboRama.Suite.AuthorityConfigurationSha256";
    public const string IssuerSpkiMetadataKey =
        "TurboRama.Suite.AuthorityIssuerSpkiBase64";
    public const string ConfigurationKind = "TURBORAMA_SUITE_AUTHORITY";
    public const string SignatureAlgorithm = "rsa-pss-sha256";

    private static readonly byte[] SignatureDomain =
        Encoding.ASCII.GetBytes("TurboRamaSuiteAuthorityConfiguration/v1\0");

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.Default,
        Indented = false,
        SkipValidation = false
    };

    private static readonly JsonSerializerOptions StrictJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        MaxDepth = 8,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict,
        Encoder = JavaScriptEncoder.Default,
        WriteIndented = false
    };

    public static byte[] CanonicalPayload(SuiteAuthorityPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ValidatePayloadShape(payload);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", payload.SchemaVersion);
            writer.WriteString("kind", payload.Kind);
            writer.WriteString("productId", payload.ProductId);
            writer.WriteString("baseUrl", payload.BaseUrl);
            writer.WriteString("identityPolicy", payload.IdentityPolicy);
            writer.WriteString("onlineAssertionAlgorithm", payload.OnlineAssertionAlgorithm);
            writer.WriteString("onlineAssertionKeyId", payload.OnlineAssertionKeyId);
            writer.WriteString("onlineAssertionPublicKeySpki",
                payload.OnlineAssertionPublicKeySpki);
            writer.WriteString("tlsServerSpkiSha256", payload.TlsServerSpkiSha256);
            writer.WriteNumber("issuedAtUnixSeconds", payload.IssuedAtUnixSeconds);
            writer.WriteNumber("expiresAtUnixSeconds", payload.ExpiresAtUnixSeconds);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    public static byte[] BuildSigningMessage(SuiteAuthorityPayload payload)
    {
        var canonical = CanonicalPayload(payload);
        var output = new byte[SignatureDomain.Length + canonical.Length];
        SignatureDomain.CopyTo(output, 0);
        canonical.CopyTo(output, SignatureDomain.Length);
        CryptographicOperations.ZeroMemory(canonical);
        return output;
    }

    public static string KeyIdFromSpki(ReadOnlySpan<byte> issuerSpki)
    {
        ValidateIssuerSpki(issuerSpki);
        return Convert.ToHexString(SHA256.HashData(issuerSpki)).ToLowerInvariant();
    }

    public static byte[] SerializeEnvelope(SuiteAuthorityEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return JsonSerializer.SerializeToUtf8Bytes(envelope, StrictJsonOptions);
    }

    public static SuiteAuthorityConfiguration VerifyPinnedEnvelope(
        ReadOnlySpan<byte> envelopeUtf8,
        ReadOnlySpan<byte> issuerSpki,
        string expectedConfigurationSha256,
        TimeProvider? timeProvider = null)
    {
        if (envelopeUtf8.Length is < 64 or > 32 * 1024)
            throw new SecurityException("A configuracao da autoridade possui tamanho invalido.");
        var canonicalExpected = SuiteOnlineLicenseProtocol.RequireHex(
            expectedConfigurationSha256,
            "AuthorityConfigurationSha256",
            64);
        if (!string.Equals(
                canonicalExpected,
                expectedConfigurationSha256,
                StringComparison.Ordinal))
            throw new SecurityException("O hash ancorado da autoridade nao esta canonico.");

        var expected = Convert.FromHexString(canonicalExpected);
        var actual = SHA256.HashData(envelopeUtf8);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(actual, expected))
                throw new SecurityException(
                    "A configuracao da autoridade nao corresponde ao envelope aprovado.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(actual);
            CryptographicOperations.ZeroMemory(expected);
        }

        return VerifyEnvelope(envelopeUtf8, issuerSpki, timeProvider);
    }

    public static SuiteAuthorityConfiguration VerifyEnvelope(ReadOnlySpan<byte> envelopeUtf8,
        ReadOnlySpan<byte> issuerSpki, TimeProvider? timeProvider = null)
    {
        if (envelopeUtf8.Length is < 64 or > 32 * 1024)
            throw new SecurityException("A configuracao da autoridade possui tamanho invalido.");
        RejectDuplicateProperties(envelopeUtf8);
        SuiteAuthorityEnvelope envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<SuiteAuthorityEnvelope>(
                envelopeUtf8, StrictJsonOptions)
                ?? throw new SecurityException("A configuracao da autoridade esta vazia.");
        }
        catch (JsonException ex)
        {
            throw new SecurityException("A configuracao da autoridade possui formato invalido.", ex);
        }

        ValidateIssuerSpki(issuerSpki);
        if (envelope.SchemaVersion != SuiteOnlineLicenseProtocol.SchemaVersion
            || !string.Equals(envelope.Algorithm, SignatureAlgorithm, StringComparison.Ordinal))
            throw new SecurityException("O envelope da autoridade possui versao ou algoritmo invalido.");

        var expectedKeyId = KeyIdFromSpki(issuerSpki);
        var declaredKeyId = SuiteOnlineLicenseProtocol.RequireHex(
            envelope.KeyId, "KeyId", 64);
        if (!string.Equals(declaredKeyId, envelope.KeyId, StringComparison.Ordinal)
            || !SuiteOnlineLicenseProtocol.FixedHexEquals(declaredKeyId, expectedKeyId))
            throw new SecurityException("A chave declarada pela configuracao nao e confiavel.");

        var payloadBytes = DecodeCanonicalBase64(envelope.Payload, "payload", 32, 16 * 1024);
        var signature = DecodeCanonicalBase64(envelope.Signature, "signature", 256, 512);
        byte[] message = Array.Empty<byte>();
        try
        {
            RejectDuplicateProperties(payloadBytes);
            SuiteAuthorityPayload payload;
            try
            {
                payload = JsonSerializer.Deserialize<SuiteAuthorityPayload>(
                    payloadBytes, StrictJsonOptions)
                    ?? throw new SecurityException("O payload da autoridade esta vazio.");
            }
            catch (JsonException ex)
            {
                throw new SecurityException("O payload da autoridade possui formato invalido.", ex);
            }

            var canonical = CanonicalPayload(payload);
            try
            {
                if (!canonical.AsSpan().SequenceEqual(payloadBytes))
                    throw new SecurityException("O payload da autoridade nao usa JSON canonico.");
            }
            finally { CryptographicOperations.ZeroMemory(canonical); }

            if (SuiteOnlineLicenseProtocol.FixedHexEquals(
                    payload.OnlineAssertionKeyId, expectedKeyId))
                throw new SecurityException(
                    "A chave on-line deve ser separada da chave offline da autoridade.");

            message = BuildSigningMessage(payload);
            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(issuerSpki, out var consumed);
            if (consumed != issuerSpki.Length
                || !rsa.VerifyData(message, signature, HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pss))
                throw new SecurityException("A assinatura da autoridade e invalida.");

            return Materialize(payload, expectedKeyId, timeProvider ?? TimeProvider.System);
        }
        catch (CryptographicException ex)
        {
            throw new SecurityException("Nao foi possivel validar a chave da autoridade.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payloadBytes);
            CryptographicOperations.ZeroMemory(signature);
            if (message.Length != 0) CryptographicOperations.ZeroMemory(message);
        }
    }

    private static SuiteAuthorityConfiguration Materialize(SuiteAuthorityPayload payload,
        string keyId, TimeProvider timeProvider)
    {
        ValidatePayloadShape(payload);
        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var latestAllowedIssue = Math.Min(
            SuiteOnlineLicenseProtocol.MaximumUnixTimeSeconds, now + 300);
        var validitySeconds = payload.ExpiresAtUnixSeconds
            - payload.IssuedAtUnixSeconds;
        if (payload.IssuedAtUnixSeconds > latestAllowedIssue
            || payload.ExpiresAtUnixSeconds <= now
            || validitySeconds > 366L * 24 * 60 * 60)
            throw new SecurityException("A configuracao da autoridade esta fora da validade.");

        var onlineSpki = DecodeCanonicalBase64(payload.OnlineAssertionPublicKeySpki,
            "chave publica on-line", 256, 4096);
        try
        {
            ValidateIssuerSpki(onlineSpki);
            return new SuiteAuthorityConfiguration(
                new Uri(payload.BaseUrl, UriKind.Absolute),
                SuiteIdentityPolicyCodec.Parse(payload.IdentityPolicy),
                keyId,
                payload.OnlineAssertionKeyId,
                onlineSpki,
                payload.TlsServerSpkiSha256,
                DateTimeOffset.FromUnixTimeSeconds(payload.IssuedAtUnixSeconds),
                DateTimeOffset.FromUnixTimeSeconds(payload.ExpiresAtUnixSeconds));
        }
        finally { CryptographicOperations.ZeroMemory(onlineSpki); }
    }

    private static void ValidatePayloadShape(SuiteAuthorityPayload payload)
    {
        if (payload.SchemaVersion != SuiteOnlineLicenseProtocol.SchemaVersion
            || !string.Equals(payload.Kind, ConfigurationKind, StringComparison.Ordinal)
            || !string.Equals(payload.ProductId, SuiteOnlineLicenseProtocol.ProductId,
                StringComparison.Ordinal))
            throw new SecurityException("A configuracao nao pertence ao produto TURBORAMA_SUITE.");

        _ = SuiteIdentityPolicyCodec.Parse(payload.IdentityPolicy);

        if (!string.Equals(payload.OnlineAssertionAlgorithm, SignatureAlgorithm,
                StringComparison.Ordinal))
            throw new SecurityException("O algoritmo da chave on-line e invalido.");

        var canonicalOnlineKeyId = SuiteOnlineLicenseProtocol.RequireHex(
            payload.OnlineAssertionKeyId, "OnlineAssertionKeyId", 64);
        var canonicalTlsPin = SuiteOnlineLicenseProtocol.RequireHex(
            payload.TlsServerSpkiSha256, "TlsServerSpkiSha256", 64);
        if (!string.Equals(canonicalOnlineKeyId, payload.OnlineAssertionKeyId,
                StringComparison.Ordinal)
            || !string.Equals(canonicalTlsPin, payload.TlsServerSpkiSha256,
                StringComparison.Ordinal))
            throw new SecurityException("Os identificadores da autoridade nao estao canonicos.");

        var onlineSpki = DecodeCanonicalBase64(payload.OnlineAssertionPublicKeySpki,
            "chave publica on-line", 256, 4096);
        try
        {
            var actualOnlineKeyId = KeyIdFromSpki(onlineSpki);
            if (!SuiteOnlineLicenseProtocol.FixedHexEquals(
                    payload.OnlineAssertionKeyId, actualOnlineKeyId))
                throw new SecurityException(
                    "O KeyId on-line nao corresponde a chave publica autorizada.");
        }
        finally { CryptographicOperations.ZeroMemory(onlineSpki); }

        if (!Uri.TryCreate(payload.BaseUrl, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || !payload.BaseUrl.EndsWith('/')
            || !string.Equals(uri.AbsoluteUri, payload.BaseUrl, StringComparison.Ordinal))
            throw new SecurityException("A URL da autoridade deve ser HTTPS absoluta e canonica.");
        SuiteOnlineLicenseProtocol.ValidateUnixTimeSeconds(
            payload.IssuedAtUnixSeconds, "A emissao da autoridade");
        SuiteOnlineLicenseProtocol.ValidateUnixTimeSeconds(
            payload.ExpiresAtUnixSeconds, "A expiracao da autoridade");
        if (payload.ExpiresAtUnixSeconds <= payload.IssuedAtUnixSeconds)
            throw new SecurityException("A validade da autoridade e invalida.");
    }

    private static void ValidateIssuerSpki(ReadOnlySpan<byte> issuerSpki)
    {
        if (issuerSpki.Length is < 256 or > 4096)
            throw new SecurityException("A chave da autoridade possui tamanho invalido.");
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(issuerSpki, out var consumed);
            if (consumed != issuerSpki.Length || rsa.KeySize is < 2048 or > 4096)
                throw new SecurityException("A chave da autoridade nao e RSA compativel.");
            var canonical = rsa.ExportSubjectPublicKeyInfo();
            try
            {
                if (!canonical.AsSpan().SequenceEqual(issuerSpki))
                    throw new SecurityException("A chave da autoridade nao usa SPKI DER canonico.");
            }
            finally { CryptographicOperations.ZeroMemory(canonical); }
        }
        catch (CryptographicException ex)
        {
            throw new SecurityException("A chave da autoridade e invalida.", ex);
        }
    }

    private static byte[] DecodeCanonicalBase64(string? value, string label,
        int minimumBytes, int maximumBytes)
    {
        var encoded = value ?? "";
        if (encoded.Any(char.IsWhiteSpace))
            throw new SecurityException($"O {label} da autoridade e invalido.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(encoded); }
        catch (FormatException ex)
        {
            throw new SecurityException($"O {label} da autoridade e invalido.", ex);
        }
        if (bytes.Length < minimumBytes || bytes.Length > maximumBytes
            || !string.Equals(Convert.ToBase64String(bytes), encoded, StringComparison.Ordinal))
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new SecurityException($"O {label} da autoridade e invalido.");
        }
        return bytes;
    }

    private static void RejectDuplicateProperties(ReadOnlySpan<byte> utf8)
    {
        var copy = utf8.ToArray();
        try
        {
            using var document = JsonDocument.Parse(copy, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8
            });
            RejectDuplicateProperties(document.RootElement);
        }
        catch (JsonException ex)
        {
            throw new SecurityException("A configuracao da autoridade possui JSON invalido.", ex);
        }
        finally { CryptographicOperations.ZeroMemory(copy); }
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new SecurityException("A configuracao da autoridade contem campo duplicado.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
        }
    }
}

internal sealed record SuiteAuthorityLoadResult(
    SuiteAuthorityConfiguration? Configuration,
    string FailureCode);

internal static class SuiteEmbeddedAuthorityLoader
{
    internal const string MissingConfiguration = "AUTHORITY_CONFIGURATION_MISSING";
    internal const string InvalidConfiguration = "AUTHORITY_CONFIGURATION_INVALID";

    public static SuiteAuthorityLoadResult Load(Assembly assembly, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(timeProvider);
        try
        {
            var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToArray();
            var envelopeValues = metadata
                .Where(item => string.Equals(item.Key,
                    SuiteAuthorityConfigurationVerifier.ConfigurationMetadataKey,
                    StringComparison.Ordinal))
                .Select(item => item.Value).ToArray();
            var issuerValues = metadata
                .Where(item => string.Equals(item.Key,
                    SuiteAuthorityConfigurationVerifier.IssuerSpkiMetadataKey,
                    StringComparison.Ordinal))
                .Select(item => item.Value).ToArray();
            var configurationHashValues = metadata
                .Where(item => string.Equals(item.Key,
                    SuiteAuthorityConfigurationVerifier.ConfigurationSha256MetadataKey,
                    StringComparison.Ordinal))
                .Select(item => item.Value).ToArray();

            if (envelopeValues.Length == 0
                && issuerValues.Length == 0
                && configurationHashValues.Length == 0)
                return new SuiteAuthorityLoadResult(null, MissingConfiguration);
            if (envelopeValues.Length != 1
                || issuerValues.Length != 1
                || configurationHashValues.Length != 1
                || string.IsNullOrEmpty(envelopeValues[0])
                || string.IsNullOrEmpty(issuerValues[0])
                || string.IsNullOrEmpty(configurationHashValues[0]))
                return new SuiteAuthorityLoadResult(null, InvalidConfiguration);

            byte[] envelope = Array.Empty<byte>();
            byte[] issuer = Array.Empty<byte>();
            try
            {
                envelope = DecodeMetadata(envelopeValues[0]!, 64, 32 * 1024);
                issuer = DecodeMetadata(issuerValues[0]!, 256, 4096);
                var configuration = SuiteAuthorityConfigurationVerifier.VerifyPinnedEnvelope(
                    envelope,
                    issuer,
                    configurationHashValues[0]!,
                    timeProvider);
                return new SuiteAuthorityLoadResult(configuration, "");
            }
            finally
            {
                if (envelope.Length != 0) CryptographicOperations.ZeroMemory(envelope);
                if (issuer.Length != 0) CryptographicOperations.ZeroMemory(issuer);
            }
        }
        catch (Exception ex) when (ex is SecurityException or FormatException
            or CryptographicException or ArgumentException)
        {
            return new SuiteAuthorityLoadResult(null, InvalidConfiguration);
        }
    }

    private static byte[] DecodeMetadata(string value, int minimum, int maximum)
    {
        if (value.Any(char.IsWhiteSpace)) throw new FormatException();
        var bytes = Convert.FromBase64String(value);
        if (bytes.Length < minimum || bytes.Length > maximum
            || !string.Equals(Convert.ToBase64String(bytes), value, StringComparison.Ordinal))
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new FormatException();
        }
        return bytes;
    }
}
