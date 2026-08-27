using System.Security;
using System.Security.Cryptography;
using System.Text;
using TurboBoxManager.Catalog;

namespace TurboBoxManager.CatalogVerifier;

internal static class CrossVolumeMoveVerifier
{
    private const string TemporaryRootPrefix = "Turborama-CrossVolumeVerifier-";

    internal static async Task RunAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine(
                "SKIP: integração cross-volume requer Windows e pelo menos dois volumes NTFS graváveis.");
            return;
        }

        var runId = Guid.NewGuid().ToString("N");
        var createdRoots = new List<CreatedRoot>();
        Exception? testFailure = null;
        try
        {
            var volumes = DiscoverWritableNtfsVolumes(runId, createdRoots);
            if (volumes.Count < 2)
            {
                var discovered = volumes.Count == 0
                    ? "nenhum"
                    : string.Join(
                        ", ",
                        volumes.Select(volume => FormatVolume(volume)));
                Console.WriteLine(
                    $"SKIP: integração cross-volume requer dois volumes NTFS graváveis; disponíveis: {discovered}.");
                return;
            }

            var sourceVolume = volumes[0];
            var destinationVolume = volumes[1];
            if (sourceVolume.VolumeSerialNumber == destinationVolume.VolumeSerialNumber)
                throw new InvalidOperationException(
                    "O teste cross-volume selecionou duas raízes da mesma identidade física.");

            await VerifySuccessfulMoveAsync(sourceVolume, destinationVolume);
            EnsureNoPathIdentityHandles("após o cenário de sucesso cross-volume");
            await VerifyCollisionPreservesBothFilesAsync(sourceVolume, destinationVolume);
            EnsureNoPathIdentityHandles("após o cenário de colisão cross-volume");

            Console.WriteLine(
                $"PASS: move cross-volume físico exercitado de {FormatVolume(sourceVolume)} para {FormatVolume(destinationVolume)}; sucesso e colisão preservadora verificados.");
        }
        catch (Exception exception)
        {
            testFailure = exception;
            throw;
        }
        finally
        {
            if (PathIdentity.OutstandingDirectoryHandles != 0)
            {
                var leak =
                    $"CrossVolumeMoveVerifier deixou {PathIdentity.OutstandingDirectoryHandles} handles: "
                    + PathIdentity.OutstandingDirectoryHandlePaths;
                if (testFailure is null) throw new InvalidOperationException(leak);
                Console.Error.WriteLine(leak);
            }
            else
            {
                for (var index = createdRoots.Count - 1; index >= 0; index--)
                    DeleteExactTemporaryRoot(createdRoots[index], runId);
            }
        }
    }

    private static List<VolumeCandidate> DiscoverWritableNtfsVolumes(
        string runId,
        List<CreatedRoot> createdRoots)
    {
        var candidates = new List<VolumeCandidate>(capacity: 2);
        foreach (var drive in DriveInfo.GetDrives().OrderBy(drive => drive.Name, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!drive.IsReady
                    || drive.DriveType != DriveType.Fixed
                    || !drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
                    continue;

                var volumeRoot = Path.GetFullPath(drive.RootDirectory.FullName);
                var testRoot = Path.Combine(volumeRoot, TemporaryRootPrefix + runId);
                if (Directory.Exists(testRoot) || File.Exists(testRoot))
                    throw new IOException(
                        $"A raiz temporária exclusiva já existe em '{volumeRoot}'.");

                Directory.CreateDirectory(testRoot);
                var created = new CreatedRoot(volumeRoot, testRoot);
                createdRoots.Add(created);
                ValidateTemporaryRoot(created, runId);

                ulong serial;
                using (var lease = PathIdentity.OpenDirectoryTree(testRoot))
                {
                    serial = PathIdentity.CaptureDirectoryIdentity(
                        lease.AnchorHandle,
                        testRoot).VolumeSerialNumber;
                }

                if (candidates.Any(candidate => candidate.VolumeSerialNumber == serial))
                    continue;
                candidates.Add(new VolumeCandidate(volumeRoot, testRoot, serial));
                if (candidates.Count == 2) break;
            }
            catch (Exception exception) when (IsUnavailableVolume(exception))
            {
                Console.WriteLine(
                    $"SKIP parcial: volume '{drive.Name}' não pôde hospedar o teste cross-volume ({exception.Message}).");
            }
        }

        EnsureNoPathIdentityHandles("após descobrir volumes cross-volume");
        return candidates;
    }

    private static async Task VerifySuccessfulMoveAsync(
        VolumeCandidate sourceVolume,
        VolumeCandidate destinationVolume)
    {
        var sourceDirectory = Path.Combine(sourceVolume.TestRoot, "success-source");
        var destinationDirectory = Path.Combine(destinationVolume.TestRoot, "success-destination");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(destinationDirectory);

        var payload = CreatePayload(384 * 1024 + 37, seed: 17);
        var sourcePath = Path.Combine(sourceDirectory, "authorized-source.bin");
        var destinationPath = Path.Combine(destinationDirectory, "published.bin");
        await File.WriteAllBytesAsync(sourcePath, payload);
        var artifact = CreateArtifact("cross-volume-success", "published.bin", payload);

        await StoreWindow.MoveFilePreservingSourceOnFailureAsync(
            sourcePath,
            destinationPath,
            artifact,
            CancellationToken.None);

        Check(!File.Exists(sourcePath),
            "O sucesso cross-volume não removeu a origem somente após a publicação íntegra.");
        Check(File.Exists(destinationPath),
            "O sucesso cross-volume não publicou o destino final.");
        await CheckFileMatchesArtifactAsync(
            destinationPath,
            artifact,
            "O destino publicado no sucesso cross-volume divergiu do artefato.");
        CheckNoTransactionTemporary(destinationDirectory, Path.GetFileName(destinationPath));
    }

    private static async Task VerifyCollisionPreservesBothFilesAsync(
        VolumeCandidate sourceVolume,
        VolumeCandidate destinationVolume)
    {
        var sourceDirectory = Path.Combine(sourceVolume.TestRoot, "collision-source");
        var destinationDirectory = Path.Combine(destinationVolume.TestRoot, "collision-destination");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(destinationDirectory);

        var sourcePayload = CreatePayload(256 * 1024 + 19, seed: 43);
        var existingDestinationPayload = CreatePayload(32 * 1024 + 11, seed: 91);
        var sourcePath = Path.Combine(sourceDirectory, "collision-source.bin");
        var destinationPath = Path.Combine(destinationDirectory, "occupied.bin");
        await File.WriteAllBytesAsync(sourcePath, sourcePayload);
        await File.WriteAllBytesAsync(destinationPath, existingDestinationPayload);
        var sourceArtifact = CreateArtifact(
            "cross-volume-collision",
            "occupied.bin",
            sourcePayload);
        var existingDestinationHash = Convert.ToHexString(
            SHA256.HashData(existingDestinationPayload));

        var collisionRejected = false;
        try
        {
            await StoreWindow.MoveFilePreservingSourceOnFailureAsync(
                sourcePath,
                destinationPath,
                sourceArtifact,
                CancellationToken.None);
        }
        catch (IOException)
        {
            collisionRejected = true;
        }

        Check(collisionRejected,
            "Um destino existente deveria bloquear a publicação cross-volume sem sobrescrita.");
        Check(File.Exists(sourcePath),
            "A colisão cross-volume apagou a origem antes de concluir a publicação.");
        await CheckFileMatchesArtifactAsync(
            sourcePath,
            sourceArtifact,
            "A colisão cross-volume alterou a origem preservada.");
        Check(File.Exists(destinationPath),
            "A colisão cross-volume removeu o destino que já existia.");
        var destinationInfo = new FileInfo(destinationPath);
        Check(destinationInfo.Length == existingDestinationPayload.LongLength,
            "A colisão cross-volume alterou o tamanho do destino existente.");
        var destinationHash = await ComputeSha256Async(destinationPath);
        Check(destinationHash.Equals(existingDestinationHash, StringComparison.Ordinal),
            "A colisão cross-volume alterou os bytes do destino existente.");
        CheckNoTransactionTemporary(destinationDirectory, Path.GetFileName(destinationPath));
    }

    private static CatalogArtifactDescriptor CreateArtifact(
        string id,
        string safeFileName,
        byte[] payload) => new()
        {
            ArtifactId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)))
            .ToLowerInvariant()[..32],
            ArtifactVersion = 1,
            ContentLength = payload.LongLength,
            Sha256 = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
            SafeFileName = safeFileName,
            FileExtension = Path.GetExtension(safeFileName),
            ExtractPolicy = CatalogExtractPolicy.None,
            ManifestIdentity = Convert.ToHexString(
                SHA256.HashData("catalog-verifier-cross-volume-v1"u8))
            .ToLowerInvariant()
        };

    private static byte[] CreatePayload(int length, int seed)
    {
        var payload = GC.AllocateUninitializedArray<byte>(length);
        for (var index = 0; index < payload.Length; index++)
            payload[index] = (byte)((index * 31L + seed) % 251);
        return payload;
    }

    private static async Task CheckFileMatchesArtifactAsync(
        string path,
        CatalogArtifactDescriptor artifact,
        string message)
    {
        var info = new FileInfo(path);
        Check(info.Exists && info.Length == artifact.ContentLength, message);
        var hash = await ComputeSha256Async(path);
        Check(hash.Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase), message);
    }

    private static async Task<string> ComputeSha256Async(string path)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    private static void CheckNoTransactionTemporary(
        string destinationDirectory,
        string destinationLeafName)
    {
        var temporaryPrefix = destinationLeafName + ".copy-";
        Check(!Directory.EnumerateFiles(destinationDirectory, "*", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Any(fileName => fileName?.StartsWith(
                    temporaryPrefix,
                    StringComparison.OrdinalIgnoreCase) == true),
            "O fluxo cross-volume deixou um arquivo temporário de transação.");
    }

    private static void EnsureNoPathIdentityHandles(string context)
    {
        Check(PathIdentity.OutstandingDirectoryHandles == 0,
            $"PathIdentity manteve {PathIdentity.OutstandingDirectoryHandles} handles {context}: "
            + PathIdentity.OutstandingDirectoryHandlePaths);
    }

    private static void DeleteExactTemporaryRoot(CreatedRoot createdRoot, string runId)
    {
        if (!Directory.Exists(createdRoot.TestRoot)) return;
        ValidateTemporaryRoot(createdRoot, runId);
        ValidateTreeContainsNoReparsePoints(createdRoot.TestRoot);
        Directory.Delete(createdRoot.TestRoot, recursive: true);
    }

    private static void ValidateTemporaryRoot(CreatedRoot createdRoot, string runId)
    {
        var volumeRoot = Path.GetFullPath(createdRoot.VolumeRoot);
        var temporaryRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(createdRoot.TestRoot));
        var expectedLeaf = TemporaryRootPrefix + runId;
        var expectedRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(Path.Combine(volumeRoot, expectedLeaf)));
        var parent = Directory.GetParent(temporaryRoot)?.FullName;
        if (!temporaryRoot.Equals(expectedRoot, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(temporaryRoot).Equals(expectedLeaf, StringComparison.Ordinal)
            || parent is null
            || !Path.GetFullPath(parent).Equals(volumeRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Cleanup recusado para raiz temporária inesperada: '{temporaryRoot}'.");

        var attributes = File.GetAttributes(temporaryRoot);
        if ((attributes & FileAttributes.ReparsePoint) != 0
            || (attributes & FileAttributes.Directory) == 0)
            throw new InvalidOperationException(
                $"Cleanup recusado para raiz temporária não física: '{temporaryRoot}'.");
    }

    private static void ValidateTreeContainsNoReparsePoints(string temporaryRoot)
    {
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(temporaryRoot));
        var rootPrefix = canonicalRoot + Path.DirectorySeparatorChar;
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(canonicalRoot);
        while (pendingDirectories.Count > 0)
        {
            var current = pendingDirectories.Pop();
            foreach (var entry in new DirectoryInfo(current).EnumerateFileSystemInfos())
            {
                var fullPath = Path.GetFullPath(entry.FullName);
                if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
                    || (entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException(
                        $"Cleanup recusado para entrada inesperada: '{fullPath}'.");
                if ((entry.Attributes & FileAttributes.Directory) != 0)
                    pendingDirectories.Push(fullPath);
            }
        }
    }

    private static bool IsUnavailableVolume(Exception exception) => exception is IOException
        or UnauthorizedAccessException
        or InvalidDataException
        or NotSupportedException
        or SecurityException;

    private static string FormatVolume(VolumeCandidate volume) =>
        $"{volume.VolumeRoot} (serial 0x{volume.VolumeSerialNumber:X16})";

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed record CreatedRoot(string VolumeRoot, string TestRoot);

    private sealed record VolumeCandidate(
        string VolumeRoot,
        string TestRoot,
        ulong VolumeSerialNumber);
}
