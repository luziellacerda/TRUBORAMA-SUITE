using System.Globalization;
using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TurboBoxManager.Licensing;

public sealed record SuiteMotherboardInventoryV1(
    int SchemaVersion,
    string LicenseId,
    string DeviceId,
    string MotherboardFingerprint,
    string BaseboardManufacturer,
    string BaseboardProduct,
    string BaseboardVersion,
    string BaseboardSerial,
    string SystemManufacturer,
    string SystemModel,
    string SystemUuid,
    string BiosManufacturer,
    string BiosVersion,
    string OsName,
    string OsVersion,
    string Architecture,
    string ClientVersion,
    string Source,
    long CollectedAtUnixSeconds);

public sealed record SuiteDeviceInventoryChallengeRequestV1(
    int SchemaVersion,
    string ProductId,
    string LicenseId,
    string DeviceId,
    string SessionId,
    string Action,
    string InventoryHash);

public sealed record SuiteDeviceInventoryProofV1(
    int SchemaVersion,
    string ProductId,
    string LicenseId,
    string DeviceId,
    string SessionId,
    string Action,
    string InventoryHash,
    string ChallengeId,
    string Signature,
    SuiteMotherboardInventoryV1 Inventory);

public sealed record SuiteDeviceInventoryChallengeAssertionV1(
    int SchemaVersion,
    string Kind,
    string ProductId,
    string LicenseId,
    string DeviceId,
    string SessionId,
    string Action,
    string InventoryHash,
    string ChallengeId,
    string Nonce,
    string Status,
    long ServerTimeUnixSeconds,
    long ExpiresAtUnixSeconds);

public sealed record SuiteDeviceInventoryResultAssertionV1(
    int SchemaVersion,
    string Kind,
    string ProductId,
    string LicenseId,
    string DeviceId,
    string SessionId,
    string Action,
    string InventoryHash,
    string ChallengeId,
    string Status,
    long ServerTimeUnixSeconds);

public sealed record SuiteDeviceInventoryResultV1(
    int SchemaVersion,
    string Status,
    string InventoryHash,
    long ServerTimeUnixSeconds);

/// <summary>
/// Isolated, additive protocol for publishing a signed motherboard inventory.
/// None of the existing Suite v1 contexts, actions or machine-proof bytes are
/// used or modified by this protocol.
/// </summary>
public static class SuiteDeviceInventoryProtocol
{
    public const int SchemaVersion = 1;
    public const string ProductId = "TURBORAMA_SUITE";
    public const string SigningAlgorithm = "rsa-pss-sha256";
    public const int MaximumBodyBytes = 64 * 1024;

    public const string ChallengeRoute = "v1/suite/devices/inventory/challenge";
    public const string InventoryRoute = "v1/suite/devices/inventory";
    public const string Action = "device.inventory";
    public const string ChallengeAssertionKind =
        "TURBORAMA_SUITE_DEVICE_INVENTORY_CHALLENGE";
    public const string ResultAssertionKind =
        "TURBORAMA_SUITE_DEVICE_INVENTORY_RESULT";
    public const string ChallengeStatus = "ISSUED";
    public const string ResultStatus = "ACCEPTED";

    public const string MotherboardIdentityDomain =
        "TurboRamaMotherboardIdentity/v1\0";
    public const string InventoryDocumentDomain =
        "TurboRamaSuiteDeviceInventory/document/v1\0";
    public const string InventoryStateDomain =
        "TurboRamaSuiteDeviceInventory/state/v1\0";
    public const string InventoryProofDomain =
        "TurboRamaSuiteDeviceInventoryProof/v1\0";
    public const string ChallengeAssertionDomain =
        "TurboRamaSuiteOnlineAssertion/device-inventory-challenge/v1\0";
    public const string ResultAssertionDomain =
        "TurboRamaSuiteOnlineAssertion/device-inventory-result/v1\0";

    private const long MinimumUnixTimeSeconds = 1;
    private const long MaximumUnixTimeSeconds = 253_402_300_799;

    private static readonly byte[] InventoryDocumentDomainBytes =
        Encoding.ASCII.GetBytes(InventoryDocumentDomain);
    private static readonly byte[] InventoryStateDomainBytes =
        Encoding.ASCII.GetBytes(InventoryStateDomain);
    private static readonly byte[] InventoryProofDomainBytes =
        Encoding.ASCII.GetBytes(InventoryProofDomain);
    private static readonly byte[] ChallengeAssertionDomainBytes =
        Encoding.ASCII.GetBytes(ChallengeAssertionDomain);
    private static readonly byte[] ResultAssertionDomainBytes =
        Encoding.ASCII.GetBytes(ResultAssertionDomain);

