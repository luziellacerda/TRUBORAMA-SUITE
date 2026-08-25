using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TurboBoxManager.Catalog;

public sealed class CatalogDownloadOptions
{
    public long MaximumFileSizeBytes { get; init; } = 256L * 1024L * 1024L;
    public int MaximumRedirects { get; init; } = 5;
    public TimeSpan InactivityTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public IReadOnlyList<TimeSpan> RetryDelays { get; init; } =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30)];
    public IReadOnlySet<string> AllowedHosts { get; init; } = new HashSet<string>(
        [
            "github.com",
            "objects.githubusercontent.com",
            "release-assets.githubusercontent.com",
            "raw.githubusercontent.com"
        ],
        StringComparer.OrdinalIgnoreCase);
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
/// Persistent, resumable downloader. Network interruptions are retried with an
/// HTTP Range request; pausing or closing the application keeps the partial
/// package. Only Discard removes the partial data.
/// </summary>
public sealed class CatalogDownloadService : IDisposable
{
    private const string ResumeSuffix = ".resume.json";
    private const int BufferSize = 128 * 1024;
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions ResumeJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly HttpClient _httpClient;
    private readonly CatalogDownloadOptions _options;
    private readonly bool _ownsHttpClient;
    private readonly SemaphoreSlim _downloadQueue = new(1, 1);
    private readonly ConcurrentDictionary<string, ActiveDownload> _active =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lifetimeGate = new();
    private bool _disposed;
    private bool _ownedResourcesDisposed;

    public CatalogDownloadService(CatalogDownloadOptions? options = null)
        : this(CreateHttpClient(), options, ownsHttpClient: true)
    {
    }

    public CatalogDownloadService(HttpClient httpClient, CatalogDownloadOptions? options = null)
        : this(httpClient, options, ownsHttpClient: false)
    {
    }

    private CatalogDownloadService(
        HttpClient httpClient,
        CatalogDownloadOptions? options,
        bool ownsHttpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
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
    }

    public bool IsActive(string itemId) => _active.ContainsKey(itemId);

    /// <summary>Pauses a transfer without removing its partial file.</summary>
    public bool Pause(string itemId)
    {
        if (!_active.TryGetValue(itemId, out var active)) return false;
        active.PauseRequested = true;
        active.Metadata.IsPaused = true;
        if (!TrySaveResumeMetadata(active))
        {
            active.PauseRequested = false;
            active.Metadata.IsPaused = false;
            return false;
        }
        active.Cancellation.Cancel();
        return true;
    }

    // Compatibility with the previous UI: cancellation now means pause.
    public bool Cancel(string itemId) => Pause(itemId);

    /// <summary>
    /// Explicitly removes a partial or downloaded package. This is deliberately
    /// separate from Pause so an accidental interruption never loses progress.
    /// </summary>
    public bool Discard(CatalogItem item, string installationRoot)
    {
        ArgumentNullException.ThrowIfNull(item);

        // The UI deliberately requires Pause first. This avoids a terminal race
        // between publishing the completed file and trying to erase it.
        if (_active.ContainsKey(item.Id)) return false;

        try
        {
            var canonicalRoot = Path.GetFullPath(installationRoot);
            var savedStates = FindResumeFileSets(canonicalRoot, item.Id);
            foreach (var saved in savedStates)
            {
                if (!TryDiscardFileSet(saved, canonicalRoot, out var failure))
                    throw new IOException(failure);
            }

            if (savedStates.Count == 0
                && Uri.TryCreate(item.DownloadUrl, UriKind.Absolute, out var candidateUri))
            {
                var sourceUri = ValidateSourceUri(candidateUri.AbsoluteUri);
                var destinationPath = BuildSafeDestinationPath(canonicalRoot, item, sourceUri);
                var partialPath = destinationPath + ".part";
                EnsureNoReparsePoints(canonicalRoot, destinationPath);
                EnsureNoReparsePoints(canonicalRoot, partialPath);
                EnsureNoReparsePoints(canonicalRoot, partialPath + ResumeSuffix);
                if (!TryDeleteFileStrict(partialPath, out var failure)
                    || !TryDeletePreservedPartialsStrict(partialPath, out failure)
                    || !TryDeleteFileStrict(partialPath + ResumeSuffix, out failure))
                    throw new IOException(failure);
            }

            if (!string.IsNullOrWhiteSpace(item.ArchiveFilePath))
            {
                var archivePath = Path.GetFullPath(item.ArchiveFilePath);
                if (!IsWithinRoot(archivePath, canonicalRoot))
                    throw new InvalidDataException("O pacote salvo está fora da pasta autorizada.");
                EnsureNoReparsePoints(canonicalRoot, archivePath);
                if (!TryDeleteFileStrict(archivePath, out var failure))
                    throw new IOException(failure);
            }

            item.DiscardDownload();
            return true;
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidDataException
                                           or UriFormatException)
        {
            item.SetDownloadState(CatalogDownloadState.Failed, exception.Message);
            return false;
        }
    }

