using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TurboBoxManager.CatalogVerifier;

internal static class PathIdentityVerifier
{
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileWriteAttributes = 0x00000100;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const int FileCaseSensitiveInfo = 23;
    private const int ErrorAccessDenied = 5;
    private const int ErrorNotSupported = 50;
    private const int ErrorInvalidParameter = 87;

    internal static void Run(string testRoot)
    {
        var root = Path.GetFullPath(testRoot);
        Directory.CreateDirectory(root);
        VerifyAnchorBoundaryAndNonDestructiveModes(root);
        VerifyCaseSensitiveDirectoryIsRejected(root);
        if (PathIdentity.OutstandingDirectoryHandles != 0)
            throw new InvalidOperationException(
                $"PathIdentityVerifier deixou {PathIdentity.OutstandingDirectoryHandles} handles: "
                + PathIdentity.OutstandingDirectoryHandlePaths);
    }

    private static void VerifyAnchorBoundaryAndNonDestructiveModes(string root)
    {
        var anchor = Path.Combine(root, "anchor");
        var sibling = Path.Combine(root, "outside.bin");
        Directory.CreateDirectory(anchor);
        File.WriteAllBytes(sibling, [0x7A, 0x7B]);
        var protectedFile = Path.Combine(anchor, "protected.bin");
        var original = new byte[] { 0x10, 0x20, 0x30, 0x40 };
        File.WriteAllBytes(protectedFile, original);

        using var lease = PathIdentity.OpenDirectoryTree(anchor);
        ExpectRejected(
            () => lease.OpenFile(
                sibling,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1,
                FileOptions.None),
            "O lease aceitou um arquivo fora de sua raiz lógica.");

        foreach (var destructiveMode in new[] { FileMode.Create, FileMode.Truncate })
        {
            ExpectRejected(
                () => lease.OpenFile(
                    protectedFile,
                    destructiveMode,
                    FileAccess.ReadWrite,
                    FileShare.Read,
                    1,
                    FileOptions.None),
                $"O helper aceitou {destructiveMode}, que altera bytes antes da validação.");
            if (!File.ReadAllBytes(protectedFile).AsSpan().SequenceEqual(original))
                throw new InvalidDataException(
                    $"O modo rejeitado {destructiveMode} alterou o arquivo protegido.");
        }

        ExpectRejected(
            () => lease.OpenFile(
                protectedFile,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.Read,
                1,
                FileOptions.DeleteOnClose,
                deleteAccess: true),
            "O helper aceitou DeleteOnClose em um arquivo protegido.");
        if (!File.Exists(protectedFile)
            || !File.ReadAllBytes(protectedFile).AsSpan().SequenceEqual(original))
            throw new InvalidDataException(
                "A opção DeleteOnClose rejeitada alterou o arquivo protegido.");

        ExpectRejected(
            () => lease.OpenFile(
                protectedFile,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1,
                FileOptions.RandomAccess | FileOptions.SequentialScan),
            "O helper aceitou hints de acesso contraditórios.");

        using var stream = lease.OpenFile(
            protectedFile,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1,
            FileOptions.None);
        var parentIdentity = PathIdentity.CaptureDirectoryIdentity(lease.AnchorHandle, anchor);
        var childIdentity = PathIdentity.CaptureFileIdentity(
            stream.SafeFileHandle,
            protectedFile);
        var expectedNtPath = parentIdentity.FinalNtPath.TrimEnd(Path.DirectorySeparatorChar)
                             + Path.DirectorySeparatorChar
                             + Path.GetFileName(protectedFile);
        if (!string.Equals(expectedNtPath, childIdentity.FinalNtPath,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "A identidade NT não vinculou o arquivo ao diretório-pai retido.");

        var temporary = Path.Combine(anchor, "rename-source.tmp");
        var published = Path.Combine(anchor, "rename-target.tmp");
        using (var renameSource = lease.OpenFile(
                   temporary,
                   FileMode.CreateNew,
                   FileAccess.ReadWrite,
                   FileShare.Read,
                   1,
                   FileOptions.WriteThrough,
                   deleteAccess: true))
        {
            renameSource.WriteByte(0x51);
            renameSource.Flush(flushToDisk: true);
            var renameIdentity = PathIdentity.CaptureFileIdentity(
                renameSource.SafeFileHandle,
                temporary);
            ExpectRejectedOperation(
                () => PathIdentity.RenameByHandle(
                    renameSource.SafeFileHandle,
                    renameIdentity,
                    lease.AnchorHandle,
                    anchor,
                    "CON.txt",
                    replaceIfExists: false),
                "O rename aceitou um nome de dispositivo DOS reservado.");
            _ = PathIdentity.RenameByHandle(
                renameSource.SafeFileHandle,
                renameIdentity,
                lease.AnchorHandle,
                anchor,
                Path.GetFileName(published),
                replaceIfExists: false);
        }
        if (!File.Exists(published) || File.ReadAllBytes(published) is not [0x51])
            throw new InvalidDataException("O rename pelo pai físico não publicou o arquivo esperado.");
    }

    private static void VerifyCaseSensitiveDirectoryIsRejected(string root)
    {
        var caseSensitive = Path.Combine(root, "case-sensitive");
        Directory.CreateDirectory(caseSensitive);
        using var handle = CreateFileW(
            PathIdentity.ToExtendedPath(caseSensitive),
            FileReadAttributes | FileWriteAttributes,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
            throw new IOException(
                "Não foi possível abrir o diretório do teste case-sensitive.",
                new Win32Exception(Marshal.GetLastPInvokeError()));

        var enabled = new FileCaseSensitiveInformation { Flags = 1 };
        if (!SetFileInformationByHandle(
                handle,
                FileCaseSensitiveInfo,
                ref enabled,
                (uint)Marshal.SizeOf<FileCaseSensitiveInformation>()))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error is ErrorAccessDenied or ErrorNotSupported or ErrorInvalidParameter)
            {
                Console.WriteLine(
                    $"SKIP: filesystem não permitiu ativar case-sensitive no teste (Win32 {error}).");
                return;
            }
            throw new IOException(
                "Não foi possível ativar o diretório case-sensitive de controle.",
                new Win32Exception(error));
        }

        try
        {
            var rejected = false;
            try
            {
                using var forbidden = PathIdentity.OpenDirectoryTree(caseSensitive);
            }
            catch (InvalidDataException)
            {
                rejected = true;
            }
            if (!rejected)
                throw new InvalidDataException(
                    "PathIdentity aceitou um diretório NTFS sensível a maiúsculas/minúsculas.");
        }
        finally
        {
            var disabled = new FileCaseSensitiveInformation { Flags = 0 };
            if (!SetFileInformationByHandle(
                    handle,
                    FileCaseSensitiveInfo,
                    ref disabled,
                    (uint)Marshal.SizeOf<FileCaseSensitiveInformation>()))
                throw new IOException(
                    "Não foi possível restaurar a política do diretório de teste.",
                    new Win32Exception(Marshal.GetLastPInvokeError()));
        }
    }

    private static void ExpectRejected(Func<IDisposable> action, string message)
    {
        try
        {
            using var unexpected = action();
        }
        catch (Exception exception) when (exception is InvalidDataException
                                           or ArgumentOutOfRangeException)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }

    private static void ExpectRejectedOperation(Action action, string message)
    {
        try
        {
            action();
        }
        catch (InvalidDataException)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileCaseSensitiveInformation
    {
        internal uint Flags;
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

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        int informationClass,
        ref FileCaseSensitiveInformation fileInformation,
        uint bufferSize);
#pragma warning restore SYSLIB1054
}
