using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace TurboramaBootstrapper;

internal static class Program
{
    private const string PackageVersion = "__PACKAGE_VERSION__";
    private const long PayloadLength = __PAYLOAD_LENGTH__;
    private const string PayloadSha256 = "__PAYLOAD_SHA256__";
    private const int ExpectedFileCount = __EXPECTED_FILE_COUNT__;
    private const long ExpectedContentBytes = __EXPECTED_CONTENT_BYTES__;
    private const string ExpectedTreeSha256 = "__EXPECTED_TREE_SHA256__";
    private const string ExpectedMainExeSha256 = "__EXPECTED_MAIN_EXE_SHA256__";
    private const string MarkerName = ".turborama-package.sha256";
    private const int TrailerSize = 56;
    private const int TrailerSearchBytes = 4 * 1024 * 1024;
    private static readonly byte[] TrailerMagic = Encoding.ASCII.GetBytes("TURBORAMA-PKG-V1");
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    [STAThread]
    private static int Main()
    {
        try
        {
            string processPath = Environment.ProcessPath
                ?? throw new InvalidOperationException("Não foi possível localizar o inicializador.");
            processPath = Path.GetFullPath(processPath);

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData))
                throw new InvalidOperationException("LOCALAPPDATA indisponível.");

            // Hook exclusivo deste protótipo para QA em uma pasta limpa e isolada.
            string? testRoot = Environment.GetEnvironmentVariable("TURBORAMA_BOOTSTRAPPER_TEST_ROOT");
            string packageBase = string.IsNullOrWhiteSpace(testRoot)
                ? Path.Combine(localAppData, "Turborama", "Packages")
                : testRoot;
            packageBase = Path.GetFullPath(packageBase);
            Directory.CreateDirectory(packageBase);
            RejectReparsePoint(packageBase);

            string packageRoot = CanonicalChild(
                packageBase,
                Path.Combine(PackageVersion, PayloadSha256.ToLowerInvariant()));
            string logRoot = CanonicalChild(packageBase, ".launcher-logs");
            Directory.CreateDirectory(logRoot);
            string logPath = CanonicalChild(logRoot, $"launcher-{PackageVersion}.log");

            using var mutex = new Mutex(false, $"Local\\Turborama.Package.{PayloadSha256}");
            if (!mutex.WaitOne(TimeSpan.FromMinutes(3)))
                throw new TimeoutException("Outra extração do Turborama não terminou.");

