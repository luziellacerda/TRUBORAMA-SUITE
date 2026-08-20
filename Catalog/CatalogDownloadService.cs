using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;

namespace TurboBoxManager.Catalog;

public sealed class CatalogDownloadOptions
{
    public long MaximumFileSizeBytes { get; init; } = 256L * 1024L * 1024L;
    public int MaximumRedirects { get; init; } = 5;
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
    public bool WasCanceled => State == CatalogDownloadState.Canceled;
}

/// <summary>
/// Small, intentionally conservative downloader for authorized Turborama test
/// assets. It does not extract or execute content. One transfer runs at a time;
/// additional items wait in a cancellable queue.
/// </summary>
public sealed class CatalogDownloadService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly CatalogDownloadOptions _options;
    private readonly bool _ownsHttpClient;
    private readonly SemaphoreSlim _downloadQueue = new(1, 1);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _active =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

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
    }

    public bool IsActive(string itemId) => _active.ContainsKey(itemId);

    public bool Cancel(string itemId)
    {
        if (!_active.TryGetValue(itemId, out var cancellation)) return false;
        cancellation.Cancel();
        return true;
    }

    public async Task<CatalogDownloadResult> DownloadAsync(
        CatalogItem item,
        string installationRoot,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(item);

        if (string.IsNullOrWhiteSpace(item.DownloadUrl))
        {
            const string message = "Nenhum endereço autorizado foi configurado. Nada foi baixado.";
            item.SetDownloadState(CatalogDownloadState.Failed, message);
            return new CatalogDownloadResult(CatalogDownloadState.Failed, message);
        }

        Uri sourceUri;
        try
        {
            sourceUri = ValidateSourceUri(item.DownloadUrl);
        }
        catch (Exception exception) when (exception is InvalidDataException or UriFormatException)
        {
            item.SetDownloadState(CatalogDownloadState.Failed, exception.Message);
            return new CatalogDownloadResult(CatalogDownloadState.Failed, exception.Message);
        }

        if (!Directory.Exists(installationRoot))
        {
            const string message = "A pasta de instalação não existe. Escolha uma pasta válida.";
            item.SetDownloadState(CatalogDownloadState.Failed, message);
            return new CatalogDownloadResult(CatalogDownloadState.Failed, message);
        }

        var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!_active.TryAdd(item.Id, linkedCancellation))
        {
            const string message = "Este item já está na fila ou em download.";
            return new CatalogDownloadResult(item.DownloadState, message, item.LocalFilePath);
        }

        var queueEntered = false;
        string? partialPath = null;
        try
        {
            item.SetDownloadState(CatalogDownloadState.Queued, "Aguardando na fila");
            await _downloadQueue.WaitAsync(linkedCancellation.Token);
            queueEntered = true;

            var destinationPath = BuildSafeDestinationPath(installationRoot, item, sourceUri);
            partialPath = destinationPath + ".part";
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            DeletePartialFile(partialPath);

            item.SetDownloadState(CatalogDownloadState.Downloading, "Iniciando download seguro");
            using var response = await SendWithSafeRedirectsAsync(sourceUri, linkedCancellation.Token);
            response.EnsureSuccessStatusCode();

            var declaredLength = response.Content.Headers.ContentLength;
            if (declaredLength > _options.MaximumFileSizeBytes)
                throw new InvalidDataException("O arquivo excede o limite seguro configurado.");

            await using var input = await response.Content.ReadAsStreamAsync(linkedCancellation.Token);
            await using var output = new FileStream(
                partialPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            var buffer = new byte[64 * 1024];
            long totalRead = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer, linkedCancellation.Token);
                if (read == 0) break;

                totalRead += read;
                if (totalRead > _options.MaximumFileSizeBytes)
                    throw new InvalidDataException("O arquivo excedeu o limite seguro durante a transferência.");

                await output.WriteAsync(buffer.AsMemory(0, read), linkedCancellation.Token);
                hash.AppendData(buffer, 0, read);
                item.UpdateDownloadProgress(totalRead, declaredLength);
            }

            await output.FlushAsync(linkedCancellation.Token);
            if (declaredLength.HasValue && totalRead != declaredLength.Value)
                throw new InvalidDataException("O tamanho recebido não corresponde ao informado pelo servidor.");

            var actualHash = Convert.ToHexString(hash.GetHashAndReset());
            if (!string.IsNullOrWhiteSpace(item.Sha256)
                && !actualHash.Equals(item.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A verificação SHA-256 falhou. O arquivo não foi instalado.");

            // The final rename must happen after releasing the exclusive handle
            // opened for the .part file (not only when leaving the try scope).
            await output.DisposeAsync();
            File.Move(partialPath, destinationPath, overwrite: true);
            partialPath = null;
            item.CompleteDownload(destinationPath);
            return new CatalogDownloadResult(
                CatalogDownloadState.Completed,
                $"Download concluído e verificado: {Path.GetFileName(destinationPath)}",
                destinationPath);
        }
        catch (OperationCanceledException)
        {
            item.SetDownloadState(CatalogDownloadState.Canceled, "Download cancelado");
            return new CatalogDownloadResult(CatalogDownloadState.Canceled, "Download cancelado. Nenhum parcial foi mantido.");
        }
        catch (Exception exception) when (exception is HttpRequestException
                                           or IOException
                                           or InvalidDataException
                                           or UnauthorizedAccessException)
        {
            item.SetDownloadState(CatalogDownloadState.Failed, exception.Message);
            return new CatalogDownloadResult(CatalogDownloadState.Failed, $"Falha no download: {exception.Message}");
        }
        finally
        {
            if (partialPath is not null) DeletePartialFile(partialPath);
            if (queueEntered) _downloadQueue.Release();
            _active.TryRemove(item.Id, out _);
            linkedCancellation.Dispose();
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
        var idSuffix = idSegment.Length <= 8 ? idSegment : idSegment[..8];
        var itemSegment = $"{titleSegment}-{idSuffix}";
        var extension = ResolveSafeExtension(item.DownloadFileExtension, sourceUri);
        var destination = Path.GetFullPath(Path.Combine(
            canonicalRoot,
            "Turborama",
            "Downloads",
            categorySegment,
            itemSegment + extension));

        if (!IsWithinRoot(destination, canonicalRoot))
            throw new InvalidDataException("O destino calculado saiu da pasta autorizada.");
        return destination;
    }

    private async Task<HttpResponseMessage> SendWithSafeRedirectsAsync(
        Uri sourceUri,
        CancellationToken cancellationToken)
    {
        var currentUri = sourceUri;
        for (var redirect = 0; redirect <= _options.MaximumRedirects; redirect++)
        {
            ValidateSourceUri(currentUri.AbsoluteUri);
            using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
            request.Headers.UserAgent.ParseAdd("Turborama/2.0-safe-test");
            var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

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
                throw new HttpRequestException("O servidor retornou um redirecionamento sem destino.");
            currentUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
        }

        throw new HttpRequestException("O download excedeu o limite de redirecionamentos.");
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
        if (safe.Length == 0) throw new InvalidDataException("O item não possui um identificador de arquivo seguro.");
        return safe.Length <= 96 ? safe : safe[..96];
    }

    private static bool IsWithinRoot(string candidatePath, string rootPath)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath))
                             + Path.DirectorySeparatorChar;
        return Path.GetFullPath(candidatePath)
            .StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRedirect(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.MovedPermanently
        or HttpStatusCode.Found
        or HttpStatusCode.SeeOther
        or HttpStatusCode.TemporaryRedirect
        or HttpStatusCode.PermanentRedirect;

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
    }

    private static void DeletePartialFile(string partialPath)
    {
        try
        {
            if (File.Exists(partialPath)) File.Delete(partialPath);
        }
        catch (IOException)
        {
            // A failed cleanup is reported by the next FileMode.CreateNew call;
            // never broaden deletion beyond this exact .part path.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var cancellation in _active.Values) cancellation.Cancel();
        if (_ownsHttpClient) _httpClient.Dispose();
        if (_active.IsEmpty) _downloadQueue.Dispose();
    }
}
