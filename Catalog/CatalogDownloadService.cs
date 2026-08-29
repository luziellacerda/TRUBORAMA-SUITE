using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;
using TurboBoxManager.Licensing;

namespace TurboBoxManager.Catalog;

public sealed class CatalogDownloadOptions
{
    public long MaximumFileSizeBytes { get; init; } = 256L * 1024L * 1024L;
    // One redirect may be used after the API authorizes a direct origin download.
    public int MaximumRedirects { get; init; }
    public TimeSpan InactivityTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public IReadOnlyList<TimeSpan> RetryDelays { get; init; } =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30)];
    public IReadOnlySet<string> AllowedHosts { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}

public sealed record CatalogDownloadResult(
    CatalogDownloadState State,
    string Message,
    string LocalFilePath = "")
{
    public bool Succeeded => State == CatalogDownloadState.Completed;
    public bool WasCanceled => State is CatalogDownloadState.Paused
        or CatalogDownloadState.Canceled
        or CatalogDownloadState.Discarded;
}

public sealed record CatalogResumableDownload(
    string ItemId,
    long BytesReceived,
    long? TotalBytes,
    bool IsPaused,
    bool ArchiveReady = false,
    string ArchiveFilePath = "");

/// <summary>
/// Persistent downloader whose durable authority is an artifact descriptor,
/// never a URL. Every network attempt asks the request provider for a fresh,
/// authenticated GET; the request and its proof live in memory only.
/// </summary>
public sealed class CatalogDownloadService : IDisposable
{
    private const string ResumeSuffix = ".resume.json";
    private const string LockSuffix = ".download.lock";
    private const int ResumeSchemaVersion = 2;
    private const int BufferSize = 128 * 1024;
    private const int MaximumMetadataBytes = 64 * 1024;
    private const int MaximumImmediateRestarts = 2;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint CreateNewDisposition = 1;
    private const uint OpenExistingDisposition = 3;
    private const uint OpenAlwaysDisposition = 4;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileFlagWriteThrough = 0x80000000;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint FileFlagSequentialScan = 0x08000000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions ResumeJsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly HttpClient _httpClient;
    private readonly HttpClient _directHttpClient = CreateHttpClient();
    private readonly ICatalogDownloadRequestProvider _requestProvider;
    private readonly CatalogDownloadOptions _options;
    private readonly bool _ownsHttpClient;
    private readonly SemaphoreSlim _downloadQueue = new(1, 1);
    private readonly ConcurrentDictionary<string, ActiveDownload> _active =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lifetimeGate = new();
    private bool _disposed;
    private bool _ownedResourcesDisposed;

    public CatalogDownloadService(CatalogDownloadOptions? options = null)
        : this(CreateHttpClient(), FailClosedRequestProvider.Instance, options, ownsHttpClient: true)
    {
    }

    public CatalogDownloadService(
        ICatalogDownloadRequestProvider requestProvider,
        CatalogDownloadOptions? options = null)
        : this(CreateHttpClient(), requestProvider, options, ownsHttpClient: true)
    {
    }

    internal CatalogDownloadService(HttpClient httpClient, CatalogDownloadOptions? options = null)
        : this(httpClient, FailClosedRequestProvider.Instance, options, ownsHttpClient: false)
    {
    }

    internal CatalogDownloadService(
        HttpClient httpClient,
        ICatalogDownloadRequestProvider requestProvider,
        CatalogDownloadOptions? options = null)
        : this(httpClient, requestProvider, options, ownsHttpClient: false)
    {
    }

    private CatalogDownloadService(
        HttpClient httpClient,
        ICatalogDownloadRequestProvider requestProvider,
        CatalogDownloadOptions? options,
        bool ownsHttpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _requestProvider = requestProvider ?? throw new ArgumentNullException(nameof(requestProvider));
        _options = options ?? new CatalogDownloadOptions();
        _ownsHttpClient = ownsHttpClient;

        if (_options.MaximumFileSizeBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "O limite de download deve ser positivo.");
        if (_options.MaximumRedirects < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "O limite de redirecionamentos é inválido.");
        if (_options.InactivityTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "O tempo de inatividade deve ser positivo.");
        if (_options.RetryDelays.Count == 0
            || _options.RetryDelays.Any(delay => delay < TimeSpan.Zero || delay > MaximumRetryDelay))
            throw new ArgumentOutOfRangeException(nameof(options), "Configure pelo menos um intervalo de nova tentativa.");
        if ((_requestProvider != FailClosedRequestProvider.Instance
                && _options.AllowedHosts.Count == 0)
            || _options.AllowedHosts.Any(host => string.IsNullOrWhiteSpace(host)))
            throw new ArgumentOutOfRangeException(nameof(options), "Configure pelo menos um host autorizado.");
    }

    public bool IsActive(string itemId) => _active.ContainsKey(itemId);

    public bool Pause(string itemId)
    {
        if (!_active.TryGetValue(itemId, out var active)) return false;
        lock (active.MetadataGate)
        {
            if (active.IsDisposed) return false;
            active.PauseRequested = true;
            active.Metadata.IsPaused = true;
            if (!TrySaveResumeMetadata(active))
            {
                active.PauseRequested = false;
                active.Metadata.IsPaused = false;
                return false;
            }
            try
            {
                active.Cancellation.Cancel();
                return true;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }
    }

    public bool Cancel(string itemId) => Pause(itemId);

    public bool Discard(CatalogItem item, string installationRoot)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (_active.ContainsKey(item.Id)) return false;

        try
        {
            var artifact = RequireValidArtifact(item);
            var canonicalRoot = ValidateInstallationRoot(installationRoot);
            var canonicalDestinationPath = BuildSafeDestinationPath(canonicalRoot, item);
            var savedStates = FindResumeFileSets(canonicalRoot)
                .Where(saved => MetadataMatchesArtifact(saved.Metadata, artifact)
                                && PathsEqual(saved.DestinationPath, canonicalDestinationPath))
                .ToArray();

            if (savedStates.Length == 0)
            {
                var destinationPath = canonicalDestinationPath;
                if (!File.Exists(destinationPath)
                    && !File.Exists(destinationPath + ".part")
                    && !File.Exists(destinationPath + ".part" + ResumeSuffix))
                {
                    item.DiscardDownload();
                    return true;
                }
                savedStates =
                [new ResumeFileSet(
                    destinationPath + ".part" + ResumeSuffix,
                    destinationPath + ".part",
                    destinationPath,
                    CreateMetadata(item, artifact))];
            }

            foreach (var saved in savedStates)
            {
                using var pathLease = PathIdentity.OpenDirectoryTree(
                    Path.GetDirectoryName(saved.DestinationPath)!);
                FileStream? artifactLock = null;
                try
                {
                    artifactLock = AcquireArtifactLock(saved.DestinationPath, pathLease);
                    pathLease.Revalidate();
                    if (!TryDiscardFileSet(saved, canonicalRoot, out var failure))
                        throw new IOException(failure);
                }
                finally
                {
                    DisposeArtifactLock(artifactLock, saved.DestinationPath);
                }
            }

            item.DiscardDownload();
            return true;
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidDataException
                                           or ArgumentException
                                           or NotSupportedException)
        {
            item.SetDownloadState(CatalogDownloadState.Failed, SafeLocalFailure(exception));
            return false;
        }
    }