            try
            {
                AppendLog(logPath, $"START launcher={processPath}");
                PayloadLocation payload = LocateAndVerifyPayload(processPath);

                if (!ValidateInstalledPackage(packageRoot))
                {
                    AppendLog(logPath, "CACHE_MISS extracting verified payload");
                    InstallAtomically(processPath, payload, packageBase, packageRoot);
                }
                else
                {
                    AppendLog(logPath, "CACHE_OK full content tree verified");
                }

                if (!ValidateInstalledPackage(packageRoot))
                    throw new InvalidDataException("A validação final do pacote extraído falhou.");

                string appPath = CanonicalChild(packageRoot, "Turborama.exe");
                if (Environment.GetEnvironmentVariable("TURBORAMA_BOOTSTRAPPER_VERIFY_ONLY") == "1")
                {
                    AppendLog(logPath, $"VERIFY_ONLY success root={packageRoot}");
                    return 0;
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = appPath,
                    WorkingDirectory = packageRoot,
                    UseShellExecute = false
                };
                Process? child = Process.Start(startInfo);
                if (child is null)
                    throw new InvalidOperationException("O Windows não iniciou Turborama.exe.");
                AppendLog(logPath, $"LAUNCHED pid={child.Id} exe={appPath}");
                return 0;
            }
            finally
            {
                mutex.ReleaseMutex();
            }
        }
        catch (Exception ex)
        {
            TryWriteEmergencyLog(ex);
            return 1;
        }
    }

    private static PayloadLocation LocateAndVerifyPayload(string launcherPath)
    {
        using var stream = new FileStream(launcherPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        int tailLength = checked((int)Math.Min(stream.Length, TrailerSearchBytes));
        var tail = new byte[tailLength];
        stream.Position = stream.Length - tailLength;
        stream.ReadExactly(tail);

        int magicIndex = -1;
        for (int i = tail.Length - TrailerSize; i >= 0; i--)
        {
            if (tail.AsSpan(i, TrailerMagic.Length).SequenceEqual(TrailerMagic))
            {
                magicIndex = i;
                break;
            }
        }
        if (magicIndex < 0)
            throw new InvalidDataException("Trailer autenticado do pacote não encontrado.");

        ReadOnlySpan<byte> trailer = tail.AsSpan(magicIndex, TrailerSize);
        long declaredLength = BinaryPrimitives.ReadInt64LittleEndian(trailer.Slice(16, 8));
        string declaredHash = Convert.ToHexString(trailer.Slice(24, 32));
        if (declaredLength != PayloadLength || !declaredHash.Equals(PayloadSha256, StringComparison.Ordinal))
            throw new InvalidDataException("Metadados do pacote não correspondem à versão 1.6.0.");

        long trailerPosition = stream.Length - tailLength + magicIndex;
        long payloadOffset = trailerPosition - declaredLength;
        if (payloadOffset < 1_024 || payloadOffset + declaredLength != trailerPosition)
            throw new InvalidDataException("Posição inválida do pacote incorporado.");

        string computed = HashSlice(stream, payloadOffset, declaredLength);
        if (!computed.Equals(PayloadSha256, StringComparison.Ordinal))
            throw new CryptographicException("SHA-256 do pacote incorporado é inválido.");

        return new PayloadLocation(payloadOffset, declaredLength);
    }

    private static void InstallAtomically(
        string launcherPath,
        PayloadLocation payload,
        string packageBase,
        string packageRoot)
    {
        string staging = CanonicalChild(packageBase, $".staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        RejectReparsePoint(staging);

        bool stagingMoved = false;
        try
        {
            ExtractSafely(launcherPath, payload, staging);
            ContentTree tree = ComputeContentTree(staging);
            if (!tree.IsExpected)
                throw new CryptographicException(
                    $"Conteúdo extraído inválido: files={tree.FileCount}, bytes={tree.TotalBytes}, sha={tree.Sha256}.");

            string marker = CanonicalChild(staging, MarkerName);
            File.WriteAllText(marker, PayloadSha256 + Environment.NewLine, new UTF8Encoding(false));

            Directory.CreateDirectory(Path.GetDirectoryName(packageRoot)!);
            if (Directory.Exists(packageRoot))
            {
                string quarantine = CanonicalChild(
                    packageBase,
                    $".invalid-{PackageVersion}-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}");
                Directory.Move(packageRoot, quarantine);
            }

            Directory.Move(staging, packageRoot);
            stagingMoved = true;
        }
        finally
        {
            if (!stagingMoved && Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
        }
    }

    private static void ExtractSafely(string launcherPath, PayloadLocation payload, string staging)
    {
        using var launcher = new FileStream(launcherPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var slice = new BoundedReadStream(launcher, payload.Offset, payload.Length);
        using var archive = new ZipArchive(slice, ZipArchiveMode.Read, leaveOpen: false);
        var seen = new HashSet<string>(PathComparer);
        int fileCount = 0;
        long contentBytes = 0;

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string relative = entry.FullName.Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(relative) || relative.IndexOf('\0') >= 0)
                throw new InvalidDataException("Entrada ZIP vazia ou inválida.");

            bool isDirectory = relative.EndsWith("/", StringComparison.Ordinal);
            string trimmed = isDirectory ? relative.TrimEnd('/') : relative;
            if (string.IsNullOrEmpty(trimmed) || Path.IsPathRooted(trimmed))
                throw new InvalidDataException($"Caminho ZIP inválido: {relative}");

            string destination = CanonicalChild(staging, trimmed.Replace('/', Path.DirectorySeparatorChar));
            string normalizedKey = Path.GetRelativePath(staging, destination).Replace('\\', '/');
            if (!seen.Add(normalizedKey))
                throw new InvalidDataException($"Entrada ZIP duplicada: {relative}");

            if (isDirectory)
            {
                CreateSafeDirectory(staging, destination);
                continue;
            }

            if (entry.Length < 0 || entry.Length > ExpectedContentBytes)
                throw new InvalidDataException($"Tamanho ZIP inválido: {relative}");
            contentBytes = checked(contentBytes + entry.Length);
            fileCount++;
            if (fileCount > ExpectedFileCount || contentBytes > ExpectedContentBytes)
                throw new InvalidDataException("Limites do pacote foram excedidos.");

            string parent = Path.GetDirectoryName(destination)
                ?? throw new InvalidDataException($"Diretório inválido: {relative}");
            CreateSafeDirectory(staging, parent);
            using Stream input = entry.Open();
            using var output = new FileStream(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1024 * 1024,
                FileOptions.SequentialScan);
            input.CopyTo(output, 1024 * 1024);
            if (output.Length != entry.Length)
                throw new InvalidDataException($"Extração incompleta: {relative}");
        }

        if (fileCount != ExpectedFileCount || contentBytes != ExpectedContentBytes)
            throw new InvalidDataException(
                $"Inventário ZIP inesperado: files={fileCount}, bytes={contentBytes}.");
    }

    private static bool ValidateInstalledPackage(string packageRoot)
    {
        try
        {
            if (!Directory.Exists(packageRoot))
                return false;
            RejectTreeReparsePoints(packageRoot);

            string marker = CanonicalChild(packageRoot, MarkerName);
            if (!File.Exists(marker))
                return false;
            string markerValue = File.ReadAllText(marker).Trim();
            if (!markerValue.Equals(PayloadSha256, StringComparison.Ordinal))
                return false;

            ContentTree tree = ComputeContentTree(packageRoot);
            if (!tree.IsExpected)
                return false;

            string mainExe = CanonicalChild(packageRoot, "Turborama.exe");
            return HashFile(mainExe).Equals(ExpectedMainExeSha256, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static ContentTree ComputeContentTree(string root)
    {
        string canonicalRoot = Path.GetFullPath(root);
        var files = Directory.EnumerateFiles(canonicalRoot, "*", SearchOption.AllDirectories)
            .Where(path => !Path.GetFileName(path).Equals(MarkerName, StringComparison.Ordinal))
            .Select(path => new
            {
                FullPath = path,
                Relative = Path.GetRelativePath(canonicalRoot, path).Replace('\\', '/')
            })
            .OrderBy(item => item.Relative, StringComparer.Ordinal)
            .ToArray();

        long totalBytes = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[1024 * 1024];
        byte[] encodedLength = new byte[8];
        foreach (var file in files)
        {
            RejectReparsePoint(file.FullPath);
            var info = new FileInfo(file.FullPath);
            totalBytes = checked(totalBytes + info.Length);
            hash.AppendData(Encoding.UTF8.GetBytes(file.Relative));
            hash.AppendData(new byte[] { 0 });
            BinaryPrimitives.WriteInt64LittleEndian(encodedLength, info.Length);
            hash.AppendData(encodedLength);
            hash.AppendData(new byte[] { 0 });

            using var stream = new FileStream(
                file.FullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                buffer.Length,
                FileOptions.SequentialScan);
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                hash.AppendData(buffer, 0, read);
        }

        string sha = Convert.ToHexString(hash.GetHashAndReset());
        return new ContentTree(files.Length, totalBytes, sha);
    }

    private static string HashSlice(FileStream stream, long offset, long length)
    {
        stream.Position = offset;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[1024 * 1024];
        long remaining = length;
        while (remaining > 0)
        {
            int requested = (int)Math.Min(buffer.Length, remaining);
            int read = stream.Read(buffer, 0, requested);
            if (read <= 0)
                throw new EndOfStreamException("Pacote incorporado truncado.");
            hash.AppendData(buffer, 0, read);
            remaining -= read;
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string CanonicalChild(string root, string relative)
    {
        string canonicalRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string candidate = Path.GetFullPath(Path.Combine(canonicalRoot, relative));
        if (!candidate.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Caminho fora da raiz autorizada: {relative}");
        return candidate;
    }

    private static void CreateSafeDirectory(string root, string target)
    {
        string canonicalRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        string requestedTarget = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar);
        if (requestedTarget.Equals(canonicalRoot, StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(canonicalRoot);
            RejectReparsePoint(canonicalRoot);
            return;
        }
        string canonicalTarget = CanonicalChild(canonicalRoot, Path.GetRelativePath(canonicalRoot, requestedTarget));
        string relative = Path.GetRelativePath(canonicalRoot, canonicalTarget);
        string current = canonicalRoot;
        foreach (string part in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (part is "." or "..")
                throw new InvalidDataException("Componente de caminho inválido.");
            current = Path.Combine(current, part);
            Directory.CreateDirectory(current);
            RejectReparsePoint(current);
        }
    }

    private static void RejectTreeReparsePoints(string root)
    {
        RejectReparsePoint(root);
        foreach (string path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
            RejectReparsePoint(path);
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"Reparse point não permitido: {path}");
    }

    private static void AppendLog(string path, string message)
    {
        File.AppendAllText(
            path,
            $"{DateTime.UtcNow:O} {message}{Environment.NewLine}",
            new UTF8Encoding(false));
    }

    private static void TryWriteEmergencyLog(Exception exception)
    {
        try
        {
            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Turborama",
                "Launcher");
            Directory.CreateDirectory(root);
            File.AppendAllText(
                Path.Combine(root, "launcher-error.log"),
                $"{DateTime.UtcNow:O} {exception}{Environment.NewLine}",
                new UTF8Encoding(false));
        }
        catch
        {
            // Sem prompt: a falha de logging não deve gerar outra falha visível.
        }
    }

    private readonly record struct PayloadLocation(long Offset, long Length);

    private readonly record struct ContentTree(int FileCount, long TotalBytes, string Sha256)
    {
        public bool IsExpected =>
            FileCount == ExpectedFileCount &&
            TotalBytes == ExpectedContentBytes &&
            Sha256.Equals(ExpectedTreeSha256, StringComparison.Ordinal);
    }

    private sealed class BoundedReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _start;
        private readonly long _length;
        private long _position;

        public BoundedReadStream(Stream inner, long start, long length)
        {
            _inner = inner;
            _start = start;
            _length = length;
            _position = 0;
            _inner.Position = _start;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position
        {
            get => _position;
            set => Seek(value, SeekOrigin.Begin);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= _length)
                return 0;
            count = (int)Math.Min(count, _length - _position);
            _inner.Position = _start + _position;
            int read = _inner.Read(buffer, offset, count);
            _position += read;
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            if (_position >= _length)
                return 0;
            int count = (int)Math.Min(buffer.Length, _length - _position);
            _inner.Position = _start + _position;
            int read = _inner.Read(buffer[..count]);
            _position += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long candidate = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(_position + offset),
                SeekOrigin.End => checked(_length + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            if (candidate < 0 || candidate > _length)
                throw new IOException("Seek fora do payload.");
            _position = candidate;
            return _position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