    /// <summary>Removes only the state record after a successful extraction.</summary>
    public bool MarkExtractionCompleted(
        CatalogItem item,
        string installationRoot,
        string archivePath)
    {
        ArgumentNullException.ThrowIfNull(item);
        try
        {
            var canonicalArchivePath = Path.GetFullPath(archivePath);
            var savedStates = FindResumeFileSets(installationRoot, item.Id);
            var matchingStates = savedStates.Where(state =>
                    state.Metadata.ArchiveReady
                    && Path.GetFullPath(state.DestinationPath).Equals(
                        canonicalArchivePath,
                        StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (matchingStates.Length == 0) return false;

            foreach (var saved in matchingStates)
            {
                EnsureNoReparsePoints(installationRoot, saved.MetadataPath);
                EnsureNoReparsePoints(installationRoot, canonicalArchivePath);
                if (!TryDeleteFileStrict(saved.MetadataPath, out _)) return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidDataException
                                           or UriFormatException
                                           or ArgumentException)
        {
            return false;
        }
    }

    public IReadOnlyList<CatalogResumableDownload> DiscoverResumableDownloads(string installationRoot)
    {
        var canonicalRoot = Path.GetFullPath(installationRoot);
        var records = new List<CatalogResumableDownload>();
        foreach (var saved in FindResumeFileSets(canonicalRoot))
        {
            try
            {
                if (saved.Metadata.ArchiveReady
                    && !File.Exists(saved.PartialPath)
                    && File.Exists(saved.DestinationPath))
                {
                    var length = new FileInfo(saved.DestinationPath).Length;
                    records.Add(new CatalogResumableDownload(
                        saved.Metadata.ItemId,
                        length,
                        saved.Metadata.TotalBytes is > 0 ? saved.Metadata.TotalBytes : length,
                        true,
                        true,
                        saved.DestinationPath));
                    continue;
                }

                var bytes = File.Exists(saved.PartialPath)
                    ? new FileInfo(saved.PartialPath).Length
                    : 0;
                records.Add(new CatalogResumableDownload(
                    saved.Metadata.ItemId,
                    bytes,
                    saved.Metadata.TotalBytes is > 0 ? saved.Metadata.TotalBytes : null,
                    saved.Metadata.IsPaused));
            }
            catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException
                                               or JsonException
                                               or ArgumentException)
            {
                // A damaged sidecar cannot be trusted and is ignored. The .part
                // remains untouched so the user can still recover it manually.
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

        Uri sourceUri;
        string destinationPath;
        try
        {
            if (string.IsNullOrWhiteSpace(item.DownloadUrl))
                throw new InvalidDataException("Nenhum endereço autorizado foi configurado. Nada foi baixado.");
            sourceUri = ValidateSourceUri(item.DownloadUrl);
            if (!Directory.Exists(installationRoot))
                throw new InvalidDataException("A pasta de instalação não existe. Escolha uma pasta válida.");
            destinationPath = BuildSafeDestinationPath(installationRoot, item, sourceUri);
        }
        catch (Exception exception) when (exception is InvalidDataException
                                           or UriFormatException
                                           or IOException
                                           or UnauthorizedAccessException)
        {
            item.SetDownloadState(CatalogDownloadState.Failed, exception.Message);
            return new CatalogDownloadResult(CatalogDownloadState.Failed, exception.Message);
        }

        string partialPath;
        string metadataPath;
        DownloadResumeMetadata metadata;
        try
        {
            var reusable = FindResumeFileSets(
                    installationRoot,
                    item.Id,
                    CreateSourceFingerprint(sourceUri))
                .OrderByDescending(candidate => candidate.Metadata.UpdatedUtc)
                .FirstOrDefault();
            if (reusable is not null) destinationPath = reusable.DestinationPath;

            partialPath = destinationPath + ".part";
            metadataPath = partialPath + ResumeSuffix;
            EnsureNoReparsePoints(
                installationRoot,
                Path.GetDirectoryName(destinationPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            EnsureNoReparsePoints(
                installationRoot,
                Path.GetDirectoryName(destinationPath)!);
            metadata = LoadMatchingMetadata(metadataPath, item, sourceUri, destinationPath, partialPath);
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidDataException
                                           or ArgumentException
                                           or NotSupportedException)
        {
            item.SetDownloadState(CatalogDownloadState.Failed, exception.Message);
            return new CatalogDownloadResult(
                CatalogDownloadState.Failed,
                $"Não foi possível preparar o download: {exception.Message}");
        }

        if (metadata.ArchiveReady
            && !File.Exists(partialPath)
            && File.Exists(destinationPath))
        {
            if (item.Extract) item.MarkArchiveReady(destinationPath);
            else item.CompleteDownload(destinationPath);
            return new CatalogDownloadResult(
                CatalogDownloadState.Completed,
                "O pacote já foi baixado e está pronto para extrair.",
                destinationPath);
        }

        metadata.IsPaused = false;
        var active = new ActiveDownload(
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken),
            metadataPath,
            partialPath,
            destinationPath,
            Path.GetFullPath(installationRoot),
            metadata);

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
            EnsureNoReparsePoints(active.InstallationRoot, partialPath);
            // A zero-byte partial makes a queued/offline transfer discoverable
            // even if the first response never delivers payload bytes.
            using var durablePartial = new FileStream(
                partialPath,
                FileMode.OpenOrCreate,
                FileAccess.Write,
                FileShare.Read);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            FinishActiveDownload(item.Id, active);
            item.SetDownloadState(CatalogDownloadState.Failed, exception.Message);
            return new CatalogDownloadResult(
                CatalogDownloadState.Failed,
                $"Não foi possível preparar a retomada: {exception.Message}");
        }

        var existingBytes = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
        item.RestoreDownload(existingBytes, metadata.TotalBytes, isPaused: false);

        try
        {
            if (!TrySaveResumeMetadata(active))
                throw new IOException("Não foi possível salvar o estado persistente do download.");

            var retryNumber = 0;
            while (true)
            {
                active.Cancellation.Token.ThrowIfCancellationRequested();
                var queueEntered = false;
                try
                {
                    item.SetDownloadState(CatalogDownloadState.Queued,
                        existingBytes > 0 ? "Aguardando na fila para continuar" : "Aguardando na fila");
                    await _downloadQueue.WaitAsync(active.Cancellation.Token);
                    queueEntered = true;
                    await DownloadAttemptAsync(item, sourceUri, active);
                    break;
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
                    existingBytes = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
                }
                finally
                {
                    if (queueEntered) _downloadQueue.Release();
                }
            }

            if (item.Extract)
                item.MarkArchiveReady(destinationPath);
            else
                item.CompleteDownload(destinationPath);

            return new CatalogDownloadResult(
                CatalogDownloadState.Completed,
                item.Extract
                    ? $"Download concluído: {Path.GetFileName(destinationPath)}. Iniciando extração."
                    : $"Download concluído e verificado: {Path.GetFileName(destinationPath)}",
                destinationPath);
        }
        catch (OperationCanceledException)
        {
            metadata.IsPaused = !active.ShutdownRequested;
            TrySaveResumeMetadata(active);
            var bytes = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
            item.UpdateDownloadProgress(bytes, metadata.TotalBytes);
            item.PauseDownload(active.ShutdownRequested
                ? "Programa fechado — o download continuará automaticamente ao abrir novamente"
                : "Download pausado — o progresso foi preservado");
            return new CatalogDownloadResult(
                CatalogDownloadState.Paused,
                "Download pausado. Todo o progresso foi preservado.",
                partialPath);
        }
        catch (Exception exception) when (exception is HttpRequestException
                                           or IOException
                                           or InvalidDataException
                                           or UnauthorizedAccessException
                                           or CryptographicException)
        {
            metadata.IsPaused = true;
            TrySaveResumeMetadata(active);
            var bytes = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
            item.UpdateDownloadProgress(bytes, metadata.TotalBytes);
            item.SetDownloadState(CatalogDownloadState.Failed, exception.Message);
            return new CatalogDownloadResult(
                CatalogDownloadState.Failed,
                $"Falha no download: {exception.Message}",
                partialPath);
        }
        finally
        {
            FinishActiveDownload(item.Id, active);
        }
    }

    public string BuildSafeDestinationPath(string installationRoot, CatalogItem item, Uri sourceUri)
    {
        var canonicalRoot = Path.GetFullPath(installationRoot);
        var categorySegment = SanitizePathSegment(item.CategoryId);
        var titleSegment = SanitizePathSegment(
            string.IsNullOrWhiteSpace(item.Title) ? item.Id : item.Title);
        if (titleSegment.Length > 72) titleSegment = titleSegment[..72].TrimEnd('-');
        var idSegment = SanitizePathSegment(item.Id);
        var idHash = Convert.ToHexString(SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(item.Id)))
            [..12]
            .ToLowerInvariant();
        var itemSegment = $"{titleSegment}-{idSegment}-{idHash}";
        var extension = ResolveSafeExtension(item.DownloadFileExtension, sourceUri);
        var useGameLibraryLayout = !item.CategoryId.Equals(
                                       "system-tools",
                                       StringComparison.OrdinalIgnoreCase)
                                   && CatalogArchiveExtractor.IsGameLibraryRoot(canonicalRoot);
        var gameTitleSegment = titleSegment.Length > 48
            ? titleSegment[..48].TrimEnd('-')
            : titleSegment;
        var gameItemFolder = $"{gameTitleSegment}-{idHash}";
        var gameFileName = $"{idSegment}-{idHash}{extension}";
        var destination = useGameLibraryLayout
            ? Path.GetFullPath(Path.Combine(
                canonicalRoot,
                categorySegment,
                gameItemFolder,
                gameFileName))
            : Path.GetFullPath(Path.Combine(
                canonicalRoot,
                "Turborama",
                "Downloads",
                categorySegment,
                itemSegment + extension));

        if (!IsWithinRoot(destination, canonicalRoot))
            throw new InvalidDataException("O destino calculado saiu da pasta autorizada.");
        return destination;
    }

    private async Task DownloadAttemptAsync(CatalogItem item, Uri sourceUri, ActiveDownload active)
    {
        EnsureNoReparsePoints(active.InstallationRoot, active.PartialPath);
        EnsureNoReparsePoints(active.InstallationRoot, active.DestinationPath);
        var offset = File.Exists(active.PartialPath) ? new FileInfo(active.PartialPath).Length : 0;
        if (offset > _options.MaximumFileSizeBytes)
            throw new InvalidDataException("O arquivo parcial excede o limite seguro configurado.");

        using var response = await SendWithSafeRedirectsAsync(
            sourceUri,
            offset,
            active.Metadata,
            active.Cancellation.Token);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            var remoteLength = response.Content.Headers.ContentRange?.Length;
            var hasReliableIdentity = !string.IsNullOrWhiteSpace(item.Sha256)
                                      || !string.IsNullOrWhiteSpace(active.Metadata.ETag)
                                      || !string.IsNullOrWhiteSpace(active.Metadata.LastModified);
            if (offset > 0
                && remoteLength == offset
                && active.Metadata.TotalBytes == offset
                && hasReliableIdentity)
            {
                ValidateResumeValidator(active.Metadata, response);
                await VerifyAndFinalizeAsync(item, active, offset, active.Cancellation.Token);
                return;
            }

            if (offset > 0)
            {
                PreservePartialBeforeRestart(active.PartialPath);
                active.Metadata.ETag = string.Empty;
                active.Metadata.LastModified = string.Empty;
                active.Metadata.TotalBytes = null;
                active.Metadata.ArchiveReady = false;
                active.Metadata.IsPaused = false;
                if (!TrySaveResumeMetadata(active))
                    throw new IOException("Não foi possível registrar o reinício seguro do download.");
                throw new TransientDownloadException(
                    "O arquivo remoto mudou; o parcial antigo foi preservado e o download será reiniciado.",
                    TimeSpan.Zero);
            }

            throw new InvalidDataException("O servidor recusou a retomada porque o arquivo remoto mudou.");
        }

        if (IsTransientStatus(response.StatusCode))
            throw new TransientDownloadException(
                $"O servidor respondeu {(int)response.StatusCode}.",
                GetRetryAfter(response));

        if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.PartialContent))
            throw new InvalidDataException(
                $"O servidor recusou o download (HTTP {(int)response.StatusCode}).");

        var append = response.StatusCode == HttpStatusCode.PartialContent;
        long? totalBytes;
        if (append)
        {
            var range = response.Content.Headers.ContentRange;
            if (range?.From != offset || !string.Equals(range.Unit, "bytes", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("O servidor retornou uma faixa incompatível com o arquivo parcial.");
            totalBytes = range.Length;
            if (totalBytes is not > 0)
                throw new InvalidDataException("O servidor não informou o tamanho total ao retomar.");

            ValidateResumeValidator(active.Metadata, response);
        }
        else
        {
            // A 200 response to a Range request means the server ignored Range
            // or the validator changed. Preserve the old partial before safely
            // restarting, so even this exceptional case does not erase data.
            if (offset > 0) PreservePartialBeforeRestart(active.PartialPath);
            offset = 0;
            totalBytes = response.Content.Headers.ContentLength;
        }

        if (totalBytes == 0 || response.Content.Headers.ContentLength == 0)
            throw new InvalidDataException("O servidor retornou um arquivo vazio. O link precisa ser corrigido.");
        if (totalBytes > _options.MaximumFileSizeBytes)
            throw new InvalidDataException("O arquivo excede o limite seguro configurado.");

        var responseStrongEtag = response.Headers.ETag is { IsWeak: false } strongEtag
            ? strongEtag.ToString()
            : string.Empty;
        var responseLastModified = response.Content.Headers.LastModified?.ToString("R", CultureInfo.InvariantCulture)
                                   ?? string.Empty;
        if (!append)
        {
            active.Metadata.ETag = responseStrongEtag;
            active.Metadata.LastModified = responseLastModified;
        }
        else
        {
            // A valid 206 may omit validators. Keep the validator that was used
            // in If-Range instead of silently making the next retry unsafe.
            if (responseStrongEtag.Length > 0) active.Metadata.ETag = responseStrongEtag;
            if (responseLastModified.Length > 0) active.Metadata.LastModified = responseLastModified;
        }
        active.Metadata.TotalBytes = totalBytes;
        active.Metadata.ArchiveReady = false;
        active.Metadata.IsPaused = false;
        if (!TrySaveResumeMetadata(active))
            throw new IOException("Não foi possível salvar os validadores antes de gravar o download.");

        item.SetDownloadState(CatalogDownloadState.Downloading,
            offset > 0 ? "Continuando download" : "Iniciando download");
        item.UpdateDownloadProgress(offset, totalBytes);

        await using var input = await ReadContentStreamAsync(response, active.Cancellation.Token);
        await using var output = new FileStream(
            active.PartialPath,
            append ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var buffer = new byte[BufferSize];
        var totalRead = offset;
        while (true)
        {
            var read = await ReadWithInactivityTimeoutAsync(input, buffer, active.Cancellation.Token);
            if (read == 0) break;

            totalRead = checked(totalRead + read);
            if (totalRead > _options.MaximumFileSizeBytes
                || totalBytes is > 0 && totalRead > totalBytes.Value)
                throw new InvalidDataException("O arquivo excedeu o tamanho seguro durante a transferência.");

            await output.WriteAsync(buffer.AsMemory(0, read), active.Cancellation.Token);
            item.UpdateDownloadProgress(totalRead, totalBytes);
        }

        await output.FlushAsync(active.Cancellation.Token);
        if (totalRead == 0)
            throw new InvalidDataException("O servidor retornou um arquivo vazio. O link precisa ser corrigido.");
        if (totalBytes.HasValue && totalRead != totalBytes.Value)
            throw new TransientDownloadException(
                $"A conexão terminou antes do arquivo completo ({totalRead}/{totalBytes.Value} bytes).");

        await output.DisposeAsync();
        await VerifyAndFinalizeAsync(item, active, totalRead, active.Cancellation.Token);
    }

    private async Task VerifyAndFinalizeAsync(
        CatalogItem item,
        ActiveDownload active,
        long expectedLength,
        CancellationToken cancellationToken)
    {
        item.BeginVerification();
        var actualLength = new FileInfo(active.PartialPath).Length;
        if (actualLength == 0 || actualLength != expectedLength)
            throw new InvalidDataException("O tamanho final do arquivo não corresponde ao informado pelo servidor.");

        if (!string.IsNullOrWhiteSpace(item.Sha256))
        {
            await using var stream = new FileStream(
                active.PartialPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
            if (!actualHash.Equals(item.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A verificação SHA-256 falhou. O pacote foi preservado para análise.");
        }

        active.Metadata.TotalBytes = actualLength;
        active.Metadata.ArchiveReady = true;
        active.Metadata.IsPaused = true;
        if (!TrySaveResumeMetadata(active))
            throw new IOException("Não foi possível registrar a conclusão antes de publicar o arquivo.");

        EnsureNoReparsePoints(active.InstallationRoot, active.PartialPath);
        EnsureNoReparsePoints(active.InstallationRoot, active.DestinationPath);
        File.Move(active.PartialPath, active.DestinationPath, overwrite: true);
        DeletePreservedPartials(active.PartialPath);
        if (!item.Extract) DeleteExactFile(active.MetadataPath);
    }

    private async Task<HttpResponseMessage> SendWithSafeRedirectsAsync(
        Uri sourceUri,
        long offset,
        DownloadResumeMetadata metadata,
        CancellationToken cancellationToken)
    {
        var currentUri = sourceUri;
        for (var redirect = 0; redirect <= _options.MaximumRedirects; redirect++)
        {
            ValidateSourceUri(currentUri.AbsoluteUri);
            using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
            request.Headers.UserAgent.ParseAdd("Turborama/2.1-resumable");
            request.Headers.AcceptEncoding.Clear();
            request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
            if (offset > 0)
            {
                request.Headers.Range = new RangeHeaderValue(offset, null);
                var validator = !string.IsNullOrWhiteSpace(metadata.ETag)
                    ? metadata.ETag
                    : metadata.LastModified;
                if (!string.IsNullOrWhiteSpace(validator))
                    request.Headers.TryAddWithoutValidation("If-Range", validator);
            }

            HttpResponseMessage response;
            using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            headerTimeout.CancelAfter(_options.InactivityTimeout);
            try
            {
                response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    headerTimeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TransientDownloadException("A conexão excedeu o tempo limite.");
            }
            catch (HttpRequestException exception)
            {
                throw new TransientDownloadException(
                    $"Não foi possível alcançar o servidor: {exception.Message}",
                    innerException: exception);
            }

            var effectiveUri = response.RequestMessage?.RequestUri ?? currentUri;
            try
            {
                ValidateSourceUri(effectiveUri.AbsoluteUri);
            }
            catch
            {
                response.Dispose();
                throw;
            }

            if (!IsRedirect(response.StatusCode)) return response;

            var location = response.Headers.Location;
            response.Dispose();
            if (location is null)
                throw new InvalidDataException("O servidor retornou um redirecionamento sem destino.");
            currentUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
        }

        throw new InvalidDataException("O download excedeu o limite de redirecionamentos.");
    }

    private async Task<Stream> ReadContentStreamAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadAsStreamAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TransientDownloadException("A conexão ficou sem resposta.");
        }
        catch (Exception exception) when (exception is IOException or HttpRequestException)
        {
            throw new TransientDownloadException("A conexão foi interrompida antes de receber os dados.",
                innerException: exception);
        }
    }

    private async Task<int> ReadWithInactivityTimeoutAsync(
        Stream input,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        using var inactivity = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        inactivity.CancelAfter(_options.InactivityTimeout);
        try
        {
            return await input.ReadAsync(buffer, inactivity.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TransientDownloadException("A conexão ficou inativa e será retomada.");
        }
        catch (Exception exception) when (exception is IOException or HttpRequestException)
        {
            throw new TransientDownloadException("A conexão caiu e será retomada.",
                innerException: exception);
        }
    }

    private static IReadOnlyList<ResumeFileSet> FindResumeFileSets(
        string installationRoot,
        string? itemId = null,
        string? sourceFingerprint = null)
    {
        var canonicalRoot = Path.GetFullPath(installationRoot);
        var downloadsRoot = Path.Combine(canonicalRoot, "Turborama", "Downloads");
        if (!Directory.Exists(downloadsRoot)) return [];

        string[] metadataPaths;
        try
        {
            EnsureNoReparsePoints(canonicalRoot, downloadsRoot);
            metadataPaths = Directory.EnumerateFiles(
                    downloadsRoot,
                    "*.part" + ResumeSuffix,
                    new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible = true,
                        ReturnSpecialDirectories = false,
                        AttributesToSkip = FileAttributes.ReparsePoint
                    })
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or ArgumentException)
        {
            return [];
        }

        var matches = new List<ResumeFileSet>();
        foreach (var metadataPath in metadataPaths)
        {
            try
            {
                EnsureNoReparsePoints(canonicalRoot, metadataPath);
                var metadata = LoadResumeMetadata(metadataPath);
                if (metadata is null || string.IsNullOrWhiteSpace(metadata.ItemId)) continue;
                if (itemId is not null
                    && !metadata.ItemId.Equals(itemId, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (sourceFingerprint is not null
                    && !metadata.SourceFingerprint.Equals(sourceFingerprint, StringComparison.Ordinal))
                    continue;

                var partialPath = metadataPath[..^ResumeSuffix.Length];
                if (!partialPath.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) continue;
                var destinationPath = partialPath[..^5];
                if (!IsWithinRoot(metadataPath, canonicalRoot)
                    || !IsWithinRoot(partialPath, canonicalRoot)
                    || !IsWithinRoot(destinationPath, canonicalRoot)
                    || string.IsNullOrWhiteSpace(metadata.DestinationPath)
                    || !Path.GetFullPath(metadata.DestinationPath)
                        .Equals(Path.GetFullPath(destinationPath), StringComparison.OrdinalIgnoreCase))
                    continue;

                EnsureNoReparsePoints(canonicalRoot, partialPath);
                EnsureNoReparsePoints(canonicalRoot, destinationPath);
                matches.Add(new ResumeFileSet(
                    metadataPath,
                    partialPath,
                    destinationPath,
                    metadata));
            }
            catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException
                                               or JsonException
                                               or ArgumentException
                                               or NotSupportedException)
            {
            }
        }

        return matches;
    }

    private DownloadResumeMetadata LoadMatchingMetadata(
        string metadataPath,
        CatalogItem item,
        Uri sourceUri,
        string destinationPath,
        string partialPath)
    {
        var metadata = LoadResumeMetadata(metadataPath);
        var matches = false;
        try
        {
            var sourceFingerprint = CreateSourceFingerprint(sourceUri);
            matches = metadata is not null
                      && metadata.ItemId.Equals(item.Id, StringComparison.OrdinalIgnoreCase)
                      && metadata.SourceFingerprint.Equals(sourceFingerprint, StringComparison.Ordinal)
                      && Path.GetFullPath(metadata.DestinationPath)
                          .Equals(Path.GetFullPath(destinationPath), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException
                                           or NotSupportedException
                                           or PathTooLongException)
        {
            matches = false;
        }

        if (!matches)
        {
            // A sidecar for another URL cannot safely authorize appending. Move
            // the old partial aside instead of deleting it; only Discard erases.
            PreservePartialBeforeRestart(partialPath);
            PreservePartialBeforeRestart(metadataPath);
            return new DownloadResumeMetadata
            {
                ItemId = item.Id,
                SourceFingerprint = CreateSourceFingerprint(sourceUri),
                DestinationPath = destinationPath,
                UpdatedUtc = DateTimeOffset.UtcNow
            };
        }

        return metadata!;
    }

    private static DownloadResumeMetadata? LoadResumeMetadata(string metadataPath)
    {
        if (!File.Exists(metadataPath)) return null;
        try
        {
            var json = File.ReadAllText(metadataPath);
            var metadata = JsonSerializer.Deserialize<DownloadResumeMetadata>(json, ResumeJsonOptions);
            if (metadata is null) return null;

            // Migrate sidecars created by older builds. The original address is
            // converted to an irreversible fingerprint and removed immediately.
            if (string.IsNullOrWhiteSpace(metadata.SourceFingerprint))
            {
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.TryGetProperty("SourceUrl", out var legacyValue)
                    && legacyValue.ValueKind == JsonValueKind.String
                    && Uri.TryCreate(legacyValue.GetString(), UriKind.Absolute, out var legacyUri))
                {
                    metadata.SourceFingerprint = CreateSourceFingerprint(legacyUri);
                    TryRewriteSanitizedResumeMetadata(metadataPath, metadata);
                }
            }

            return metadata;
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or JsonException)
        {
            return null;
        }
    }

    private static void TryRewriteSanitizedResumeMetadata(
        string metadataPath,
        DownloadResumeMetadata metadata)
    {
        var temporaryPath = metadataPath + ".sanitize-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(metadata, ResumeJsonOptions));
            File.Move(temporaryPath, metadataPath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or JsonException)
        {
            try { File.Delete(temporaryPath); }
            catch (Exception cleanupException) when (cleanupException is IOException or UnauthorizedAccessException) { }
        }
    }

    private static string CreateSourceFingerprint(Uri sourceUri)
    {
        var bytes = Encoding.UTF8.GetBytes(sourceUri.AbsoluteUri);
        try
        {
            return Convert.ToHexString(SHA256.HashData(bytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static bool TrySaveResumeMetadata(ActiveDownload active)
    {
        try
        {
            lock (active.MetadataGate)
            {
                if (active.PauseRequested)
                    active.Metadata.IsPaused = true;
                active.Metadata.UpdatedUtc = DateTimeOffset.UtcNow;
                var temporaryPath = active.MetadataPath + ".tmp-" + Guid.NewGuid().ToString("N");
                Directory.CreateDirectory(Path.GetDirectoryName(active.MetadataPath)!);
                File.WriteAllText(
                    temporaryPath,
                    JsonSerializer.Serialize(active.Metadata, ResumeJsonOptions));
                File.Move(temporaryPath, active.MetadataPath, overwrite: true);
                return true;
            }
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or JsonException)
        {
            // The transfer may continue; if persistence is unavailable the
            // existing partial file is still never deleted implicitly.
            return false;
        }
    }

    private static void ValidateResumeValidator(
        DownloadResumeMetadata metadata,
        HttpResponseMessage response)
    {
        var responseEtag = response.Headers.ETag?.ToString();
        if (!string.IsNullOrWhiteSpace(metadata.ETag)
            && !string.IsNullOrWhiteSpace(responseEtag)
            && !metadata.ETag.Equals(responseEtag, StringComparison.Ordinal))
            throw new InvalidDataException("O arquivo remoto mudou desde o início do download.");

        var responseLastModified = response.Content.Headers.LastModified?.ToString("R", CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(metadata.ETag)
            && !string.IsNullOrWhiteSpace(metadata.LastModified)
            && !string.IsNullOrWhiteSpace(responseLastModified)
            && !metadata.LastModified.Equals(responseLastModified, StringComparison.Ordinal))
            throw new InvalidDataException("O arquivo remoto foi atualizado durante a pausa.");
    }

    private Uri ValidateSourceUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            throw new UriFormatException("O endereço de download é inválido.");
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Somente downloads HTTPS são permitidos.");
        if (!_options.AllowedHosts.Contains(uri.Host))
            throw new InvalidDataException($"O host de download não está autorizado: {uri.Host}.");
        return uri;
    }

    private static string ResolveSafeExtension(string configuredExtension, Uri sourceUri)
    {
        var extension = configuredExtension;
        if (string.IsNullOrWhiteSpace(extension))
            extension = Path.GetExtension(Uri.UnescapeDataString(sourceUri.AbsolutePath));
        if (string.IsNullOrWhiteSpace(extension)) return ".bin";

        extension = extension.ToLowerInvariant();
        if (extension.Length is < 2 or > 10
            || extension[0] != '.'
            || extension.Skip(1).Any(character => !char.IsAsciiLetterOrDigit(character)))
            throw new InvalidDataException("A extensão do arquivo é inválida.");
        return extension;
    }

    private static string SanitizePathSegment(string value)
    {
        var safe = new string(value
            .Select(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'
                ? character
                : '-')
            .ToArray())
            .Trim('-', '.', ' ');
        if (safe.Length == 0)
            throw new InvalidDataException("O item não possui um identificador de arquivo seguro.");
        return safe.Length <= 96 ? safe : safe[..96];
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

    private static void EnsureNoReparsePoints(string rootPath, string candidatePath)
    {
        var canonicalRoot = Path.GetFullPath(rootPath);
        var canonicalCandidate = Path.GetFullPath(candidatePath);
        if (!IsWithinRoot(canonicalCandidate, canonicalRoot))
            throw new InvalidDataException("O caminho saiu da pasta autorizada.");

        var relative = Path.GetRelativePath(canonicalRoot, canonicalCandidate);
        if (relative == ".") return;
        var current = canonicalRoot;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (FileNotFoundException)
            {
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException(
                    $"O caminho contém um atalho ou junção não autorizado: {current}");
        }
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
            ConnectTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static void DeleteExactFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void PreservePartialBeforeRestart(string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            var preservedPath = path + ".preserved-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
            if (File.Exists(preservedPath))
                preservedPath += "-" + Guid.NewGuid().ToString("N");
            File.Move(path, preservedPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new IOException(
                "O arquivo parcial antigo foi preservado, mas não pôde ser separado para reiniciar com segurança.",
                exception);
        }
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
        if (!TryDeleteFileStrict(saved.PartialPath, out failure)) return false;
        if (!TryDeletePreservedPartialsStrict(saved.PartialPath, out failure)) return false;
        if (saved.Metadata.ArchiveReady
            && !TryDeleteFileStrict(saved.DestinationPath, out failure))
            return false;
        return TryDeleteFileStrict(saved.MetadataPath, out failure);
    }

    private static bool TryDeletePreservedPartialsStrict(string partialPath, out string failure)
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
                if (!TryDeleteFileStrict(candidate, out failure)) return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            failure = exception.Message;
            return false;
        }
    }

    private static bool TryDeleteFileStrict(string path, out string failure)
    {
        failure = string.Empty;
        try
        {
            try
            {
                _ = File.GetAttributes(path);
            }
            catch (FileNotFoundException)
            {
                return true;
            }
            catch (DirectoryNotFoundException)
            {
                return true;
            }

            File.Delete(path);
            try
            {
                _ = File.GetAttributes(path);
                failure = $"O arquivo permaneceu no disco: {path}";
                return false;
            }
            catch (FileNotFoundException)
            {
                return true;
            }
            catch (DirectoryNotFoundException)
            {
                return true;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            failure = $"{path}: {exception.Message}";
            return false;
        }
    }

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
            active.ShutdownRequested = true;
            active.Metadata.IsPaused = false;
            TrySaveResumeMetadata(active);
            try
            {
                active.Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The transfer completed between the lifetime snapshot and cancel.
            }
        }
    }

    private void DisposeOwnedResourcesUnderLock()
    {
        if (_ownedResourcesDisposed) return;
        _ownedResourcesDisposed = true;
        if (_ownsHttpClient) _httpClient.Dispose();
        _downloadQueue.Dispose();
    }

    private void FinishActiveDownload(string itemId, ActiveDownload active)
    {
        _active.TryRemove(itemId, out _);
        active.Dispose();
        lock (_lifetimeGate)
        {
            if (_disposed && _active.IsEmpty) DisposeOwnedResourcesUnderLock();
        }
    }

    private sealed class ActiveDownload : IDisposable
    {
        public ActiveDownload(
            CancellationTokenSource cancellation,
            string metadataPath,
            string partialPath,
            string destinationPath,
            string installationRoot,
            DownloadResumeMetadata metadata)
        {
            Cancellation = cancellation;
            MetadataPath = metadataPath;
            PartialPath = partialPath;
            DestinationPath = destinationPath;
            InstallationRoot = installationRoot;
            Metadata = metadata;
        }

        public CancellationTokenSource Cancellation { get; }
        public string MetadataPath { get; }
        public string PartialPath { get; }
        public string DestinationPath { get; }
        public string InstallationRoot { get; }
        public DownloadResumeMetadata Metadata { get; }
        public object MetadataGate { get; } = new();
        public bool PauseRequested { get; set; }
        public bool ShutdownRequested { get; set; }

        public void Dispose() => Cancellation.Dispose();
    }

    private sealed class DownloadResumeMetadata
    {
        public string ItemId { get; set; } = string.Empty;
        public string SourceFingerprint { get; set; } = string.Empty;
        public string DestinationPath { get; set; } = string.Empty;
        public string ETag { get; set; } = string.Empty;
        public string LastModified { get; set; } = string.Empty;
        public long? TotalBytes { get; set; }
        public bool IsPaused { get; set; }
        public bool ArchiveReady { get; set; }
        public DateTimeOffset UpdatedUtc { get; set; }
    }

    private sealed record ResumeFileSet(
        string MetadataPath,
        string PartialPath,
        string DestinationPath,
        DownloadResumeMetadata Metadata);

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
