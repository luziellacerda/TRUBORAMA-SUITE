using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using TurboBoxManager.Catalog;

internal static class DownloadResumeVerifier
{
    public static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        await VerifyAutomaticRangeResumeAsync(Path.Combine(root, "automatic"));
        await VerifyPauseAndExplicitDiscardAsync(Path.Combine(root, "pause"));
        await VerifyResumeAfterCatalogRenameAsync(Path.Combine(root, "renamed"));
        await VerifyZeroByteRestartIntentAsync(Path.Combine(root, "restart-zero"));
        await VerifyPendingPublishUsesPartialAsync(Path.Combine(root, "pending-publish"));
        await VerifyUnsafe416RestartsAsync(Path.Combine(root, "unsafe-416"));
        await VerifyHeaderTimeoutRetriesAsync(Path.Combine(root, "header-timeout"));
    }

    private static async Task VerifyAutomaticRangeResumeAsync(string root)
    {
        Directory.CreateDirectory(root);
        var bytes = CreatePayload(420_000);
        const int firstInterruptionOffset = 137_777;
        const int secondInterruptionOffset = 278_123;
        using var handler = new InterruptedTransferHandler(
            bytes,
            firstInterruptionOffset,
            secondInterruptionOffset);
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var service = new CatalogDownloadService(client, CreateOptions());
        var item = CreateItem("resume-auto", bytes);

        var result = await service.DownloadAsync(item, root).WaitAsync(TimeSpan.FromSeconds(10));

        Check(result.Succeeded, result.Message);
        Check(handler.RequestCount == 3, "As duas quedas deveriam produzir duas novas tentativas.");
        Check(handler.ObservedRangeStarts.SequenceEqual(
                new long[] { firstInterruptionOffset, secondInterruptionOffset }),
            "As requisições não retomaram exatamente dos bytes salvos.");
        Check(handler.LastObservedIfRange == "\"resume-v1\"",
            "O validador original deveria sobreviver a um 206 que o omitiu.");
        Check(await File.ReadAllBytesAsync(result.LocalFilePath) is var downloaded
              && downloaded.SequenceEqual(bytes),
            "O arquivo retomado ficou diferente do original.");
        Check(!Directory.EnumerateFiles(root, "*.part", SearchOption.AllDirectories).Any(),
            "O parcial deveria virar o arquivo final após a retomada.");
    }

    private static async Task VerifyPauseAndExplicitDiscardAsync(string root)
    {
        Directory.CreateDirectory(root);
        var bytes = CreatePayload(300_000);
        const int pauseOffset = 96_000;
        using var handler = new PausableTransferHandler(bytes, pauseOffset);
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var service = new CatalogDownloadService(client, CreateOptions());
        var item = CreateItem("resume-pause", bytes);

        var running = service.DownloadAsync(item, root);
        await handler.PrefixDelivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var attempt = 0; attempt < 100 && item.BytesReceived < pauseOffset; attempt++)
            await Task.Delay(10);

        Check(item.BytesReceived == pauseOffset, "O primeiro trecho não chegou ao arquivo parcial.");
        Check(service.Pause(item.Id), "O download ativo não aceitou pausa.");
        var paused = await running.WaitAsync(TimeSpan.FromSeconds(5));

        Check(paused.State == CatalogDownloadState.Paused, "Pausar não preservou o estado correto.");
        var partial = Directory.EnumerateFiles(root, "*.part", SearchOption.AllDirectories).Single();
        Check(new FileInfo(partial).Length == pauseOffset, "A pausa perdeu bytes já recebidos.");
        var restored = service.DiscoverResumableDownloads(root).Single();
        Check(restored.IsPaused && restored.BytesReceived == pauseOffset,
            "O download pausado não pôde ser restaurado.");

        await using (var lockedPartial = new FileStream(
                         partial,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.None))
        {
            Check(!service.Discard(item, root),
                "O descarte não pode confirmar sucesso enquanto o parcial continua bloqueado.");
            Check(File.Exists(partial), "Uma falha de descarte não deveria fingir que removeu o parcial.");
        }

        Check(service.Discard(item, root), "O descarte explícito deveria ser aceito após liberar o arquivo.");
        Check(!Directory.EnumerateFiles(root, "*.part*", SearchOption.AllDirectories).Any(),
            "Somente o descarte explícito deveria apagar o parcial e seu registro.");
    }

    private static async Task VerifyZeroByteRestartIntentAsync(string root)
    {
        Directory.CreateDirectory(root);
        var bytes = CreatePayload(32_000);
        using var handler = new OfflineHandler();
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var service = new CatalogDownloadService(client, CreateOptions());
        var item = CreateItem("resume-before-first-byte", bytes);

        var running = service.DownloadAsync(item, root);
        await handler.RequestArrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var attempt = 0;
             attempt < 100 && item.DownloadState != CatalogDownloadState.WaitingForNetwork;
             attempt++)
            await Task.Delay(10);

        Check(item.DownloadState == CatalogDownloadState.WaitingForNetwork,
            "HTTP 503 deveria manter o download aguardando a rede.");
        Check(item.DownloadStatus.Contains("300 s", StringComparison.Ordinal),
            "Retry-After excessivo deveria ser limitado a cinco minutos.");
        service.Dispose();
        var stopped = await running.WaitAsync(TimeSpan.FromSeconds(5));
        Check(stopped.State == CatalogDownloadState.Paused,
            "Fechar o serviço deveria preservar a intenção de retomada.");

        using var restoredService = new CatalogDownloadService(client, CreateOptions());
        var restored = restoredService.DiscoverResumableDownloads(root).Single();
        Check(restored.BytesReceived == 0 && !restored.IsPaused,
            "Um download ativo sem primeiro byte deveria continuar automaticamente ao reabrir.");
        Check(restoredService.Discard(item, root), "O registro restaurado deveria aceitar descarte explícito.");
    }

    private static async Task VerifyResumeAfterCatalogRenameAsync(string root)
    {
        Directory.CreateDirectory(root);
        var bytes = CreatePayload(260_000);
        const int pauseOffset = 80_000;
        using var handler = new PausableTransferHandler(bytes, pauseOffset);
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var service = new CatalogDownloadService(client, CreateOptions());
        var original = CreateItem("stable-id-after-rename", bytes, "Título antigo", "categoria-antiga");

        var firstRun = service.DownloadAsync(original, root);
        await handler.PrefixDelivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var attempt = 0; attempt < 100 && original.BytesReceived < pauseOffset; attempt++)
            await Task.Delay(10);
        Check(service.Pause(original.Id), "A preparação do teste de renomeação não pausou.");
        await firstRun.WaitAsync(TimeSpan.FromSeconds(5));

        var renamed = CreateItem("stable-id-after-rename", bytes, "Título novo", "categoria-nova");
        var resumed = await service.DownloadAsync(renamed, root).WaitAsync(TimeSpan.FromSeconds(5));
        Check(resumed.Succeeded, resumed.Message);
        Check(handler.ObservedRangeStart == pauseOffset,
            "Renomear título/categoria não deveria abandonar o parcial do mesmo ID.");
        Check((await File.ReadAllBytesAsync(resumed.LocalFilePath)).SequenceEqual(bytes),
            "A retomada após renomear o catálogo ficou corrompida.");
    }

    private static async Task VerifyPendingPublishUsesPartialAsync(string root)
    {
        Directory.CreateDirectory(root);
        var bytes = CreatePayload(180_000);
        using var handler = new PublishRetryHandler(bytes);
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var service = new CatalogDownloadService(client, CreateOptions());
        var item = CreateItem("publish-after-locked-destination", bytes);
        var destination = service.BuildSafeDestinationPath(
            root,
            item,
            new Uri(item.DownloadUrl));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await File.WriteAllBytesAsync(destination, [1, 2, 3, 4]);

        CatalogDownloadResult firstResult;
        await using (var lockedDestination = new FileStream(
                         destination,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.None))
        {
            firstResult = await service.DownloadAsync(item, root).WaitAsync(TimeSpan.FromSeconds(5));
        }

        Check(firstResult.State == CatalogDownloadState.Failed,
            "Publicar sobre um destino bloqueado deveria manter o parcial como falha recuperável.");
        Check(Directory.EnumerateFiles(root, "*.part", SearchOption.AllDirectories).Any(),
            "O parcial verificado deveria permanecer após falhar o rename final.");

        var retried = await service.DownloadAsync(item, root).WaitAsync(TimeSpan.FromSeconds(5));
        Check(retried.Succeeded, retried.Message);
        Check(handler.ObservedRangeStart == bytes.Length,
            "O retry de publicação deveria reconhecer o parcial completo.");
        Check((await File.ReadAllBytesAsync(retried.LocalFilePath)).SequenceEqual(bytes),
            "O destino antigo foi confundido com o parcial novo verificado.");
    }

    private static async Task VerifyUnsafe416RestartsAsync(string root)
    {
        Directory.CreateDirectory(root);
        var oldBytes = CreatePayload(120_000);
        var replacement = Enumerable.Repeat((byte)0xE7, 60_000).ToArray();
        using var handler = new ShrinkingTransferHandler(oldBytes, replacement);
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var service = new CatalogDownloadService(client, CreateOptions());
        var item = new CatalogItem
        {
            Id = "unsafe-416",
            CategoryId = "tests",
            Title = "unsafe-416",
            Category = "Testes",
            DownloadUrl = "https://resume.test/package.bin",
            DownloadFileExtension = ".bin"
        };

        var running = service.DownloadAsync(item, root);
        await handler.PrefixDelivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var attempt = 0; attempt < 100 && item.BytesReceived < replacement.Length; attempt++)
            await Task.Delay(10);
        Check(service.Pause(item.Id), "A preparação do cenário 416 não pausou.");
        await running.WaitAsync(TimeSpan.FromSeconds(5));

        var result = await service.DownloadAsync(item, root).WaitAsync(TimeSpan.FromSeconds(5));
        Check(result.Succeeded, result.Message);
        Check(handler.RequestCount == 3 && handler.FinalRequestHadNoRange,
            "Um 416 sem identidade confiável deveria reiniciar com GET completo.");
        Check((await File.ReadAllBytesAsync(result.LocalFilePath)).SequenceEqual(replacement),
            "O parcial antigo foi promovido incorretamente após o recurso remoto encolher.");
    }

    private static async Task VerifyHeaderTimeoutRetriesAsync(string root)
    {
        Directory.CreateDirectory(root);
        var bytes = CreatePayload(20_000);
        using var handler = new HangingHeadersHandler(bytes);
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var service = new CatalogDownloadService(client, new CatalogDownloadOptions
        {
            MaximumFileSizeBytes = 2 * 1024 * 1024,
            InactivityTimeout = TimeSpan.FromMilliseconds(50),
            RetryDelays = [TimeSpan.FromMilliseconds(10)],
            AllowedHosts = new HashSet<string>(["resume.test"], StringComparer.OrdinalIgnoreCase)
        });
        var item = CreateItem("headers-timeout", bytes);

        var result = await service.DownloadAsync(item, root).WaitAsync(TimeSpan.FromSeconds(5));
        Check(result.Succeeded && handler.RequestCount == 2,
            "Um servidor que trava antes dos headers deveria liberar a fila e tentar novamente.");
    }

    private static CatalogDownloadOptions CreateOptions() => new()
    {
        MaximumFileSizeBytes = 2 * 1024 * 1024,
        InactivityTimeout = TimeSpan.FromSeconds(2),
        RetryDelays = [TimeSpan.FromMilliseconds(10)],
        AllowedHosts = new HashSet<string>(["resume.test"], StringComparer.OrdinalIgnoreCase)
    };

    private static CatalogItem CreateItem(
        string id,
        byte[] bytes,
        string? title = null,
        string categoryId = "tests") => new()
    {
        Id = id,
        CategoryId = categoryId,
        Title = title ?? id,
        Category = "Testes",
        DownloadUrl = "https://resume.test/package.bin",
        DownloadFileExtension = ".bin",
        Sha256 = Convert.ToHexString(SHA256.HashData(bytes))
    };

    private static byte[] CreatePayload(int length)
    {
        var bytes = new byte[length];
        for (var index = 0; index < bytes.Length; index++)
            bytes[index] = (byte)((index * 31 + 17) % 251);
        return bytes;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class InterruptedTransferHandler(
        byte[] payload,
        int firstInterruptionOffset,
        int secondInterruptionOffset)
        : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public List<long> ObservedRangeStarts { get; } = [];
        public string LastObservedIfRange { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            if (RequestCount == 1)
            {
                Check(request.Headers.Range is null, "A primeira requisição não deveria conter Range.");
                return Task.FromResult(CreateResponse(
                    HttpStatusCode.OK,
                    new InterruptingReadStream(payload, firstInterruptionOffset),
                    0,
                    includeValidators: true));
            }

            var rangeStart = request.Headers.Range?.Ranges.Single().From
                             ?? throw new InvalidOperationException("A retomada não enviou Range.");
            ObservedRangeStarts.Add(rangeStart);
            LastObservedIfRange = request.Headers.TryGetValues("If-Range", out var values)
                ? values.Single()
                : string.Empty;

            if (RequestCount == 2)
            {
                var remaining = payload[(int)rangeStart..];
                return Task.FromResult(CreateResponse(
                    HttpStatusCode.PartialContent,
                    new InterruptingReadStream(
                        remaining,
                        secondInterruptionOffset - (int)rangeStart),
                    rangeStart,
                    includeValidators: false));
            }

            return Task.FromResult(CreateResponse(
                HttpStatusCode.PartialContent,
                new MemoryStream(payload, (int)rangeStart,
                    payload.Length - (int)rangeStart, writable: false),
                rangeStart,
                includeValidators: true));
        }

        private HttpResponseMessage CreateResponse(
            HttpStatusCode status,
            Stream stream,
            long offset,
            bool includeValidators)
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StreamContent(stream)
            };
            if (includeValidators)
            {
                response.Headers.ETag = new EntityTagHeaderValue("\"resume-v1\"");
                response.Content.Headers.LastModified =
                    new DateTimeOffset(2026, 8, 21, 0, 0, 0, TimeSpan.Zero);
            }
            response.Content.Headers.ContentLength = payload.Length - offset;
            if (status == HttpStatusCode.PartialContent)
                response.Content.Headers.ContentRange =
                    new ContentRangeHeaderValue(offset, payload.Length - 1, payload.Length);
            return response;
        }
    }

    private sealed class OfflineHandler : HttpMessageHandler
    {
        public TaskCompletionSource RequestArrived { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestArrived.TrySetResult();
            var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            response.Headers.RetryAfter =
                new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddYears(1));
            response.Content = new ByteArrayContent([]);
            return Task.FromResult(response);
        }
    }

    private sealed class PublishRetryHandler(byte[] payload) : HttpMessageHandler
    {
        private int _requestCount;
        public long? ObservedRangeStart { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _requestCount++;
            if (_requestCount == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(payload)
                };
                response.Headers.ETag = new EntityTagHeaderValue("\"publish-v1\"");
                response.Content.Headers.ContentLength = payload.Length;
                return Task.FromResult(response);
            }

            ObservedRangeStart = request.Headers.Range?.Ranges.Single().From;
            var unsatisfied = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                Content = new ByteArrayContent([])
            };
            unsatisfied.Headers.ETag = new EntityTagHeaderValue("\"publish-v1\"");
            unsatisfied.Content.Headers.ContentRange = new ContentRangeHeaderValue(payload.Length);
            return Task.FromResult(unsatisfied);
        }
    }

    private sealed class ShrinkingTransferHandler(byte[] oldPayload, byte[] replacement)
        : HttpMessageHandler
    {
        public TaskCompletionSource PrefixDelivered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int RequestCount { get; private set; }
        public bool FinalRequestHadNoRange { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            if (RequestCount == 1)
            {
                var first = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(
                        new PausingReadStream(oldPayload, replacement.Length, PrefixDelivered))
                };
                first.Content.Headers.ContentLength = oldPayload.Length;
                return Task.FromResult(first);
            }

            if (RequestCount == 2)
            {
                Check(request.Headers.Range?.Ranges.Single().From == replacement.Length,
                    "O cenário 416 deveria começar no tamanho do parcial.");
                var unsatisfied = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    Content = new ByteArrayContent([])
                };
                unsatisfied.Content.Headers.ContentRange =
                    new ContentRangeHeaderValue(replacement.Length);
                return Task.FromResult(unsatisfied);
            }

            FinalRequestHadNoRange = request.Headers.Range is null;
            var final = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(replacement)
            };
            final.Content.Headers.ContentLength = replacement.Length;
            return Task.FromResult(final);
        }
    }

    private sealed class HangingHeadersHandler(byte[] payload) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            if (RequestCount == 1)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            };
            response.Headers.ETag = new EntityTagHeaderValue("\"headers-v1\"");
            response.Content.Headers.ContentLength = payload.Length;
            return response;
        }
    }

    private sealed class PausableTransferHandler(byte[] payload, int pauseOffset) : HttpMessageHandler
    {
        public TaskCompletionSource PrefixDelivered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long? ObservedRangeStart { get; private set; }
        private int _requestCount;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _requestCount++;
            if (_requestCount > 1)
            {
                ObservedRangeStart = request.Headers.Range?.Ranges.Single().From;
                var rangeStart = ObservedRangeStart
                                 ?? throw new InvalidOperationException("A continuação deveria enviar Range.");
                var resumedResponse = new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent(payload[(int)rangeStart..])
                };
                resumedResponse.Headers.ETag = new EntityTagHeaderValue("\"pause-v1\"");
                resumedResponse.Content.Headers.ContentLength = payload.Length - rangeStart;
                resumedResponse.Content.Headers.ContentRange =
                    new ContentRangeHeaderValue(rangeStart, payload.Length - 1, payload.Length);
                return Task.FromResult(resumedResponse);
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(
                    new PausingReadStream(payload, pauseOffset, PrefixDelivered))
            };
            response.Headers.ETag = new EntityTagHeaderValue("\"pause-v1\"");
            response.Content.Headers.ContentLength = payload.Length;
            return Task.FromResult(response);
        }
    }

    private sealed class InterruptingReadStream(byte[] payload, int failAfter) : Stream
    {
        private int _position;
        private bool _failed;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => payload.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadCore(buffer.AsSpan(offset, count));
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return ValueTask.FromResult(ReadCore(buffer.Span)); }
            catch (Exception exception) { return ValueTask.FromException<int>(exception); }
        }
        private int ReadCore(Span<byte> buffer)
        {
            if (_position >= failAfter && !_failed)
            {
                _failed = true;
                throw new IOException("Queda simulada da conexão.");
            }
            if (_position >= payload.Length) return 0;
            var count = Math.Min(buffer.Length, Math.Min(payload.Length, failAfter) - _position);
            payload.AsSpan(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class PausingReadStream(
        byte[] payload,
        int pauseAfter,
        TaskCompletionSource prefixDelivered) : Stream
    {
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => payload.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_position == 0)
            {
                var count = Math.Min(buffer.Length, pauseAfter);
                payload.AsMemory(0, count).CopyTo(buffer);
                _position = count;
                prefixDelivered.TrySetResult();
                return count;
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
