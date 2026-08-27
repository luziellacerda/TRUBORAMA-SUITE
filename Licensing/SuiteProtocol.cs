using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TurboBoxManager.Licensing;

public enum SuiteProtectionProfile
{
    TpmBound,
    SoftwareBoundOnline
}

public static class SuiteProtectionProfileCodec
{
    public static string Format(SuiteProtectionProfile profile) => profile switch
    {
        SuiteProtectionProfile.TpmBound => "TPM_BOUND",
        SuiteProtectionProfile.SoftwareBoundOnline => "SOFTWARE_BOUND_ONLINE",
        _ => throw new ArgumentOutOfRangeException(nameof(profile))
    };

    public static SuiteProtectionProfile Parse(string? value) => value switch
    {
        "TPM_BOUND" => SuiteProtectionProfile.TpmBound,
        "SOFTWARE_BOUND_ONLINE" => SuiteProtectionProfile.SoftwareBoundOnline,
        _ => throw new SecurityException("O perfil de protecao da licenca e invalido.")
    };
}

public sealed record SuiteDeviceDescriptor(
    int SchemaVersion,
    string DeviceId,
    string BindingType,
    string Algorithm,
    string PublicKeySpki,
    string HardwareFingerprint,
    string AgentVersion);

public sealed record SuiteActivationChallengeRequest(
    int SchemaVersion,
    string ProductId,
    string LicenseId,
    string ActivationCode,
    SuiteDeviceDescriptor Device);

public sealed record SuiteChallengeRequest(
    int SchemaVersion,
    string ProductId,
    string LicenseId,
    string DeviceId,
    string SessionId,
    string Action,
    string ContextHash);

public sealed record SuiteChallengeResponse(
    int SchemaVersion,
    string ChallengeId,
    string Nonce,
    long ExpiresAtUnixSeconds);

public sealed record SuiteActivationProof(
    int SchemaVersion,
    string ProductId,
    string LicenseId,
    string ChallengeId,
    SuiteDeviceDescriptor Device,
    string Signature);

public sealed record SuiteOperationProof(
    int SchemaVersion,
    string ProductId,
    string LicenseId,
    string DeviceId,
    string SessionId,
    string Action,
    string ContextHash,
    string ChallengeId,
    string Signature);

public sealed record SuiteActivationResult(
    int SchemaVersion,
    string Status,
    string DeviceId,
    string BindingType);

public sealed record SuiteSessionContext(
    int SchemaVersion,
    string ProductId,
    string LicenseId,
    string DeviceId,
    string SessionId,
    string Action,
    string HardwareFingerprint,
    string ClientVersion);

public sealed record SuiteSessionProof(
    SuiteOperationProof Proof,
    SuiteSessionContext Context);

public sealed record SuiteSessionResponse(
    int SchemaVersion,
    string ProductId,
    string LicenseId,
    string DeviceId,
    string SessionId,
    string Status,
    long ServerTimeUnixSeconds,
    long AuthorizedUntilUnixSeconds,
    int HeartbeatAfterSeconds);

public sealed record SuiteErrorResponse(int SchemaVersion, string Code, string Message);

public sealed record SuiteSignedAssertionEnvelope(
    int SchemaVersion,
    string Kind,
    string Algorithm,
    string KeyId,
    string Payload,
    string Signature);

public sealed record SuiteActivationChallengeAssertion(
    int SchemaVersion,
    string Kind,
    string ProductId,
    string LicenseId,
    string DeviceId,
    string Action,
    string ContextHash,
    string ChallengeId,
    string Nonce,
    string Status,
    long ServerTimeUnixSeconds,
    long ExpiresAtUnixSeconds);

public sealed record SuiteOperationChallengeAssertion(
    int SchemaVersion,
    string Kind,
    string ProductId,
    string LicenseId,
    string DeviceId,
    string SessionId,
    string Action,
    string ContextHash,
    string ChallengeId,
    string Nonce,
    string Status,
    long ServerTimeUnixSeconds,
    long ExpiresAtUnixSeconds);

public sealed record SuiteActivationResultAssertion(
    int SchemaVersion,
    string Kind,
    string ProductId,
    string LicenseId,
    string DeviceId,
    string Action,
    string ContextHash,
    string ChallengeId,
    string Status,
    string BindingType,
    long ServerTimeUnixSeconds);

public sealed record SuiteSessionAssertion(
    int SchemaVersion,
    string Kind,
    string ProductId,
    string LicenseId,
    string DeviceId,
    string SessionId,
    string Action,
    string ContextHash,
    string ChallengeId,
    string Status,
    long ServerTimeUnixSeconds,
    long AuthorizedUntilUnixSeconds,
    int HeartbeatAfterSeconds);

/// <summary>
/// Canonical, version-one machine proof protocol used by the Suite client.
/// The signing domain and signed envelope deliberately remain byte-for-byte
/// compatible with the existing v1 PIX protocol.
/// </summary>
public static class SuiteOnlineLicenseProtocol
{
    public const int SchemaVersion = 1;
    public const string ProductId = "TURBORAMA_SUITE";
    public const string SigningAlgorithm = "rsa-pss-sha256";
    public const int MaximumBodyBytes = 64 * 1024;
    internal const long MinimumUnixTimeSeconds = 1;
    internal const long MaximumUnixTimeSeconds = 253_402_300_799;

    // Suite routes are additive and namespaced so the legacy v1 endpoints and
    // DTOs remain byte-for-byte untouched. The cryptographic proof domain is
    // still the existing v1 domain; this is product isolation, not a parallel
    // protocol.
    public const string ActivationChallengeRoute = "v1/suite/activations/challenge";
    public const string ActivationCompleteRoute = "v1/suite/activations/complete";
    public const string ChallengeRoute = "v1/suite/challenges";
    public const string SuiteSessionRoute = "v1/suite/sessions";

