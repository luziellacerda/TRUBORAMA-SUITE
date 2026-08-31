using System.Buffers;
using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;

namespace TurboBoxManager;

internal sealed record EmbeddedMusicTrack(
    string DisplayName,
    string FileName,
    string ResourceName,
    long Length,
    string Sha256);

internal sealed class EmbeddedMusicTrackLease : IDisposable
{
    private PathIdentity.DirectoryTreeLease? _cache;
    private FileStream? _stream;
    private readonly PathIdentity.HandleIdentity _identity;

    internal EmbeddedMusicTrackLease(
        string path,
        PathIdentity.DirectoryTreeLease cache,
        FileStream stream,
        PathIdentity.HandleIdentity identity)
    {
        Path = path;
        _cache = cache;
        _stream = stream;
        _identity = identity;
    }

    internal string Path { get; }

    internal void Revalidate()
    {
        var cache = _cache ?? throw new ObjectDisposedException(nameof(EmbeddedMusicTrackLease));
        var stream = _stream ?? throw new ObjectDisposedException(nameof(EmbeddedMusicTrackLease));
        _ = PathIdentity.RevalidateFile(stream.SafeFileHandle, Path, _identity);
        cache.Revalidate();
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _stream = null;
        _cache?.Dispose();
        _cache = null;
    }
}

internal static class EmbeddedMusicLibrary
{
    private const int CopyBufferSize = 128 * 1024;
    private const string CacheVersion = "built-in-v1";
    private static readonly ReadOnlyCollection<EmbeddedMusicTrack> BuiltInTracks =
        Array.AsReadOnly<EmbeddedMusicTrack>(
    [
        new(
            "Turborama - Faixa 01",
            "Turborama - Faixa 01.mp3",
            "Turborama.Music.Track01.mp3",
            2_095_127,
            "5e292676aa783eff64870eb9ca87adb4307560243361c92e8afa26276655918f"),
        new(
            "Turborama - Faixa 02",
            "Turborama - Faixa 02.mp3",
            "Turborama.Music.Track02.mp3",
            1_568_528,
            "9e957ca19cebe0be04ea45ef846bfa051fa55593fbe3d7e6c34a7ae6a252108d"),
        new(
            "Turborama - Faixa 03",
            "Turborama - Faixa 03.mp3",
            "Turborama.Music.Track03.mp3",
            3_261_957,
            "58771cceb6a3b5fe28bab9e70f25696dd93ab3c2c6d2393397b48ee9c6ce7c1a"),
        new(
            "Turborama - Faixa 04",
            "Turborama - Faixa 04.mp3",
            "Turborama.Music.Track04.mp3",
            4_603_186,
            "546eb11340006df8103af31e89c781e3c92dd577b11e19a619e7f0f3867ec20c"),
        new(
            "Turborama - Faixa 05",
            "Turborama - Faixa 05.mp3",
            "Turborama.Music.Track05.mp3",
            4_993_016,
            "869676a2ff6c864b307a87dbe61eb5a99419c91e44dfbdd24bb593bb2263f10f"),
        new(
            "Turborama - Faixa 06",
            "Turborama - Faixa 06.mp3",
            "Turborama.Music.Track06.mp3",
            2_418_436,
            "5f2a7fcbce44ddc1124a0f5cf8a3a7c2db248e02a7cd027a42075a93f2c3a25b"),
        new(
            "Turborama - Faixa 07",
            "Turborama - Faixa 07.mp3",
            "Turborama.Music.Track07.mp3",
            5_690_824,
            "3836006f2c9ceeeb51d1063f9ce501919df95b87457a179979aae5bb694563d6"),
        new(
            "Turborama - Faixa 08",
            "Turborama - Faixa 08.mp3",
            "Turborama.Music.Track08.mp3",
            4_963_886,
            "a3de2a48c6622d2786afc77c53bbcc75c774ded68c7c4260b73d540fa789c4e7")
    ]);

