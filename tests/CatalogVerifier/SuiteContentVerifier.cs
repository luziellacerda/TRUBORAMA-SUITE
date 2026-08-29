using System.Net.Http.Headers;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using TurboBoxManager.Catalog;
using TurboBoxManager.Licensing;

namespace TurboBoxManager.CatalogVerifier;

internal static class SuiteContentVerifier
{
    private const long Now = 2_000_000_000;
    private static readonly string LicenseId = "TR-CONTENT-01";
    private static readonly string DeviceId = new('a', 64);
    private static readonly string SessionId = new('b', 64);
    private static readonly string CatalogIdentity = new('c', 64);
    private static readonly string ChallengeId = new('d', 64);
    private static readonly string ItemId = new('1', 32);

    internal static void Run()
    {
        VerifyGoldenVectors();
        VerifySignedAssertionsAndBindings();
        VerifyAtomicSnapshot();
        VerifyProductionCatalogCardinality();
        VerifySameOriginDownloadRequest();
        VerifySeparateContentAuthority();
    }

    internal static void VerifyAuthorityBase64(
        string envelopeBase64,
        string issuerSpkiBase64,
        string expectedSha256)
    {
        var envelope = Convert.FromBase64String(envelopeBase64);
        var issuer = Convert.FromBase64String(issuerSpkiBase64);
        try
        {
            _ = SuiteContentAuthorityConfigurationVerifier.VerifyPinnedEnvelope(
                envelope, issuer, expectedSha256, TimeProvider.System);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(envelope);
            CryptographicOperations.ZeroMemory(issuer);
        }
    }

