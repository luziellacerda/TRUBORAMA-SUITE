using System.Buffers.Binary;
using System.ComponentModel;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace TurboBoxManager;

/// <summary>
/// Pins a physical Windows path by keeping the volume root and every directory
/// ancestor open without FILE_SHARE_DELETE.  All security-sensitive leaf
/// operations are performed through the validated handle, never by re-resolving
/// a pathname after releasing the directory identities.
/// </summary>
internal static class PathIdentity
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileListDirectory = 0x00000001;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint CreateNew = 1;
    private const uint OpenExisting = 3;
    private const uint OpenAlways = 4;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const int FileAttributeTagInfo = 9;
    private const int FileIdInfo = 18;
    private const int FileRenameInfo = 3;
    private const int FileDispositionInfo = 4;
    private const int FileDispositionInfoEx = 21;
    private const int FileRenameInfoEx = 22;
    private const int FileCaseSensitiveInfo = 23;
    private const uint FileRenameFlagReplaceIfExists = 0x00000001;
    private const uint FileRenameFlagPosixSemantics = 0x00000002;
    private const uint FileDispositionFlagDelete = 0x00000001;
    private const uint FileDispositionFlagPosixSemantics = 0x00000002;
    private const uint FileDispositionFlagIgnoreReadonlyAttribute = 0x00000010;
    private const uint FileCaseSensitiveDir = 0x00000001;
    private const uint VolumeNameNt = 0x00000002;
    private const uint AllowedFileOptionFlags =
        unchecked((uint)(int)FileOptions.WriteThrough)
        | unchecked((uint)(int)FileOptions.Asynchronous)
        | unchecked((uint)(int)FileOptions.RandomAccess)
        | unchecked((uint)(int)FileOptions.SequentialScan);
    private const uint SecurityDescriptorRevision = 1;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorInvalidParameter = 87;
    private const int ErrorNotSupported = 50;
    private const int ErrorCallNotImplemented = 120;

    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
    private static int _outstandingDirectoryHandles;
    private static readonly ConcurrentDictionary<nint, string> OutstandingDirectoryPaths = [];

    internal static int OutstandingDirectoryHandles =>
        Volatile.Read(ref _outstandingDirectoryHandles);

    internal static string OutstandingDirectoryHandlePaths => string.Join(
        " | ",
        OutstandingDirectoryPaths.Values.Distinct(PathComparer).OrderBy(path => path));

    internal sealed class DirectoryTreeLease : IDisposable
    {
        private readonly Dictionary<string, DirectoryRecord> _directories =
            new(PathComparer);
        private readonly List<DirectoryRecord> _ownershipOrder = [];
        private readonly Dictionary<string, FileRecord> _retainedFiles =
            new(PathComparer);
        private bool _disposed;

        private DirectoryTreeLease(string anchorPath)
        {
            AnchorPath = anchorPath;
        }

        internal string AnchorPath { get; }

        internal SafeFileHandle AnchorHandle => GetDirectoryHandle(AnchorPath);

        internal static DirectoryTreeLease Open(
            string directoryPath,
            bool createIfMissing = false,
            bool privateLeaf = false,
            bool leafDeleteAccess = false)
        {
            EnsureWindows();
            var canonical = Canonicalize(directoryPath);
            var lease = new DirectoryTreeLease(canonical);
            try
            {
                lease.OpenPathFromVolume(
                    canonical,
                    createIfMissing,
                    privateLeaf,
                    requireNewLeaf: false,
                    leafDeleteAccess);
                return lease;
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }

        internal SafeFileHandle EnsureDirectory(
            string directoryPath,
            bool privateLeaf = false,
            bool requireNewLeaf = false,
            bool leafDeleteAccess = false)
        {
            ThrowIfDisposed();
            var canonical = Canonicalize(directoryPath);
            EnsureWithin(canonical, AnchorPath);
            OpenPathFromVolume(
                canonical,
                createIfMissing: true,
                privateLeaf,
                requireNewLeaf,
                leafDeleteAccess);
            return GetDirectoryHandle(canonical);
        }

        internal SafeFileHandle GetDirectoryHandle(string directoryPath)
        {
            ThrowIfDisposed();
            var canonical = Canonicalize(directoryPath);
            EnsureWithin(canonical, AnchorPath);
            if (!_directories.TryGetValue(canonical, out var record))
                throw new InvalidOperationException(
                    $"O diretório '{canonical}' não está retido pelo lease de identidade.");
            ValidateRecord(record, probePath: !record.HasDeleteAccess);
            return record.Handle;
        }

        internal FileStream OpenFile(
            string filePath,
            FileMode mode,
            FileAccess access,
            FileShare share,
            int bufferSize,
            FileOptions options,
            bool deleteAccess = false,
            bool requireSingleLink = true)
        {
            ThrowIfDisposed();
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferSize);
            var nativeFileOptions = unchecked((uint)(int)options);
            if ((nativeFileOptions & ~AllowedFileOptionFlags) != 0)
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    options,
                    "A opção solicitada pode alterar ou ocultar o ciclo de vida do arquivo protegido.");
            if ((options & (FileOptions.RandomAccess | FileOptions.SequentialScan))
                == (FileOptions.RandomAccess | FileOptions.SequentialScan))
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    options,
                    "RandomAccess e SequentialScan não podem ser combinados.");
            if (mode == FileMode.Append && (access & FileAccess.Write) == 0)
                throw new ArgumentOutOfRangeException(
                    nameof(access),
                    access,
                    "Append exige acesso de escrita.");
            var canonical = Canonicalize(filePath);
            EnsureWithin(canonical, AnchorPath);
            var parent = Path.GetDirectoryName(canonical)
                         ?? throw new InvalidDataException("O arquivo não possui diretório-pai.");
            if (!_directories.TryGetValue(Canonicalize(parent), out var parentRecord))
                throw new InvalidOperationException(
                    "O diretório-pai precisa permanecer retido durante a operação de arquivo.");
            ValidateRecord(parentRecord, probePath: !parentRecord.HasDeleteAccess);

            var desiredAccess = access switch
            {
                FileAccess.Read => GenericRead,
                FileAccess.Write => GenericWrite,
                FileAccess.ReadWrite => GenericRead | GenericWrite,
                _ => throw new ArgumentOutOfRangeException(nameof(access))
            };
            if (deleteAccess) desiredAccess |= DeleteAccess;
            var nativeShare = ToNativeShare(share & ~FileShare.Delete);
            var disposition = mode switch
            {
                FileMode.CreateNew => CreateNew,
                FileMode.Open => OpenExisting,
                FileMode.OpenOrCreate => OpenAlways,
                FileMode.Append => OpenAlways,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(mode),
                    "O modo solicitado pode alterar o arquivo antes da validação de identidade.")
            };
            var nativeOptions = FileAttributeNormal
                                | FileFlagOpenReparsePoint
                                | nativeFileOptions;
            var handle = CreateFileW(
                ToExtendedPath(canonical),
                desiredAccess,
                nativeShare,
                IntPtr.Zero,
                disposition,
                nativeOptions,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                ThrowIo(error, $"Não foi possível abrir com segurança '{canonical}'.");
            }

            try
            {
                var identity = CaptureAndValidate(
                    handle,
                    canonical,
                    expectDirectory: false,
                    requireSingleLink);
                ValidateDirectChild(parentRecord.Identity, identity, Path.GetFileName(canonical));
                ValidateRecord(parentRecord, probePath: !parentRecord.HasDeleteAccess);
                var stream = new FileStream(
                    handle,
                    access,
                    bufferSize,
                    (options & FileOptions.Asynchronous) != 0);
                if (mode == FileMode.Append) stream.Position = stream.Length;
                return stream;
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        internal void Revalidate()
        {
            ThrowIfDisposed();
            foreach (var record in _ownershipOrder)
                ValidateRecord(record, probePath: !record.HasDeleteAccess);
            foreach (var record in _retainedFiles.Values)
            {
                var current = CaptureAndValidate(
                    record.Handle,
                    record.Path,
                    expectDirectory: false,
                    requireSingleLink: true);
                if (!current.SameObject(record.Identity)
                    || current.Attributes != record.Identity.Attributes
                    || current.ReparseTag != record.Identity.ReparseTag
                    || current.LinkCount != record.Identity.LinkCount)
                    throw new IOException($"A identidade do arquivo '{record.Path}' mudou.");
            }
        }

        internal void RetainFile(
            SafeFileHandle sourceHandle,
            string filePath,
            HandleIdentity identity)
        {
            ThrowIfDisposed();
            var canonical = Canonicalize(filePath);
            EnsureWithin(canonical, AnchorPath);
            if (_retainedFiles.ContainsKey(canonical))
                throw new InvalidOperationException("O arquivo já está retido pelo lease.");
            var parentPath = Canonicalize(
                Path.GetDirectoryName(canonical)
                ?? throw new InvalidDataException("O arquivo retido não possui diretório-pai."));
            if (!_directories.TryGetValue(parentPath, out var parentRecord))
                throw new InvalidOperationException(
                    "O diretório-pai do arquivo retido precisa permanecer aberto.");
            ValidateRecord(parentRecord, probePath: !parentRecord.HasDeleteAccess);
            var duplicate = Duplicate(sourceHandle);
            try
            {
                var duplicateIdentity = CaptureAndValidate(
                    duplicate,
                    canonical,
                    expectDirectory: false,
                    requireSingleLink: true);
                if (!duplicateIdentity.SameObject(identity))
                    throw new IOException("A duplicação do handle perdeu a identidade do arquivo.");
                ValidateDirectChild(
                    parentRecord.Identity,
                    duplicateIdentity,
                    Path.GetFileName(canonical));
                ValidateRecord(parentRecord, probePath: !parentRecord.HasDeleteAccess);
                _retainedFiles.Add(canonical, new FileRecord(canonical, duplicate, identity));
            }
            catch
            {
                duplicate.Dispose();
                throw;
            }
        }

        internal FileStream OpenRetainedFileForRead(
            string filePath,
            int bufferSize,
            bool asynchronous)
        {
            ThrowIfDisposed();
            var canonical = Canonicalize(filePath);
            if (!_retainedFiles.TryGetValue(canonical, out var record))
                throw new InvalidOperationException("O arquivo não está retido pelo lease.");
            var current = CaptureAndValidate(
                record.Handle,
                canonical,
                expectDirectory: false,
                requireSingleLink: true);
            if (!current.SameObject(record.Identity))
                throw new IOException("A identidade do arquivo retido mudou.");
            var duplicate = Duplicate(record.Handle);
            try
            {
                return new FileStream(duplicate, FileAccess.Read, bufferSize, asynchronous);
            }
            catch
            {
                duplicate.Dispose();
                throw;
            }
        }

        internal bool HasRetainedFile(string filePath)
        {
            ThrowIfDisposed();
            return _retainedFiles.ContainsKey(Canonicalize(filePath));
        }

        internal SubtreeRenameTransition PrepareSubtreeForRename(string subtreeRoot)
        {
            ThrowIfDisposed();
            var canonicalRoot = Canonicalize(subtreeRoot);
            EnsureWithin(canonicalRoot, AnchorPath);
            if (!_directories.ContainsKey(canonicalRoot))
                throw new InvalidOperationException("A raiz a publicar não está retida.");
            Revalidate();

            var transition = new SubtreeRenameTransition(canonicalRoot);
            try
            {
                foreach (var record in _retainedFiles.Values
                             .Where(record => IsWithinOrEqual(record.Path, canonicalRoot)))
                    transition.Add(OpenTransition(record.Path, record.Identity, isDirectory: false));
                foreach (var record in _ownershipOrder
                             .Where(record => !PathComparer.Equals(record.Path, canonicalRoot)
                                              && IsWithinOrEqual(record.Path, canonicalRoot)))
                    transition.Add(OpenTransition(record.Path, record.Identity, isDirectory: true));

                foreach (var path in _retainedFiles.Keys
                             .Where(path => IsWithinOrEqual(path, canonicalRoot))
                             .ToArray())
                {
                    _retainedFiles[path].Handle.Dispose();
                    _retainedFiles.Remove(path);
                }
                for (var index = _ownershipOrder.Count - 1; index >= 0; index--)
                {
                    var record = _ownershipOrder[index];
                    if (PathComparer.Equals(record.Path, canonicalRoot)
                        || !IsWithinOrEqual(record.Path, canonicalRoot))
                        continue;
                    _ = OutstandingDirectoryPaths.TryRemove(
                        record.Handle.DangerousGetHandle(),
                        out _);
                    record.Handle.Dispose();
                    _ = Interlocked.Decrement(ref _outstandingDirectoryHandles);
                    _ownershipOrder.RemoveAt(index);
                    _directories.Remove(record.Path);
                }
                return transition;
            }
            catch
            {
                transition.Dispose();
                throw;
            }
        }

        internal void ReleaseDirectoryAfterRename(string oldPath)
        {
            ThrowIfDisposed();
            var canonical = Canonicalize(oldPath);
            EnsureWithin(canonical, AnchorPath);
            if (!_directories.TryGetValue(canonical, out var record))
                throw new InvalidOperationException("O diretório renomeado não está retido.");
            _ = OutstandingDirectoryPaths.TryRemove(record.Handle.DangerousGetHandle(), out _);
            record.Handle.Dispose();
            _ = Interlocked.Decrement(ref _outstandingDirectoryHandles);
            _directories.Remove(canonical);
            _ownershipOrder.Remove(record);
        }

        internal static TransitionEntry OpenTransition(
            string path,
            HandleIdentity expected,
            bool isDirectory)
        {
            var flags = FileFlagOpenReparsePoint
                        | (isDirectory ? FileFlagBackupSemantics : FileAttributeNormal);
            var handle = CreateFileW(
                ToExtendedPath(path),
                FileReadAttributes,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                OpenExisting,
                flags,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                ThrowIo(error, $"Não foi possível preparar '{path}' para publicação.");
            }
            try
            {
                var current = CaptureAndValidate(
                    handle,
                    path,
                    isDirectory,
                    requireSingleLink: !isDirectory);
                if (!current.SameObject(expected))
                    throw new IOException("A identidade mudou ao preparar a publicação.");
                return new TransitionEntry(path, handle, expected, isDirectory);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var record in _retainedFiles.Values)
                record.Handle.Dispose();
            _retainedFiles.Clear();
            for (var index = _ownershipOrder.Count - 1; index >= 0; index--)
            {
                _ = OutstandingDirectoryPaths.TryRemove(
                    _ownershipOrder[index].Handle.DangerousGetHandle(),
                    out _);
                _ownershipOrder[index].Handle.Dispose();
                _ = Interlocked.Decrement(ref _outstandingDirectoryHandles);
            }
            _ownershipOrder.Clear();
            _directories.Clear();
        }

        private void OpenPathFromVolume(
            string canonicalPath,
            bool createIfMissing,
            bool privateLeaf,
            bool requireNewLeaf,
            bool leafDeleteAccess)
        {
            var volumeRoot = Path.GetPathRoot(canonicalPath);
            if (string.IsNullOrWhiteSpace(volumeRoot))
                throw new InvalidDataException("O caminho não possui volume ou compartilhamento físico.");
            volumeRoot = Canonicalize(volumeRoot);
            var relative = Path.GetRelativePath(volumeRoot, canonicalPath);
            var segments = relative.Equals(".", StringComparison.Ordinal)
                ? Array.Empty<string>()
                : relative.Split(
                    [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                    StringSplitOptions.RemoveEmptyEntries);

            var current = volumeRoot;
            OpenOneDirectory(
                current,
                create: false,
                privateAcl: false,
                requireNew: false,
                deleteAccess: leafDeleteAccess && segments.Length == 0);
            for (var index = 0; index < segments.Length; index++)
            {
                current = Canonicalize(Path.Combine(current, segments[index]));
                var isLeaf = index == segments.Length - 1;
                OpenOneDirectory(
                    current,
                    createIfMissing,
                    privateLeaf && isLeaf,
                    requireNewLeaf && isLeaf,
                    leafDeleteAccess && isLeaf);
            }
        }

        private void OpenOneDirectory(
            string path,
            bool create,
            bool privateAcl,
            bool requireNew,
            bool deleteAccess)
        {
            if (_directories.TryGetValue(path, out var existing))
            {
                if (requireNew)
                    throw new IOException($"O diretório seguro '{path}' já existe.");
                if (deleteAccess && !existing.HasDeleteAccess)
                    throw new InvalidOperationException(
                        "O diretório já foi aberto sem DELETE e não pode ser promovido sem liberar sua identidade.");
                ValidateRecord(existing, probePath: !existing.HasDeleteAccess);
                return;
            }

            DirectoryRecord? parentRecord = null;
            var volumeRoot = Canonicalize(
                Path.GetPathRoot(path)
                ?? throw new InvalidDataException("O diretório não possui uma raiz física."));
            if (!PathComparer.Equals(path, volumeRoot))
            {
                var parentPath = Canonicalize(
                    Path.GetDirectoryName(path)
                    ?? throw new InvalidDataException("O diretório não possui pai físico."));
                if (!_directories.TryGetValue(parentPath, out parentRecord))
                    throw new InvalidOperationException(
                        "O diretório-pai precisa permanecer retido durante a abertura do descendente.");
                ValidateRecord(parentRecord, probePath: !parentRecord.HasDeleteAccess);
            }

            if (create)
            {
                var created = privateAcl
                    ? CreatePrivateDirectory(path)
                    : CreateDirectoryW(ToExtendedPath(path), IntPtr.Zero);
                if (!created)
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (requireNew || error is not 183 and not 80)
                        ThrowIo(error, $"Não foi possível criar o diretório seguro '{path}'.");
                }
            }

            var desiredAccess = FileReadAttributes | FileListDirectory;
            if (deleteAccess) desiredAccess |= DeleteAccess;
            var handle = CreateFileW(
                ToExtendedPath(path),
                desiredAccess,
                FileShareRead | FileShareWrite,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                ThrowIo(error, $"Não foi possível reter o diretório físico '{path}'.");
            }

            try
            {
                var identity = CaptureAndValidate(
                    handle,
                    path,
                    expectDirectory: true,
                    requireSingleLink: false);
                if (parentRecord is not null)
                {
                    ValidateDirectChild(parentRecord.Identity, identity, Path.GetFileName(path));
                    ValidateRecord(parentRecord, probePath: !parentRecord.HasDeleteAccess);
                }
                var record = new DirectoryRecord(path, handle, identity, deleteAccess);
                _directories.Add(path, record);
                _ownershipOrder.Add(record);
                OutstandingDirectoryPaths[handle.DangerousGetHandle()] = path;
                _ = Interlocked.Increment(ref _outstandingDirectoryHandles);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    }

    internal readonly record struct HandleIdentity(
        ulong VolumeSerialNumber,
        byte[] FileId,
        uint Attributes,
        uint ReparseTag,
        uint LinkCount,
        string FinalPath,
        string FinalNtPath)
    {
        internal bool SameObject(HandleIdentity other) =>
            VolumeSerialNumber == other.VolumeSerialNumber
            && FileId.AsSpan().SequenceEqual(other.FileId);
    }

    internal sealed class SubtreeRenameTransition : IDisposable
    {
        private readonly string _oldRoot;
        private readonly List<TransitionEntry> _entries = [];
        private bool _disposed;

        internal SubtreeRenameTransition(string oldRoot)
        {
            _oldRoot = oldRoot;
        }

        internal void Add(TransitionEntry entry) => _entries.Add(entry);

        internal void ValidateRenamedSubtree(string newRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var canonicalNewRoot = Canonicalize(newRoot);
            foreach (var entry in _entries)
            {
                var relative = Path.GetRelativePath(_oldRoot, entry.OldPath);
                var expectedPath = Canonicalize(Path.Combine(canonicalNewRoot, relative));
                var current = CaptureAndValidate(
                    entry.Handle,
                    expectedPath,
                    entry.IsDirectory,
                    requireSingleLink: !entry.IsDirectory);
                if (!current.SameObject(entry.Identity))
                    throw new IOException("Um descendente mudou de identidade durante a publicação.");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            for (var index = _entries.Count - 1; index >= 0; index--)
                _entries[index].Handle.Dispose();
            _entries.Clear();
        }
    }

    internal static DirectoryTreeLease OpenDirectoryTree(
        string directoryPath,
        bool createIfMissing = false,
        bool privateLeaf = false,
        bool leafDeleteAccess = false) => DirectoryTreeLease.Open(
        directoryPath,
        createIfMissing,
        privateLeaf,
        leafDeleteAccess);

    internal static HandleIdentity CaptureFileIdentity(
        SafeFileHandle handle,
        string expectedPath,
        bool requireSingleLink = true) => CaptureAndValidate(
        handle,
        Canonicalize(expectedPath),
        expectDirectory: false,
        requireSingleLink);

    internal static HandleIdentity CaptureDirectoryIdentity(
        SafeFileHandle handle,
        string expectedPath) => CaptureAndValidate(
        handle,
        Canonicalize(expectedPath),
        expectDirectory: true,
        requireSingleLink: false);

    internal static HandleIdentity RevalidateFile(
        SafeFileHandle handle,
        string expectedPath,
        HandleIdentity expected,
        bool requireSingleLink = true)
    {
        var current = CaptureFileIdentity(handle, expectedPath, requireSingleLink);
        if (!current.SameObject(expected)
            || current.Attributes != expected.Attributes
            || current.ReparseTag != expected.ReparseTag
            || current.LinkCount != expected.LinkCount)
            throw new IOException("A identidade do arquivo mudou durante a operação.");
        return current;
    }

    internal static HandleIdentity RenameByHandle(
        SafeFileHandle sourceHandle,
        HandleIdentity sourceIdentity,
        SafeFileHandle destinationParentHandle,
        string destinationParentPath,
        string destinationLeafName,
        bool replaceIfExists)
    {
        EnsureWindows();
        ValidateLeafName(destinationLeafName);
        var canonicalDestination = Canonicalize(
            Path.Combine(Canonicalize(destinationParentPath), destinationLeafName));
        var parentIdentity = CaptureAndValidate(
            destinationParentHandle,
            Canonicalize(destinationParentPath),
            expectDirectory: true,
            requireSingleLink: false);
        var physicalDestination = @"\??\GLOBALROOT"
                                  + parentIdentity.FinalNtPath.TrimEnd(
                                      Path.DirectorySeparatorChar)
                                  + Path.DirectorySeparatorChar
                                  + destinationLeafName;

        var flags = FileRenameFlagPosixSemantics
                    | (replaceIfExists ? FileRenameFlagReplaceIfExists : 0u);
        var error = SetRenameInformation(
            sourceHandle,
            destinationParentHandle,
            destinationLeafName,
            flags,
            FileRenameInfoEx);
        if (error is ErrorInvalidParameter or ErrorNotSupported or ErrorCallNotImplemented)
        {
            error = SetRenameInformation(
                sourceHandle,
                destinationParentHandle,
                destinationLeafName,
                replaceIfExists ? 1u : 0u,
                FileRenameInfo);
        }
        if (error is ErrorInvalidParameter or ErrorNotSupported or ErrorCallNotImplemented)
        {
            // Some filesystems reject RootDirectory even though they implement
            // FILE_RENAME_INFO. Fall back to the kernel path obtained from the
            // already pinned parent handle, never to a mutable DOS/UNC alias.
            error = SetRenameInformation(
                sourceHandle,
                destinationParent: null,
                physicalDestination,
                flags,
                FileRenameInfoEx);
        }
        if (error is ErrorInvalidParameter or ErrorNotSupported or ErrorCallNotImplemented)
        {
            error = SetRenameInformation(
                sourceHandle,
                destinationParent: null,
                physicalDestination,
                replaceIfExists ? 1u : 0u,
                FileRenameInfo);
        }
        if (error != 0)
            ThrowIo(error, "A publicação atômica por identidade falhou.");

        var renamed = CaptureAndValidate(
            sourceHandle,
            canonicalDestination,
            (sourceIdentity.Attributes & (uint)FileAttributes.Directory) != 0,
            requireSingleLink: (sourceIdentity.Attributes & (uint)FileAttributes.Directory) == 0);
        if (!renamed.SameObject(sourceIdentity))
            throw new IOException("O rename publicou um objeto diferente do validado.");
        var parentAfter = CaptureAndValidate(
            destinationParentHandle,
            Canonicalize(destinationParentPath),
            expectDirectory: true,
            requireSingleLink: false);
        if (!parentAfter.SameObject(parentIdentity))
            throw new IOException("O diretório de publicação mudou durante o rename.");
        ValidateDirectChild(parentAfter, renamed, destinationLeafName);
        return renamed;
    }

    internal static void DeleteByHandle(
        SafeFileHandle handle,
        string expectedPath,
        HandleIdentity expected,
        bool isDirectory = false)
    {
        var current = CaptureAndValidate(
            handle,
            Canonicalize(expectedPath),
            isDirectory,
            requireSingleLink: !isDirectory);
        if (!current.SameObject(expected))
            throw new IOException("A exclusão foi recusada porque a identidade mudou.");

        var flags = FileDispositionFlagDelete
                    | FileDispositionFlagPosixSemantics
                    | FileDispositionFlagIgnoreReadonlyAttribute;
        Span<byte> exBuffer = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(exBuffer, flags);
        var error = SetInformation(handle, FileDispositionInfoEx, exBuffer);
        if (error is ErrorInvalidParameter or ErrorNotSupported or ErrorCallNotImplemented)
        {
            Span<byte> fallback = stackalloc byte[1];
            fallback[0] = 1; // FILE_DISPOSITION_INFO.DeleteFile is BOOLEAN.
            error = SetInformation(handle, FileDispositionInfo, fallback);
        }
        if (error != 0)
            ThrowIo(error, "Não foi possível excluir o objeto validado por handle.");
    }

    internal static bool DeleteFileExact(string filePath, string allowedRoot)
    {
        var canonical = Canonicalize(filePath);
        var canonicalRoot = Canonicalize(allowedRoot);
        EnsureWithin(canonical, canonicalRoot);
        var parent = Path.GetDirectoryName(canonical)
                     ?? throw new InvalidDataException("O arquivo não possui diretório-pai.");
        using var directories = OpenDirectoryTree(parent);
        FileStream? stream = null;
        try
        {
            stream = directories.OpenFile(
                canonical,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                1,
                FileOptions.None,
                deleteAccess: true,
                requireSingleLink: true);
        }
        catch (IOException exception) when (IsMissing(exception))
        {
            return false;
        }

        using (stream)
        {
            var identity = CaptureFileIdentity(stream.SafeFileHandle, canonical);
            directories.Revalidate();
            DeleteByHandle(stream.SafeFileHandle, canonical, identity);
        }
        directories.Revalidate();
        return true;
    }

    internal static bool DeleteDirectoryTreeExact(
        string directoryPath,
        string allowedRoot,
        int maximumEntries = 200_000)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEntries);
        var canonical = Canonicalize(directoryPath);
        var root = Canonicalize(allowedRoot);
        EnsureWithin(canonical, root);
        if (!Directory.Exists(canonical)) return false;
        var visited = 0;
        DeleteDirectoryTreeCore(canonical, ref visited, maximumEntries);
        return true;
    }

    private static void DeleteDirectoryTreeCore(
        string directoryPath,
        ref int visited,
        int maximumEntries)
    {
        using var directoryLease = OpenDirectoryTree(directoryPath);
        foreach (var child in Directory.EnumerateFileSystemEntries(directoryPath))
        {
            visited = checked(visited + 1);
            if (visited > maximumEntries)
                throw new InvalidDataException("A limpeza segura excedeu o limite de entradas.");
            var attributes = File.GetAttributes(child);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException(
                    "A limpeza foi interrompida diante de um reparse point.");
            if ((attributes & FileAttributes.Directory) != 0)
                DeleteDirectoryTreeCore(child, ref visited, maximumEntries);
            else
                _ = DeleteFileExact(child, directoryPath);
        }

        directoryLease.Revalidate();
        var directoryHandle = directoryLease.AnchorHandle;
        var identity = CaptureDirectoryIdentity(directoryHandle, directoryPath);
        var parentPath = Path.GetDirectoryName(directoryPath)
                         ?? throw new InvalidDataException(
                             "A raiz de volume não pode ser removida por este helper.");
        using var parentLease = OpenDirectoryTree(parentPath);
        using var transition = DirectoryTreeLease.OpenTransition(
            directoryPath,
            identity,
            isDirectory: true).Handle;
        directoryLease.Dispose();

        using var deleteHandle = CreateFileW(
            ToExtendedPath(directoryPath),
            FileReadAttributes | DeleteAccess,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (deleteHandle.IsInvalid)
            ThrowIo(
                Marshal.GetLastPInvokeError(),
                $"Não foi possível abrir o diretório validado para exclusão '{directoryPath}'.");
        var deleteIdentity = CaptureDirectoryIdentity(deleteHandle, directoryPath);
        if (!deleteIdentity.SameObject(identity))
            throw new IOException("O diretório mudou durante o handoff de exclusão.");
        parentLease.Revalidate();
        DeleteByHandle(deleteHandle, directoryPath, deleteIdentity, isDirectory: true);
        parentLease.Revalidate();
    }

    internal static string Canonicalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("O caminho não pode ser vazio.", nameof(path));
        var ordinary = FromExtendedPath(path);
        if (!Path.IsPathFullyQualified(ordinary))
            throw new ArgumentException("O caminho precisa ser absoluto.", nameof(path));
        var canonical = Path.GetFullPath(ordinary);
        var root = Path.GetPathRoot(canonical);
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidDataException("O caminho não possui uma raiz física.");
        if (canonical.AsSpan(root.Length).Contains(':'))
            throw new InvalidDataException("Alternate data streams não são aceitos em caminhos protegidos.");
        return canonical.Length == root.Length
            ? root
            : Path.TrimEndingDirectorySeparator(canonical);
    }

    internal static string ToExtendedPath(string path)
    {
        var canonical = Canonicalize(path);
        if (canonical.StartsWith(@"\\", StringComparison.Ordinal))
            return @"\\?\UNC\" + canonical[2..];
        return @"\\?\" + canonical;
    }

    private static string FromExtendedPath(string path)
    {
        if (path.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(@"\??\", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(@"\\??\", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(@"\\?\GLOBALROOT\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Caminhos de dispositivo não são aceitos.");
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            return @"\\" + path[8..];
        if (path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
            return path[4..];
        return path;
    }

    private static void EnsureWithin(string candidatePath, string rootPath)
    {
        var candidate = Canonicalize(candidatePath);
        var root = Canonicalize(rootPath);
        if (PathComparer.Equals(candidate, root)) return;
        var prefix = Path.EndsInDirectorySeparator(root)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("O caminho físico saiu da raiz retida.");
    }

    private static HandleIdentity CaptureAndValidate(
        SafeFileHandle handle,
        string expectedPath,
        bool expectDirectory,
        bool requireSingleLink)
    {
        ObjectDisposedException.ThrowIf(handle.IsInvalid || handle.IsClosed, handle);

        var tagBytes = new byte[8];
        if (!GetFileInformationByHandleEx(
                handle,
                FileAttributeTagInfo,
                tagBytes,
                (uint)tagBytes.Length))
            ThrowIo(Marshal.GetLastPInvokeError(), "Não foi possível ler atributos pelo handle.");
        var attributes = BinaryPrimitives.ReadUInt32LittleEndian(tagBytes);
        var tag = BinaryPrimitives.ReadUInt32LittleEndian(tagBytes.AsSpan(4));
        var isDirectory = (attributes & (uint)FileAttributes.Directory) != 0;
        if (isDirectory != expectDirectory)
            throw new InvalidDataException("O tipo do objeto físico não corresponde ao esperado.");
        if ((attributes & (uint)FileAttributes.ReparsePoint) != 0 || tag != 0)
            throw new InvalidDataException("Links, junctions e outros reparse points não são aceitos.");
        if (isDirectory)
        {
            var caseSensitiveBytes = new byte[sizeof(uint)];
            if (!GetFileInformationByHandleEx(
                    handle,
                    FileCaseSensitiveInfo,
                    caseSensitiveBytes,
                    (uint)caseSensitiveBytes.Length))
                ThrowIo(
                    Marshal.GetLastPInvokeError(),
                    "Não foi possível confirmar a política de maiúsculas/minúsculas do diretório.");
            var caseSensitiveFlags = BinaryPrimitives.ReadUInt32LittleEndian(caseSensitiveBytes);
            if ((caseSensitiveFlags & FileCaseSensitiveDir) != 0)
                throw new InvalidDataException(
                    "Diretórios NTFS sensíveis a maiúsculas/minúsculas não são aceitos por caminhos protegidos.");
        }

        var idBytes = new byte[24];
        if (!GetFileInformationByHandleEx(
                handle,
                FileIdInfo,
                idBytes,
                (uint)idBytes.Length))
            ThrowIo(Marshal.GetLastPInvokeError(), "Não foi possível ler FILE_ID_INFO pelo handle.");
        var volume = BinaryPrimitives.ReadUInt64LittleEndian(idBytes);
        var fileId = idBytes.AsSpan(8, 16).ToArray();

        if (!GetFileInformationByHandle(handle, out var basic))
            ThrowIo(Marshal.GetLastPInvokeError(), "Não foi possível ler a identidade básica do handle.");
        if (!isDirectory && requireSingleLink && basic.NumberOfLinks != 1)
            throw new InvalidDataException("Hardlinks não são aceitos para arquivos mutáveis.");

        var finalPath = GetFinalPath(handle);
        var finalNtPath = GetFinalNtPath(handle);
        var canonicalExpected = Canonicalize(expectedPath);
        if (!PathComparer.Equals(finalPath, canonicalExpected))
            throw new InvalidDataException(
                $"O caminho final do handle divergiu do caminho autorizado: '{finalPath}'.");
        return new HandleIdentity(
            volume,
            fileId,
            attributes,
            tag,
            basic.NumberOfLinks,
            finalPath,
            finalNtPath);
    }

    private static void ValidateRecord(DirectoryRecord record, bool probePath)
    {
        var current = CaptureAndValidate(
            record.Handle,
            record.Path,
            expectDirectory: true,
            requireSingleLink: false);
        if (!current.SameObject(record.Identity)
            || current.Attributes != record.Identity.Attributes
            || current.ReparseTag != record.Identity.ReparseTag)
            throw new IOException($"A identidade do ancestral '{record.Path}' mudou.");
        if (!probePath) return;

        using var probe = CreateFileW(
            ToExtendedPath(record.Path),
            FileReadAttributes,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (probe.IsInvalid)
            ThrowIo(Marshal.GetLastPInvokeError(), $"Não foi possível revalidar '{record.Path}'.");
        var probeIdentity = CaptureAndValidate(
            probe,
            record.Path,
            expectDirectory: true,
            requireSingleLink: false);
        if (!probeIdentity.SameObject(record.Identity))
            throw new IOException($"O pathname do ancestral '{record.Path}' sofreu troca de identidade.");
    }

    private static string GetFinalPath(SafeFileHandle handle)
        => ReadFinalPath(handle, flags: 0, canonicalizeDosPath: true);

    private static string GetFinalNtPath(SafeFileHandle handle)
        => ReadFinalPath(handle, VolumeNameNt, canonicalizeDosPath: false);

    private static string ReadFinalPath(
        SafeFileHandle handle,
        uint flags,
        bool canonicalizeDosPath)
    {
        var capacity = 512;
        while (capacity <= 32768)
        {
            var buffer = new char[capacity];
            var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, flags);
            if (length == 0)
                ThrowIo(Marshal.GetLastPInvokeError(), "Não foi possível obter o caminho final do handle.");
            if (length < buffer.Length)
            {
                var value = new string(buffer, 0, checked((int)length));
                if (canonicalizeDosPath) return Canonicalize(value);
                if (!value.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase)
                    || value.Contains('\0'))
                    throw new InvalidDataException(
                        "O caminho físico NT do handle possui formato inesperado.");
                return value.Length > @"\Device\".Length
                    ? value.TrimEnd(Path.DirectorySeparatorChar)
                    : value;
            }
            capacity = checked((int)length + 1);
        }
        throw new PathTooLongException("O caminho final do handle excede o limite do Windows.");
    }

    private static void ValidateDirectChild(
        HandleIdentity parent,
        HandleIdentity child,
        string expectedLeafName)
    {
        ValidateLeafName(expectedLeafName);
        if (parent.VolumeSerialNumber != child.VolumeSerialNumber)
            throw new InvalidDataException(
                "O descendente físico foi aberto em outro volume durante a operação.");
        var expectedNtPath = parent.FinalNtPath.TrimEnd(Path.DirectorySeparatorChar)
                             + Path.DirectorySeparatorChar
                             + expectedLeafName;
        if (!string.Equals(expectedNtPath, child.FinalNtPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "O handle aberto não é filho físico direto do diretório retido.");
    }

    private static int SetRenameInformation(
        SafeFileHandle source,
        SafeFileHandle? destinationParent,
        string leafName,
        uint flags,
        int informationClass)
    {
        var nameBytes = checked(leafName.Length * sizeof(char));
        var rootOffset = IntPtr.Size == 8 ? 8 : 4;
        var nameLengthOffset = rootOffset + IntPtr.Size;
        var nameOffset = nameLengthOffset + sizeof(uint);
        // Although FileNameLength excludes the terminator, several supported
        // Windows filesystems still inspect the trailing WCHAR. Supplying it
        // also prevents an unterminated relative name from consuming padding.
        var total = checked(nameOffset + nameBytes + sizeof(char));
        var buffer = Marshal.AllocHGlobal(total);
        var parentAddRef = false;
        try
        {
            destinationParent?.DangerousAddRef(ref parentAddRef);
            Marshal.Copy(new byte[total], 0, buffer, total);
            if (informationClass == FileRenameInfoEx)
                Marshal.WriteInt32(buffer, 0, unchecked((int)flags));
            else
                Marshal.WriteByte(buffer, 0, flags == 0 ? (byte)0 : (byte)1);
            Marshal.WriteIntPtr(
                buffer,
                rootOffset,
                destinationParent?.DangerousGetHandle() ?? IntPtr.Zero);
            Marshal.WriteInt32(buffer, nameLengthOffset, nameBytes);
            Marshal.Copy(leafName.ToCharArray(), 0, IntPtr.Add(buffer, nameOffset), leafName.Length);
            if (SetFileInformationByHandle(source, informationClass, buffer, (uint)total))
                return 0;
            return Marshal.GetLastPInvokeError();
        }
        finally
        {
            if (parentAddRef) destinationParent!.DangerousRelease();
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int SetInformation(
        SafeFileHandle handle,
        int informationClass,
        ReadOnlySpan<byte> data)
    {
        var bytes = data.ToArray();
        var buffer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, buffer, bytes.Length);
            if (SetFileInformationByHandle(
                    handle,
                    informationClass,
                    buffer,
                    (uint)bytes.Length))
                return 0;
            return Marshal.GetLastPInvokeError();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static SafeFileHandle Duplicate(SafeFileHandle source)
    {
        if (!DuplicateHandle(
                GetCurrentProcess(),
                source,
                GetCurrentProcess(),
                out var duplicate,
                desiredAccess: 0,
                inheritHandle: false,
                options: 0x00000002))
            ThrowIo(Marshal.GetLastPInvokeError(), "Não foi possível duplicar o handle retido.");
        return duplicate;
    }

    private static bool CreatePrivateDirectory(string path)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value
                  ?? throw new UnauthorizedAccessException(
                      "Não foi possível determinar o SID do usuário atual.");
        var sddl = $"O:{sid}D:P(A;;FA;;;{sid})(A;;FA;;;SY)(A;;FA;;;BA)";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
                sddl,
                SecurityDescriptorRevision,
                out var descriptor,
                out _))
            ThrowIo(Marshal.GetLastPInvokeError(), "Não foi possível criar a ACL privada do staging.");
        try
        {
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = descriptor,
                InheritHandle = 0
            };
            return CreateDirectoryW(ToExtendedPath(path), ref attributes);
        }
        finally
        {
            _ = LocalFree(descriptor);
        }
    }

    private static void ValidateLeafName(string leafName)
    {
        var baseName = Path.GetFileNameWithoutExtension(leafName);
        if (string.IsNullOrWhiteSpace(leafName)
            || leafName is "." or ".."
            || leafName.EndsWith(' ')
            || leafName.EndsWith('.')
            || leafName.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, ':']) >= 0
            || leafName.Any(character => Path.GetInvalidFileNameChars().Contains(character))
            || IsReservedDosDeviceName(baseName))
            throw new InvalidDataException("O nome relativo de publicação é inválido.");
    }

    private static bool IsReservedDosDeviceName(string baseName)
    {
        if (baseName.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || baseName.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || baseName.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || baseName.Equals("NUL", StringComparison.OrdinalIgnoreCase))
            return true;
        if (baseName.Length != 4) return false;
        return (baseName.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                || baseName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
               && baseName[3] is >= '1' and <= '9';
    }

    private static uint ToNativeShare(FileShare share)
    {
        uint result = 0;
        if ((share & FileShare.Read) != 0) result |= FileShareRead;
        if ((share & FileShare.Write) != 0) result |= FileShareWrite;
        // Deliberately never propagate FILE_SHARE_DELETE.
        return result;
    }

    private static bool IsMissing(IOException exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is Win32Exception win32
                && win32.NativeErrorCode is ErrorFileNotFound or ErrorPathNotFound)
                return true;
        }
        return false;
    }

    private static void ThrowIo(int error, string message)
    {
        var native = new Win32Exception(error);
        throw new IOException($"{message} (Win32 {error}: {native.Message})", native);
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("PathIdentity requer Windows NTFS/ReFS.");
    }

    private sealed record DirectoryRecord(
        string Path,
        SafeFileHandle Handle,
        HandleIdentity Identity,
        bool HasDeleteAccess);

    private sealed record FileRecord(
        string Path,
        SafeFileHandle Handle,
        HandleIdentity Identity);

    internal sealed record TransitionEntry(
        string OldPath,
        SafeFileHandle Handle,
        HandleIdentity Identity,
        bool IsDirectory);

    private static bool IsWithinOrEqual(string candidatePath, string rootPath)
    {
        var candidate = Canonicalize(candidatePath);
        var root = Canonicalize(rootPath);
        if (PathComparer.Equals(candidate, root)) return true;
        var prefix = Path.EndsInDirectorySeparator(root)
            ? root
            : root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(
            prefix,
            StringComparison.OrdinalIgnoreCase);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        internal uint FileAttributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        internal int Length;
        internal IntPtr SecurityDescriptor;
        internal int InheritHandle;
    }

#pragma warning disable SYSLIB1054
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode,
        ExactSpelling = true, SetLastError = true, BestFitMapping = false,
        ThrowOnUnmappableChar = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode,
        ExactSpelling = true, SetLastError = true, BestFitMapping = false,
        ThrowOnUnmappableChar = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string path, IntPtr securityAttributes);

    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode,
        ExactSpelling = true, SetLastError = true, BestFitMapping = false,
        ThrowOnUnmappableChar = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string path, ref SecurityAttributes securityAttributes);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        int informationClass,
        [Out] byte[] fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW",
        CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file,
        [Out] char[] filePath,
        uint filePathLength,
        uint flags);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        int informationClass,
        IntPtr fileInformation,
        uint bufferSize);

    [DllImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW",
        CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string stringSecurityDescriptor,
        uint stringSdRevision,
        out IntPtr securityDescriptor,
        out uint securityDescriptorSize);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(
        IntPtr sourceProcess,
        SafeFileHandle sourceHandle,
        IntPtr targetProcess,
        out SafeFileHandle targetHandle,
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint options);
#pragma warning restore SYSLIB1054
}
