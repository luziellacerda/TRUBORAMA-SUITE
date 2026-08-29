using System.IO;
using System.Reflection;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TurboBoxManager.Licensing;

public sealed record SuiteContentAuthorityPayload(
    int SchemaVersion,
    string Kind,
    string ProductId,
    string BaseUrl,
    string ContentAssertionAlgorithm,
    string ContentAssertionKeyId,
    string ContentAssertionPublicKeySpki,
    string TlsServerSpkiSha256Current,
    string TlsServerSpkiSha256Next,
    long IssuedAtUnixSeconds,
    long ExpiresAtUnixSeconds);

public sealed record SuiteContentAuthorityEnvelope(
    int SchemaVersion,
    string Algorithm,
    string KeyId,
    string Payload,
    string Signature);

public sealed class SuiteContentAuthorityConfiguration
{
    private readonly byte[] _contentAssertionSpki;
    private readonly string[] _tlsServerSpkiSha256Pins;

    internal SuiteContentAuthorityConfiguration(
        Uri baseUri,
        string issuerKeyId,
        string contentAssertionKeyId,
        byte[] contentAssertionSpki,
        IEnumerable<string> tlsServerSpkiSha256Pins,
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt)
    {
        BaseUri = baseUri;
        IssuerKeyId = issuerKeyId;
        ContentAssertionKeyId = contentAssertionKeyId;
        _contentAssertionSpki = contentAssertionSpki.ToArray();
        _tlsServerSpkiSha256Pins = tlsServerSpkiSha256Pins.ToArray();
        IssuedAt = issuedAt;
        ExpiresAt = expiresAt;
    }

    public Uri BaseUri { get; }
    public string IssuerKeyId { get; }
    public string ContentAssertionKeyId { get; }
    public IReadOnlyList<string> TlsServerSpkiSha256Pins
        => Array.AsReadOnly(_tlsServerSpkiSha256Pins);
    public DateTimeOffset IssuedAt { get; }
    public DateTimeOffset ExpiresAt { get; }

    internal byte[] ExportContentAssertionSpki()
        => _contentAssertionSpki.ToArray();
}

public static class SuiteContentAuthorityConfigurationVerifier
{
    public const string ConfigurationMetadataKey =
        "TurboRama.Suite.ContentAuthorityConfigurationBase64";
    public const string ConfigurationSha256MetadataKey =
        "TurboRama.Suite.ContentAuthorityConfigurationSha256";
    public const string IssuerSpkiMetadataKey =
        "TurboRama.Suite.ContentAuthorityIssuerSpkiBase64";
    public const string ConfigurationKind =
        "TURBORAMA_SUITE_CONTENT_AUTHORITY";
    public const string SignatureAlgorithm = "rsa-pss-sha256";

    private static readonly byte[] SignatureDomain = Encoding.ASCII.GetBytes(
        "TurboRamaSuiteContentAuthorityConfiguration/v1\0");

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

    public static byte[] CanonicalPayload(SuiteContentAuthorityPayload payload)
    {
        ValidatePayloadShape(payload);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", payload.SchemaVersion);
            writer.WriteString("kind", payload.Kind);
            writer.WriteString("productId", payload.ProductId);
            writer.WriteString("baseUrl", payload.BaseUrl);
            writer.WriteString("contentAssertionAlgorithm",
                payload.ContentAssertionAlgorithm);
            writer.WriteString("contentAssertionKeyId",
                payload.ContentAssertionKeyId);
            writer.WriteString("contentAssertionPublicKeySpki",
                payload.ContentAssertionPublicKeySpki);
            writer.WriteString("tlsServerSpkiSha256Current",
                payload.TlsServerSpkiSha256Current);
            writer.WriteString("tlsServerSpkiSha256Next",
                payload.TlsServerSpkiSha256Next);
            writer.WriteNumber("issuedAtUnixSeconds",
                payload.IssuedAtUnixSeconds);
            writer.WriteNumber("expiresAtUnixSeconds",
                payload.ExpiresAtUnixSeconds);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    public static byte[] BuildSigningMessage(
        SuiteContentAuthorityPayload payload)
    {
        var canonical = CanonicalPayload(payload);
        try
        {
            var output = new byte[SignatureDomain.Length + canonical.Length];
            SignatureDomain.CopyTo(output, 0);
            canonical.CopyTo(output, SignatureDomain.Length);
            return output;
        }
        finally { CryptographicOperations.ZeroMemory(canonical); }
    }

    public static string KeyIdFromSpki(ReadOnlySpan<byte> spki)
    {
        ValidateRsaSpki(spki, "A chave da autoridade de conteudo");
        return Convert.ToHexString(SHA256.HashData(spki)).ToLowerInvariant();
    }

    public static byte[] SerializeEnvelope(
        SuiteContentAuthorityEnvelope envelope)
        => JsonSerializer.SerializeToUtf8Bytes(
            envelope ?? throw new ArgumentNullException(nameof(envelope)),
            StrictJsonOptions);

    public static SuiteContentAuthorityConfiguration VerifyPinnedEnvelope(
        ReadOnlySpan<byte> envelopeUtf8,
        ReadOnlySpan<byte> issuerSpki,
        string expectedConfigurationSha256,
        TimeProvider? timeProvider = null)
    {
        var canonicalHash = RequireCanonicalHex(
            expectedConfigurationSha256, "ContentAuthorityConfigurationSha256");
        var expected = Convert.FromHexString(canonicalHash);
        var actual = SHA256.HashData(envelopeUtf8);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(actual, expected))
                throw new SecurityException(
                    "A autoridade de conteudo nao corresponde ao envelope aprovado.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expected);
            CryptographicOperations.ZeroMemory(actual);
        }
        return VerifyEnvelope(
            envelopeUtf8, issuerSpki, timeProvider ?? TimeProvider.System);
    }