    private static void VerifyGoldenVectors()
    {
        var context = new SuiteCatalogPageContext(
            1,
            SuiteOnlineLicenseProtocol.ProductId,
            LicenseId,
            DeviceId,
            SessionId,
            SuiteContentProtocol.CatalogAction,
            string.Empty,
            64);
        const string expectedCanonical =
            "{\"schemaVersion\":1,\"productId\":\"TURBORAMA_SUITE\","
            + "\"licenseId\":\"TR-CONTENT-01\",\"deviceId\":\""
            + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\","
            + "\"sessionId\":\""
            + "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\","
            + "\"action\":\"catalog.read\",\"cursor\":\"\",\"pageSize\":64}";
        var canonical = SuiteContentProtocol.CanonicalCatalogPageContext(context);
        try
        {
            Check(Encoding.UTF8.GetString(canonical) == expectedCanonical,
                "O contexto canonico de catalogo mudou.");
        }
        finally { CryptographicOperations.ZeroMemory(canonical); }
        Check(SuiteContentProtocol.CatalogPageContextHash(context)
                == "e6db87f5c399cde66cab3e8982664a5a253fca122a7197364cfd7e2e96f32de9",
            "O golden hash do contexto de catalogo mudou.");

        var descriptor = CreateWireDescriptor();
        Check(SuiteContentProtocol.DescriptorHash(ItemId, descriptor)
                == "2d10dac39f109fb95093e8a31feb2ae17f48e0a62fb1fae79b5e9ee455dbbc89",
            "O golden hash escopado do descritor mudou.");
        Check(SuiteContentProtocol.DescriptorHash(new string('5', 32), descriptor)
                != SuiteContentProtocol.DescriptorHash(ItemId, descriptor),
            "O hash do descritor precisa vincular o item publico.");
        ExpectSecurity(() => SuiteContentProtocol.CanonicalArtifactDescriptor(
                descriptor with { SafeFileName = "CON.zip" }),
            "Nome reservado do Windows precisa ser rejeitado.");
        ExpectSecurity(() => SuiteContentProtocol.CanonicalArtifactDescriptor(
                descriptor with
                {
                    SafeFileName = new string('\u00e9', 91) + ".zip"
                }),
            "SafeFileName precisa respeitar o teto de 180 bytes UTF-8.");
        Check(!SuiteContentProtocol.IsCanonicalCursor("YQ==", allowEmpty: false),
            "Cursor com padding nao e base64url canonico.");

        var maintenancePage = CreatePage(
            CatalogIdentity,
            7,
            [new SuiteAuthorizedArtifact(
                ItemId,
                SuiteContentProtocol.MaintenanceAvailability,
                null,
                SuiteContentProtocol.MaintenanceReasonCode)],
            null);
        var maintenanceCanonical =
            SuiteContentProtocol.CanonicalCatalogPageAssertion(maintenancePage);
        try
        {
            Check(Encoding.UTF8.GetString(maintenanceCanonical).Contains(
                    $"{{\"itemId\":\"{ItemId}\",\"availability\":\"MAINTENANCE\","
                    + "\"descriptor\":null,\"reasonCode\":"
                    + "\"CONTENT_TEMPORARILY_UNAVAILABLE\"}",
                    StringComparison.Ordinal),
                "A forma canonica do item em manutencao mudou.");
        }
        finally { CryptographicOperations.ZeroMemory(maintenanceCanonical); }
        var contentSigningMessage =
            SuiteContentProtocol.BuildCatalogPageAssertionSigningMessage(
                maintenancePage);
        try
        {
            Check(contentSigningMessage.AsSpan().StartsWith(
                    "TurboRamaSuiteContentAssertion/catalog-page/v1\0"u8),
                "Catalogo precisa usar dominio exclusivo da Content Authority.");
        }
        finally { CryptographicOperations.ZeroMemory(contentSigningMessage); }
        ExpectSecurity(() => SuiteContentProtocol.CanonicalCatalogPageAssertion(
                maintenancePage with
                {
                    Items =
                    [new SuiteAuthorizedArtifact(
                        ItemId,
                        SuiteContentProtocol.ReadyAvailability,
                        null,
                        null)]
                }),
            "READY sem descritor precisa ser rejeitado.");
        ExpectSecurity(() => SuiteContentProtocol.CanonicalCatalogPageAssertion(
                maintenancePage with
                {
                    Items =
                    [new SuiteAuthorizedArtifact(
                        ItemId,
                        SuiteContentProtocol.MaintenanceAvailability,
                        descriptor,
                        SuiteContentProtocol.MaintenanceReasonCode)]
                }),
            "MAINTENANCE nao pode carregar descritor.");

        var crossRuntimePage = new SuiteCatalogPageAssertion(
            1,
            SuiteContentProtocol.CatalogPageAssertionKind,
            SuiteOnlineLicenseProtocol.ProductId,
            "TR-000125",
            new string('4', 64),
            new string('1', 64),
            SuiteContentProtocol.CatalogAction,
            "45fd00059fc5b0ad2e5c42834d19948611fd37aa27ccea429893e2b662ff9651",
            new string('c', 64),
            "AUTHORIZED",
            1_800_000_000,
            1_800_000_060,
            new string('b', 64),
            1,
            [new SuiteAuthorizedArtifact(
                new string('1', 32),
                SuiteContentProtocol.MaintenanceAvailability,
                null,
                SuiteContentProtocol.MaintenanceReasonCode)],
            null);
        const string expectedPageCanonical =
            """{"schemaVersion":1,"kind":"TURBORAMA_SUITE_CATALOG_PAGE","productId":"TURBORAMA_SUITE","licenseId":"TR-000125","deviceId":"4444444444444444444444444444444444444444444444444444444444444444","sessionId":"1111111111111111111111111111111111111111111111111111111111111111","action":"catalog.read","contextHash":"45fd00059fc5b0ad2e5c42834d19948611fd37aa27ccea429893e2b662ff9651","challengeId":"cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc","status":"AUTHORIZED","serverTimeUnixSeconds":1800000000,"expiresAtUnixSeconds":1800000060,"catalogIdentity":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","catalogSequence":1,"items":[{"itemId":"11111111111111111111111111111111","availability":"MAINTENANCE","descriptor":null,"reasonCode":"CONTENT_TEMPORARILY_UNAVAILABLE"}],"nextCursor":null}""";
        var crossRuntimeCanonical =
            SuiteContentProtocol.CanonicalCatalogPageAssertion(crossRuntimePage);
        try
        {
            Check(Encoding.UTF8.GetString(crossRuntimeCanonical)
                    == expectedPageCanonical,
                "O JSON de catalogo divergiu do golden do servidor.");
            Check(LowerSha256(crossRuntimeCanonical)
                    == "5a20353458aa364bd0832efd8c6f594c02c48b238b49c7f1452ee72be980f236",
                "O SHA-256 do catalogo divergiu do golden do servidor.");
        }
        finally { CryptographicOperations.ZeroMemory(crossRuntimeCanonical); }
    }