    private static readonly HashSet<string> PlaceholderValues = new(
        StringComparer.Ordinal)
    {
        "TO BE FILLED BY O.E.M.",
        "TO BE FILLED BY O.E.M",
        "TO BE FILLED BY OEM",
        "DEFAULT STRING",
        "SYSTEM PRODUCT NAME",
        "UNKNOWN",
        "NONE",
        "NOT SPECIFIED",
        "00000000",
        "FFFFFFFF"
    };

    private static readonly HashSet<string> AllowedArchitectures = new(
        StringComparer.Ordinal)
    {
        "X64", "X86", "ARM64", "ARM"
    };

    private static readonly HashSet<string> AllowedSources = new(
        StringComparer.Ordinal)
    {
        "CIM", "REGISTRY_FALLBACK", "CIM_AND_REGISTRY"
    };

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

    public static byte[] CanonicalInventory(SuiteMotherboardInventoryV1 inventory)
    {
        ValidateInventory(inventory);
        return CanonicalBytes(writer => WriteInventory(writer, inventory,
            includeCollectedAt: true));
    }

    public static string InventoryHash(SuiteMotherboardInventoryV1 inventory)
    {
        var canonical = CanonicalInventory(inventory);
        try { return DomainSeparatedHash(InventoryDocumentDomainBytes, canonical); }
        finally { CryptographicOperations.ZeroMemory(canonical); }
    }

    /// <summary>
    /// Stable local publication gate. It covers every inventory member except
    /// CollectedAtUnixSeconds so merely collecting the same state again does not
    /// create a network publication.
    /// </summary>
    public static byte[] CanonicalInventoryState(SuiteMotherboardInventoryV1 inventory)
    {
        ValidateInventory(inventory);
        return CanonicalBytes(writer => WriteInventory(writer, inventory,
            includeCollectedAt: false));
    }

    public static string InventoryStateHash(SuiteMotherboardInventoryV1 inventory)
    {
        var canonical = CanonicalInventoryState(inventory);
        try { return DomainSeparatedHash(InventoryStateDomainBytes, canonical); }
        finally { CryptographicOperations.ZeroMemory(canonical); }
    }

