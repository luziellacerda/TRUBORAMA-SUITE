using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace TurboBoxManager.Licensing;

internal delegate TResponse SuiteResponseParser<TResponse>(ReadOnlySpan<byte> utf8);

public sealed class SuiteApiException : Exception
{
    internal SuiteApiException(int statusCode, string code, string message,
        Exception? innerException = null) : base(message, innerException)
        => (StatusCode, Code) = (statusCode, code);

    public int StatusCode { get; }
    public string Code { get; }
}

public sealed class SuiteActivationIndeterminateException : Exception
{
    internal SuiteActivationIndeterminateException(string message, Exception? innerException = null)
        : base(message, innerException) { }
}

internal sealed class SuiteLicenseClient : IDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    private readonly ISuiteMachineIdentity _identity;
    private readonly HttpClient _http;
    private readonly TimeProvider _timeProvider;
    private readonly string _onlineAssertionKeyId;
    private readonly byte[] _onlineAssertionSpki;
    private readonly object _challengeGate = new();
    private readonly Dictionary<string, long> _activeChallengeExpirations =
        new(StringComparer.Ordinal);

    internal SuiteLicenseClient(SuiteAuthorityConfiguration authority,
        ISuiteMachineIdentity identity, TimeProvider? timeProvider = null)
        : this(authority, identity, handler: null, timeProvider)
    {
    }

    internal static SuiteLicenseClient CreateForVerifier(
        SuiteAuthorityConfiguration authority,
        ISuiteMachineIdentity identity,
        HttpMessageHandler handler,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return new SuiteLicenseClient(authority, identity, handler, timeProvider);
    }

    private SuiteLicenseClient(SuiteAuthorityConfiguration authority,
        ISuiteMachineIdentity identity, HttpMessageHandler? handler,
        TimeProvider? timeProvider)
    {
        ArgumentNullException.ThrowIfNull(authority);
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _onlineAssertionKeyId = authority.OnlineAssertionKeyId;
        _onlineAssertionSpki = authority.ExportOnlineAssertionSpki();
        _http = handler is null
            ? new HttpClient(CreateHandler(authority.TlsServerSpkiSha256),
                disposeHandler: true)
            : new HttpClient(handler, disposeHandler: true);
        _http.BaseAddress = authority.BaseUri;
        _http.Timeout = DefaultTimeout;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("TurboramaSuite/2.0");
        _http.DefaultRequestHeaders.Accept.Clear();
        _http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public Task ActivateAsync(string licenseId, string activationCode,
        CancellationToken cancellationToken)
    {
        var normalizedLicenseId = RequireCanonicalLicenseId(licenseId);
        if (activationCode is null || activationCode.Length is < 16 or > 128
            || activationCode.Any(char.IsWhiteSpace))
            throw new SecurityException("O codigo de ativacao possui formato invalido.");
        var device = UseMachineIdentity(_identity.Describe);
        var contextHash = SuiteOnlineLicenseProtocol.ActivationContextHash(
            normalizedLicenseId, device);
        var challengeTask = RequestActivationChallengeAsync(normalizedLicenseId,
            activationCode, device, contextHash, cancellationToken);
        return CompleteActivationAfterChallengeAsync(challengeTask,
            normalizedLicenseId, device, contextHash, cancellationToken);
    }

    private async Task<SuiteChallengeResponse> RequestActivationChallengeAsync(
        string licenseId, string activationCode, SuiteDeviceDescriptor device,
        string contextHash,
        CancellationToken cancellationToken)
    {
        var challenge = await PostAsync(
            SuiteOnlineLicenseProtocol.ActivationChallengeRoute,
            new SuiteActivationChallengeRequest(
                SuiteOnlineLicenseProtocol.SchemaVersion,
                SuiteOnlineLicenseProtocol.ProductId,
                licenseId,
                activationCode,
                device),
            bytes => SuiteOnlineLicenseProtocol.ParseActivationChallengeAssertion(
                bytes, _onlineAssertionSpki, _onlineAssertionKeyId,
                licenseId, device.DeviceId, contextHash, NowUnixSeconds()),
            cancellationToken).ConfigureAwait(false);
        RegisterChallenge(challenge);
        return challenge;
    }

    private async Task CompleteActivationAfterChallengeAsync(
        Task<SuiteChallengeResponse> challengeTask, string licenseId,
        SuiteDeviceDescriptor device, string contextHash,
        CancellationToken cancellationToken)
    {
        var challenge = await challengeTask.ConfigureAwait(false);
        challengeTask = null!;

        var signature = UseMachineIdentity(() => _identity.Sign(challenge,
            licenseId, "", "device.activate", contextHash));

        SuiteActivationResult result;
        try
        {
            result = await PostAsync(
                SuiteOnlineLicenseProtocol.ActivationCompleteRoute,
                new SuiteActivationProof(
                    SuiteOnlineLicenseProtocol.SchemaVersion,
                    SuiteOnlineLicenseProtocol.ProductId,
                    licenseId,
                    challenge.ChallengeId,
                    device,
                    signature),
                bytes => SuiteOnlineLicenseProtocol.ParseActivationResultAssertion(
                    bytes, _onlineAssertionSpki, _onlineAssertionKeyId,
                    licenseId, device.DeviceId, device.BindingType, contextHash,
                    challenge.ChallengeId, NowUnixSeconds()),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested
            && (ex is HttpRequestException or TaskCanceledException
                || ex is SuiteApiException { StatusCode: >= 500 }))
        {
            throw new SuiteActivationIndeterminateException(
                "A resposta final da ativacao ficou inconclusiva; a sessao deve ser conferida com a mesma identidade.",
                ex);
        }

        if (result.SchemaVersion != SuiteOnlineLicenseProtocol.SchemaVersion
            || !string.Equals(result.Status, "ACTIVE", StringComparison.Ordinal)
            || !SuiteOnlineLicenseProtocol.FixedHexEquals(result.DeviceId, device.DeviceId)
            || !string.Equals(result.BindingType, device.BindingType, StringComparison.Ordinal))
            throw new SuiteActivationIndeterminateException(
                "A autoridade nao confirmou de forma conclusiva a identidade ativada.");
    }

    public async Task<SuiteSessionResponse> OpenSessionAsync(string licenseId,
        string sessionId, bool heartbeat, CancellationToken cancellationToken)
    {
        var normalizedLicenseId = RequireCanonicalLicenseId(licenseId);
        var normalizedSessionId = SuiteOnlineLicenseProtocol.RequireHex(
            sessionId, "SessionId", 64);
        var device = UseMachineIdentity(_identity.Describe);
        var action = heartbeat ? "session.heartbeat" : "session.open";
        var context = new SuiteSessionContext(
            SuiteOnlineLicenseProtocol.SchemaVersion,
            SuiteOnlineLicenseProtocol.ProductId,
            normalizedLicenseId,
            device.DeviceId,
            normalizedSessionId,
            action,
            device.HardwareFingerprint,
            device.AgentVersion);
        var contextHash = SuiteOnlineLicenseProtocol.ContextHash(context);

        var challenge = await PostAsync(
            SuiteOnlineLicenseProtocol.ChallengeRoute,
            new SuiteChallengeRequest(
                SuiteOnlineLicenseProtocol.SchemaVersion,
                SuiteOnlineLicenseProtocol.ProductId,
                normalizedLicenseId,
                device.DeviceId,
                normalizedSessionId,
                action,
                contextHash),
            bytes => SuiteOnlineLicenseProtocol.ParseOperationChallengeAssertion(
                bytes, _onlineAssertionSpki, _onlineAssertionKeyId,
                normalizedLicenseId, device.DeviceId, normalizedSessionId,
                action, contextHash, NowUnixSeconds()),
            cancellationToken).ConfigureAwait(false);
        RegisterChallenge(challenge);

        var proof = new SuiteOperationProof(
            SuiteOnlineLicenseProtocol.SchemaVersion,
            SuiteOnlineLicenseProtocol.ProductId,
            normalizedLicenseId,
            device.DeviceId,
            normalizedSessionId,
            action,
            contextHash,
            challenge.ChallengeId,
            UseMachineIdentity(() => _identity.Sign(challenge,
                normalizedLicenseId, normalizedSessionId, action, contextHash)));

        var response = await PostAsync(
            SuiteOnlineLicenseProtocol.SuiteSessionRoute,
            new SuiteSessionProof(proof, context),
            bytes => SuiteOnlineLicenseProtocol.ParseSessionAssertion(
                bytes, _onlineAssertionSpki, _onlineAssertionKeyId,
                normalizedLicenseId, device.DeviceId, normalizedSessionId,
                action, contextHash, challenge.ChallengeId, NowUnixSeconds()),
            cancellationToken).ConfigureAwait(false);
        SuiteOnlineLicenseProtocol.ValidateSessionResponse(response,
            normalizedLicenseId, device.DeviceId, normalizedSessionId);
        return response;
    }

    internal async Task<SuiteChallengeResponse> RequestOperationChallengeAsync(
        string licenseId,
        string deviceId,
        string sessionId,
        string action,
        string contextHash,
        CancellationToken cancellationToken)
    {
        var normalizedLicenseId = RequireCanonicalLicenseId(licenseId);
        var normalizedDeviceId = SuiteOnlineLicenseProtocol.RequireHex(
            deviceId, "DeviceId", 64);
        var normalizedSessionId = SuiteOnlineLicenseProtocol.RequireHex(
            sessionId, "SessionId", 64);
        var normalizedContextHash = SuiteOnlineLicenseProtocol.RequireHex(
            contextHash, "ContextHash", 64);
        var device = UseMachineIdentity(_identity.Describe);
        if (!SuiteOnlineLicenseProtocol.FixedHexEquals(
                normalizedDeviceId, device.DeviceId))
            throw new SecurityException(
                "A identidade atual nao corresponde a sessao autorizada.");

        var challenge = await PostAsync(
            SuiteOnlineLicenseProtocol.ChallengeRoute,
            new SuiteChallengeRequest(
                SuiteOnlineLicenseProtocol.SchemaVersion,
                SuiteOnlineLicenseProtocol.ProductId,
                normalizedLicenseId,
                normalizedDeviceId,
                normalizedSessionId,
                action,
                normalizedContextHash),
            bytes => SuiteOnlineLicenseProtocol.ParseOperationChallengeAssertion(
                bytes, _onlineAssertionSpki, _onlineAssertionKeyId,
                normalizedLicenseId, normalizedDeviceId, normalizedSessionId,
                action, normalizedContextHash, NowUnixSeconds()),
            cancellationToken).ConfigureAwait(false);
        RegisterChallenge(challenge);
        return challenge;
    }

    internal SuiteOperationProof CreateOperationProof(
        SuiteChallengeResponse challenge,
        string licenseId,
        string deviceId,
        string sessionId,
        string action,
        string contextHash)
    {
        var normalizedLicenseId = RequireCanonicalLicenseId(licenseId);
        var normalizedDeviceId = SuiteOnlineLicenseProtocol.RequireHex(
            deviceId, "DeviceId", 64);
        var normalizedSessionId = SuiteOnlineLicenseProtocol.RequireHex(
            sessionId, "SessionId", 64);
        var normalizedContextHash = SuiteOnlineLicenseProtocol.RequireHex(
            contextHash, "ContextHash", 64);
        var device = UseMachineIdentity(_identity.Describe);
        if (!SuiteOnlineLicenseProtocol.FixedHexEquals(
                normalizedDeviceId, device.DeviceId))
            throw new SecurityException(
                "A identidade atual nao corresponde a sessao autorizada.");

        return new SuiteOperationProof(
            SuiteOnlineLicenseProtocol.SchemaVersion,
            SuiteOnlineLicenseProtocol.ProductId,
            normalizedLicenseId,
            normalizedDeviceId,
            normalizedSessionId,
            action,
            normalizedContextHash,
            challenge.ChallengeId,
            UseMachineIdentity(() => _identity.Sign(challenge,
                normalizedLicenseId, normalizedSessionId, action,
                normalizedContextHash)));
    }

    public void Dispose()
    {
        _http.Dispose();
    }

    private static T UseMachineIdentity<T>(Func<T> operation)
    {
        try { return operation(); }
        catch (Exception ex) when (ex is PlatformNotSupportedException
            or CryptographicException or SecurityException
            or UnauthorizedAccessException or NotSupportedException
            or ArgumentException)
        {
            throw new SuiteLicensingUnavailableException(
                "IDENTITY_UNAVAILABLE", ex);
        }
    }

    private static string RequireCanonicalLicenseId(string? licenseId)
    {
        var normalized = SuiteOnlineLicenseProtocol.RequireIdentifier(
            licenseId, "LicenseId", 6, 64);
        if (!string.Equals(normalized, licenseId, StringComparison.Ordinal))
            throw new SecurityException("LicenseId nao esta canonico.");
        return normalized;
    }

    private async Task<TResponse> PostAsync<TRequest, TResponse>(string route, TRequest request,
        SuiteResponseParser<TResponse> parse, CancellationToken cancellationToken)
    {
        var requestBytes = SuiteOnlineLicenseProtocol.SerializeRequest(request);
        using var content = new ZeroingJsonContent(requestBytes);
        using var message = new HttpRequestMessage(HttpMethod.Post, route) { Content = content };
        using var response = await _http.SendAsync(message,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        ValidateResponseHeaders(response);
        byte[] responseBytes;
        using (var bodyTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            bodyTimeout.CancelAfter(DefaultTimeout);
            try
            {
                responseBytes = await ReadBoundedAsync(
                        response.Content,
                        SuiteOnlineLicenseProtocol.MaximumBodyBytes,
                        bodyTimeout.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new SuiteApiException(
                    504,
                    "RESPONSE_BODY_TIMEOUT",
                    "A autoridade de licenciamento não concluiu a resposta no prazo permitido.",
                    exception);
            }
        }
        try
        {
            if (!response.IsSuccessStatusCode)
                throw CreateDeniedException(response.StatusCode, responseBytes);
            try { return parse(responseBytes); }
            catch (SecurityException ex)
            {
                throw new SuiteApiException(502, "INVALID_RESPONSE",
                    "A autoridade de licenciamento retornou uma resposta invalida.", ex);
            }
        }
        finally { CryptographicOperations.ZeroMemory(responseBytes); }
    }

    internal static SocketsHttpHandler CreateHandler(string tlsServerSpkiSha256)
    {
        var canonicalPin = SuiteOnlineLicenseProtocol.RequireHex(
            tlsServerSpkiSha256, "TlsServerSpkiSha256", 64);
        if (!string.Equals(canonicalPin, tlsServerSpkiSha256,
                StringComparison.Ordinal))
            throw new SecurityException("O pin TLS nao esta canonico.");

        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            UseProxy = false,
            SslOptions =
            {
                CertificateRevocationCheckMode = X509RevocationMode.Online,
                RemoteCertificateValidationCallback = (_, certificate, chain, errors) =>
                    ValidatePinnedServerCertificate(
                        certificate, chain, errors, canonicalPin)
            },
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 4
        };
    }

    internal static bool ValidatePinnedServerCertificate(
        X509Certificate? certificate, X509Chain? chain,
        SslPolicyErrors sslPolicyErrors, string tlsServerSpkiSha256)
    {
        _ = chain;
        if (certificate is null || sslPolicyErrors != SslPolicyErrors.None)
            return false;

        byte[] expectedPin = Array.Empty<byte>();
        byte[] spki = Array.Empty<byte>();
        byte[] actualPin = Array.Empty<byte>();
        X509Certificate2? ownedCertificate = null;
        try
        {
            var canonicalPin = SuiteOnlineLicenseProtocol.RequireHex(
                tlsServerSpkiSha256, "TlsServerSpkiSha256", 64);
            if (!string.Equals(canonicalPin, tlsServerSpkiSha256,
                    StringComparison.Ordinal))
                return false;
            expectedPin = Convert.FromHexString(canonicalPin);
            var certificate2 = certificate as X509Certificate2
                ?? (ownedCertificate = new X509Certificate2(certificate));
            spki = certificate2.PublicKey.ExportSubjectPublicKeyInfo();
            actualPin = SHA256.HashData(spki);
            return CryptographicOperations.FixedTimeEquals(actualPin, expectedPin);
        }
        catch (Exception ex) when (ex is CryptographicException
            or SecurityException or FormatException or ArgumentException)
        {
            return false;
        }
        finally
        {
            ownedCertificate?.Dispose();
            if (expectedPin.Length != 0) CryptographicOperations.ZeroMemory(expectedPin);
            if (spki.Length != 0) CryptographicOperations.ZeroMemory(spki);
            if (actualPin.Length != 0) CryptographicOperations.ZeroMemory(actualPin);
        }
    }

    internal long NowUnixSeconds() => _timeProvider.GetUtcNow().ToUnixTimeSeconds();

    private void RegisterChallenge(SuiteChallengeResponse challenge)
    {
        var now = NowUnixSeconds();
        lock (_challengeGate)
        {
            foreach (var expired in _activeChallengeExpirations
                .Where(item => item.Value <= now)
                .Select(item => item.Key)
                .ToArray())
                _activeChallengeExpirations.Remove(expired);

            if (challenge.ExpiresAtUnixSeconds <= now)
                throw new SecurityException("O desafio expirou antes de ser utilizado.");
            if (_activeChallengeExpirations.ContainsKey(challenge.ChallengeId))
                throw new SecurityException(
                    "A autoridade repetiu um desafio de licenciamento.");
            if (_activeChallengeExpirations.Count >= 1024)
                throw new SecurityException(
                    "A autoridade excedeu o limite de desafios simultaneos.");
            _activeChallengeExpirations.Add(
                challenge.ChallengeId, challenge.ExpiresAtUnixSeconds);
        }
    }

    private static void ValidateResponseHeaders(HttpResponseMessage response)
    {
        if (response.Content.Headers.ContentLength is >
            SuiteOnlineLicenseProtocol.MaximumBodyBytes)
            throw new SuiteApiException(502, "INVALID_RESPONSE",
                "A resposta de licenciamento excedeu o limite permitido.");
        if (response.Content.Headers.ContentEncoding.Count != 0)
            throw new SuiteApiException(502, "INVALID_RESPONSE",
                "A resposta de licenciamento usa codificacao nao permitida.");

        var contentType = response.Content.Headers.ContentType;
        if (contentType is null
            || !string.Equals(contentType.MediaType, "application/json",
                StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrEmpty(contentType.CharSet)
                && !string.Equals(contentType.CharSet.Trim('"'), "utf-8",
                    StringComparison.OrdinalIgnoreCase)))
            throw new SuiteApiException(502, "INVALID_RESPONSE",
                "A resposta de licenciamento nao e JSON UTF-8.");
    }

    private static SuiteApiException CreateDeniedException(HttpStatusCode statusCode,
        ReadOnlySpan<byte> responseBytes)
    {
        var code = "ONLINE_DENIED";
        try
        {
            var error = SuiteOnlineLicenseProtocol.ParseErrorResponse(responseBytes);
            if (error.SchemaVersion == SuiteOnlineLicenseProtocol.SchemaVersion
                && IsSafeErrorCode(error.Code))
                code = error.Code;
        }
        catch (SecurityException) { }

        return new SuiteApiException((int)statusCode, code,
            "A autoridade de licenciamento recusou ou nao confirmou esta operacao.");
    }

    private static bool IsSafeErrorCode(string? value)
        => value is { Length: >= 1 and <= 64 }
            && value.All(character => character is >= 'A' and <= 'Z'
                or >= '0' and <= '9' or '_');

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var input = await content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        try
        {
            while (true)
            {
                var remaining = maximumBytes + 1 - checked((int)output.Length);
                if (remaining <= 0)
                    throw new SuiteApiException(502, "INVALID_RESPONSE",
                        "A resposta de licenciamento excedeu o limite permitido.");
                var read = await input.ReadAsync(
                    buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0) return output.ToArray();
                output.Write(buffer, 0, read);
                if (output.Length > maximumBytes)
                    throw new SuiteApiException(502, "INVALID_RESPONSE",
                        "A resposta de licenciamento excedeu o limite permitido.");
            }
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    private sealed class ZeroingJsonContent : HttpContent
    {
        private byte[]? _buffer;

        public ZeroingJsonContent(byte[] buffer)
        {
            _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
            Headers.ContentType = new MediaTypeHeaderValue("application/json")
            {
                CharSet = "utf-8"
            };
        }

        protected override Task SerializeToStreamAsync(Stream stream,
            TransportContext? context)
        {
            var buffer = _buffer
                ?? throw new ObjectDisposedException(nameof(ZeroingJsonContent));
            return stream.WriteAsync(buffer, 0, buffer.Length);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _buffer?.LongLength ?? 0;
            return true;
        }

        protected override void Dispose(bool disposing)
        {
            var buffer = Interlocked.Exchange(ref _buffer, null);
            if (buffer is not null) CryptographicOperations.ZeroMemory(buffer);
            base.Dispose(disposing);
        }
    }
}
