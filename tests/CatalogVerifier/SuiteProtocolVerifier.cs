using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Reflection;
using System.Reflection.Emit;
using System.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using TurboBoxManager.Licensing;

namespace TurboBoxManager.CatalogVerifier;

public static class SuiteProtocolVerifier
{
    private const string LicenseId = "TR-000125";
    private static readonly string SessionId = new('1', 64);
    private static readonly string HardwareFingerprint = new('2', 64);
    private static readonly string ChallengeId = new('3', 64);
    private static readonly string DeviceId = new('4', 64);

    public static void Run()
    {
        VerifyAdditiveSuiteRouteNamespace();
        VerifyCanonicalContexts();
        VerifyLegacyV1SigningEnvelope();
        VerifyStrictResponsesAndActionMatrix();
        VerifySpkiDeviceId();
        VerifySignedAuthorityConfiguration();
        VerifySignedOnlineAssertionsAndTlsPinAsync()
            .GetAwaiter().GetResult();
        VerifyNonForgeableCapabilityAndDefaultFailClosed();
        VerifyUnixEpochBounds();
        VerifyPassiveSessionExpiryAndAtomicConsumerAsync()
            .GetAwaiter().GetResult();
        VerifyRenewalMovesMonotonicDeadlineAsync()
            .GetAwaiter().GetResult();
        VerifyLongAuthorityDeadlineIsSlicedAsync()
            .GetAwaiter().GetResult();
        VerifyAuthorityExpiryAndIdentityFailureAsync()
            .GetAwaiter().GetResult();
    }

    private static void VerifyAdditiveSuiteRouteNamespace()
    {
        foreach (var route in new[]
                 {
                     SuiteOnlineLicenseProtocol.ActivationChallengeRoute,
                     SuiteOnlineLicenseProtocol.ActivationCompleteRoute,
                     SuiteOnlineLicenseProtocol.ChallengeRoute,
                     SuiteOnlineLicenseProtocol.SuiteSessionRoute
                 })
        {
            True(route.StartsWith("v1/suite/", StringComparison.Ordinal),
                $"Suite endpoint must not mutate or reuse a legacy route: {route}");
            True(!route.StartsWith("/", StringComparison.Ordinal)
                 && !route.Contains('?') && !route.Contains('#'),
                $"Suite endpoint must be a canonical relative path: {route}");
        }
    }

    private static void VerifyCanonicalContexts()
    {
        var descriptor = FixtureDescriptor();
        var activation = SuiteOnlineLicenseProtocol.CanonicalActivationContext(
            LicenseId, descriptor);
        try
        {
            Equal("ed47acd52669a3901931427994a191cae8a50af369b3cf1b81a5221b46623958",
                LowerSha256(activation), "Suite activation canonical hash");
        }
        finally { CryptographicOperations.ZeroMemory(activation); }

        var session = new SuiteSessionContext(
            SuiteOnlineLicenseProtocol.SchemaVersion,
            SuiteOnlineLicenseProtocol.ProductId,
            LicenseId,
            DeviceId,
            SessionId,
            "session.open",
            HardwareFingerprint,
            "1.7.0");
        Equal("5e05775e2819b9cfce3f5c16662cff88bcf627d26f26695c2a993bc04d0f9989",
            SuiteOnlineLicenseProtocol.ContextHash(session),
            "Suite session canonical hash");

        ExpectSecurity(() => SuiteOnlineLicenseProtocol.ContextHash(session with
        {
            ProductId = "TURBORAMA_PIX"
        }), "wrong product must be rejected");
        ExpectSecurity(() => SuiteOnlineLicenseProtocol.ContextHash(session with
        {
            Action = "payment.create"
        }), "PIX payment action must not be accepted by Suite");
        ExpectSecurity(() => SuiteOnlineLicenseProtocol.ActivationContextHash(
            LicenseId, descriptor with { SchemaVersion = 2 }),
            "wrong descriptor schema must be rejected");
    }

    private static void VerifyLegacyV1SigningEnvelope()
    {
        var challenge = new SuiteChallengeResponse(
            SuiteOnlineLicenseProtocol.SchemaVersion,
            ChallengeId,
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
            1_800_000_060);
        var message = SuiteOnlineLicenseProtocol.BuildSigningMessage(
            challenge,
            LicenseId,
            DeviceId,
            SessionId,
            "session.open",
            "77c209a4ab9fd413b3c39f9d150b8605f6bf4fdcf63a1c7698606975b24fdeb0");
        try
        {
            Equal(506, message.Length, "v1 signed message length");
            Equal("e446888f27083109d8fb454ba287a2c524691329434906971f1f5a3a870bc481",
                LowerSha256(message), "v1 signed message hash");
            var expectedDomain = Encoding.ASCII.GetBytes(
                "TurboRamaOnlineMachineProof/v1\0");
            try
            {
                True(message.AsSpan(0, expectedDomain.Length).SequenceEqual(expectedDomain),
                    "v1 signing domain changed");
            }
            finally { CryptographicOperations.ZeroMemory(expectedDomain); }
        }
        finally { CryptographicOperations.ZeroMemory(message); }

        ExpectSecurity(() => SuiteOnlineLicenseProtocol.BuildSigningMessage(
            challenge, LicenseId, DeviceId, SessionId, "configuration.write",
            new string('a', 64)), "Suite configuration.write must be rejected");
    }

    private static void VerifyStrictResponsesAndActionMatrix()
    {
        var validError = Encoding.UTF8.GetBytes(
            "{\"schemaVersion\":1,\"code\":\"DENIED\",\"message\":\"denied\"}");
        try
        {
            var parsed = SuiteOnlineLicenseProtocol.ParseErrorResponse(validError);
            Equal("DENIED", parsed.Code, "strict error parse");
        }
        finally { CryptographicOperations.ZeroMemory(validError); }

        ExpectSecurity(() => ParseError(
            "{\"schemaVersion\":1,\"schemaVersion\":1,"
            + "\"code\":\"DENIED\",\"message\":\"denied\"}"),
            "duplicate JSON member must be rejected");
        ExpectSecurity(() => ParseError(
            "{\"schemaVersion\":1,\"code\":\"DENIED\","
            + "\"message\":\"denied\",\"unexpected\":true}"),
            "unknown JSON member must be rejected");
        ExpectSecurity(() => ParseError(
            "{\"SchemaVersion\":1,\"code\":\"DENIED\","
            + "\"message\":\"denied\"}"),
            "case-insensitive JSON field must be rejected");

        var response = new SuiteSessionResponse(
            1, SuiteOnlineLicenseProtocol.ProductId, LicenseId, DeviceId, SessionId,
            "ACTIVE", 1_800_000_000, 1_800_000_180, 60);
        SuiteOnlineLicenseProtocol.ValidateSessionResponse(
            response, LicenseId, DeviceId, SessionId);
        ExpectSecurity(() => SuiteOnlineLicenseProtocol.ValidateSessionResponse(
            response with { ProductId = "TURBORAMA_PIX" },
            LicenseId, DeviceId, SessionId), "wrong session product must be rejected");
        ExpectSecurity(() => SuiteOnlineLicenseProtocol.ValidateSessionResponse(
            response with { HeartbeatAfterSeconds = 180 },
            LicenseId, DeviceId, SessionId), "heartbeat at deadline must be rejected");
    }

    private static void VerifySpkiDeviceId()
    {
        using var rsa = RSA.Create(2048);
        var spki = rsa.ExportSubjectPublicKeyInfo();
        try
        {
            var deviceId = SuiteOnlineLicenseProtocol.DeviceIdFromSpki(spki);
            var descriptor = new SuiteDeviceDescriptor(
                1,
                deviceId,
                "SOFTWARE_BOUND_ONLINE",
                SuiteOnlineLicenseProtocol.SigningAlgorithm,
                Convert.ToBase64String(spki),
                HardwareFingerprint,
                "2.0.0.0");
            var parsed = SuiteOnlineLicenseProtocol.ParseAndValidateSpki(descriptor);
            try { True(parsed.AsSpan().SequenceEqual(spki), "canonical SPKI changed"); }
            finally { CryptographicOperations.ZeroMemory(parsed); }

            ExpectSecurity(() =>
            {
                var invalid = SuiteOnlineLicenseProtocol.ParseAndValidateSpki(
                    descriptor with { DeviceId = new string('0', 64) });
                CryptographicOperations.ZeroMemory(invalid);
            }, "deviceId must be SHA-256(SPKI)");
        }
        finally { CryptographicOperations.ZeroMemory(spki); }
    }