    public static byte[] CanonicalChallengeRequest(
        SuiteDeviceInventoryChallengeRequestV1 request)
    {
        ValidateChallengeRequest(request);
        return CanonicalBytes(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", request.SchemaVersion);
            writer.WriteString("productId", request.ProductId);
            writer.WriteString("licenseId", request.LicenseId);
            writer.WriteString("deviceId", request.DeviceId);
            writer.WriteString("sessionId", request.SessionId);
            writer.WriteString("action", request.Action);
            writer.WriteString("inventoryHash", request.InventoryHash);
            writer.WriteEndObject();
        });
    }

    public static byte[] SerializeChallengeRequest(
        SuiteDeviceInventoryChallengeRequestV1 request)
        => CanonicalChallengeRequest(request);

    public static byte[] CanonicalProof(SuiteDeviceInventoryProofV1 proof)
    {
        ValidateProof(proof);
        return CanonicalBytes(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", proof.SchemaVersion);
            writer.WriteString("productId", proof.ProductId);
            writer.WriteString("licenseId", proof.LicenseId);
            writer.WriteString("deviceId", proof.DeviceId);
            writer.WriteString("sessionId", proof.SessionId);
            writer.WriteString("action", proof.Action);
            writer.WriteString("inventoryHash", proof.InventoryHash);
            writer.WriteString("challengeId", proof.ChallengeId);
            writer.WriteString("signature", proof.Signature);
            writer.WritePropertyName("inventory");
            WriteInventory(writer, proof.Inventory, includeCollectedAt: true);
            writer.WriteEndObject();
        });
    }

    public static byte[] SerializeProof(SuiteDeviceInventoryProofV1 proof)
        => CanonicalProof(proof);

    public static byte[] BuildProofSigningMessage(SuiteChallengeResponse challenge,
        string licenseId, string deviceId, string sessionId, string inventoryHash)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        ValidateChallengeShape(challenge);
        var canonical = CanonicalBytes(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", SchemaVersion);
            writer.WriteString("challengeId", RequireCanonicalHex(
                challenge.ChallengeId, "ChallengeId"));
            writer.WriteString("nonce", RequireCanonicalBase64(
                challenge.Nonce, "Nonce", 32, 64));
            writer.WriteNumber("expiresAtUnixSeconds", challenge.ExpiresAtUnixSeconds);
            writer.WriteString("productId", ProductId);
            writer.WriteString("licenseId", RequireCanonicalIdentifier(
                licenseId, "LicenseId", 6, 64));
            writer.WriteString("deviceId", RequireCanonicalHex(deviceId, "DeviceId"));
            writer.WriteString("sessionId", RequireCanonicalHex(sessionId, "SessionId"));
            writer.WriteString("action", Action);
            writer.WriteString("inventoryHash", RequireCanonicalHex(
                inventoryHash, "InventoryHash"));
            writer.WriteEndObject();
        });
        try { return PrefixDomain(InventoryProofDomainBytes, canonical); }
        finally { CryptographicOperations.ZeroMemory(canonical); }
    }

    public static byte[] CanonicalChallengeAssertion(
        SuiteDeviceInventoryChallengeAssertionV1 assertion)
    {
        ValidateChallengeAssertion(assertion);
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
            writer.WriteString("inventoryHash", assertion.InventoryHash);
            writer.WriteString("challengeId", assertion.ChallengeId);
            writer.WriteString("nonce", assertion.Nonce);
            writer.WriteString("status", assertion.Status);
            writer.WriteNumber("serverTimeUnixSeconds",
                assertion.ServerTimeUnixSeconds);
            writer.WriteNumber("expiresAtUnixSeconds",
                assertion.ExpiresAtUnixSeconds);
            writer.WriteEndObject();
        });
    }

    public static byte[] BuildChallengeAssertionSigningMessage(
        SuiteDeviceInventoryChallengeAssertionV1 assertion)
        => BuildAssertionSigningMessage(assertion, CanonicalChallengeAssertion,
            ChallengeAssertionDomainBytes);

    public static byte[] CanonicalResultAssertion(
        SuiteDeviceInventoryResultAssertionV1 assertion)
    {
        ValidateResultAssertion(assertion);
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
            writer.WriteString("inventoryHash", assertion.InventoryHash);
            writer.WriteString("challengeId", assertion.ChallengeId);
            writer.WriteString("status", assertion.Status);
            writer.WriteNumber("serverTimeUnixSeconds",
                assertion.ServerTimeUnixSeconds);
            writer.WriteEndObject();
        });
    }

    public static byte[] BuildResultAssertionSigningMessage(
        SuiteDeviceInventoryResultAssertionV1 assertion)
        => BuildAssertionSigningMessage(assertion, CanonicalResultAssertion,
            ResultAssertionDomainBytes);

    public static SuiteChallengeResponse ParseChallengeAssertion(
        ReadOnlySpan<byte> utf8,
        ReadOnlySpan<byte> onlineAssertionSpki,
        string onlineAssertionKeyId,
        string licenseId,
        string deviceId,
        string sessionId,
        string inventoryHash,
        long nowUnixSeconds)
    {
        var assertion = ParseSignedAssertion<SuiteDeviceInventoryChallengeAssertionV1>(
            utf8, onlineAssertionSpki, onlineAssertionKeyId,
            ChallengeAssertionKind, ChallengeAssertionDomainBytes,
            CanonicalChallengeAssertion);
        ValidateFreshChallenge(assertion.ServerTimeUnixSeconds,
            assertion.ExpiresAtUnixSeconds, nowUnixSeconds);
        MatchOperation(assertion.ProductId, assertion.LicenseId,
            assertion.DeviceId, assertion.SessionId, assertion.Action,
            assertion.InventoryHash, licenseId, deviceId, sessionId,
            inventoryHash);
        return new SuiteChallengeResponse(assertion.SchemaVersion,
            assertion.ChallengeId, assertion.Nonce,
            assertion.ExpiresAtUnixSeconds);
    }

    public static SuiteDeviceInventoryResultV1 ParseResultAssertion(
        ReadOnlySpan<byte> utf8,
        ReadOnlySpan<byte> onlineAssertionSpki,
        string onlineAssertionKeyId,
        string licenseId,
        string deviceId,
        string sessionId,
        string inventoryHash,
        string challengeId,
        long nowUnixSeconds)
    {
        var assertion = ParseSignedAssertion<SuiteDeviceInventoryResultAssertionV1>(
            utf8, onlineAssertionSpki, onlineAssertionKeyId,
            ResultAssertionKind, ResultAssertionDomainBytes,
            CanonicalResultAssertion);
        ValidateFreshServerTime(assertion.ServerTimeUnixSeconds, nowUnixSeconds);
        MatchOperation(assertion.ProductId, assertion.LicenseId,
            assertion.DeviceId, assertion.SessionId, assertion.Action,
            assertion.InventoryHash, licenseId, deviceId, sessionId,
            inventoryHash);
        if (!FixedHexEquals(assertion.ChallengeId,
                RequireCanonicalHex(challengeId, "ChallengeId")))
            throw new SecurityException(
                "O resultado do inventario nao pertence ao desafio enviado.");
        return new SuiteDeviceInventoryResultV1(assertion.SchemaVersion,
            assertion.Status, assertion.InventoryHash,
            assertion.ServerTimeUnixSeconds);
    }

    public static void ValidateInventory(SuiteMotherboardInventoryV1 inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        RequireSchema(inventory.SchemaVersion);
        _ = RequireCanonicalIdentifier(inventory.LicenseId, "LicenseId", 6, 64);
        _ = RequireCanonicalHex(inventory.DeviceId, "DeviceId");
        _ = RequireCanonicalHex(inventory.MotherboardFingerprint,
            "MotherboardFingerprint");

        RequireDisplayText(inventory.BaseboardManufacturer,
            "BaseboardManufacturer", 128, identityField: true);
        RequireDisplayText(inventory.BaseboardProduct,
            "BaseboardProduct", 128, identityField: true);
        RequireDisplayText(inventory.BaseboardVersion,
            "BaseboardVersion", 128, identityField: true);
        RequireDisplayText(inventory.BaseboardSerial,
            "BaseboardSerial", 128, identityField: true);
        if (!string.Equals(
                inventory.BaseboardSerial,
                SuiteMotherboardInventoryNormalizer.NormalizeSerial(
                    inventory.BaseboardSerial, 128),
                StringComparison.Ordinal))
            throw new SecurityException(
                "BaseboardSerial deve representar serial nao confiavel como string vazia.");
        RequireDisplayText(inventory.SystemManufacturer,
            "SystemManufacturer", 128, identityField: true);
        RequireDisplayText(inventory.SystemModel,
            "SystemModel", 128, identityField: true);
        RequireCanonicalUuid(inventory.SystemUuid);
        RequireDisplayText(inventory.BiosManufacturer,
            "BiosManufacturer", 128, identityField: true);
        RequireDisplayText(inventory.BiosVersion,
            "BiosVersion", 128, identityField: true);
        RequireDisplayText(inventory.OsName,
            "OsName", 128, identityField: false);
        RequireDisplayText(inventory.OsVersion,
            "OsVersion", 64, identityField: false);
        if (!AllowedArchitectures.Contains(inventory.Architecture ?? ""))
            throw new SecurityException("Architecture possui formato invalido.");
        RequireVersion(inventory.ClientVersion, "ClientVersion");
        if (!AllowedSources.Contains(inventory.Source ?? ""))
            throw new SecurityException("Source possui formato invalido.");
        ValidateUnixTimeSeconds(inventory.CollectedAtUnixSeconds,
            "CollectedAtUnixSeconds");

        var hasIdentityEvidence = inventory.BaseboardSerial.Length != 0
            || inventory.SystemUuid.Length != 0
            || (inventory.BaseboardManufacturer.Length != 0
                && inventory.BaseboardProduct.Length != 0)
            || (inventory.SystemManufacturer.Length != 0
                && inventory.SystemModel.Length != 0);
        if (!hasIdentityEvidence)
            throw new SecurityException(
                "O inventario nao contem evidencia suficiente da placa-mae.");

        var expectedFingerprint =
            SuiteMotherboardInventoryNormalizer.ComputeFingerprint(
                inventory.BaseboardManufacturer,
                inventory.BaseboardProduct,
                inventory.BaseboardVersion,
                inventory.BaseboardSerial,
                inventory.SystemManufacturer,
                inventory.SystemModel,
                inventory.SystemUuid);
        if (!FixedHexEquals(
                inventory.MotherboardFingerprint, expectedFingerprint))
            throw new SecurityException(
                "MotherboardFingerprint nao corresponde ao inventario.");
    }

    public static void ValidateChallengeRequest(
        SuiteDeviceInventoryChallengeRequestV1 request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireCommonOperation(request.SchemaVersion, request.ProductId,
            request.LicenseId, request.DeviceId, request.SessionId,
            request.Action, request.InventoryHash);
    }

    public static void ValidateProof(SuiteDeviceInventoryProofV1 proof)
    {
        ArgumentNullException.ThrowIfNull(proof);
        RequireCommonOperation(proof.SchemaVersion, proof.ProductId,
            proof.LicenseId, proof.DeviceId, proof.SessionId,
            proof.Action, proof.InventoryHash);
        _ = RequireCanonicalHex(proof.ChallengeId, "ChallengeId");
        _ = RequireCanonicalBase64(proof.Signature, "Signature", 256, 512);
        ValidateInventory(proof.Inventory);
        if (!string.Equals(proof.LicenseId, proof.Inventory.LicenseId,
                StringComparison.Ordinal)
            || !FixedHexEquals(proof.DeviceId, proof.Inventory.DeviceId)
            || !FixedHexEquals(proof.InventoryHash,
                InventoryHash(proof.Inventory)))
            throw new SecurityException(
                "O inventario nao corresponde a prova informada.");
    }

    public static bool VerifyProof(ReadOnlySpan<byte> deviceSpki,
        SuiteChallengeResponse challenge, SuiteDeviceInventoryProofV1 proof)
    {
        ValidateProof(proof);
        if (!FixedHexEquals(proof.ChallengeId, challenge.ChallengeId))
            throw new SecurityException(
                "A prova nao pertence ao desafio de inventario informado.");
        var signature = DecodeCanonicalBase64(proof.Signature,
            "Signature", 256, 512);
        var message = BuildProofSigningMessage(challenge,
            proof.LicenseId, proof.DeviceId, proof.SessionId,
            proof.InventoryHash);
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(deviceSpki, out var consumed);
            if (consumed != deviceSpki.Length || rsa.KeySize is < 2048 or > 4096)
                throw new SecurityException(
                    "A chave publica do dispositivo e invalida.");
            var canonicalSpki = rsa.ExportSubjectPublicKeyInfo();
            try
            {
                if (!canonicalSpki.AsSpan().SequenceEqual(deviceSpki))
                    throw new SecurityException(
                        "A chave publica do dispositivo nao usa SPKI DER canonico.");
                var actualDeviceId = LowerHex(SHA256.HashData(canonicalSpki));
                if (!FixedHexEquals(actualDeviceId, proof.DeviceId))
                    throw new SecurityException(
                        "O DeviceId nao corresponde a chave publica do dispositivo.");
                return rsa.VerifyData(message, signature,
                    HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
            }
            finally { CryptographicOperations.ZeroMemory(canonicalSpki); }
        }
        catch (CryptographicException ex)
        {
            throw new SecurityException(
                "Nao foi possivel validar a prova do inventario.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
            CryptographicOperations.ZeroMemory(message);
        }
    }

    private static TAssertion ParseSignedAssertion<TAssertion>(
        ReadOnlySpan<byte> utf8,
        ReadOnlySpan<byte> onlineAssertionSpki,
        string onlineAssertionKeyId,
        string expectedKind,
        byte[] domain,
        Func<TAssertion, byte[]> canonicalPayload)
    {
        var envelope = ParseStrict<SuiteSignedAssertionEnvelope>(utf8);
        var expectedKeyId = RequireCanonicalHex(
            onlineAssertionKeyId, "OnlineAssertionKeyId");
        if (envelope.SchemaVersion != SchemaVersion
            || !string.Equals(envelope.Kind, expectedKind,
                StringComparison.Ordinal)
            || !string.Equals(envelope.Algorithm, SigningAlgorithm,
                StringComparison.Ordinal)
            || !string.Equals(envelope.KeyId,
                RequireCanonicalHex(envelope.KeyId, "KeyId"),
                StringComparison.Ordinal)
            || !FixedHexEquals(envelope.KeyId, expectedKeyId))
            throw new SecurityException(
                "O envelope da assertion de inventario e invalido.");

        var payload = DecodeCanonicalBase64(envelope.Payload,
            "Payload", 64, MaximumBodyBytes);
        var signature = DecodeCanonicalBase64(envelope.Signature,
            "Signature", 256, 512);
        byte[] canonical = Array.Empty<byte>();
        byte[] message = Array.Empty<byte>();
        try
        {
            var assertion = ParseStrict<TAssertion>(payload);
            canonical = canonicalPayload(assertion);
            if (!canonical.AsSpan().SequenceEqual(payload))
                throw new SecurityException(
                    "O payload da assertion nao usa JSON canonico.");
            message = PrefixDomain(domain, canonical);

            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(onlineAssertionSpki,
                out var consumed);
            if (consumed != onlineAssertionSpki.Length
                || rsa.KeySize is < 2048 or > 4096)
                throw new SecurityException(
                    "A chave de assertions on-line e invalida.");
            var canonicalSpki = rsa.ExportSubjectPublicKeyInfo();
            try
            {
                if (!canonicalSpki.AsSpan().SequenceEqual(onlineAssertionSpki))
                    throw new SecurityException(
                        "A chave de assertions nao usa SPKI DER canonico.");
                var actualKeyId = LowerHex(SHA256.HashData(canonicalSpki));
                if (!FixedHexEquals(actualKeyId, expectedKeyId)
                    || !rsa.VerifyData(message, signature,
                        HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
                    throw new SecurityException(
                        "A assinatura da assertion de inventario e invalida.");
            }
            finally { CryptographicOperations.ZeroMemory(canonicalSpki); }
            return assertion;
        }
        catch (CryptographicException ex)
        {
            throw new SecurityException(
                "Nao foi possivel validar a assertion de inventario.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
            CryptographicOperations.ZeroMemory(signature);
            if (canonical.Length != 0)
                CryptographicOperations.ZeroMemory(canonical);
            if (message.Length != 0)
                CryptographicOperations.ZeroMemory(message);
        }
    }

    private static byte[] BuildAssertionSigningMessage<TAssertion>(
        TAssertion assertion,
        Func<TAssertion, byte[]> canonicalPayload,
        byte[] domain)
    {
        var canonical = canonicalPayload(assertion);
        try { return PrefixDomain(domain, canonical); }
        finally { CryptographicOperations.ZeroMemory(canonical); }
    }

    private static void WriteInventory(Utf8JsonWriter writer,
        SuiteMotherboardInventoryV1 inventory, bool includeCollectedAt)
    {
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", inventory.SchemaVersion);
        writer.WriteString("licenseId", inventory.LicenseId);
        writer.WriteString("deviceId", inventory.DeviceId);
        writer.WriteString("motherboardFingerprint",
            inventory.MotherboardFingerprint);
        writer.WriteString("baseboardManufacturer",
            inventory.BaseboardManufacturer);
        writer.WriteString("baseboardProduct", inventory.BaseboardProduct);
        writer.WriteString("baseboardVersion", inventory.BaseboardVersion);
        writer.WriteString("baseboardSerial", inventory.BaseboardSerial);
        writer.WriteString("systemManufacturer", inventory.SystemManufacturer);
        writer.WriteString("systemModel", inventory.SystemModel);
        writer.WriteString("systemUuid", inventory.SystemUuid);
        writer.WriteString("biosManufacturer", inventory.BiosManufacturer);
        writer.WriteString("biosVersion", inventory.BiosVersion);
        writer.WriteString("osName", inventory.OsName);
        writer.WriteString("osVersion", inventory.OsVersion);
        writer.WriteString("architecture", inventory.Architecture);
        writer.WriteString("clientVersion", inventory.ClientVersion);
        writer.WriteString("source", inventory.Source);
        if (includeCollectedAt)
            writer.WriteNumber("collectedAtUnixSeconds",
                inventory.CollectedAtUnixSeconds);
        writer.WriteEndObject();
    }

    private static void ValidateChallengeAssertion(
        SuiteDeviceInventoryChallengeAssertionV1 assertion)
    {
        ArgumentNullException.ThrowIfNull(assertion);
        RequireCommonOperation(assertion.SchemaVersion, assertion.ProductId,
            assertion.LicenseId, assertion.DeviceId, assertion.SessionId,
            assertion.Action, assertion.InventoryHash);
        if (!string.Equals(assertion.Kind, ChallengeAssertionKind,
                StringComparison.Ordinal)
            || !string.Equals(assertion.Status, ChallengeStatus,
                StringComparison.Ordinal))
            throw new SecurityException(
                "A assertion do desafio de inventario possui tipo invalido.");
        _ = RequireCanonicalHex(assertion.ChallengeId, "ChallengeId");
        _ = RequireCanonicalBase64(assertion.Nonce, "Nonce", 32, 64);
        ValidateChallengeWindow(assertion.ServerTimeUnixSeconds,
            assertion.ExpiresAtUnixSeconds);
    }

    private static void ValidateResultAssertion(
        SuiteDeviceInventoryResultAssertionV1 assertion)
    {
        ArgumentNullException.ThrowIfNull(assertion);
        RequireCommonOperation(assertion.SchemaVersion, assertion.ProductId,
            assertion.LicenseId, assertion.DeviceId, assertion.SessionId,
            assertion.Action, assertion.InventoryHash);
        if (!string.Equals(assertion.Kind, ResultAssertionKind,
                StringComparison.Ordinal)
            || !string.Equals(assertion.Status, ResultStatus,
                StringComparison.Ordinal))
            throw new SecurityException(
                "A assertion do resultado de inventario possui tipo invalido.");
        _ = RequireCanonicalHex(assertion.ChallengeId, "ChallengeId");
        ValidateUnixTimeSeconds(assertion.ServerTimeUnixSeconds,
            "ServerTimeUnixSeconds");
    }

    private static void RequireCommonOperation(int schemaVersion,
        string productId, string licenseId, string deviceId,
        string sessionId, string action, string inventoryHash)
    {
        RequireSchema(schemaVersion);
        if (!string.Equals(productId, ProductId, StringComparison.Ordinal))
            throw new SecurityException("ProductId possui formato invalido.");
        _ = RequireCanonicalIdentifier(licenseId, "LicenseId", 6, 64);
        _ = RequireCanonicalHex(deviceId, "DeviceId");
        _ = RequireCanonicalHex(sessionId, "SessionId");
        if (!string.Equals(action, Action, StringComparison.Ordinal))
            throw new SecurityException("Action possui formato invalido.");
        _ = RequireCanonicalHex(inventoryHash, "InventoryHash");
    }

    private static void MatchOperation(string productId, string licenseId,
        string deviceId, string sessionId, string action,
        string inventoryHash, string expectedLicenseId,
        string expectedDeviceId, string expectedSessionId,
        string expectedInventoryHash)
    {
        if (!string.Equals(productId, ProductId, StringComparison.Ordinal)
            || !string.Equals(licenseId, RequireCanonicalIdentifier(
                expectedLicenseId, "LicenseId", 6, 64),
                StringComparison.Ordinal)
            || !FixedHexEquals(deviceId,
                RequireCanonicalHex(expectedDeviceId, "DeviceId"))
            || !FixedHexEquals(sessionId,
                RequireCanonicalHex(expectedSessionId, "SessionId"))
            || !string.Equals(action, Action, StringComparison.Ordinal)
            || !FixedHexEquals(inventoryHash,
                RequireCanonicalHex(expectedInventoryHash, "InventoryHash")))
            throw new SecurityException(
                "A assertion de inventario nao pertence a esta operacao.");
    }

    private static void ValidateChallengeShape(SuiteChallengeResponse challenge)
    {
        RequireSchema(challenge.SchemaVersion);
        _ = RequireCanonicalHex(challenge.ChallengeId, "ChallengeId");
        _ = RequireCanonicalBase64(challenge.Nonce, "Nonce", 32, 64);
        ValidateUnixTimeSeconds(challenge.ExpiresAtUnixSeconds,
            "ExpiresAtUnixSeconds");
    }

    private static void ValidateChallengeWindow(long serverTimeUnixSeconds,
        long expiresAtUnixSeconds)
    {
        ValidateUnixTimeSeconds(serverTimeUnixSeconds,
            "ServerTimeUnixSeconds");
        ValidateUnixTimeSeconds(expiresAtUnixSeconds,
            "ExpiresAtUnixSeconds");
        var lifetime = expiresAtUnixSeconds - serverTimeUnixSeconds;
        if (lifetime is < 5 or > 300)
            throw new SecurityException(
                "A validade do desafio de inventario e invalida.");
    }

    private static void ValidateFreshChallenge(long serverTimeUnixSeconds,
        long expiresAtUnixSeconds, long nowUnixSeconds)
    {
        ValidateChallengeWindow(serverTimeUnixSeconds, expiresAtUnixSeconds);
        ValidateFreshServerTime(serverTimeUnixSeconds, nowUnixSeconds);
        var remaining = expiresAtUnixSeconds - nowUnixSeconds;
        if (remaining is < 1 or > 300)
            throw new SecurityException(
                "A expiracao do desafio de inventario esta fora da janela permitida.");
    }

    private static void ValidateFreshServerTime(long serverTimeUnixSeconds,
        long nowUnixSeconds)
    {
        ValidateUnixTimeSeconds(serverTimeUnixSeconds,
            "ServerTimeUnixSeconds");
        ValidateUnixTimeSeconds(nowUnixSeconds, "NowUnixSeconds");
        var minimum = Math.Max(MinimumUnixTimeSeconds,
            nowUnixSeconds - 300);
        var maximum = Math.Min(MaximumUnixTimeSeconds,
            nowUnixSeconds + 300);
        if (serverTimeUnixSeconds < minimum
            || serverTimeUnixSeconds > maximum)
            throw new SecurityException(
                "O horario da assertion de inventario esta fora da janela permitida.");
    }

    private static void ValidateUnixTimeSeconds(long value, string label)
    {
        if (value is < MinimumUnixTimeSeconds or > MaximumUnixTimeSeconds)
            throw new SecurityException($"{label} possui epoca Unix invalida.");
    }

    private static void RequireSchema(int schemaVersion)
    {
        if (schemaVersion != SchemaVersion)
            throw new SecurityException(
                "SchemaVersion do inventario e invalido.");
    }

    private static string RequireCanonicalIdentifier(string? value,
        string label, int minimum, int maximum)
    {
        var candidate = value ?? "";
        if (candidate.Length < minimum || candidate.Length > maximum
            || candidate.Any(character => !(char.IsAsciiLetterOrDigit(character)
                || character is '-' or '_')))
            throw new SecurityException($"{label} possui formato invalido.");
        return candidate;
    }

    private static string RequireCanonicalHex(string? value, string label)
    {
        var candidate = value ?? "";
        if (candidate.Length != 64
            || candidate.Any(character => character is not (>= '0' and <= '9')
                and not (>= 'a' and <= 'f')))
            throw new SecurityException($"{label} possui formato invalido.");
        return candidate;
    }

    private static void RequireDisplayText(string? value, string label,
        int maximum, bool identityField)
    {
        var candidate = value ?? throw new SecurityException(
            $"{label} nao foi informado.");
        if (Encoding.UTF8.GetByteCount(candidate) > maximum
            || !candidate.IsNormalized(NormalizationForm.FormC)
            || (candidate.Length != 0
                && (char.IsWhiteSpace(candidate[0])
                    || char.IsWhiteSpace(candidate[^1]))))
            throw new SecurityException($"{label} possui formato invalido.");

        var previousWhitespace = false;
        foreach (var rune in candidate.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            var isWhitespace = Rune.IsWhiteSpace(rune);
            if (category is UnicodeCategory.Control
                    or UnicodeCategory.Format
                    or UnicodeCategory.Surrogate
                    or UnicodeCategory.PrivateUse
                    or UnicodeCategory.OtherNotAssigned
                || (isWhitespace && rune.Value != ' ')
                || (rune.Value == ' ' && previousWhitespace))
                throw new SecurityException($"{label} possui formato invalido.");
            previousWhitespace = rune.Value == ' ';
        }

        if (identityField && candidate.Length != 0)
        {
            if (PlaceholderValues.Contains(IdentityComparisonValue(candidate))
                || !string.Equals(
                    candidate,
                    SuiteMotherboardInventoryNormalizer.NormalizeHardwareDisplay(
                        candidate, maximum),
                    StringComparison.Ordinal))
                throw new SecurityException(
                    $"{label} deve usar a forma canonica ou representar valor nao confiavel como string vazia.");
        }
    }

    private static string IdentityComparisonValue(string value)
        => value.Normalize(NormalizationForm.FormKC).ToUpperInvariant();

    private static void RequireCanonicalUuid(string? value)
    {
        var candidate = value ?? throw new SecurityException(
            "SystemUuid nao foi informado.");
        if (candidate.Length == 0) return;
        if (!Guid.TryParseExact(candidate, "D", out var parsed)
            || parsed == Guid.Empty
            || parsed.ToString("D").Equals(
                "ffffffff-ffff-ffff-ffff-ffffffffffff",
                StringComparison.Ordinal)
            || !string.Equals(parsed.ToString("D"), candidate,
                StringComparison.Ordinal))
            throw new SecurityException("SystemUuid possui formato invalido.");
    }

    private static void RequireVersion(string? value, string label)
    {
        var candidate = value ?? "";
        if (candidate.Length is < 1 or > 64
            || candidate.Any(character => !(char.IsAsciiLetterOrDigit(character)
                || character is '.' or '-' or '+')))
            throw new SecurityException($"{label} possui formato invalido.");
    }

    private static string RequireCanonicalBase64(string? value,
        string label, int minimumBytes, int maximumBytes)
    {
        var bytes = DecodeCanonicalBase64(value, label,
            minimumBytes, maximumBytes);
        try { return value!; }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static byte[] DecodeCanonicalBase64(string? value,
        string label, int minimumBytes, int maximumBytes)
    {
        var encoded = value ?? "";
        if (encoded.Any(char.IsWhiteSpace))
            throw new SecurityException($"{label} possui formato invalido.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(encoded); }
        catch (FormatException ex)
        {
            throw new SecurityException($"{label} possui formato invalido.", ex);
        }

        if (bytes.Length < minimumBytes || bytes.Length > maximumBytes
            || !string.Equals(Convert.ToBase64String(bytes), encoded,
                StringComparison.Ordinal))
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new SecurityException($"{label} nao usa Base64 canonico.");
        }
        return bytes;
    }

    private static bool FixedHexEquals(string? left, string? right)
    {
        try
        {
            var a = Encoding.ASCII.GetBytes(
                RequireCanonicalHex(left, "Hash"));
            var b = Encoding.ASCII.GetBytes(
                RequireCanonicalHex(right, "Hash"));
            try { return CryptographicOperations.FixedTimeEquals(a, b); }
            finally
            {
                CryptographicOperations.ZeroMemory(a);
                CryptographicOperations.ZeroMemory(b);
            }
        }
        catch (SecurityException) { return false; }
    }

    private static T ParseStrict<T>(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length is < 2 or > MaximumBodyBytes)
            throw new SecurityException(
                "A resposta de inventario possui tamanho invalido.");
        RejectDuplicateProperties(utf8);
        try
        {
            return JsonSerializer.Deserialize<T>(utf8, WireJsonOptions)
                ?? throw new SecurityException(
                    "A resposta de inventario esta vazia.");
        }
        catch (JsonException ex)
        {
            throw new SecurityException(
                "A resposta de inventario possui formato invalido.", ex);
        }
    }

    private static void RejectDuplicateProperties(ReadOnlySpan<byte> utf8)
    {
        var copy = utf8.ToArray();
        try
        {
            using var document = JsonDocument.Parse(copy,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 16
                });
            RejectDuplicateProperties(document.RootElement);
        }
        catch (JsonException ex)
        {
            throw new SecurityException(
                "A resposta de inventario possui JSON invalido.", ex);
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
                    throw new SecurityException(
                        "A resposta de inventario contem campo duplicado.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                RejectDuplicateProperties(item);
        }
    }

    private static string DomainSeparatedHash(ReadOnlySpan<byte> domain,
        ReadOnlySpan<byte> canonical)
    {
        var bytes = PrefixDomain(domain, canonical);
        try { return LowerHex(SHA256.HashData(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static byte[] PrefixDomain(ReadOnlySpan<byte> domain,
        ReadOnlySpan<byte> canonical)
    {
        var result = new byte[checked(domain.Length + canonical.Length)];
        domain.CopyTo(result);
        canonical.CopyTo(result.AsSpan(domain.Length));
        return result;
    }

    private static byte[] CanonicalBytes(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
            write(writer);
        return stream.ToArray();
    }

    private static string LowerHex(ReadOnlySpan<byte> bytes)
        => Convert.ToHexString(bytes).ToLowerInvariant();
}
