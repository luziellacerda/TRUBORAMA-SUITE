using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace TurboBoxManager.Licensing;

internal interface ISuiteMachineIdentity
{
    SuiteDeviceDescriptor Describe();
    string Sign(SuiteChallengeResponse challenge, string licenseId, string sessionId,
        string action, string contextHash);
    string SignDeviceInventory(SuiteChallengeResponse challenge, string licenseId,
        string sessionId, string inventoryHash);
}

internal sealed class SuiteCngMachineIdentity : ISuiteMachineIdentity
{
    private const string KeyPrefix = "TurboRama.Suite.OnlineIdentity.v1";
    private const string ImplementationTypeProperty = "Impl Type";
    private const int HardwareImplementationFlag = 0x00000001;

    private static readonly CngProvider TpmProvider =
        CngProvider.MicrosoftPlatformCryptoProvider;
    private static readonly CngProvider SoftwareProvider =
        CngProvider.MicrosoftSoftwareKeyStorageProvider;

    private readonly SuiteIdentityPolicy _policy;
    private readonly object _gate = new();

    public SuiteCngMachineIdentity(SuiteIdentityPolicy policy)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "A identidade de licenciamento da Suite exige Windows.");
        _policy = policy;
    }

    public SuiteDeviceDescriptor Describe()
    {
        lock (_gate)
        {
            using var selected = OpenOrCreateSelectedKey();
            ValidateKey(selected.Key, selected.Profile, selected.Provider);
            using var rsa = new RSACng(selected.Key);
            return DescribeWithOpenKey(rsa, selected.Profile);
        }
    }

    public string Sign(SuiteChallengeResponse challenge, string licenseId, string sessionId,
        string action, string contextHash)
    {
        lock (_gate)
        {
            using var selected = OpenExistingSelectedKey();
            ValidateKey(selected.Key, selected.Profile, selected.Provider);
            using var rsa = new RSACng(selected.Key);
            var descriptor = DescribeWithOpenKey(rsa, selected.Profile);
            var message = SuiteOnlineLicenseProtocol.BuildSigningMessage(challenge, licenseId,
                descriptor.DeviceId, sessionId, action, contextHash);
            byte[] signature = Array.Empty<byte>();
            try
            {
                signature = rsa.SignData(message, HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pss);
                return Convert.ToBase64String(signature);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(message);
                if (signature.Length != 0) CryptographicOperations.ZeroMemory(signature);
            }
        }
    }

    public string SignDeviceInventory(SuiteChallengeResponse challenge,
        string licenseId, string sessionId, string inventoryHash)
    {
        lock (_gate)
        {
            using var selected = OpenExistingSelectedKey();
            ValidateKey(selected.Key, selected.Profile, selected.Provider);
            using var rsa = new RSACng(selected.Key);
            var descriptor = DescribeWithOpenKey(rsa, selected.Profile);
            var message = SuiteDeviceInventoryProtocol.BuildProofSigningMessage(
                challenge, licenseId, descriptor.DeviceId, sessionId,
                inventoryHash);
            byte[] signature = Array.Empty<byte>();
            try
            {
                signature = rsa.SignData(message, HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pss);
                return Convert.ToBase64String(signature);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(message);
                if (signature.Length != 0)
                    CryptographicOperations.ZeroMemory(signature);
            }
        }
    }

    private SelectedCngKey OpenOrCreateSelectedKey()
    {
        var existing = TryOpenPreferredExistingKey();
        if (existing is not null) return existing;

        return _policy switch
        {
            SuiteIdentityPolicy.TpmRequired => CreateKey(
                SuiteProtectionProfile.TpmBound, TpmProvider),
            SuiteIdentityPolicy.SoftwareOnly => CreateKey(
                SuiteProtectionProfile.SoftwareBoundOnline, SoftwareProvider),
            SuiteIdentityPolicy.TpmPreferred => CreateTpmOrSoftwareFallback(),
            _ => throw new SecurityException("A politica da identidade Suite e invalida.")
        };
    }

    private SelectedCngKey OpenExistingSelectedKey()
        => TryOpenPreferredExistingKey()
            ?? throw new CryptographicException(
                "A chave persistente da identidade Suite nao esta disponivel.");

    private SelectedCngKey? TryOpenPreferredExistingKey()
    {
        return _policy switch
        {
            SuiteIdentityPolicy.TpmRequired => TryOpenExisting(
                SuiteProtectionProfile.TpmBound, TpmProvider),
            SuiteIdentityPolicy.SoftwareOnly => TryOpenExisting(
                SuiteProtectionProfile.SoftwareBoundOnline, SoftwareProvider),
            SuiteIdentityPolicy.TpmPreferred =>
                TryOpenExisting(SuiteProtectionProfile.TpmBound, TpmProvider)
                ?? TryOpenExisting(SuiteProtectionProfile.SoftwareBoundOnline, SoftwareProvider),
            _ => throw new SecurityException("A politica da identidade Suite e invalida.")
        };
    }

    private static SelectedCngKey CreateTpmOrSoftwareFallback()
    {
        try
        {
            return CreateKey(SuiteProtectionProfile.TpmBound, TpmProvider);
        }
        catch (Exception ex) when (ex is CryptographicException
            or PlatformNotSupportedException or NotSupportedException)
        {
            return CreateKey(SuiteProtectionProfile.SoftwareBoundOnline, SoftwareProvider);
        }
    }

    private static SelectedCngKey? TryOpenExisting(SuiteProtectionProfile profile,
        CngProvider provider)
    {
        var name = KeyName(profile);
        bool exists;
        try { exists = CngKey.Exists(name, provider, CngKeyOpenOptions.UserKey); }
        catch (Exception ex) when (ex is CryptographicException
            or PlatformNotSupportedException or NotSupportedException)
        {
            return null;
        }
        if (!exists) return null;

        var key = CngKey.Open(name, provider, CngKeyOpenOptions.UserKey);
        try
        {
            ValidateKey(key, profile, provider);
            return new SelectedCngKey(key, profile, provider);
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    private static SelectedCngKey CreateKey(SuiteProtectionProfile profile,
        CngProvider provider)
    {
        var parameters = new CngKeyCreationParameters
        {
            Provider = provider,
            ExportPolicy = CngExportPolicies.None,
            KeyUsage = CngKeyUsages.Signing,
            KeyCreationOptions = CngKeyCreationOptions.None
        };
        parameters.Parameters.Add(new CngProperty(
            "Length", BitConverter.GetBytes(2048), CngPropertyOptions.None));

        CngKey key;
        try
        {
            key = CngKey.Create(CngAlgorithm.Rsa, KeyName(profile), parameters);
        }
        catch (CryptographicException)
        {
            var raced = TryOpenExisting(profile, provider);
            if (raced is not null) return raced;
            throw;
        }

        try
        {
            ValidateKey(key, profile, provider);
            return new SelectedCngKey(key, profile, provider);
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    private static void ValidateKey(CngKey key, SuiteProtectionProfile profile,
        CngProvider provider)
    {
        if (!string.Equals(key.Provider?.Provider, provider.Provider, StringComparison.Ordinal)
            || key.AlgorithmGroup != CngAlgorithmGroup.Rsa
            || key.KeySize is < 2048 or > 4096
            || key.IsEphemeral
            || (key.ExportPolicy & (CngExportPolicies.AllowExport
                | CngExportPolicies.AllowPlaintextExport
                | CngExportPolicies.AllowArchiving
                | CngExportPolicies.AllowPlaintextArchiving)) != 0
            || (key.KeyUsage & CngKeyUsages.Signing) == 0)
            throw new SecurityException(
                "A chave de identidade Suite nao atende a politica de seguranca.");

        if (profile != SuiteProtectionProfile.TpmBound) return;
        byte[] implementation;
        try
        {
            implementation = key.GetProperty(
                ImplementationTypeProperty, CngPropertyOptions.None).GetValue()
                ?? throw new SecurityException(
                    "O provedor TPM nao informou o tipo de implementacao.");
        }
        catch (CryptographicException ex)
        {
            throw new SecurityException(
                "O provedor TPM nao comprovou implementacao em hardware.", ex);
        }

        try
        {
            if (implementation.Length < sizeof(int)
                || (BitConverter.ToInt32(implementation, 0)
                    & HardwareImplementationFlag) == 0)
                throw new SecurityException("A chave declarada como TPM nao esta em hardware.");
        }
        finally { CryptographicOperations.ZeroMemory(implementation); }
    }

    private static SuiteDeviceDescriptor DescribeWithOpenKey(RSA rsa,
        SuiteProtectionProfile profile)
    {
        var spki = rsa.ExportSubjectPublicKeyInfo();
        try
        {
            return new SuiteDeviceDescriptor(
                SuiteOnlineLicenseProtocol.SchemaVersion,
                SuiteOnlineLicenseProtocol.DeviceIdFromSpki(spki),
                SuiteProtectionProfileCodec.Format(profile),
                SuiteOnlineLicenseProtocol.SigningAlgorithm,
                Convert.ToBase64String(spki),
                SuiteHardwareFingerprint.Create(),
                ClientVersion());
        }
        finally { CryptographicOperations.ZeroMemory(spki); }
    }

    private static string KeyName(SuiteProtectionProfile profile)
        => KeyPrefix + "." + SuiteProtectionProfileCodec.Format(profile) + "." + SidSuffix();

    private static string SidSuffix()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value
            ?? throw new SecurityException("O Windows nao informou o SID do usuario.");
        var bytes = Encoding.UTF8.GetBytes(sid);
        try
        {
            return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()[..24];
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    internal static string ClientVersion()
        => typeof(SuiteCngMachineIdentity).Assembly.GetName().Version?.ToString()
            ?? "1.0.0.0";

    private sealed class SelectedCngKey : IDisposable
    {
        public SelectedCngKey(CngKey key, SuiteProtectionProfile profile,
            CngProvider provider)
            => (Key, Profile, Provider) = (key, profile, provider);

        public CngKey Key { get; }
        public SuiteProtectionProfile Profile { get; }
        public CngProvider Provider { get; }
        public void Dispose() => Key.Dispose();
    }
}

internal static class SuiteHardwareFingerprint
{
    public static string Create()
    {
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["architecture"] = RuntimeInformation.OSArchitecture.ToString(),
            ["baseboard"] = ReadRegistry(Registry.LocalMachine,
                @"HARDWARE\DESCRIPTION\System\BIOS", "BaseBoardProduct"),
            ["biosManufacturer"] = ReadRegistry(Registry.LocalMachine,
                @"HARDWARE\DESCRIPTION\System\BIOS", "BIOSVendor"),
            ["biosVersion"] = ReadRegistry(Registry.LocalMachine,
                @"HARDWARE\DESCRIPTION\System\BIOS", "BIOSVersion"),
            ["machineGuid"] = ReadRegistry(Registry.LocalMachine,
                @"SOFTWARE\Microsoft\Cryptography", "MachineGuid"),
            ["processor"] = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "",
            ["systemManufacturer"] = ReadRegistry(Registry.LocalMachine,
                @"HARDWARE\DESCRIPTION\System\BIOS", "SystemManufacturer"),
            ["systemProduct"] = ReadRegistry(Registry.LocalMachine,
                @"HARDWARE\DESCRIPTION\System\BIOS", "SystemProductName")
        };
        var canonical = string.Join("\n", values.Select(pair =>
            pair.Key + "=" + Normalize(pair.Value)));
        var bytes = Encoding.UTF8.GetBytes(
            "TurboRamaHardwareFingerprint/v1\0" + canonical);
        try
        {
            return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static string ReadRegistry(RegistryKey root, string path, string name)
    {
        try
        {
            using var key = root.OpenSubKey(path, writable: false);
            return key?.GetValue(name)?.ToString() ?? "";
        }
        catch (Exception ex) when (ex is SecurityException
            or UnauthorizedAccessException or IOException)
        {
            return "";
        }
    }

    private static string Normalize(string value)
        => string.Join(' ', value.Trim().ToUpperInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