    private static void VerifySignedAuthorityConfiguration()
    {
        const long nowSeconds = 2_000_000_000;
        using var online = RSA.Create(2048);
        var onlineSpki = online.ExportSubjectPublicKeyInfo();
        try
        {
            var onlineKeyId = SuiteAuthorityConfigurationVerifier.KeyIdFromSpki(
                onlineSpki);
            var payload = new SuiteAuthorityPayload(
                1,
                SuiteAuthorityConfigurationVerifier.ConfigurationKind,
                SuiteOnlineLicenseProtocol.ProductId,
                "https://licensing.example.invalid/",
                "TPM_PREFERRED",
                SuiteAuthorityConfigurationVerifier.SignatureAlgorithm,
                onlineKeyId,
                Convert.ToBase64String(onlineSpki),
                new string('b', 64),
                nowSeconds - 60,
                nowSeconds + 3600);
            var canonical = SuiteAuthorityConfigurationVerifier.CanonicalPayload(payload);
            try
            {
                Equal("{\"schemaVersion\":1,\"kind\":\"TURBORAMA_SUITE_AUTHORITY\","
                    + "\"productId\":\"TURBORAMA_SUITE\","
                    + "\"baseUrl\":\"https://licensing.example.invalid/\","
                    + "\"identityPolicy\":\"TPM_PREFERRED\","
                    + "\"onlineAssertionAlgorithm\":\"rsa-pss-sha256\","
                    + "\"onlineAssertionKeyId\":\"" + onlineKeyId + "\","
                    + "\"onlineAssertionPublicKeySpki\":\""
                    + JsonEncodedText.Encode(Convert.ToBase64String(onlineSpki),
                        JavaScriptEncoder.Default) + "\","
                    + "\"tlsServerSpkiSha256\":\"" + new string('b', 64) + "\","
                    + "\"issuedAtUnixSeconds\":1999999940,"
                    + "\"expiresAtUnixSeconds\":2000003600}",
                    Encoding.UTF8.GetString(canonical), "authority canonical payload");

                using var issuer = RSA.Create(2048);
                var issuerSpki = issuer.ExportSubjectPublicKeyInfo();
                var signingMessage = SuiteAuthorityConfigurationVerifier.BuildSigningMessage(
                    payload);
                byte[] signature = Array.Empty<byte>();
                byte[] envelopeBytes = Array.Empty<byte>();
                try
                {
                    signature = issuer.SignData(signingMessage, HashAlgorithmName.SHA256,
                        RSASignaturePadding.Pss);
                    var envelope = new SuiteAuthorityEnvelope(
                        1,
                        SuiteAuthorityConfigurationVerifier.SignatureAlgorithm,
                        SuiteAuthorityConfigurationVerifier.KeyIdFromSpki(issuerSpki),
                        Convert.ToBase64String(canonical),
                        Convert.ToBase64String(signature));
                    envelopeBytes = SuiteAuthorityConfigurationVerifier.SerializeEnvelope(
                        envelope);

                    var fixedTime = new FixedUtcTimeProvider(
                        DateTimeOffset.FromUnixTimeSeconds(nowSeconds));
                    var configurationHashBytes = SHA256.HashData(envelopeBytes);
                    var configurationHash = Convert.ToHexString(configurationHashBytes)
                        .ToLowerInvariant();
                    CryptographicOperations.ZeroMemory(configurationHashBytes);
                    var verified = SuiteAuthorityConfigurationVerifier.VerifyPinnedEnvelope(
                        envelopeBytes, issuerSpki, configurationHash, fixedTime);
                    Equal("https://licensing.example.invalid/",
                        verified.BaseUri.AbsoluteUri, "verified authority URL");
                    Equal(SuiteIdentityPolicy.TpmPreferred, verified.IdentityPolicy,
                        "verified identity policy");
                    Equal(onlineKeyId, verified.OnlineAssertionKeyId,
                        "verified online assertion key");
                    Equal(new string('b', 64), verified.TlsServerSpkiSha256,
                        "verified TLS SPKI pin");

                    var loaded = SuiteEmbeddedAuthorityLoader.Load(
                        CreateAuthorityMetadataAssembly(
                            envelopeBytes,
                            issuerSpki,
                            configurationHash),
                        fixedTime);
                    True(loaded.Configuration is not null
                         && loaded.FailureCode.Length == 0,
                        "embedded authority must require and accept its exact hash anchor");

                    var mismatchedHash = (configurationHash[0] == '0' ? "1" : "0")
                                         + configurationHash[1..];
                    ExpectSecurity(() =>
                        SuiteAuthorityConfigurationVerifier.VerifyPinnedEnvelope(
                            envelopeBytes, issuerSpki, mismatchedHash, fixedTime),
                        "authority envelope hash mismatch must be rejected");
                    ExpectSecurity(() =>
                        SuiteAuthorityConfigurationVerifier.VerifyPinnedEnvelope(
                            envelopeBytes,
                            issuerSpki,
                            configurationHash.ToUpperInvariant(),
                            fixedTime),
                        "authority envelope hash anchor must be lowercase canonical hex");

                    var previousPayload = payload with
                    {
                        IssuedAtUnixSeconds = nowSeconds - 120,
                        ExpiresAtUnixSeconds = nowSeconds + 1800
                    };
                    var previousEnvelope = SignAuthorityEnvelope(
                        issuer,
                        issuerSpki,
                        previousPayload);
                    try
                    {
                        _ = SuiteAuthorityConfigurationVerifier.VerifyEnvelope(
                            previousEnvelope,
                            issuerSpki,
                            fixedTime);
                        ExpectSecurity(() =>
                            SuiteAuthorityConfigurationVerifier.VerifyPinnedEnvelope(
                                previousEnvelope,
                                issuerSpki,
                                configurationHash,
                                fixedTime),
                            "an older valid envelope from the same issuer must not roll back the pinned configuration");
                        var rolledBack = SuiteEmbeddedAuthorityLoader.Load(
                            CreateAuthorityMetadataAssembly(
                                previousEnvelope,
                                issuerSpki,
                                configurationHash),
                            fixedTime);
                        Equal(SuiteEmbeddedAuthorityLoader.InvalidConfiguration,
                            rolledBack.FailureCode,
                            "embedded loader must reject a same-issuer rollback");
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(previousEnvelope);
                    }

                    var maximumValidityPayload = payload with
                    {
                        IssuedAtUnixSeconds = nowSeconds,
                        ExpiresAtUnixSeconds = nowSeconds + 366L * 24 * 60 * 60
                    };
                    var maximumValidityEnvelope = SignAuthorityEnvelope(
                        issuer,
                        issuerSpki,
                        maximumValidityPayload);
                    var excessiveValidityEnvelope = SignAuthorityEnvelope(
                        issuer,
                        issuerSpki,
                        maximumValidityPayload with
                        {
                            ExpiresAtUnixSeconds =
                                maximumValidityPayload.ExpiresAtUnixSeconds + 1
                        });
                    try
                    {
                        _ = SuiteAuthorityConfigurationVerifier.VerifyEnvelope(
                            maximumValidityEnvelope,
                            issuerSpki,
                            fixedTime);
                        ExpectSecurity(() =>
                            SuiteAuthorityConfigurationVerifier.VerifyEnvelope(
                                excessiveValidityEnvelope,
                                issuerSpki,
                                fixedTime),
                            "authority validity must not exceed 366 days");
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(maximumValidityEnvelope);
                        CryptographicOperations.ZeroMemory(excessiveValidityEnvelope);
                    }

                    var tamperedSignature = signature.ToArray();
                    try
                    {
                        tamperedSignature[0] ^= 0x01;
                        var tamperedEnvelope = envelope with
                        {
                            Signature = Convert.ToBase64String(tamperedSignature)
                        };
                        var tamperedBytes =
                            SuiteAuthorityConfigurationVerifier.SerializeEnvelope(
                                tamperedEnvelope);
                        try
                        {
                            ExpectSecurity(() =>
                                SuiteAuthorityConfigurationVerifier.VerifyEnvelope(
                                    tamperedBytes, issuerSpki, fixedTime),
                                "tampered authority signature must be rejected");
                        }
                        finally { CryptographicOperations.ZeroMemory(tamperedBytes); }
                    }
                    finally { CryptographicOperations.ZeroMemory(tamperedSignature); }

                    var nonCanonicalKeyEnvelope =
                        SuiteAuthorityConfigurationVerifier.SerializeEnvelope(
                            envelope with { KeyId = envelope.KeyId.ToUpperInvariant() });
                    try
                    {
                        ExpectSecurity(() =>
                            SuiteAuthorityConfigurationVerifier.VerifyEnvelope(
                                nonCanonicalKeyEnvelope, issuerSpki, fixedTime),
                            "authority keyId must be lowercase canonical hex");
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(nonCanonicalKeyEnvelope);
                    }

                    ExpectSecurity(() =>
                        SuiteAuthorityConfigurationVerifier.VerifyEnvelope(
                            envelopeBytes, issuerSpki,
                            new FixedUtcTimeProvider(
                                DateTimeOffset.FromUnixTimeSeconds(nowSeconds + 3601))),
                        "expired authority configuration must be rejected");

                    var reusedOfflineKeyPayload = payload with
                    {
                        OnlineAssertionKeyId =
                            SuiteAuthorityConfigurationVerifier.KeyIdFromSpki(issuerSpki),
                        OnlineAssertionPublicKeySpki = Convert.ToBase64String(issuerSpki)
                    };
                    var reusedEnvelope = SignAuthorityEnvelope(
                        issuer, issuerSpki, reusedOfflineKeyPayload);
                    try
                    {
                        ExpectSecurity(() =>
                            SuiteAuthorityConfigurationVerifier.VerifyEnvelope(
                                reusedEnvelope, issuerSpki, fixedTime),
                            "offline issuer key must not be reused on-line");
                    }
                    finally { CryptographicOperations.ZeroMemory(reusedEnvelope); }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(issuerSpki);
                    CryptographicOperations.ZeroMemory(signingMessage);
                    if (signature.Length != 0)
                        CryptographicOperations.ZeroMemory(signature);
                    if (envelopeBytes.Length != 0)
                        CryptographicOperations.ZeroMemory(envelopeBytes);
                }
            }
            finally { CryptographicOperations.ZeroMemory(canonical); }

            ExpectSecurity(() => SuiteAuthorityConfigurationVerifier.CanonicalPayload(
                payload with { ProductId = "TURBORAMA_PIX" }),
                "authority for another product must be rejected");
            ExpectSecurity(() => SuiteAuthorityConfigurationVerifier.CanonicalPayload(
                payload with { BaseUrl = "http://licensing.example.invalid/" }),
                "non-HTTPS authority must be rejected");
            ExpectSecurity(() => SuiteAuthorityConfigurationVerifier.CanonicalPayload(
                payload with { OnlineAssertionKeyId = new string('0', 64) }),
                "online assertion keyId must match its SPKI");
            ExpectSecurity(() => SuiteAuthorityConfigurationVerifier.CanonicalPayload(
                payload with { TlsServerSpkiSha256 = new string('B', 64) }),
                "TLS SPKI pin must be lowercase canonical hex");
        }
        finally { CryptographicOperations.ZeroMemory(onlineSpki); }
    }

