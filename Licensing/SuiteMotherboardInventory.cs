using System.Globalization;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace TurboBoxManager.Licensing;

internal interface ISuiteMotherboardInventorySource
{
    Task<SuiteMotherboardInventory> CollectAsync(
        CancellationToken cancellationToken = default);
}

internal sealed record SuiteMotherboardInventory(
    int SchemaVersion,
    string MotherboardFingerprint,
    string BaseboardManufacturer,
    string BaseboardProduct,
    string BaseboardVersion,
    string BaseboardSerial,
    string SystemManufacturer,
    string SystemModel,
    string SystemUuid,
    string BiosManufacturer,
    string BiosVersion,
    string OsName,
    string OsVersion,
    string Architecture,
    string ClientVersion,
    string Source,
    long CollectedAtUnixSeconds)
{
    public bool HasIdentityEvidence
        => BaseboardSerial.Length != 0
            || SystemUuid.Length != 0
            || (BaseboardManufacturer.Length != 0 && BaseboardProduct.Length != 0)
            || (SystemManufacturer.Length != 0 && SystemModel.Length != 0);
}

internal sealed class SuiteWindowsMotherboardInventorySource
    : ISuiteMotherboardInventorySource
{
    internal const int SchemaVersion = 1;
    internal const string CimSource = "CIM";
    internal const string RegistryFallbackSource = "REGISTRY_FALLBACK";
    internal const string CimAndRegistrySource = "CIM_AND_REGISTRY";

    private const string BiosRegistryPath = @"HARDWARE\DESCRIPTION\System\BIOS";
    private const int HardwareTextLimit = 128;
    private const int OperatingSystemTextLimit = 128;
    private const int VersionTextLimit = 64;

    private static readonly TimeSpan QueryTimeout = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan TotalCimTimeout = TimeSpan.FromSeconds(3);

    private readonly Lazy<Task<CimSnapshot>> _cimSnapshot = new(
        static () => Task.Run(CollectCimSafely),
        LazyThreadSafetyMode.ExecutionAndPublication);

    public async Task<SuiteMotherboardInventory> CollectAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var registry = CollectRegistrySnapshot();
        CimSnapshot cim;
        try
        {
            cim = await _cimSnapshot.Value
                .WaitAsync(TotalCimTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // WMI can spend time establishing its local COM connection, and the
            // per-query ManagementOptions timeout does not cover Connect(). The
            // caller must never wait indefinitely for this auxiliary inventory.
            cim = CimSnapshot.Empty;
        }

        return CreateInventory(cim, registry);
    }

    private static SuiteMotherboardInventory CreateInventory(
        CimSnapshot cim,
        RegistrySnapshot registry)
    {
        var usedRegistry = false;

        var baseboardManufacturer = PreferCim(
            NormalizeHardware(cim.Baseboard?.Manufacturer),
            NormalizeHardware(registry.BaseboardManufacturer),
            ref usedRegistry);
        var baseboardProduct = PreferCim(
            NormalizeHardware(cim.Baseboard?.Product),
            NormalizeHardware(registry.BaseboardProduct),
            ref usedRegistry);
        var baseboardVersion = PreferCim(
            NormalizeHardware(cim.Baseboard?.Version),
            NormalizeHardware(registry.BaseboardVersion),
            ref usedRegistry);
        var baseboardSerial = SuiteMotherboardInventoryNormalizer.NormalizeSerial(
            cim.Baseboard?.SerialNumber, HardwareTextLimit);
        var systemManufacturer = PreferCim(
            NormalizeHardware(cim.ComputerSystem?.Manufacturer),
            NormalizeHardware(registry.SystemManufacturer),
            ref usedRegistry);
        var systemModel = PreferCim(
            NormalizeHardware(cim.ComputerSystem?.Model),
            NormalizeHardware(registry.SystemModel),
            ref usedRegistry);
        var systemUuid = SuiteMotherboardInventoryNormalizer.NormalizeUuid(
            cim.ComputerSystemProduct?.Uuid);
        var biosManufacturer = PreferCim(
            NormalizeHardware(cim.Bios?.Manufacturer),
            NormalizeHardware(registry.BiosManufacturer),
            ref usedRegistry);
        var biosVersion = PreferCim(
            NormalizeHardware(cim.Bios?.Version),
            NormalizeHardware(registry.BiosVersion),
            ref usedRegistry);

        var osName = SuiteMotherboardInventoryNormalizer.NormalizeDisplay(
            RuntimeInformation.OSDescription, OperatingSystemTextLimit);
        var osVersion = SuiteMotherboardInventoryNormalizer.NormalizeDisplay(
            Environment.OSVersion.Version.ToString(), VersionTextLimit);
        var architecture = SuiteMotherboardInventoryNormalizer.NormalizeDisplay(
                RuntimeInformation.OSArchitecture.ToString(), VersionTextLimit)
            .ToUpperInvariant();
        var clientVersion = SuiteMotherboardInventoryNormalizer.NormalizeDisplay(
            SuiteCngMachineIdentity.ClientVersion(), VersionTextLimit);

        var fingerprint = SuiteMotherboardInventoryNormalizer.ComputeFingerprint(
            baseboardManufacturer,
            baseboardProduct,
            baseboardVersion,
            baseboardSerial,
            systemManufacturer,
            systemModel,
            systemUuid);

        var source = !cim.AnyQuerySucceeded
            ? RegistryFallbackSource
            : usedRegistry ? CimAndRegistrySource : CimSource;

        return new SuiteMotherboardInventory(
            SchemaVersion,
            fingerprint,
            baseboardManufacturer,
            baseboardProduct,
            baseboardVersion,
            baseboardSerial,
            systemManufacturer,
            systemModel,
            systemUuid,
            biosManufacturer,
            biosVersion,
            osName,
            osVersion,
            architecture,
            clientVersion,
            source,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    private static string NormalizeHardware(string? value)
        => SuiteMotherboardInventoryNormalizer.NormalizeHardwareDisplay(
            value, HardwareTextLimit);

    private static string PreferCim(
        string cimValue,
        string registryValue,
        ref bool usedRegistry)
    {
        if (cimValue.Length != 0) return cimValue;
        if (registryValue.Length == 0) return "";
        usedRegistry = true;
        return registryValue;
    }

    private static RegistrySnapshot CollectRegistrySnapshot()
    {
        try
        {
            using var localMachine = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine, RegistryView.Registry64);
            using var bios = localMachine.OpenSubKey(BiosRegistryPath, writable: false);
            if (bios is null) return RegistrySnapshot.Empty;

            return new RegistrySnapshot(
                ReadRegistryString(bios, "BaseBoardManufacturer"),
                ReadRegistryString(bios, "BaseBoardProduct"),
                ReadRegistryString(bios, "BaseBoardVersion"),
                ReadRegistryString(bios, "SystemManufacturer"),
                ReadRegistryString(bios, "SystemProductName"),
                ReadRegistryString(bios, "BIOSVendor"),
                ReadRegistryString(bios, "BIOSVersion"));
        }
        catch (Exception ex) when (IsExpectedLocalReadFailure(ex))
        {
            return RegistrySnapshot.Empty;
        }
    }

    private static string ReadRegistryString(RegistryKey key, string valueName)
    {
        try
        {
            return key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames)
                switch
                {
                    string value => value,
                    string[] values => string.Join(' ', values),
                    _ => ""
                };
        }
        catch (Exception ex) when (IsExpectedLocalReadFailure(ex))
        {
            return "";
        }
    }

    private static CimSnapshot CollectCimSafely()
    {
        try
        {
            var connection = new ConnectionOptions
            {
                EnablePrivileges = false,
                Timeout = QueryTimeout
            };
            var scope = new ManagementScope(@"\\.\root\cimv2", connection);

            var baseboard = QueryFirst(
                scope,
                "SELECT Manufacturer, Product, Version, SerialNumber, HostingBoard "
                    + "FROM Win32_BaseBoard",
                static value => new CimBaseboard(
                    GetString(value, "Manufacturer"),
                    GetString(value, "Product"),
                    GetString(value, "Version"),
                    GetString(value, "SerialNumber"),
                    GetBoolean(value, "HostingBoard")),
                static value => BaseboardSortKey(value));

            var computerSystem = QueryFirst(
                scope,
                "SELECT Manufacturer, Model FROM Win32_ComputerSystem",
                static value => new CimComputerSystem(
                    GetString(value, "Manufacturer"),
                    GetString(value, "Model")),
                static value => SortKey(value.Manufacturer, value.Model));

            var computerSystemProduct = QueryFirst(
                scope,
                "SELECT UUID FROM Win32_ComputerSystemProduct",
                static value => new CimComputerSystemProduct(GetString(value, "UUID")),
                static value => UuidSortKey(value.Uuid));

            var bios = QueryFirst(
                scope,
                "SELECT Manufacturer, SMBIOSBIOSVersion FROM Win32_BIOS",
                static value => new CimBios(
                    GetString(value, "Manufacturer"),
                    GetString(value, "SMBIOSBIOSVersion")),
                static value => SortKey(value.Manufacturer, value.Version));

            return new CimSnapshot(
                baseboard.Succeeded,
                baseboard.Value,
                computerSystem.Succeeded,
                computerSystem.Value,
                computerSystemProduct.Succeeded,
                computerSystemProduct.Value,
                bios.Succeeded,
                bios.Value);
        }
        catch (Exception ex) when (IsExpectedCimFailure(ex))
        {
            return CimSnapshot.Empty;
        }
    }

    private static CimQueryResult<T> QueryFirst<T>(
        ManagementScope scope,
        string queryText,
        Func<ManagementBaseObject, T> projector,
        Func<T, string> sortKey)
        where T : class
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                scope,
                new ObjectQuery(queryText),
                new System.Management.EnumerationOptions
                {
                    Timeout = QueryTimeout,
                    ReturnImmediately = false,
                    Rewindable = false,
                    BlockSize = 1
                });
            using var results = searcher.Get();
            var values = new List<T>();
            foreach (ManagementBaseObject result in results)
            {
                using (result)
                {
                    values.Add(projector(result));
                }
            }

            var selected = values
                .OrderBy(sortKey, StringComparer.Ordinal)
                .FirstOrDefault();
            return new CimQueryResult<T>(true, selected);
        }
        catch (Exception ex) when (IsExpectedCimFailure(ex))
        {
            return new CimQueryResult<T>(false, null);
        }
    }

    private static string BaseboardSortKey(CimBaseboard value)
    {
        var hostingPriority = value.HostingBoard switch
        {
            true => '0',
            null => '1',
            false => '2'
        };
        var serial = SuiteMotherboardInventoryNormalizer.NormalizeSerial(
            value.SerialNumber, HardwareTextLimit);
        var serialPriority = serial.Length == 0 ? '1' : '0';
        return string.Concat(
            hostingPriority,
            serialPriority,
            "\0",
            SortKey(value.Manufacturer, value.Product, value.Version, serial));
    }

    private static string SortKey(params string?[] values)
    {
        var normalized = values.Select(value =>
                SuiteMotherboardInventoryNormalizer.NormalizeIdentity(
                    SuiteMotherboardInventoryNormalizer.NormalizeHardwareDisplay(
                        value, HardwareTextLimit),
                    HardwareTextLimit))
            .ToArray();
        var missingFields = normalized.Count(static value => value.Length == 0);
        return string.Concat(
            missingFields.ToString("D3", CultureInfo.InvariantCulture),
            "\0",
            string.Join('\0', normalized));
    }

    private static string UuidSortKey(string? value)
    {
        var normalized = SuiteMotherboardInventoryNormalizer.NormalizeUuid(value);
        return string.Concat(
            normalized.Length == 0 ? '1' : '0',
            "\0",
            normalized);
    }

    private static string GetString(ManagementBaseObject value, string propertyName)
    {
        var raw = value.Properties[propertyName]?.Value;
        return raw switch
        {
            string text => text,
            string[] texts => string.Join(' ', texts),
            _ => ""
        };
    }

    private static bool? GetBoolean(ManagementBaseObject value, string propertyName)
        => value.Properties[propertyName]?.Value is bool result ? result : null;

    private static bool IsExpectedCimFailure(Exception exception)
        => exception is ManagementException
            or COMException
            or SecurityException
            or UnauthorizedAccessException
            or InvalidOperationException
            or NotSupportedException
            or PlatformNotSupportedException;

    private static bool IsExpectedLocalReadFailure(Exception exception)
        => exception is SecurityException
            or UnauthorizedAccessException
            or IOException
            or ObjectDisposedException
            or PlatformNotSupportedException;

    private sealed record CimBaseboard(
        string Manufacturer,
        string Product,
        string Version,
        string SerialNumber,
        bool? HostingBoard);

    private sealed record CimComputerSystem(string Manufacturer, string Model);
    private sealed record CimComputerSystemProduct(string Uuid);
    private sealed record CimBios(string Manufacturer, string Version);

    private readonly record struct CimQueryResult<T>(bool Succeeded, T? Value)
        where T : class;

    private sealed record CimSnapshot(
        bool BaseboardQuerySucceeded,
        CimBaseboard? Baseboard,
        bool ComputerSystemQuerySucceeded,
        CimComputerSystem? ComputerSystem,
        bool ComputerSystemProductQuerySucceeded,
        CimComputerSystemProduct? ComputerSystemProduct,
        bool BiosQuerySucceeded,
        CimBios? Bios)
    {
        public static CimSnapshot Empty { get; } = new(
            false, null, false, null, false, null, false, null);

        public bool AnyQuerySucceeded
            => BaseboardQuerySucceeded
                || ComputerSystemQuerySucceeded
                || ComputerSystemProductQuerySucceeded
                || BiosQuerySucceeded;
    }

    private sealed record RegistrySnapshot(
        string BaseboardManufacturer,
        string BaseboardProduct,
        string BaseboardVersion,
        string SystemManufacturer,
        string SystemModel,
        string BiosManufacturer,
        string BiosVersion)
    {
        public static RegistrySnapshot Empty { get; } = new("", "", "", "", "", "", "");
    }
}