    private static void VerifySignedAssertionsAndBindings()
    {
        using var contentSigner = RSA.Create(2048);
        using var unrelatedSigner = RSA.Create(2048);
        var contentSpki = contentSigner.ExportSubjectPublicKeyInfo();
        var unrelatedSpki = unrelatedSigner.ExportSubjectPublicKeyInfo();
        try
        {
            var contentKeyId = LowerSha256(contentSpki);
            var pageContext = new SuiteCatalogPageContext(
                1, SuiteOnlineLicenseProtocol.ProductId, LicenseId, DeviceId,
                SessionId, SuiteContentProtocol.CatalogAction, string.Empty, 64);
            var pageHash = SuiteContentProtocol.CatalogPageContextHash(pageContext);
            var page = new SuiteCatalogPageAssertion(
                1,
                SuiteContentProtocol.CatalogPageAssertionKind,
                SuiteOnlineLicenseProtocol.ProductId,
                LicenseId,
                DeviceId,
                SessionId,
                SuiteContentProtocol.CatalogAction,
                pageHash,
                ChallengeId,
                "AUTHORIZED",
                Now,
                Now + 60,
                CatalogIdentity,
                7,
                [new SuiteAuthorizedArtifact(
                    ItemId,
                    SuiteContentProtocol.ReadyAvailability,
                    CreateWireDescriptor(CatalogIdentity),
                    null)],
                null);
            var pageEnvelope = SignAssertion(
                contentSigner,
                contentKeyId,
                SuiteContentProtocol.CatalogPageAssertionKind,
                SuiteContentProtocol.CanonicalCatalogPageAssertion(page),
                SuiteContentProtocol.BuildCatalogPageAssertionSigningMessage(page));
            try
            {
                _ = SuiteContentProtocol.ParseCatalogPageAssertion(
                    pageEnvelope, contentSpki, contentKeyId, pageContext,
                    pageHash, ChallengeId, Now);
                ExpectSecurity(() => SuiteContentProtocol.ParseCatalogPageAssertion(
                        pageEnvelope,
                        unrelatedSpki,
                        LowerSha256(unrelatedSpki),
                        pageContext,
                        pageHash,
                        ChallengeId,
                        Now),
                    "Uma chave de licenciamento nao pode validar o catalogo.");
            }
            finally { CryptographicOperations.ZeroMemory(pageEnvelope); }

            var descriptor = CreateWireDescriptor(CatalogIdentity);
            var descriptorHash = SuiteContentProtocol.DescriptorHash(
                ItemId, descriptor);
            var grantContext = new SuiteDownloadGrantContext(
                1,
                SuiteOnlineLicenseProtocol.ProductId,
                LicenseId,
                DeviceId,
                SessionId,
                SuiteContentProtocol.DownloadAction,
                CatalogIdentity,
                ItemId,
                descriptor.ArtifactId,
                descriptor.ArtifactVersion,
                descriptor.ManifestIdentity,
                descriptorHash,
                4096);
            var grantHash = SuiteContentProtocol.DownloadGrantContextHash(
                grantContext);
            var grant = CreateGrant(grantHash, descriptorHash, descriptor);
            var grantEnvelope = SignAssertion(
                contentSigner,
                contentKeyId,
                SuiteContentProtocol.DownloadGrantAssertionKind,
                SuiteContentProtocol.CanonicalDownloadGrantAssertion(grant),
                SuiteContentProtocol.BuildDownloadGrantAssertionSigningMessage(grant));
            try
            {
                _ = SuiteContentProtocol.ParseDownloadGrantAssertion(
                    grantEnvelope, contentSpki, contentKeyId, grantContext,
                    grantHash, ChallengeId, Now);
                var wrongManifest = grantContext with
                {
                    ManifestIdentity = new string('5', 64)
                };
                ExpectSecurity(() => SuiteContentProtocol.ParseDownloadGrantAssertion(
                        grantEnvelope, contentSpki, contentKeyId, wrongManifest,
                        SuiteContentProtocol.DownloadGrantContextHash(wrongManifest),
                        ChallengeId, Now),
                    "O grant precisa vincular ManifestIdentity.");
            }
            finally { CryptographicOperations.ZeroMemory(grantEnvelope); }

            var absoluteGrant = grant with
            {
                ContentPath = "https://evil.invalid/file"
            };
            ExpectSecurity(
                () => SuiteContentProtocol.CanonicalDownloadGrantAssertion(
                    absoluteGrant),
                "URL absoluta precisa ser rejeitada no grant.");
            ExpectSecurity(
                () => SuiteContentProtocol.CanonicalDownloadGrantAssertion(
                    grant with { BearerToken = new string('A', 42) }),
                "Bearer precisa ter exatamente 256 bits em base64url.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(contentSpki);
            CryptographicOperations.ZeroMemory(unrelatedSpki);
        }
    }

    private static void VerifyAtomicSnapshot()
    {
        var secondItem = new string('2', 32);
        var expected = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            [ItemId] = true,
            [secondItem] = true
        };
        var first = CreatePage(
            CatalogIdentity, 7,
            [new SuiteAuthorizedArtifact(
                ItemId,
                SuiteContentProtocol.ReadyAvailability,
                CreateWireDescriptor(CatalogIdentity),
                null)],
            "bmV4dA");
        var second = CreatePage(
            CatalogIdentity, 7,
            [new SuiteAuthorizedArtifact(
                secondItem,
                SuiteContentProtocol.MaintenanceAvailability,
                null,
                SuiteContentProtocol.MaintenanceReasonCode)],
            null);

        var complete = new SuiteCatalogSnapshotAccumulator(expected);
        complete.Apply(first);
        complete.Apply(second);
        var completeSnapshot = complete.Complete();
        Check(completeSnapshot.Descriptors.Count == 1
              && completeSnapshot.MaintenanceItems.Count == 1,
            "O snapshot completo nao foi materializado atomicamente.");

        var partial = new SuiteCatalogSnapshotAccumulator(expected);
        partial.Apply(first);
        ExpectSecurity(() => partial.Complete(),
            "Um snapshot parcial precisa falhar fechado.");

        var duplicate = new SuiteCatalogSnapshotAccumulator(expected);
        duplicate.Apply(first);
        ExpectSecurity(() => duplicate.Apply(first),
            "Item duplicado precisa invalidar todo o snapshot.");

        var unknown = new SuiteCatalogSnapshotAccumulator(expected);
        ExpectSecurity(() => unknown.Apply(CreatePage(
                CatalogIdentity, 7,
                [new SuiteAuthorizedArtifact(
                    new string('9', 32),
                    SuiteContentProtocol.ReadyAvailability,
                    CreateWireDescriptor(CatalogIdentity),
                    null)],
                null)),
            "Item desconhecido precisa invalidar todo o snapshot.");

        var inconsistent = new SuiteCatalogSnapshotAccumulator(expected);
        inconsistent.Apply(first);
        ExpectSecurity(() => inconsistent.Apply(second with
            {
                CatalogIdentity = new string('e', 64)
            }),
            "Troca de identidade durante a paginacao precisa falhar.");

        var wrongManifest = new SuiteCatalogSnapshotAccumulator(
            new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                [ItemId] = true
            });
        ExpectSecurity(() => wrongManifest.Apply(CreatePage(
                CatalogIdentity,
                7,
                [new SuiteAuthorizedArtifact(
                    ItemId,
                    SuiteContentProtocol.ReadyAvailability,
                    CreateWireDescriptor(),
                    null)],
                null)),
            "Descritor de outro manifest precisa invalidar todo o snapshot.");
    }

    private static void VerifyProductionCatalogCardinality()
    {
        var manifestPath = Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "Catalog",
            "catalog.json");
        var publicCatalog = CatalogRepository.Load(manifestPath);
        Check(publicCatalog.ItemCount
              == SuiteContentProtocol.ExpectedCatalogItemCount,
            "O catalogo publico de producao precisa conter exatamente 850 IDs.");

        var descriptor = SuiteContentProtocol.ToCatalogDescriptor(
            CreateWireDescriptor(CatalogIdentity));
        var complete = publicCatalog.Items.ToDictionary(
            item => item.Id,
            _ => descriptor,
            StringComparer.Ordinal);
        Check(CatalogRepository.Load(manifestPath, complete).ItemCount
              == SuiteContentProtocol.ExpectedCatalogItemCount,
            "Os 850 descritores assinados nao foram materializados.");
        complete.Remove(publicCatalog.Items[^1].Id);
        ExpectInvalidData(() => CatalogRepository.Load(manifestPath, complete),
            "Um unico ID ausente precisa reprovar o catalogo inteiro.");
        var maintenanceId = publicCatalog.Items[^1].Id;
        var maintenance = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [maintenanceId] = SuiteContentProtocol.MaintenanceReasonCode
        };
        var withMaintenance = CatalogRepository.Load(
            manifestPath, complete, maintenance);
        var maintenanceItem = withMaintenance.FindById(maintenanceId)
            ?? throw new InvalidOperationException(
                "O item em manutencao nao foi materializado.");
        Check(maintenanceItem.IsMaintenance
              && !maintenanceItem.CanDownload
              && maintenanceItem.DownloadActionLabel == "EM MANUTENÇÃO",
            "Somente o item em manutencao deve ficar sem download.");
    }

    private static void VerifySameOriginDownloadRequest()
    {
        var descriptor = SuiteContentProtocol.ToCatalogDescriptor(
            CreateWireDescriptor(CatalogIdentity));
        var descriptorHash = SuiteContentProtocol.DescriptorHash(
            ItemId, CreateWireDescriptor(CatalogIdentity));
        var grant = CreateGrant(new string('6', 64), descriptorHash,
            CreateWireDescriptor(CatalogIdentity));
        var authority = new Uri("https://content.example.invalid/");

        using var resumed = SuiteContentClient.BuildDownloadRequest(
            authority,
            grant,
            descriptor,
            4096,
            new CatalogDownloadValidators());
        Check(resumed.RequestUri?.Host == authority.Host
              && resumed.RequestUri.Query.Length == 0
              && resumed.Headers.Authorization?.Scheme == "Bearer"
              && resumed.Headers.Authorization.Parameter?.Length == 43
              && resumed.Headers.Range?.Ranges.Single().From == 4096
              && resumed.Headers.IfRange?.ToString() == $"\"{descriptor.Sha256}\"",
            "A retomada precisa usar mesmo host, Bearer, Range e ETag assinado.");

        ExpectSecurity(() =>
        {
            using var _ = SuiteContentClient.BuildDownloadRequest(
                authority,
                grant,
                descriptor,
                4096,
                new CatalogDownloadValidators("\"wrong\""));
        }, "ETag local divergente precisa falhar antes da rede.");

        ExpectSecurity(() =>
        {
            using var _ = SuiteContentClient.BuildDownloadRequest(
                authority,
                grant with { ContentPath = "//evil.invalid/file" },
                descriptor,
                0,
                new CatalogDownloadValidators());
        }, "Caminho host-relative precisa falhar antes da rede.");
    }

    private static void VerifySeparateContentAuthority()
    {
        using var issuer = RSA.Create(2048);
        using var contentSigner = RSA.Create(2048);
        var issuerSpki = issuer.ExportSubjectPublicKeyInfo();
        var contentSpki = contentSigner.ExportSubjectPublicKeyInfo();
        try
        {
            var payload = new SuiteContentAuthorityPayload(
                1,
                SuiteContentAuthorityConfigurationVerifier.ConfigurationKind,
                SuiteOnlineLicenseProtocol.ProductId,
                "https://content.example.invalid/",
                SuiteContentAuthorityConfigurationVerifier.SignatureAlgorithm,
                LowerSha256(contentSpki),
                Convert.ToBase64String(contentSpki),
                new string('a', 64),
                new string('b', 64),
                Now - 60,
                Now + 3600);
            var envelope = SignAuthorityEnvelope(issuer, issuerSpki, payload);
            var hash = LowerSha256(envelope);
            try
            {
                var configuration =
                    SuiteContentAuthorityConfigurationVerifier.VerifyPinnedEnvelope(
                        envelope,
                        issuerSpki,
                        hash,
                        new FixedTimeProvider(
                            DateTimeOffset.FromUnixTimeSeconds(Now)));
                Check(configuration.ContentAssertionKeyId
                      == LowerSha256(contentSpki)
                      && configuration.TlsServerSpkiSha256Pins.Count == 2,
                    "A autoridade de conteudo nao preservou chave e pinset.");
            }
            finally { CryptographicOperations.ZeroMemory(envelope); }

            var issuerAsContent = payload with
            {
                ContentAssertionKeyId = LowerSha256(issuerSpki),
                ContentAssertionPublicKeySpki = Convert.ToBase64String(issuerSpki)
            };
            var invalidEnvelope = SignAuthorityEnvelope(
                issuer, issuerSpki, issuerAsContent);
            try
            {
                ExpectSecurity(() =>
                    SuiteContentAuthorityConfigurationVerifier.VerifyEnvelope(
                        invalidEnvelope,
                        issuerSpki,
                        new FixedTimeProvider(
                            DateTimeOffset.FromUnixTimeSeconds(Now))),
                    "Issuer e chave de assertion de conteudo precisam ser distintos.");
            }
            finally { CryptographicOperations.ZeroMemory(invalidEnvelope); }

            ExpectSecurity(() =>
                SuiteContentAuthorityConfigurationVerifier.CanonicalPayload(
                    payload with
                    {
                        TlsServerSpkiSha256Next =
                            payload.TlsServerSpkiSha256Current
                    }),
                "O pinset de conteudo nao pode repetir o pin atual.");
            ExpectSecurity(() =>
                SuiteContentAuthorityConfigurationVerifier.CanonicalPayload(
                    payload with
                    {
                        TlsServerSpkiSha256Next = new string('A', 64)
                    }),
                "Pin TLS em caixa alta nao e canonico.");
            ExpectSecurity(() =>
                SuiteContentAuthorityConfigurationVerifier.CanonicalPayload(
                    payload with
                    {
                        BaseUrl = "https://content.example.invalid/prefix/"
                    }),
                "A autoridade de conteudo nao pode usar prefixo de caminho.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(issuerSpki);
            CryptographicOperations.ZeroMemory(contentSpki);
        }
    }

    private static SuiteWireArtifactDescriptor CreateWireDescriptor(
        string? manifestIdentity = null) => new(
        new string('2', 32),
        7,
        123456,
        new string('3', 64),
        "game.zip",
        ".zip",
        "EXTRACT_ARCHIVE",
        manifestIdentity ?? new string('4', 64));

    private static SuiteCatalogPageAssertion CreatePage(
        string catalogIdentity,
        long sequence,
        List<SuiteAuthorizedArtifact> items,
        string? nextCursor) => new(
        1,
        SuiteContentProtocol.CatalogPageAssertionKind,
        SuiteOnlineLicenseProtocol.ProductId,
        LicenseId,
        DeviceId,
        SessionId,
        SuiteContentProtocol.CatalogAction,
        new string('5', 64),
        ChallengeId,
        "AUTHORIZED",
        Now,
        Now + 60,
        catalogIdentity,
        sequence,
        items,
        nextCursor);

    private static SuiteDownloadGrantAssertion CreateGrant(
        string contextHash,
        string descriptorHash,
        SuiteWireArtifactDescriptor descriptor) => new(
        1,
        SuiteContentProtocol.DownloadGrantAssertionKind,
        SuiteOnlineLicenseProtocol.ProductId,
        LicenseId,
        DeviceId,
        SessionId,
        SuiteContentProtocol.DownloadAction,
        contextHash,
        ChallengeId,
        "GRANTED",
        Now,
        Now + 60,
        CatalogIdentity,
        ItemId,
        descriptor.ArtifactId,
        descriptor.ArtifactVersion,
        descriptor.ManifestIdentity,
        descriptorHash,
        4096,
        new string('e', 64),
        SuiteContentProtocol.ArtifactRoutePrefix + new string('e', 64),
        new string('A', 43));

    private static byte[] SignAssertion(
        RSA signer,
        string keyId,
        string kind,
        byte[] canonical,
        byte[] message)
    {
        var signature = signer.SignData(
            message, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        try
        {
            return SuiteOnlineLicenseProtocol.SerializeSignedAssertionEnvelope(
                new SuiteSignedAssertionEnvelope(
                    1,
                    kind,
                    SuiteOnlineLicenseProtocol.SigningAlgorithm,
                    keyId,
                    Convert.ToBase64String(canonical),
                    Convert.ToBase64String(signature)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonical);
            CryptographicOperations.ZeroMemory(message);
            CryptographicOperations.ZeroMemory(signature);
        }
    }

    private static byte[] SignAuthorityEnvelope(
        RSA issuer,
        ReadOnlySpan<byte> issuerSpki,
        SuiteContentAuthorityPayload payload)
    {
        var canonical =
            SuiteContentAuthorityConfigurationVerifier.CanonicalPayload(payload);
        var message =
            SuiteContentAuthorityConfigurationVerifier.BuildSigningMessage(payload);
        var signature = issuer.SignData(
            message, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        try
        {
            return SuiteContentAuthorityConfigurationVerifier.SerializeEnvelope(
                new SuiteContentAuthorityEnvelope(
                    1,
                    SuiteContentAuthorityConfigurationVerifier.SignatureAlgorithm,
                    SuiteContentAuthorityConfigurationVerifier.KeyIdFromSpki(
                        issuerSpki),
                    Convert.ToBase64String(canonical),
                    Convert.ToBase64String(signature)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonical);
            CryptographicOperations.ZeroMemory(message);
            CryptographicOperations.ZeroMemory(signature);
        }
    }

    private static string LowerSha256(ReadOnlySpan<byte> bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void ExpectSecurity(Action action, string message)
    {
        try { action(); }
        catch (SecurityException) { return; }
        throw new InvalidOperationException(message);
    }

    private static void ExpectInvalidData(Action action, string message)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException(message);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
