using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using TurboBoxManager.Catalog;

internal static class DownloadResumeVerifier
{
    private static readonly TimeSpan ScenarioTimeout = TimeSpan.FromSeconds(15);

    public static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        VerifyPublicTransportCannotBeInjected();
        await VerifyDescriptorFailsClosedAsync(Path.Combine(root, "descriptor"));
        VerifyDiscoveryHonorsCancellation(Path.Combine(root, "discovery-cancellation"));
        await VerifyNonCanonicalSidecarIsRejectedAsync(Path.Combine(root, "sidecar-casing"));
        await VerifyUnauthorizedSidecarCannotAutoResumeAsync(
            Path.Combine(root, "sidecar-authorization"));
        await VerifySidecarCannotChooseDestinationAsync(Path.Combine(root, "sidecar-path"));
        await VerifyRotatingGrantResumesWithoutPersistenceAsync(Path.Combine(root, "rotating-grant"));
        await VerifyReadySidecarIsRehashedAsync(Path.Combine(root, "ready-rehash"));
        await VerifyRedirectIsDeniedAsync(Path.Combine(root, "redirect"));
        await VerifyInvalidRangeIsDeniedBeforeNetworkAsync(Path.Combine(root, "invalid-range"));
        await VerifyHardLinkedPartialCannotModifyTargetAsync(Path.Combine(root, "hardlink-partial"));
        await VerifyExactResponseLengthIsRequiredAsync(Path.Combine(root, "response-length"));
    }

    private static async Task VerifyUnauthorizedSidecarCannotAutoResumeAsync(string root)
    {
        Directory.CreateDirectory(root);
        var bytes = CreatePayload(160_000);
        const int pauseOffset = 48_000;
        var item = CreateItem("sidecar-authorization", bytes);

        using (var handler = new PausingHandler(bytes, pauseOffset))
        using (var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan })
        using (var service = new CatalogDownloadService(
                   client,
                   new RotatingGrantProvider(),
            CreateOptions()))
        {
            var running = service.DownloadAsync(item, root);
            await WaitForPrefixOrEarlyCompletionAsync(
                handler.PrefixDelivered.Task,
                running,
                "sidecar-authorization");
            for (var attempt = 0; attempt < 100 && item.BytesReceived < pauseOffset; attempt++)
                await Task.Delay(10);
            Check(service.Pause(item.Id), "O sidecar para teste de autorização não pôde ser preparado.");
            await running.WaitAsync(ScenarioTimeout);
        }

        var sidecar = Directory.EnumerateFiles(
                root,
                "*.part.resume.json",
                SearchOption.AllDirectories)
            .Single();
        var canonical = await File.ReadAllTextAsync(sidecar);
        var forgedManifestIdentity = new string(
            item.Artifact!.ManifestIdentity[0] == 'a' ? 'b' : 'a',
            64);
        var forged = canonical.Replace(
            item.Artifact.ManifestIdentity,
            forgedManifestIdentity,
            StringComparison.Ordinal);
        Check(!canonical.Equals(forged, StringComparison.Ordinal),
            "O teste não conseguiu trocar a identidade do manifest no sidecar.");
        await File.WriteAllTextAsync(sidecar, forged);

        using var verifier = new CatalogDownloadService(CreateOptions());
        Check(verifier.DiscoverResumableDownloads(root, [item]).Count == 0,
            "Sidecar sem correspondência integral ao descritor não pode disparar retomada autorizada.");
        Check(verifier.DiscoverResumableDownloads(root).Count == 1,
            "O parcial não autorizado deve ser preservado para diagnóstico/descarte explícito.");
    }

    private static void VerifyDiscoveryHonorsCancellation(string root)
    {
        Directory.CreateDirectory(root);
        using var service = new CatalogDownloadService(CreateOptions());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var canceled = false;
        try
        {
            _ = service.DiscoverResumableDownloads(root, [], cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            canceled = true;
        }

        Check(canceled,
            "A descoberta de downloads deve ser cancelável quando a autorização termina.");
    }

    private static void VerifyPublicTransportCannotBeInjected()
    {
        var publicHttpClientConstructor = typeof(CatalogDownloadService)
            .GetConstructors()
            .Any(constructor => constructor.GetParameters()
                .Any(parameter => parameter.ParameterType == typeof(HttpClient)));
        Check(!publicHttpClientConstructor,
            "O transporte HTTP público não pode aceitar um cliente que siga redirects autenticados.");
    }

    private static async Task VerifyDescriptorFailsClosedAsync(string root)
    {
        Directory.CreateDirectory(root);
        using var service = new CatalogDownloadService(CreateOptions());

        var missing = new CatalogItem { Id = "missing", CategoryId = "tests" };
        var missingResult = await service.DownloadAsync(missing, root);
        Check(missingResult.State == CatalogDownloadState.Failed,
            "Um item sem descritor não pode iniciar download.");

        var bytes = CreatePayload(128);
        var invalidHash = WithArtifact(
            CreateItem("invalid-hash", bytes),
            artifact => artifact with { Sha256 = string.Empty });
        var hashResult = await service.DownloadAsync(invalidHash, root);
        Check(hashResult.State == CatalogDownloadState.Failed,
            "SHA-256 ausente deve falhar antes de criar requisição.");

        var invalidLength = WithArtifact(
            CreateItem("invalid-length", bytes),
            artifact => artifact with { ContentLength = 0 });
        var lengthResult = await service.DownloadAsync(invalidLength, root);
        Check(lengthResult.State == CatalogDownloadState.Failed,
            "Tamanho exato ausente deve falhar antes de criar requisição.");

        var invalidId = WithArtifact(
            CreateItem("invalid-id", bytes),
            artifact => artifact with { ArtifactId = new string('A', 32) });
        Check((await service.DownloadAsync(invalidId, root)).State == CatalogDownloadState.Failed,
            "ArtifactId deve ser exatamente 32 hex minúsculos.");

        var invalidManifest = WithArtifact(
            CreateItem("invalid-manifest", bytes),
            artifact => artifact with { ManifestIdentity = new string('F', 64) });
        Check((await service.DownloadAsync(invalidManifest, root)).State == CatalogDownloadState.Failed,
            "manifestSha256 deve ser exatamente 64 hex minúsculos.");

        var invalidFileName = WithArtifact(
            CreateItem("invalid-file-name", bytes),
            artifact => artifact with { SafeFileName = "package.zip" });
        Check((await service.DownloadAsync(invalidFileName, root)).State == CatalogDownloadState.Failed,
            "SafeFileName deve terminar na FileExtension autorizada.");

        var aboveConfiguredMaximum = WithArtifact(
            CreateItem("above-maximum", bytes),
            artifact => artifact with { ContentLength = 2L * 1024 * 1024 + 1 });
        Check((await service.DownloadAsync(aboveConfiguredMaximum, root)).State == CatalogDownloadState.Failed,
            "ContentLength acima de MaximumFileSizeBytes deve falhar antes da rede.");

    }

    private static async Task VerifyNonCanonicalSidecarIsRejectedAsync(string root)
    {
        // Exercise the native handle-validation path beyond legacy MAX_PATH even
        // when the verifier itself runs under a short TEMP directory.
        root = Path.Combine(root, new string('d', 96));
        Directory.CreateDirectory(root);
        var bytes = CreatePayload(160_000);
        const int pauseOffset = 48_000;
        var item = CreateItem("sidecar-casing", bytes);

        using (var handler = new PausingHandler(bytes, pauseOffset))
        using (var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan })
        using (var service = new CatalogDownloadService(
                   client,
                   new RotatingGrantProvider(),
                   CreateOptions()))
        {
            var running = service.DownloadAsync(item, root);
            await WaitForPrefixOrEarlyCompletionAsync(
                handler.PrefixDelivered.Task,
                running,
                "sidecar-casing");
            for (var attempt = 0; attempt < 100 && item.BytesReceived < pauseOffset; attempt++)
                await Task.Delay(10);
            Check(service.Pause(item.Id), "O sidecar canônico não pôde ser preparado.");
            await running.WaitAsync(ScenarioTimeout);
        }

        var sidecar = Directory.EnumerateFiles(root, "*.resume.json", SearchOption.AllDirectories).Single();
        var canonical = await File.ReadAllTextAsync(sidecar);
        var nonCanonical = canonical.Replace(
            item.Artifact!.ArtifactId,
            item.Artifact.ArtifactId.ToUpperInvariant(),
            StringComparison.Ordinal);
        Check(!canonical.Equals(nonCanonical, StringComparison.Ordinal),
            "O teste não conseguiu adulterar o casing do ArtifactId.");
        await File.WriteAllTextAsync(sidecar, nonCanonical);

        using var verifier = new CatalogDownloadService(CreateOptions());
        Check(verifier.DiscoverResumableDownloads(root, [item]).Count == 0,
            "Sidecar com ArtifactId fora do casing canônico deve ser rejeitado integralmente.");
        Check(Directory.EnumerateFiles(root, "*.part", SearchOption.AllDirectories).Any(),
            "Rejeitar sidecar adulterado não pode apagar o parcial.");
    }

    private static async Task VerifyRotatingGrantResumesWithoutPersistenceAsync(string root)
    {
        Directory.CreateDirectory(root);
        var bytes = CreatePayload(420_000);
        const int interruptionOffset = 137_777;
        using var handler = new InterruptedTransferHandler(bytes, interruptionOffset);
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var provider = new RotatingGrantProvider();
        using var service = new CatalogDownloadService(client, provider, CreateOptions());
        var item = CreateItem("rotating-resume", bytes, extract: true);

        var result = await service.DownloadAsync(item, root).WaitAsync(ScenarioTimeout);

        Check(result.Succeeded, result.Message);
        Check(provider.Offsets.SequenceEqual(new long[] { 0, interruptionOffset }),
            "Cada tentativa deve receber o offset realmente persistido.");
        Check(provider.RequestPaths.Distinct(StringComparer.Ordinal).Count() == 2,
            "A retomada deve aceitar uma concessão com destino efêmero rotacionado.");
        Check(provider.AuthorizationValues.Distinct(StringComparer.Ordinal).Count() == 2,
            "Cada tentativa deve usar uma autorização nova.");
        Check(provider.ObservedValidators[1].ETag == "\"artifact-v1\"",
            "O provedor deve receber o validador capturado na tentativa anterior.");
        Check((await File.ReadAllBytesAsync(result.LocalFilePath)).SequenceEqual(bytes),
            "O arquivo retomado não corresponde ao artefato autorizado.");

        var sidecar = Directory.EnumerateFiles(root, "*.resume.json", SearchOption.AllDirectories).Single();
        var durableText = await File.ReadAllTextAsync(sidecar);
        Check(!durableText.Contains("https://", StringComparison.OrdinalIgnoreCase)
              && !durableText.Contains("Authorization", StringComparison.OrdinalIgnoreCase)
              && !provider.AuthorizationValues.Any(value =>
                  durableText.Contains(value, StringComparison.Ordinal)),
            "URL, grant ou Authorization não podem ser persistidos no sidecar.");
        Check(durableText.Contains("TURBORAMA_SUITE", StringComparison.Ordinal)
              && durableText.Contains(item.Artifact!.ArtifactId, StringComparison.Ordinal)
              && durableText.Contains(item.Artifact.Sha256, StringComparison.OrdinalIgnoreCase),
            "O sidecar deve conter somente a identidade estável do artefato.");
    }

    private static async Task VerifySidecarCannotChooseDestinationAsync(string root)
    {
        Directory.CreateDirectory(root);
        var bytes = CreatePayload(160_000);
        const int pauseOffset = 48_000;
        var item = CreateItem("sidecar-path", bytes);

        using (var handler = new PausingHandler(bytes, pauseOffset))
        using (var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan })
        using (var service = new CatalogDownloadService(
                   client,
                   new RotatingGrantProvider(),
                   CreateOptions()))
        {
            var running = service.DownloadAsync(item, root);
            await WaitForPrefixOrEarlyCompletionAsync(
                handler.PrefixDelivered.Task,
                running,
                "sidecar-path");
            for (var attempt = 0; attempt < 100 && item.BytesReceived < pauseOffset; attempt++)
                await Task.Delay(10);
            Check(service.Pause(item.Id), "O sidecar para teste de caminho não pôde ser preparado.");
            await running.WaitAsync(ScenarioTimeout);
        }

        var canonicalSidecar = Directory
            .EnumerateFiles(root, "*.part.resume.json", SearchOption.AllDirectories)
            .Single();
        var canonicalPartial = canonicalSidecar[..^".resume.json".Length];
        var unrelatedDestination = Path.Combine(root, "unrelated.bin");
        var misplacedPartial = unrelatedDestination + ".part";
        var misplacedSidecar = misplacedPartial + ".resume.json";
        File.Move(canonicalPartial, misplacedPartial);
        File.Move(canonicalSidecar, misplacedSidecar);
        var sentinel = Encoding.UTF8.GetBytes("do-not-delete-or-overwrite");
        await File.WriteAllBytesAsync(unrelatedDestination, sentinel);

        using var verifier = new CatalogDownloadService(CreateOptions());
        Check(verifier.Discard(item, root),
            "Descartar o artefato canônico deveria continuar sendo uma operação válida.");
        Check((await File.ReadAllBytesAsync(unrelatedDestination)).SequenceEqual(sentinel)
              && File.Exists(misplacedPartial)
              && File.Exists(misplacedSidecar),
            "Um sidecar fora do caminho canônico não pode escolher arquivos para descarte.");
    }

    private static async Task VerifyReadySidecarIsRehashedAsync(string root)
    {
        Directory.CreateDirectory(root);
        var bytes = CreatePayload(96_000);
        var item = CreateItem("ready-rehash", bytes, extract: true);

        string completedPath;
        using (var firstHandler = new PayloadHandler(bytes))
        using (var firstClient = new HttpClient(firstHandler) { Timeout = Timeout.InfiniteTimeSpan })
        using (var firstService = new CatalogDownloadService(
                   firstClient,
                   new RotatingGrantProvider(),
                   CreateOptions()))
        {
            var first = await firstService.DownloadAsync(item, root);
            Check(first.Succeeded, first.Message);
            completedPath = first.LocalFilePath;
        }

        await File.WriteAllBytesAsync(completedPath, Enumerable.Repeat((byte)0xA5, bytes.Length).ToArray());

        using var handler = new PayloadHandler(bytes);
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var service = new CatalogDownloadService(
            client,
            new RotatingGrantProvider(),
            CreateOptions());

        var untrustedDiscovery = service.DiscoverResumableDownloads(root, [item]).Single();
        Check(!untrustedDiscovery.ArchiveReady,
            "ArchiveReady adulterado não pode ser aceito sem recalcular tamanho e SHA.");

        var recovered = await service.DownloadAsync(item, root).WaitAsync(ScenarioTimeout);
        Check(recovered.Succeeded && handler.RequestCount == 1,
            "O arquivo final adulterado deve ser rejeitado e baixado novamente.");
        Check((await File.ReadAllBytesAsync(recovered.LocalFilePath)).SequenceEqual(bytes),
            "A recuperação não restaurou o artefato autorizado.");
        Check(Directory.EnumerateFiles(root, "*.preserved-*", SearchOption.AllDirectories).Any(),
            "O arquivo adulterado deve ser preservado, não promovido nem apagado silenciosamente.");
    }

    private static async Task VerifyRedirectIsDeniedAsync(string root)
    {
        Directory.CreateDirectory(root);
        var bytes = CreatePayload(256);
        using var handler = new RedirectHandler();
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var service = new CatalogDownloadService(
            client,
            new RotatingGrantProvider(),
            CreateOptions());

        var result = await service.DownloadAsync(CreateItem("redirect-denied", bytes), root);
        Check(result.State == CatalogDownloadState.Failed && handler.RequestCount == 1,
            "Redirect autenticado deve falhar sem seguir o Location.");
    }

    private static async Task VerifyInvalidRangeIsDeniedBeforeNetworkAsync(string root)
    {
        Directory.CreateDirectory(root);
        var bytes = CreatePayload(200_000);
        const int pauseOffset = 64_000;
        var item = CreateItem("invalid-range", bytes);

        using (var pausingHandler = new PausingHandler(bytes, pauseOffset))
        using (var pausingClient = new HttpClient(pausingHandler) { Timeout = Timeout.InfiniteTimeSpan })
        using (var pausingService = new CatalogDownloadService(
                   pausingClient,
                   new RotatingGrantProvider(),
                   CreateOptions()))
        {
            var running = pausingService.DownloadAsync(item, root);
            await WaitForPrefixOrEarlyCompletionAsync(
                pausingHandler.PrefixDelivered.Task,
                running,
                "invalid-range");
            for (var attempt = 0; attempt < 100 && item.BytesReceived < pauseOffset; attempt++)
                await Task.Delay(10);
            Check(pausingService.Pause(item.Id), "O cenário de Range inválido não conseguiu pausar.");
            var paused = await running.WaitAsync(ScenarioTimeout);
            Check(paused.State == CatalogDownloadState.Paused,
                "A pausa deve preservar o parcial usado no teste de Range.");
        }

        using var countingHandler = new CountingHandler();
        using var client = new HttpClient(countingHandler) { Timeout = Timeout.InfiniteTimeSpan };
        using var service = new CatalogDownloadService(
            client,
            new InvalidRangeProvider(),
            CreateOptions());
        var result = await service.DownloadAsync(item, root).WaitAsync(ScenarioTimeout);

        Check(result.State == CatalogDownloadState.Failed && countingHandler.RequestCount == 0,
            "Range diferente do offset local deve ser recusado antes de chegar à rede.");
        Check(Directory.EnumerateFiles(root, "*.part", SearchOption.AllDirectories).Single()
                  is var partial
              && new FileInfo(partial).Length == pauseOffset,
            "Uma requisição inválida não pode apagar o parcial.");
    }

    private static async Task VerifyExactResponseLengthIsRequiredAsync(string root)
    {
        Directory.CreateDirectory(root);
        var bytes = CreatePayload(4_096);
        using var handler = new WrongLengthHandler(bytes);
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var service = new CatalogDownloadService(
            client,
            new RotatingGrantProvider(),
            CreateOptions());

        var result = await service.DownloadAsync(CreateItem("wrong-length", bytes), root);
        Check(result.State == CatalogDownloadState.Failed,
            "Content-Length divergente deve falhar antes de promover o arquivo.");
    }

    private static async Task VerifyHardLinkedPartialCannotModifyTargetAsync(string root)
    {
        Directory.CreateDirectory(root);
        var bytes = CreatePayload(180_000);
        const int pauseOffset = 52_000;
        var item = CreateItem("hardlink-partial", bytes);

        using (var pausingHandler = new PausingHandler(bytes, pauseOffset))
        using (var pausingClient = new HttpClient(pausingHandler) { Timeout = Timeout.InfiniteTimeSpan })
        using (var pausingService = new CatalogDownloadService(
                   pausingClient,
                   new RotatingGrantProvider(),
                   CreateOptions()))
        {
            var running = pausingService.DownloadAsync(item, root);
            await WaitForPrefixOrEarlyCompletionAsync(
                pausingHandler.PrefixDelivered.Task,
                running,
                "hardlink-partial");
            for (var attempt = 0; attempt < 100 && item.BytesReceived < pauseOffset; attempt++)
                await Task.Delay(10);
            Check(pausingService.Pause(item.Id),
                "O parcial para o teste de hardlink não pôde ser preparado.");
            var paused = await running.WaitAsync(ScenarioTimeout);
            Check(paused.State == CatalogDownloadState.Paused,
                "O teste de hardlink exige um parcial persistido.");
        }

        var partialPath = Directory.EnumerateFiles(root, "*.part", SearchOption.AllDirectories).Single();
        var sentinelPath = Path.Combine(root, "hardlink-target.bin");
        var sentinel = Enumerable.Repeat((byte)0xA7, pauseOffset).ToArray();
        await File.WriteAllBytesAsync(sentinelPath, sentinel);
        File.Delete(partialPath);
        Check(CreateHardLink(partialPath, sentinelPath, IntPtr.Zero),
            $"O teste não conseguiu criar o hardlink hostil (Win32 {Marshal.GetLastWin32Error()}).");

        using var handler = new PayloadHandler(bytes);
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var service = new CatalogDownloadService(
            client,
            new RotatingGrantProvider(),
            CreateOptions());
        var result = await service.DownloadAsync(item, root).WaitAsync(ScenarioTimeout);

        Check(result.State == CatalogDownloadState.Failed && handler.RequestCount == 0,
            "Um parcial com mais de um hardlink deve falhar antes da rede e de qualquer escrita.");
        Check((await File.ReadAllBytesAsync(sentinelPath)).SequenceEqual(sentinel),
            "O downloader alterou o alvo de um hardlink hostil.");
    }

    private static CatalogDownloadOptions CreateOptions() => new()
    {
        MaximumFileSizeBytes = 2 * 1024 * 1024,
        InactivityTimeout = TimeSpan.FromSeconds(2),
        RetryDelays = [TimeSpan.FromMilliseconds(10)],
        AllowedHosts = new HashSet<string>(["resume.test"], StringComparer.OrdinalIgnoreCase)
    };

    private static CatalogItem CreateItem(string id, byte[] bytes, bool extract = false) => new()
    {
        Id = id,
        CategoryId = "tests",
        Title = id,
        Category = "Testes",
        Extract = extract,
        Artifact = new CatalogArtifactDescriptor
        {
            ArtifactId = CreateArtifactId(id),
            ArtifactVersion = 1,
            ContentLength = bytes.LongLength,
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            SafeFileName = id + ".bin",
            FileExtension = ".bin",
            ExtractPolicy = extract
                ? CatalogExtractPolicy.ExtractArchive
                : CatalogExtractPolicy.None,
            ManifestIdentity = Convert.ToHexString(
                    SHA256.HashData("manifest-tests-v1"u8))
                .ToLowerInvariant()
        }
    };

    private static string CreateArtifactId(string id) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)))
            .ToLowerInvariant()[..32];

    private static CatalogItem WithArtifact(
        CatalogItem source,
        Func<CatalogArtifactDescriptor, CatalogArtifactDescriptor> transform) => new()
        {
            Id = source.Id,
            CategoryId = source.CategoryId,
            Title = source.Title,
            Category = source.Category,
            Extract = source.Extract,
            Artifact = transform(source.Artifact!)
        };

    private static async Task WaitForPrefixOrEarlyCompletionAsync(
        Task prefixDelivered,
        Task<CatalogDownloadResult> download,
        string scenario)
    {
        Task completed;
        try
        {
            completed = await Task.WhenAny(prefixDelivered, download).WaitAsync(ScenarioTimeout);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException(
                $"O cenário '{scenario}' não alcançou a rede nem concluiu em "
                + $"{ScenarioTimeout.TotalSeconds:0} segundos.",
                exception);
        }

        if (ReferenceEquals(completed, prefixDelivered))
        {
            await prefixDelivered;
            return;
        }

        var result = await download;
        throw new InvalidDataException(
            $"O cenário '{scenario}' terminou antes de entregar o prefixo HTTP: "
            + $"estado={result.State}; mensagem={result.Message}");
    }

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

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);

    private sealed class RotatingGrantProvider : ICatalogDownloadRequestProvider
    {
        private int _requestCount;
        public List<long> Offsets { get; } = [];
        public List<string> RequestPaths { get; } = [];
        public List<string> AuthorizationValues { get; } = [];
        public List<CatalogDownloadValidators> ObservedValidators { get; } = [];

        public ValueTask<HttpRequestMessage> CreateRequestAsync(
            string itemId,
            CatalogArtifactDescriptor artifact,
            long offset,
            CatalogDownloadValidators validators,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _requestCount++;
            var path = $"/ephemeral-grant-{_requestCount}/{artifact.ArtifactId}{artifact.FileExtension}";
            var token = Convert.ToBase64String(SHA256.HashData(
                    Encoding.UTF8.GetBytes($"test-only-grant-{_requestCount}")))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
            var request = new HttpRequestMessage(
                HttpMethod.Get,
                new Uri("https://resume.test" + path));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
            Offsets.Add(offset);
            RequestPaths.Add(path);
            AuthorizationValues.Add(token);
            ObservedValidators.Add(validators);
            return ValueTask.FromResult(request);
        }
    }

    private sealed class InvalidRangeProvider : ICatalogDownloadRequestProvider
    {
        public ValueTask<HttpRequestMessage> CreateRequestAsync(
            string itemId,
            CatalogArtifactDescriptor artifact,
            long offset,
            CatalogDownloadValidators validators,
            CancellationToken cancellationToken)
        {
            var request = new HttpRequestMessage(
                HttpMethod.Get,
                new Uri("https://resume.test/invalid-range/package.bin"));
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer", new string('A', 43));
            request.Headers.Range = new RangeHeaderValue(offset + 1, null);
            return ValueTask.FromResult(request);
        }
    }

    private sealed class InterruptedTransferHandler(byte[] payload, int interruptionOffset)
        : HttpMessageHandler
    {
        private int _requestCount;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _requestCount++;
            Check(request.Headers.Authorization?.Scheme == "Bearer",
                "A concessão efêmera não chegou à camada HTTP.");
            if (_requestCount == 1)
            {
                Check(request.Headers.Range is null, "A primeira tentativa não deve conter Range.");
                return Task.FromResult(CreateResponse(
                    HttpStatusCode.OK,
                    new InterruptingReadStream(payload, interruptionOffset),
                    0));
            }

            var offset = request.Headers.Range?.Ranges.Single().From
                         ?? throw new InvalidOperationException("A retomada não enviou Range.");
            return Task.FromResult(CreateResponse(
                HttpStatusCode.PartialContent,
                new MemoryStream(payload, (int)offset, payload.Length - (int)offset, writable: false),
                offset));
        }

        private HttpResponseMessage CreateResponse(HttpStatusCode status, Stream stream, long offset)
        {
            var response = new HttpResponseMessage(status) { Content = new StreamContent(stream) };
            response.Headers.ETag = new EntityTagHeaderValue("\"artifact-v1\"");
            response.Content.Headers.ContentLength = payload.LongLength - offset;
            if (offset > 0)
                response.Content.Headers.ContentRange =
                    new ContentRangeHeaderValue(offset, payload.LongLength - 1, payload.LongLength);
            return response;
        }
    }

    private sealed class PayloadHandler(byte[] payload) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            var offset = request.Headers.Range?.Ranges.Single().From ?? 0;
            var status = offset == 0 ? HttpStatusCode.OK : HttpStatusCode.PartialContent;
            var response = new HttpResponseMessage(status)
            {
                Content = new ByteArrayContent(payload[(int)offset..])
            };
            response.Headers.ETag = new EntityTagHeaderValue("\"payload-v1\"");
            response.Content.Headers.ContentLength = payload.LongLength - offset;
            if (offset > 0)
                response.Content.Headers.ContentRange =
                    new ContentRangeHeaderValue(offset, payload.LongLength - 1, payload.LongLength);
            return Task.FromResult(response);
        }
    }

    private sealed class RedirectHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
            {
                Content = new ByteArrayContent([])
            };
            response.Headers.Location = new Uri("https://resume.test/redirected/package.bin");
            return Task.FromResult(response);
        }
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            throw new InvalidOperationException("A requisição inválida não deveria chegar à rede.");
        }
    }

    private sealed class WrongLengthHandler(byte[] payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload[..^1])
            };
            response.Content.Headers.ContentLength = payload.LongLength - 1;
            return Task.FromResult(response);
        }
    }

    private sealed class PausingHandler(
        byte[] payload,
        int pauseOffset) : HttpMessageHandler
    {
        public TaskCompletionSource PrefixDelivered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(
                    new PausingReadStream(payload, pauseOffset, PrefixDelivered))
            };
            response.Headers.ETag = new EntityTagHeaderValue("\"pause-v1\"");
            response.Content.Headers.ContentLength = payload.LongLength;
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
                throw new IOException("Interrupção simulada.");
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
