using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using TurboBoxManager.Licensing;

namespace TurboBoxManager.Catalog;

internal delegate TResponse SuiteContentAssertionParser<TResponse>(
    ReadOnlySpan<byte> body, string challengeId);

internal sealed class SuiteAuthorizedCatalog
{
    internal static SuiteAuthorizedCatalog Empty { get; } = new(
        new string('0', 64),
        1,
        new ReadOnlyDictionary<string, CatalogArtifactDescriptor>(
            new Dictionary<string, CatalogArtifactDescriptor>(
                StringComparer.Ordinal)),
        new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(StringComparer.Ordinal)),
        requiresCompleteCoverage: false);

    internal SuiteAuthorizedCatalog(
        string catalogIdentity,
        long catalogSequence,
        IReadOnlyDictionary<string, CatalogArtifactDescriptor> descriptors,
        IReadOnlyDictionary<string, string> maintenanceItems,
        bool requiresCompleteCoverage = true)
    {
        CatalogIdentity = catalogIdentity;
        CatalogSequence = catalogSequence;
        Descriptors = descriptors;
        MaintenanceItems = maintenanceItems;
        RequiresCompleteCoverage = requiresCompleteCoverage;
    }

    internal string CatalogIdentity { get; }
    internal long CatalogSequence { get; }
    internal IReadOnlyDictionary<string, CatalogArtifactDescriptor> Descriptors { get; }
    internal IReadOnlyDictionary<string, string> MaintenanceItems { get; }
    internal bool RequiresCompleteCoverage { get; }
}

internal sealed class SuiteCatalogSnapshotAccumulator
{
    private readonly IReadOnlyDictionary<string, bool> _expectedItems;
    private readonly Dictionary<string, CatalogArtifactDescriptor> _descriptors =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _maintenanceItems =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> _seenItems = new(StringComparer.Ordinal);
    private string? _catalogIdentity;
    private long _catalogSequence;

    internal SuiteCatalogSnapshotAccumulator(
        IReadOnlyDictionary<string, bool> expectedItems)
        => _expectedItems = expectedItems
            ?? throw new ArgumentNullException(nameof(expectedItems));

    internal void Apply(SuiteCatalogPageAssertion assertion)
    {
        ArgumentNullException.ThrowIfNull(assertion);
        if (_catalogIdentity is null)
        {
            _catalogIdentity = assertion.CatalogIdentity;
            _catalogSequence = assertion.CatalogSequence;
        }
        else if (!SuiteOnlineLicenseProtocol.FixedHexEquals(
                     _catalogIdentity, assertion.CatalogIdentity)
                 || _catalogSequence != assertion.CatalogSequence)
        {
            throw new SecurityException(
                "O snapshot do catalogo mudou durante a leitura.");
        }

        foreach (var authorizedItem in assertion.Items)
        {
            if (!_expectedItems.TryGetValue(authorizedItem.ItemId,
                    out var expectedExtraction))
                throw new SecurityException(
                    "A autoridade retornou um item fora do catalogo publico.");
            if (!_seenItems.Add(authorizedItem.ItemId))
                throw new SecurityException(
                    "A autoridade repetiu um item no snapshot do catalogo.");

            if (string.Equals(authorizedItem.Availability,
                    SuiteContentProtocol.ReadyAvailability,
                    StringComparison.Ordinal))
            {
                var descriptor = SuiteContentProtocol.ToCatalogDescriptor(
                    authorizedItem.Descriptor
                    ?? throw new SecurityException(
                        "Um item READY nao possui descritor."));
                if (!SuiteOnlineLicenseProtocol.FixedHexEquals(
                        descriptor.ManifestIdentity,
                        assertion.CatalogIdentity))
                    throw new SecurityException(
                        "O descritor nao pertence ao manifest deste snapshot.");
                if (expectedExtraction
                    != (descriptor.ExtractPolicy
                        == CatalogExtractPolicy.ExtractArchive))
                    throw new SecurityException(
                        "A politica assinada diverge do catalogo publico.");
                _descriptors.Add(authorizedItem.ItemId, descriptor);
            }
            else if (string.Equals(authorizedItem.Availability,
                         SuiteContentProtocol.MaintenanceAvailability,
                         StringComparison.Ordinal)
                     && string.Equals(authorizedItem.ReasonCode,
                         SuiteContentProtocol.MaintenanceReasonCode,
                         StringComparison.Ordinal)
                     && authorizedItem.Descriptor is null)
            {
                _maintenanceItems.Add(
                    authorizedItem.ItemId,
                    SuiteContentProtocol.MaintenanceReasonCode);
            }
            else
            {
                throw new SecurityException(
                    "A disponibilidade assinada do item e invalida.");
            }

            if (_seenItems.Count > _expectedItems.Count)
                throw new SecurityException(
                    "A autoridade excedeu o catalogo publico aprovado.");
        }
    }

