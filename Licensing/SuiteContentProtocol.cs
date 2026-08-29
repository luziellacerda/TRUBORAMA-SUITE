using System.Globalization;
using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using TurboBoxManager.Catalog;

namespace TurboBoxManager.Licensing;

public sealed record SuiteCatalogPageContext(
    int SchemaVersion,
    string ProductId,
    string LicenseId,
    string DeviceId,
    string SessionId,
    string Action,
    string Cursor,
    int PageSize);

public sealed record SuiteDownloadGrantContext(
    int SchemaVersion,
    string ProductId,
    string LicenseId,
    string DeviceId,
    string SessionId,
    string Action,
    string CatalogIdentity,
    string ItemId,
    string ArtifactId,
    int ArtifactVersion,
    string ManifestIdentity,
    string DescriptorHash,
    long Offset,
    string SourceETag,
    string SourceLastModified);

public sealed record SuiteContentProof<TContext>(
    SuiteOperationProof Proof,
    TContext Context);

public sealed record SuiteWireArtifactDescriptor(
    string ArtifactId,
    int ArtifactVersion,
    string SafeFileName,
    string FileExtension,
    string ExtractPolicy,
    string ManifestIdentity);

public sealed record SuiteAuthorizedArtifact(
    string ItemId,
    string Availability,
    SuiteWireArtifactDescriptor? Descriptor,
    string? ReasonCode);

public sealed record SuiteCatalogPageAssertion(
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
    long ExpiresAtUnixSeconds,
    string CatalogIdentity,
    long CatalogSequence,
    List<SuiteAuthorizedArtifact> Items,
    string? NextCursor);

public sealed record SuiteDownloadGrantAssertion(
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
    long ExpiresAtUnixSeconds,
    string CatalogIdentity,
    string ItemId,
    string ArtifactId,
    int ArtifactVersion,
    string ManifestIdentity,
    string DescriptorHash,
    long RangeStart,
    string GrantId,
    string ContentPath,
    string BearerToken);

/// <summary>
/// Additive content protocol. It deliberately uses the unchanged v1 machine-proof
/// signing message; product and content binding remain committed by ContextHash.
/// </summary>
public static class SuiteContentProtocol
{
    public const string CatalogAction = "catalog.read";
    public const string DownloadAction = "download.authorize";
    public const string CatalogRoute = "v1/suite-content/catalog/current";
    public const string DownloadAuthorizeRoute =
        "v1/suite-content/downloads/authorize";
    public const string ArtifactRoutePrefix = "/v1/suite-content/artifacts/";
    public const string CatalogPageAssertionKind =
        "TURBORAMA_SUITE_CATALOG_PAGE";
    public const string DownloadGrantAssertionKind =
        "TURBORAMA_SUITE_DOWNLOAD_GRANT";
    public const int MaximumPageSize = 64;
    public const int ExpectedCatalogItemCount = 850;
    public const string ReadyAvailability = "READY";
    public const string MaintenanceAvailability = "MAINTENANCE";
    public const string MaintenanceReasonCode =
        "CONTENT_TEMPORARILY_UNAVAILABLE";

    private static readonly byte[] CatalogPageAssertionDomain =
        Encoding.ASCII.GetBytes(
            "TurboRamaSuiteContentAssertion/catalog-page/v1\0");
    private static readonly byte[] DownloadGrantAssertionDomain =
        Encoding.ASCII.GetBytes(
            "TurboRamaSuiteContentAssertion/download-grant/v1\0");

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