    public const string ActivationChallengeAssertionKind =
        "TURBORAMA_SUITE_ACTIVATION_CHALLENGE";
    public const string ActivationResultAssertionKind =
        "TURBORAMA_SUITE_ACTIVATION_RESULT";
    public const string SessionOpenChallengeAssertionKind =
        "TURBORAMA_SUITE_SESSION_OPEN_CHALLENGE";
    public const string SessionHeartbeatChallengeAssertionKind =
        "TURBORAMA_SUITE_SESSION_HEARTBEAT_CHALLENGE";
    public const string SessionOpenAssertionKind = "TURBORAMA_SUITE_SESSION_OPEN";
    public const string SessionHeartbeatAssertionKind =
        "TURBORAMA_SUITE_SESSION_HEARTBEAT";

    private static readonly byte[] SigningDomain =
        Encoding.ASCII.GetBytes("TurboRamaOnlineMachineProof/v1\0");

    private static readonly byte[] ActivationChallengeAssertionDomain =
        Encoding.ASCII.GetBytes("TurboRamaSuiteOnlineAssertion/activation-challenge/v1\0");
    private static readonly byte[] ActivationResultAssertionDomain =
        Encoding.ASCII.GetBytes("TurboRamaSuiteOnlineAssertion/activation-result/v1\0");
    private static readonly byte[] SessionOpenChallengeAssertionDomain =
        Encoding.ASCII.GetBytes("TurboRamaSuiteOnlineAssertion/session-open-challenge/v1\0");
    private static readonly byte[] SessionHeartbeatChallengeAssertionDomain =
        Encoding.ASCII.GetBytes("TurboRamaSuiteOnlineAssertion/session-heartbeat-challenge/v1\0");
    private static readonly byte[] SessionOpenAssertionDomain =
        Encoding.ASCII.GetBytes("TurboRamaSuiteOnlineAssertion/session-open/v1\0");
    private static readonly byte[] SessionHeartbeatAssertionDomain =
        Encoding.ASCII.GetBytes("TurboRamaSuiteOnlineAssertion/session-heartbeat/v1\0");

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.Default,
        Indented = false,
        SkipValidation = false
    };

    private static readonly JsonSerializerOptions WireJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        MaxDepth = 16,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict,
        Encoder = JavaScriptEncoder.Default,
        WriteIndented = false
    };

    private static readonly HashSet<string> AllowedActions = new(StringComparer.Ordinal)
    {
        "device.activate",
        "session.open",
        "session.heartbeat",
        "configuration.read",
        "catalog.read",
        "offline-lease.issue",
        "download.authorize",
        "download.content",
        "update.read"
    };

    public static byte[] CanonicalActivationContext(string licenseId, SuiteDeviceDescriptor device)
        => CanonicalBytes(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", SchemaVersion);
            writer.WriteString("productId", ProductId);
            writer.WriteString("licenseId", RequireIdentifier(licenseId, "LicenseId", 6, 64));
            writer.WriteString("action", "device.activate");
            writer.WritePropertyName("device");
            WriteDevice(writer, device);
            writer.WriteEndObject();
        });

    public static string ActivationContextHash(string licenseId, SuiteDeviceDescriptor device)
    {
        var bytes = CanonicalActivationContext(licenseId, device);
        try { return LowerHex(SHA256.HashData(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public static byte[] CanonicalSessionContext(SuiteSessionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ValidateSessionContext(context);
        return CanonicalBytes(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", context.SchemaVersion);
            writer.WriteString("productId", context.ProductId);
            writer.WriteString("licenseId", context.LicenseId);
            writer.WriteString("deviceId", context.DeviceId);
            writer.WriteString("sessionId", context.SessionId);
            writer.WriteString("action", context.Action);
            writer.WriteString("hardwareFingerprint", context.HardwareFingerprint);
            writer.WriteString("clientVersion", context.ClientVersion);
            writer.WriteEndObject();
        });
    }

    public static string ContextHash(SuiteSessionContext context)
    {
        var bytes = CanonicalSessionContext(context);
        try { return LowerHex(SHA256.HashData(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public static byte[] BuildSigningMessage(SuiteChallengeResponse challenge, string licenseId,
        string deviceId, string sessionId, string action, string contextHash)
    {
        ValidateChallenge(challenge);
        var canonical = CanonicalBytes(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", SchemaVersion);
            writer.WriteString("challengeId", RequireHex(challenge.ChallengeId, "ChallengeId", 64));
            writer.WriteString("nonce", RequireBase64(challenge.Nonce, "nonce", 32, 64));
            writer.WriteNumber("expiresAtUnixSeconds", challenge.ExpiresAtUnixSeconds);
            writer.WriteString("licenseId", RequireIdentifier(licenseId, "LicenseId", 6, 64));
            writer.WriteString("deviceId", RequireHex(deviceId, "DeviceId", 64));
            writer.WriteString("sessionId", RequireHex(sessionId, "SessionId", 64, allowEmpty: true));
            writer.WriteString("action", RequireAction(action));
            writer.WriteString("contextHash", RequireHex(contextHash, "ContextHash", 64));
            writer.WriteEndObject();
        });

        var output = new byte[SigningDomain.Length + canonical.Length];
        SigningDomain.CopyTo(output, 0);
        canonical.CopyTo(output, SigningDomain.Length);
        CryptographicOperations.ZeroMemory(canonical);
        return output;
    }

    public static byte[] CanonicalActivationChallengeAssertion(
        SuiteActivationChallengeAssertion assertion)
    {
        ArgumentNullException.ThrowIfNull(assertion);
        ValidateActivationChallengeAssertion(assertion);
        return CanonicalBytes(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", assertion.SchemaVersion);
            writer.WriteString("kind", assertion.Kind);
            writer.WriteString("productId", assertion.ProductId);
            writer.WriteString("licenseId", assertion.LicenseId);
            writer.WriteString("deviceId", assertion.DeviceId);
            writer.WriteString("action", assertion.Action);
            writer.WriteString("contextHash", assertion.ContextHash);
            writer.WriteString("challengeId", assertion.ChallengeId);
            writer.WriteString("nonce", assertion.Nonce);
            writer.WriteString("status", assertion.Status);
            writer.WriteNumber("serverTimeUnixSeconds", assertion.ServerTimeUnixSeconds);
            writer.WriteNumber("expiresAtUnixSeconds", assertion.ExpiresAtUnixSeconds);
            writer.WriteEndObject();
        });
    }

    public static byte[] CanonicalOperationChallengeAssertion(
        SuiteOperationChallengeAssertion assertion)
    {
        ArgumentNullException.ThrowIfNull(assertion);
        ValidateOperationChallengeAssertion(assertion);
        return CanonicalBytes(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", assertion.SchemaVersion);
            writer.WriteString("kind", assertion.Kind);
            writer.WriteString("productId", assertion.ProductId);
            writer.WriteString("licenseId", assertion.LicenseId);
            writer.WriteString("deviceId", assertion.DeviceId);
            writer.WriteString("sessionId", assertion.SessionId);
            writer.WriteString("action", assertion.Action);
            writer.WriteString("contextHash", assertion.ContextHash);
            writer.WriteString("challengeId", assertion.ChallengeId);
            writer.WriteString("nonce", assertion.Nonce);
            writer.WriteString("status", assertion.Status);
            writer.WriteNumber("serverTimeUnixSeconds", assertion.ServerTimeUnixSeconds);
            writer.WriteNumber("expiresAtUnixSeconds", assertion.ExpiresAtUnixSeconds);
            writer.WriteEndObject();
        });
    }

    public static byte[] CanonicalActivationResultAssertion(
        SuiteActivationResultAssertion assertion)
    {
        ArgumentNullException.ThrowIfNull(assertion);
        ValidateActivationResultAssertion(assertion);
        return CanonicalBytes(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", assertion.SchemaVersion);
            writer.WriteString("kind", assertion.Kind);
            writer.WriteString("productId", assertion.ProductId);
            writer.WriteString("licenseId", assertion.LicenseId);
            writer.WriteString("deviceId", assertion.DeviceId);
            writer.WriteString("action", assertion.Action);
            writer.WriteString("contextHash", assertion.ContextHash);
            writer.WriteString("challengeId", assertion.ChallengeId);
            writer.WriteString("status", assertion.Status);
            writer.WriteString("bindingType", assertion.BindingType);
            writer.WriteNumber("serverTimeUnixSeconds", assertion.ServerTimeUnixSeconds);
            writer.WriteEndObject();
        });
    }

    public static byte[] CanonicalSessionAssertion(SuiteSessionAssertion assertion)
    {
        ArgumentNullException.ThrowIfNull(assertion);
        ValidateSessionAssertion(assertion);
        return CanonicalBytes(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", assertion.SchemaVersion);
            writer.WriteString("kind", assertion.Kind);
            writer.WriteString("productId", assertion.ProductId);
            writer.WriteString("licenseId", assertion.LicenseId);
            writer.WriteString("deviceId", assertion.DeviceId);
            writer.WriteString("sessionId", assertion.SessionId);
            writer.WriteString("action", assertion.Action);
            writer.WriteString("contextHash", assertion.ContextHash);
            writer.WriteString("challengeId", assertion.ChallengeId);
            writer.WriteString("status", assertion.Status);
            writer.WriteNumber("serverTimeUnixSeconds", assertion.ServerTimeUnixSeconds);
            writer.WriteNumber("authorizedUntilUnixSeconds",
                assertion.AuthorizedUntilUnixSeconds);
            writer.WriteNumber("heartbeatAfterSeconds", assertion.HeartbeatAfterSeconds);
            writer.WriteEndObject();
        });
    }

    public static byte[] BuildActivationChallengeAssertionSigningMessage(
        SuiteActivationChallengeAssertion assertion)
        => BuildAssertionSigningMessage(assertion,
            CanonicalActivationChallengeAssertion, ActivationChallengeAssertionDomain);

    public static byte[] BuildOperationChallengeAssertionSigningMessage(
        SuiteOperationChallengeAssertion assertion)
        => BuildAssertionSigningMessage(assertion,
            CanonicalOperationChallengeAssertion, AssertionDomainForOperationChallenge(
                assertion.Action));

    public static byte[] BuildActivationResultAssertionSigningMessage(
        SuiteActivationResultAssertion assertion)
        => BuildAssertionSigningMessage(assertion,
            CanonicalActivationResultAssertion, ActivationResultAssertionDomain);

    public static byte[] BuildSessionAssertionSigningMessage(
        SuiteSessionAssertion assertion)
        => BuildAssertionSigningMessage(assertion,
            CanonicalSessionAssertion, AssertionDomainForSession(assertion.Action));

    public static byte[] SerializeSignedAssertionEnvelope(
        SuiteSignedAssertionEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return JsonSerializer.SerializeToUtf8Bytes(envelope, WireJsonOptions);
    }

    public static string DeviceIdFromSpki(ReadOnlySpan<byte> spki)
    {
        if (spki.Length is < 256 or > 4096)
            throw new SecurityException("A chave publica da maquina possui tamanho invalido.");
        return LowerHex(SHA256.HashData(spki));
    }

    public static byte[] ParseAndValidateSpki(SuiteDeviceDescriptor descriptor)
    {
        ValidateDescriptorShape(descriptor);
        byte[] spki;
        try { spki = Convert.FromBase64String(descriptor.PublicKeySpki); }
        catch (FormatException ex)
        {
            throw new SecurityException("A chave publica da maquina e invalida.", ex);
        }

        try
        {
            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(spki, out var consumed);
            if (consumed != spki.Length || rsa.KeySize is < 2048 or > 4096)
                throw new SecurityException("A chave publica da maquina nao e RSA compativel.");

            var canonical = rsa.ExportSubjectPublicKeyInfo();
            try
            {
                if (!canonical.AsSpan().SequenceEqual(spki))
                    throw new SecurityException("A chave publica da maquina nao usa SPKI DER canonico.");
            }
            finally { CryptographicOperations.ZeroMemory(canonical); }

            if (!FixedHexEquals(DeviceIdFromSpki(spki), descriptor.DeviceId))
                throw new SecurityException("O DeviceId nao corresponde a chave publica informada.");
            return spki;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(spki);
            throw;
        }
    }

    public static void ValidateDescriptorShape(SuiteDeviceDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.SchemaVersion != SchemaVersion)
            throw new SecurityException("A versao da identidade da maquina e invalida.");
        RequireHex(descriptor.DeviceId, "DeviceId", 64);
        _ = SuiteProtectionProfileCodec.Parse(descriptor.BindingType);
        if (!string.Equals(descriptor.Algorithm, SigningAlgorithm, StringComparison.Ordinal))
            throw new SecurityException("O algoritmo da identidade da maquina e invalido.");
        RequireHex(descriptor.HardwareFingerprint, "HardwareFingerprint", 64);
        RequireVersion(descriptor.AgentVersion, "AgentVersion");
        var publicKey = descriptor.PublicKeySpki ?? "";
        if (publicKey.Length is < 300 or > 8192 || publicKey.Any(char.IsWhiteSpace))
            throw new SecurityException("A chave publica da maquina e invalida.");
    }

    public static void ValidateChallenge(SuiteChallengeResponse challenge)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        if (challenge.SchemaVersion != SchemaVersion)
            throw new SecurityException("A versao do desafio on-line e invalida.");
        RequireHex(challenge.ChallengeId, "ChallengeId", 64);
        RequireBase64(challenge.Nonce, "nonce", 32, 64);
        ValidateUnixTimeSeconds(challenge.ExpiresAtUnixSeconds,
            "A expiracao do desafio on-line");
    }

    public static void ValidateSessionResponse(SuiteSessionResponse response, string licenseId,
        string deviceId, string sessionId)
    {
        ArgumentNullException.ThrowIfNull(response);
        ValidateUnixTimeSeconds(response.ServerTimeUnixSeconds,
            "O horario do servidor");
        ValidateUnixTimeSeconds(response.AuthorizedUntilUnixSeconds,
            "A expiracao da sessao");
        var lifetimeSeconds = response.AuthorizedUntilUnixSeconds
            - response.ServerTimeUnixSeconds;
        if (response.SchemaVersion != SchemaVersion
            || !string.Equals(response.ProductId, ProductId, StringComparison.Ordinal)
            || !string.Equals(response.LicenseId,
                RequireIdentifier(licenseId, "LicenseId", 6, 64), StringComparison.Ordinal)
            || !FixedHexEquals(response.DeviceId, deviceId)
            || !FixedHexEquals(response.SessionId, sessionId)
            || !string.Equals(response.Status, "ACTIVE", StringComparison.Ordinal)
            || lifetimeSeconds < 20
            || lifetimeSeconds > 86_400
            || response.HeartbeatAfterSeconds is < 5 or > 3600
            || response.HeartbeatAfterSeconds >= lifetimeSeconds)
            throw new SecurityException("O servidor nao confirmou uma sessao valida para este produto.");
    }

    internal static void ValidateUnixTimeSeconds(long value, string label)
    {
        if (value is < MinimumUnixTimeSeconds or > MaximumUnixTimeSeconds)
            throw new SecurityException($"{label} possui epoca Unix invalida.");
    }

    public static SuiteChallengeResponse ParseActivationChallengeAssertion(
        ReadOnlySpan<byte> utf8, ReadOnlySpan<byte> onlineAssertionSpki,
        string onlineAssertionKeyId, string licenseId, string deviceId,
        string contextHash, long nowUnixSeconds)
    {
        var assertion = ParseSignedAssertion<SuiteActivationChallengeAssertion>(
            utf8, onlineAssertionSpki, onlineAssertionKeyId,
            ActivationChallengeAssertionKind, ActivationChallengeAssertionDomain,
            CanonicalActivationChallengeAssertion);
        ValidateFreshChallenge(assertion.ServerTimeUnixSeconds,
            assertion.ExpiresAtUnixSeconds, nowUnixSeconds);
        if (!string.Equals(assertion.ProductId, ProductId, StringComparison.Ordinal)
            || !string.Equals(assertion.LicenseId,
                RequireCanonicalIdentifier(licenseId, "LicenseId", 6, 64),
                StringComparison.Ordinal)
            || !FixedHexEquals(assertion.DeviceId, deviceId)
            || !string.Equals(assertion.Action, "device.activate", StringComparison.Ordinal)
            || !FixedHexEquals(assertion.ContextHash, contextHash))
            throw new SecurityException(
                "O desafio assinado nao pertence a esta ativacao.");
        return new SuiteChallengeResponse(
            assertion.SchemaVersion,
            assertion.ChallengeId,
            assertion.Nonce,
            assertion.ExpiresAtUnixSeconds);
    }

    public static SuiteChallengeResponse ParseOperationChallengeAssertion(
        ReadOnlySpan<byte> utf8, ReadOnlySpan<byte> onlineAssertionSpki,
        string onlineAssertionKeyId, string licenseId, string deviceId,
        string sessionId, string action, string contextHash, long nowUnixSeconds)
    {
        var expectedKind = ChallengeKindForAction(action);
        var assertion = ParseSignedAssertion<SuiteOperationChallengeAssertion>(
            utf8, onlineAssertionSpki, onlineAssertionKeyId,
            expectedKind, AssertionDomainForOperationChallenge(action),
            CanonicalOperationChallengeAssertion);
        ValidateFreshChallenge(assertion.ServerTimeUnixSeconds,
            assertion.ExpiresAtUnixSeconds, nowUnixSeconds);
        if (!string.Equals(assertion.ProductId, ProductId, StringComparison.Ordinal)
            || !string.Equals(assertion.LicenseId,
                RequireCanonicalIdentifier(licenseId, "LicenseId", 6, 64),
                StringComparison.Ordinal)
            || !FixedHexEquals(assertion.DeviceId, deviceId)
            || !FixedHexEquals(assertion.SessionId, sessionId)
            || !string.Equals(assertion.Action, action, StringComparison.Ordinal)
            || !FixedHexEquals(assertion.ContextHash, contextHash))
            throw new SecurityException(
                "O desafio assinado nao pertence a esta operacao.");
        return new SuiteChallengeResponse(
            assertion.SchemaVersion,
            assertion.ChallengeId,
            assertion.Nonce,
            assertion.ExpiresAtUnixSeconds);
    }

    public static SuiteActivationResult ParseActivationResultAssertion(
        ReadOnlySpan<byte> utf8, ReadOnlySpan<byte> onlineAssertionSpki,
        string onlineAssertionKeyId, string licenseId, string deviceId,
        string bindingType, string contextHash, string challengeId,
        long nowUnixSeconds)
    {
        var assertion = ParseSignedAssertion<SuiteActivationResultAssertion>(
            utf8, onlineAssertionSpki, onlineAssertionKeyId,
            ActivationResultAssertionKind, ActivationResultAssertionDomain,
            CanonicalActivationResultAssertion);
        ValidateFreshServerTime(assertion.ServerTimeUnixSeconds, nowUnixSeconds);
        if (!string.Equals(assertion.ProductId, ProductId, StringComparison.Ordinal)
            || !string.Equals(assertion.LicenseId,
                RequireCanonicalIdentifier(licenseId, "LicenseId", 6, 64),
                StringComparison.Ordinal)
            || !FixedHexEquals(assertion.DeviceId, deviceId)
            || !string.Equals(assertion.Action, "device.activate", StringComparison.Ordinal)
            || !FixedHexEquals(assertion.ContextHash, contextHash)
            || !FixedHexEquals(assertion.ChallengeId, challengeId)
            || !string.Equals(assertion.BindingType, bindingType,
                StringComparison.Ordinal))
            throw new SecurityException(
                "O resultado assinado nao pertence a esta ativacao.");
        return new SuiteActivationResult(
            assertion.SchemaVersion,
            assertion.Status,
            assertion.DeviceId,
            assertion.BindingType);
    }

    public static SuiteSessionResponse ParseSessionAssertion(
        ReadOnlySpan<byte> utf8, ReadOnlySpan<byte> onlineAssertionSpki,
        string onlineAssertionKeyId, string licenseId, string deviceId,
        string sessionId, string action, string contextHash, string challengeId,
        long nowUnixSeconds)
    {
        var assertion = ParseSignedAssertion<SuiteSessionAssertion>(
            utf8, onlineAssertionSpki, onlineAssertionKeyId,
            AssertionKindForSession(action), AssertionDomainForSession(action),
            CanonicalSessionAssertion);
        ValidateFreshServerTime(assertion.ServerTimeUnixSeconds, nowUnixSeconds);
        if (!string.Equals(assertion.ProductId, ProductId, StringComparison.Ordinal)
            || !string.Equals(assertion.LicenseId,
                RequireCanonicalIdentifier(licenseId, "LicenseId", 6, 64),
                StringComparison.Ordinal)
            || !FixedHexEquals(assertion.DeviceId, deviceId)
            || !FixedHexEquals(assertion.SessionId, sessionId)
            || !string.Equals(assertion.Action, action, StringComparison.Ordinal)
            || !FixedHexEquals(assertion.ContextHash, contextHash)
            || !FixedHexEquals(assertion.ChallengeId, challengeId)
            || assertion.AuthorizedUntilUnixSeconds <= nowUnixSeconds)
            throw new SecurityException(
                "A resposta assinada nao pertence a esta sessao.");

        var response = new SuiteSessionResponse(
            assertion.SchemaVersion,
            assertion.ProductId,
            assertion.LicenseId,
            assertion.DeviceId,
            assertion.SessionId,
            assertion.Status,
            assertion.ServerTimeUnixSeconds,
            assertion.AuthorizedUntilUnixSeconds,
            assertion.HeartbeatAfterSeconds);
        ValidateSessionResponse(response, licenseId, deviceId, sessionId);
        return response;
    }

    public static SuiteErrorResponse ParseErrorResponse(ReadOnlySpan<byte> utf8)
        => ParseStrict<SuiteErrorResponse>(utf8);

    public static string RequireIdentifier(string? value, string label, int minimum, int maximum)
    {
        var normalized = (value ?? "").Trim();
        if (normalized.Length < minimum || normalized.Length > maximum
            || normalized.Any(character => !(char.IsAsciiLetterOrDigit(character)
                || character is '-' or '_')))
            throw new SecurityException($"{label} possui formato invalido.");
        return normalized;
    }

    public static string RequireHex(string? value, string label, int length, bool allowEmpty = false)
    {
        var normalized = (value ?? "").Trim().ToLowerInvariant();
        if (allowEmpty && normalized.Length == 0) return "";
        if (normalized.Length != length || normalized.Any(character => character is not (>= '0' and <= '9')
                and not (>= 'a' and <= 'f')))
            throw new SecurityException($"{label} possui formato invalido.");
        return normalized;
    }

    public static bool FixedHexEquals(string? left, string? right)
    {
        try
        {
            var a = Encoding.ASCII.GetBytes(RequireHex(left, "hash", 64));
            var b = Encoding.ASCII.GetBytes(RequireHex(right, "hash", 64));
            try { return CryptographicOperations.FixedTimeEquals(a, b); }
            finally
            {
                CryptographicOperations.ZeroMemory(a);
                CryptographicOperations.ZeroMemory(b);
            }
        }
        catch (SecurityException) { return false; }
    }

    internal static byte[] SerializeRequest<T>(T value)
        => JsonSerializer.SerializeToUtf8Bytes(value, WireJsonOptions);

    private static TAssertion ParseSignedAssertion<TAssertion>(
        ReadOnlySpan<byte> utf8, ReadOnlySpan<byte> onlineAssertionSpki,
        string onlineAssertionKeyId, string expectedKind, ReadOnlySpan<byte> domain,
        Func<TAssertion, byte[]> canonicalPayload)
    {
        var envelope = ParseStrict<SuiteSignedAssertionEnvelope>(utf8);
        var canonicalKeyId = RequireHex(
            onlineAssertionKeyId, "OnlineAssertionKeyId", 64);
        if (!string.Equals(canonicalKeyId, onlineAssertionKeyId, StringComparison.Ordinal)
            || envelope.SchemaVersion != SchemaVersion
            || !string.Equals(envelope.Kind, expectedKind, StringComparison.Ordinal)
            || !string.Equals(envelope.Algorithm, SigningAlgorithm,
                StringComparison.Ordinal)
            || !string.Equals(envelope.KeyId,
                RequireHex(envelope.KeyId, "KeyId", 64), StringComparison.Ordinal)
            || !FixedHexEquals(envelope.KeyId, canonicalKeyId))
            throw new SecurityException("O envelope da assertion on-line e invalido.");

        var payloadBytes = DecodeCanonicalBase64(
            envelope.Payload, "payload da assertion", 64, MaximumBodyBytes);
        var signature = DecodeCanonicalBase64(
            envelope.Signature, "assinatura da assertion", 256, 512);
        byte[] canonical = Array.Empty<byte>();
        byte[] message = Array.Empty<byte>();
        try
        {
            var assertion = ParseStrict<TAssertion>(payloadBytes);
            canonical = canonicalPayload(assertion);
            if (!canonical.AsSpan().SequenceEqual(payloadBytes))
                throw new SecurityException(
                    "O payload da assertion on-line nao usa JSON canonico.");

            message = PrefixDomain(domain, canonical);
            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(onlineAssertionSpki, out var consumed);
            if (consumed != onlineAssertionSpki.Length
                || rsa.KeySize is < 2048 or > 4096)
                throw new SecurityException("A chave de assertions on-line e invalida.");
            var canonicalSpki = rsa.ExportSubjectPublicKeyInfo();
            try
            {
                if (!canonicalSpki.AsSpan().SequenceEqual(onlineAssertionSpki))
                    throw new SecurityException(
                        "A chave de assertions nao usa SPKI DER canonico.");
                var actualKeyId = LowerHex(SHA256.HashData(canonicalSpki));
                if (!FixedHexEquals(actualKeyId, canonicalKeyId)
                    || !rsa.VerifyData(message, signature, HashAlgorithmName.SHA256,
                        RSASignaturePadding.Pss))
                    throw new SecurityException(
                        "A assinatura da assertion on-line e invalida.");
            }
            finally { CryptographicOperations.ZeroMemory(canonicalSpki); }
            return assertion;
        }
        catch (CryptographicException ex)
        {
            throw new SecurityException(
                "Nao foi possivel validar a assertion on-line.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payloadBytes);
            CryptographicOperations.ZeroMemory(signature);
            if (canonical.Length != 0) CryptographicOperations.ZeroMemory(canonical);
            if (message.Length != 0) CryptographicOperations.ZeroMemory(message);
        }
    }

    private static byte[] BuildAssertionSigningMessage<TAssertion>(
        TAssertion assertion, Func<TAssertion, byte[]> canonicalPayload,
        ReadOnlySpan<byte> domain)
    {
        var canonical = canonicalPayload(assertion);
        try { return PrefixDomain(domain, canonical); }
        finally { CryptographicOperations.ZeroMemory(canonical); }
    }

    private static byte[] PrefixDomain(ReadOnlySpan<byte> domain,
        ReadOnlySpan<byte> canonical)
    {
        var output = new byte[checked(domain.Length + canonical.Length)];
        domain.CopyTo(output);
        canonical.CopyTo(output.AsSpan(domain.Length));
        return output;
    }

    private static void ValidateActivationChallengeAssertion(
        SuiteActivationChallengeAssertion assertion)
    {
        if (assertion.SchemaVersion != SchemaVersion
            || !string.Equals(assertion.Kind, ActivationChallengeAssertionKind,
                StringComparison.Ordinal)
            || !string.Equals(assertion.ProductId, ProductId, StringComparison.Ordinal)
            || !string.Equals(assertion.Action, "device.activate", StringComparison.Ordinal)
            || !string.Equals(assertion.Status, "ISSUED", StringComparison.Ordinal))
            throw new SecurityException("O desafio de ativacao possui tipo invalido.");
        _ = RequireCanonicalIdentifier(assertion.LicenseId, "LicenseId", 6, 64);
        _ = RequireCanonicalHex(assertion.DeviceId, "DeviceId", 64);
        _ = RequireCanonicalHex(assertion.ContextHash, "ContextHash", 64);
        _ = RequireCanonicalHex(assertion.ChallengeId, "ChallengeId", 64);
        _ = RequireBase64(assertion.Nonce, "nonce", 32, 64);
        ValidateChallengeWindow(assertion.ServerTimeUnixSeconds,
            assertion.ExpiresAtUnixSeconds);
    }

    private static void ValidateOperationChallengeAssertion(
        SuiteOperationChallengeAssertion assertion)
    {
        var expectedKind = ChallengeKindForAction(assertion.Action);
        if (assertion.SchemaVersion != SchemaVersion
            || !string.Equals(assertion.Kind, expectedKind, StringComparison.Ordinal)
            || !string.Equals(assertion.ProductId, ProductId, StringComparison.Ordinal)
            || !string.Equals(assertion.Status, "ISSUED", StringComparison.Ordinal))
            throw new SecurityException("O desafio de sessao possui tipo invalido.");
        _ = RequireCanonicalIdentifier(assertion.LicenseId, "LicenseId", 6, 64);
        _ = RequireCanonicalHex(assertion.DeviceId, "DeviceId", 64);
        _ = RequireCanonicalHex(assertion.SessionId, "SessionId", 64);
        _ = RequireCanonicalHex(assertion.ContextHash, "ContextHash", 64);
        _ = RequireCanonicalHex(assertion.ChallengeId, "ChallengeId", 64);
        _ = RequireBase64(assertion.Nonce, "nonce", 32, 64);
        ValidateChallengeWindow(assertion.ServerTimeUnixSeconds,
            assertion.ExpiresAtUnixSeconds);
    }

    private static void ValidateActivationResultAssertion(
        SuiteActivationResultAssertion assertion)
    {
        if (assertion.SchemaVersion != SchemaVersion
            || !string.Equals(assertion.Kind, ActivationResultAssertionKind,
                StringComparison.Ordinal)
            || !string.Equals(assertion.ProductId, ProductId, StringComparison.Ordinal)
            || !string.Equals(assertion.Action, "device.activate", StringComparison.Ordinal)
            || !string.Equals(assertion.Status, "ACTIVE", StringComparison.Ordinal))
            throw new SecurityException("O resultado de ativacao possui tipo invalido.");
        _ = RequireCanonicalIdentifier(assertion.LicenseId, "LicenseId", 6, 64);
        _ = RequireCanonicalHex(assertion.DeviceId, "DeviceId", 64);
        _ = RequireCanonicalHex(assertion.ContextHash, "ContextHash", 64);
        _ = RequireCanonicalHex(assertion.ChallengeId, "ChallengeId", 64);
        var binding = SuiteProtectionProfileCodec.Format(
            SuiteProtectionProfileCodec.Parse(assertion.BindingType));
        if (!string.Equals(binding, assertion.BindingType, StringComparison.Ordinal))
            throw new SecurityException("O binding da ativacao nao esta canonico.");
        ValidateUnixTimeSeconds(assertion.ServerTimeUnixSeconds,
            "O horario do resultado de ativacao");
    }

    private static void ValidateSessionAssertion(SuiteSessionAssertion assertion)
    {
        var expectedKind = AssertionKindForSession(assertion.Action);
        if (assertion.SchemaVersion != SchemaVersion
            || !string.Equals(assertion.Kind, expectedKind, StringComparison.Ordinal)
            || !string.Equals(assertion.ProductId, ProductId, StringComparison.Ordinal)
            || !string.Equals(assertion.Status, "ACTIVE", StringComparison.Ordinal))
            throw new SecurityException("A assertion da sessao possui tipo invalido.");
        _ = RequireCanonicalIdentifier(assertion.LicenseId, "LicenseId", 6, 64);
        _ = RequireCanonicalHex(assertion.DeviceId, "DeviceId", 64);
        _ = RequireCanonicalHex(assertion.SessionId, "SessionId", 64);
        _ = RequireCanonicalHex(assertion.ContextHash, "ContextHash", 64);
        _ = RequireCanonicalHex(assertion.ChallengeId, "ChallengeId", 64);
        var response = new SuiteSessionResponse(
            assertion.SchemaVersion,
            assertion.ProductId,
            assertion.LicenseId,
            assertion.DeviceId,
            assertion.SessionId,
            assertion.Status,
            assertion.ServerTimeUnixSeconds,
            assertion.AuthorizedUntilUnixSeconds,
            assertion.HeartbeatAfterSeconds);
        ValidateSessionResponse(response, assertion.LicenseId,
            assertion.DeviceId, assertion.SessionId);
    }

    private static void ValidateChallengeWindow(long serverTimeUnixSeconds,
        long expiresAtUnixSeconds)
    {
        ValidateUnixTimeSeconds(serverTimeUnixSeconds, "O horario do desafio");
        ValidateUnixTimeSeconds(expiresAtUnixSeconds, "A expiracao do desafio");
        var lifetime = expiresAtUnixSeconds - serverTimeUnixSeconds;
        if (lifetime is < 5 or > 300)
            throw new SecurityException("A validade do desafio on-line e invalida.");
    }

    private static void ValidateFreshChallenge(long serverTimeUnixSeconds,
        long expiresAtUnixSeconds, long nowUnixSeconds)
    {
        ValidateChallengeWindow(serverTimeUnixSeconds, expiresAtUnixSeconds);
        ValidateFreshServerTime(serverTimeUnixSeconds, nowUnixSeconds);
        var remainingLifetime = expiresAtUnixSeconds - nowUnixSeconds;
        if (remainingLifetime is < 1 or > 300)
            throw new SecurityException(
                "A expiracao do desafio esta fora da janela local permitida.");
    }

    private static void ValidateFreshServerTime(long serverTimeUnixSeconds,
        long nowUnixSeconds)
    {
        ValidateUnixTimeSeconds(serverTimeUnixSeconds, "O horario do servidor");
        ValidateUnixTimeSeconds(nowUnixSeconds, "O horario local");
        var minimum = Math.Max(MinimumUnixTimeSeconds, nowUnixSeconds - 300);
        var maximum = Math.Min(MaximumUnixTimeSeconds, nowUnixSeconds + 300);
        if (serverTimeUnixSeconds < minimum || serverTimeUnixSeconds > maximum)
            throw new SecurityException(
                "O horario assinado pela autoridade esta fora da janela permitida.");
    }

    private static string ChallengeKindForAction(string? action) => action switch
    {
        "session.open" => SessionOpenChallengeAssertionKind,
        "session.heartbeat" => SessionHeartbeatChallengeAssertionKind,
        _ => throw new SecurityException("A acao do desafio de sessao e invalida.")
    };

    private static ReadOnlySpan<byte> AssertionDomainForOperationChallenge(
        string? action) => action switch
        {
            "session.open" => SessionOpenChallengeAssertionDomain,
            "session.heartbeat" => SessionHeartbeatChallengeAssertionDomain,
            _ => throw new SecurityException("A acao do desafio de sessao e invalida.")
        };

    private static string AssertionKindForSession(string? action) => action switch
    {
        "session.open" => SessionOpenAssertionKind,
        "session.heartbeat" => SessionHeartbeatAssertionKind,
        _ => throw new SecurityException("A acao da assertion de sessao e invalida.")
    };

    private static ReadOnlySpan<byte> AssertionDomainForSession(string? action)
        => action switch
        {
            "session.open" => SessionOpenAssertionDomain,
            "session.heartbeat" => SessionHeartbeatAssertionDomain,
            _ => throw new SecurityException("A acao da assertion de sessao e invalida.")
        };

    private static string RequireCanonicalIdentifier(string? value, string label,
        int minimum, int maximum)
    {
        var normalized = RequireIdentifier(value, label, minimum, maximum);
        if (!string.Equals(normalized, value, StringComparison.Ordinal))
            throw new SecurityException($"{label} nao esta canonico.");
        return normalized;
    }

    private static string RequireCanonicalHex(string? value, string label, int length)
    {
        var normalized = RequireHex(value, label, length);
        if (!string.Equals(normalized, value, StringComparison.Ordinal))
            throw new SecurityException($"{label} nao esta canonico.");
        return normalized;
    }

    private static byte[] DecodeCanonicalBase64(string? value, string label,
        int minimumBytes, int maximumBytes)
    {
        var encoded = value ?? "";
        if (encoded.Any(char.IsWhiteSpace))
            throw new SecurityException($"O {label} possui formato invalido.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(encoded); }
        catch (FormatException ex)
        {
            throw new SecurityException($"O {label} possui formato invalido.", ex);
        }
        if (bytes.Length < minimumBytes || bytes.Length > maximumBytes
            || !string.Equals(Convert.ToBase64String(bytes), encoded,
                StringComparison.Ordinal))
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new SecurityException($"O {label} nao usa Base64 canonico.");
        }
        return bytes;
    }

    private static T ParseStrict<T>(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length is < 2 or > MaximumBodyBytes)
            throw new SecurityException("A resposta de licenciamento possui tamanho invalido.");
        RejectDuplicateProperties(utf8);
        try
        {
            return JsonSerializer.Deserialize<T>(utf8, WireJsonOptions)
                ?? throw new SecurityException("A resposta de licenciamento esta vazia.");
        }
        catch (JsonException ex)
        {
            throw new SecurityException("A resposta de licenciamento possui formato invalido.", ex);
        }
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
                MaxDepth = 16
            });
            RejectDuplicateProperties(document.RootElement);
        }
        catch (JsonException ex)
        {
            throw new SecurityException("A resposta de licenciamento possui JSON invalido.", ex);
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
                    throw new SecurityException("A resposta de licenciamento contem campo duplicado.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
        }
    }

    private static void ValidateSessionContext(SuiteSessionContext context)
    {
        if (context.SchemaVersion != SchemaVersion
            || !string.Equals(context.ProductId, ProductId, StringComparison.Ordinal))
            throw new SecurityException("O contexto da sessao possui produto ou versao invalida.");
        var license = RequireIdentifier(context.LicenseId, "LicenseId", 6, 64);
        var device = RequireHex(context.DeviceId, "DeviceId", 64);
        var session = RequireHex(context.SessionId, "SessionId", 64);
        var action = RequireAction(context.Action);
        var fingerprint = RequireHex(context.HardwareFingerprint, "HardwareFingerprint", 64);
        RequireVersion(context.ClientVersion, "ClientVersion");
        if (!string.Equals(license, context.LicenseId, StringComparison.Ordinal)
            || !string.Equals(device, context.DeviceId, StringComparison.Ordinal)
            || !string.Equals(session, context.SessionId, StringComparison.Ordinal)
            || !string.Equals(action, context.Action, StringComparison.Ordinal)
            || !string.Equals(fingerprint, context.HardwareFingerprint, StringComparison.Ordinal))
            throw new SecurityException("O contexto da sessao nao esta canonico.");
    }

    private static void WriteDevice(Utf8JsonWriter writer, SuiteDeviceDescriptor device)
    {
        ValidateDescriptorShape(device);
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", device.SchemaVersion);
        writer.WriteString("deviceId", RequireHex(device.DeviceId, "DeviceId", 64));
        writer.WriteString("bindingType", SuiteProtectionProfileCodec.Format(
            SuiteProtectionProfileCodec.Parse(device.BindingType)));
        writer.WriteString("algorithm", device.Algorithm);
        writer.WriteString("publicKeySpki", device.PublicKeySpki);
        writer.WriteString("hardwareFingerprint",
            RequireHex(device.HardwareFingerprint, "HardwareFingerprint", 64));
        writer.WriteString("agentVersion", device.AgentVersion);
        writer.WriteEndObject();
    }

    private static string RequireAction(string? value)
    {
        if (value is null || !AllowedActions.Contains(value))
            throw new SecurityException("A acao da prova on-line e invalida.");
        return value;
    }

    private static string RequireBase64(string? value, string label, int minimumBytes, int maximumBytes)
    {
        var normalized = value ?? "";
        if (normalized.Any(char.IsWhiteSpace))
            throw new SecurityException($"{label} possui formato invalido.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(normalized); }
        catch (FormatException ex)
        {
            throw new SecurityException($"{label} possui formato invalido.", ex);
        }
        try
        {
            if (bytes.Length < minimumBytes || bytes.Length > maximumBytes)
                throw new SecurityException($"{label} possui tamanho invalido.");
            if (!string.Equals(Convert.ToBase64String(bytes), normalized, StringComparison.Ordinal))
                throw new SecurityException($"{label} nao usa Base64 canonico.");
            return normalized;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static void RequireVersion(string? value, string label)
    {
        var version = value ?? "";
        if (version.Length is < 1 or > 64
            || version.Any(character => !(char.IsAsciiLetterOrDigit(character)
                || character is '.' or '-' or '+')))
            throw new SecurityException($"{label} possui formato invalido.");
    }

    private static byte[] CanonicalBytes(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions)) write(writer);
        return stream.ToArray();
    }

    private static string LowerHex(ReadOnlySpan<byte> bytes)
        => Convert.ToHexString(bytes).ToLowerInvariant();
}
