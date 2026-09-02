using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TurboBoxManager.Licensing;

namespace TurboBoxManager.CatalogVerifier;

public static class SuiteDeviceInventoryVerifier
{
    private const string LicenseId = "TS-INVENTORY-FIXTURE-00000001";
    private const long Now = 1_800_000_000;

    private static readonly string DeviceId = new('a', 64);
    private static readonly string SessionId = new('b', 64);
    private static readonly string ChallengeId = new('c', 64);
    private static readonly string OtherHash = new('d', 64);
    private static readonly string Nonce = Convert.ToBase64String(
        Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray());
    private static readonly string FixedProofSignature = CreateFixedSignature();
    private static readonly JsonSerializerOptions PublisherWireJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    // These are protocol compatibility vectors, not values recomputed into the
    // expected side of an assertion. A canonical byte change must update them
    // intentionally in the client and server implementations together.
    private const string ExpectedMotherboardFingerprint =
        "e2b57c24e8e7ebf36d63e93f6f1021c3eb9990d95a8c50494fa6e2604749d283";
    private const string ExpectedCanonicalInventorySha256 =
        "3a60c9704cdc1f1ee8da929e4bedbcabe6fddfe053d470d27f29ab25557deec3";
    private const string ExpectedCanonicalStateSha256 =
        "ac3a2717355c268cb2150f06e04a0a708f7881f580a90a7cdfe903b88f5fe2df";
    private const string ExpectedInventoryDocumentHash =
        "4282fabba3c01c0fd12c8ba51309c76a6db022b832039016d7df6e25fca9c4f5";
    private const string ExpectedInventoryStateHash =
        "de55bd35fb40807e8bf0a54f74827946e4a7f48c6e61e0f59a49b8a3f1544717";
    private const string ExpectedChallengeRequestSha256 =
        "ecf8c37d6c468730a01a0ce68c274fef5d41ccbde0760a505a47bda7dfdcbacc";
    private const string ExpectedProofSigningMessageSha256 =
        "465f80c902e286a8af0a067c0ac8e24ae16c2bd4d1e95d1af72b3d2437798bfb";
    private const string ExpectedCanonicalProofSha256 =
        "6e355cd41a463b9e8990864c687ca11e5bba103fba61831aaac8a808e62d292d";
    private const string ExpectedChallengeAssertionSha256 =
        "beab807bddc94a88379a7862c528d5a58cdb9f97b355946a47d4d11f767615f2";
    private const string ExpectedChallengeAssertionMessageSha256 =
        "7ae0f0e8933a85936dbf1a9e369a6e738819daea2801077bc9a18ecc23850d69";
    private const string ExpectedResultAssertionSha256 =
        "7fc90ae16476b900ea7003b475a3b1d9c01b5e3943fd3ea1c6fa9ec0d8ab3ac6";
    private const string ExpectedResultAssertionMessageSha256 =
        "6d394b689b8dabb0c8afc6cce11181742a3152752ee7103b0ecc0eea4550c2b3";

    public static void Run() => RunAsync().GetAwaiter().GetResult();

    public static async Task RunAsync()
    {
        VerifyNormalizationAndIdentitySemantics();
        VerifyCanonicalVectors();
        VerifyDeviceProofAndTamperResistance();
        VerifyStrictSignedAssertionsAndBindings();
        await VerifyInMemoryPublicationStateAsync().ConfigureAwait(false);
        await VerifyProtectedPublicationStateAsync().ConfigureAwait(false);
        await VerifyPublisherIntegrationAsync().ConfigureAwait(false);
        await VerifyRuntimeInventoryIsolationAsync().ConfigureAwait(false);
        await VerifyWindowsCollectorSmokeAsync().ConfigureAwait(false);
    }

    private static void VerifyNormalizationAndIdentitySemantics()
    {
        Equal(1, SuiteMotherboardInventoryNormalizer.PlaceholderSetVersion,
            "placeholder-set protocol version");
        Equal(
            "Caf\u00e9 Tech",
            SuiteMotherboardInventoryNormalizer.NormalizeDisplay(
                "  Cafe\u0301\tTech\u0000", 128),
            "display normalization must use NFC and collapse whitespace");
        Equal(
            "GIGABYTE",
            SuiteMotherboardInventoryNormalizer.NormalizeIdentity(
                "  \uff27\uff49\uff47\uff41\uff42\uff59\uff54\uff45  ", 128),
            "identity normalization must use NFKC and invariant uppercase");
        Equal(
            "AB",
            SuiteMotherboardInventoryNormalizer.NormalizeDisplay(
                "A\u200d\u0001B", 128),
            "control and format characters must be removed");
        Equal(
            "",
            SuiteMotherboardInventoryNormalizer.NormalizeDisplay(
                new string('x', 129), 128),
            "over-limit text must become absent instead of being truncated");

        var placeholders = new[]
        {
            "To Be Filled By O.E.M.",
            "To Be Filled By O.E.M",
            "To Be Filled By OEM",
            "Default string",
            "System Product Name",
            "Unknown",
            "None",
            "Not Specified",
            "00000000",
            "FFFFFFFF"
        };
        foreach (var placeholder in placeholders)
        {
            Equal(
                "",
                SuiteMotherboardInventoryNormalizer.NormalizeHardwareDisplay(
                    placeholder, 128),
                $"placeholder-set v{SuiteMotherboardInventoryNormalizer.PlaceholderSetVersion}: "
                    + placeholder);
        }
        Equal(
            "Unknown board",
            SuiteMotherboardInventoryNormalizer.NormalizeHardwareDisplay(
                "Unknown board", 128),
            "placeholder matching must be exact after normalization");
        Equal(10, placeholders.Length,
            "the complete versioned hardware placeholder set must be covered");

        const string supplementaryUnicode = "Placa 🚀 Gamer";
        Equal(
            supplementaryUnicode,
            SuiteMotherboardInventoryNormalizer.NormalizeHardwareDisplay(
                "  Placa 🚀   Gamer  ", 128),
            "collector normalization must preserve valid supplementary Unicode");

        Equal(
            "",
            SuiteMotherboardInventoryNormalizer.NormalizeSerial(
                "00-00-00-00", 128),
            "all-zero serial must be absent");
        Equal(
            "",
            SuiteMotherboardInventoryNormalizer.NormalizeSerial(
                "FFFF-FFFF", 128),
            "all-F serial must be absent");
        Equal(
            "03560230-040f-0585-c906-a80700080009",
            SuiteMotherboardInventoryNormalizer.NormalizeUuid(
                "03560230-040F-0585-C906-A80700080009"),
            "UUID must use canonical lowercase D format");
        Equal(
            "",
            SuiteMotherboardInventoryNormalizer.NormalizeUuid(
                "00000000-0000-0000-0000-000000000000"),
            "zero UUID must be absent");
        Equal(
            "",
            SuiteMotherboardInventoryNormalizer.NormalizeUuid(
                "ffffffff-ffff-ffff-ffff-ffffffffffff"),
            "all-F UUID must be absent");

        var collected = FixtureCollectedInventory();
        var baseline = SuiteMotherboardInventoryNormalizer.ComputeFingerprint(collected);
        Equal(ExpectedMotherboardFingerprint, baseline,
            "motherboard identity vector");
        Equal(
            baseline,
            SuiteMotherboardInventoryNormalizer.ComputeFingerprint(collected with
            {
                BiosManufacturer = "",
                BiosVersion = "",
                OsName = "",
                OsVersion = "",
                Architecture = "ARM64",
                ClientVersion = "99.0.0.0"
            }),
            "BIOS, OS, architecture and client version must not affect motherboard identity");
        True(
            typeof(SuiteMotherboardInventory).GetProperties().All(property =>
                !property.Name.Contains("Cpu", StringComparison.OrdinalIgnoreCase)
                && !property.Name.Contains("Processor", StringComparison.OrdinalIgnoreCase)),
            "CPU must be absent from the motherboard inventory identity");
        NotEqual(
            baseline,
            SuiteMotherboardInventoryNormalizer.ComputeFingerprint(collected with
            {
                BaseboardSerial = "SN-000002"
            }),
            "valid board serial change must alter identity");
        NotEqual(
            baseline,
            SuiteMotherboardInventoryNormalizer.ComputeFingerprint(collected with
            {
                SystemUuid = "13560230-040f-0585-c906-a80700080009"
            }),
            "valid system UUID change must alter identity");
        NotEqual(
            baseline,
            SuiteMotherboardInventoryNormalizer.ComputeFingerprint(collected with
            {
                BaseboardProduct = "B650M AORUS ELITE"
            }),
            "board model change must alter identity");
        NotEqual(
            baseline,
            SuiteMotherboardInventoryNormalizer.ComputeFingerprint(collected with
            {
                SystemModel = "B650M AORUS ELITE"
            }),
            "system model change must alter identity");

        var wireInventory = FixtureWireInventory();
        SuiteDeviceInventoryProtocol.ValidateInventory(wireInventory with
        {
            BaseboardProduct = supplementaryUnicode,
            MotherboardFingerprint = SuiteMotherboardInventoryNormalizer
                .ComputeFingerprint(
                    wireInventory.BaseboardManufacturer,
                    supplementaryUnicode,
                    wireInventory.BaseboardVersion,
                    wireInventory.BaseboardSerial,
                    wireInventory.SystemManufacturer,
                    wireInventory.SystemModel,
                    wireInventory.SystemUuid)
        });
        SuiteDeviceInventoryProtocol.ValidateInventory(
            wireInventory with { Architecture = "ARM" });
        SuiteDeviceInventoryProtocol.ValidateInventory(
            wireInventory with { Architecture = "ARM64" });
        ExpectSecurity(
            () => SuiteDeviceInventoryProtocol.ValidateInventory(
                wireInventory with { Architecture = "Arm64" }),
            "architecture casing must remain canonical");
        ExpectSecurity(
            () => SuiteDeviceInventoryProtocol.ValidateInventory(
                wireInventory with { MotherboardFingerprint = OtherHash }),
            "inventory must reject a motherboard fingerprint divergent from its fields");
    }