    internal static IReadOnlyList<EmbeddedMusicTrack> Tracks => BuiltInTracks;

    internal static IReadOnlyList<string> PreparePlaylist(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cacheRoot = GetCacheRoot();
        using var cache = PathIdentity.OpenDirectoryTree(
            cacheRoot,
            createIfMissing: true,
            privateLeaf: true);
        var paths = new List<string>(BuiltInTracks.Count);
        foreach (var track in BuiltInTracks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateTrackDefinition(track);
            var path = PathIdentity.Canonicalize(Path.Combine(cacheRoot, track.FileName));
            EnsureInsideRoot(path, cacheRoot);
            if (!IsCachedTrackValid(cache, path, track, cancellationToken))
                PublishTrack(cache, cacheRoot, path, track, cancellationToken);
            if (!IsCachedTrackValid(cache, path, track, cancellationToken))
                throw new InvalidDataException(
                    $"A música interna '{track.DisplayName}' não passou pela validação final.");
            paths.Add(path);
        }

        cache.Revalidate();
        return paths;
    }

    internal static EmbeddedMusicTrackLease OpenVerifiedTrackLease(
        string path,
        CancellationToken cancellationToken)
    {
        var cacheRoot = GetCacheRoot();
        var canonicalPath = PathIdentity.Canonicalize(path);
        EnsureInsideRoot(canonicalPath, cacheRoot);
        var track = BuiltInTracks.FirstOrDefault(candidate =>
            PathIdentity.Canonicalize(Path.Combine(cacheRoot, candidate.FileName)).Equals(
                canonicalPath,
                StringComparison.OrdinalIgnoreCase));
        if (track is null)
            throw new InvalidDataException("A faixa não pertence à playlist interna aprovada.");

        try
        {
            return OpenVerifiedTrackLeaseCore(
                cacheRoot,
                canonicalPath,
                track,
                cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            _ = PreparePlaylist(cancellationToken);
            return OpenVerifiedTrackLeaseCore(
                cacheRoot,
                canonicalPath,
                track,
                cancellationToken);
        }
    }

    private static EmbeddedMusicTrackLease OpenVerifiedTrackLeaseCore(
        string cacheRoot,
        string path,
        EmbeddedMusicTrack track,
        CancellationToken cancellationToken)
    {
        var cache = PathIdentity.OpenDirectoryTree(cacheRoot);
        FileStream? stream = null;
        try
        {
            stream = cache.OpenFile(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                CopyBufferSize,
                FileOptions.SequentialScan);
            var identity = PathIdentity.CaptureFileIdentity(stream.SafeFileHandle, path);
            if (stream.Length != track.Length
                || !HashMatches(HashStream(stream, cancellationToken), track.Sha256))
                throw new InvalidDataException(
                    $"A música interna '{track.DisplayName}' foi alterada.");
            _ = PathIdentity.RevalidateFile(stream.SafeFileHandle, path, identity);
            cache.Revalidate();
            return new EmbeddedMusicTrackLease(path, cache, stream, identity);
        }
        catch
        {
            stream?.Dispose();
            cache.Dispose();
            throw;
        }
    }

    private static void PublishTrack(
        PathIdentity.DirectoryTreeLease cache,
        string cacheRoot,
        string destinationPath,
        EmbeddedMusicTrack track,
        CancellationToken cancellationToken)
    {
        var temporaryPath = PathIdentity.Canonicalize(
            destinationPath + ".tmp-" + Guid.NewGuid().ToString("N"));
        EnsureInsideRoot(temporaryPath, cacheRoot);
        try
        {
            using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(
                                     track.ResourceName)
                                 ?? throw new InvalidDataException(
                                     $"A música interna '{track.DisplayName}' não foi incorporada ao executável.");
            if (resource.Length != track.Length)
                throw new InvalidDataException(
                    $"A música interna '{track.DisplayName}' possui tamanho inesperado.");

            using var output = cache.OpenFile(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                CopyBufferSize,
                FileOptions.SequentialScan | FileOptions.WriteThrough,
                deleteAccess: true);
            var temporaryIdentity = PathIdentity.CaptureFileIdentity(
                output.SafeFileHandle,
                temporaryPath);
            var actualHash = CopyAndHash(resource, output, cancellationToken);
            output.Flush(flushToDisk: true);
            if (output.Length != track.Length || !HashMatches(actualHash, track.Sha256))
                throw new InvalidDataException(
                    $"A música interna '{track.DisplayName}' falhou na validação de integridade.");

            _ = PathIdentity.RevalidateFile(
                output.SafeFileHandle,
                temporaryPath,
                temporaryIdentity);
            cache.Revalidate();
            try
            {
                _ = PathIdentity.RenameByHandle(
                    output.SafeFileHandle,
                    temporaryIdentity,
                    cache.AnchorHandle,
                    cacheRoot,
                    Path.GetFileName(destinationPath),
                    replaceIfExists: true);
            }
            catch (IOException) when (IsCachedTrackValid(
                                          cache,
                                          destinationPath,
                                          track,
                                          cancellationToken))
            {
                // Outra instância publicou exatamente a mesma faixa primeiro.
            }
        }
        finally
        {
            try { _ = PathIdentity.DeleteFileExact(temporaryPath, cacheRoot); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static bool IsCachedTrackValid(
        PathIdentity.DirectoryTreeLease cache,
        string path,
        EmbeddedMusicTrack track,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var stream = cache.OpenFile(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                CopyBufferSize,
                FileOptions.SequentialScan);
            var identity = PathIdentity.CaptureFileIdentity(stream.SafeFileHandle, path);
            if (stream.Length != track.Length) return false;
            var actualHash = HashStream(stream, cancellationToken);
            _ = PathIdentity.RevalidateFile(stream.SafeFileHandle, path, identity);
            cache.Revalidate();
            return HashMatches(actualHash, track.Sha256);
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static byte[] CopyAndHash(
        Stream source,
        Stream destination,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = source.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                hash.AppendData(buffer, 0, read);
                destination.Write(buffer, 0, read);
            }
            return hash.GetHashAndReset();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static byte[] HashStream(Stream source, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = source.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                hash.AppendData(buffer, 0, read);
            }
            return hash.GetHashAndReset();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool HashMatches(ReadOnlySpan<byte> actual, string expectedSha256)
    {
        try
        {
            var expected = Convert.FromHexString(expectedSha256);
            return expected.Length == 32
                   && CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static void ValidateTrackDefinition(EmbeddedMusicTrack track)
    {
        if (string.IsNullOrWhiteSpace(track.DisplayName)
            || string.IsNullOrWhiteSpace(track.ResourceName)
            || !Path.GetFileName(track.FileName).Equals(track.FileName, StringComparison.Ordinal)
            || !track.FileName.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)
            || track.Length <= 0
            || track.Sha256.Length != 64)
            throw new InvalidDataException("A playlist interna possui uma definição inválida.");
    }

    private static void EnsureInsideRoot(string path, string root)
    {
        var canonicalPath = PathIdentity.Canonicalize(path);
        var canonicalRoot = PathIdentity.Canonicalize(root);
        var prefix = Path.TrimEndingDirectorySeparator(canonicalRoot)
                     + Path.DirectorySeparatorChar;
        if (!canonicalPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("O cache de músicas saiu da pasta local autorizada.");
    }

    private static string GetCacheRoot()
    {
        var localApplicationData = PathIdentity.Canonicalize(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        if (!Directory.Exists(localApplicationData))
            throw new DirectoryNotFoundException(
                "A pasta local do perfil não está disponível para preparar as músicas internas.");
        var cacheRoot = PathIdentity.Canonicalize(Path.Combine(
            localApplicationData,
            "Turborama",
            "Music",
            CacheVersion));
        EnsureInsideRoot(cacheRoot, localApplicationData);
        return cacheRoot;
    }
}