    public static SuiteContentAuthorityConfiguration VerifyEnvelope(
        ReadOnlySpan<byte> envelopeUtf8,
        ReadOnlySpan<byte> issuerSpki,
        TimeProvider? timeProvider = null)
    {
        if (envelopeUtf8.Length is < 64 or > 32 * 1024)
            throw new SecurityException(
                "A configuracao da autoridade de conteudo possui tamanho invalido.");
        ValidateRsaSpki(issuerSpki, "A chave emissora de conteudo");
        var expectedIssuerKeyId = KeyIdFromSpki(issuerSpki);
        var envelope = ParseStrict<SuiteContentAuthorityEnvelope>(envelopeUtf8);
        if (envelope.SchemaVersion != SuiteOnlineLicenseProtocol.SchemaVersion
            || !string.Equals(envelope.Algorithm, SignatureAlgorithm,
                StringComparison.Ordinal)
            || !string.Equals(RequireCanonicalHex(envelope.KeyId, "KeyId"),
                expectedIssuerKeyId, StringComparison.Ordinal))
            throw new SecurityException(
                "O envelope da autoridade de conteudo e invalido.");

        var payloadBytes = DecodeCanonicalBase64(
            envelope.Payload, "payload", 64, 16 * 1024);
        var signature = DecodeCanonicalBase64(
            envelope.Signature, "signature", 256, 512);
        byte[] message = [];
        try
        {
            var payload = ParseStrict<SuiteContentAuthorityPayload>(payloadBytes);
            var canonical = CanonicalPayload(payload);
            try
            {
                if (!canonical.AsSpan().SequenceEqual(payloadBytes))
                    throw new SecurityException(
                        "O payload da autoridade de conteudo nao e canonico.");
            }
            finally { CryptographicOperations.ZeroMemory(canonical); }

            if (SuiteOnlineLicenseProtocol.FixedHexEquals(
                    payload.ContentAssertionKeyId, expectedIssuerKeyId))
                throw new SecurityException(
                    "A chave emissora nao pode assinar assertions de conteudo.");
            message = BuildSigningMessage(payload);
            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(issuerSpki, out var consumed);
            if (consumed != issuerSpki.Length
                || !rsa.VerifyData(message, signature,
                    HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
                throw new SecurityException(
                    "A assinatura da autoridade de conteudo e invalida.");
            return Materialize(payload, expectedIssuerKeyId,
                timeProvider ?? TimeProvider.System);
        }
        catch (CryptographicException exception)
        {
            throw new SecurityException(
                "Nao foi possivel validar a autoridade de conteudo.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payloadBytes);
            CryptographicOperations.ZeroMemory(signature);
            if (message.Length != 0) CryptographicOperations.ZeroMemory(message);
        }
    }

    private static SuiteContentAuthorityConfiguration Materialize(
        SuiteContentAuthorityPayload payload,
        string issuerKeyId,
        TimeProvider timeProvider)
    {
        ValidatePayloadShape(payload);
        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var validity = payload.ExpiresAtUnixSeconds
            - payload.IssuedAtUnixSeconds;
        if (payload.IssuedAtUnixSeconds > now + 300
            || payload.ExpiresAtUnixSeconds <= now
            || validity > 366L * 24 * 60 * 60)
            throw new SecurityException(
                "A autoridade de conteudo esta fora da validade.");
        var contentSpki = DecodeCanonicalBase64(
            payload.ContentAssertionPublicKeySpki,
            "chave publica de assertions de conteudo", 256, 4096);
        try
        {
            var pins = payload.TlsServerSpkiSha256Next.Length == 0
                ? new[] { payload.TlsServerSpkiSha256Current }
                : new[]
                {
                    payload.TlsServerSpkiSha256Current,
                    payload.TlsServerSpkiSha256Next
                };
            return new SuiteContentAuthorityConfiguration(
                new Uri(payload.BaseUrl, UriKind.Absolute),
                issuerKeyId,
                payload.ContentAssertionKeyId,
                contentSpki,
                pins,
                DateTimeOffset.FromUnixTimeSeconds(payload.IssuedAtUnixSeconds),
                DateTimeOffset.FromUnixTimeSeconds(payload.ExpiresAtUnixSeconds));
        }
        finally { CryptographicOperations.ZeroMemory(contentSpki); }
    }

    private static void ValidatePayloadShape(
        SuiteContentAuthorityPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.SchemaVersion != SuiteOnlineLicenseProtocol.SchemaVersion
            || !string.Equals(payload.Kind, ConfigurationKind,
                StringComparison.Ordinal)
            || !string.Equals(payload.ProductId,
                SuiteOnlineLicenseProtocol.ProductId, StringComparison.Ordinal)
            || !string.Equals(payload.ContentAssertionAlgorithm,
                SignatureAlgorithm, StringComparison.Ordinal))
            throw new SecurityException(
                "A configuracao de conteudo possui tipo invalido.");
        RequireCanonicalHex(payload.ContentAssertionKeyId,
            "ContentAssertionKeyId");
        RequireCanonicalHex(payload.TlsServerSpkiSha256Current,
            "TlsServerSpkiSha256Current");
        if (payload.TlsServerSpkiSha256Next.Length != 0)
        {
            RequireCanonicalHex(payload.TlsServerSpkiSha256Next,
                "TlsServerSpkiSha256Next");
            if (SuiteOnlineLicenseProtocol.FixedHexEquals(
                    payload.TlsServerSpkiSha256Current,
                    payload.TlsServerSpkiSha256Next))
                throw new SecurityException(
                    "Os pins TLS de conteudo nao podem ser repetidos.");
        }

        var contentSpki = DecodeCanonicalBase64(
            payload.ContentAssertionPublicKeySpki,
            "chave publica de assertions de conteudo", 256, 4096);
        try
        {
            ValidateRsaSpki(contentSpki, "A chave de assertions de conteudo");
            if (!SuiteOnlineLicenseProtocol.FixedHexEquals(
                    payload.ContentAssertionKeyId,
                    KeyIdFromSpki(contentSpki)))
                throw new SecurityException(
                    "O KeyId de conteudo nao corresponde ao SPKI.");
        }
        finally { CryptographicOperations.ZeroMemory(contentSpki); }

        if (!Uri.TryCreate(payload.BaseUrl, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort
            || string.IsNullOrWhiteSpace(uri.Host)
            || uri.UserInfo.Length != 0
            || uri.Query.Length != 0
            || uri.Fragment.Length != 0
            || !string.Equals(uri.AbsolutePath, "/", StringComparison.Ordinal)
            || !string.Equals(uri.AbsoluteUri, payload.BaseUrl,
                StringComparison.Ordinal))
            throw new SecurityException(
                "A URL da autoridade de conteudo nao e canonica.");
        SuiteOnlineLicenseProtocol.ValidateUnixTimeSeconds(
            payload.IssuedAtUnixSeconds, "A emissao da autoridade de conteudo");
        SuiteOnlineLicenseProtocol.ValidateUnixTimeSeconds(
            payload.ExpiresAtUnixSeconds,
            "A expiracao da autoridade de conteudo");
        if (payload.ExpiresAtUnixSeconds <= payload.IssuedAtUnixSeconds)
            throw new SecurityException(
                "A validade da autoridade de conteudo e invalida.");
    }

    private static string RequireCanonicalHex(string? value, string label)
    {
        var canonical = SuiteOnlineLicenseProtocol.RequireHex(value, label, 64);
        if (!string.Equals(canonical, value, StringComparison.Ordinal))
            throw new SecurityException($"{label} nao esta canonico.");
        return canonical;
    }

    private static void ValidateRsaSpki(ReadOnlySpan<byte> spki, string label)
    {
        if (spki.Length is < 256 or > 4096)
            throw new SecurityException($"{label} possui tamanho invalido.");
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(spki, out var consumed);
            if (consumed != spki.Length || rsa.KeySize is < 2048 or > 4096)
                throw new SecurityException($"{label} e invalida.");
            var canonical = rsa.ExportSubjectPublicKeyInfo();
            try
            {
                if (!canonical.AsSpan().SequenceEqual(spki))
                    throw new SecurityException($"{label} nao usa DER canonico.");
            }
            finally { CryptographicOperations.ZeroMemory(canonical); }
        }
        catch (CryptographicException exception)
        {
            throw new SecurityException($"{label} e invalida.", exception);
        }
    }

    private static T ParseStrict<T>(ReadOnlySpan<byte> utf8)
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
            RejectDuplicates(document.RootElement);
            return JsonSerializer.Deserialize<T>(copy, StrictJsonOptions)
                ?? throw new SecurityException(
                    "A autoridade de conteudo retornou JSON vazio.");
        }
        catch (JsonException exception)
        {
            throw new SecurityException(
                "A autoridade de conteudo possui JSON invalido.", exception);
        }
        finally { CryptographicOperations.ZeroMemory(copy); }
    }

    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new SecurityException(
                        "A autoridade de conteudo contem campo duplicado.");
                RejectDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                RejectDuplicates(item);
        }
    }

    private static byte[] DecodeCanonicalBase64(string? value, string label,
        int minimumBytes, int maximumBytes)
    {
        var encoded = value ?? string.Empty;
        if (encoded.Any(char.IsWhiteSpace))
            throw new SecurityException($"O {label} de conteudo e invalido.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(encoded); }
        catch (FormatException exception)
        {
            throw new SecurityException(
                $"O {label} de conteudo e invalido.", exception);
        }
        if (bytes.Length < minimumBytes || bytes.Length > maximumBytes
            || !string.Equals(Convert.ToBase64String(bytes), encoded,
                StringComparison.Ordinal))
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new SecurityException($"O {label} de conteudo e invalido.");
        }
        return bytes;
    }
}

