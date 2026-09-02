using System.Collections.Concurrent;
using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TurboBoxManager.Licensing;

internal readonly record struct SuiteInventoryPublicationCacheKey(
    string AuthorityKeyId,
    int InventorySchemaVersion,
    string ProductId,
    string LicenseId,
    string DeviceId);

internal sealed record SuiteInventoryPublicationState(
    string? SemanticStateHash,
    string? InventoryHash,
    DateTimeOffset? UnsupportedUntil,
    DateTimeOffset? AcceptedAt);

internal interface ISuiteInventoryPublicationStateStore
{
    ValueTask<SuiteInventoryPublicationState?> LoadAsync(
        SuiteInventoryPublicationCacheKey key,
        CancellationToken cancellationToken);

    ValueTask<bool> TrySaveAsync(
        SuiteInventoryPublicationCacheKey key,
        SuiteInventoryPublicationState state,
        CancellationToken cancellationToken);
}

/// <summary>
/// Persists only the minimum state needed to suppress duplicate inventory
/// publication. Neither readable hardware attributes nor cache-key components
/// are written to disk.
/// </summary>
internal sealed class SuiteInventoryPublicationStateStore
    : ISuiteInventoryPublicationStateStore
{
    private const int StorageSchemaVersion = 1;
    private const int MaximumProtectedBytes = 16 * 1024;
    private const int MaximumClearTextBytes = 4 * 1024;
    private const string Purpose =
        "TURBORAMA_SUITE_INVENTORY_PUBLICATION_STATE";

    private static readonly byte[] ProtectionEntropy = Encoding.UTF8.GetBytes(
        "TurboRama/Suite/InventoryPublicationState/v1");
    private static readonly byte[] CacheKeyDomain = Encoding.UTF8.GetBytes(
        "TurboRamaSuiteInventoryPublicationCacheKey/v1\0");
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> FileGates =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly string _root;

    internal SuiteInventoryPublicationStateStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Turborama",
            "Suite",
            "licensing"))
    {
    }

    internal SuiteInventoryPublicationStateStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (!Path.IsPathFullyQualified(root))
            throw new ArgumentException(
                "A raiz do cache de inventario deve ser absoluta.", nameof(root));
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    public async ValueTask<SuiteInventoryPublicationState?> LoadAsync(
        SuiteInventoryPublicationCacheKey key,
        CancellationToken cancellationToken)
    {
        ValidateKey(key);
        cancellationToken.ThrowIfCancellationRequested();

        var stem = CacheFileStem(key);
        var gate = FileGates.GetOrAdd(
            GateKey(stem), static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var rootLease = OpenRoot();
                var primaryPath = Path.Combine(_root, stem + ".state");
                if (TryReadState(
                        rootLease,
                        primaryPath,
                        out var primary,
                        out var primaryBytes))
                {
                    try { return primary; }
                    finally { Zero(primaryBytes); }
                }

                var backupPath = Path.Combine(_root, stem + ".state.bak");
                if (!TryReadState(
                        rootLease,
                        backupPath,
                        out var backup,
                        out var backupBytes)) return null;
                try { return backup; }
                finally { Zero(backupBytes); }
            }
            catch (Exception exception) when (IsRecoverableStorageFailure(exception))
            {
                return null;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<bool> TrySaveAsync(
        SuiteInventoryPublicationCacheKey key,
        SuiteInventoryPublicationState state,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ValidateKey(key);
        ValidateState(state);
        cancellationToken.ThrowIfCancellationRequested();

        var stem = CacheFileStem(key);
        var gate = FileGates.GetOrAdd(
            GateKey(stem), static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[]? clearText = null;
            byte[]? protectedBytes = null;
            try
            {
                clearText = SerializeState(state);
                protectedBytes = ProtectedData.Protect(
                    clearText,
                    ProtectionEntropy,
                    DataProtectionScope.CurrentUser);
                if (protectedBytes.Length is <= 0 or > MaximumProtectedBytes)
                    return false;

                using var rootLease = OpenRoot();
                var primaryPath = Path.Combine(_root, stem + ".state");
                var backupPath = Path.Combine(_root, stem + ".state.bak");
                byte[]? recoverablePrimary = null;
                try
                {
                    if (TryReadState(
                            rootLease,
                            primaryPath,
                            out var current,
                            out recoverablePrimary))
                    {
                        if (StatesEquivalent(current!, state)) return true;
                        if (recoverablePrimary is not null
                            && !TryWriteAtomically(
                                rootLease,
                                backupPath,
                                recoverablePrimary,
                                cancellationToken))
                            return false;
                    }
                }
                finally
                {
                    Zero(recoverablePrimary);
                }

                return TryWriteAtomically(
                    rootLease,
                    primaryPath,
                    protectedBytes,
                    cancellationToken);
            }
            catch (Exception exception) when (IsRecoverableStorageFailure(exception))
            {
                return false;
            }
            finally
            {
                Zero(clearText);
                Zero(protectedBytes);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private PathIdentity.DirectoryTreeLease OpenRoot()
    {
        var localApplicationData = Path.GetFullPath(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        var localPrefix = Path.TrimEndingDirectorySeparator(localApplicationData)
                          + Path.DirectorySeparatorChar;
        if (!_root.StartsWith(localPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "O cache de inventario saiu do perfil local do usuario.");
        if (!Directory.Exists(localApplicationData))
            throw new DirectoryNotFoundException(
                "A raiz de dados locais do usuario nao esta disponivel.");

        using var localLease = PathIdentity.OpenDirectoryTree(localApplicationData);
        localLease.Revalidate();
        return PathIdentity.OpenDirectoryTree(
            _root,
            createIfMissing: true,
            privateLeaf: true);
    }

    private static bool TryReadState(
        PathIdentity.DirectoryTreeLease rootLease,
        string path,
        out SuiteInventoryPublicationState? state,
        out byte[]? protectedBytes)
    {
        state = null;
        protectedBytes = null;
        byte[]? readProtectedBytes = null;
        byte[]? clearText = null;
        try
        {
            rootLease.Revalidate();
            using var stream = rootLease.OpenFile(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4 * 1024,
                FileOptions.SequentialScan);
            var identity = PathIdentity.CaptureFileIdentity(
                stream.SafeFileHandle, path);
            if (stream.Length is <= 0 or > MaximumProtectedBytes) return false;

            readProtectedBytes = GC.AllocateUninitializedArray<byte>(
                checked((int)stream.Length));
            stream.ReadExactly(readProtectedBytes);
            _ = PathIdentity.RevalidateFile(stream.SafeFileHandle, path, identity);
            rootLease.Revalidate();

            clearText = ProtectedData.Unprotect(
                readProtectedBytes,
                ProtectionEntropy,
                DataProtectionScope.CurrentUser);
            if (clearText.Length is <= 0 or > MaximumClearTextBytes) return false;
            state = ParseState(clearText);
            protectedBytes = readProtectedBytes;
            readProtectedBytes = null;
            return true;
        }
        catch (Exception exception) when (IsRecoverableStorageFailure(exception))
        {
            state = null;
            return false;
        }
        finally
        {
            Zero(readProtectedBytes);
            Zero(clearText);
        }
    }

    private static bool TryWriteAtomically(
        PathIdentity.DirectoryTreeLease rootLease,
        string destinationPath,
        ReadOnlySpan<byte> bytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var temporaryPath = Path.Combine(
            rootLease.AnchorPath,
            "." + Path.GetFileName(destinationPath) + "." +
            Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using var output = rootLease.OpenFile(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                4 * 1024,
                FileOptions.WriteThrough,
                deleteAccess: true);
            var temporaryIdentity = PathIdentity.CaptureFileIdentity(
                output.SafeFileHandle, temporaryPath);
            output.Write(bytes);
            output.Flush(flushToDisk: true);
            cancellationToken.ThrowIfCancellationRequested();
            _ = PathIdentity.RevalidateFile(
                output.SafeFileHandle, temporaryPath, temporaryIdentity);
            rootLease.Revalidate();
            _ = PathIdentity.RenameByHandle(
                output.SafeFileHandle,
                temporaryIdentity,
                rootLease.AnchorHandle,
                rootLease.AnchorPath,
                Path.GetFileName(destinationPath),
                replaceIfExists: true);
            rootLease.Revalidate();
            return true;
        }
        catch (Exception exception) when (IsRecoverableStorageFailure(exception))
        {
            return false;
        }
        finally
        {
            try { _ = PathIdentity.DeleteFileExact(temporaryPath, rootLease.AnchorPath); }
            catch (Exception exception) when (IsRecoverableStorageFailure(exception))
            {
            }
        }
    }

    private static byte[] SerializeState(SuiteInventoryPublicationState state)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions
               {
                   Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default,
                   Indented = false,
                   SkipValidation = false
               }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", StorageSchemaVersion);
            writer.WriteString("purpose", Purpose);
            WriteOptionalString(
                writer, "semanticStateHash", state.SemanticStateHash);
            WriteOptionalString(writer, "inventoryHash", state.InventoryHash);
            WriteOptionalTimestamp(
                writer, "unsupportedUntil", state.UnsupportedUntil);
            WriteOptionalTimestamp(writer, "acceptedAt", state.AcceptedAt);
            writer.WriteEndObject();
        }

        if (output.Length is <= 0 or > MaximumClearTextBytes)
            throw new InvalidDataException(
                "O estado local de publicacao excedeu o limite permitido.");
        return output.ToArray();
    }

    private static SuiteInventoryPublicationState ParseState(
        ReadOnlyMemory<byte> utf8)
    {
        using var document = JsonDocument.Parse(utf8, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 2
        });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("O estado local nao e um objeto JSON.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        int? schemaVersion = null;
        string? purpose = null;
        string? semanticStateHash = null;
        string? inventoryHash = null;
        DateTimeOffset? unsupportedUntil = null;
        DateTimeOffset? acceptedAt = null;
        foreach (var property in root.EnumerateObject())
        {
            if (!seen.Add(property.Name))
                throw new JsonException("O estado local possui campo duplicado.");
            switch (property.Name)
            {
                case "schemaVersion" when
                    property.Value.ValueKind == JsonValueKind.Number
                    && property.Value.TryGetInt32(out var version):
                    schemaVersion = version;
                    break;
                case "purpose" when property.Value.ValueKind == JsonValueKind.String:
                    purpose = property.Value.GetString();
                    break;
                case "semanticStateHash":
                    semanticStateHash = ReadOptionalString(property.Value);
                    break;
                case "inventoryHash":
                    inventoryHash = ReadOptionalString(property.Value);
                    break;
                case "unsupportedUntil":
                    unsupportedUntil = ReadOptionalTimestamp(property.Value);
                    break;
                case "acceptedAt":
                    acceptedAt = ReadOptionalTimestamp(property.Value);
                    break;
                default:
                    throw new JsonException(
                        "O estado local possui campo desconhecido ou tipo invalido.");
            }
        }

        if (seen.Count != 6
            || schemaVersion != StorageSchemaVersion
            || !string.Equals(purpose, Purpose, StringComparison.Ordinal))
            throw new JsonException(
                "O estado local possui versao, finalidade ou campos invalidos.");

        var state = new SuiteInventoryPublicationState(
            semanticStateHash,
            inventoryHash,
            unsupportedUntil,
            acceptedAt);
        ValidateState(state);
        return state;
    }

    private static string? ReadOptionalString(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => value.GetString(),
            _ => throw new JsonException(
                "O estado local possui string opcional invalida.")
        };

    private static DateTimeOffset? ReadOptionalTimestamp(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out var seconds))
            throw new JsonException("O estado local possui instante invalido.");
        try { return DateTimeOffset.FromUnixTimeSeconds(seconds); }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new JsonException(
                "O estado local possui instante fora do limite.",
                path: null,
                lineNumber: null,
                bytePositionInLine: null,
                exception);
        }
    }

    private static void WriteOptionalString(
        Utf8JsonWriter writer,
        string propertyName,
        string? value)
    {
        if (value is null) writer.WriteNull(propertyName);
        else writer.WriteString(propertyName, value);
    }

    private static void WriteOptionalTimestamp(
        Utf8JsonWriter writer,
        string propertyName,
        DateTimeOffset? value)
    {
        if (value is null) writer.WriteNull(propertyName);
        else writer.WriteNumber(propertyName, value.Value.ToUnixTimeSeconds());
    }

    internal static void ValidateKey(SuiteInventoryPublicationCacheKey key)
    {
        var authority = SuiteOnlineLicenseProtocol.RequireHex(
            key.AuthorityKeyId, nameof(key.AuthorityKeyId), 64);
        var product = SuiteOnlineLicenseProtocol.RequireIdentifier(
            key.ProductId, nameof(key.ProductId), 1, 64);
        var license = SuiteOnlineLicenseProtocol.RequireIdentifier(
            key.LicenseId, nameof(key.LicenseId), 6, 64);
        var device = SuiteOnlineLicenseProtocol.RequireHex(
            key.DeviceId, nameof(key.DeviceId), 64);
        if (key.InventorySchemaVersion is < 1 or > ushort.MaxValue
            || !string.Equals(authority, key.AuthorityKeyId, StringComparison.Ordinal)
            || !string.Equals(product, key.ProductId, StringComparison.Ordinal)
            || !string.Equals(license, key.LicenseId, StringComparison.Ordinal)
            || !string.Equals(device, key.DeviceId, StringComparison.Ordinal))
            throw new SecurityException(
                "A chave do cache de publicacao nao esta canonica.");
    }

    internal static void ValidateState(SuiteInventoryPublicationState state)
    {
        var semantic = RequireOptionalCanonicalHash(
            state.SemanticStateHash, nameof(state.SemanticStateHash));
        var inventory = RequireOptionalCanonicalHash(
            state.InventoryHash, nameof(state.InventoryHash));
        if ((semantic is null) != (inventory is null)
            || (inventory is null) != (state.AcceptedAt is null))
            throw new SecurityException(
                "Hashes aceitos e instante de aceite devem existir em conjunto.");
        if (semantic is null && state.UnsupportedUntil is null)
            throw new SecurityException(
                "O estado local de publicacao esta vazio.");
        RequireUtc(state.UnsupportedUntil, nameof(state.UnsupportedUntil));
        RequireUtc(state.AcceptedAt, nameof(state.AcceptedAt));
    }

    private static string? RequireOptionalCanonicalHash(
        string? value,
        string label)
    {
        if (value is null) return null;
        var canonical = SuiteOnlineLicenseProtocol.RequireHex(value, label, 64);
        if (!string.Equals(canonical, value, StringComparison.Ordinal))
            throw new SecurityException($"{label} nao esta canonico.");
        return canonical;
    }

    private static void RequireUtc(DateTimeOffset? value, string label)
    {
        if (value is not null && value.Value.Offset != TimeSpan.Zero)
            throw new SecurityException($"{label} deve estar em UTC.");
    }

    private static bool StatesEquivalent(
        SuiteInventoryPublicationState left,
        SuiteInventoryPublicationState right)
        => string.Equals(
               left.SemanticStateHash,
               right.SemanticStateHash,
               StringComparison.Ordinal)
           && string.Equals(
               left.InventoryHash,
               right.InventoryHash,
               StringComparison.Ordinal)
           && OptionalUnixSecondsEqual(left.UnsupportedUntil, right.UnsupportedUntil)
           && OptionalUnixSecondsEqual(left.AcceptedAt, right.AcceptedAt);

    private static bool OptionalUnixSecondsEqual(
        DateTimeOffset? left,
        DateTimeOffset? right)
        => left.HasValue == right.HasValue
           && (!left.HasValue
               || left.Value.ToUnixTimeSeconds() == right!.Value.ToUnixTimeSeconds());

    private static string CacheFileStem(SuiteInventoryPublicationCacheKey key)
    {
        byte[]? canonical = null;
        byte[]? digest = null;
        try
        {
            using var output = new MemoryStream();
            using (var writer = new Utf8JsonWriter(output))
            {
                writer.WriteStartObject();
                writer.WriteString("authorityKeyId", key.AuthorityKeyId);
                writer.WriteNumber(
                    "inventorySchemaVersion", key.InventorySchemaVersion);
                writer.WriteString("productId", key.ProductId);
                writer.WriteString("licenseId", key.LicenseId);
                writer.WriteString("deviceId", key.DeviceId);
                writer.WriteEndObject();
            }
            canonical = output.ToArray();
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hasher.AppendData(CacheKeyDomain);
            hasher.AppendData(canonical);
            digest = hasher.GetHashAndReset();
            return Convert.ToHexString(digest).ToLowerInvariant();
        }
        finally
        {
            Zero(canonical);
            Zero(digest);
        }
    }

    private string GateKey(string stem)
        => _root + Path.DirectorySeparatorChar + stem;

    private static bool IsRecoverableStorageFailure(Exception exception)
        => exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or JsonException
            or CryptographicException
            or PlatformNotSupportedException
            or NotSupportedException
            or SecurityException
            or ArgumentException;

    private static void Zero(byte[]? value)
    {
        if (value is not null) CryptographicOperations.ZeroMemory(value);
    }
}

internal sealed class InMemorySuiteInventoryPublicationStateStore
    : ISuiteInventoryPublicationStateStore
{
    private readonly ConcurrentDictionary<
        SuiteInventoryPublicationCacheKey,
        SuiteInventoryPublicationState> _states = [];

    public ValueTask<SuiteInventoryPublicationState?> LoadAsync(
        SuiteInventoryPublicationCacheKey key,
        CancellationToken cancellationToken)
    {
        SuiteInventoryPublicationStateStore.ValidateKey(key);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            _states.TryGetValue(key, out var state) ? state : null);
    }

    public ValueTask<bool> TrySaveAsync(
        SuiteInventoryPublicationCacheKey key,
        SuiteInventoryPublicationState state,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        SuiteInventoryPublicationStateStore.ValidateKey(key);
        SuiteInventoryPublicationStateStore.ValidateState(state);
        cancellationToken.ThrowIfCancellationRequested();
        _states[key] = state;
        return ValueTask.FromResult(true);
    }
}