    private static async Task VerifySignedOnlineAssertionsAndTlsPinAsync()
    {
        const long now = 2_050_000_000;
        var contextHash = new string('5', 64);
        using var signer = new TestOnlineAssertionSigner();
        using var wrongSigner = new TestOnlineAssertionSigner();
        var signerSpki = signer.ExportSubjectPublicKeyInfo();
        try
        {
            var challengeAssertion = new SuiteOperationChallengeAssertion(
                1,
                SuiteOnlineLicenseProtocol.SessionOpenChallengeAssertionKind,
                SuiteOnlineLicenseProtocol.ProductId,
                LicenseId,
                DeviceId,
                SessionId,
                "session.open",
                contextHash,
                ChallengeId,
                "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
                "ISSUED",
                now,
                now + 60);
            var canonical =
                SuiteOnlineLicenseProtocol.CanonicalOperationChallengeAssertion(
                    challengeAssertion);
            try
            {
                Equal("{\"schemaVersion\":1,"
                    + "\"kind\":\"TURBORAMA_SUITE_SESSION_OPEN_CHALLENGE\","
                    + "\"productId\":\"TURBORAMA_SUITE\","
                    + "\"licenseId\":\"TR-000125\","
                    + "\"deviceId\":\"" + DeviceId + "\","
                    + "\"sessionId\":\"" + SessionId + "\","
                    + "\"action\":\"session.open\","
                    + "\"contextHash\":\"" + contextHash + "\","
                    + "\"challengeId\":\"" + ChallengeId + "\","
                    + "\"nonce\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=\","
                    + "\"status\":\"ISSUED\","
                    + "\"serverTimeUnixSeconds\":2050000000,"
                    + "\"expiresAtUnixSeconds\":2050000060}",
                    Encoding.UTF8.GetString(canonical),
                    "signed operation challenge canonical vector");
            }
            finally { CryptographicOperations.ZeroMemory(canonical); }

            var signedChallenge = signer.SignOperationChallenge(challengeAssertion);
            try
            {
                var parsed = SuiteOnlineLicenseProtocol.ParseOperationChallengeAssertion(
                    signedChallenge, signerSpki, signer.KeyId,
                    LicenseId, DeviceId, SessionId, "session.open", contextHash, now);
                Equal(ChallengeId, parsed.ChallengeId,
                    "valid signed challenge must parse");
            }
            finally { CryptographicOperations.ZeroMemory(signedChallenge); }

            var unsigned = Encoding.UTF8.GetBytes(
                "{\"schemaVersion\":1,\"challengeId\":\"" + ChallengeId
                + "\",\"nonce\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=\","
                + "\"expiresAtUnixSeconds\":2050000060}");
            try
            {
                ExpectSecurity(() =>
                    SuiteOnlineLicenseProtocol.ParseOperationChallengeAssertion(
                        unsigned, signerSpki, signer.KeyId,
                        LicenseId, DeviceId, SessionId, "session.open",
                        contextHash, now),
                    "unsigned positive response must be rejected");
            }
            finally { CryptographicOperations.ZeroMemory(unsigned); }

            var wrongSignature = wrongSigner.SignOperationChallenge(
                challengeAssertion, signer.KeyId);
            try
            {
                ExpectSecurity(() =>
                    SuiteOnlineLicenseProtocol.ParseOperationChallengeAssertion(
                        wrongSignature, signerSpki, signer.KeyId,
                        LicenseId, DeviceId, SessionId, "session.open",
                        contextHash, now),
                    "assertion signed by the wrong key must be rejected");
            }
            finally { CryptographicOperations.ZeroMemory(wrongSignature); }

            var crossContext = signer.SignOperationChallenge(
                challengeAssertion with { ContextHash = new string('6', 64) });
            try
            {
                ExpectSecurity(() =>
                    SuiteOnlineLicenseProtocol.ParseOperationChallengeAssertion(
                        crossContext, signerSpki, signer.KeyId,
                        LicenseId, DeviceId, SessionId, "session.open",
                        contextHash, now),
                    "valid signature from another context must be rejected");
            }
            finally { CryptographicOperations.ZeroMemory(crossContext); }

            var expiredChallenge = signer.SignOperationChallenge(challengeAssertion);
            try
            {
                ExpectSecurity(() =>
                    SuiteOnlineLicenseProtocol.ParseOperationChallengeAssertion(
                        expiredChallenge, signerSpki, signer.KeyId,
                        LicenseId, DeviceId, SessionId, "session.open",
                        contextHash, now + 60),
                    "expired signed challenge must be rejected before signing");
            }
            finally { CryptographicOperations.ZeroMemory(expiredChallenge); }

            var futureAssertion = challengeAssertion with
            {
                ServerTimeUnixSeconds = now + 301,
                ExpiresAtUnixSeconds = now + 361
            };
            var futureChallenge = signer.SignOperationChallenge(futureAssertion);
            try
            {
                ExpectSecurity(() =>
                    SuiteOnlineLicenseProtocol.ParseOperationChallengeAssertion(
                        futureChallenge, signerSpki, signer.KeyId,
                        LicenseId, DeviceId, SessionId, "session.open",
                        contextHash, now),
                    "challenge too far in the future must be rejected");
            }
            finally { CryptographicOperations.ZeroMemory(futureChallenge); }

            var excessiveLocalLifetimeAssertion = challengeAssertion with
            {
                ServerTimeUnixSeconds = now + 250,
                ExpiresAtUnixSeconds = now + 310
            };
            var excessiveLocalLifetime = signer.SignOperationChallenge(
                excessiveLocalLifetimeAssertion);
            try
            {
                ExpectSecurity(() =>
                    SuiteOnlineLicenseProtocol.ParseOperationChallengeAssertion(
                        excessiveLocalLifetime, signerSpki, signer.KeyId,
                        LicenseId, DeviceId, SessionId, "session.open",
                        contextHash, now),
                    "challenge must not remain valid more than five local minutes");
            }
            finally { CryptographicOperations.ZeroMemory(excessiveLocalLifetime); }

            var activationResultAssertion = new SuiteActivationResultAssertion(
                1,
                SuiteOnlineLicenseProtocol.ActivationResultAssertionKind,
                SuiteOnlineLicenseProtocol.ProductId,
                LicenseId,
                DeviceId,
                "device.activate",
                contextHash,
                ChallengeId,
                "ACTIVE",
                "SOFTWARE_BOUND_ONLINE",
                now);
            var signedActivationResult = signer.SignActivationResult(
                activationResultAssertion);
            try
            {
                var result = SuiteOnlineLicenseProtocol.ParseActivationResultAssertion(
                    signedActivationResult, signerSpki, signer.KeyId,
                    LicenseId, DeviceId, "SOFTWARE_BOUND_ONLINE", contextHash,
                    ChallengeId, now);
                Equal("ACTIVE", result.Status,
                    "valid signed activation result must parse");
                ExpectSecurity(() =>
                    SuiteOnlineLicenseProtocol.ParseActivationResultAssertion(
                        signedActivationResult, signerSpki, signer.KeyId,
                        LicenseId, DeviceId, "SOFTWARE_BOUND_ONLINE", contextHash,
                        new string('7', 64), now),
                    "activation result replayed across challenges must be rejected");
            }
            finally { CryptographicOperations.ZeroMemory(signedActivationResult); }

            var sessionAssertion = new SuiteSessionAssertion(
                1,
                SuiteOnlineLicenseProtocol.SessionHeartbeatAssertionKind,
                SuiteOnlineLicenseProtocol.ProductId,
                LicenseId,
                DeviceId,
                SessionId,
                "session.heartbeat",
                contextHash,
                ChallengeId,
                "ACTIVE",
                now,
                now + 120,
                30);
            var signedSession = signer.SignSession(sessionAssertion);
            try
            {
                var session = SuiteOnlineLicenseProtocol.ParseSessionAssertion(
                    signedSession, signerSpki, signer.KeyId,
                    LicenseId, DeviceId, SessionId, "session.heartbeat",
                    contextHash, ChallengeId, now);
                Equal("ACTIVE", session.Status,
                    "valid signed heartbeat must parse");
                ExpectSecurity(() =>
                    SuiteOnlineLicenseProtocol.ParseSessionAssertion(
                        signedSession, signerSpki, signer.KeyId,
                        LicenseId, DeviceId, SessionId, "session.open",
                        contextHash, ChallengeId, now),
                    "heartbeat/session type confusion must be rejected");
            }
            finally { CryptographicOperations.ZeroMemory(signedSession); }

            var time = new ManualTimeProvider(
                DateTimeOffset.FromUnixTimeSeconds(now));
            var authority = TestAuthority(time, TimeSpan.FromHours(1), signer);
            using (var activationClient = SuiteLicenseClient.CreateForVerifier(
                authority, new TestMachineIdentity(),
                new SessionAuthorityHandler(time, signer, 120, stallHeartbeat: false),
                time))
            {
                await activationClient.ActivateAsync(
                    LicenseId, "0123456789abcdef", CancellationToken.None)
                    .ConfigureAwait(false);
            }

            using (var unsignedClient = SuiteLicenseClient.CreateForVerifier(
                authority, new TestMachineIdentity(),
                new SessionAuthorityHandler(time, signer, 120,
                    stallHeartbeat: false, unsignedChallenges: true), time))
            {
                var invalid = await ThrowsAsync<SuiteApiException>(
                    () => unsignedClient.OpenSessionAsync(
                        LicenseId, SessionId, false, CancellationToken.None),
                    "custom transport must not bypass application signatures")
                    .ConfigureAwait(false);
                Equal("INVALID_RESPONSE", invalid.Code,
                    "unsigned custom transport failure code");
            }

            using (var replayClient = SuiteLicenseClient.CreateForVerifier(
                authority, new TestMachineIdentity(),
                new SessionAuthorityHandler(time, signer, 120,
                    stallHeartbeat: false, replayChallenges: true), time))
            {
                _ = await replayClient.OpenSessionAsync(
                    LicenseId, SessionId, false, CancellationToken.None)
                    .ConfigureAwait(false);
                _ = await ThrowsAsync<SecurityException>(
                    () => replayClient.OpenSessionAsync(
                        LicenseId, SessionId, false, CancellationToken.None),
                    "replayed signed challenge must be rejected by the client")
                    .ConfigureAwait(false);
            }

            using var certificateKey = RSA.Create(2048);
            var request = new CertificateRequest(
                "CN=licensing.example.invalid", certificateKey,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var certificate = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(-1),
                DateTimeOffset.UtcNow.AddDays(1));
            var certificateSpki = certificate.PublicKey.ExportSubjectPublicKeyInfo();
            try
            {
                var pin = LowerSha256(certificateSpki);
                True(SuiteLicenseClient.ValidatePinnedServerCertificate(
                        certificate, null, SslPolicyErrors.None, pin),
                    "matching TLS SPKI pin with normal validation must pass");
                True(!SuiteLicenseClient.ValidatePinnedServerCertificate(
                        certificate, null, SslPolicyErrors.None, new string('0', 64)),
                    "MITM certificate with another SPKI must fail");
                True(!SuiteLicenseClient.ValidatePinnedServerCertificate(
                        certificate, null, SslPolicyErrors.RemoteCertificateChainErrors,
                        pin),
                    "SPKI pin must not bypass normal chain validation");
                using var transport = SuiteLicenseClient.CreateHandler(pin);
                True(!transport.UseProxy, "production licensing transport must disable proxy");
                True(!transport.AllowAutoRedirect,
                    "production licensing transport must disable redirects");
                True(!transport.UseCookies,
                    "production licensing transport must disable cookies");
                Equal(DecompressionMethods.None, transport.AutomaticDecompression,
                    "production licensing transport must disable decompression");
                Equal(X509RevocationMode.Online,
                    transport.SslOptions.CertificateRevocationCheckMode,
                    "production licensing transport must check revocation");
            }
            finally { CryptographicOperations.ZeroMemory(certificateSpki); }
        }
        finally { CryptographicOperations.ZeroMemory(signerSpki); }
    }