    private static void VerifyCanonicalVectors()
    {
        var inventory = FixtureWireInventory();
        Equal(ExpectedMotherboardFingerprint, inventory.MotherboardFingerprint,
            "fixture motherboard fingerprint");

        var canonicalInventory = SuiteDeviceInventoryProtocol.CanonicalInventory(
            inventory);
        try
        {
            Equal(ExpectedCanonicalInventorySha256,
                LowerSha256(canonicalInventory), "canonical inventory hash vector");
        }
        finally { CryptographicOperations.ZeroMemory(canonicalInventory); }

        var canonicalState = SuiteDeviceInventoryProtocol.CanonicalInventoryState(
            inventory);
        try
        {
            Equal(ExpectedCanonicalStateSha256,
                LowerSha256(canonicalState), "canonical semantic-state vector");
        }
        finally { CryptographicOperations.ZeroMemory(canonicalState); }

        var inventoryHash = SuiteDeviceInventoryProtocol.InventoryHash(inventory);
        var stateHash = SuiteDeviceInventoryProtocol.InventoryStateHash(inventory);
        Equal(ExpectedInventoryDocumentHash, inventoryHash,
            "domain-separated inventory document vector");
        Equal(ExpectedInventoryStateHash, stateHash,
            "domain-separated inventory state vector");
        Equal(
            stateHash,
            SuiteDeviceInventoryProtocol.InventoryStateHash(inventory with
            {
                CollectedAtUnixSeconds = inventory.CollectedAtUnixSeconds + 1
            }),
            "collection timestamp alone must not reopen the publication gate");
        NotEqual(
            inventoryHash,
            SuiteDeviceInventoryProtocol.InventoryHash(inventory with
            {
                CollectedAtUnixSeconds = inventory.CollectedAtUnixSeconds + 1
            }),
            "signed document must bind the collection timestamp");

        var request = FixtureChallengeRequest(inventoryHash);
        var canonicalRequest = SuiteDeviceInventoryProtocol.CanonicalChallengeRequest(
            request);
        try
        {
            Equal(ExpectedChallengeRequestSha256,
                LowerSha256(canonicalRequest), "canonical challenge request vector");
            SequenceEqual(
                canonicalRequest,
                SuiteDeviceInventoryProtocol.SerializeChallengeRequest(request),
                "challenge request serialization must be canonical");
        }
        finally { CryptographicOperations.ZeroMemory(canonicalRequest); }

        var challenge = FixtureChallenge();
        var proofMessage = SuiteDeviceInventoryProtocol.BuildProofSigningMessage(
            challenge, LicenseId, DeviceId, SessionId, inventoryHash);
        try
        {
            Equal(ExpectedProofSigningMessageSha256,
                LowerSha256(proofMessage), "inventory proof message vector");
            True(proofMessage.AsSpan().StartsWith(
                    Encoding.ASCII.GetBytes(
                        SuiteDeviceInventoryProtocol.InventoryProofDomain)),
                "proof message must use its isolated domain");
        }
        finally { CryptographicOperations.ZeroMemory(proofMessage); }

        var proof = FixtureProof(inventory, inventoryHash, FixedProofSignature);
        var canonicalProof = SuiteDeviceInventoryProtocol.CanonicalProof(proof);
        try
        {
            Equal(ExpectedCanonicalProofSha256,
                LowerSha256(canonicalProof), "canonical proof vector");
            SequenceEqual(
                canonicalProof,
                SuiteDeviceInventoryProtocol.SerializeProof(proof),
                "proof serialization must be canonical");
        }
        finally { CryptographicOperations.ZeroMemory(canonicalProof); }

        var challengeAssertion = FixtureChallengeAssertion(inventoryHash);
        var canonicalChallengeAssertion =
            SuiteDeviceInventoryProtocol.CanonicalChallengeAssertion(
                challengeAssertion);
        var challengeAssertionMessage =
            SuiteDeviceInventoryProtocol.BuildChallengeAssertionSigningMessage(
                challengeAssertion);
        try
        {
            Equal(ExpectedChallengeAssertionSha256,
                LowerSha256(canonicalChallengeAssertion),
                "canonical challenge assertion vector");
            Equal(ExpectedChallengeAssertionMessageSha256,
                LowerSha256(challengeAssertionMessage),
                "challenge assertion signing-message vector");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonicalChallengeAssertion);
            CryptographicOperations.ZeroMemory(challengeAssertionMessage);
        }