    internal SuiteAuthorizedCatalog Complete()
    {
        if (_catalogIdentity is null)
            throw new SecurityException(
                "A autoridade nao identificou o snapshot do catalogo.");
        if (_seenItems.Count != _expectedItems.Count
            || _descriptors.Count + _maintenanceItems.Count
            != _expectedItems.Count)
            throw new SecurityException(
                "O snapshot autorizado nao cobre integralmente o catalogo publico.");
        return new SuiteAuthorizedCatalog(
            _catalogIdentity,
            _catalogSequence,
            new ReadOnlyDictionary<string, CatalogArtifactDescriptor>(
                _descriptors),
            new ReadOnlyDictionary<string, string>(_maintenanceItems));
    }
}

/// <summary>
/// Reads signed content metadata and mints ephemeral same-origin GET requests.
/// Upstream URLs are neither accepted from the server nor represented by any
/// client-side model.
/// </summary>
internal sealed class SuiteContentClient : IDisposable
{
    private const int MaximumCatalogPages = 64;
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(20);

    private readonly SuiteLicenseClient _licenseClient;
    private readonly Uri _baseUri;
    private readonly string _contentAssertionKeyId;
    private readonly byte[] _contentAssertionSpki;
    private readonly HttpClient _downloadHttpClient;
    private int _disposed;