    public async Task<bool> MarkExtractionCompletedAsync(
        CatalogItem item,
        string installationRoot,
        string archivePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var artifact = RequireValidArtifact(item);
            if (artifact.ExtractPolicy != CatalogExtractPolicy.ExtractArchive) return false;
            var canonicalRoot = ValidateInstallationRoot(installationRoot);
            var canonicalArchivePath = Path.GetFullPath(archivePath);
            if (!IsWithinRoot(canonicalArchivePath, canonicalRoot)) return false;
            var canonicalDestinationPath = BuildSafeDestinationPath(canonicalRoot, item);
            if (!PathsEqual(canonicalArchivePath, canonicalDestinationPath)) return false;

            var matching = await Task.Run(
                    () => FindResumeFileSets(canonicalRoot, cancellationToken)
                        .Where(saved => MetadataMatchesArtifact(saved.Metadata, artifact)
                                        && saved.DestinationPath.Equals(
                                            canonicalArchivePath,
                                            StringComparison.OrdinalIgnoreCase))
                        .ToArray(),
                    cancellationToken)
                .ConfigureAwait(false);
            if (matching.Length == 0) return false;

            foreach (var saved in matching)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var pathLease = PathIdentity.OpenDirectoryTree(
                    Path.GetDirectoryName(saved.DestinationPath)!);
                FileStream? artifactLock = null;
                try
                {
                    artifactLock = AcquireArtifactLock(saved.DestinationPath, pathLease);
                    if (!await ValidateFileWithLeaseAsync(
                            canonicalArchivePath,
                            artifact,
                            pathLease,
                            cancellationToken).ConfigureAwait(false)) return false;
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!TryDeleteFileStrict(saved.MetadataPath, canonicalRoot, out _)) return false;
                }
                finally
                {
                    DisposeArtifactLock(artifactLock, saved.DestinationPath);
                }
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidDataException
                                           or ArgumentException
                                           or NotSupportedException
                                           or CryptographicException)
        {
            return false;
        }
    }

    /// <summary>
    /// Lists partial transfers only. A ready archive is not trusted without an
    /// authorized catalog descriptor; use the overload accepting catalog items
    /// when ready archives must be restored for extraction.
    /// </summary>
    public IReadOnlyList<CatalogResumableDownload> DiscoverResumableDownloads(
        string installationRoot) => DiscoverResumableDownloadsCore(
            installationRoot,
            [],
            requireAuthorizedDescriptor: false,
            CancellationToken.None);

    public IReadOnlyList<CatalogResumableDownload> DiscoverResumableDownloads(
        string installationRoot,
        IEnumerable<CatalogItem> authorizedItems) => DiscoverResumableDownloads(
            installationRoot,
            authorizedItems,
            CancellationToken.None);

    public IReadOnlyList<CatalogResumableDownload> DiscoverResumableDownloads(
        string installationRoot,
        IEnumerable<CatalogItem> authorizedItems,
        CancellationToken cancellationToken) => DiscoverResumableDownloadsCore(
            installationRoot,
            authorizedItems,
            requireAuthorizedDescriptor: true,
            cancellationToken);

    private List<CatalogResumableDownload> DiscoverResumableDownloadsCore(
        string installationRoot,
        IEnumerable<CatalogItem> authorizedItems,
        bool requireAuthorizedDescriptor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorizedItems);
        cancellationToken.ThrowIfCancellationRequested();
        var canonicalRoot = Path.GetFullPath(installationRoot);
        var authorized = new Dictionary<
            string,
            (string ItemId, CatalogArtifactDescriptor Artifact, string DestinationPath)>(
            StringComparer.Ordinal);
        foreach (var item in authorizedItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Artifact is null) continue;
            try
            {
                ValidateArtifact(item.Artifact);
                authorized[CreateArtifactIdentity(item.Artifact)] = (
                    item.Id,
                    item.Artifact,
                    BuildSafeDestinationPath(canonicalRoot, item));
            }
            catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException
                                               or InvalidDataException
                                               or ArgumentException
                                               or NotSupportedException)
            {
                // Invalid catalog entries cannot authorize durable state.
            }
        }

        var records = new List<CatalogResumableDownload>();
        foreach (var saved in FindResumeFileSets(canonicalRoot, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var identity = CreateArtifactIdentity(saved.Metadata);
                var isAuthorized = authorized.TryGetValue(identity, out var authorization)
                                   && MetadataMatchesArtifact(
                                       saved.Metadata,
                                       authorization.Artifact)
                                   && PathsEqual(
                                       saved.DestinationPath,
                                       authorization.DestinationPath);
                if (requireAuthorizedDescriptor && !isAuthorized) continue;
                var itemId = isAuthorized ? authorization.ItemId : saved.Metadata.ItemId;
                var ready = isAuthorized
                            && saved.Metadata.ArchiveReady
                            && !File.Exists(saved.PartialPath)
                            && ValidateFile(
                                saved.DestinationPath,
                                authorization.Artifact,
                                cancellationToken);
                if (ready)
                {
                    records.Add(new CatalogResumableDownload(
                        itemId,
                        authorization.Artifact.ContentLength,
                        authorization.Artifact.ContentLength,
                        true,
                        true,
                        saved.DestinationPath));
                    continue;
                }

                var bytes = File.Exists(saved.PartialPath)
                    ? new FileInfo(saved.PartialPath).Length
                    : 0;
                records.Add(new CatalogResumableDownload(
                    itemId,
                    Math.Min(bytes, saved.Metadata.ContentLength),
                    saved.Metadata.ContentLength,
                    saved.Metadata.IsPaused));
            }
            catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException
                                               or InvalidDataException
                                               or ArgumentException
                                               or CryptographicException)
            {
                // An untrusted sidecar never authorizes a completed artifact.
            }
        }
        return records;
    }

    public async Task<CatalogDownloadResult> DownloadAsync(
        CatalogItem item,
        string installationRoot,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(item);
        if (cancellationToken.IsCancellationRequested)
        {
            item.PauseDownload("Operação cancelada antes de iniciar — nenhum progresso foi perdido");
            return new CatalogDownloadResult(
                CatalogDownloadState.Paused,
                "Operação cancelada; nenhum progresso foi perdido.");
        }

        CatalogArtifactDescriptor artifact;
        string canonicalRoot;
        string destinationPath = string.Empty;
        string partialPath;
        string metadataPath;
        DownloadResumeMetadata metadata;
        FileStream? artifactLock = null;
        PathIdentity.DirectoryTreeLease? pathLease = null;
        try
        {
            artifact = RequireValidArtifact(item);
            canonicalRoot = ValidateInstallationRoot(installationRoot);
            destinationPath = BuildSafeDestinationPath(canonicalRoot, item);

            var reusable = FindResumeFileSets(canonicalRoot, cancellationToken)
                .Where(saved => MetadataMatchesArtifact(saved.Metadata, artifact)
                                && PathsEqual(saved.DestinationPath, destinationPath))
                .OrderByDescending(saved => saved.Metadata.UpdatedUtc)
                .FirstOrDefault();
            if (reusable is not null) destinationPath = reusable.DestinationPath;

            partialPath = destinationPath + ".part";
            metadataPath = partialPath + ResumeSuffix;
            var destinationDirectory = Path.GetDirectoryName(destinationPath)!;
            if (!IsWithinRoot(destinationDirectory, canonicalRoot))
                throw new InvalidDataException("O diretório do artefato saiu da raiz autorizada.");
            pathLease = PathIdentity.OpenDirectoryTree(
                destinationDirectory,
                createIfMissing: true);
            pathLease.Revalidate();
            artifactLock = AcquireArtifactLock(destinationPath, pathLease);
            metadata = LoadMatchingMetadata(
                metadataPath,
                item,
                artifact,
                partialPath,
                pathLease);
        }
        catch (OperationCanceledException)
        {
            DisposeArtifactLock(artifactLock, destinationPath);
            pathLease?.Dispose();
            item.PauseDownload("Operação cancelada antes da transferência — o progresso foi preservado");
            return new CatalogDownloadResult(
                CatalogDownloadState.Paused,
                "Operação cancelada; o progresso salvo foi preservado.");
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidDataException
                                           or ArgumentException
                                           or NotSupportedException)
        {
            DisposeArtifactLock(artifactLock, destinationPath);
            pathLease?.Dispose();
            var message = SafeLocalFailure(exception);
            item.SetDownloadState(CatalogDownloadState.Failed, message);
            return new CatalogDownloadResult(CatalogDownloadState.Failed, message);
        }
        catch
        {
            DisposeArtifactLock(artifactLock, destinationPath);
            pathLease?.Dispose();
            throw;
        }

        metadata.IsPaused = false;
        var active = new ActiveDownload(
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken),
            metadataPath,
            partialPath,
            destinationPath,
            canonicalRoot,
            metadata,
            artifactLock!,
            pathLease!);

        var rejectedBecauseDisposed = false;
        var added = false;
        lock (_lifetimeGate)
        {
            if (_disposed) rejectedBecauseDisposed = true;
            else added = _active.TryAdd(item.Id, active);
        }

        if (!added)
        {
            active.Dispose();
            if (rejectedBecauseDisposed)
            {
                item.PauseDownload("Programa fechando — o download não foi iniciado");
                return new CatalogDownloadResult(
                    CatalogDownloadState.Paused,
                    "O programa está fechando; nenhum progresso foi perdido.");
            }
            return new CatalogDownloadResult(
                item.DownloadState,
                "Este item já está na fila ou em download.",
                item.LocalFilePath);
        }

        try
        {
            if (await TryCompleteExistingArtifactAsync(item, artifact, active))
                return CompleteResult(item, active.DestinationPath, artifact);

            EnsureNoReparsePoints(canonicalRoot, partialPath);
            long existingBytes;
            await using (var durablePartial = OpenValidatedMutableFile(
                             partialPath,
                             canonicalRoot,
                             FileMode.OpenOrCreate,
                             asynchronous: true))
            {
                if (durablePartial.Length > artifact.ContentLength)
                    throw new InvalidDataException("O arquivo parcial excede o tamanho autorizado.");
                existingBytes = durablePartial.Length;
            }

            item.RestoreDownload(existingBytes, artifact.ContentLength, isPaused: false);
            if (!TrySaveResumeMetadata(active))
                throw new IOException("Não foi possível salvar o estado persistente do download.");

            var retryNumber = 0;
            var immediateRestarts = 0;
            while (true)
            {
                active.Cancellation.Token.ThrowIfCancellationRequested();
                var queueEntered = false;
                try
                {
                    var currentBytes = File.Exists(partialPath)
                        ? new FileInfo(partialPath).Length
                        : 0;
                    item.SetDownloadState(
                        CatalogDownloadState.Queued,
                        currentBytes > 0 ? "Aguardando na fila para continuar" : "Aguardando na fila");
                    await _downloadQueue.WaitAsync(active.Cancellation.Token);
                    queueEntered = true;
                    await DownloadAttemptAsync(item, artifact, active);
                    break;
                }
                catch (RestartDownloadException)
                {
                    immediateRestarts++;
                    if (immediateRestarts > MaximumImmediateRestarts)
                        throw new InvalidDataException("O servidor não respeitou o contrato de retomada.");
                    ResetPartialForFreshAttempt(active);
                }
                catch (TransientDownloadException exception)
                {
                    var configuredDelay = _options.RetryDelays[
                        Math.Min(retryNumber, _options.RetryDelays.Count - 1)];
                    retryNumber++;
                    var retryDelay = exception.RetryAfter is { } requested && requested > configuredDelay
                        ? requested
                        : configuredDelay;
                    if (retryDelay > MaximumRetryDelay) retryDelay = MaximumRetryDelay;
                    metadata.IsPaused = false;
                    TrySaveResumeMetadata(active);
                    item.WaitForNetwork(retryDelay);

                    if (queueEntered)
                    {
                        _downloadQueue.Release();
                        queueEntered = false;
                    }
                    await Task.Delay(retryDelay, active.Cancellation.Token);
                }
                finally
                {
                    if (queueEntered) _downloadQueue.Release();
                }
            }

            return CompleteResult(item, active.DestinationPath, artifact);
        }
        catch (OperationCanceledException)
        {
            metadata.IsPaused = !active.ShutdownRequested;
            TrySaveResumeMetadata(active);
            var bytes = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
            item.UpdateDownloadProgress(bytes, artifact.ContentLength);
            item.PauseDownload(active.ShutdownRequested
                ? "Programa fechado — o download continuará ao abrir novamente"
                : "Download pausado — o progresso foi preservado");
            return new CatalogDownloadResult(
                CatalogDownloadState.Paused,
                "Download pausado; o arquivo parcial foi preservado.");
        }
        catch (Exception exception) when (exception is HttpRequestException
                                           or IOException
                                           or UnauthorizedAccessException
                                           or InvalidDataException
                                           or CryptographicException)
        {
            metadata.IsPaused = false;
            TrySaveResumeMetadata(active);
            var bytes = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
            item.UpdateDownloadProgress(bytes, artifact.ContentLength);
            var message = SafeDownloadFailure(exception);
            item.SetDownloadState(CatalogDownloadState.Failed, message);
            return new CatalogDownloadResult(CatalogDownloadState.Failed, message);
        }
        finally
        {
            FinishActiveDownload(item.Id, active);
        }
    }

    public string BuildSafeDestinationPath(string installationRoot, CatalogItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var artifact = RequireValidArtifact(item);
        var canonicalRoot = Path.GetFullPath(installationRoot);
        var packageRoot = Path.Combine(canonicalRoot, "artifacts");
        var artifactFolder = SanitizePathSegment(artifact.ArtifactId, 96);
        var versionFolder = artifact.ArtifactVersion.ToString(CultureInfo.InvariantCulture);
        var fileName = artifact.SafeFileName;
        var destinationPath = Path.GetFullPath(
            Path.Combine(packageRoot, artifactFolder, versionFolder, fileName));
        if (!IsWithinRoot(destinationPath, canonicalRoot))
            throw new InvalidDataException("O destino calculado saiu da pasta autorizada.");
        EnsureNoReparsePoints(canonicalRoot, Path.GetDirectoryName(destinationPath)!);
        return destinationPath;
    }

    // Compatibility overload for UI code being migrated. The transport URI is
    // ignored deliberately and cannot influence the destination or authority.
    public string BuildSafeDestinationPath(string installationRoot, CatalogItem item, Uri sourceUri)
    {
        ArgumentNullException.ThrowIfNull(sourceUri);
        return BuildSafeDestinationPath(installationRoot, item);
    }

    private static async Task<bool> TryCompleteExistingArtifactAsync(
        CatalogItem item,
        CatalogArtifactDescriptor artifact,
        ActiveDownload active)
    {
        EnsureNoReparsePoints(active.InstallationRoot, active.DestinationPath);
        EnsureNoReparsePoints(active.InstallationRoot, active.PartialPath);
        if (File.Exists(active.DestinationPath))
        {
            if (await ValidateFileWithLeaseAsync(
                    active.DestinationPath,
                    artifact,
                    active.PathLease,
                    active.Cancellation.Token))
            {
                active.Metadata.ArchiveReady = artifact.ExtractPolicy == CatalogExtractPolicy.ExtractArchive;
                if (active.Metadata.ArchiveReady)
                {
                    if (!TrySaveResumeMetadata(active))
                        throw new IOException("Não foi possível registrar o pacote verificado.");
                }
                else
                {
                    DeleteExactFile(active.MetadataPath);
                }
                SetCompletedItem(item, active.DestinationPath, artifact);
                return true;
            }

            PreserveFileByHandle(active.DestinationPath, active.PathLease);
            active.Metadata.ArchiveReady = false;
            TrySaveResumeMetadata(active);
        }

        if (!File.Exists(active.PartialPath)) return false;
        var partialLength = GetValidatedMutableFileLength(
            active.PartialPath,
            active.PathLease);
        if (partialLength > artifact.ContentLength)
        {
            PreserveFileByHandle(active.PartialPath, active.PathLease);
            active.Metadata.ArchiveReady = false;
            TrySaveResumeMetadata(active);
            return false;
        }

        if (partialLength != artifact.ContentLength) return false;
        item.BeginVerification();
        if (!await ValidateFileWithLeaseAsync(
                active.PartialPath,
                artifact,
                active.PathLease,
                active.Cancellation.Token))
        {
            PreserveFileByHandle(active.PartialPath, active.PathLease);
            active.Metadata.ArchiveReady = false;
            TrySaveResumeMetadata(active);
            return false;
        }

        await PublishVerifiedPartialAsync(item, artifact, active);
        return true;
    }

    private async Task DownloadAttemptAsync(
        CatalogItem item,
        CatalogArtifactDescriptor artifact,
        ActiveDownload active)
    {
        EnsureNoReparsePoints(active.InstallationRoot, active.PartialPath);
        EnsureNoReparsePoints(active.InstallationRoot, active.DestinationPath);
        active.PathLease.Revalidate();
        await using var output = active.PathLease.OpenFile(
            active.PartialPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var offset = output.Length;
        if (artifact.ContentLength == 0 && active.Metadata.ContentLength > 0)
        {
            artifact = artifact with
            {
                ContentLength = active.Metadata.ContentLength,
                Sha256 = active.Metadata.Sha256
            };
            item.ResolveDirectArtifactMetadata(artifact.ContentLength, artifact.Sha256);
        }
        if (artifact.ContentLength > 0 && offset > artifact.ContentLength)
            throw new InvalidDataException("O parcial excede o tamanho autorizado.");
        if (artifact.ContentLength > 0 && offset == artifact.ContentLength)
        {
            await output.DisposeAsync();
            await VerifyAndPublishAsync(item, artifact, active);
            return;
        }

        using var request = await CreateAuthorizedRequestAsync(
            item.Id, artifact, offset, active);
        var authorizationResponse = await SendWithHeaderTimeoutAsync(
            _httpClient, request, active.Cancellation.Token);
        ValidateResponseRequestIdentity(request, authorizationResponse);
        HttpResponseMessage response;
        if (IsRedirect(authorizationResponse.StatusCode))
        {
            try
            {
                response = await FollowAuthorizedRedirectAsync(authorizationResponse, request,
                    active.Cancellation.Token);
            }
            finally { authorizationResponse.Dispose(); }
        }
        else response = authorizationResponse;
        using (response)
        {
        if (IsTransientStatus(response.StatusCode))
            throw new TransientDownloadException(
                $"Falha HTTP transitória ({(int)response.StatusCode}).",
                GetRetryAfter(response));
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            throw new InvalidDataException("O servidor recusou a faixa autorizada do artefato.");
        if (offset == 0 && response.StatusCode != HttpStatusCode.OK)
            throw new InvalidDataException($"Resposta de download inválida (HTTP {(int)response.StatusCode}).");
        if (offset > 0 && response.StatusCode == HttpStatusCode.OK)
            throw new RestartDownloadException();
        if (offset > 0 && response.StatusCode != HttpStatusCode.PartialContent)
            throw new InvalidDataException($"Resposta de retomada inválida (HTTP {(int)response.StatusCode}).");

        var responseTotalLength = ValidateResponseEnvelope(response, artifact, offset,
            _options.MaximumFileSizeBytes);
        if (artifact.ContentLength == 0)
        {
            artifact = artifact with { ContentLength = responseTotalLength };
            active.Metadata.ContentLength = responseTotalLength;
            item.ResolveDirectArtifactMetadata(responseTotalLength, artifact.Sha256);
        }
        ValidateResumeValidator(active.Metadata, response);
        CaptureResumeValidators(active.Metadata, response);
        active.Metadata.ArchiveReady = false;
        if (!TrySaveResumeMetadata(active))
            throw new IOException("Não foi possível salvar o estado da retomada.");

        item.SetDownloadState(CatalogDownloadState.Downloading,
            offset > 0 ? "Continuando download verificado" : "Iniciando download verificado");

        await using var input = await ReadContentStreamAsync(response, active.Cancellation.Token);
        if (output.Length != offset)
            throw new InvalidDataException("O parcial mudou durante a retomada.");
        output.Position = offset;

        var buffer = new byte[BufferSize];
        var total = offset;
        try
        {
            while (true)
            {
                int read;
                try
                {
                    read = await ReadWithInactivityTimeoutAsync(
                        input,
                        buffer,
                        active.Cancellation.Token);
                }
                catch (OperationCanceledException) when (!active.Cancellation.IsCancellationRequested)
                {
                    throw new TransientDownloadException("A transferência ficou inativa.");
                }
                catch (Exception exception) when (exception is IOException or HttpRequestException)
                {
                    throw new TransientDownloadException("A conexão foi interrompida.", innerException: exception);
                }

                if (read == 0) break;
                total = checked(total + read);
                if (total > artifact.ContentLength)
                    throw new InvalidDataException("O servidor enviou bytes além do tamanho autorizado.");
                // Local write/flush failures are deliberately not classified as
                // network retries (for example, disk full or a revoked ACL).
                await output.WriteAsync(buffer.AsMemory(0, read), active.Cancellation.Token);
                await output.FlushAsync(active.Cancellation.Token);
                item.UpdateDownloadProgress(total, artifact.ContentLength);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }

        if (total != artifact.ContentLength)
            throw new TransientDownloadException("A resposta terminou antes do tamanho autorizado.");
        output.Flush(flushToDisk: true);
        _ = PathIdentity.CaptureFileIdentity(output.SafeFileHandle, active.PartialPath);
        active.PathLease.Revalidate();
        // Close the writer before opening a second handle for hashing. On
        // Windows, FileShare.Read permits readers but a reader opened while the
        // write handle remains active can still fail the reciprocal share check.
        await output.DisposeAsync();
        await VerifyAndPublishAsync(item, artifact, active);
    }
    }

    private async Task<HttpRequestMessage> CreateAuthorizedRequestAsync(
        string itemId,
        CatalogArtifactDescriptor artifact,
        long offset,
        ActiveDownload active)
    {
        HttpRequestMessage request;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(active.Cancellation.Token);
            timeout.CancelAfter(_options.InactivityTimeout);
            request = await _requestProvider.CreateRequestAsync(
                itemId,
                artifact,
                offset,
                new CatalogDownloadValidators(active.Metadata.ETag, active.Metadata.LastModified),
                timeout.Token);
        }
        catch (OperationCanceledException) when (!active.Cancellation.IsCancellationRequested)
        {
            throw new TransientDownloadException("A autorização temporária não respondeu.");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            throw new TransientDownloadException(
                "A autorização temporária está indisponível.",
                innerException: exception);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new InvalidDataException(
                "Não foi possível obter uma autorização temporária para o artefato.",
                exception);
        }

        try
        {
            ValidateAuthorizedRequest(request, artifact, offset, active.Metadata);
            return request;
        }
        catch
        {
            request?.Dispose();
            throw;
        }
    }

    private void ValidateAuthorizedRequest(
        HttpRequestMessage request,
        CatalogArtifactDescriptor artifact,
        long offset,
        DownloadResumeMetadata metadata)
    {
        if (request is null)
            throw new InvalidDataException("O provedor não criou uma requisição autorizada.");
        if (request.Method != HttpMethod.Get || request.Content is not null)
            throw new InvalidDataException("A autorização deve produzir somente um GET sem corpo.");
        var uri = request.RequestUri
                  ?? throw new InvalidDataException("A requisição autorizada não possui destino.");
        if (!uri.IsAbsoluteUri
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || uri.Port != 443
            || uri.UserInfo.Length != 0
            || uri.Query.Length != 0
            || uri.Fragment.Length != 0
            || uri.AbsolutePath.Length is 0 or > 2048
            || !_options.AllowedHosts.Contains(uri.IdnHost))
            throw new InvalidDataException("O destino temporário não atende à política de transporte.");

        var authorization = request.Headers.Authorization;
        if (authorization is null
            || !authorization.Scheme.Equals("Bearer", StringComparison.Ordinal)
            || !SuiteContentProtocol.IsBearerToken(authorization.Parameter))
            throw new InvalidDataException("A requisição não contém autorização temporária válida.");

        var allowedHeaders = new HashSet<string>(
            ["Authorization", "Range", "If-Range"],
            StringComparer.OrdinalIgnoreCase);
        if (request.Headers.Any(header => !allowedHeaders.Contains(header.Key)))
            throw new InvalidDataException(
                "A requisição autorizada contém cabeçalhos não permitidos.");

        var ranges = request.Headers.Range?.Ranges.ToArray() ?? [];
        if (offset == 0)
        {
            if (ranges.Length != 0 || request.Headers.IfRange is not null)
                throw new InvalidDataException("A primeira requisição não pode conter Range/If-Range.");
        }
        else
        {
            if (request.Headers.Range?.Unit != "bytes"
                || ranges.Length != 1
                || ranges[0].From != offset
                || ranges[0].To is not null)
                throw new InvalidDataException("A faixa da requisição não corresponde ao parcial local.");
            if (request.Headers.IfRange is { } ifRange)
            {
                var value = ifRange.ToString();
                var signedArtifactEtag = $"\"{artifact.Sha256}\"";
                if (!value.Equals(signedArtifactEtag, StringComparison.Ordinal)
                    && !value.Equals(metadata.ETag, StringComparison.Ordinal)
                    && !value.Equals(metadata.LastModified, StringComparison.Ordinal))
                    throw new InvalidDataException("O validador If-Range não corresponde ao estado local.");
            }
        }
    }

    private async Task<HttpResponseMessage> SendWithHeaderTimeoutAsync(
        HttpClient client,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.InactivityTimeout);
        try
        {
            return await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TransientDownloadException("O servidor não respondeu a tempo.");
        }
        catch (HttpRequestException exception)
        {
            throw new TransientDownloadException("A conexão segura falhou.", innerException: exception);
        }
    }

    private async Task<HttpResponseMessage> FollowAuthorizedRedirectAsync(
        HttpResponseMessage authorizationResponse,
        HttpRequestMessage authorizedRequest,
        CancellationToken cancellationToken)
    {
        if (_options.MaximumRedirects < 1
            || authorizationResponse.StatusCode is not (HttpStatusCode.TemporaryRedirect
                or HttpStatusCode.PermanentRedirect))
            throw new InvalidDataException("O redirecionamento de download não foi autorizado.");
        var destination = authorizationResponse.Headers.Location;
        if (destination is null || !destination.IsAbsoluteUri
            || destination.Scheme != Uri.UriSchemeHttps
            || destination.Port != 443
            || destination.UserInfo.Length != 0
            || destination.Fragment.Length != 0)
            throw new InvalidDataException("O destino direto autorizado é inválido.");

        using var directRequest = new HttpRequestMessage(HttpMethod.Get, destination);
        directRequest.Headers.Range = authorizedRequest.Headers.Range;
        directRequest.Headers.IfRange = authorizedRequest.Headers.IfRange;
        // The Suite API bearer is never forwarded to the external host.
        var response = await SendWithHeaderTimeoutAsync(
            _directHttpClient, directRequest, cancellationToken);
        ValidateResponseRequestIdentity(directRequest, response);
        if (IsRedirect(response.StatusCode))
        {
            response.Dispose();
            throw new InvalidDataException("A hospedagem tentou redirecionar novamente.");
        }
        return response;
    }

    private static void ValidateResponseRequestIdentity(
        HttpRequestMessage request,
        HttpResponseMessage response)
    {
        var finalUri = response.RequestMessage?.RequestUri;
        if (finalUri is not null && !finalUri.Equals(request.RequestUri))
            throw new InvalidDataException("A pilha HTTP seguiu um redirecionamento não autorizado.");
    }

    private static long ValidateResponseEnvelope(
        HttpResponseMessage response,
        CatalogArtifactDescriptor artifact,
        long offset,
        long maximumFileSizeBytes)
    {
        if (response.Content.Headers.ContentEncoding.Count != 0)
            throw new InvalidDataException("Respostas codificadas ou comprimidas não são aceitas.");
        var responseLength = response.Content.Headers.ContentLength
            ?? throw new InvalidDataException("A origem não informou Content-Length.");
        var totalLength = artifact.ContentLength == 0
            ? checked(offset + responseLength)
            : artifact.ContentLength;
        if (totalLength <= 0 || totalLength > maximumFileSizeBytes)
            throw new InvalidDataException("O tamanho informado pela origem excede o limite local.");
        var expectedRemaining = totalLength - offset;
        if (response.Content.Headers.ContentLength != expectedRemaining)
            throw new InvalidDataException("Content-Length não corresponde ao manifesto autorizado.");

        var range = response.Content.Headers.ContentRange;
        if (offset == 0)
        {
            if (range is not null)
                throw new InvalidDataException("A resposta inicial contém Content-Range inesperado.");
            return totalLength;
        }

        if (range is null
            || !range.Unit.Equals("bytes", StringComparison.OrdinalIgnoreCase)
            || range.From != offset
            || range.To != totalLength - 1
            || range.Length != totalLength)
            throw new InvalidDataException("Content-Range não corresponde ao manifesto autorizado.");
        return totalLength;
    }

    private static async Task VerifyAndPublishAsync(
        CatalogItem item,
        CatalogArtifactDescriptor artifact,
        ActiveDownload active)
    {
        item.BeginVerification();
        await PublishVerifiedPartialAsync(item, artifact, active);
    }

    private static async Task PublishVerifiedPartialAsync(
        CatalogItem item,
        CatalogArtifactDescriptor artifact,
        ActiveDownload active)
    {
        active.PathLease.Revalidate();
        await using var partial = active.PathLease.OpenFile(
            active.PartialPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan,
            deleteAccess: true);
        var partialIdentity = PathIdentity.CaptureFileIdentity(
            partial.SafeFileHandle,
            active.PartialPath);
        if (IsDeferredSha256(artifact.Sha256))
        {
            partial.Position = 0;
            var resolvedHash = Convert.ToHexString(
                    await SHA256.HashDataAsync(partial, active.Cancellation.Token))
                .ToLowerInvariant();
            partial.Position = 0;
            item.ResolveDeferredArtifactHash(resolvedHash);
            artifact = artifact with { Sha256 = resolvedHash };
        }
        if (!await ValidateOpenFileAsync(partial, artifact, active.Cancellation.Token))
            throw new InvalidDataException("A integridade ou o tamanho do pacote não confere.");
        _ = PathIdentity.RevalidateFile(
            partial.SafeFileHandle,
            active.PartialPath,
            partialIdentity);
        active.PathLease.Revalidate();

        var partialWasPublished = false;
        if (File.Exists(active.DestinationPath))
        {
            await using var existing = active.PathLease.OpenFile(
                active.DestinationPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan,
                deleteAccess: true);
            var existingIdentity = PathIdentity.CaptureFileIdentity(
                existing.SafeFileHandle,
                active.DestinationPath);
            if (await ValidateOpenFileAsync(existing, artifact, active.Cancellation.Token))
            {
                PathIdentity.DeleteByHandle(
                    partial.SafeFileHandle,
                    active.PartialPath,
                    partialIdentity);
            }
            else
            {
                var preservedLeaf = Path.GetFileName(active.DestinationPath)
                                    + ".preserved-"
                                    + DateTimeOffset.UtcNow.ToString(
                                        "yyyyMMddHHmmssfff",
                                        CultureInfo.InvariantCulture)
                                    + "-" + Guid.NewGuid().ToString("N");
                _ = PathIdentity.RenameByHandle(
                    existing.SafeFileHandle,
                    existingIdentity,
                    active.PathLease.AnchorHandle,
                    Path.GetDirectoryName(active.DestinationPath)!,
                    preservedLeaf,
                    replaceIfExists: false);
                _ = PathIdentity.RenameByHandle(
                    partial.SafeFileHandle,
                    partialIdentity,
                    active.PathLease.AnchorHandle,
                    Path.GetDirectoryName(active.DestinationPath)!,
                    Path.GetFileName(active.DestinationPath),
                    replaceIfExists: false);
                partialWasPublished = true;
            }
        }
        else
        {
            _ = PathIdentity.RenameByHandle(
                partial.SafeFileHandle,
                partialIdentity,
                active.PathLease.AnchorHandle,
                Path.GetDirectoryName(active.DestinationPath)!,
                Path.GetFileName(active.DestinationPath),
                replaceIfExists: false);
            partialWasPublished = true;
        }

        active.PathLease.Revalidate();
        if (partialWasPublished)
        {
            partial.Position = 0;
            if (!await ValidateOpenFileAsync(partial, artifact, active.Cancellation.Token))
                throw new InvalidDataException("O arquivo publicado não preservou a integridade autorizada.");
        }
        else if (!await ValidateFileWithLeaseAsync(
                     active.DestinationPath,
                     artifact,
                     active.PathLease,
                     active.Cancellation.Token))
            throw new InvalidDataException("O arquivo publicado não preservou a integridade autorizada.");

        active.Metadata.ArchiveReady = artifact.ExtractPolicy == CatalogExtractPolicy.ExtractArchive;
        active.Metadata.IsPaused = false;
        if (active.Metadata.ArchiveReady)
        {
            if (!TrySaveResumeMetadata(active))
                throw new IOException("Não foi possível registrar o pacote verificado.");
        }
        else
        {
            _ = TryDeleteFileStrict(active.MetadataPath, active.InstallationRoot, out _);
        }
        DeletePreservedPartials(active.PartialPath);
        SetCompletedItem(item, active.DestinationPath, artifact);
    }

    private static CatalogDownloadResult CompleteResult(
        CatalogItem item,
        string destinationPath,
        CatalogArtifactDescriptor artifact)
    {
        SetCompletedItem(item, destinationPath, artifact);
        return new CatalogDownloadResult(
            CatalogDownloadState.Completed,
            artifact.ExtractPolicy == CatalogExtractPolicy.ExtractArchive
                ? $"Download concluído: {Path.GetFileName(destinationPath)}. Iniciando extração."
                : $"Download concluído e verificado: {Path.GetFileName(destinationPath)}",
            destinationPath);
    }

    private static void SetCompletedItem(
        CatalogItem item,
        string destinationPath,
        CatalogArtifactDescriptor artifact)
    {
        if (artifact.ExtractPolicy == CatalogExtractPolicy.ExtractArchive)
            item.MarkArchiveReady(destinationPath);
        else
            item.CompleteDownload(destinationPath);
    }

    private async Task<Stream> ReadContentStreamAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.InactivityTimeout);
        try
        {
            return await response.Content.ReadAsStreamAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TransientDownloadException("O corpo da resposta não ficou disponível a tempo.");
        }
    }

    private async Task<int> ReadWithInactivityTimeoutAsync(
        Stream input,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.InactivityTimeout);
        return await input.ReadAsync(buffer.AsMemory(), timeout.Token);
    }

    private DownloadResumeMetadata LoadMatchingMetadata(
        string metadataPath,
        CatalogItem item,
        CatalogArtifactDescriptor artifact,
        string partialPath,
        PathIdentity.DirectoryTreeLease pathLease)
    {
        var metadata = LoadResumeMetadata(metadataPath, pathLease);
        if (metadata is not null && MetadataMatchesArtifact(metadata, artifact))
        {
            metadata.ItemId = item.Id;
            return metadata;
        }

        if (File.Exists(partialPath)) PreserveFileByHandle(partialPath, pathLease);
        if (File.Exists(metadataPath)) PreserveFileByHandle(metadataPath, pathLease);
        return CreateMetadata(item, artifact);
    }

    private static DownloadResumeMetadata CreateMetadata(
        CatalogItem item,
        CatalogArtifactDescriptor artifact) => new()
        {
            SchemaVersion = ResumeSchemaVersion,
            ItemId = IsSafeIdentity(item.Id, 128) ? item.Id : artifact.ArtifactId,
            ProductId = artifact.ProductId,
            ArtifactId = artifact.ArtifactId,
            ArtifactVersion = artifact.ArtifactVersion,
            Sha256 = artifact.Sha256,
            ContentLength = artifact.ContentLength,
            ManifestIdentity = artifact.ManifestIdentity,
            ExtractPolicy = artifact.ExtractPolicy,
            SafeFileName = artifact.SafeFileName,
            FileExtension = artifact.FileExtension.ToLowerInvariant(),
            UpdatedUtc = DateTimeOffset.UtcNow
        };

    private List<ResumeFileSet> FindResumeFileSets(
        string installationRoot,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = Path.GetFullPath(installationRoot);
        if (!Directory.Exists(root)) return [];
        EnsureNoReparsePoints(root, root);
        var artifactRoot = Path.Combine(root, "artifacts");
        if (!Directory.Exists(artifactRoot)) return [];
        EnsureNoReparsePoints(root, artifactRoot);
        var matches = new List<ResumeFileSet>();
        var candidates = new List<string>();
        try
        {
            const int maximumVisitedEntries = 100_000;
            const int maximumDepth = 4;
            var visitedEntries = 0;
            var pendingDirectories = new Stack<(string Path, int Depth)>();
            pendingDirectories.Push((artifactRoot, 0));
            var enumerationOptions = new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                ReturnSpecialDirectories = false,
                MatchCasing = MatchCasing.CaseInsensitive
            };

            while (pendingDirectories.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (directory, depth) = pendingDirectories.Pop();
                foreach (var entry in Directory.EnumerateFileSystemEntries(
                             directory,
                             "*",
                             enumerationOptions))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    visitedEntries++;
                    if (visitedEntries > maximumVisitedEntries) return [];

                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if (depth < maximumDepth)
                            pendingDirectories.Push((entry, depth + 1));
                        continue;
                    }

                    if (!entry.EndsWith(".part" + ResumeSuffix, StringComparison.OrdinalIgnoreCase))
                        continue;
                    candidates.Add(entry);
                    if (candidates.Count > 10_000) return [];
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
        foreach (var metadataPath in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                EnsureNoReparsePoints(root, metadataPath);
                var metadata = LoadResumeMetadata(metadataPath);
                if (metadata is null) continue;
                var partialPath = metadataPath[..^ResumeSuffix.Length];
                var destinationPath = partialPath[..^".part".Length];
                if (!IsWithinRoot(destinationPath, root)) continue;
                EnsureNoReparsePoints(root, partialPath);
                EnsureNoReparsePoints(root, destinationPath);
                matches.Add(new ResumeFileSet(
                    metadataPath,
                    partialPath,
                    destinationPath,
                    metadata));
            }
            catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException
                                               or InvalidDataException
                                               or ArgumentException
                                               or NotSupportedException)
            {
                // Malformed/untrusted resume state is ignored, never promoted.
            }
        }
        return matches;
    }

    private DownloadResumeMetadata? LoadResumeMetadata(string metadataPath)
    {
        if (!File.Exists(metadataPath)) return null;
        try
        {
            var info = new FileInfo(metadataPath);
            if (info.Length is <= 0 or > MaximumMetadataBytes) return null;
            var json = File.ReadAllText(metadataPath);
            var metadata = JsonSerializer.Deserialize<DownloadResumeMetadata>(json, ResumeJsonOptions);
            if (metadata is null || !ValidateMetadataShape(metadata)) return null;
            return metadata;
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or JsonException
                                           or InvalidDataException)
        {
            return null;
        }
    }

    private DownloadResumeMetadata? LoadResumeMetadata(
        string metadataPath,
        PathIdentity.DirectoryTreeLease pathLease)
    {
        if (!File.Exists(metadataPath)) return null;
        try
        {
            pathLease.Revalidate();
            using var stream = pathLease.OpenFile(
                metadataPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                MaximumMetadataBytes,
                FileOptions.SequentialScan);
            var identity = PathIdentity.CaptureFileIdentity(stream.SafeFileHandle, metadataPath);
            if (stream.Length is <= 0 or > MaximumMetadataBytes) return null;
            var metadata = JsonSerializer.Deserialize<DownloadResumeMetadata>(
                stream,
                ResumeJsonOptions);
            _ = PathIdentity.RevalidateFile(stream.SafeFileHandle, metadataPath, identity);
            pathLease.Revalidate();
            return metadata is not null && ValidateMetadataShape(metadata) ? metadata : null;
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or JsonException)
        {
            return null;
        }
    }

    private bool ValidateMetadataShape(DownloadResumeMetadata metadata)
    {
        if (metadata.SchemaVersion != ResumeSchemaVersion
            || metadata.ProductId != CatalogArtifactDescriptor.TurboramaSuiteProductId
            || metadata.ContentLength <= 0
            || metadata.ContentLength > _options.MaximumFileSizeBytes
            || !IsCanonicalSha256(metadata.Sha256)
            || !IsSafeIdentity(metadata.ItemId, 128)
            || !IsLowerHex(metadata.ArtifactId, 32)
            || metadata.ArtifactVersion <= 0
            || !IsLowerHex(metadata.ManifestIdentity, 64)
            || !IsSafeExtension(metadata.FileExtension)
            || !IsSafeFileName(metadata.SafeFileName, metadata.FileExtension)
            || !Enum.IsDefined(metadata.ExtractPolicy)
            || !IsValidPersistedEtag(metadata.ETag)
            || !IsValidPersistedLastModified(metadata.LastModified))
            return false;
        return true;
    }

    private static bool TrySaveResumeMetadata(ActiveDownload active)
    {
        string? temporaryPath = null;
        try
        {
            lock (active.MetadataGate)
            {
                if (active.PauseRequested) active.Metadata.IsPaused = true;
                active.Metadata.UpdatedUtc = DateTimeOffset.UtcNow;
                temporaryPath = active.MetadataPath + ".tmp-" + Guid.NewGuid().ToString("N");
                active.PathLease.Revalidate();
                var serialized = JsonSerializer.SerializeToUtf8Bytes(
                    active.Metadata,
                    ResumeJsonOptions);
                using (var temporary = active.PathLease.OpenFile(
                           temporaryPath,
                           FileMode.CreateNew,
                           FileAccess.ReadWrite,
                           FileShare.None,
                           BufferSize,
                           FileOptions.WriteThrough,
                           deleteAccess: true))
                {
                    var temporaryIdentity = PathIdentity.CaptureFileIdentity(
                        temporary.SafeFileHandle,
                        temporaryPath);
                    temporary.Write(serialized);
                    temporary.Flush(flushToDisk: true);
                    _ = PathIdentity.RevalidateFile(
                        temporary.SafeFileHandle,
                        temporaryPath,
                        temporaryIdentity);
                    active.PathLease.Revalidate();
                    _ = PathIdentity.RenameByHandle(
                        temporary.SafeFileHandle,
                        temporaryIdentity,
                        active.PathLease.AnchorHandle,
                        Path.GetDirectoryName(active.MetadataPath)!,
                        Path.GetFileName(active.MetadataPath),
                        replaceIfExists: true);
                }
                temporaryPath = null;
                active.PathLease.Revalidate();
                return true;
            }
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidDataException
                                           or JsonException)
        {
            return false;
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try { _ = PathIdentity.DeleteFileExact(temporaryPath, active.InstallationRoot); }
                catch (Exception exception) when (exception is IOException
                                                   or UnauthorizedAccessException
                                                   or InvalidDataException)
                {
                }
            }
        }
    }

    private static void CaptureResumeValidators(
        DownloadResumeMetadata metadata,
        HttpResponseMessage response)
    {
        var etag = response.Headers.ETag;
        if (etag is { IsWeak: false } && etag.ToString().Length <= 512)
            metadata.ETag = etag.ToString();
        if (response.Content.Headers.LastModified is { } modified)
            metadata.LastModified = modified.ToString("R", CultureInfo.InvariantCulture);
    }

    private static void ValidateResumeValidator(
        DownloadResumeMetadata metadata,
        HttpResponseMessage response)
    {
        var responseEtag = response.Headers.ETag;
        if (metadata.ETag.Length > 0
            && responseEtag is not null
            && !metadata.ETag.Equals(responseEtag.ToString(), StringComparison.Ordinal))
            throw new InvalidDataException("O validador do artefato mudou durante a retomada.");

        var responseLastModified = response.Content.Headers.LastModified?
            .ToString("R", CultureInfo.InvariantCulture);
        if (metadata.ETag.Length == 0
            && metadata.LastModified.Length > 0
            && responseLastModified is not null
            && !metadata.LastModified.Equals(responseLastModified, StringComparison.Ordinal))
            throw new InvalidDataException("A data do artefato mudou durante a retomada.");
    }

    private CatalogArtifactDescriptor RequireValidArtifact(CatalogItem item)
    {
        var artifact = item.Artifact
                       ?? throw new InvalidDataException(
                           "O catálogo não forneceu um descritor autorizado para este artefato.");
        ValidateArtifact(artifact);
        return artifact;
    }

    private void ValidateArtifact(CatalogArtifactDescriptor artifact)
    {
        if (artifact.ProductId != CatalogArtifactDescriptor.TurboramaSuiteProductId)
            throw new InvalidDataException("O produto do artefato não é autorizado.");
        if (!IsLowerHex(artifact.ArtifactId, 32))
            throw new InvalidDataException("O identificador do artefato é inválido.");
        if (artifact.ArtifactVersion <= 0)
            throw new InvalidDataException("A versão do artefato é inválida.");
        if (artifact.ContentLength < 0 || artifact.ContentLength > _options.MaximumFileSizeBytes)
            throw new InvalidDataException("O tamanho do artefato excede o limite.");
        if (!IsCanonicalSha256(artifact.Sha256))
            throw new InvalidDataException("O SHA-256 obrigatório do artefato é inválido.");
        if (!IsSafeExtension(artifact.FileExtension))
            throw new InvalidDataException("A extensão autorizada é inválida.");
        if (!IsSafeFileName(artifact.SafeFileName, artifact.FileExtension))
            throw new InvalidDataException("O nome seguro do arquivo é inválido.");
        if (!IsLowerHex(artifact.ManifestIdentity, 64))
            throw new InvalidDataException("A identidade do manifesto é inválida.");
        if (!Enum.IsDefined(artifact.ExtractPolicy))
            throw new InvalidDataException("A política de extração é inválida.");
    }

    private static bool MetadataMatchesArtifact(
        DownloadResumeMetadata metadata,
        CatalogArtifactDescriptor artifact) =>
        metadata.ProductId.Equals(artifact.ProductId, StringComparison.Ordinal)
        && metadata.ArtifactId.Equals(artifact.ArtifactId, StringComparison.Ordinal)
        && metadata.ArtifactVersion == artifact.ArtifactVersion
        && (artifact.ContentLength == 0 || metadata.ContentLength == artifact.ContentLength)
        && (IsDeferredSha256(artifact.Sha256) ||
            metadata.Sha256.Equals(artifact.Sha256, StringComparison.Ordinal))
        && metadata.ManifestIdentity.Equals(artifact.ManifestIdentity, StringComparison.Ordinal)
        && metadata.ExtractPolicy == artifact.ExtractPolicy
        && metadata.SafeFileName.Equals(artifact.SafeFileName, StringComparison.Ordinal)
        && metadata.FileExtension.Equals(artifact.FileExtension, StringComparison.Ordinal);

    private static string CreateArtifactIdentity(CatalogArtifactDescriptor artifact) =>
        string.Join('\u001F',
            artifact.ProductId,
            artifact.ArtifactId,
            artifact.ArtifactVersion.ToString(CultureInfo.InvariantCulture),
            artifact.Sha256,
            artifact.ContentLength.ToString(CultureInfo.InvariantCulture));

    private static string CreateArtifactIdentity(DownloadResumeMetadata metadata) =>
        string.Join('\u001F',
            metadata.ProductId,
            metadata.ArtifactId,
            metadata.ArtifactVersion.ToString(CultureInfo.InvariantCulture),
            metadata.Sha256,
            metadata.ContentLength.ToString(CultureInfo.InvariantCulture));

    private static async Task<bool> ValidateFileAsync(
        string path,
        CatalogArtifactDescriptor artifact,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path)) return false;
            await using var stream = OpenValidatedFile(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                asynchronous: true);
            if (stream.Length != artifact.ContentLength) return false;
            var hash = await SHA256.HashDataAsync(stream, cancellationToken);
            return CryptographicOperations.FixedTimeEquals(
                hash,
                Convert.FromHexString(artifact.Sha256));
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or FormatException
                                           or CryptographicException)
        {
            return false;
        }
    }

    private static async Task<bool> ValidateFileWithLeaseAsync(
        string path,
        CatalogArtifactDescriptor artifact,
        PathIdentity.DirectoryTreeLease pathLease,
        CancellationToken cancellationToken)
    {
        try
        {
            pathLease.Revalidate();
            await using var stream = pathLease.OpenFile(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var identity = PathIdentity.CaptureFileIdentity(stream.SafeFileHandle, path);
            var valid = await ValidateOpenFileAsync(stream, artifact, cancellationToken);
            _ = PathIdentity.RevalidateFile(stream.SafeFileHandle, path, identity);
            pathLease.Revalidate();
            return valid;
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or FormatException
                                           or CryptographicException)
        {
            return false;
        }
    }

    private static async Task<bool> ValidateOpenFileAsync(
        FileStream stream,
        CatalogArtifactDescriptor artifact,
        CancellationToken cancellationToken)
    {
        if (stream.Length != artifact.ContentLength) return false;
        stream.Position = 0;
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        stream.Position = 0;
        return CryptographicOperations.FixedTimeEquals(
            hash,
            Convert.FromHexString(artifact.Sha256));
    }

    private static bool ValidateFile(
        string path,
        CatalogArtifactDescriptor artifact,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (!File.Exists(path)) return false;
            using var stream = OpenValidatedFile(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                asynchronous: false);
            if (stream.Length != artifact.ContentLength) return false;
            var buffer = GC.AllocateUninitializedArray<byte>(BufferSize);
            try
            {
                using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                int bytesRead;
                while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    hasher.AppendData(buffer, 0, bytesRead);
                }
                cancellationToken.ThrowIfCancellationRequested();
                var hash = hasher.GetHashAndReset();
                return CryptographicOperations.FixedTimeEquals(
                    hash,
                    Convert.FromHexString(artifact.Sha256));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(buffer);
            }
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or FormatException
                                           or CryptographicException)
        {
            return false;
        }
    }

    private static string ValidateInstallationRoot(string installationRoot)
    {
        var canonicalRoot = Path.GetFullPath(installationRoot);
        if (!Directory.Exists(canonicalRoot))
            throw new InvalidDataException("A pasta de instalação não existe. Escolha uma pasta válida.");
        EnsureNoReparsePoints(canonicalRoot, canonicalRoot);
        return canonicalRoot;
    }

    private static bool IsCanonicalSha256(string value) => IsLowerHex(value, 64);

    private static bool IsDeferredSha256(string value) =>
        value.Length == 64 && value.All(character => character == '0');

    private static bool IsLowerHex(string value, int exactLength) =>
        value is not null
        && value.Length == exactLength
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsSafeIdentity(string value, int maximumLength) =>
        IsBoundedText(value, 1, maximumLength)
        && value.All(character => char.IsAsciiLetterOrDigit(character)
                                  || character is '-' or '_' or '.');

    private static bool IsSafeFileName(string value, string extension)
    {
        if (!IsBoundedText(value, 1, 180)
            || Encoding.UTF8.GetByteCount(value) > 180
            || value is "." or ".."
            || !Path.GetFileName(value).Equals(value, StringComparison.Ordinal)
            || value.EndsWith(' ')
            || value.EndsWith('.')
            || value.Any(character => char.IsControl(character)
                || char.IsSurrogate(character)
                || character is '<' or '>' or '"' or '/' or '\\'
                    or '|' or '?' or '*' or ':'))
            return false;
        if (!value.EndsWith(extension, StringComparison.Ordinal)) return false;
        var baseName = value.Split('.', 2)[0];
        return !WindowsReservedFileNames.Contains(baseName);
    }

    private static bool IsSafeExtension(string value) =>
        value is not null
        && value.Length is >= 2 and <= 11
        && value[0] == '.'
        && value.Skip(1).All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9');

    private static bool IsBoundedText(string value, int minimumLength, int maximumLength) =>
        value is not null && value.Length >= minimumLength && value.Length <= maximumLength;

    private static bool IsValidPersistedEtag(string value)
    {
        if (value is null) return false;
        if (value.Length == 0) return true;
        return value.Length <= 512
               && EntityTagHeaderValue.TryParse(value, out var parsed)
               && !parsed.IsWeak;
    }

    private static bool IsValidPersistedLastModified(string value)
    {
        if (value is null) return false;
        if (value.Length == 0) return true;
        return value.Length <= 128
               && DateTimeOffset.TryParseExact(
                   value,
                   "R",
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeUniversal,
                   out _);
    }

    private static readonly HashSet<string> WindowsReservedFileNames = new(
        [
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        ],
        StringComparer.OrdinalIgnoreCase);

    private static string SanitizePathSegment(string value, int maximumLength)
    {
        if (!IsSafeIdentity(value, maximumLength))
            throw new InvalidDataException("A identidade do caminho não é segura.");
        return value;
    }

    private static bool IsWithinRoot(string candidatePath, string rootPath)
    {
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        var canonicalCandidate = Path.GetFullPath(candidatePath);
        if (canonicalCandidate.Equals(canonicalRoot, StringComparison.OrdinalIgnoreCase)) return true;
        var rootPrefix = Path.EndsInDirectorySeparator(canonicalRoot)
            ? canonicalRoot
            : canonicalRoot + Path.DirectorySeparatorChar;
        return canonicalCandidate.StartsWith(
            rootPrefix,
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string firstPath, string secondPath) =>
        Path.GetFullPath(firstPath).Equals(
            Path.GetFullPath(secondPath),
            StringComparison.OrdinalIgnoreCase);

    private static void EnsureNoReparsePoints(string rootPath, string candidatePath)
    {
        var canonicalRoot = Path.GetFullPath(rootPath);
        var canonicalCandidate = Path.GetFullPath(candidatePath);
        if (!IsWithinRoot(canonicalCandidate, canonicalRoot))
            throw new InvalidDataException("O caminho saiu da pasta autorizada.");

        if (!Directory.Exists(canonicalRoot))
            throw new InvalidDataException("A raiz autorizada não existe.");

        var volumeRoot = Path.GetPathRoot(canonicalCandidate);
        if (string.IsNullOrWhiteSpace(volumeRoot))
            throw new InvalidDataException("O caminho autorizado não possui raiz física.");
        var current = volumeRoot;
        EnsureExistingPathIsNotReparsePoint(current);
        foreach (var segment in Path.GetRelativePath(volumeRoot, canonicalCandidate).Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            try
            {
                EnsureExistingPathIsNotReparsePoint(current);
            }
            catch (FileNotFoundException)
            {
                break;
            }
            catch (DirectoryNotFoundException)
            {
                break;
            }
        }
    }

    private static void EnsureExistingPathIsNotReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException(
                $"O caminho contém um atalho ou junção não autorizado: {path}");
    }

    private static FileStream OpenValidatedMutableFile(
        string path,
        string installationRoot,
        FileMode mode,
        bool asynchronous,
        FileShare share = FileShare.Read,
        bool writeThrough = false)
    {
        EnsureNoReparsePoints(installationRoot, path);
        FileStream? stream = null;
        try
        {
            stream = OpenValidatedFile(
                path,
                mode,
                FileAccess.ReadWrite,
                share,
                asynchronous,
                writeThrough);
            EnsureNoReparsePoints(installationRoot, path);
            ValidateMutableHandle(stream.SafeFileHandle, path);
            return stream;
        }
        catch
        {
            stream?.Dispose();
            throw;
        }
    }

    private static FileStream OpenValidatedFile(
        string path,
        FileMode mode,
        FileAccess access,
        FileShare share,
        bool asynchronous,
        bool writeThrough = false)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("A validação segura de arquivos requer Windows.");

        var creationDisposition = mode switch
        {
            FileMode.CreateNew => CreateNewDisposition,
            FileMode.Open => OpenExistingDisposition,
            FileMode.OpenOrCreate => OpenAlwaysDisposition,
            _ => throw new ArgumentOutOfRangeException(
                nameof(mode),
                "O modo solicitado pode truncar antes da validação.")
        };
        var desiredAccess = access switch
        {
            FileAccess.Read => GenericRead,
            FileAccess.Write => GenericWrite,
            FileAccess.ReadWrite => GenericRead | GenericWrite,
            _ => throw new ArgumentOutOfRangeException(nameof(access))
        };
        var flags = FileAttributeNormal | FileFlagOpenReparsePoint | FileFlagSequentialScan;
        if (asynchronous) flags |= FileFlagOverlapped;
        if (writeThrough) flags |= FileFlagWriteThrough;

        var handle = CreateFile(
            ToExtendedLengthPath(path),
            desiredAccess,
            (uint)share,
            IntPtr.Zero,
            creationDisposition,
            flags,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException(
                "Não foi possível abrir o arquivo por um handle seguro.",
                new Win32Exception(error));
        }

        try
        {
            ValidateMutableHandle(handle, path);
            return new FileStream(handle, access, BufferSize, asynchronous);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static long GetValidatedMutableFileLength(string path, string installationRoot)
    {
        using var stream = OpenValidatedMutableFile(
            path,
            installationRoot,
            FileMode.Open,
            asynchronous: false);
        return stream.Length;
    }

    private static long GetValidatedMutableFileLength(
        string path,
        PathIdentity.DirectoryTreeLease pathLease)
    {
        pathLease.Revalidate();
        using var stream = pathLease.OpenFile(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.SequentialScan);
        _ = PathIdentity.CaptureFileIdentity(stream.SafeFileHandle, path);
        pathLease.Revalidate();
        return stream.Length;
    }

    private static void ValidateMutableFilePath(string path, string installationRoot)
    {
        using var stream = OpenValidatedMutableFile(
            path,
            installationRoot,
            FileMode.Open,
            asynchronous: false);
    }

    private static void ValidateMutableFilePath(string path)
    {
        using var stream = OpenValidatedFile(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            asynchronous: false);
    }

    private static SafeFileHandle AcquireDirectoryGuard(string directoryPath, string installationRoot)
    {
        EnsureNoReparsePoints(installationRoot, directoryPath);
        var handle = CreateFile(
            ToExtendedLengthPath(directoryPath),
            0,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExistingDisposition,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException(
                "Não foi possível proteger a pasta do artefato.",
                new Win32Exception(error));
        }

        try
        {
            ValidateDirectoryGuard(handle, directoryPath);
            EnsureNoReparsePoints(installationRoot, directoryPath);
            ValidateDirectoryGuard(handle, directoryPath);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static void ValidateMutableHandle(SafeFileHandle handle, string expectedPath)
    {
        var information = GetHandleInformation(handle);
        var attributes = (FileAttributes)information.FileAttributes;
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new InvalidDataException("O arquivo aberto é um reparse point ou diretório.");
        if (information.NumberOfLinks != 1)
            throw new InvalidDataException("Hardlinks não são aceitos para arquivos mutáveis.");
        ValidateHandlePath(handle, expectedPath);
    }

    private static void ValidateDirectoryGuard(SafeFileHandle handle, string expectedPath)
    {
        var information = GetHandleInformation(handle);
        var attributes = (FileAttributes)information.FileAttributes;
        if ((attributes & FileAttributes.Directory) == 0
            || (attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("A pasta protegida não é um diretório físico.");
        ValidateHandlePath(handle, expectedPath);
    }

    private static ByHandleFileInformation GetHandleInformation(SafeFileHandle handle)
    {
        if (handle.IsInvalid || handle.IsClosed
            || !GetFileInformationByHandle(handle, out var information))
            throw new IOException(
                "Não foi possível revalidar o handle do arquivo.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        return information;
    }

    private static void ValidateHandlePath(SafeFileHandle handle, string expectedPath)
    {
        var capacity = 512;
        while (capacity <= 32_768)
        {
            var buffer = new char[capacity];
            var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
            if (length == 0)
                throw new IOException(
                    "Não foi possível confirmar o caminho físico do arquivo.",
                    new Win32Exception(Marshal.GetLastWin32Error()));
            if (length < buffer.Length)
            {
                var actual = NormalizeHandlePath(new string(buffer, 0, (int)length));
                var expected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedPath));
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        "O handle aberto não corresponde ao caminho autorizado.");
                return;
            }
            capacity = checked((int)length + 1);
        }
        throw new InvalidDataException("O caminho físico do arquivo excede o limite seguro.");
    }

    private static string NormalizeHandlePath(string path)
    {
        const string extendedUncPrefix = @"\\?\UNC\";
        const string extendedPrefix = @"\\?\";
        var normalized = path.StartsWith(extendedUncPrefix, StringComparison.OrdinalIgnoreCase)
            ? @"\\" + path[extendedUncPrefix.Length..]
            : path.StartsWith(extendedPrefix, StringComparison.OrdinalIgnoreCase)
                ? path[extendedPrefix.Length..]
                : path;
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(normalized));
    }

    private static string ToExtendedLengthPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (fullPath.StartsWith(@"\\?\", StringComparison.Ordinal)) return fullPath;
        if (fullPath.StartsWith(@"\\.\", StringComparison.Ordinal))
            throw new InvalidDataException("Namespaces de dispositivo não são aceitos.");
        return fullPath.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + fullPath[2..]
            : @"\\?\" + fullPath;
    }

    private static FileStream AcquireArtifactLock(
        string destinationPath,
        PathIdentity.DirectoryTreeLease pathLease)
    {
        var lockPath = destinationPath + LockSuffix;
        try
        {
            return pathLease.OpenFile(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                1,
                FileOptions.WriteThrough,
                deleteAccess: true);
        }
        catch (IOException exception)
        {
            throw new IOException("Este artefato já está sendo processado por outra instância.", exception);
        }
    }

    private static void DisposeArtifactLock(FileStream? artifactLock, string destinationPath)
    {
        if (artifactLock is null) return;
        try
        {
            var lockPath = destinationPath + LockSuffix;
            var identity = PathIdentity.CaptureFileIdentity(
                artifactLock.SafeFileHandle,
                lockPath);
            PathIdentity.DeleteByHandle(
                artifactLock.SafeFileHandle,
                lockPath,
                identity);
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidDataException)
        {
            // Fail closed: never fall back to pathname deletion. A stale lock is
            // safer than deleting an object whose identity could not be proven.
        }
        finally
        {
            artifactLock.Dispose();
        }
    }

    private static void ResetPartialForFreshAttempt(ActiveDownload active)
    {
        if (File.Exists(active.PartialPath))
            PreserveFileByHandle(active.PartialPath, active.PathLease);
        active.Metadata.ETag = string.Empty;
        active.Metadata.LastModified = string.Empty;
        active.Metadata.ArchiveReady = false;
        if (!TrySaveResumeMetadata(active))
            throw new IOException("Não foi possível registrar a reinicialização segura.");
    }

    private static bool IsRedirect(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.MovedPermanently
        or HttpStatusCode.Found
        or HttpStatusCode.SeeOther
        or HttpStatusCode.TemporaryRedirect
        or HttpStatusCode.PermanentRedirect;

    private static bool IsTransientStatus(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.RequestTimeout
        or HttpStatusCode.TooManyRequests
        or HttpStatusCode.InternalServerError
        or HttpStatusCode.BadGateway
        or HttpStatusCode.ServiceUnavailable
        or HttpStatusCode.GatewayTimeout;

    private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta) return delta;
        if (retryAfter?.Date is { } date)
            return date > DateTimeOffset.UtcNow ? date - DateTimeOffset.UtcNow : TimeSpan.Zero;
        return null;
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            SslOptions = { CertificateRevocationCheckMode = X509RevocationMode.Online },
            ConnectTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static void DeleteExactFile(string path)
    {
        try
        {
            var parent = Path.GetDirectoryName(Path.GetFullPath(path));
            if (parent is not null)
                _ = PathIdentity.DeleteFileExact(path, parent);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (InvalidDataException)
        {
        }
    }

    private static void PreservePartialBeforeRestart(string path)
    {
        if (!File.Exists(path)) return;
        ValidateMutableFilePath(path);
        var preservedPath = path + ".preserved-"
                            + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture)
                            + "-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Move(path, preservedPath, overwrite: false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new IOException(
                "O arquivo antigo foi preservado, mas não pôde ser separado com segurança.",
                exception);
        }
    }

    private static void PreserveFileByHandle(
        string path,
        PathIdentity.DirectoryTreeLease pathLease)
    {
        pathLease.Revalidate();
        using var stream = pathLease.OpenFile(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.SequentialScan,
            deleteAccess: true);
        var identity = PathIdentity.CaptureFileIdentity(stream.SafeFileHandle, path);
        var preservedLeaf = Path.GetFileName(path)
                            + ".preserved-"
                            + DateTimeOffset.UtcNow.ToString(
                                "yyyyMMddHHmmssfff",
                                CultureInfo.InvariantCulture)
                            + "-" + Guid.NewGuid().ToString("N");
        _ = PathIdentity.RenameByHandle(
            stream.SafeFileHandle,
            identity,
            pathLease.AnchorHandle,
            Path.GetDirectoryName(path)!,
            preservedLeaf,
            replaceIfExists: false);
        pathLease.Revalidate();
    }

    private static void DeletePreservedPartials(string partialPath)
    {
        try
        {
            var directory = Path.GetDirectoryName(partialPath);
            var fileName = Path.GetFileName(partialPath);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return;
            foreach (var path in Directory.EnumerateFiles(
                         directory,
                         fileName + ".preserved-*",
                         SearchOption.TopDirectoryOnly))
                DeleteExactFile(path);
            foreach (var path in Directory.EnumerateFiles(
                         directory,
                         fileName + ResumeSuffix + ".preserved-*",
                         SearchOption.TopDirectoryOnly))
                DeleteExactFile(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static bool TryDiscardFileSet(
        ResumeFileSet saved,
        string installationRoot,
        out string failure)
    {
        EnsureNoReparsePoints(installationRoot, saved.PartialPath);
        EnsureNoReparsePoints(installationRoot, saved.MetadataPath);
        EnsureNoReparsePoints(installationRoot, saved.DestinationPath);
        if (!TryDeleteFileStrict(saved.PartialPath, installationRoot, out failure)) return false;
        if (!TryDeletePreservedPartialsStrict(
                saved.PartialPath,
                installationRoot,
                out failure)) return false;
        if (!TryDeleteFileStrict(saved.DestinationPath, installationRoot, out failure)) return false;
        return TryDeleteFileStrict(saved.MetadataPath, installationRoot, out failure);
    }

    private static bool TryDeletePreservedPartialsStrict(
        string partialPath,
        string installationRoot,
        out string failure)
    {
        failure = string.Empty;
        try
        {
            var directory = Path.GetDirectoryName(partialPath);
            var fileName = Path.GetFileName(partialPath);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return true;
            var candidates = Directory.EnumerateFiles(
                    directory,
                    fileName + ".preserved-*",
                    SearchOption.TopDirectoryOnly)
                .Concat(Directory.EnumerateFiles(
                    directory,
                    fileName + ResumeSuffix + ".preserved-*",
                    SearchOption.TopDirectoryOnly))
                .ToArray();
            foreach (var candidate in candidates)
            {
                if (!TryDeleteFileStrict(candidate, installationRoot, out failure)) return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            failure = "Não foi possível remover um parcial preservado.";
            return false;
        }
    }

    private static bool TryDeleteFileStrict(
        string path,
        string installationRoot,
        out string failure)
    {
        failure = string.Empty;
        try
        {
            if (!File.Exists(path)) return true;
            _ = PathIdentity.DeleteFileExact(path, installationRoot);
            return true;
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidDataException)
        {
            failure = "O arquivo está em uso ou não pôde ser removido.";
            return false;
        }
    }

    private static string SafeLocalFailure(Exception exception) => exception switch
    {
        InvalidDataException => exception.Message,
        UnauthorizedAccessException => "A pasta escolhida não permite esta operação.",
        IOException => exception.Message,
        _ => "Não foi possível preparar o artefato com segurança."
    };

    private static string SafeDownloadFailure(Exception exception) => exception switch
    {
        InvalidDataException => exception.Message,
        UnauthorizedAccessException => "A gravação do pacote não foi autorizada.",
        CryptographicException => "Não foi possível verificar a integridade do pacote.",
        IOException => exception.Message,
        _ => "O download falhou; o progresso parcial foi preservado."
    };

    public void Dispose()
    {
        ActiveDownload[] activeDownloads;
        lock (_lifetimeGate)
        {
            if (_disposed) return;
            _disposed = true;
            activeDownloads = _active.Values.ToArray();
            if (activeDownloads.Length == 0) DisposeOwnedResourcesUnderLock();
        }

        foreach (var active in activeDownloads)
        {
            lock (active.MetadataGate)
            {
                if (active.IsDisposed) continue;
                active.ShutdownRequested = true;
                active.Metadata.IsPaused = false;
                TrySaveResumeMetadata(active);
                try
                {
                    active.Cancellation.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }
    }

    private void DisposeOwnedResourcesUnderLock()
    {
        if (_ownedResourcesDisposed) return;
        _ownedResourcesDisposed = true;
        if (_ownsHttpClient) _httpClient.Dispose();
        _directHttpClient.Dispose();
        _downloadQueue.Dispose();
    }

    private void FinishActiveDownload(string itemId, ActiveDownload active)
    {
        _active.TryRemove(itemId, out _);
        lock (active.MetadataGate)
        {
            active.IsDisposed = true;
            active.Dispose();
        }
        lock (_lifetimeGate)
        {
            if (_disposed && _active.IsEmpty) DisposeOwnedResourcesUnderLock();
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW",
        CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        [Out] char[] filePath,
        uint filePathLength,
        uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    private sealed class ActiveDownload : IDisposable
    {
        public ActiveDownload(
            CancellationTokenSource cancellation,
            string metadataPath,
            string partialPath,
            string destinationPath,
            string installationRoot,
            DownloadResumeMetadata metadata,
            FileStream artifactLock,
            PathIdentity.DirectoryTreeLease pathLease)
        {
            Cancellation = cancellation;
            MetadataPath = metadataPath;
            PartialPath = partialPath;
            DestinationPath = destinationPath;
            InstallationRoot = installationRoot;
            Metadata = metadata;
            ArtifactLock = artifactLock;
            PathLease = pathLease;
        }

        public CancellationTokenSource Cancellation { get; }
        public string MetadataPath { get; }
        public string PartialPath { get; }
        public string DestinationPath { get; }
        public string InstallationRoot { get; }
        public string LockPath => DestinationPath + LockSuffix;
        public DownloadResumeMetadata Metadata { get; }
        public FileStream ArtifactLock { get; }
        public PathIdentity.DirectoryTreeLease PathLease { get; }
        public object MetadataGate { get; } = new();
        public bool IsDisposed { get; set; }
        public bool PauseRequested { get; set; }
        public bool ShutdownRequested { get; set; }

        public void Dispose()
        {
            Cancellation.Dispose();
            DisposeArtifactLock(ArtifactLock, DestinationPath);
            PathLease.Dispose();
        }
    }

    private sealed class DownloadResumeMetadata
    {
        public int SchemaVersion { get; set; }
        public string ItemId { get; set; } = string.Empty;
        public string ProductId { get; set; } = string.Empty;
        public string ArtifactId { get; set; } = string.Empty;
        public int ArtifactVersion { get; set; }
        public string Sha256 { get; set; } = string.Empty;
        public long ContentLength { get; set; }
        public string ManifestIdentity { get; set; } = string.Empty;
        public CatalogExtractPolicy ExtractPolicy { get; set; }
        public string SafeFileName { get; set; } = string.Empty;
        public string FileExtension { get; set; } = string.Empty;
        public string ETag { get; set; } = string.Empty;
        public string LastModified { get; set; } = string.Empty;
        public bool IsPaused { get; set; }
        public bool ArchiveReady { get; set; }
        public DateTimeOffset UpdatedUtc { get; set; }
    }

    private sealed record ResumeFileSet(
        string MetadataPath,
        string PartialPath,
        string DestinationPath,
        DownloadResumeMetadata Metadata);

    private sealed class FailClosedRequestProvider : ICatalogDownloadRequestProvider
    {
        public static FailClosedRequestProvider Instance { get; } = new();

        public ValueTask<HttpRequestMessage> CreateRequestAsync(
            string itemId,
            CatalogArtifactDescriptor artifact,
            long offset,
            CatalogDownloadValidators validators,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<HttpRequestMessage>(
                new InvalidOperationException("Nenhuma sessão autorizada está disponível."));
    }

    private sealed class RestartDownloadException : IOException;

    private sealed class TransientDownloadException : IOException
    {
        public TransientDownloadException(
            string message,
            TimeSpan? retryAfter = null,
            Exception? innerException = null)
            : base(message, innerException)
        {
            RetryAfter = retryAfter;
        }

        public TimeSpan? RetryAfter { get; }
    }
}