    private static void VerifyNonForgeableCapabilityAndDefaultFailClosed()
    {
        Equal(0, typeof(AuthorizedStoreContext).GetConstructors(
            BindingFlags.Instance | BindingFlags.Public).Length,
            "AuthorizedStoreContext must not have a public constructor");
        Equal(0, typeof(SuiteAuthorityConfiguration).GetConstructors(
            BindingFlags.Instance | BindingFlags.Public).Length,
            "SuiteAuthorityConfiguration must not have a public constructor");
        Equal(0, typeof(SuiteLicenseClient).GetConstructors(
            BindingFlags.Instance | BindingFlags.Public).Length,
            "SuiteLicenseClient transport seam must not be publicly constructible");

        var lifetimeTime = new ManualTimeProvider(
            DateTimeOffset.FromUnixTimeSeconds(2_025_000_000));
        using (var lifetimeSigner = new TestOnlineAssertionSigner())
        {
            var lifetimeAuthority = TestAuthority(
                lifetimeTime,
                TimeSpan.FromHours(1),
                lifetimeSigner);
            var lifetimeClient = new SuiteLicenseClient(
                lifetimeAuthority,
                new TestMachineIdentity(),
                lifetimeTime);
            var spkiField = typeof(SuiteLicenseClient).GetField(
                                "_onlineAssertionSpki",
                                BindingFlags.Instance | BindingFlags.NonPublic)
                            ?? throw new InvalidOperationException(
                                "SuiteLicenseClient SPKI field is missing.");
            var publicSpki = (byte[]?)spkiField.GetValue(lifetimeClient)
                             ?? throw new InvalidOperationException(
                                 "SuiteLicenseClient SPKI is missing.");
            var beforeDispose = publicSpki.ToArray();
            try
            {
                lifetimeClient.Dispose();
                True(publicSpki.SequenceEqual(beforeDispose),
                    "Dispose must not mutate public assertion-key bytes racing active parsers");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(beforeDispose);
                lifetimeClient.Dispose();
            }
        }

        var activate = typeof(SuiteLicensingRuntime).GetMethod(
            nameof(SuiteLicensingRuntime.ActivateAndOpenAsync),
            BindingFlags.Instance | BindingFlags.Public)
            ?? throw new InvalidOperationException("ActivateAndOpenAsync is missing.");
        var parameters = activate.GetParameters();
        Equal(typeof(string), parameters[0].ParameterType, "licenseId API parameter");
        Equal("licenseId", parameters[0].Name, "licenseId API name");
        Equal(typeof(string), parameters[1].ParameterType, "activationCode API parameter");

        var runtime = SuiteLicensingFactory.CreateDefault();
        try
        {
            True(!runtime.IsAvailable, "default build must fail closed without signed authority");
            Equal("AUTHORITY_CONFIGURATION_MISSING", runtime.FailureCode,
                "missing authority failure code");
            Throws<SuiteLicensingUnavailableException>(() =>
                runtime.OpenAsync(LicenseId).GetAwaiter().GetResult(),
                "unavailable runtime must reject store opening");
        }
        finally { runtime.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    private static void VerifyUnixEpochBounds()
    {
        var challenge = new SuiteChallengeResponse(
            1, ChallengeId, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
            long.MaxValue);
        ExpectSecurity(() => SuiteOnlineLicenseProtocol.ValidateChallenge(challenge),
            "challenge epoch overflow must be rejected");
        ExpectSecurity(() => SuiteOnlineLicenseProtocol.ValidateChallenge(
            challenge with { ExpiresAtUnixSeconds = -1 }),
            "negative challenge epoch must be rejected");

        var response = new SuiteSessionResponse(
            1, SuiteOnlineLicenseProtocol.ProductId, LicenseId, DeviceId, SessionId,
            "ACTIVE", 2_000_000_000, long.MaxValue, 60);
        ExpectSecurity(() => SuiteOnlineLicenseProtocol.ValidateSessionResponse(
            response, LicenseId, DeviceId, SessionId),
            "session epoch overflow must be rejected before arithmetic");
        ExpectSecurity(() => SuiteOnlineLicenseProtocol.ValidateSessionResponse(
            response with
            {
                ServerTimeUnixSeconds = -1,
                AuthorizedUntilUnixSeconds = 2_000_000_180
            }, LicenseId, DeviceId, SessionId),
            "negative server epoch must be rejected");

        using var online = RSA.Create(2048);
        var onlineSpki = online.ExportSubjectPublicKeyInfo();
        try
        {
            var authority = new SuiteAuthorityPayload(
                1,
                SuiteAuthorityConfigurationVerifier.ConfigurationKind,
                SuiteOnlineLicenseProtocol.ProductId,
                "https://licensing.example.invalid/",
                "SOFTWARE_ONLY",
                SuiteAuthorityConfigurationVerifier.SignatureAlgorithm,
                SuiteAuthorityConfigurationVerifier.KeyIdFromSpki(onlineSpki),
                Convert.ToBase64String(onlineSpki),
                new string('b', 64),
                2_000_000_000,
                long.MaxValue);
            ExpectSecurity(() =>
                SuiteAuthorityConfigurationVerifier.CanonicalPayload(authority),
                "authority epoch overflow must be rejected before DateTimeOffset");
            ExpectSecurity(() => SuiteAuthorityConfigurationVerifier.CanonicalPayload(
                authority with
                {
                    IssuedAtUnixSeconds = -1,
                    ExpiresAtUnixSeconds = 2_000_000_001
                }), "negative authority epoch must be rejected");
        }
        finally { CryptographicOperations.ZeroMemory(onlineSpki); }
    }

    private static async Task VerifyPassiveSessionExpiryAndAtomicConsumerAsync()
    {
        var time = new ManualTimeProvider(
            DateTimeOffset.FromUnixTimeSeconds(2_100_000_000));
        using var signer = new TestOnlineAssertionSigner();
        var authority = TestAuthority(time, TimeSpan.FromHours(2), signer);
        var handler = new SessionAuthorityHandler(time, signer,
            sessionLifetimeSeconds: 20, stallHeartbeat: true);
        var client = SuiteLicenseClient.CreateForVerifier(authority, new TestMachineIdentity(), handler,
            time);
        await using var runtime = new SuiteLicensingRuntime(client, authority, time);
        var context = await runtime.OpenAsync(LicenseId).ConfigureAwait(false);

        var eventCount = 0;
        var eventReason = "";
        using var subscription = runtime.AttachAuthorizationConsumer(context, (_, args) =>
        {
            Interlocked.Increment(ref eventCount);
            eventReason = args.ReasonCode;
        });
        True(!subscription.AuthorizationCancellationToken.IsCancellationRequested,
            "authorization token must begin active");

        time.Advance(TimeSpan.FromSeconds(6));
        await handler.HeartbeatStarted.WaitAsync(TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        time.JumpUtc(TimeSpan.FromDays(-1));
        time.Advance(TimeSpan.FromSeconds(14));

        await EventuallyAsync(() =>
            subscription.AuthorizationCancellationToken.IsCancellationRequested
            && Volatile.Read(ref eventCount) == 1).ConfigureAwait(false);
        True(!context.IsAuthorized, "passive session deadline must revoke context");
        Equal("SESSION_EXPIRED", context.RevocationCode,
            "passive session revocation code");
        Equal("SESSION_EXPIRED", eventReason,
            "passive session event reason");
        Throws<SuiteAuthorizationException>(context.ThrowIfUnauthorized,
            "expired capability must fail closed");
        Throws<SuiteAuthorizationException>(() =>
            runtime.AttachAuthorizationConsumer(context, (_, _) => { }),
            "consumer cannot attach after revocation");
    }

    private static async Task VerifyAuthorityExpiryAndIdentityFailureAsync()
    {
        var time = new ManualTimeProvider(
            DateTimeOffset.FromUnixTimeSeconds(2_200_000_000));
        using var signer = new TestOnlineAssertionSigner();
        var authority = TestAuthority(time, TimeSpan.FromSeconds(30), signer);
        var handler = new SessionAuthorityHandler(time, signer,
            sessionLifetimeSeconds: 3600, stallHeartbeat: true);
        var client = SuiteLicenseClient.CreateForVerifier(authority, new TestMachineIdentity(), handler,
            time);
        await using (var runtime = new SuiteLicensingRuntime(client, authority, time))
        {
            var context = await runtime.OpenAsync(LicenseId).ConfigureAwait(false);
            var eventCount = 0;
            var eventReason = "";
            using var subscription = runtime.AttachAuthorizationConsumer(context, (_, args) =>
            {
                Interlocked.Increment(ref eventCount);
                eventReason = args.ReasonCode;
            });

            time.JumpUtc(TimeSpan.FromDays(-7));
            time.Advance(TimeSpan.FromSeconds(30));
            await EventuallyAsync(() =>
                subscription.AuthorizationCancellationToken.IsCancellationRequested
                && Volatile.Read(ref eventCount) == 1).ConfigureAwait(false);

            True(!runtime.IsAvailable,
                "expired authority configuration must disable runtime");
            Equal("AUTHORITY_CONFIGURATION_EXPIRED", runtime.FailureCode,
                "expired authority failure code");
            Equal("AUTHORITY_CONFIGURATION_EXPIRED", context.RevocationCode,
                "authority expiry must revoke open context");
            Equal("AUTHORITY_CONFIGURATION_EXPIRED", eventReason,
                "authority expiry event reason");
            var expiration = await ThrowsAsync<SuiteLicensingUnavailableException>(
                () => runtime.OpenAsync(LicenseId),
                "new operation must fail after authority expiration").ConfigureAwait(false);
            Equal("AUTHORITY_CONFIGURATION_EXPIRED", expiration.FailureCode,
                "new operation authority-expired code");
        }

        var identityTime = new ManualTimeProvider(
            DateTimeOffset.FromUnixTimeSeconds(2_300_000_000));
        var identityAuthority = TestAuthority(identityTime, TimeSpan.FromHours(1), signer);
        var unreachableHandler = new SessionAuthorityHandler(identityTime, signer,
            sessionLifetimeSeconds: 120, stallHeartbeat: false);
        var identityClient = SuiteLicenseClient.CreateForVerifier(identityAuthority,
            new ThrowingMachineIdentity(), unreachableHandler, identityTime);
        await using var identityRuntime = new SuiteLicensingRuntime(
            identityClient, identityAuthority, identityTime);
        var identityFailure = await ThrowsAsync<SuiteLicensingUnavailableException>(
            () => identityRuntime.OpenAsync(LicenseId),
            "late CNG failure must become controlled licensing failure")
            .ConfigureAwait(false);
        Equal("IDENTITY_UNAVAILABLE", identityFailure.FailureCode,
            "controlled identity failure code");
        Equal(0, unreachableHandler.RequestCount,
            "identity failure must occur before authority request");

        var signHandler = new SessionAuthorityHandler(identityTime, signer,
            sessionLifetimeSeconds: 120, stallHeartbeat: false);
        var signClient = SuiteLicenseClient.CreateForVerifier(identityAuthority,
            new SignThrowingMachineIdentity(), signHandler, identityTime);
        await using var signRuntime = new SuiteLicensingRuntime(
            signClient, identityAuthority, identityTime);
        var signFailure = await ThrowsAsync<SuiteLicensingUnavailableException>(
            () => signRuntime.OpenAsync(LicenseId),
            "late CNG signing failure must become controlled licensing failure")
            .ConfigureAwait(false);
        Equal("IDENTITY_UNAVAILABLE", signFailure.FailureCode,
            "controlled signing failure code");
        Equal(1, signHandler.RequestCount,
            "signing failure must stop after the challenge request");
    }

    private static async Task VerifyRenewalMovesMonotonicDeadlineAsync()
    {
        const long serverNow = 2_150_000_000;
        var time = new ManualTimeProvider(
            DateTimeOffset.FromUnixTimeSeconds(serverNow));
        var initial = new SuiteSessionResponse(
            1, SuiteOnlineLicenseProtocol.ProductId, LicenseId, DeviceId, SessionId,
            "ACTIVE", serverNow, serverNow + 20, 5);
        var state = new SuiteAuthorizationState(time, initial);
        var expiration = state.WaitForExpirationAsync(CancellationToken.None);

        time.Advance(TimeSpan.FromSeconds(10));
        var renewal = initial with
        {
            ServerTimeUnixSeconds = serverNow + 10,
            AuthorizedUntilUnixSeconds = serverNow + 40
        };
        state.Renew(renewal);
        ExpectSecurity(
            () => state.Renew(renewal),
            "a replayed heartbeat must not restart the monotonic deadline");
        ExpectSecurity(
            () => state.Renew(renewal with
            {
                ServerTimeUnixSeconds = serverNow + 9,
                AuthorizedUntilUnixSeconds = serverNow + 39
            }),
            "an out-of-order heartbeat must not restart the monotonic deadline");
        time.Advance(TimeSpan.FromSeconds(10));
        await Task.Yield();
        True(!expiration.IsCompleted,
            "renewal must cancel the old monotonic deadline");
        True(state.IsAuthorized,
            "renewed state must remain authorized at old deadline");

        time.Advance(TimeSpan.FromSeconds(20));
        True(await expiration.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false),
            "renewed monotonic deadline must eventually expire");
    }

    private static async Task VerifyLongAuthorityDeadlineIsSlicedAsync()
    {
        var time = new ManualTimeProvider(
            DateTimeOffset.FromUnixTimeSeconds(2_175_000_000));
        using var signer = new TestOnlineAssertionSigner();
        var authority = TestAuthority(time, TimeSpan.FromDays(90), signer);
        var handler = new SessionAuthorityHandler(time, signer,
            sessionLifetimeSeconds: 120, stallHeartbeat: false);
        var client = SuiteLicenseClient.CreateForVerifier(authority, new TestMachineIdentity(), handler,
            time);
        await using var runtime = new SuiteLicensingRuntime(client, authority, time);

        True(runtime.IsAvailable,
            "long authority must not expire while creating its timer");
        Equal(1, time.TimerCreationCount,
            "long authority should initially schedule one bounded timer slice");

        time.Advance(TimeSpan.FromDays(30));
        await EventuallyAsync(() => time.TimerCreationCount >= 2)
            .ConfigureAwait(false);
        True(runtime.IsAvailable,
            "long authority must survive its first timer slice");

        time.Advance(TimeSpan.FromDays(30));
        await EventuallyAsync(() => time.TimerCreationCount >= 3)
            .ConfigureAwait(false);
        True(runtime.IsAvailable,
            "long authority must survive its second timer slice");

        time.Advance(TimeSpan.FromDays(29));
        True(runtime.IsAvailable,
            "long authority must remain active before its monotonic deadline");
        time.Advance(TimeSpan.FromDays(1));
        await EventuallyAsync(() => !runtime.IsAvailable).ConfigureAwait(false);
        Equal("AUTHORITY_CONFIGURATION_EXPIRED", runtime.FailureCode,
            "long authority must expire at its full monotonic deadline");
    }

    private static SuiteAuthorityConfiguration TestAuthority(
        TimeProvider timeProvider, TimeSpan lifetime,
        TestOnlineAssertionSigner signer)
    {
        var onlineSpki = signer.ExportSubjectPublicKeyInfo();
        try
        {
            return new SuiteAuthorityConfiguration(
                new Uri("https://licensing.example.invalid/", UriKind.Absolute),
                SuiteIdentityPolicy.SoftwareOnly,
                new string('a', 64),
                signer.KeyId,
                onlineSpki,
                new string('b', 64),
                timeProvider.GetUtcNow() - TimeSpan.FromMinutes(1),
                timeProvider.GetUtcNow() + lifetime);
        }
        finally { CryptographicOperations.ZeroMemory(onlineSpki); }
    }

    private static Assembly CreateAuthorityMetadataAssembly(
        ReadOnlySpan<byte> envelope,
        ReadOnlySpan<byte> issuerSpki,
        string configurationSha256)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("TurboramaAuthorityFixture_" + Guid.NewGuid().ToString("N")),
            AssemblyBuilderAccess.Run);
        var metadataConstructor = typeof(AssemblyMetadataAttribute).GetConstructor(
                                      [typeof(string), typeof(string)])
                                  ?? throw new InvalidOperationException(
                                      "AssemblyMetadataAttribute constructor is missing.");
        foreach (var (key, value) in new[]
                 {
                     (
                         SuiteAuthorityConfigurationVerifier.ConfigurationMetadataKey,
                         Convert.ToBase64String(envelope)),
                     (
                         SuiteAuthorityConfigurationVerifier.ConfigurationSha256MetadataKey,
                         configurationSha256),
                     (
                         SuiteAuthorityConfigurationVerifier.IssuerSpkiMetadataKey,
                         Convert.ToBase64String(issuerSpki))
                 })
        {
            assembly.SetCustomAttribute(new CustomAttributeBuilder(
                metadataConstructor,
                [key, value]));
        }
        return assembly;
    }