internal static class SuiteMotherboardInventoryNormalizer
{
    // Frozen by MotherboardIdentity/v1 compatibility vectors. Any semantic
    // change to this set requires a new identity domain and inventory schema.
    internal const int PlaceholderSetVersion = 1;
    private const string FingerprintDomain = "TurboRamaMotherboardIdentity/v1\0";

    internal static string NormalizeDisplay(string? value, int maxUtf8Bytes)
        => NormalizeCore(value, NormalizationForm.FormC, uppercase: false, maxUtf8Bytes);

    internal static string NormalizeIdentity(string? value, int maxUtf8Bytes)
        => NormalizeCore(value, NormalizationForm.FormKC, uppercase: true, maxUtf8Bytes);

    internal static string NormalizeHardwareDisplay(string? value, int maxUtf8Bytes)
    {
        var display = NormalizeDisplay(value, maxUtf8Bytes);
        if (display.Length == 0) return "";

        var identity = NormalizeIdentity(display, maxUtf8Bytes);
        return identity.Length == 0 || IsPlaceholderIdentity(identity) ? "" : display;
    }

    internal static string NormalizeSerial(string? value, int maxUtf8Bytes)
    {
        var serial = NormalizeHardwareDisplay(value, maxUtf8Bytes);
        if (serial.Length == 0) return "";

        var identity = NormalizeIdentity(serial, maxUtf8Bytes);
        var significant = new string(identity
            .Where(character => char.IsLetterOrDigit(character))
            .ToArray());
        if (significant.Length >= 4
            && (significant.All(static character => character == '0')
                || significant.All(static character => character == 'F')))
            return "";

        return serial;
    }