internal sealed record SuiteContentAuthorityLoadResult(
    SuiteContentAuthorityConfiguration? Configuration,
    string FailureCode);

internal static class SuiteEmbeddedContentAuthorityLoader
{
    internal const string MissingConfiguration =
        "CONTENT_AUTHORITY_CONFIGURATION_MISSING";
    internal const string InvalidConfiguration =
        "CONTENT_AUTHORITY_CONFIGURATION_INVALID";

    internal static SuiteContentAuthorityLoadResult Load(
        Assembly assembly,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(timeProvider);
        try
        {
            var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .ToArray();
            var envelope = SingleMetadata(metadata,
                SuiteContentAuthorityConfigurationVerifier.ConfigurationMetadataKey);
            var hash = SingleMetadata(metadata,
                SuiteContentAuthorityConfigurationVerifier.ConfigurationSha256MetadataKey);
            var issuer = SingleMetadata(metadata,
                SuiteContentAuthorityConfigurationVerifier.IssuerSpkiMetadataKey);
            if (envelope is null && hash is null && issuer is null)
                return new SuiteContentAuthorityLoadResult(
                    null, MissingConfiguration);
            if (envelope is null || hash is null || issuer is null)
                return new SuiteContentAuthorityLoadResult(
                    null, InvalidConfiguration);
            var envelopeBytes = DecodeMetadata(envelope, 64, 32 * 1024);
            var issuerBytes = DecodeMetadata(issuer, 256, 4096);
            try
            {
                return new SuiteContentAuthorityLoadResult(
                    SuiteContentAuthorityConfigurationVerifier.VerifyPinnedEnvelope(
                        envelopeBytes, issuerBytes, hash, timeProvider),
                    string.Empty);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(envelopeBytes);
                CryptographicOperations.ZeroMemory(issuerBytes);
            }
        }
        catch (Exception exception) when (exception is SecurityException
            or FormatException or CryptographicException or ArgumentException)
        {
            return new SuiteContentAuthorityLoadResult(
                null, InvalidConfiguration);
        }
    }

    private static string? SingleMetadata(
        IEnumerable<AssemblyMetadataAttribute> metadata,
        string key)
    {
        var values = metadata.Where(item => string.Equals(
                item.Key, key, StringComparison.Ordinal))
            .Select(item => item.Value).ToArray();
        if (values.Length == 0) return null;
        if (values.Length != 1 || string.IsNullOrEmpty(values[0]))
            throw new SecurityException("Metadata de conteudo duplicada.");
        return values[0];
    }

    private static byte[] DecodeMetadata(string value, int minimum, int maximum)
    {
        if (value.Any(char.IsWhiteSpace)) throw new FormatException();
        var bytes = Convert.FromBase64String(value);
        if (bytes.Length < minimum || bytes.Length > maximum
            || !string.Equals(Convert.ToBase64String(bytes), value,
                StringComparison.Ordinal))
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new FormatException();
        }
        return bytes;
    }
}