    private static byte[] SignAuthorityEnvelope(RSA issuer,
        ReadOnlySpan<byte> issuerSpki, SuiteAuthorityPayload payload)
    {
        var canonical = SuiteAuthorityConfigurationVerifier.CanonicalPayload(payload);
        var message = SuiteAuthorityConfigurationVerifier.BuildSigningMessage(payload);
        var signature = issuer.SignData(
            message, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        try
        {
            return SuiteAuthorityConfigurationVerifier.SerializeEnvelope(
                new SuiteAuthorityEnvelope(
                    1,
                    SuiteAuthorityConfigurationVerifier.SignatureAlgorithm,
                    SuiteAuthorityConfigurationVerifier.KeyIdFromSpki(issuerSpki),
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

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (condition()) return;
            await Task.Delay(TimeSpan.FromMilliseconds(10)).ConfigureAwait(false);
        }
        throw new InvalidOperationException("Timed out waiting for deterministic event.");
    }

    private static async Task<TException> ThrowsAsync<TException>(
        Func<Task> action, string label)
        where TException : Exception
    {
        try { await action().ConfigureAwait(false); }
        catch (TException exception) { return exception; }
        throw new InvalidOperationException(label);
    }

    private sealed class TestMachineIdentity : ISuiteMachineIdentity
    {
        public SuiteDeviceDescriptor Describe() => FixtureDescriptor();

        public string Sign(SuiteChallengeResponse challenge, string licenseId,
            string sessionId, string action, string contextHash)
        {
            _ = challenge;
            _ = licenseId;
            _ = sessionId;
            _ = action;
            _ = contextHash;
            return Convert.ToBase64String(new byte[256]);
        }
    }

    private sealed class ThrowingMachineIdentity : ISuiteMachineIdentity
    {
        public SuiteDeviceDescriptor Describe()
            => throw new CryptographicException("Synthetic late CNG failure.");

        public string Sign(SuiteChallengeResponse challenge, string licenseId,
            string sessionId, string action, string contextHash)
            => throw new CryptographicException("Synthetic late CNG failure.");
    }

    private sealed class SignThrowingMachineIdentity : ISuiteMachineIdentity
    {
        public SuiteDeviceDescriptor Describe() => FixtureDescriptor();

        public string Sign(SuiteChallengeResponse challenge, string licenseId,
            string sessionId, string action, string contextHash)
            => throw new CryptographicException("Synthetic late CNG signing failure.");
    }

    private sealed class TestOnlineAssertionSigner : IDisposable
    {
        private readonly RSA _rsa = RSA.Create(2048);
        private readonly byte[] _spki;

        public TestOnlineAssertionSigner()
        {
            _spki = _rsa.ExportSubjectPublicKeyInfo();
            KeyId = SuiteAuthorityConfigurationVerifier.KeyIdFromSpki(_spki);
        }

        public string KeyId { get; }

        public byte[] ExportSubjectPublicKeyInfo() => _spki.ToArray();

        public byte[] SignActivationChallenge(
            SuiteActivationChallengeAssertion assertion,
            string? envelopeKeyId = null)
            => Sign(
                SuiteOnlineLicenseProtocol.ActivationChallengeAssertionKind,
                assertion,
                SuiteOnlineLicenseProtocol.CanonicalActivationChallengeAssertion,
                SuiteOnlineLicenseProtocol.BuildActivationChallengeAssertionSigningMessage,
                envelopeKeyId);

        public byte[] SignOperationChallenge(
            SuiteOperationChallengeAssertion assertion,
            string? envelopeKeyId = null)
            => Sign(
                assertion.Kind,
                assertion,
                SuiteOnlineLicenseProtocol.CanonicalOperationChallengeAssertion,
                SuiteOnlineLicenseProtocol.BuildOperationChallengeAssertionSigningMessage,
                envelopeKeyId);

        public byte[] SignActivationResult(
            SuiteActivationResultAssertion assertion,
            string? envelopeKeyId = null)
            => Sign(
                SuiteOnlineLicenseProtocol.ActivationResultAssertionKind,
                assertion,
                SuiteOnlineLicenseProtocol.CanonicalActivationResultAssertion,
                SuiteOnlineLicenseProtocol.BuildActivationResultAssertionSigningMessage,
                envelopeKeyId);

        public byte[] SignSession(SuiteSessionAssertion assertion,
            string? envelopeKeyId = null)
            => Sign(
                assertion.Kind,
                assertion,
                SuiteOnlineLicenseProtocol.CanonicalSessionAssertion,
                SuiteOnlineLicenseProtocol.BuildSessionAssertionSigningMessage,
                envelopeKeyId);

        private byte[] Sign<TAssertion>(string kind, TAssertion assertion,
            Func<TAssertion, byte[]> canonicalPayload,
            Func<TAssertion, byte[]> signingMessage,
            string? envelopeKeyId)
        {
            var canonical = canonicalPayload(assertion);
            var message = signingMessage(assertion);
            var signature = _rsa.SignData(
                message, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
            try
            {
                return SuiteOnlineLicenseProtocol.SerializeSignedAssertionEnvelope(
                    new SuiteSignedAssertionEnvelope(
                        1,
                        kind,
                        SuiteOnlineLicenseProtocol.SigningAlgorithm,
                        envelopeKeyId ?? KeyId,
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

        public void Dispose()
        {
            _rsa.Dispose();
            CryptographicOperations.ZeroMemory(_spki);
        }
    }

    private sealed class SessionAuthorityHandler : HttpMessageHandler
    {
        private readonly TimeProvider _timeProvider;
        private readonly TestOnlineAssertionSigner _signer;
        private readonly int _sessionLifetimeSeconds;
        private readonly bool _stallHeartbeat;
        private readonly bool _replayChallenges;
        private readonly bool _unsignedChallenges;
        private readonly TaskCompletionSource _heartbeatStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _requestCount;
        private long _challengeSequence;

        public SessionAuthorityHandler(TimeProvider timeProvider,
            TestOnlineAssertionSigner signer, int sessionLifetimeSeconds,
            bool stallHeartbeat, bool replayChallenges = false,
            bool unsignedChallenges = false)
            => (_timeProvider, _signer, _sessionLifetimeSeconds, _stallHeartbeat,
                _replayChallenges, _unsignedChallenges) =
                (timeProvider, signer, sessionLifetimeSeconds, stallHeartbeat,
                    replayChallenges, unsignedChallenges);

        public Task HeartbeatStarted => _heartbeatStarted.Task;
        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            var route = request.RequestUri?.AbsolutePath.TrimStart('/') ?? "";
            var bytes = request.Content is null
                ? Array.Empty<byte>()
                : await request.Content.ReadAsByteArrayAsync(cancellationToken)
                    .ConfigureAwait(false);
            try
            {
                using var document = JsonDocument.Parse(bytes);
                if (string.Equals(route,
                    SuiteOnlineLicenseProtocol.ActivationChallengeRoute,
                    StringComparison.Ordinal))
                {
                    var root = document.RootElement;
                    var licenseId = root.GetProperty("licenseId").GetString()!;
                    var device = ReadDevice(root.GetProperty("device"));
                    var now = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
                    var challengeId = NextChallengeId();
                    var assertion = new SuiteActivationChallengeAssertion(
                        1,
                        SuiteOnlineLicenseProtocol.ActivationChallengeAssertionKind,
                        SuiteOnlineLicenseProtocol.ProductId,
                        licenseId,
                        device.DeviceId,
                        "device.activate",
                        SuiteOnlineLicenseProtocol.ActivationContextHash(
                            licenseId, device),
                        challengeId,
                        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
                        "ISSUED",
                        now,
                        now + 60);
                    if (_unsignedChallenges)
                        return JsonResponse(HttpStatusCode.OK, new
                        {
                            schemaVersion = 1,
                            challengeId,
                            nonce = assertion.Nonce,
                            expiresAtUnixSeconds = assertion.ExpiresAtUnixSeconds
                        });
                    return JsonBytesResponse(HttpStatusCode.OK,
                        _signer.SignActivationChallenge(assertion));
                }

                if (string.Equals(route,
                    SuiteOnlineLicenseProtocol.ActivationCompleteRoute,
                    StringComparison.Ordinal))
                {
                    var root = document.RootElement;
                    var licenseId = root.GetProperty("licenseId").GetString()!;
                    var challengeId = root.GetProperty("challengeId").GetString()!;
                    var device = ReadDevice(root.GetProperty("device"));
                    var assertion = new SuiteActivationResultAssertion(
                        1,
                        SuiteOnlineLicenseProtocol.ActivationResultAssertionKind,
                        SuiteOnlineLicenseProtocol.ProductId,
                        licenseId,
                        device.DeviceId,
                        "device.activate",
                        SuiteOnlineLicenseProtocol.ActivationContextHash(
                            licenseId, device),
                        challengeId,
                        "ACTIVE",
                        device.BindingType,
                        _timeProvider.GetUtcNow().ToUnixTimeSeconds());
                    return JsonBytesResponse(HttpStatusCode.OK,
                        _signer.SignActivationResult(assertion));
                }

                if (string.Equals(route, SuiteOnlineLicenseProtocol.ChallengeRoute,
                    StringComparison.Ordinal))
                {
                    var root = document.RootElement;
                    var action = root.GetProperty("action").GetString()!;
                    if (_stallHeartbeat
                        && string.Equals(action, "session.heartbeat",
                            StringComparison.Ordinal))
                    {
                        _heartbeatStarted.TrySetResult();
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    var now = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
                    var challengeId = NextChallengeId();
                    var assertion = new SuiteOperationChallengeAssertion(
                        1,
                        string.Equals(action, "session.open", StringComparison.Ordinal)
                            ? SuiteOnlineLicenseProtocol.SessionOpenChallengeAssertionKind
                            : SuiteOnlineLicenseProtocol.SessionHeartbeatChallengeAssertionKind,
                        SuiteOnlineLicenseProtocol.ProductId,
                        root.GetProperty("licenseId").GetString()!,
                        root.GetProperty("deviceId").GetString()!,
                        root.GetProperty("sessionId").GetString()!,
                        action,
                        root.GetProperty("contextHash").GetString()!,
                        challengeId,
                        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
                        "ISSUED",
                        now,
                        now + 60);
                    if (_unsignedChallenges)
                    {
                        return JsonResponse(HttpStatusCode.OK, new
                        {
                            schemaVersion = 1,
                            challengeId,
                            nonce = assertion.Nonce,
                            expiresAtUnixSeconds = assertion.ExpiresAtUnixSeconds
                        });
                    }
                    return JsonBytesResponse(HttpStatusCode.OK,
                        _signer.SignOperationChallenge(assertion));
                }

                if (string.Equals(route, SuiteOnlineLicenseProtocol.SuiteSessionRoute,
                    StringComparison.Ordinal))
                {
                    var root = document.RootElement;
                    var context = root.GetProperty("context");
                    var proof = root.GetProperty("proof");
                    var now = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
                    var action = context.GetProperty("action").GetString()!;
                    var assertion = new SuiteSessionAssertion(
                        1,
                        string.Equals(action, "session.open", StringComparison.Ordinal)
                            ? SuiteOnlineLicenseProtocol.SessionOpenAssertionKind
                            : SuiteOnlineLicenseProtocol.SessionHeartbeatAssertionKind,
                        SuiteOnlineLicenseProtocol.ProductId,
                        context.GetProperty("licenseId").GetString()!,
                        context.GetProperty("deviceId").GetString()!,
                        context.GetProperty("sessionId").GetString()!,
                        action,
                        proof.GetProperty("contextHash").GetString()!,
                        proof.GetProperty("challengeId").GetString()!,
                        "ACTIVE",
                        now,
                        now + _sessionLifetimeSeconds,
                        Math.Min(60, _sessionLifetimeSeconds - 1));
                    return JsonBytesResponse(HttpStatusCode.OK,
                        _signer.SignSession(assertion));
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }

            return JsonResponse(HttpStatusCode.NotFound,
                new { schemaVersion = 1, code = "NOT_FOUND", message = "not found" });
        }

        private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode,
            object value)
        {
            var json = JsonSerializer.Serialize(value);
            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue(
                "application/json")
            {
                CharSet = "utf-8"
            };
            return response;
        }

        private static HttpResponseMessage JsonBytesResponse(HttpStatusCode statusCode,
            byte[] json)
        {
            var response = new HttpResponseMessage(statusCode)
            {
                Content = new ByteArrayContent(json)
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue(
                "application/json")
            {
                CharSet = "utf-8"
            };
            return response;
        }

        private string NextChallengeId()
        {
            if (_replayChallenges) return ChallengeId;
            var value = Interlocked.Increment(ref _challengeSequence);
            return value.ToString("x64", CultureInfo.InvariantCulture);
        }

        private static SuiteDeviceDescriptor ReadDevice(JsonElement device)
            => new(
                device.GetProperty("schemaVersion").GetInt32(),
                device.GetProperty("deviceId").GetString()!,
                device.GetProperty("bindingType").GetString()!,
                device.GetProperty("algorithm").GetString()!,
                device.GetProperty("publicKeySpki").GetString()!,
                device.GetProperty("hardwareFingerprint").GetString()!,
                device.GetProperty("agentVersion").GetString()!);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _utcNow;
        private long _timestamp;
        private int _timerCreationCount;

        public ManualTimeProvider(DateTimeOffset utcNow) => _utcNow = utcNow;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public int TimerCreationCount => Volatile.Read(ref _timerCreationCount);

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate) return _utcNow;
        }

        public override long GetTimestamp()
        {
            lock (_gate) return _timestamp;
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state,
            TimeSpan dueTime, TimeSpan period)
        {
            ArgumentNullException.ThrowIfNull(callback);
            var timer = new ManualTimer(this, callback, state);
            lock (_gate)
            {
                _timers.Add(timer);
                Interlocked.Increment(ref _timerCreationCount);
                timer.ChangeUnsafe(dueTime, period, _timestamp);
            }
            return timer;
        }

        public void JumpUtc(TimeSpan delta)
        {
            lock (_gate) _utcNow = _utcNow.Add(delta);
        }

        public void Advance(TimeSpan delta)
        {
            if (delta < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(delta));
            lock (_gate)
            {
                _utcNow = _utcNow.Add(delta);
                _timestamp = checked(_timestamp + delta.Ticks);
            }

            while (true)
            {
                List<(TimerCallback Callback, object? State)> callbacks = [];
                lock (_gate)
                {
                    foreach (var timer in _timers)
                    {
                        if (timer.TryTakeCallbackUnsafe(_timestamp,
                            out var callback, out var state))
                            callbacks.Add((callback!, state));
                    }
                }

                if (callbacks.Count == 0) return;
                foreach (var callback in callbacks)
                    callback.Callback(callback.State);
            }
        }

        private sealed class ManualTimer : ITimer
        {
            private readonly ManualTimeProvider _owner;
            private readonly TimerCallback _callback;
            private readonly object? _state;
            private long _dueTimestamp = long.MaxValue;
            private long _periodTicks = -1;
            private bool _disposed;

            public ManualTimer(ManualTimeProvider owner, TimerCallback callback,
                object? state)
                => (_owner, _callback, _state) = (owner, callback, state);

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (_owner._gate)
                {
                    if (_disposed) return false;
                    ChangeUnsafe(dueTime, period, _owner._timestamp);
                    return true;
                }
            }

            public void Dispose()
            {
                lock (_owner._gate) _disposed = true;
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            internal void ChangeUnsafe(TimeSpan dueTime, TimeSpan period,
                long currentTimestamp)
            {
                ValidateTimerDuration(dueTime, nameof(dueTime));
                ValidateTimerDuration(period, nameof(period));
                _periodTicks = period == Timeout.InfiniteTimeSpan
                    ? -1
                    : period.Ticks;
                _dueTimestamp = dueTime == Timeout.InfiniteTimeSpan
                    ? long.MaxValue
                    : checked(currentTimestamp + dueTime.Ticks);
            }

            internal bool TryTakeCallbackUnsafe(long timestamp,
                out TimerCallback? callback, out object? state)
            {
                if (_disposed || timestamp < _dueTimestamp)
                {
                    callback = null;
                    state = null;
                    return false;
                }

                callback = _callback;
                state = _state;
                _dueTimestamp = _periodTicks < 0
                    ? long.MaxValue
                    : checked(timestamp + _periodTicks);
                return true;
            }

            private static void ValidateTimerDuration(TimeSpan value, string label)
            {
                if (value < TimeSpan.Zero && value != Timeout.InfiniteTimeSpan)
                    throw new ArgumentOutOfRangeException(label);
            }
        }
    }

    private static SuiteDeviceDescriptor FixtureDescriptor()
        => new(
            1,
            DeviceId,
            "SOFTWARE_BOUND_ONLINE",
            SuiteOnlineLicenseProtocol.SigningAlgorithm,
            new string('A', 344),
            HardwareFingerprint,
            "25.0.0.0");

    private static void ParseError(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        try { _ = SuiteOnlineLicenseProtocol.ParseErrorResponse(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static string LowerSha256(ReadOnlySpan<byte> bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void ExpectSecurity(Action action, string label)
        => Throws<SecurityException>(action, label);

    private static void Throws<TException>(Action action, string label)
        where TException : Exception
    {
        try { action(); }
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

    private sealed class FixedUtcTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedUtcTimeProvider(DateTimeOffset utcNow) => _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}