    public static byte[] CanonicalCatalogPageContext(
        SuiteCatalogPageContext context)
    {
        ValidateCatalogPageContext(context);
        return CanonicalBytes(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", context.SchemaVersion);
            writer.WriteString("productId", context.ProductId);
            writer.WriteString("licenseId", context.LicenseId);
            writer.WriteString("deviceId", context.DeviceId);
            writer.WriteString("sessionId", context.SessionId);
            writer.WriteString("action", context.Action);
            writer.WriteString("cursor", context.Cursor);
            writer.WriteNumber("pageSize", context.PageSize);
            writer.WriteEndObject();
        });
    }

    public static string CatalogPageContextHash(SuiteCatalogPageContext context)
        => HashCanonical(CanonicalCatalogPageContext(context));

    public static byte[] CanonicalDownloadGrantContext(
        SuiteDownloadGrantContext context)
    {
        ValidateDownloadGrantContext(context);
        return CanonicalBytes(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", context.SchemaVersion);
            writer.WriteString("productId", context.ProductId);
            writer.WriteString("licenseId", context.LicenseId);
            writer.WriteString("deviceId", context.DeviceId);
            writer.WriteString("sessionId", context.SessionId);
            writer.WriteString("action", context.Action);
            writer.WriteString("catalogIdentity", context.CatalogIdentity);
            writer.WriteString("itemId", context.ItemId);
            writer.WriteString("artifactId", context.ArtifactId);
            writer.WriteNumber("artifactVersion", context.ArtifactVersion);
            writer.WriteString("manifestIdentity", context.ManifestIdentity);
            writer.WriteString("descriptorHash", context.DescriptorHash);
            writer.WriteNumber("offset", context.Offset);
            writer.WriteString("sourceETag", context.SourceETag);
            writer.WriteString("sourceLastModified", context.SourceLastModified);
            writer.WriteEndObject();
        });
    }

    public static string DownloadGrantContextHash(
        SuiteDownloadGrantContext context)
        => HashCanonical(CanonicalDownloadGrantContext(context));

    public static byte[] CanonicalArtifactDescriptor(
        SuiteWireArtifactDescriptor descriptor)
    {
        ValidateDescriptor(descriptor);
        return CanonicalBytes(writer => WriteDescriptor(writer, descriptor));
    }

    public static string DescriptorHash(string itemId,
        SuiteWireArtifactDescriptor descriptor)
    {
        RequireCanonicalHex(itemId, "ItemId", 32);
        ValidateDescriptor(descriptor);
        return HashCanonical(CanonicalBytes(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion",
                SuiteOnlineLicenseProtocol.SchemaVersion);
            writer.WriteString("productId",
                SuiteOnlineLicenseProtocol.ProductId);
            writer.WriteString("itemId", itemId);
            writer.WriteString("artifactId", descriptor.ArtifactId);
            writer.WriteNumber("artifactVersion", descriptor.ArtifactVersion);
            writer.WriteString("safeFileName", descriptor.SafeFileName);
            writer.WriteString("fileExtension", descriptor.FileExtension);
            writer.WriteString("extractPolicy", descriptor.ExtractPolicy);
            writer.WriteString("manifestIdentity", descriptor.ManifestIdentity);
            writer.WriteEndObject();
        }));
    }

    public static byte[] CanonicalCatalogPageAssertion(
        SuiteCatalogPageAssertion assertion)
    {
        ValidateCatalogPageAssertionShape(assertion);
        return CanonicalBytes(writer =>
        {
            writer.WriteStartObject();
            WriteCommonAssertionFields(writer, assertion.SchemaVersion,
                assertion.Kind, assertion.ProductId, assertion.LicenseId,
                assertion.DeviceId, assertion.SessionId, assertion.Action,
                assertion.ContextHash, assertion.ChallengeId, assertion.Status,
                assertion.ServerTimeUnixSeconds, assertion.ExpiresAtUnixSeconds);
            writer.WriteString("catalogIdentity", assertion.CatalogIdentity);
            writer.WriteNumber("catalogSequence", assertion.CatalogSequence);
            writer.WritePropertyName("items");
            writer.WriteStartArray();
            foreach (var item in assertion.Items)
            {
                writer.WriteStartObject();
                writer.WriteString("itemId", item.ItemId);
                writer.WriteString("availability", item.Availability);
                if (item.Descriptor is null)
                    writer.WriteNull("descriptor");
                else
                {
                    writer.WritePropertyName("descriptor");
                    WriteDescriptor(writer, item.Descriptor);
                }
                if (item.ReasonCode is null)
                    writer.WriteNull("reasonCode");
                else
                    writer.WriteString("reasonCode", item.ReasonCode);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            if (assertion.NextCursor is null)
                writer.WriteNull("nextCursor");
            else
                writer.WriteString("nextCursor", assertion.NextCursor);
            writer.WriteEndObject();
        });
    }

    public static byte[] CanonicalDownloadGrantAssertion(
        SuiteDownloadGrantAssertion assertion)
    {
        ValidateDownloadGrantAssertionShape(assertion);
        return CanonicalBytes(writer =>
        {
            writer.WriteStartObject();
            WriteCommonAssertionFields(writer, assertion.SchemaVersion,
                assertion.Kind, assertion.ProductId, assertion.LicenseId,
                assertion.DeviceId, assertion.SessionId, assertion.Action,
                assertion.ContextHash, assertion.ChallengeId, assertion.Status,
                assertion.ServerTimeUnixSeconds, assertion.ExpiresAtUnixSeconds);
            writer.WriteString("catalogIdentity", assertion.CatalogIdentity);
            writer.WriteString("itemId", assertion.ItemId);
            writer.WriteString("artifactId", assertion.ArtifactId);
            writer.WriteNumber("artifactVersion", assertion.ArtifactVersion);
            writer.WriteString("manifestIdentity", assertion.ManifestIdentity);
            writer.WriteString("descriptorHash", assertion.DescriptorHash);
            writer.WriteNumber("rangeStart", assertion.RangeStart);
            writer.WriteString("grantId", assertion.GrantId);
            writer.WriteString("contentPath", assertion.ContentPath);
            writer.WriteString("bearerToken", assertion.BearerToken);
            writer.WriteEndObject();
        });
    }

    public static byte[] BuildCatalogPageAssertionSigningMessage(
        SuiteCatalogPageAssertion assertion)
        => PrefixDomain(CatalogPageAssertionDomain,
            CanonicalCatalogPageAssertion(assertion));

    public static byte[] BuildDownloadGrantAssertionSigningMessage(
        SuiteDownloadGrantAssertion assertion)
        => PrefixDomain(DownloadGrantAssertionDomain,
            CanonicalDownloadGrantAssertion(assertion));

    public static SuiteCatalogPageAssertion ParseCatalogPageAssertion(
        ReadOnlySpan<byte> utf8,
        ReadOnlySpan<byte> onlineAssertionSpki,
        string onlineAssertionKeyId,
        SuiteCatalogPageContext context,
        string contextHash,
        string challengeId,
        long nowUnixSeconds)
    {
        var assertion = ParseSignedAssertion<SuiteCatalogPageAssertion>(
            utf8, onlineAssertionSpki, onlineAssertionKeyId,
            CatalogPageAssertionKind, CatalogPageAssertionDomain,
            CanonicalCatalogPageAssertion);
        ValidateFreshAssertion(assertion.ServerTimeUnixSeconds,
            assertion.ExpiresAtUnixSeconds, nowUnixSeconds);
        if (!string.Equals(assertion.ProductId, context.ProductId,
                StringComparison.Ordinal)
            || !string.Equals(assertion.LicenseId, context.LicenseId,
                StringComparison.Ordinal)
            || !FixedHexEquals(assertion.DeviceId, context.DeviceId)
            || !FixedHexEquals(assertion.SessionId, context.SessionId)
            || !string.Equals(assertion.Action, CatalogAction,
                StringComparison.Ordinal)
            || !FixedHexEquals(assertion.ContextHash, contextHash)
            || !FixedHexEquals(assertion.ChallengeId, challengeId))
            throw new SecurityException(
                "A pagina assinada nao pertence a esta leitura de catalogo.");
        return assertion;
    }

    public static SuiteDownloadGrantAssertion ParseDownloadGrantAssertion(
        ReadOnlySpan<byte> utf8,
        ReadOnlySpan<byte> onlineAssertionSpki,
        string onlineAssertionKeyId,
        SuiteDownloadGrantContext context,
        string contextHash,
        string challengeId,
        long nowUnixSeconds)
    {
        var assertion = ParseSignedAssertion<SuiteDownloadGrantAssertion>(
            utf8, onlineAssertionSpki, onlineAssertionKeyId,
            DownloadGrantAssertionKind, DownloadGrantAssertionDomain,
            CanonicalDownloadGrantAssertion);
        ValidateFreshAssertion(assertion.ServerTimeUnixSeconds,
            assertion.ExpiresAtUnixSeconds, nowUnixSeconds);
        if (!string.Equals(assertion.ProductId, context.ProductId,
                StringComparison.Ordinal)
            || !string.Equals(assertion.LicenseId, context.LicenseId,
                StringComparison.Ordinal)
            || !FixedHexEquals(assertion.DeviceId, context.DeviceId)
            || !FixedHexEquals(assertion.SessionId, context.SessionId)
            || !string.Equals(assertion.Action, DownloadAction,
                StringComparison.Ordinal)
            || !FixedHexEquals(assertion.ContextHash, contextHash)
            || !FixedHexEquals(assertion.ChallengeId, challengeId)
            || !FixedHexEquals(assertion.CatalogIdentity,
                context.CatalogIdentity)
            || !string.Equals(assertion.ItemId, context.ItemId,
                StringComparison.Ordinal)
            || !string.Equals(assertion.ArtifactId, context.ArtifactId,
                StringComparison.Ordinal)
            || assertion.ArtifactVersion != context.ArtifactVersion
            || !FixedHexEquals(assertion.ManifestIdentity,
                context.ManifestIdentity)
            || !FixedHexEquals(assertion.DescriptorHash,
                context.DescriptorHash)
            || assertion.RangeStart != context.Offset)
            throw new SecurityException(
                "O grant assinado nao pertence a este artefato.");
        return assertion;
    }

    public static CatalogArtifactDescriptor ToCatalogDescriptor(
        SuiteWireArtifactDescriptor descriptor)
    {
        ValidateDescriptor(descriptor);
        return new CatalogArtifactDescriptor
        {
            ArtifactId = descriptor.ArtifactId,
            ArtifactVersion = descriptor.ArtifactVersion,
            ContentLength = 0,
            Sha256 = new string('0', 64),
            SafeFileName = descriptor.SafeFileName,
            FileExtension = descriptor.FileExtension,
            ExtractPolicy = descriptor.ExtractPolicy switch
            {
                "NONE" => CatalogExtractPolicy.None,
                "EXTRACT_ARCHIVE" => CatalogExtractPolicy.ExtractArchive,
                _ => throw new SecurityException(
                    "A politica de extracao do artefato e invalida.")
            },
            ManifestIdentity = descriptor.ManifestIdentity
        };
    }

    public static SuiteWireArtifactDescriptor ToWireDescriptor(
        CatalogArtifactDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var wire = new SuiteWireArtifactDescriptor(
            descriptor.ArtifactId,
            descriptor.ArtifactVersion,
            descriptor.SafeFileName,
            descriptor.FileExtension,
            descriptor.ExtractPolicy switch
            {
                CatalogExtractPolicy.None => "NONE",
                CatalogExtractPolicy.ExtractArchive => "EXTRACT_ARCHIVE",
                _ => throw new SecurityException(
                    "A politica de extracao do artefato e invalida.")
            },
            descriptor.ManifestIdentity);
        ValidateDescriptor(wire);
        return wire;
    }

    public static bool IsCanonicalCursor(string? cursor, bool allowEmpty)
    {
        if (cursor is null || cursor.Length > 256) return false;
        if (allowEmpty && cursor.Length == 0) return true;
        return cursor.Length >= 1 && cursor.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    }

    private static void ValidateCatalogPageContext(
        SuiteCatalogPageContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ValidateCommonContext(context.SchemaVersion, context.ProductId,
            context.LicenseId, context.DeviceId, context.SessionId,
            context.Action, CatalogAction);
        if (!IsCanonicalCursor(context.Cursor, allowEmpty: true)
            || context.PageSize is < 1 or > MaximumPageSize)
            throw new SecurityException("A pagina solicitada nao esta canonica.");
    }

    private static void ValidateDownloadGrantContext(
        SuiteDownloadGrantContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ValidateCommonContext(context.SchemaVersion, context.ProductId,
            context.LicenseId, context.DeviceId, context.SessionId,
            context.Action, DownloadAction);
        RequireCanonicalHex(context.CatalogIdentity, "CatalogIdentity", 64);
        RequireCanonicalHex(context.ItemId, "ItemId", 32);
        RequireCanonicalHex(context.ArtifactId, "ArtifactId", 32);
        RequireCanonicalHex(context.ManifestIdentity, "ManifestIdentity", 64);
        RequireCanonicalHex(context.DescriptorHash, "DescriptorHash", 64);
        if (context.ArtifactVersion <= 0 || context.Offset < 0)
            throw new SecurityException("O contexto do download e invalido.");
    }

    private static void ValidateCommonContext(int schemaVersion,
        string productId, string licenseId, string deviceId, string sessionId,
        string action, string expectedAction)
    {
        if (schemaVersion != SuiteOnlineLicenseProtocol.SchemaVersion
            || !string.Equals(productId, SuiteOnlineLicenseProtocol.ProductId,
                StringComparison.Ordinal)
            || !string.Equals(action, expectedAction, StringComparison.Ordinal))
            throw new SecurityException("O contexto de conteudo possui tipo invalido.");
        RequireCanonicalIdentifier(licenseId, "LicenseId", 6, 64);
        RequireCanonicalHex(deviceId, "DeviceId", 64);
        RequireCanonicalHex(sessionId, "SessionId", 64);
    }

    private static void ValidateCatalogPageAssertionShape(
        SuiteCatalogPageAssertion assertion)
    {
        ArgumentNullException.ThrowIfNull(assertion);
        if (assertion.Items is null || assertion.Items.Count > MaximumPageSize
            || assertion.Items.Any(item => item is null))
            throw new SecurityException("A pagina do catalogo possui itens invalidos.");
        ValidateCommonAssertion(assertion.SchemaVersion, assertion.Kind,
            CatalogPageAssertionKind, assertion.ProductId, assertion.LicenseId,
            assertion.DeviceId, assertion.SessionId, assertion.Action,
            CatalogAction, assertion.ContextHash, assertion.ChallengeId,
            assertion.Status, "AUTHORIZED", assertion.ServerTimeUnixSeconds,
            assertion.ExpiresAtUnixSeconds);
        RequireCanonicalHex(assertion.CatalogIdentity, "CatalogIdentity", 64);
        if (assertion.CatalogSequence <= 0
            || assertion.NextCursor is not null
            && !IsCanonicalCursor(assertion.NextCursor, allowEmpty: false))
            throw new SecurityException("O snapshot do catalogo e invalido.");
        foreach (var item in assertion.Items)
        {
            RequireCanonicalHex(item.ItemId, "ItemId", 32);
            if (string.Equals(item.Availability, ReadyAvailability,
                    StringComparison.Ordinal))
            {
                if (item.Descriptor is null || item.ReasonCode is not null)
                    throw new SecurityException(
                        "Um item READY exige somente o descritor.");
                ValidateDescriptor(item.Descriptor);
            }
            else if (string.Equals(item.Availability,
                         MaintenanceAvailability,
                         StringComparison.Ordinal))
            {
                if (item.Descriptor is not null
                    || !string.Equals(item.ReasonCode,
                        MaintenanceReasonCode, StringComparison.Ordinal))
                    throw new SecurityException(
                        "Um item em manutencao possui forma invalida.");
            }
            else
            {
                throw new SecurityException(
                    "A disponibilidade do item e invalida.");
            }
        }
    }

    private static void ValidateDownloadGrantAssertionShape(
        SuiteDownloadGrantAssertion assertion)
    {
        ArgumentNullException.ThrowIfNull(assertion);
        ValidateCommonAssertion(assertion.SchemaVersion, assertion.Kind,
            DownloadGrantAssertionKind, assertion.ProductId, assertion.LicenseId,
            assertion.DeviceId, assertion.SessionId, assertion.Action,
            DownloadAction, assertion.ContextHash, assertion.ChallengeId,
            assertion.Status, "GRANTED", assertion.ServerTimeUnixSeconds,
            assertion.ExpiresAtUnixSeconds);
        RequireCanonicalHex(assertion.CatalogIdentity, "CatalogIdentity", 64);
        RequireCanonicalHex(assertion.ItemId, "ItemId", 32);
        RequireCanonicalHex(assertion.ArtifactId, "ArtifactId", 32);
        RequireCanonicalHex(assertion.ManifestIdentity, "ManifestIdentity", 64);
        RequireCanonicalHex(assertion.DescriptorHash, "DescriptorHash", 64);
        RequireCanonicalHex(assertion.GrantId, "GrantId", 64);
        if (assertion.ArtifactVersion <= 0 || assertion.RangeStart < 0)
            throw new SecurityException("O grant de download e invalido.");
        var expectedPath = ArtifactRoutePrefix + assertion.GrantId;
        if (!string.Equals(assertion.ContentPath, expectedPath,
                StringComparison.Ordinal))
            throw new SecurityException("O caminho do grant e invalido.");
        if (!IsBearerToken(assertion.BearerToken))
            throw new SecurityException("O bearer do grant e invalido.");
    }

    private static void ValidateCommonAssertion(int schemaVersion, string kind,
        string expectedKind, string productId, string licenseId,
        string deviceId, string sessionId, string action, string expectedAction,
        string contextHash, string challengeId, string status,
        string expectedStatus, long serverTimeUnixSeconds,
        long expiresAtUnixSeconds)
    {
        if (schemaVersion != SuiteOnlineLicenseProtocol.SchemaVersion
            || !string.Equals(kind, expectedKind, StringComparison.Ordinal)
            || !string.Equals(productId, SuiteOnlineLicenseProtocol.ProductId,
                StringComparison.Ordinal)
            || !string.Equals(action, expectedAction, StringComparison.Ordinal)
            || !string.Equals(status, expectedStatus, StringComparison.Ordinal))
            throw new SecurityException("A assertion de conteudo possui tipo invalido.");
        RequireCanonicalIdentifier(licenseId, "LicenseId", 6, 64);
        RequireCanonicalHex(deviceId, "DeviceId", 64);
        RequireCanonicalHex(sessionId, "SessionId", 64);
        RequireCanonicalHex(contextHash, "ContextHash", 64);
        RequireCanonicalHex(challengeId, "ChallengeId", 64);
        SuiteOnlineLicenseProtocol.ValidateUnixTimeSeconds(
            serverTimeUnixSeconds, "O horario da assertion de conteudo");
        SuiteOnlineLicenseProtocol.ValidateUnixTimeSeconds(
            expiresAtUnixSeconds, "A expiracao da assertion de conteudo");
        if (expiresAtUnixSeconds - serverTimeUnixSeconds is < 1 or > 300)
            throw new SecurityException("A validade da assertion de conteudo e invalida.");
    }

    private static void ValidateDescriptor(SuiteWireArtifactDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        RequireCanonicalHex(descriptor.ArtifactId, "ArtifactId", 32);
        RequireCanonicalHex(descriptor.ManifestIdentity, "ManifestIdentity", 64);
        if (descriptor.ArtifactVersion <= 0
            || descriptor.ExtractPolicy is not ("NONE" or "EXTRACT_ARCHIVE")
            || !IsSafeExtension(descriptor.FileExtension)
            || !IsSafeFileName(descriptor.SafeFileName,
                descriptor.FileExtension))
            throw new SecurityException("O descritor do artefato e invalido.");
    }

    private static void WriteDescriptor(Utf8JsonWriter writer,
        SuiteWireArtifactDescriptor descriptor)
    {
        ValidateDescriptor(descriptor);
        writer.WriteStartObject();
        writer.WriteString("artifactId", descriptor.ArtifactId);
        writer.WriteNumber("artifactVersion", descriptor.ArtifactVersion);
        writer.WriteString("safeFileName", descriptor.SafeFileName);
        writer.WriteString("fileExtension", descriptor.FileExtension);
        writer.WriteString("extractPolicy", descriptor.ExtractPolicy);
        writer.WriteString("manifestIdentity", descriptor.ManifestIdentity);
        writer.WriteEndObject();
    }

    private static void WriteCommonAssertionFields(Utf8JsonWriter writer,
        int schemaVersion, string kind, string productId, string licenseId,
        string deviceId, string sessionId, string action, string contextHash,
        string challengeId, string status, long serverTimeUnixSeconds,
        long expiresAtUnixSeconds)
    {
        writer.WriteNumber("schemaVersion", schemaVersion);
        writer.WriteString("kind", kind);
        writer.WriteString("productId", productId);
        writer.WriteString("licenseId", licenseId);
        writer.WriteString("deviceId", deviceId);
        writer.WriteString("sessionId", sessionId);
        writer.WriteString("action", action);
        writer.WriteString("contextHash", contextHash);
        writer.WriteString("challengeId", challengeId);
        writer.WriteString("status", status);
        writer.WriteNumber("serverTimeUnixSeconds", serverTimeUnixSeconds);
        writer.WriteNumber("expiresAtUnixSeconds", expiresAtUnixSeconds);
    }

    private static TAssertion ParseSignedAssertion<TAssertion>(
        ReadOnlySpan<byte> utf8, ReadOnlySpan<byte> onlineAssertionSpki,
        string onlineAssertionKeyId, string expectedKind,
        ReadOnlySpan<byte> domain, Func<TAssertion, byte[]> canonicalPayload)
    {
        var envelope = ParseStrict<SuiteSignedAssertionEnvelope>(utf8);
        var canonicalKeyId = RequireCanonicalHex(
            onlineAssertionKeyId, "OnlineAssertionKeyId", 64);
        if (envelope.SchemaVersion != SuiteOnlineLicenseProtocol.SchemaVersion
            || !string.Equals(envelope.Kind, expectedKind, StringComparison.Ordinal)
            || !string.Equals(envelope.Algorithm,
                SuiteOnlineLicenseProtocol.SigningAlgorithm, StringComparison.Ordinal)
            || !string.Equals(RequireCanonicalHex(envelope.KeyId, "KeyId", 64),
                canonicalKeyId, StringComparison.Ordinal))
            throw new SecurityException("O envelope de conteudo e invalido.");

        var payloadBytes = DecodeCanonicalBase64(
            envelope.Payload, "payload", 64,
            SuiteOnlineLicenseProtocol.MaximumBodyBytes);
        var signature = DecodeCanonicalBase64(
            envelope.Signature, "assinatura", 256, 512);
        byte[] canonical = [];
        byte[] message = [];
        try
        {
            var assertion = ParseStrict<TAssertion>(payloadBytes);
            canonical = canonicalPayload(assertion);
            if (!canonical.AsSpan().SequenceEqual(payloadBytes))
                throw new SecurityException(
                    "O payload de conteudo nao usa JSON canonico.");
            message = PrefixDomain(domain, canonical);
            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(onlineAssertionSpki, out var consumed);
            if (consumed != onlineAssertionSpki.Length
                || rsa.KeySize is < 2048 or > 4096)
                throw new SecurityException("A chave de assertions e invalida.");
            var canonicalSpki = rsa.ExportSubjectPublicKeyInfo();
            try
            {
                var actualKeyId = LowerHex(SHA256.HashData(canonicalSpki));
                if (!canonicalSpki.AsSpan().SequenceEqual(onlineAssertionSpki)
                    || !FixedHexEquals(actualKeyId, canonicalKeyId)
                    || !rsa.VerifyData(message, signature,
                        HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
                    throw new SecurityException(
                        "A assinatura da assertion de conteudo e invalida.");
            }
            finally { CryptographicOperations.ZeroMemory(canonicalSpki); }
            return assertion;
        }
        catch (CryptographicException exception)
        {
            throw new SecurityException(
                "Nao foi possivel validar a assertion de conteudo.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payloadBytes);
            CryptographicOperations.ZeroMemory(signature);
            if (canonical.Length != 0) CryptographicOperations.ZeroMemory(canonical);
            if (message.Length != 0) CryptographicOperations.ZeroMemory(message);
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
                MaxDepth = 16
            });
            RejectDuplicateProperties(document.RootElement);
            return JsonSerializer.Deserialize<T>(copy, WireJsonOptions)
                ?? throw new SecurityException("A resposta de conteudo esta vazia.");
        }
        catch (JsonException exception)
        {
            throw new SecurityException(
                "A resposta de conteudo possui JSON invalido.", exception);
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
                        "A resposta de conteudo contem campo duplicado.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                RejectDuplicateProperties(item);
        }
    }

    private static void ValidateFreshAssertion(long serverTimeUnixSeconds,
        long expiresAtUnixSeconds, long nowUnixSeconds)
    {
        SuiteOnlineLicenseProtocol.ValidateUnixTimeSeconds(
            nowUnixSeconds, "O horario local");
        var minimum = Math.Max(
            SuiteOnlineLicenseProtocol.MinimumUnixTimeSeconds,
            nowUnixSeconds - 300);
        var maximum = Math.Min(
            SuiteOnlineLicenseProtocol.MaximumUnixTimeSeconds,
            nowUnixSeconds + 300);
        if (serverTimeUnixSeconds < minimum || serverTimeUnixSeconds > maximum
            || expiresAtUnixSeconds <= nowUnixSeconds
            || expiresAtUnixSeconds - nowUnixSeconds > 300)
            throw new SecurityException(
                "A assertion de conteudo esta expirada ou fora da janela local.");
    }

    private static string HashCanonical(byte[] canonical)
    {
        try { return LowerHex(SHA256.HashData(canonical)); }
        finally { CryptographicOperations.ZeroMemory(canonical); }
    }

    private static byte[] PrefixDomain(ReadOnlySpan<byte> domain,
        byte[] canonical)
    {
        try
        {
            var output = new byte[checked(domain.Length + canonical.Length)];
            domain.CopyTo(output);
            canonical.CopyTo(output.AsSpan(domain.Length));
            return output;
        }
        finally { CryptographicOperations.ZeroMemory(canonical); }
    }

    private static byte[] DecodeCanonicalBase64(string? value, string label,
        int minimumBytes, int maximumBytes)
    {
        var encoded = value ?? string.Empty;
        if (encoded.Any(char.IsWhiteSpace))
            throw new SecurityException($"O {label} possui formato invalido.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(encoded); }
        catch (FormatException exception)
        {
            throw new SecurityException(
                $"O {label} possui formato invalido.", exception);
        }
        if (bytes.Length < minimumBytes || bytes.Length > maximumBytes
            || !string.Equals(Convert.ToBase64String(bytes), encoded,
                StringComparison.Ordinal))
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new SecurityException($"O {label} possui tamanho invalido.");
        }
        return bytes;
    }

    private static string RequireCanonicalIdentifier(string? value,
        string label, int minimum, int maximum)
    {
        var canonical = SuiteOnlineLicenseProtocol.RequireIdentifier(
            value, label, minimum, maximum);
        if (!string.Equals(canonical, value, StringComparison.Ordinal))
            throw new SecurityException($"{label} nao esta canonico.");
        return canonical;
    }

    private static string RequireCanonicalHex(string? value, string label,
        int length)
    {
        var canonical = SuiteOnlineLicenseProtocol.RequireHex(value, label, length);
        if (!string.Equals(canonical, value, StringComparison.Ordinal))
            throw new SecurityException($"{label} nao esta canonico.");
        return canonical;
    }

    private static bool FixedHexEquals(string? left, string? right)
    {
        if (left is null || right is null || left.Length != right.Length
            || left.Length is not (32 or 64)) return false;
        byte[] a;
        byte[] b;
        try
        {
            a = Convert.FromHexString(left);
            b = Convert.FromHexString(right);
        }
        catch (FormatException) { return false; }
        try { return CryptographicOperations.FixedTimeEquals(a, b); }
        finally
        {
            CryptographicOperations.ZeroMemory(a);
            CryptographicOperations.ZeroMemory(b);
        }
    }

    internal static bool IsBearerToken(string? token)
        => token is { Length: 43 }
           && token.All(character => char.IsAsciiLetterOrDigit(character)
               || character is '-' or '_');

    private static bool IsSafeExtension(string? extension)
        => extension is { Length: >= 2 and <= 11 }
           && extension[0] == '.'
           && extension.Skip(1).All(character => character is >= 'a' and <= 'z'
               or >= '0' and <= '9');

    private static bool IsSafeFileName(string? fileName, string extension)
    {
        if (fileName is not { Length: >= 1 and <= 180 }
            || Encoding.UTF8.GetByteCount(fileName) > 180
            || fileName is "." or ".."
            || !fileName.EndsWith(extension, StringComparison.Ordinal)
            || fileName.EndsWith(' ') || fileName.EndsWith('.')
            || fileName.Any(character => char.IsControl(character)
                || char.IsSurrogate(character)
                || character is '<' or '>' or '"' or '/' or '\\'
                    or '|' or '?' or '*' or ':'))
            return false;
        if (!string.Equals(Path.GetFileName(fileName), fileName,
                StringComparison.Ordinal))
            return false;

        var stem = fileName.Split('.', 2)[0];
        return !stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
               && !stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
               && !stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
               && !stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
               && !(stem.Length == 4
                    && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                        || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                    && stem[3] is >= '1' and <= '9');
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