    internal SuiteContentClient(
        SuiteLicenseClient licenseClient,
        SuiteContentAuthorityConfiguration authority,
        HttpMessageHandler? downloadHandler = null)
    {
        _licenseClient = licenseClient
            ?? throw new ArgumentNullException(nameof(licenseClient));
        ArgumentNullException.ThrowIfNull(authority);
        _baseUri = authority.BaseUri;
        _contentAssertionKeyId = authority.ContentAssertionKeyId;
        _contentAssertionSpki = authority.ExportContentAssertionSpki();
        _downloadHttpClient = downloadHandler is null
            ? new HttpClient(
                CreateContentHandler(authority.TlsServerSpkiSha256Pins),
                disposeHandler: true)
            : new HttpClient(downloadHandler, disposeHandler: true);
        _downloadHttpClient.BaseAddress = _baseUri;
        _downloadHttpClient.Timeout = Timeout.InfiniteTimeSpan;
        _downloadHttpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "TurboramaSuite/2.0");
        _downloadHttpClient.DefaultRequestHeaders.Accept.Clear();
    }

    internal HttpClient DownloadHttpClient => _downloadHttpClient;
    internal string AuthorityHost => _baseUri.IdnHost;

    internal async Task<SuiteAuthorizedCatalog> ReadAuthorizedCatalogAsync(
        AuthorizedStoreContext authorization,
        IReadOnlyDictionary<string, bool> expectedItems,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(expectedItems);
        authorization.ThrowIfUnauthorized();
        if (expectedItems.Count != SuiteContentProtocol.ExpectedCatalogItemCount)
            throw new SecurityException(
                "O catalogo publico nao possui os 902 itens aprovados.");

        var accumulator = new SuiteCatalogSnapshotAccumulator(expectedItems);
        var cursors = new HashSet<string>(StringComparer.Ordinal) { string.Empty };
        string cursor = string.Empty;

        for (var pageNumber = 0; pageNumber < MaximumCatalogPages; pageNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            authorization.ThrowIfUnauthorized();
            var context = new SuiteCatalogPageContext(
                SuiteOnlineLicenseProtocol.SchemaVersion,
                SuiteOnlineLicenseProtocol.ProductId,
                authorization.LicenseId,
                authorization.DeviceId,
                authorization.SessionId,
                SuiteContentProtocol.CatalogAction,
                cursor,
                SuiteContentProtocol.MaximumPageSize);
            var contextHash = SuiteContentProtocol.CatalogPageContextHash(context);
            var assertion = await ExecuteAsync(
                context,
                contextHash,
                SuiteContentProtocol.CatalogAction,
                SuiteContentProtocol.CatalogRoute,
                (body, challengeId) => SuiteContentProtocol.ParseCatalogPageAssertion(
                    body,
                    _contentAssertionSpki,
                    _contentAssertionKeyId,
                    context,
                    contextHash,
                    challengeId,
                    _licenseClient.NowUnixSeconds()),
                cancellationToken).ConfigureAwait(false);
            authorization.ThrowIfUnauthorized();

            accumulator.Apply(assertion);

            if (assertion.NextCursor is null)
            {
                return accumulator.Complete();
            }

            if (assertion.Items.Count == 0
                || !cursors.Add(assertion.NextCursor))
                throw new SecurityException(
                    "A paginacao do catalogo nao progride de forma canonica.");
            cursor = assertion.NextCursor;
        }

        throw new SecurityException(
            "A autoridade excedeu o limite de paginas do catalogo.");
    }

    internal ICatalogDownloadRequestProvider CreateRequestProvider(
        AuthorizedStoreContext authorization,
        SuiteAuthorizedCatalog catalog,
        Action validateCurrentContext)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0, this);
        return new SuiteCatalogDownloadRequestProvider(
            this, authorization, catalog, validateCurrentContext);
    }

    internal static HttpRequestMessage BuildDownloadRequest(
        Uri baseUri,
        SuiteDownloadGrantAssertion grant,
        CatalogArtifactDescriptor artifact,
        long offset,
        CatalogDownloadValidators validators)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(validators);
        var expectedPath = SuiteContentProtocol.ArtifactRoutePrefix
            + grant.GrantId;
        if (!string.Equals(grant.ContentPath, expectedPath,
                StringComparison.Ordinal)
            || grant.ContentPath.Contains('%')
            || grant.ContentPath.Contains('\\'))
            throw new SecurityException("O caminho temporario nao e confiavel.");
        var destination = new Uri(baseUri, grant.ContentPath);
        if (!string.Equals(destination.Scheme, Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase)
            || destination.Port != 443
            || !string.Equals(destination.IdnHost, baseUri.IdnHost,
                StringComparison.OrdinalIgnoreCase)
            || destination.UserInfo.Length != 0
            || destination.Query.Length != 0
            || destination.Fragment.Length != 0)
            throw new SecurityException(
                "O grant tentou sair da autoridade configurada.");

        var request = new HttpRequestMessage(HttpMethod.Get, destination);
        try
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer", grant.BearerToken);
            if (offset > 0)
            {
                request.Headers.Range = new RangeHeaderValue(offset, null);
                if (validators.ETag.Length != 0)
                    request.Headers.IfRange = new RangeConditionHeaderValue(
                        new EntityTagHeaderValue(validators.ETag));
                else if (validators.LastModified.Length != 0 &&
                         DateTimeOffset.TryParse(validators.LastModified,
                             CultureInfo.InvariantCulture,
                             DateTimeStyles.AssumeUniversal, out var lastModified))
                    request.Headers.IfRange = new RangeConditionHeaderValue(lastModified);
            }
            return request;
        }
        catch
        {
            request.Dispose();
            throw;
        }
    }

    private async Task<SuiteDownloadGrantAssertion> AuthorizeDownloadAsync(
        AuthorizedStoreContext authorization,
        string catalogIdentity,
        string itemId,
        CatalogArtifactDescriptor descriptor,
        long offset,
        CatalogDownloadValidators validators,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0, this);
        authorization.ThrowIfUnauthorized();
        if (offset < 0)
            throw new SecurityException("O offset solicitado nao e autorizado.");
        var wireDescriptor = SuiteContentProtocol.ToWireDescriptor(descriptor);
        var context = new SuiteDownloadGrantContext(
            SuiteOnlineLicenseProtocol.SchemaVersion,
            SuiteOnlineLicenseProtocol.ProductId,
            authorization.LicenseId,
            authorization.DeviceId,
            authorization.SessionId,
            SuiteContentProtocol.DownloadAction,
            catalogIdentity,
            itemId,
            descriptor.ArtifactId,
            descriptor.ArtifactVersion,
            descriptor.ManifestIdentity,
            SuiteContentProtocol.DescriptorHash(itemId, wireDescriptor),
            offset,
            validators.ETag,
            validators.LastModified);
        var contextHash = SuiteContentProtocol.DownloadGrantContextHash(context);
        var grant = await ExecuteAsync(
            context,
            contextHash,
            SuiteContentProtocol.DownloadAction,
            SuiteContentProtocol.DownloadAuthorizeRoute,
            (body, challengeId) => SuiteContentProtocol.ParseDownloadGrantAssertion(
                body,
                _contentAssertionSpki,
                _contentAssertionKeyId,
                context,
                contextHash,
                challengeId,
                _licenseClient.NowUnixSeconds()),
            cancellationToken).ConfigureAwait(false);
        authorization.ThrowIfUnauthorized();
        return grant;
    }

    private async Task<TResponse> ExecuteAsync<TContext, TResponse>(
        TContext context,
        string contextHash,
        string action,
        string route,
        SuiteContentAssertionParser<TResponse> parse,
        CancellationToken cancellationToken)
        where TContext : notnull
    {
        string licenseId;
        string deviceId;
        string sessionId;
        switch (context)
        {
            case SuiteCatalogPageContext catalog:
                (licenseId, deviceId, sessionId) =
                    (catalog.LicenseId, catalog.DeviceId, catalog.SessionId);
                break;
            case SuiteDownloadGrantContext download:
                (licenseId, deviceId, sessionId) =
                    (download.LicenseId, download.DeviceId, download.SessionId);
                break;
            default:
                throw new SecurityException("O contexto de conteudo e invalido.");
        }

        var challenge = await _licenseClient.RequestOperationChallengeAsync(
            licenseId, deviceId, sessionId, action, contextHash,
            cancellationToken).ConfigureAwait(false);
        var proof = _licenseClient.CreateOperationProof(
            challenge, licenseId, deviceId, sessionId, action, contextHash);
        return await PostContentAsync(
            route,
            new SuiteContentProof<TContext>(proof, context),
            body => parse(body, challenge.ChallengeId),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<TResponse> PostContentAsync<TRequest, TResponse>(
        string route,
        TRequest request,
        SuiteResponseParser<TResponse> parse,
        CancellationToken cancellationToken)
    {
        var requestBytes = SuiteOnlineLicenseProtocol.SerializeRequest(request);
        using var content = new ZeroingJsonContent(requestBytes);
        using var message = new HttpRequestMessage(HttpMethod.Post, route)
        {
            Content = content
        };
        message.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(MetadataTimeout);
        using var response = await _downloadHttpClient.SendAsync(
            message,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token).ConfigureAwait(false);
        ValidateMetadataResponseHeaders(response);
        var responseBytes = await ReadBoundedAsync(
            response.Content,
            SuiteOnlineLicenseProtocol.MaximumBodyBytes,
            timeout.Token).ConfigureAwait(false);
        try
        {
            if (!response.IsSuccessStatusCode)
                throw CreateDeniedException(response.StatusCode, responseBytes);
            try { return parse(responseBytes); }
            catch (SecurityException exception)
            {
                throw new SuiteApiException(
                    502,
                    "INVALID_CONTENT_RESPONSE",
                    "A autoridade de conteudo retornou uma resposta invalida.",
                    exception);
            }
        }
        finally { CryptographicOperations.ZeroMemory(responseBytes); }
    }

    private static SocketsHttpHandler CreateContentHandler(
        IReadOnlyList<string> pins)
    {
        ArgumentNullException.ThrowIfNull(pins);
        if (pins.Count is < 1 or > 2)
            throw new SecurityException("O pinset TLS de conteudo e invalido.");
        var canonicalPins = pins.Select(pin =>
        {
            var canonical = SuiteOnlineLicenseProtocol.RequireHex(
                pin, "TlsServerSpkiSha256", 64);
            if (!string.Equals(canonical, pin, StringComparison.Ordinal))
                throw new SecurityException("O pin TLS nao esta canonico.");
            return canonical;
        }).Distinct(StringComparer.Ordinal).ToArray();
        if (canonicalPins.Length != pins.Count)
            throw new SecurityException("O pinset TLS possui repeticao.");

        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            UseProxy = false,
            SslOptions =
            {
                CertificateRevocationCheckMode = X509RevocationMode.Online,
                RemoteCertificateValidationCallback =
                    (_, certificate, chain, errors) => canonicalPins.Any(pin =>
                        SuiteLicenseClient.ValidatePinnedServerCertificate(
                            certificate, chain, errors, pin))
            },
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 4
        };
    }

    private static void ValidateMetadataResponseHeaders(
        HttpResponseMessage response)
    {
        if (response.Content.Headers.ContentLength is >
            SuiteOnlineLicenseProtocol.MaximumBodyBytes
            || response.Content.Headers.ContentEncoding.Count != 0)
            throw new SuiteApiException(502, "INVALID_CONTENT_RESPONSE",
                "A resposta de conteudo excedeu a politica permitida.");
        var contentType = response.Content.Headers.ContentType;
        if (contentType is null
            || !string.Equals(contentType.MediaType, "application/json",
                StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(contentType.CharSet)
            && !string.Equals(contentType.CharSet.Trim('"'), "utf-8",
                StringComparison.OrdinalIgnoreCase))
            throw new SuiteApiException(502, "INVALID_CONTENT_RESPONSE",
                "A resposta de conteudo nao e JSON UTF-8.");
    }

    private static SuiteApiException CreateDeniedException(
        HttpStatusCode statusCode,
        ReadOnlySpan<byte> responseBytes)
    {
        var code = "CONTENT_DENIED";
        try
        {
            var error = SuiteOnlineLicenseProtocol.ParseErrorResponse(responseBytes);
            if (error.SchemaVersion == SuiteOnlineLicenseProtocol.SchemaVersion
                && error.Code is { Length: >= 1 and <= 64 }
                && error.Code.All(character => character is >= 'A' and <= 'Z'
                    or >= '0' and <= '9' or '_'))
                code = error.Code;
        }
        catch (SecurityException) { }
        return new SuiteApiException((int)statusCode, code,
            "A autoridade de conteudo recusou esta operacao.");
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
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
                    throw new SuiteApiException(502, "INVALID_CONTENT_RESPONSE",
                        "A resposta de conteudo excedeu o limite.");
                var read = await input.ReadAsync(
                    buffer.AsMemory(0, Math.Min(buffer.Length, remaining)),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0) return output.ToArray();
                output.Write(buffer, 0, read);
                if (output.Length > maximumBytes)
                    throw new SuiteApiException(502, "INVALID_CONTENT_RESPONSE",
                        "A resposta de conteudo excedeu o limite.");
            }
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    private sealed class ZeroingJsonContent : HttpContent
    {
        private byte[]? _buffer;

        internal ZeroingJsonContent(byte[] buffer)
        {
            _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
            Headers.ContentType = new MediaTypeHeaderValue("application/json")
            {
                CharSet = "utf-8"
            };
        }

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context)
        {
            var current = _buffer
                ?? throw new ObjectDisposedException(nameof(ZeroingJsonContent));
            return stream.WriteAsync(current, 0, current.Length);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _buffer?.LongLength ?? 0;
            return true;
        }

        protected override void Dispose(bool disposing)
        {
            var current = Interlocked.Exchange(ref _buffer, null);
            if (current is not null) CryptographicOperations.ZeroMemory(current);
            base.Dispose(disposing);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _downloadHttpClient.Dispose();
    }

    private sealed class SuiteCatalogDownloadRequestProvider(
        SuiteContentClient owner,
        AuthorizedStoreContext authorization,
        SuiteAuthorizedCatalog catalog,
        Action validateCurrentContext) : ICatalogDownloadRequestProvider
    {
        public async ValueTask<HttpRequestMessage> CreateRequestAsync(
            string itemId,
            CatalogArtifactDescriptor artifact,
            long offset,
            CatalogDownloadValidators validators,
            CancellationToken cancellationToken)
        {
            validateCurrentContext();
            authorization.ThrowIfUnauthorized();
            if (!catalog.Descriptors.TryGetValue(itemId, out var expected)
                || expected.ArtifactId != artifact.ArtifactId
                || expected.ArtifactVersion != artifact.ArtifactVersion
                || expected.SafeFileName != artifact.SafeFileName
                || expected.FileExtension != artifact.FileExtension
                || expected.ExtractPolicy != artifact.ExtractPolicy
                || expected.ManifestIdentity != artifact.ManifestIdentity)
                throw new SecurityException(
                    "O item nao corresponde ao snapshot autorizado.");

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                authorization.AuthorizationCancellationToken);
            var grant = await owner.AuthorizeDownloadAsync(
                authorization,
                catalog.CatalogIdentity,
                itemId,
                artifact,
                offset,
                validators,
                linked.Token).ConfigureAwait(false);
            validateCurrentContext();
            authorization.ThrowIfUnauthorized();

            return BuildDownloadRequest(
                owner._baseUri, grant, artifact, offset, validators);
        }
    }
}