        var resultAssertion = FixtureResultAssertion(inventoryHash);
        var canonicalResultAssertion =
            SuiteDeviceInventoryProtocol.CanonicalResultAssertion(resultAssertion);
        var resultAssertionMessage =
            SuiteDeviceInventoryProtocol.BuildResultAssertionSigningMessage(
                resultAssertion);
        try
        {
            Equal(ExpectedResultAssertionSha256,
                LowerSha256(canonicalResultAssertion),
                "canonical result assertion vector");
            Equal(ExpectedResultAssertionMessageSha256,
                LowerSha256(resultAssertionMessage),
                "result assertion signing-message vector");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonicalResultAssertion);
            CryptographicOperations.ZeroMemory(resultAssertionMessage);
        }
    }

    private static void VerifyDeviceProofAndTamperResistance()
    {
        using var deviceKey = RSA.Create(2048);
        var deviceSpki = deviceKey.ExportSubjectPublicKeyInfo();
        try
        {
            var deviceId = LowerSha256(deviceSpki);
            var inventory = FixtureWireInventory(deviceId);
            var inventoryHash = SuiteDeviceInventoryProtocol.InventoryHash(inventory);
            var challenge = FixtureChallenge();
            var message = SuiteDeviceInventoryProtocol.BuildProofSigningMessage(
                challenge, LicenseId, deviceId, SessionId, inventoryHash);
            var signature = deviceKey.SignData(
                message, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
            try
            {
                var proof = FixtureProof(
                    inventory, inventoryHash, Convert.ToBase64String(signature));
                True(SuiteDeviceInventoryProtocol.VerifyProof(
                        deviceSpki, challenge, proof),
                    "valid RSA-PSS device proof must verify");

                var alteredSignature = signature.ToArray();
                try
                {
                    alteredSignature[0] ^= 0x01;
                    True(!SuiteDeviceInventoryProtocol.VerifyProof(
                            deviceSpki,
                            challenge,
                            proof with
                            {
                                Signature = Convert.ToBase64String(alteredSignature)
                            }),
                        "tampered RSA-PSS signature must fail");
                }
                finally { CryptographicOperations.ZeroMemory(alteredSignature); }

                var changedInventoryWithStaleFingerprint = inventory with
                {
                    SystemModel = "A DIFFERENT MODEL"
                };
                ExpectSecurity(
                    () => SuiteDeviceInventoryProtocol.ValidateProof(
                        proof with { Inventory = changedInventoryWithStaleFingerprint }),
                    "inventory tamper without its document hash must fail");

                var changedInventory = changedInventoryWithStaleFingerprint with
                {
                    MotherboardFingerprint = SuiteMotherboardInventoryNormalizer.ComputeFingerprint(
                        changedInventoryWithStaleFingerprint.BaseboardManufacturer,
                        changedInventoryWithStaleFingerprint.BaseboardProduct,
                        changedInventoryWithStaleFingerprint.BaseboardVersion,
                        changedInventoryWithStaleFingerprint.BaseboardSerial,
                        changedInventoryWithStaleFingerprint.SystemManufacturer,
                        changedInventoryWithStaleFingerprint.SystemModel,
                        changedInventoryWithStaleFingerprint.SystemUuid)
                };
                var changedHash = SuiteDeviceInventoryProtocol.InventoryHash(
                    changedInventory);
                True(!SuiteDeviceInventoryProtocol.VerifyProof(
                        deviceSpki,
                        challenge,
                        proof with
                        {
                            Inventory = changedInventory,
                            InventoryHash = changedHash
                        }),
                    "rehashed inventory tamper without a new signature must fail");

                var replayedChallenge = challenge with
                {
                    Nonce = Convert.ToBase64String(
                        Enumerable.Repeat((byte)0x5a, 32).ToArray())
                };
                True(!SuiteDeviceInventoryProtocol.VerifyProof(
                        deviceSpki, replayedChallenge, proof),
                    "proof must be bound to the exact challenge nonce");

                ExpectSecurity(
                    () => SuiteDeviceInventoryProtocol.ValidateProof(proof with
                    {
                        LicenseId = "TS-OTHER-LICENSE"
                    }),
                    "proof license must match the embedded inventory");

                using var otherKey = RSA.Create(2048);
                var otherSpki = otherKey.ExportSubjectPublicKeyInfo();
                try
                {
                    ExpectSecurity(
                        () => _ = SuiteDeviceInventoryProtocol.VerifyProof(
                            otherSpki, challenge, proof),
                        "proof must be bound to the enrolled SPKI/deviceId");
                }
                finally { CryptographicOperations.ZeroMemory(otherSpki); }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(message);
                CryptographicOperations.ZeroMemory(signature);
            }
        }
        finally { CryptographicOperations.ZeroMemory(deviceSpki); }
    }

    private static void VerifyStrictSignedAssertionsAndBindings()
    {
        var inventoryHash = SuiteDeviceInventoryProtocol.InventoryHash(
            FixtureWireInventory());
        using var signer = new AssertionSigner();
        var signerSpki = signer.ExportSubjectPublicKeyInfo();
        try
        {
            var challengeAssertion = FixtureChallengeAssertion(inventoryHash);
            var challengeEnvelope = signer.SignChallenge(challengeAssertion);
            try
            {
                var parsed = SuiteDeviceInventoryProtocol.ParseChallengeAssertion(
                    challengeEnvelope,
                    signerSpki,
                    signer.KeyId,
                    LicenseId,
                    DeviceId,
                    SessionId,
                    inventoryHash,
                    Now + 1);
                Equal(ChallengeId, parsed.ChallengeId,
                    "valid challenge assertion binding");
                Equal(Nonce, parsed.Nonce, "valid challenge nonce");

                ExpectStrictJsonFailures(
                    challengeEnvelope,
                    bytes => ParseChallenge(
                        bytes, signerSpki, signer.KeyId, inventoryHash),
                    "challenge assertion envelope");
                ExpectStrictPayloadFailures(
                    SuiteDeviceInventoryProtocol.CanonicalChallengeAssertion(
                        challengeAssertion),
                    SuiteDeviceInventoryProtocol.ChallengeAssertionKind,
                    SuiteDeviceInventoryProtocol.ChallengeAssertionDomain,
                    signer,
                    bytes => ParseChallenge(
                        bytes, signerSpki, signer.KeyId, inventoryHash),
                    "challenge assertion payload");

                var tamperedPayload =
                    SuiteDeviceInventoryProtocol.CanonicalChallengeAssertion(
                        challengeAssertion with
                        {
                            ServerTimeUnixSeconds = Now + 1
                        });
                var tamperedEnvelope = ReplaceEnvelopePayload(
                    challengeEnvelope, tamperedPayload);
                try
                {
                    ExpectSecurity(
                        () => ParseChallenge(
                            tamperedEnvelope, signerSpki, signer.KeyId,
                            inventoryHash),
                        "challenge assertion payload tamper must fail");
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(tamperedPayload);
                    CryptographicOperations.ZeroMemory(tamperedEnvelope);
                }

                ExpectSecurity(
                    () => SuiteDeviceInventoryProtocol.ParseChallengeAssertion(
                        challengeEnvelope, signerSpki, signer.KeyId,
                        "TS-OTHER-LICENSE", DeviceId, SessionId,
                        inventoryHash, Now + 1),
                    "challenge assertion must not replay across licenses");
                ExpectSecurity(
                    () => SuiteDeviceInventoryProtocol.ParseChallengeAssertion(
                        challengeEnvelope, signerSpki, signer.KeyId,
                        LicenseId, new string('e', 64), SessionId,
                        inventoryHash, Now + 1),
                    "challenge assertion must not replay across devices");
                ExpectSecurity(
                    () => SuiteDeviceInventoryProtocol.ParseChallengeAssertion(
                        challengeEnvelope, signerSpki, signer.KeyId,
                        LicenseId, DeviceId, new string('e', 64),
                        inventoryHash, Now + 1),
                    "challenge assertion must not replay across sessions");
                ExpectSecurity(
                    () => SuiteDeviceInventoryProtocol.ParseChallengeAssertion(
                        challengeEnvelope, signerSpki, signer.KeyId,
                        LicenseId, DeviceId, SessionId, OtherHash, Now + 1),
                    "challenge assertion must bind inventory hash");
                ExpectSecurity(
                    () => SuiteDeviceInventoryProtocol.ParseChallengeAssertion(
                        challengeEnvelope, signerSpki, signer.KeyId,
                        LicenseId, DeviceId, SessionId, inventoryHash, Now + 60),
                    "expired challenge assertion must not replay");

                using var attacker = new AssertionSigner();
                var canonical = SuiteDeviceInventoryProtocol.CanonicalChallengeAssertion(
                    challengeAssertion with { ServerTimeUnixSeconds = Now + 1 });
                try
                {
                    var forged = attacker.SignRaw(
                        SuiteDeviceInventoryProtocol.ChallengeAssertionKind,
                        SuiteDeviceInventoryProtocol.ChallengeAssertionDomain,
                        canonical,
                        signer.KeyId);
                    try
                    {
                        ExpectSecurity(
                            () => ParseChallenge(
                                forged, signerSpki, signer.KeyId, inventoryHash),
                            "challenge assertion signed by another key must fail");
                    }
                    finally { CryptographicOperations.ZeroMemory(forged); }
                }
                finally { CryptographicOperations.ZeroMemory(canonical); }
            }
            finally { CryptographicOperations.ZeroMemory(challengeEnvelope); }

            var resultAssertion = FixtureResultAssertion(inventoryHash);
            var resultEnvelope = signer.SignResult(resultAssertion);
            try
            {
                var result = SuiteDeviceInventoryProtocol.ParseResultAssertion(
                    resultEnvelope,
                    signerSpki,
                    signer.KeyId,
                    LicenseId,
                    DeviceId,
                    SessionId,
                    inventoryHash,
                    ChallengeId,
                    Now + 1);
                Equal(SuiteDeviceInventoryProtocol.ResultStatus, result.Status,
                    "valid result assertion status");

                ExpectStrictJsonFailures(
                    resultEnvelope,
                    bytes => ParseResult(
                        bytes, signerSpki, signer.KeyId, inventoryHash),
                    "result assertion envelope");
                ExpectStrictPayloadFailures(
                    SuiteDeviceInventoryProtocol.CanonicalResultAssertion(
                        resultAssertion),
                    SuiteDeviceInventoryProtocol.ResultAssertionKind,
                    SuiteDeviceInventoryProtocol.ResultAssertionDomain,
                    signer,
                    bytes => ParseResult(
                        bytes, signerSpki, signer.KeyId, inventoryHash),
                    "result assertion payload");

                var tamperedPayload =
                    SuiteDeviceInventoryProtocol.CanonicalResultAssertion(
                        resultAssertion with
                        {
                            ServerTimeUnixSeconds = Now + 1
                        });
                var tamperedEnvelope = ReplaceEnvelopePayload(
                    resultEnvelope, tamperedPayload);
                try
                {
                    ExpectSecurity(
                        () => ParseResult(
                            tamperedEnvelope, signerSpki, signer.KeyId,
                            inventoryHash),
                        "result assertion payload tamper must fail");
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(tamperedPayload);
                    CryptographicOperations.ZeroMemory(tamperedEnvelope);
                }

                ExpectSecurity(
                    () => SuiteDeviceInventoryProtocol.ParseResultAssertion(
                        resultEnvelope, signerSpki, signer.KeyId,
                        LicenseId, DeviceId, SessionId, inventoryHash,
                        new string('e', 64), Now + 1),
                    "result assertion must bind the consumed challenge");
                ExpectSecurity(
                    () => SuiteDeviceInventoryProtocol.ParseResultAssertion(
                        resultEnvelope, signerSpki, signer.KeyId,
                        "TS-OTHER-LICENSE", DeviceId, SessionId, inventoryHash,
                        ChallengeId, Now + 1),
                    "result assertion must not replay across licenses");
                ExpectSecurity(
                    () => SuiteDeviceInventoryProtocol.ParseResultAssertion(
                        resultEnvelope, signerSpki, signer.KeyId,
                        LicenseId, new string('e', 64), SessionId, inventoryHash,
                        ChallengeId, Now + 1),
                    "result assertion must not replay across devices");
                ExpectSecurity(
                    () => SuiteDeviceInventoryProtocol.ParseResultAssertion(
                        resultEnvelope, signerSpki, signer.KeyId,
                        LicenseId, DeviceId, new string('e', 64), inventoryHash,
                        ChallengeId, Now + 1),
                    "result assertion must not replay across sessions");
                ExpectSecurity(
                    () => SuiteDeviceInventoryProtocol.ParseResultAssertion(
                        resultEnvelope, signerSpki, signer.KeyId,
                        LicenseId, DeviceId, SessionId, OtherHash,
                        ChallengeId, Now + 1),
                    "result assertion must bind inventory hash");
                ExpectSecurity(
                    () => SuiteDeviceInventoryProtocol.ParseResultAssertion(
                        resultEnvelope, signerSpki, signer.KeyId,
                        LicenseId, DeviceId, SessionId, inventoryHash,
                        ChallengeId, Now + 301),
                    "stale result assertion must not replay");
            }
            finally { CryptographicOperations.ZeroMemory(resultEnvelope); }
        }
        finally { CryptographicOperations.ZeroMemory(signerSpki); }
    }

    private static async Task VerifyInMemoryPublicationStateAsync()
    {
        var store = new InMemorySuiteInventoryPublicationStateStore();
        var key = new SuiteInventoryPublicationCacheKey(
            new string('1', 64),
            SuiteDeviceInventoryProtocol.SchemaVersion,
            SuiteDeviceInventoryProtocol.ProductId,
            LicenseId,
            DeviceId);
        True(await store.LoadAsync(key, CancellationToken.None) is null,
            "new in-memory publication cache must be empty");

        var accepted = new SuiteInventoryPublicationState(
            ExpectedInventoryStateHash,
            ExpectedInventoryDocumentHash,
            null,
            DateTimeOffset.FromUnixTimeSeconds(Now));
        True(await store.TrySaveAsync(key, accepted, CancellationToken.None),
            "accepted publication state must save");
        Equal(
            accepted,
            await store.LoadAsync(key, CancellationToken.None),
            "accepted publication state must round-trip");
        True(await store.TrySaveAsync(key, accepted, CancellationToken.None),
            "idempotent state save must succeed");

        var otherKey = key with { DeviceId = new string('2', 64) };
        True(await store.LoadAsync(otherKey, CancellationToken.None) is null,
            "publication state must be isolated by cache key");

        var unsupported = new SuiteInventoryPublicationState(
            null,
            null,
            DateTimeOffset.FromUnixTimeSeconds(Now + 3_600),
            null);
        True(await store.TrySaveAsync(key, unsupported, CancellationToken.None),
            "unsupported-server cooldown state must save");
        Equal(
            unsupported,
            await store.LoadAsync(key, CancellationToken.None),
            "unsupported-server state must replace accepted state");

        ExpectSecurity(
            () => _ = store.LoadAsync(
                key with { AuthorityKeyId = new string('A', 64) },
                CancellationToken.None),
            "noncanonical cache key must fail");
        ExpectSecurity(
            () => _ = store.TrySaveAsync(
                key,
                new SuiteInventoryPublicationState(
                    ExpectedInventoryStateHash, null, null,
                    DateTimeOffset.FromUnixTimeSeconds(Now)),
                CancellationToken.None),
            "partial accepted state must fail");
        ExpectSecurity(
            () => _ = store.TrySaveAsync(
                key,
                accepted with
                {
                    AcceptedAt = new DateTimeOffset(
                        2027, 1, 15, 12, 0, 0, TimeSpan.FromHours(1))
                },
                CancellationToken.None),
            "non-UTC cache timestamp must fail");

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await ExpectThrowsAsync<OperationCanceledException>(
            async () => _ = await store.LoadAsync(key, canceled.Token),
            "canceled cache load must fail").ConfigureAwait(false);
    }

    private static async Task VerifyPublisherIntegrationAsync()
    {
        await VerifyAcceptedPublisherAndCacheAsync().ConfigureAwait(false);
        await VerifyUnsupportedPublisherAsync().ConfigureAwait(false);
        await VerifyTransientBodyReadRetryAsync().ConfigureAwait(false);
        await VerifyPublisherExpectedFailuresAreOutcomesAsync().ConfigureAwait(false);
    }

    private static async Task VerifyProtectedPublicationStateAsync()
    {
        if (!OperatingSystem.IsWindows()) return;

        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Turborama",
            "Suite",
            "licensing-verifier-" + Guid.NewGuid().ToString("N"));
        var store = new SuiteInventoryPublicationStateStore(root);
        var key = new SuiteInventoryPublicationCacheKey(
            new string('1', 64),
            SuiteDeviceInventoryProtocol.SchemaVersion,
            SuiteDeviceInventoryProtocol.ProductId,
            LicenseId,
            DeviceId);
        var accepted = new SuiteInventoryPublicationState(
            ExpectedInventoryStateHash,
            ExpectedInventoryDocumentHash,
            null,
            DateTimeOffset.FromUnixTimeSeconds(Now));
        var unsupported = new SuiteInventoryPublicationState(
            null,
            null,
            DateTimeOffset.FromUnixTimeSeconds(Now + 3_600),
            null);

        try
        {
            True(await store.TrySaveAsync(key, accepted, CancellationToken.None),
                "DPAPI publication state must save");
            Equal(accepted,
                await store.LoadAsync(key, CancellationToken.None),
                "DPAPI publication state must round-trip for CurrentUser");
            True(await store.TrySaveAsync(key, unsupported, CancellationToken.None),
                "DPAPI publication state update must save a recoverable backup");

            var files = Directory.GetFiles(root, "*", SearchOption.TopDirectoryOnly);
            Equal(2, files.Length,
                "protected state update must retain primary and recoverable backup");
            foreach (var file in files)
            {
                var bytes = await File.ReadAllBytesAsync(file).ConfigureAwait(false);
                try
                {
                    True(!ContainsUtf8(bytes, LicenseId)
                         && !ContainsUtf8(bytes, DeviceId)
                         && !ContainsUtf8(bytes, ExpectedInventoryStateHash)
                         && !ContainsUtf8(bytes, ExpectedInventoryDocumentHash),
                        "DPAPI bytes must not expose cache keys or state hashes");
                    var name = Path.GetFileName(file);
                    True(!name.Contains(LicenseId, StringComparison.Ordinal)
                         && !name.Contains(DeviceId, StringComparison.Ordinal)
                         && !name.Contains(ExpectedInventoryStateHash,
                             StringComparison.Ordinal),
                        "protected state filename must not contain PII or clear hashes");
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }
            }

            var primary = files.Single(static path =>
                path.EndsWith(".state", StringComparison.OrdinalIgnoreCase));
            var backup = files.Single(static path =>
                path.EndsWith(".state.bak", StringComparison.OrdinalIgnoreCase));
            await File.WriteAllBytesAsync(
                    primary,
                    Encoding.UTF8.GetBytes("corrupted-primary"))
                .ConfigureAwait(false);
            Equal(accepted,
                await store.LoadAsync(key, CancellationToken.None),
                "corrupted primary must fail over to the last valid backup");

            await File.WriteAllBytesAsync(
                    backup,
                    Encoding.UTF8.GetBytes("corrupted-backup"))
                .ConfigureAwait(false);
            True(await store.LoadAsync(key, CancellationToken.None) is null,
                "corruption of both protected copies must fail open as a cache miss");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static bool ContainsUtf8(ReadOnlySpan<byte> bytes, string value)
    {
        var needle = Encoding.UTF8.GetBytes(value);
        try { return bytes.IndexOf(needle) >= 0; }
        finally { CryptographicOperations.ZeroMemory(needle); }
    }

    private static async Task VerifyAcceptedPublisherAndCacheAsync()
    {
        var time = TimeProvider.System;
        using var assertionSigner = new AssertionSigner();
        using var identity = new PublisherMachineIdentity();
        var authority = CreatePublisherAuthority(time, assertionSigner);
        var store = new InMemorySuiteInventoryPublicationStateStore();
        var handler = new InventoryAuthorityHandler(
            time, assertionSigner, identity.ExportSubjectPublicKeyInfo());
        using var client = SuiteLicenseClient.CreateForInventoryVerifier(
            authority,
            identity,
            handler,
            new FixedInventorySource(FixtureCollectedInventory()),
            store,
            time);
        var context = CreatePublisherContext(time, identity.DeviceId);

        var first = await client.PublishMotherboardInventoryAsync(
                context, CancellationToken.None)
            .ConfigureAwait(false);
        Equal(SuiteInventoryPublicationOutcome.Published, first,
            "valid signed inventory publication outcome");
        True(handler.ProofVerified,
            "fake authority must verify the device RSA-PSS proof before accepting");
        Equal(1, identity.InventorySignatureCount,
            "accepted publication must sign exactly one inventory proof");
        Equal(2, handler.RequestCount,
            "accepted publication must use only challenge and proof routes");
        True(context.IsAuthorized,
            "auxiliary publication must preserve its authorized context");

        var requestsAfterAcceptance = handler.RequestCount;
        var second = await client.PublishMotherboardInventoryAsync(
                context, CancellationToken.None)
            .ConfigureAwait(false);
        Equal(SuiteInventoryPublicationOutcome.AlreadyCurrent, second,
            "accepted semantic state must suppress duplicate publication");
        Equal(requestsAfterAcceptance, handler.RequestCount,
            "publication cache must suppress all duplicate network requests");
        Equal(1, identity.InventorySignatureCount,
            "cache hit must not ask the machine key to sign again");
        True(context.IsAuthorized,
            "cache hit must not mutate the authorized context");
    }

    private static async Task VerifyUnsupportedPublisherAsync()
    {
        foreach (var status in new[]
                 {
                     HttpStatusCode.NotFound,
                     HttpStatusCode.MethodNotAllowed,
                     HttpStatusCode.NotImplemented
                 })
        {
            var time = TimeProvider.System;
            using var assertionSigner = new AssertionSigner();
            using var identity = new PublisherMachineIdentity();
            var authority = CreatePublisherAuthority(time, assertionSigner);
            var store = new InMemorySuiteInventoryPublicationStateStore();
            var handler = new InventoryAuthorityHandler(
                time,
                assertionSigner,
                identity.ExportSubjectPublicKeyInfo(),
                unsupportedStatus: status);
            using var client = SuiteLicenseClient.CreateForInventoryVerifier(
                authority,
                identity,
                handler,
                new FixedInventorySource(FixtureCollectedInventory()),
                store,
                time);
            var context = CreatePublisherContext(time, identity.DeviceId);

            var outcome = await client.PublishMotherboardInventoryAsync(
                    context, CancellationToken.None)
                .ConfigureAwait(false);
            Equal(SuiteInventoryPublicationOutcome.Unsupported, outcome,
                $"old server status {(int)status} must disable only inventory");
            True(context.IsAuthorized,
                $"old server status {(int)status} must not revoke the session");
            Equal(1, handler.RequestCount,
                $"old server status {(int)status} must stop after one request");
            Equal(0, identity.InventorySignatureCount,
                $"old server status {(int)status} must not produce a proof");

            var cachedOutcome = await client.PublishMotherboardInventoryAsync(
                    context, CancellationToken.None)
                .ConfigureAwait(false);
            Equal(SuiteInventoryPublicationOutcome.Unsupported, cachedOutcome,
                $"old server status {(int)status} must enter unsupported cooldown");
            Equal(1, handler.RequestCount,
                $"unsupported cooldown for {(int)status} must suppress network retry");
            True(context.IsAuthorized,
                $"unsupported cooldown for {(int)status} must preserve the session");
        }

        foreach (var testCase in new[]
                 {
                     (HttpStatusCode.BadRequest, "UNSUPPORTED_OPERATION"),
                     (HttpStatusCode.UnprocessableEntity,
                         "UNSUPPORTED_SCHEMA_VERSION")
                 })
        {
            var time = TimeProvider.System;
            using var assertionSigner = new AssertionSigner();
            using var identity = new PublisherMachineIdentity();
            var authority = CreatePublisherAuthority(time, assertionSigner);
            var handler = new InventoryAuthorityHandler(
                time,
                assertionSigner,
                identity.ExportSubjectPublicKeyInfo(),
                unsupportedStatus: testCase.Item1,
                unsupportedErrorCode: testCase.Item2);
            using var client = SuiteLicenseClient.CreateForInventoryVerifier(
                authority,
                identity,
                handler,
                new FixedInventorySource(FixtureCollectedInventory()),
                new InMemorySuiteInventoryPublicationStateStore(),
                time);
            var context = CreatePublisherContext(time, identity.DeviceId);

            var outcome = await client.PublishMotherboardInventoryAsync(
                    context, CancellationToken.None)
                .ConfigureAwait(false);
            Equal(SuiteInventoryPublicationOutcome.Unsupported, outcome,
                $"canonical {testCase.Item2} must negotiate inventory as unsupported");
            Equal(1, handler.RequestCount,
                "canonical unsupported error must stop after its first request");
            True(context.IsAuthorized,
                "canonical unsupported error must preserve the active session");
        }

        {
            var time = TimeProvider.System;
            using var assertionSigner = new AssertionSigner();
            using var identity = new PublisherMachineIdentity();
            var authority = CreatePublisherAuthority(time, assertionSigner);
            var handler = new InventoryAuthorityHandler(
                time,
                assertionSigner,
                identity.ExportSubjectPublicKeyInfo(),
                unsupportedStatus: HttpStatusCode.Forbidden);
            using var client = SuiteLicenseClient.CreateForInventoryVerifier(
                authority,
                identity,
                handler,
                new FixedInventorySource(FixtureCollectedInventory()),
                new InMemorySuiteInventoryPublicationStateStore(),
                time);
            var context = CreatePublisherContext(time, identity.DeviceId);

            var outcome = await client.PublishMotherboardInventoryAsync(
                    context, CancellationToken.None)
                .ConfigureAwait(false);
            Equal(SuiteInventoryPublicationOutcome.Rejected, outcome,
                "an unrelated 4xx must be a rejection, never feature negotiation");
            Equal(1, handler.RequestCount,
                "unrelated 4xx rejection must not retry");
            True(context.IsAuthorized,
                "auxiliary 4xx rejection must not revoke the active session");
        }
    }

    private static async Task VerifyTransientBodyReadRetryAsync()
    {
        var time = TimeProvider.System;
        using var assertionSigner = new AssertionSigner();
        using var identity = new PublisherMachineIdentity();
        var authority = CreatePublisherAuthority(time, assertionSigner);
        var handler = new InventoryAuthorityHandler(
            time,
            assertionSigner,
            identity.ExportSubjectPublicKeyInfo(),
            failFirstResponseBody: true);
        using var client = SuiteLicenseClient.CreateForInventoryVerifier(
            authority,
            identity,
            handler,
            new FixedInventorySource(FixtureCollectedInventory()),
            new InMemorySuiteInventoryPublicationStateStore(),
            time);
        var context = CreatePublisherContext(time, identity.DeviceId);

        var outcome = await client.PublishMotherboardInventoryAsync(
                context, CancellationToken.None)
            .ConfigureAwait(false);
        Equal(SuiteInventoryPublicationOutcome.Published, outcome,
            "response-body transport failure must retry and then publish");
        Equal(3, handler.RequestCount,
            "body read retry must perform failed challenge, new challenge and proof");
        Equal(2, handler.ChallengeRequestCount,
            "body read retry must request a fresh challenge");
        Equal(1, handler.ProofRequestCount,
            "body read retry must submit only one proof");
        True(handler.ProofVerified,
            "proof after body read retry must remain cryptographically valid");
        True(context.IsAuthorized,
            "transient auxiliary retry must preserve the authorized context");
    }

    private static async Task VerifyPublisherExpectedFailuresAreOutcomesAsync()
    {
        var time = TimeProvider.System;
        using var assertionSigner = new AssertionSigner();
        var authority = CreatePublisherAuthority(time, assertionSigner);

        using (var identity = new PublisherMachineIdentity())
        {
            var handler = new InventoryAuthorityHandler(
                time, assertionSigner, identity.ExportSubjectPublicKeyInfo());
            using var client = SuiteLicenseClient.CreateForInventoryVerifier(
                authority,
                identity,
                handler,
                new ThrowingInventorySource(
                    new IOException("Synthetic inventory collection failure.")),
                new InMemorySuiteInventoryPublicationStateStore(),
                time);
            var context = CreatePublisherContext(time, identity.DeviceId);

            var outcome = await client.PublishMotherboardInventoryAsync(
                    context, CancellationToken.None)
                .ConfigureAwait(false);
            Equal(SuiteInventoryPublicationOutcome.CollectionUnavailable, outcome,
                "expected collection failure must become a non-throwing outcome");
            Equal(0, handler.RequestCount,
                "collection failure must occur before auxiliary network access");
            True(context.IsAuthorized,
                "collection failure must preserve the authorized context");
        }

        var cngHandler = new InventoryAuthorityHandler(
            time, assertionSigner, Array.Empty<byte>());
        using (var client = SuiteLicenseClient.CreateForInventoryVerifier(
                   authority,
                   new ThrowingPublisherMachineIdentity(),
                   cngHandler,
                   new FixedInventorySource(FixtureCollectedInventory()),
                   new InMemorySuiteInventoryPublicationStateStore(),
                   time))
        {
            var context = CreatePublisherContext(time, DeviceId);
            var outcome = await client.PublishMotherboardInventoryAsync(
                    context, CancellationToken.None)
                .ConfigureAwait(false);
            Equal(SuiteInventoryPublicationOutcome.CollectionUnavailable, outcome,
                "expected CNG describe failure must become a non-throwing outcome");
            Equal(0, cngHandler.RequestCount,
                "CNG describe failure must occur before auxiliary network access");
            True(context.IsAuthorized,
                "CNG inventory failure must preserve the authorized context");
        }
    }

    private static async Task VerifyRuntimeInventoryIsolationAsync()
    {
        var time = TimeProvider.System;
        using var assertionSigner = new AssertionSigner();
        using var identity = new PublisherMachineIdentity();
        var authority = CreatePublisherAuthority(time, assertionSigner);
        var handler = new RuntimeInventoryIsolationHandler(
            time,
            assertionSigner);
        var client = SuiteLicenseClient.CreateForInventoryVerifier(
            authority,
            identity,
            handler,
            new FixedInventorySource(FixtureCollectedInventory()),
            new InMemorySuiteInventoryPublicationStateStore(),
            time);
        await using var runtime = new SuiteLicensingRuntime(client, authority, time);

        var context = await runtime.OpenAsync(LicenseId)
            .WaitAsync(TimeSpan.FromSeconds(3))
            .ConfigureAwait(false);
        True(context.IsAuthorized,
            "signed session.open must return an authorized context");
        Equal(1, handler.OpenSessionCount,
            "runtime must complete exactly one signed session.open");

        await handler.InventoryStarted.WaitAsync(TimeSpan.FromSeconds(3))
            .ConfigureAwait(false);
        True(!handler.InventoryCompleted.IsCompleted,
            "session.open must return independently of the stalled inventory request");

        await handler.HeartbeatCompleted.WaitAsync(TimeSpan.FromSeconds(7))
            .ConfigureAwait(false);
        True(context.IsAuthorized,
            "heartbeat must continue while auxiliary inventory is still pending");
        True(!handler.InventoryCompleted.IsCompleted,
            "heartbeat must not wait for the auxiliary inventory response");
        Equal(1, handler.InventoryRequestCount,
            "heartbeat must not trigger another inventory publication");

        handler.ReleaseInventoryAsUnsupported();
        await handler.InventoryCompleted.WaitAsync(TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
        True(context.IsAuthorized,
            "inventory 404 must not invalidate the signed session context");
        Equal(1, handler.HeartbeatSessionCount,
            "runtime must complete a signed heartbeat after open");
        Equal(1, handler.InventoryRequestCount,
            "inventory 404 must not be retried by the heartbeat loop");
    }

    private static async Task VerifyWindowsCollectorSmokeAsync()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        var source = new SuiteWindowsMotherboardInventorySource();
        var inventory = await source.CollectAsync(timeout.Token)
            .WaitAsync(TimeSpan.FromSeconds(6), timeout.Token)
            .ConfigureAwait(false);

        Equal(SuiteWindowsMotherboardInventorySource.SchemaVersion,
            inventory.SchemaVersion,
            "real Windows inventory schema");
        Equal(
            SuiteMotherboardInventoryNormalizer.ComputeFingerprint(inventory),
            inventory.MotherboardFingerprint,
            "real Windows inventory fingerprint must match normalized evidence");
        True(inventory.Source is SuiteWindowsMotherboardInventorySource.CimSource
                or SuiteWindowsMotherboardInventorySource.RegistryFallbackSource
                or SuiteWindowsMotherboardInventorySource.CimAndRegistrySource,
            "real Windows collector must report a canonical source");

        if (!inventory.HasIdentityEvidence) return;

        var wire = new SuiteMotherboardInventoryV1(
            SuiteDeviceInventoryProtocol.SchemaVersion,
            LicenseId,
            DeviceId,
            inventory.MotherboardFingerprint,
            inventory.BaseboardManufacturer,
            inventory.BaseboardProduct,
            inventory.BaseboardVersion,
            inventory.BaseboardSerial,
            inventory.SystemManufacturer,
            inventory.SystemModel,
            inventory.SystemUuid,
            inventory.BiosManufacturer,
            inventory.BiosVersion,
            inventory.OsName,
            inventory.OsVersion,
            inventory.Architecture,
            inventory.ClientVersion,
            inventory.Source,
            inventory.CollectedAtUnixSeconds);
        SuiteDeviceInventoryProtocol.ValidateInventory(wire);
    }

    private static SuiteAuthorityConfiguration CreatePublisherAuthority(
        TimeProvider time,
        AssertionSigner assertionSigner)
    {
        var assertionSpki = assertionSigner.ExportSubjectPublicKeyInfo();
        try
        {
            return new SuiteAuthorityConfiguration(
                new Uri("https://licensing.example.invalid/", UriKind.Absolute),
                SuiteIdentityPolicy.SoftwareOnly,
                new string('1', 64),
                assertionSigner.KeyId,
                assertionSpki,
                new string('2', 64),
                time.GetUtcNow() - TimeSpan.FromMinutes(1),
                time.GetUtcNow() + TimeSpan.FromHours(1));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(assertionSpki);
        }
    }

    private static AuthorizedStoreContext CreatePublisherContext(
        TimeProvider time,
        string deviceId)
    {
        var now = time.GetUtcNow().ToUnixTimeSeconds();
        return new AuthorizedStoreContext(new SuiteAuthorizationState(
            time,
            new SuiteSessionResponse(
                SuiteOnlineLicenseProtocol.SchemaVersion,
                SuiteOnlineLicenseProtocol.ProductId,
                LicenseId,
                deviceId,
                SessionId,
                "ACTIVE",
                now,
                now + 3_600,
                60)));
    }

    private static SuiteMotherboardInventory FixtureCollectedInventory()
        => new(
            1,
            SuiteMotherboardInventoryNormalizer.ComputeFingerprint(
                "Gigabyte Technology Co., Ltd.",
                "B550M AORUS ELITE",
                "x.x",
                "SN-000001",
                "Gigabyte Technology Co., Ltd.",
                "B550M AORUS ELITE",
                "03560230-040f-0585-c906-a80700080009"),
            "Gigabyte Technology Co., Ltd.",
            "B550M AORUS ELITE",
            "x.x",
            "SN-000001",
            "Gigabyte Technology Co., Ltd.",
            "B550M AORUS ELITE",
            "03560230-040f-0585-c906-a80700080009",
            "American Megatrends International, LLC.",
            "FG",
            "Microsoft Windows 10 Pro",
            "10.0.19044.0",
            "X64",
            "2.0.0.0",
            SuiteWindowsMotherboardInventorySource.CimSource,
            Now);

    private static SuiteMotherboardInventoryV1 FixtureWireInventory(
        string? deviceId = null)
    {
        var collected = FixtureCollectedInventory();
        return new SuiteMotherboardInventoryV1(
            SuiteDeviceInventoryProtocol.SchemaVersion,
            LicenseId,
            deviceId ?? DeviceId,
            collected.MotherboardFingerprint,
            collected.BaseboardManufacturer,
            collected.BaseboardProduct,
            collected.BaseboardVersion,
            collected.BaseboardSerial,
            collected.SystemManufacturer,
            collected.SystemModel,
            collected.SystemUuid,
            collected.BiosManufacturer,
            collected.BiosVersion,
            collected.OsName,
            collected.OsVersion,
            collected.Architecture,
            collected.ClientVersion,
            collected.Source,
            collected.CollectedAtUnixSeconds);
    }

    private static SuiteDeviceInventoryChallengeRequestV1 FixtureChallengeRequest(
        string inventoryHash)
        => new(
            SuiteDeviceInventoryProtocol.SchemaVersion,
            SuiteDeviceInventoryProtocol.ProductId,
            LicenseId,
            DeviceId,
            SessionId,
            SuiteDeviceInventoryProtocol.Action,
            inventoryHash);

    private static SuiteChallengeResponse FixtureChallenge()
        => new(
            SuiteDeviceInventoryProtocol.SchemaVersion,
            ChallengeId,
            Nonce,
            Now + 60);

    private static SuiteDeviceInventoryProofV1 FixtureProof(
        SuiteMotherboardInventoryV1 inventory,
        string inventoryHash,
        string signature)
        => new(
            SuiteDeviceInventoryProtocol.SchemaVersion,
            SuiteDeviceInventoryProtocol.ProductId,
            LicenseId,
            inventory.DeviceId,
            SessionId,
            SuiteDeviceInventoryProtocol.Action,
            inventoryHash,
            ChallengeId,
            signature,
            inventory);

    private static SuiteDeviceInventoryChallengeAssertionV1 FixtureChallengeAssertion(
        string inventoryHash)
        => new(
            SuiteDeviceInventoryProtocol.SchemaVersion,
            SuiteDeviceInventoryProtocol.ChallengeAssertionKind,
            SuiteDeviceInventoryProtocol.ProductId,
            LicenseId,
            DeviceId,
            SessionId,
            SuiteDeviceInventoryProtocol.Action,
            inventoryHash,
            ChallengeId,
            Nonce,
            SuiteDeviceInventoryProtocol.ChallengeStatus,
            Now,
            Now + 60);

    private static SuiteDeviceInventoryResultAssertionV1 FixtureResultAssertion(
        string inventoryHash)
        => new(
            SuiteDeviceInventoryProtocol.SchemaVersion,
            SuiteDeviceInventoryProtocol.ResultAssertionKind,
            SuiteDeviceInventoryProtocol.ProductId,
            LicenseId,
            DeviceId,
            SessionId,
            SuiteDeviceInventoryProtocol.Action,
            inventoryHash,
            ChallengeId,
            SuiteDeviceInventoryProtocol.ResultStatus,
            Now);

    private static void ParseChallenge(
        byte[] bytes,
        byte[] signerSpki,
        string signerKeyId,
        string inventoryHash)
        => _ = SuiteDeviceInventoryProtocol.ParseChallengeAssertion(
            bytes,
            signerSpki,
            signerKeyId,
            LicenseId,
            DeviceId,
            SessionId,
            inventoryHash,
            Now + 1);

    private static void ParseResult(
        byte[] bytes,
        byte[] signerSpki,
        string signerKeyId,
        string inventoryHash)
        => _ = SuiteDeviceInventoryProtocol.ParseResultAssertion(
            bytes,
            signerSpki,
            signerKeyId,
            LicenseId,
            DeviceId,
            SessionId,
            inventoryHash,
            ChallengeId,
            Now + 1);

    private static void ExpectStrictJsonFailures(
        byte[] canonicalEnvelope,
        Action<byte[]> parse,
        string label)
    {
        var json = Encoding.UTF8.GetString(canonicalEnvelope);
        var duplicate = Encoding.UTF8.GetBytes(
            "{\"schemaVersion\":1," + json[1..]);
        var unknown = Encoding.UTF8.GetBytes(
            "{\"unexpected\":true," + json[1..]);
        var wrongCase = Encoding.UTF8.GetBytes(json.Replace(
            "\"schemaVersion\":",
            "\"SchemaVersion\":",
            StringComparison.Ordinal));
        try
        {
            ExpectSecurity(() => parse(duplicate),
                label + " must reject duplicate fields");
            ExpectSecurity(() => parse(unknown),
                label + " must reject unknown fields");
            ExpectSecurity(() => parse(wrongCase),
                label + " must reject case variants");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(duplicate);
            CryptographicOperations.ZeroMemory(unknown);
            CryptographicOperations.ZeroMemory(wrongCase);
        }
    }

    private static void ExpectStrictPayloadFailures(
        byte[] canonicalPayload,
        string kind,
        string domain,
        AssertionSigner signer,
        Action<byte[]> parse,
        string label)
    {
        try
        {
            var json = Encoding.UTF8.GetString(canonicalPayload);
            var variants = new[]
            {
                Encoding.UTF8.GetBytes(
                    "{\"schemaVersion\":1," + json[1..]),
                Encoding.UTF8.GetBytes(
                    "{\"unexpected\":true," + json[1..]),
                Encoding.UTF8.GetBytes(json.Replace(
                    "\"schemaVersion\":",
                    "\"SchemaVersion\":",
                    StringComparison.Ordinal))
            };
            try
            {
                for (var index = 0; index < variants.Length; index++)
                {
                    var envelope = signer.SignRaw(kind, domain, variants[index]);
                    try
                    {
                        ExpectSecurity(
                            () => parse(envelope),
                            $"{label} strict variant {index + 1} must fail");
                    }
                    finally { CryptographicOperations.ZeroMemory(envelope); }
                }
            }
            finally
            {
                foreach (var variant in variants)
                    CryptographicOperations.ZeroMemory(variant);
            }
        }
        finally { CryptographicOperations.ZeroMemory(canonicalPayload); }
    }

    private static byte[] ReplaceEnvelopePayload(
        ReadOnlyMemory<byte> canonicalEnvelope,
        ReadOnlySpan<byte> replacementPayload)
    {
        using var document = JsonDocument.Parse(canonicalEnvelope);
        var root = document.RootElement;
        return SuiteOnlineLicenseProtocol.SerializeSignedAssertionEnvelope(
            new SuiteSignedAssertionEnvelope(
                root.GetProperty("schemaVersion").GetInt32(),
                root.GetProperty("kind").GetString()
                    ?? throw new InvalidOperationException("Envelope kind ausente."),
                root.GetProperty("algorithm").GetString()
                    ?? throw new InvalidOperationException(
                        "Envelope algorithm ausente."),
                root.GetProperty("keyId").GetString()
                    ?? throw new InvalidOperationException("Envelope keyId ausente."),
                Convert.ToBase64String(replacementPayload),
                root.GetProperty("signature").GetString()
                    ?? throw new InvalidOperationException(
                        "Envelope signature ausente.")));
    }

    private static string CreateFixedSignature()
    {
        var bytes = Enumerable.Range(0, 256)
            .Select(static value => (byte)value)
            .ToArray();
        try { return Convert.ToBase64String(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static string LowerSha256(ReadOnlySpan<byte> value)
        => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private static void SequenceEqual(
        ReadOnlySpan<byte> expected,
        ReadOnlySpan<byte> actual,
        string label)
    {
        if (!expected.SequenceEqual(actual))
            throw new InvalidOperationException(label);
    }

    private static void ExpectSecurity(Action action, string label)
        => Throws<SecurityException>(action, label);

    private static void Throws<TException>(Action action, string label)
        where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        throw new InvalidOperationException(label);
    }

    private static async Task ExpectThrowsAsync<TException>(
        Func<Task> action,
        string label)
        where TException : Exception
    {
        try { await action().ConfigureAwait(false); }
        catch (TException) { return; }
        throw new InvalidOperationException(label);
    }

    private static void True(bool value, string label)
    {
        if (!value) throw new InvalidOperationException(label);
    }

    private static void Equal<T>(T expected, T actual, string label)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException(
                $"{label}: expected '{expected}', actual '{actual}'.");
    }

    private static void NotEqual<T>(T left, T right, string label)
    {
        if (EqualityComparer<T>.Default.Equals(left, right))
            throw new InvalidOperationException(label);
    }

    private sealed class FixedInventorySource : ISuiteMotherboardInventorySource
    {
        private readonly SuiteMotherboardInventory _inventory;

        public FixedInventorySource(SuiteMotherboardInventory inventory)
            => _inventory = inventory;

        public Task<SuiteMotherboardInventory> CollectAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_inventory);
        }
    }

    private sealed class ThrowingInventorySource : ISuiteMotherboardInventorySource
    {
        private readonly Exception _exception;

        public ThrowingInventorySource(Exception exception)
            => _exception = exception;

        public Task<SuiteMotherboardInventory> CollectAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromException<SuiteMotherboardInventory>(_exception);
        }
    }

    private sealed class PublisherMachineIdentity : ISuiteMachineIdentity, IDisposable
    {
        private readonly RSA _rsa = RSA.Create(2048);
        private readonly byte[] _spki;
        private int _inventorySignatureCount;

        public PublisherMachineIdentity()
        {
            _spki = _rsa.ExportSubjectPublicKeyInfo();
            DeviceId = SuiteOnlineLicenseProtocol.DeviceIdFromSpki(_spki);
        }

        public string DeviceId { get; }
        public int InventorySignatureCount
            => Volatile.Read(ref _inventorySignatureCount);

        public byte[] ExportSubjectPublicKeyInfo() => _spki.ToArray();

        public SuiteDeviceDescriptor Describe()
            => new(
                SuiteOnlineLicenseProtocol.SchemaVersion,
                DeviceId,
                "SOFTWARE_BOUND_ONLINE",
                SuiteOnlineLicenseProtocol.SigningAlgorithm,
                Convert.ToBase64String(_spki),
                new string('f', 64),
                "2.0.0.0");

        public string Sign(SuiteChallengeResponse challenge, string licenseId,
            string sessionId, string action, string contextHash)
        {
            var message = SuiteOnlineLicenseProtocol.BuildSigningMessage(
                challenge, licenseId, DeviceId, sessionId, action, contextHash);
            return SignMessage(message);
        }

        public string SignDeviceInventory(SuiteChallengeResponse challenge,
            string licenseId, string sessionId, string inventoryHash)
        {
            var message = SuiteDeviceInventoryProtocol.BuildProofSigningMessage(
                challenge, licenseId, DeviceId, sessionId, inventoryHash);
            Interlocked.Increment(ref _inventorySignatureCount);
            return SignMessage(message);
        }

        private string SignMessage(byte[] message)
        {
            byte[]? signature = null;
            try
            {
                signature = _rsa.SignData(
                    message,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pss);
                return Convert.ToBase64String(signature);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(message);
                if (signature is not null)
                    CryptographicOperations.ZeroMemory(signature);
            }
        }

        public void Dispose()
        {
            _rsa.Dispose();
            CryptographicOperations.ZeroMemory(_spki);
        }
    }

    private sealed class ThrowingPublisherMachineIdentity : ISuiteMachineIdentity
    {
        public SuiteDeviceDescriptor Describe()
            => throw new CryptographicException("Synthetic CNG describe failure.");

        public string Sign(SuiteChallengeResponse challenge, string licenseId,
            string sessionId, string action, string contextHash)
            => throw new CryptographicException("Synthetic CNG signing failure.");

        public string SignDeviceInventory(SuiteChallengeResponse challenge,
            string licenseId, string sessionId, string inventoryHash)
            => throw new CryptographicException(
                "Synthetic CNG inventory signing failure.");
    }

    private sealed class RuntimeInventoryIsolationHandler : HttpMessageHandler
    {
        private readonly TimeProvider _time;
        private readonly AssertionSigner _signer;
        private readonly TaskCompletionSource<bool> _inventoryStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _releaseInventory = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _inventoryCompleted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _heartbeatCompleted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private long _challengeSequence;
        private int _inventoryRequestCount;
        private int _openSessionCount;
        private int _heartbeatSessionCount;

        public RuntimeInventoryIsolationHandler(
            TimeProvider time,
            AssertionSigner signer)
        {
            _time = time;
            _signer = signer;
        }

        public Task InventoryStarted => _inventoryStarted.Task;
        public Task InventoryCompleted => _inventoryCompleted.Task;
        public Task HeartbeatCompleted => _heartbeatCompleted.Task;
        public int InventoryRequestCount
            => Volatile.Read(ref _inventoryRequestCount);
        public int OpenSessionCount => Volatile.Read(ref _openSessionCount);
        public int HeartbeatSessionCount
            => Volatile.Read(ref _heartbeatSessionCount);

        public void ReleaseInventoryAsUnsupported()
            => _releaseInventory.TrySetResult(true);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var route = request.RequestUri?.AbsolutePath.TrimStart('/') ?? "";
            if (string.Equals(
                    route,
                    SuiteDeviceInventoryProtocol.ChallengeRoute,
                    StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _inventoryRequestCount);
                _inventoryStarted.TrySetResult(true);
                await _releaseInventory.Task.WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
                _inventoryCompleted.TrySetResult(true);
                return new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent(
                        "<html>legacy authority</html>",
                        Encoding.UTF8,
                        "text/html")
                };
            }

            var bytes = request.Content is null
                ? Array.Empty<byte>()
                : await request.Content.ReadAsByteArrayAsync(cancellationToken)
                    .ConfigureAwait(false);
            try
            {
                using var document = JsonDocument.Parse(bytes);
                var root = document.RootElement;
                if (string.Equals(
                        route,
                        SuiteOnlineLicenseProtocol.ChallengeRoute,
                        StringComparison.Ordinal))
                {
                    var now = _time.GetUtcNow().ToUnixTimeSeconds();
                    var action = root.GetProperty("action").GetString()!;
                    var assertion = new SuiteOperationChallengeAssertion(
                        SuiteOnlineLicenseProtocol.SchemaVersion,
                        string.Equals(action, "session.open", StringComparison.Ordinal)
                            ? SuiteOnlineLicenseProtocol
                                .SessionOpenChallengeAssertionKind
                            : SuiteOnlineLicenseProtocol
                                .SessionHeartbeatChallengeAssertionKind,
                        SuiteOnlineLicenseProtocol.ProductId,
                        root.GetProperty("licenseId").GetString()!,
                        root.GetProperty("deviceId").GetString()!,
                        root.GetProperty("sessionId").GetString()!,
                        action,
                        root.GetProperty("contextHash").GetString()!,
                        NextChallengeId(),
                        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
                        "ISSUED",
                        now,
                        now + 60);
                    return JsonBytesResponse(
                        _signer.SignOperationChallenge(assertion));
                }

                if (string.Equals(
                        route,
                        SuiteOnlineLicenseProtocol.SuiteSessionRoute,
                        StringComparison.Ordinal))
                {
                    var context = root.GetProperty("context");
                    var proof = root.GetProperty("proof");
                    var action = context.GetProperty("action").GetString()!;
                    var now = _time.GetUtcNow().ToUnixTimeSeconds();
                    var assertion = new SuiteSessionAssertion(
                        SuiteOnlineLicenseProtocol.SchemaVersion,
                        string.Equals(action, "session.open", StringComparison.Ordinal)
                            ? SuiteOnlineLicenseProtocol.SessionOpenAssertionKind
                            : SuiteOnlineLicenseProtocol
                                .SessionHeartbeatAssertionKind,
                        SuiteOnlineLicenseProtocol.ProductId,
                        context.GetProperty("licenseId").GetString()!,
                        context.GetProperty("deviceId").GetString()!,
                        context.GetProperty("sessionId").GetString()!,
                        action,
                        proof.GetProperty("contextHash").GetString()!,
                        proof.GetProperty("challengeId").GetString()!,
                        "ACTIVE",
                        now,
                        now + 30,
                        5);
                    if (string.Equals(
                            action, "session.open", StringComparison.Ordinal))
                    {
                        Interlocked.Increment(ref _openSessionCount);
                    }
                    else
                    {
                        Interlocked.Increment(ref _heartbeatSessionCount);
                        _heartbeatCompleted.TrySetResult(true);
                    }
                    return JsonBytesResponse(_signer.SignSession(assertion));
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent(
                        "<html>unknown route</html>",
                        Encoding.UTF8,
                        "text/html")
                };
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }

        private string NextChallengeId()
            => Interlocked.Increment(ref _challengeSequence)
                .ToString("x64", CultureInfo.InvariantCulture);

        private static HttpResponseMessage JsonBytesResponse(byte[] bytes)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes)
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue(
                "application/json")
            {
                CharSet = "utf-8"
            };
            return response;
        }
    }

    private sealed class InventoryAuthorityHandler : HttpMessageHandler
    {
        private readonly TimeProvider _time;
        private readonly AssertionSigner _assertionSigner;
        private readonly byte[] _deviceSpki;
        private readonly HttpStatusCode? _unsupportedStatus;
        private readonly string? _unsupportedErrorCode;
        private readonly bool _failFirstResponseBody;
        private readonly object _challengeGate = new();
        private readonly Dictionary<string, SuiteChallengeResponse> _challenges =
            new(StringComparer.Ordinal);
        private int _requestCount;
        private int _challengeRequestCount;
        private int _proofRequestCount;
        private int _failedBodyIssued;
        private int _proofVerified;
        private long _challengeSequence;

        public InventoryAuthorityHandler(
            TimeProvider time,
            AssertionSigner assertionSigner,
            byte[] deviceSpki,
            HttpStatusCode? unsupportedStatus = null,
            bool failFirstResponseBody = false,
            string? unsupportedErrorCode = null)
        {
            _time = time;
            _assertionSigner = assertionSigner;
            _deviceSpki = deviceSpki;
            _unsupportedStatus = unsupportedStatus;
            _failFirstResponseBody = failFirstResponseBody;
            _unsupportedErrorCode = unsupportedErrorCode;
        }

        public int RequestCount => Volatile.Read(ref _requestCount);
        public int ChallengeRequestCount
            => Volatile.Read(ref _challengeRequestCount);
        public int ProofRequestCount => Volatile.Read(ref _proofRequestCount);
        public bool ProofVerified => Volatile.Read(ref _proofVerified) != 0;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            if (_unsupportedStatus is { } unsupportedStatus)
                return _unsupportedErrorCode is null
                    ? LegacyUnsupportedResponse(unsupportedStatus)
                    : UnsupportedJsonResponse(
                        unsupportedStatus, _unsupportedErrorCode);

            var route = request.RequestUri?.AbsolutePath.TrimStart('/') ?? "";
            var bytes = request.Content is null
                ? Array.Empty<byte>()
                : await request.Content.ReadAsByteArrayAsync(cancellationToken)
                    .ConfigureAwait(false);
            try
            {
                if (string.Equals(
                        route,
                        SuiteDeviceInventoryProtocol.ChallengeRoute,
                        StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref _challengeRequestCount);
                    var challengeRequest = ParseChallengeRequest(bytes);
                    var now = _time.GetUtcNow().ToUnixTimeSeconds();
                    var challengeId = Interlocked.Increment(ref _challengeSequence)
                        .ToString("x64", CultureInfo.InvariantCulture);
                    var challenge = new SuiteChallengeResponse(
                        SuiteDeviceInventoryProtocol.SchemaVersion,
                        challengeId,
                        Nonce,
                        now + 60);
                    lock (_challengeGate)
                        _challenges.Add(challengeId, challenge);

                    if (_failFirstResponseBody
                        && Interlocked.CompareExchange(
                            ref _failedBodyIssued, 1, 0) == 0)
                        return FailingBodyResponse();

                    var assertion = new SuiteDeviceInventoryChallengeAssertionV1(
                        SuiteDeviceInventoryProtocol.SchemaVersion,
                        SuiteDeviceInventoryProtocol.ChallengeAssertionKind,
                        challengeRequest.ProductId,
                        challengeRequest.LicenseId,
                        challengeRequest.DeviceId,
                        challengeRequest.SessionId,
                        challengeRequest.Action,
                        challengeRequest.InventoryHash,
                        challengeId,
                        Nonce,
                        SuiteDeviceInventoryProtocol.ChallengeStatus,
                        now,
                        now + 60);
                    return JsonBytesResponse(
                        _assertionSigner.SignChallenge(assertion));
                }

                if (string.Equals(
                        route,
                        SuiteDeviceInventoryProtocol.InventoryRoute,
                        StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref _proofRequestCount);
                    var proof = JsonSerializer.Deserialize<SuiteDeviceInventoryProofV1>(
                                    bytes, PublisherWireJsonOptions)
                                ?? throw new InvalidOperationException(
                                    "Inventory proof was empty.");
                    SuiteDeviceInventoryProtocol.ValidateProof(proof);
                    SuiteChallengeResponse challenge;
                    lock (_challengeGate)
                    {
                        if (!_challenges.Remove(proof.ChallengeId, out challenge!))
                            throw new InvalidOperationException(
                                "Inventory proof used an unknown challenge.");
                    }
                    if (!SuiteDeviceInventoryProtocol.VerifyProof(
                            _deviceSpki, challenge, proof))
                        throw new InvalidOperationException(
                            "Inventory device proof was invalid.");
                    Volatile.Write(ref _proofVerified, 1);

                    var assertion = new SuiteDeviceInventoryResultAssertionV1(
                        SuiteDeviceInventoryProtocol.SchemaVersion,
                        SuiteDeviceInventoryProtocol.ResultAssertionKind,
                        proof.ProductId,
                        proof.LicenseId,
                        proof.DeviceId,
                        proof.SessionId,
                        proof.Action,
                        proof.InventoryHash,
                        proof.ChallengeId,
                        SuiteDeviceInventoryProtocol.ResultStatus,
                        _time.GetUtcNow().ToUnixTimeSeconds());
                    return JsonBytesResponse(
                        _assertionSigner.SignResult(assertion));
                }

                return LegacyUnsupportedResponse(HttpStatusCode.NotFound);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }

        private static SuiteDeviceInventoryChallengeRequestV1 ParseChallengeRequest(
            byte[] bytes)
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            var request = new SuiteDeviceInventoryChallengeRequestV1(
                root.GetProperty("schemaVersion").GetInt32(),
                root.GetProperty("productId").GetString()!,
                root.GetProperty("licenseId").GetString()!,
                root.GetProperty("deviceId").GetString()!,
                root.GetProperty("sessionId").GetString()!,
                root.GetProperty("action").GetString()!,
                root.GetProperty("inventoryHash").GetString()!);
            SuiteDeviceInventoryProtocol.ValidateChallengeRequest(request);
            return request;
        }

        private static HttpResponseMessage JsonBytesResponse(byte[] bytes)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes)
            };
            response.Content.Headers.ContentType = JsonContentType();
            return response;
        }

        private static HttpResponseMessage FailingBodyResponse()
            => new(HttpStatusCode.OK)
            {
                Content = new FailingReadContent()
            };

        private static HttpResponseMessage LegacyUnsupportedResponse(
            HttpStatusCode status)
            => new(status)
            {
                Content = new StringContent(
                    "<html>legacy server</html>", Encoding.UTF8, "text/html")
            };

        private static HttpResponseMessage UnsupportedJsonResponse(
            HttpStatusCode status,
            string code)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(
                new SuiteErrorResponse(
                    SuiteDeviceInventoryProtocol.SchemaVersion,
                    code,
                    "Synthetic feature negotiation response."),
                PublisherWireJsonOptions);
            var response = new HttpResponseMessage(status)
            {
                Content = new ByteArrayContent(bytes)
            };
            response.Content.Headers.ContentType = JsonContentType();
            return response;
        }

        private static MediaTypeHeaderValue JsonContentType()
            => new("application/json") { CharSet = "utf-8" };

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                CryptographicOperations.ZeroMemory(_deviceSpki);
            base.Dispose(disposing);
        }
    }

    private sealed class FailingReadContent : HttpContent
    {
        public FailingReadContent()
            => Headers.ContentType = new MediaTypeHeaderValue("application/json")
            {
                CharSet = "utf-8"
            };

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context)
            => Task.FromException(
                new IOException("Synthetic response-body transport failure."));

        protected override bool TryComputeLength(out long length)
        {
            length = 32;
            return true;
        }
    }

    private sealed class AssertionSigner : IDisposable
    {
        private readonly RSA _rsa = RSA.Create(2048);
        private readonly byte[] _spki;

        public AssertionSigner()
        {
            _spki = _rsa.ExportSubjectPublicKeyInfo();
            KeyId = LowerSha256(_spki);
        }

        public string KeyId { get; }

        public byte[] ExportSubjectPublicKeyInfo() => _spki.ToArray();

        public byte[] SignChallenge(
            SuiteDeviceInventoryChallengeAssertionV1 assertion)
        {
            var payload = SuiteDeviceInventoryProtocol.CanonicalChallengeAssertion(
                assertion);
            try
            {
                return SignRaw(
                    SuiteDeviceInventoryProtocol.ChallengeAssertionKind,
                    SuiteDeviceInventoryProtocol.ChallengeAssertionDomain,
                    payload);
            }
            finally { CryptographicOperations.ZeroMemory(payload); }
        }

        public byte[] SignResult(SuiteDeviceInventoryResultAssertionV1 assertion)
        {
            var payload = SuiteDeviceInventoryProtocol.CanonicalResultAssertion(
                assertion);
            try
            {
                return SignRaw(
                    SuiteDeviceInventoryProtocol.ResultAssertionKind,
                    SuiteDeviceInventoryProtocol.ResultAssertionDomain,
                    payload);
            }
            finally { CryptographicOperations.ZeroMemory(payload); }
        }

        public byte[] SignOperationChallenge(
            SuiteOperationChallengeAssertion assertion)
            => SignOnline(
                assertion.Kind,
                assertion,
                SuiteOnlineLicenseProtocol.CanonicalOperationChallengeAssertion,
                SuiteOnlineLicenseProtocol
                    .BuildOperationChallengeAssertionSigningMessage);

        public byte[] SignSession(SuiteSessionAssertion assertion)
            => SignOnline(
                assertion.Kind,
                assertion,
                SuiteOnlineLicenseProtocol.CanonicalSessionAssertion,
                SuiteOnlineLicenseProtocol.BuildSessionAssertionSigningMessage);

        private byte[] SignOnline<TAssertion>(
            string kind,
            TAssertion assertion,
            Func<TAssertion, byte[]> canonicalPayload,
            Func<TAssertion, byte[]> signingMessage)
        {
            var payload = canonicalPayload(assertion);
            var message = signingMessage(assertion);
            var signature = _rsa.SignData(
                message, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
            try
            {
                return SuiteOnlineLicenseProtocol.SerializeSignedAssertionEnvelope(
                    new SuiteSignedAssertionEnvelope(
                        SuiteOnlineLicenseProtocol.SchemaVersion,
                        kind,
                        SuiteOnlineLicenseProtocol.SigningAlgorithm,
                        KeyId,
                        Convert.ToBase64String(payload),
                        Convert.ToBase64String(signature)));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(payload);
                CryptographicOperations.ZeroMemory(message);
                CryptographicOperations.ZeroMemory(signature);
            }
        }

        public byte[] SignRaw(
            string kind,
            string domain,
            ReadOnlySpan<byte> payload,
            string? envelopeKeyId = null)
        {
            var domainBytes = Encoding.ASCII.GetBytes(domain);
            var message = new byte[checked(domainBytes.Length + payload.Length)];
            domainBytes.CopyTo(message, 0);
            payload.CopyTo(message.AsSpan(domainBytes.Length));
            var signature = _rsa.SignData(
                message, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
            try
            {
                return SuiteOnlineLicenseProtocol.SerializeSignedAssertionEnvelope(
                    new SuiteSignedAssertionEnvelope(
                        SuiteDeviceInventoryProtocol.SchemaVersion,
                        kind,
                        SuiteDeviceInventoryProtocol.SigningAlgorithm,
                        envelopeKeyId ?? KeyId,
                        Convert.ToBase64String(payload),
                        Convert.ToBase64String(signature)));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(domainBytes);
                CryptographicOperations.ZeroMemory(message);
                CryptographicOperations.ZeroMemory(signature);
            }
        }

        public void Dispose()
        {
            _rsa.Dispose();
            CryptographicOperations.ZeroMemory(_spki);
        }
    }
}