    internal static string NormalizeUuid(string? value)
    {
        var display = NormalizeHardwareDisplay(value, 64);
        if (!Guid.TryParse(display, out var parsed)
            || parsed == Guid.Empty
            || parsed == AllBitsSetGuid)
            return "";

        return parsed.ToString("D", CultureInfo.InvariantCulture).ToLowerInvariant();
    }

    internal static string ComputeFingerprint(
        string? baseboardManufacturer,
        string? baseboardProduct,
        string? baseboardVersion,
        string? baseboardSerial,
        string? systemManufacturer,
        string? systemModel,
        string? systemUuid)
    {
        const int identityLimit = 128;
        var canonical = string.Join('\n',
            "baseboardManufacturer=" + NormalizeHardwareIdentity(
                baseboardManufacturer, identityLimit),
            "baseboardProduct=" + NormalizeHardwareIdentity(
                baseboardProduct, identityLimit),
            "baseboardVersion=" + NormalizeHardwareIdentity(
                baseboardVersion, identityLimit),
            "baseboardSerial=" + NormalizeIdentity(
                NormalizeSerial(baseboardSerial, identityLimit), identityLimit),
            "systemManufacturer=" + NormalizeHardwareIdentity(
                systemManufacturer, identityLimit),
            "systemModel=" + NormalizeHardwareIdentity(systemModel, identityLimit),
            "systemUuid=" + NormalizeIdentity(NormalizeUuid(systemUuid), identityLimit));
        var bytes = Encoding.UTF8.GetBytes(FingerprintDomain + canonical);
        try
        {
            return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    internal static string ComputeFingerprint(SuiteMotherboardInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        return ComputeFingerprint(
            inventory.BaseboardManufacturer,
            inventory.BaseboardProduct,
            inventory.BaseboardVersion,
            inventory.BaseboardSerial,
            inventory.SystemManufacturer,
            inventory.SystemModel,
            inventory.SystemUuid);
    }

    internal static bool IsPlaceholderIdentity(string identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return identity is
            "TO BE FILLED BY O.E.M."
            or "TO BE FILLED BY O.E.M"
            or "TO BE FILLED BY OEM"
            or "DEFAULT STRING"
            or "SYSTEM PRODUCT NAME"
            or "UNKNOWN"
            or "NONE"
            or "NOT SPECIFIED"
            or "00000000"
            or "FFFFFFFF";
    }

    private static string NormalizeHardwareIdentity(string? value, int maxUtf8Bytes)
        => NormalizeIdentity(
            NormalizeHardwareDisplay(value, maxUtf8Bytes), maxUtf8Bytes);

    private static string NormalizeCore(
        string? value,
        NormalizationForm normalizationForm,
        bool uppercase,
        int maxUtf8Bytes)
    {
        if (string.IsNullOrWhiteSpace(value) || maxUtf8Bytes <= 0) return "";

        string normalized;
        try
        {
            normalized = value.Normalize(normalizationForm);
        }
        catch (ArgumentException)
        {
            return "";
        }

        var builder = new StringBuilder(normalized.Length);
        var pendingSpace = false;
        foreach (var rune in normalized.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                pendingSpace = builder.Length != 0;
                continue;
            }

            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control
                or UnicodeCategory.Format
                or UnicodeCategory.Surrogate
                or UnicodeCategory.PrivateUse
                or UnicodeCategory.OtherNotAssigned)
                continue;

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }
            builder.Append(rune.ToString());
        }

        if (builder.Length == 0) return "";
        var result = builder.ToString();
        if (uppercase) result = result.ToUpperInvariant();
        return Encoding.UTF8.GetByteCount(result) <= maxUtf8Bytes ? result : "";
    }

    private static Guid AllBitsSetGuid { get; }
        = new("ffffffff-ffff-ffff-ffff-ffffffffffff");
}
