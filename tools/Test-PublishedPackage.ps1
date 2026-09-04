[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PackageRoot,

    [switch]$AllowUnsigned,

    [ValidatePattern('^$|^[0-9A-Fa-f]{40}$')]
    [string]$CertificateThumbprint = '',

    [ValidatePattern('^$|^[0-9A-Fa-f]{40}$')]
    [string]$TimestampCertificateThumbprint = '',

    [string]$AuthorityConfigurationBase64 = '',

    [ValidatePattern('\A(?:|[0-9a-f]{64})\z')]
    [string]$AuthorityConfigurationSha256 = '',

    [string]$AuthorityIssuerSpkiBase64 = '',

    [string]$ContentAuthorityConfigurationBase64 = '',

    [ValidatePattern('\A(?:|[0-9a-f]{64})\z')]
    [string]$ContentAuthorityConfigurationSha256 = '',

    [string]$ContentAuthorityIssuerSpkiBase64 = '',

    [string]$AuthorityVerifierAssemblyPath = '',

    [string]$DotNetPath = 'dotnet',

    [string]$GitPath = 'git',

    [ValidatePattern('\A(?:|[0-9A-Fa-f]{64})\z')]
    [string]$PowerShellSha256 = '',

    [ValidatePattern('\A(?:|[0-9A-Fa-f]{64})\z')]
    [string]$PowerShellHomeTreeSha256 = '',

    [ValidatePattern('\A(?:|[0-9A-Fa-f]{64})\z')]
    [string]$GitSha256 = '',

    [ValidatePattern('\A(?:|[0-9A-Fa-f]{64})\z')]
    [string]$GitTreeSha256 = '',

    [ValidatePattern('\A(?:|[0-9A-Fa-f]{64})\z')]
    [string]$DotNetSdkTreeSha256 = '',

    [ValidatePattern('\A(?:|[0-9A-Fa-f]{64})\z')]
    [string]$SignToolSha256 = '',

    [ValidatePattern('\A(?:|[0-9A-Fa-f]{64})\z')]
    [string]$ReleaseTagPublicKeySha256 = '',

    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$package = (Resolve-Path -LiteralPath $PackageRoot).Path
$failures = [System.Collections.Generic.List[string]]::new()

function Add-Failure([string]$Message) { $failures.Add($Message) }

function Test-ReleaseManifestJsonShape(
    [Text.Json.JsonElement]$Element,
    [string]$JsonPath) {
    if ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Object) {
        $expectedNames = $null
        switch ($JsonPath) {
            '$' {
                $expectedNames = @(
                    'schema', 'product', 'version', 'runtime', 'selfContained',
                    'unsigned', 'buildUtc', 'source', 'toolchain', 'authenticode',
                    'authority', 'contentAuthority', 'files')
            }
            '$.source' {
                $expectedNames = @('repository', 'commit', 'branch', 'tag', 'dirty')
            }
            '$.toolchain' {
                $expectedNames = @(
                    'dotnetSdk',
                    'powerShellExecutableSha256',
                    'powerShellHomeTreeSha256',
                    'gitExecutableSha256',
                    'gitTreeSha256',
                    'dotnetSdkTreeSha256',
                    'signToolExecutableSha256',
                    'releaseTagPublicKeySha256')
            }
            '$.authenticode' {
                $expectedNames = @(
                    'required', 'status', 'signerThumbprint', 'timestampThumbprint')
            }
            '$.authority' {
                $expectedNames = @('configurationSha256', 'issuerSpkiSha256')
            }
            '$.contentAuthority' {
                $expectedNames = @('configurationSha256', 'issuerSpkiSha256')
            }
            '$.files[]' { $expectedNames = @('path', 'bytes', 'sha256') }
        }

        $caseInsensitiveNames = [Collections.Generic.HashSet[string]]::new(
            [StringComparer]::OrdinalIgnoreCase)
        $exactNames = [Collections.Generic.HashSet[string]]::new(
            [StringComparer]::Ordinal)
        foreach ($property in $Element.EnumerateObject()) {
            if (-not $caseInsensitiveNames.Add($property.Name)) {
                Add-Failure "Campo JSON duplicado ou com casing ambiguo em ${JsonPath}: $($property.Name)"
            }
            [void]$exactNames.Add($property.Name)
            if ($null -ne $expectedNames -and -not ($expectedNames -ccontains $property.Name)) {
                Add-Failure "Campo JSON desconhecido ou com casing nao canonico em ${JsonPath}: $($property.Name)"
            }
            Test-ReleaseManifestJsonShape $property.Value "${JsonPath}.$($property.Name)"
        }
        if ($null -ne $expectedNames) {
            foreach ($expectedName in $expectedNames) {
                if (-not $exactNames.Contains($expectedName)) {
                    Add-Failure "Campo JSON obrigatorio ausente em ${JsonPath}: $expectedName"
                }
            }
        }
    }
    elseif ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Array) {
        $itemPath = if ($JsonPath -eq '$.files') { '$.files[]' } else { "${JsonPath}[]" }
        foreach ($item in $Element.EnumerateArray()) {
            Test-ReleaseManifestJsonShape $item $itemPath
        }
    }
}

$pathCursor = $package
while (-not [string]::IsNullOrWhiteSpace($pathCursor)) {
    try {
        $pathInfo = Get-Item -LiteralPath $pathCursor -Force
        if (($pathInfo.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            Add-Failure "Reparse point proibido na ancestralidade do pacote: $pathCursor"
        }
    }
    catch {
        Add-Failure "Nao foi possivel validar a ancestralidade do pacote: $pathCursor"
        break
    }
    $parentCursor = Split-Path -Parent $pathCursor
    if ([string]::IsNullOrWhiteSpace($parentCursor) -or
        $parentCursor.Equals($pathCursor, [StringComparison]::OrdinalIgnoreCase)) {
        break
    }
    $pathCursor = $parentCursor
}

$hasAuthorityInput = -not [string]::IsNullOrWhiteSpace($AuthorityConfigurationBase64) -or
    -not [string]::IsNullOrWhiteSpace($AuthorityConfigurationSha256) -or
    -not [string]::IsNullOrWhiteSpace($AuthorityIssuerSpkiBase64)
$hasContentAuthorityInput = `
    -not [string]::IsNullOrWhiteSpace($ContentAuthorityConfigurationBase64) -or
    -not [string]::IsNullOrWhiteSpace($ContentAuthorityConfigurationSha256) -or
    -not [string]::IsNullOrWhiteSpace($ContentAuthorityIssuerSpkiBase64)
$authorityConfigurationBytes = $null
$authorityIssuerSpkiBytes = $null
$authorityConfigurationHash = ''
$authorityIssuerSpkiHash = ''
$authorityEmbeddedValues = @()
$contentAuthorityConfigurationBytes = $null
$contentAuthorityIssuerSpkiBytes = $null
$contentAuthorityConfigurationHash = ''
$contentAuthorityIssuerSpkiHash = ''
$contentAuthorityEmbeddedValues = @()
$toolchainPinParameters = @(
    $PowerShellSha256,
    $PowerShellHomeTreeSha256,
    $GitSha256,
    $GitTreeSha256,
    $DotNetSdkTreeSha256,
    $SignToolSha256,
    $ReleaseTagPublicKeySha256)
$hasAnyToolchainPin = @($toolchainPinParameters | Where-Object {
    -not [string]::IsNullOrWhiteSpace($_)
}).Count -ne 0
if ($AllowUnsigned -and $hasAnyToolchainPin) {
    Add-Failure 'Um staging unsigned nao pode receber pins de toolchain de producao.'
}
if (-not $AllowUnsigned -and
    ([string]::IsNullOrWhiteSpace($CertificateThumbprint) -or
     [string]::IsNullOrWhiteSpace($TimestampCertificateThumbprint))) {
    Add-Failure 'O gate assinado exige os thumbprints de Authenticode e timestamp.'
}
if (-not $hasAuthorityInput -or
        -not $hasContentAuthorityInput -or
        [string]::IsNullOrWhiteSpace($AuthorityConfigurationBase64) -or
        [string]::IsNullOrWhiteSpace($AuthorityConfigurationSha256) -or
        [string]::IsNullOrWhiteSpace($AuthorityIssuerSpkiBase64) -or
        [string]::IsNullOrWhiteSpace($ContentAuthorityConfigurationBase64) -or
        [string]::IsNullOrWhiteSpace($ContentAuthorityConfigurationSha256) -or
        [string]::IsNullOrWhiteSpace($ContentAuthorityIssuerSpkiBase64) -or
        [string]::IsNullOrWhiteSpace($AuthorityVerifierAssemblyPath)) {
    Add-Failure 'O gate exige as duas autoridades publicas completas e o verificador compilado.'
}
else {
        try {
            if ($AuthorityConfigurationBase64.Length -gt 10924 -or
                $AuthorityIssuerSpkiBase64.Length -gt 1368 -or
                $AuthorityConfigurationBase64 -match '\s' -or
                $AuthorityIssuerSpkiBase64 -match '\s') {
                throw 'A representacao Base64 da autoridade e invalida.'
            }
            $authorityConfigurationBytes = [Convert]::FromBase64String(
                $AuthorityConfigurationBase64)
            $authorityIssuerSpkiBytes = [Convert]::FromBase64String(
                $AuthorityIssuerSpkiBase64)
            if ($authorityConfigurationBytes.Length -lt 64 -or
                $authorityConfigurationBytes.Length -gt 8KB -or
                $authorityIssuerSpkiBytes.Length -lt 256 -or
                $authorityIssuerSpkiBytes.Length -gt 1KB -or
                -not [Convert]::ToBase64String($authorityConfigurationBytes).Equals(
                    $AuthorityConfigurationBase64, [StringComparison]::Ordinal) -or
                -not [Convert]::ToBase64String($authorityIssuerSpkiBytes).Equals(
                    $AuthorityIssuerSpkiBase64, [StringComparison]::Ordinal)) {
                Add-Failure 'Os bytes da autoridade possuem tamanho invalido para Release.'
            }
            else {
                $expectedConfigurationHash = [Convert]::FromHexString(
                    $AuthorityConfigurationSha256)
                $actualConfigurationHash = [Security.Cryptography.SHA256]::HashData(
                    $authorityConfigurationBytes)
                try {
                    if (-not [Security.Cryptography.CryptographicOperations]::FixedTimeEquals(
                            $actualConfigurationHash,
                            $expectedConfigurationHash)) {
                        throw 'O envelope nao corresponde ao SHA-256 independente aprovado.'
                    }
                    $authorityConfigurationHash = [Convert]::ToHexString(
                        $actualConfigurationHash).ToLowerInvariant()
                }
                finally {
                    [Security.Cryptography.CryptographicOperations]::ZeroMemory(
                        $actualConfigurationHash)
                    [Security.Cryptography.CryptographicOperations]::ZeroMemory(
                        $expectedConfigurationHash)
                }
                $issuerHash = [Security.Cryptography.SHA256]::HashData(
                    $authorityIssuerSpkiBytes)
                try {
                    $authorityIssuerSpkiHash = [Convert]::ToHexString(
                        $issuerHash).ToLowerInvariant()
                }
                finally {
                    [Security.Cryptography.CryptographicOperations]::ZeroMemory($issuerHash)
                }
                $authorityEmbeddedValues = @(
                    [Convert]::ToBase64String($authorityConfigurationBytes),
                    $AuthorityConfigurationSha256,
                    [Convert]::ToBase64String($authorityIssuerSpkiBytes))

                $verifierAssembly = (Resolve-Path -LiteralPath $AuthorityVerifierAssemblyPath).Path
                $verifierInfo = Get-Item -LiteralPath $verifierAssembly -Force
                if ($verifierInfo.PSIsContainer -or
                    ($verifierInfo.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw 'O verificador criptografico da autoridade nao e um arquivo regular.'
                }
                $authorityVerificationOutput = @(& $DotNetPath $verifierAssembly `
                    '--verify-authority-base64' `
                    $AuthorityConfigurationBase64 $AuthorityIssuerSpkiBase64 `
                    $AuthorityConfigurationSha256 2>&1)
                if ($LASTEXITCODE -ne 0) {
                    Add-Failure 'A assinatura, identidade HTTPS ou vigencia da autoridade e invalida.'
                }
            }
        }
        catch {
            Add-Failure "A autoridade nao pode ser capturada exatamente: $($_.Exception.Message)"
        }

        try {
            if ($ContentAuthorityConfigurationBase64.Length -gt 10924 -or
                $ContentAuthorityIssuerSpkiBase64.Length -gt 1368 -or
                $ContentAuthorityConfigurationBase64 -match '\s' -or
                $ContentAuthorityIssuerSpkiBase64 -match '\s') {
                throw 'A representacao Base64 da autoridade de conteudo e invalida.'
            }
            $contentAuthorityConfigurationBytes = [Convert]::FromBase64String(
                $ContentAuthorityConfigurationBase64)
            $contentAuthorityIssuerSpkiBytes = [Convert]::FromBase64String(
                $ContentAuthorityIssuerSpkiBase64)
            if ($contentAuthorityConfigurationBytes.Length -lt 64 -or
                $contentAuthorityConfigurationBytes.Length -gt 8KB -or
                $contentAuthorityIssuerSpkiBytes.Length -lt 256 -or
                $contentAuthorityIssuerSpkiBytes.Length -gt 1KB -or
                -not [Convert]::ToBase64String(
                    $contentAuthorityConfigurationBytes).Equals(
                        $ContentAuthorityConfigurationBase64,
                        [StringComparison]::Ordinal) -or
                -not [Convert]::ToBase64String(
                    $contentAuthorityIssuerSpkiBytes).Equals(
                        $ContentAuthorityIssuerSpkiBase64,
                        [StringComparison]::Ordinal)) {
                Add-Failure 'Os bytes da autoridade de conteudo possuem tamanho invalido.'
            }
            else {
                $expectedContentConfigurationHash = [Convert]::FromHexString(
                    $ContentAuthorityConfigurationSha256)
                $actualContentConfigurationHash = `
                    [Security.Cryptography.SHA256]::HashData(
                        $contentAuthorityConfigurationBytes)
                try {
                    if (-not [Security.Cryptography.CryptographicOperations]::FixedTimeEquals(
                            $actualContentConfigurationHash,
                            $expectedContentConfigurationHash)) {
                        throw 'O envelope de conteudo nao corresponde ao SHA-256 aprovado.'
                    }
                    $contentAuthorityConfigurationHash = [Convert]::ToHexString(
                        $actualContentConfigurationHash).ToLowerInvariant()
                }
                finally {
                    [Security.Cryptography.CryptographicOperations]::ZeroMemory(
                        $actualContentConfigurationHash)
                    [Security.Cryptography.CryptographicOperations]::ZeroMemory(
                        $expectedContentConfigurationHash)
                }
                $contentIssuerHash = [Security.Cryptography.SHA256]::HashData(
                    $contentAuthorityIssuerSpkiBytes)
                try {
                    $contentAuthorityIssuerSpkiHash = [Convert]::ToHexString(
                        $contentIssuerHash).ToLowerInvariant()
                }
                finally {
                    [Security.Cryptography.CryptographicOperations]::ZeroMemory(
                        $contentIssuerHash)
                }
                $contentAuthorityEmbeddedValues = @(
                    [Convert]::ToBase64String($contentAuthorityConfigurationBytes),
                    $ContentAuthorityConfigurationSha256,
                    [Convert]::ToBase64String($contentAuthorityIssuerSpkiBytes))

                $contentAuthorityVerificationOutput = @(
                    & $DotNetPath $verifierAssembly `
                        '--verify-content-authority-base64' `
                        $ContentAuthorityConfigurationBase64 `
                        $ContentAuthorityIssuerSpkiBase64 `
                        $ContentAuthorityConfigurationSha256 2>&1)
                if ($LASTEXITCODE -ne 0) {
                    Add-Failure 'A assinatura ou vigencia da autoridade de conteudo e invalida.'
                }
            }
        }
        catch {
            Add-Failure "A autoridade de conteudo nao pode ser capturada: $($_.Exception.Message)"
        }
    }
if (-not $AllowUnsigned -and
    @($toolchainPinParameters | Where-Object {
            [string]::IsNullOrWhiteSpace($_)
        }).Count -ne 0) {
    Add-Failure 'O gate assinado exige todos os pins independentes de toolchain.'
}

if (-not ('Turborama.Release.BinaryNeedleScanner' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Turborama.Release
{
    public static class BinaryNeedleScanner
    {
        public static int FindFirst(string path, byte[][] needles)
        {
            if (needles == null || needles.Length == 0) return -1;
            var maximumNeedle = 0;
            foreach (var needle in needles)
            {
                if (needle == null || needle.Length == 0)
                    throw new ArgumentException("Empty binary marker.", nameof(needles));
                maximumNeedle = Math.Max(maximumNeedle, needle.Length);
            }

            const int blockSize = 1024 * 1024;
            var buffer = new byte[blockSize + maximumNeedle - 1];
            var carry = 0;
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                blockSize, FileOptions.SequentialScan);
            while (true)
            {
                var read = stream.Read(buffer, carry, blockSize);
                if (read == 0) return -1;
                var window = new ReadOnlySpan<byte>(buffer, 0, carry + read);
                for (var index = 0; index < needles.Length; index++)
                {
                    if (window.IndexOf(needles[index]) >= 0) return index;
                }

                carry = Math.Min(maximumNeedle - 1, window.Length);
                window.Slice(window.Length - carry, carry).CopyTo(buffer);
            }
        }

        public static string FindForbiddenDomainHash(
            string path,
            string[] forbiddenSha256)
        {
            if (forbiddenSha256 == null || forbiddenSha256.Length == 0)
                return null;
            var forbidden = new HashSet<string>(
                forbiddenSha256,
                StringComparer.OrdinalIgnoreCase);
            var bytes = File.ReadAllBytes(path);
            try
            {
                var match = FindAsciiDomainHash(bytes, forbidden);
                if (match != null) return match;
                match = FindUtf16DomainHash(bytes, 0, forbidden);
                return match ?? FindUtf16DomainHash(bytes, 1, forbidden);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }

        private static string FindAsciiDomainHash(
            byte[] bytes,
            HashSet<string> forbidden)
        {
            var candidate = new StringBuilder(253);
            foreach (var value in bytes)
            {
                if (IsDomainCharacter(value) && candidate.Length < 253)
                    candidate.Append((char)value);
                else
                {
                    var match = MatchCandidate(candidate, forbidden);
                    if (match != null) return match;
                    candidate.Clear();
                }
            }
            return MatchCandidate(candidate, forbidden);
        }

        private static string FindUtf16DomainHash(
            byte[] bytes,
            int offset,
            HashSet<string> forbidden)
        {
            var candidate = new StringBuilder(253);
            for (var index = offset; index + 1 < bytes.Length; index += 2)
            {
                var value = bytes[index];
                if (bytes[index + 1] == 0 && IsDomainCharacter(value) &&
                    candidate.Length < 253)
                {
                    candidate.Append((char)value);
                }
                else
                {
                    var match = MatchCandidate(candidate, forbidden);
                    if (match != null) return match;
                    candidate.Clear();
                }
            }
            return MatchCandidate(candidate, forbidden);
        }

        private static string MatchCandidate(
            StringBuilder candidate,
            HashSet<string> forbidden)
        {
            if (candidate.Length < 4 || candidate.Length > 253)
                return null;
            var value = candidate.ToString().ToLowerInvariant();
            if (!IsValidDomain(value)) return null;
            var hash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(value)));
            return forbidden.Contains(hash) ? hash : null;
        }

        private static bool IsDomainCharacter(byte value) =>
            value == (byte)'.' || value == (byte)'-' ||
            value >= (byte)'0' && value <= (byte)'9' ||
            value >= (byte)'A' && value <= (byte)'Z' ||
            value >= (byte)'a' && value <= (byte)'z';

        private static bool IsValidDomain(string value)
        {
            if (!value.Contains('.', StringComparison.Ordinal) ||
                value.StartsWith(".", StringComparison.Ordinal) ||
                value.EndsWith(".", StringComparison.Ordinal) ||
                value.Contains("..", StringComparison.Ordinal))
                return false;
            var labels = value.Split('.');
            if (labels.Length < 2) return false;
            foreach (var label in labels)
            {
                if (label.Length is < 1 or > 63 || label[0] == '-' ||
                    label[label.Length - 1] == '-')
                    return false;
            }
            var suffix = labels[labels.Length - 1];
            if (suffix.Length < 2) return false;
            foreach (var character in suffix)
            {
                if (character < 'a' || character > 'z') return false;
            }
            return true;
        }
    }
}
'@
}

$exe = Join-Path $package 'Turborama.exe'
$manifestPath = Join-Path $package 'RELEASE-MANIFEST.json'
$sbomPath = Join-Path $package 'Turborama.spdx.json'
$noticesPath = Join-Path $package 'THIRD-PARTY-NOTICES.txt'
$dotNetNoticesPath = Join-Path $package 'DOTNET-THIRD-PARTY-NOTICES.txt'
foreach ($required in @(
    $exe, $manifestPath, $sbomPath, $noticesPath, $dotNetNoticesPath)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        Add-Failure "Arquivo obrigatorio ausente: $([IO.Path]::GetFileName($required))"
    }
}

$signature = $null
if (Test-Path -LiteralPath $exe -PathType Leaf) {
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe)
    if ($version.FileVersion -ne '2.0.1.0') {
        Add-Failure "FileVersion inesperada: $($version.FileVersion)"
    }
    $signature = Microsoft.PowerShell.Security\Get-AuthenticodeSignature `
        -LiteralPath $exe
    if ($AllowUnsigned -and $signature.Status -ne 'NotSigned') {
        Add-Failure "O staging unsigned precisa estar sem assinatura: $($signature.Status)"
    }
    if (-not $AllowUnsigned -and $signature.Status -ne 'Valid') {
        Add-Failure "Assinatura Authenticode invalida ou ausente: $($signature.Status)"
    }
    if (-not $AllowUnsigned -and
        ($null -eq $signature.SignerCertificate -or
         -not $signature.SignerCertificate.Thumbprint.Equals(
             $CertificateThumbprint,
             [StringComparison]::OrdinalIgnoreCase))) {
        Add-Failure 'O certificado Authenticode nao corresponde ao thumbprint solicitado.'
    }
    if (-not $AllowUnsigned -and $null -eq $signature.TimeStamperCertificate) {
        Add-Failure 'O executavel assinado nao possui timestamp verificavel.'
    }
    if (-not $AllowUnsigned -and
        $null -ne $signature.TimeStamperCertificate -and
        -not $signature.TimeStamperCertificate.Thumbprint.Equals(
            $TimestampCertificateThumbprint,
            [StringComparison]::OrdinalIgnoreCase)) {
        Add-Failure 'O certificado de timestamp nao corresponde ao thumbprint aprovado.'
    }
}
$actualTimestampThumbprint = if ($null -ne $signature -and
    $null -ne $signature.TimeStamperCertificate) {
    [string]$signature.TimeStamperCertificate.Thumbprint
} else { '' }

if ((Test-Path -LiteralPath $exe -PathType Leaf) -and
    (Test-Path -LiteralPath $sbomPath -PathType Leaf)) {
    try {
        $sbom = Get-Content -LiteralPath $sbomPath -Raw | ConvertFrom-Json -Depth 32
        if ([string]$sbom.spdxVersion -ne 'SPDX-2.3' -or
            [string]$sbom.SPDXID -ne 'SPDXRef-DOCUMENT') {
            Add-Failure 'Documento SBOM nao e SPDX 2.3 valido para esta Release.'
        }
        $applicationPackages = @($sbom.packages | Where-Object {
            [string]$_.SPDXID -eq 'SPDXRef-Package-Turborama'
        })
        if ($applicationPackages.Count -ne 1) {
            Add-Failure 'SBOM precisa descrever exatamente um pacote Turborama.'
        }
        else {
            $applicationPackage = $applicationPackages[0]
            if ([string]$applicationPackage.versionInfo -ne '2.0.1') {
                Add-Failure "Versao inesperada no SBOM: $($applicationPackage.versionInfo)"
            }
            $exeSha256 = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
            $sbomExeHashes = @($applicationPackage.checksums | Where-Object {
                [string]$_.algorithm -eq 'SHA256' -and
                ([string]$_.checksumValue).Equals(
                    $exeSha256,
                    [StringComparison]::OrdinalIgnoreCase)
            })
            if ($sbomExeHashes.Count -ne 1) {
                Add-Failure 'O SHA-256 do executavel final nao corresponde ao SBOM.'
            }
        }
        $sharpCompress = @($sbom.packages | Where-Object {
            [string]$_.name -eq 'SharpCompress' -and
            [string]$_.versionInfo -eq '0.50.4'
        })
        if ($sharpCompress.Count -ne 1) {
            Add-Failure 'SBOM nao fixa exatamente SharpCompress 0.50.4.'
        }
        $systemManagement = @($sbom.packages | Where-Object {
            [string]$_.name -eq 'System.Management' -and
            [string]$_.versionInfo -eq '10.0.11'
        })
        if ($systemManagement.Count -ne 1) {
            Add-Failure 'SBOM nao fixa exatamente System.Management 10.0.11.'
        }
        else {
            $systemManagementEntry = $systemManagement[0]
            $expectedSystemManagementSha512 = [Convert]::ToHexString(
                [Convert]::FromBase64String(
                    'xyNn8KGbWI88LoUwg3rB8qcpFFST6dr8Ro/qS8GBu2GOwR0v7J82kVFHTiiPtvEKS79VbMTxs/sIKQ+Cq1Zs1g=='))
            $systemManagementChecksums = @(
                $systemManagementEntry.checksums | Where-Object {
                    [string]$_.algorithm -eq 'SHA512' -and
                    [string]$_.checksumValue -eq $expectedSystemManagementSha512
                })
            $systemManagementPurl = @(
                $systemManagementEntry.externalRefs | Where-Object {
                    [string]$_.referenceCategory -eq 'PACKAGE-MANAGER' -and
                    [string]$_.referenceType -eq 'purl' -and
                    [string]$_.referenceLocator -eq
                        'pkg:nuget/System.Management@10.0.11'
                })
            $systemManagementRelations = @($sbom.relationships | Where-Object {
                [string]$_.spdxElementId -eq 'SPDXRef-Package-Turborama' -and
                [string]$_.relationshipType -eq 'DEPENDS_ON' -and
                [string]$_.relatedSpdxElement -eq
                    'SPDXRef-Package-System.Management'
            })
            if ([string]$systemManagementEntry.licenseDeclared -ne 'MIT' -or
                [string]$systemManagementEntry.licenseConcluded -ne 'MIT' -or
                $systemManagementChecksums.Count -ne 1 -or
                $systemManagementPurl.Count -ne 1 -or
                $systemManagementRelations.Count -ne 1) {
                Add-Failure 'SBOM de System.Management nao preserva licença, hash, purl e relacao DEPENDS_ON esperados.'
            }
        }
        foreach ($runtimePackage in @(
            'Microsoft.NETCore.App.Runtime.win-x64',
            'Microsoft.WindowsDesktop.App.Runtime.win-x64',
            'Microsoft.AspNetCore.App.Runtime.win-x64')) {
            $runtimeEntries = @($sbom.packages | Where-Object {
                [string]$_.name -eq $runtimePackage -and
                [string]$_.versionInfo -eq '10.0.11'
            })
            if ($runtimeEntries.Count -ne 1) {
                Add-Failure "SBOM nao inventaria o runtime pack exato: $runtimePackage 10.0.11."
            }
        }
    }
    catch {
        Add-Failure "SBOM ilegivel ou invalido: $($_.Exception.Message)"
    }
}

if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
    $manifest = $null
    try {
        $manifestJson = Get-Content -LiteralPath $manifestPath -Raw
        $jsonOptions = [Text.Json.JsonDocumentOptions]::new()
        $jsonOptions.AllowTrailingCommas = $false
        $jsonOptions.CommentHandling = [Text.Json.JsonCommentHandling]::Disallow
        $jsonOptions.MaxDepth = 32
        $manifestDocument = [Text.Json.JsonDocument]::Parse($manifestJson, $jsonOptions)
        try {
            Test-ReleaseManifestJsonShape $manifestDocument.RootElement '$'
        }
        finally {
            $manifestDocument.Dispose()
        }
        $manifest = $manifestJson | ConvertFrom-Json -Depth 32
    }
    catch {
        Add-Failure "Manifesto de Release possui JSON invalido: $($_.Exception.Message)"
    }
    if ($null -ne $manifest) {
    if ($manifest.schema -ne 'turborama.release-manifest/v1') {
        Add-Failure 'Schema do manifesto de Release invalido.'
    }
    if ($manifest.product -ne 'TURBORAMA_SUITE' -or
        $manifest.version -ne '2.0.1' -or
        $manifest.runtime -ne 'win-x64' -or
        $manifest.selfContained -ne $true) {
        Add-Failure 'Identidade, versao ou runtime do manifesto de Release e invalido.'
    }
    if ($AllowUnsigned -and $manifest.unsigned -ne $true) {
        Add-Failure 'O staging permitido precisa estar explicitamente marcado como unsigned.'
    }
    if (-not $AllowUnsigned -and $manifest.unsigned -ne $false) {
        Add-Failure 'Um candidato assinado precisa declarar unsigned=false exatamente.'
    }
    if ($null -eq $manifest.authority) {
        Add-Failure 'Pacote sem hashes da configuracao de autoridade.'
    }
    if ($null -eq $manifest.contentAuthority) {
        Add-Failure 'Pacote sem hashes da autoridade de conteudo.'
    }
    $expectedCommit = (& $GitPath -c 'core.fsmonitor=false' -c 'core.hooksPath=NUL' `
        -C $root rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $expectedCommit -notmatch '^[0-9a-f]{40}$') {
        Add-Failure 'Nao foi possivel determinar o commit esperado do pacote.'
    }
    elseif ([string]$manifest.source.commit -ne $expectedCommit) {
        Add-Failure 'O manifesto nao aponta para o commit exato usado pelo gate.'
    }
    if ([string]$manifest.toolchain.dotnetSdk -ne '10.0.400') {
        Add-Failure "SDK inesperado no manifesto: $($manifest.toolchain.dotnetSdk)"
    }
    if ($AllowUnsigned) {
        foreach ($unsignedToolchainField in @(
                'powerShellExecutableSha256',
                'powerShellHomeTreeSha256',
                'gitExecutableSha256',
                'gitTreeSha256',
                'dotnetSdkTreeSha256',
                'signToolExecutableSha256',
                'releaseTagPublicKeySha256')) {
            if ($null -ne $manifest.toolchain.$unsignedToolchainField) {
                Add-Failure "Staging unsigned declarou pin de toolchain: $unsignedToolchainField"
            }
        }
        if ($manifest.authenticode.required -ne $false -or
            [string]$manifest.authenticode.status -ne 'NotSigned' -or
            $null -ne $manifest.authenticode.signerThumbprint -or
            $null -ne $manifest.authenticode.timestampThumbprint) {
            Add-Failure 'Bloco Authenticode do manifesto unsigned e invalido ou inconsistente.'
        }
    }
    else {
        $expectedToolchainPins = [ordered]@{
            powerShellExecutableSha256 = $PowerShellSha256
            powerShellHomeTreeSha256 = $PowerShellHomeTreeSha256
            gitExecutableSha256 = $GitSha256
            gitTreeSha256 = $GitTreeSha256
            dotnetSdkTreeSha256 = $DotNetSdkTreeSha256
            signToolExecutableSha256 = $SignToolSha256
            releaseTagPublicKeySha256 = $ReleaseTagPublicKeySha256
        }
        foreach ($expectedToolchainPin in $expectedToolchainPins.GetEnumerator()) {
            if (-not ([string]$manifest.toolchain.($expectedToolchainPin.Key)).Equals(
                    $expectedToolchainPin.Value,
                    [StringComparison]::OrdinalIgnoreCase)) {
                Add-Failure "Pin de toolchain divergente no manifesto: $($expectedToolchainPin.Key)"
            }
        }
        if ($manifest.source.dirty -ne $false -or
            [string]$manifest.source.tag -ne 'v2.0.1' -or
            [string]$manifest.source.branch -ne 'main') {
            Add-Failure 'Manifesto assinado exige snapshot limpo, branch main e tag v2.0.1.'
        }
        $manifestRepository = ([string]$manifest.source.repository).TrimEnd('/')
        if ($manifestRepository -notin @(
            'https://github.com/luziellacerda/TRUBORAMA-SUITE.git',
            'https://github.com/luziellacerda/TRUBORAMA-SUITE')) {
            Add-Failure "Repositorio inesperado no manifesto: $manifestRepository"
        }
        if ($manifest.authenticode.required -ne $true -or
            [string]$manifest.authenticode.status -ne 'Valid' -or
            -not ([string]$manifest.authenticode.signerThumbprint).Equals(
                $CertificateThumbprint,
                [StringComparison]::OrdinalIgnoreCase) -or
            [string]::IsNullOrWhiteSpace($actualTimestampThumbprint) -or
            -not ([string]$manifest.authenticode.timestampThumbprint).Equals(
                $actualTimestampThumbprint,
                [StringComparison]::OrdinalIgnoreCase)) {
            Add-Failure 'Bloco Authenticode do manifesto e invalido ou inconsistente.'
        }
    }

    if ($null -ne $manifest.authority -and
        -not [string]::IsNullOrWhiteSpace($authorityConfigurationHash) -and
        -not [string]::IsNullOrWhiteSpace($authorityIssuerSpkiHash)) {
        if (-not ([string]$manifest.authority.configurationSha256).Equals(
                $AuthorityConfigurationSha256,
                [StringComparison]::Ordinal) -or
            -not ([string]$manifest.authority.issuerSpkiSha256).Equals(
                $authorityIssuerSpkiHash,
                [StringComparison]::Ordinal)) {
            Add-Failure 'Hashes da autoridade no manifesto nao correspondem aos bytes incorporados.'
        }
    }
    else {
        Add-Failure 'Os bytes congelados da autoridade nao foram fornecidos ao gate.'
    }

    if ($null -ne $manifest.contentAuthority -and
        -not [string]::IsNullOrWhiteSpace(
            $contentAuthorityConfigurationHash) -and
        -not [string]::IsNullOrWhiteSpace(
            $contentAuthorityIssuerSpkiHash)) {
        if (-not ([string]$manifest.contentAuthority.configurationSha256).Equals(
                $ContentAuthorityConfigurationSha256,
                [StringComparison]::Ordinal) -or
            -not ([string]$manifest.contentAuthority.issuerSpkiSha256).Equals(
                $contentAuthorityIssuerSpkiHash,
                [StringComparison]::Ordinal)) {
            Add-Failure 'Hashes da autoridade de conteudo nao correspondem aos bytes incorporados.'
        }
    }
    else {
        Add-Failure 'A autoridade de conteudo congelada nao foi fornecida ao gate.'
    }

    $listed = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in @($manifest.files)) {
        $relative = ([string]$entry.path).Replace('/', [IO.Path]::DirectorySeparatorChar)
        $candidate = [IO.Path]::GetFullPath((Join-Path $package $relative))
        $prefix = $package.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if (-not $candidate.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
            Add-Failure "Caminho fora do pacote no manifesto: $relative"
            continue
        }
        if (-not $listed.Add([IO.Path]::GetRelativePath($package, $candidate))) {
            Add-Failure "Caminho duplicado no manifesto: $relative"
        }
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            Add-Failure "Arquivo listado ausente: $relative"
            continue
        }
        $info = Get-Item -LiteralPath $candidate
        if ($info.Length -ne [long]$entry.bytes) {
            Add-Failure "Tamanho divergente: $relative"
        }
        $hash = (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash
        if (-not $hash.Equals([string]$entry.sha256, [StringComparison]::OrdinalIgnoreCase)) {
            Add-Failure "SHA-256 divergente: $relative"
        }
    }
    foreach ($file in Get-ChildItem -LiteralPath $package -Recurse -File -Force) {
        if ($file.FullName.Equals($manifestPath, [StringComparison]::OrdinalIgnoreCase)) { continue }
        $relative = [IO.Path]::GetRelativePath($package, $file.FullName)
        if (-not $listed.Contains($relative)) { Add-Failure "Arquivo nao inventariado: $relative" }
    }
    }
}

$catalogPath = Join-Path $package 'Assets\Catalog\catalog.json'
if (-not (Test-Path -LiteralPath $catalogPath -PathType Leaf)) {
    Add-Failure 'Catalogo publico ausente do pacote.'
}
else {
    $catalog = Get-Content -LiteralPath $catalogPath -Raw | ConvertFrom-Json -Depth 32
    if ($catalog.PSObject.Properties.Name -contains 'enableTestDownloads' -or
        $catalog.PSObject.Properties.Name -contains 'testDownload') {
        Add-Failure 'Contrato legado de download de teste presente no pacote.'
    }
    $permanentUrls = @($catalog.items | Where-Object {
        $_.PSObject.Properties.Name -contains 'downloadUrl' -and
        -not [string]::IsNullOrWhiteSpace([string]$_.downloadUrl)
    })
    if ($permanentUrls.Count -ne 0) { Add-Failure 'Catalogo publico contem URL permanente.' }
}

$imageCount = @(Get-ChildItem -LiteralPath (Join-Path $package 'Assets\Catalog\Images') -File -Force -ErrorAction SilentlyContinue).Count
if ($imageCount -ne 903) { Add-Failure "Quantidade inesperada de capas: $imageCount (esperado 903)." }
$iconCount = @(Get-ChildItem -LiteralPath (Join-Path $package 'Assets\Catalog\SystemIcons') -File -Filter '*.png' -Force -ErrorAction SilentlyContinue).Count
if ($iconCount -ne 45) { Add-Failure "Quantidade inesperada de icones: $iconCount (esperado 45)." }
$menuIconCount = @(Get-ChildItem -LiteralPath (Join-Path $package 'Assets\Catalog\MenuIcons') -File -Filter '*.png' -Force -ErrorAction SilentlyContinue).Count
if ($menuIconCount -ne 22) { Add-Failure "Quantidade inesperada de icones de menu: $menuIconCount (esperado 22)." }
$descriptionCount = @(Get-ChildItem -LiteralPath (Join-Path $package 'Assets\Catalog\GameDescriptions') -File -Filter '*.xml' -Force -ErrorAction SilentlyContinue).Count
if ($descriptionCount -ne 22) { Add-Failure "Quantidade inesperada de XML de descricao: $descriptionCount (esperado 22)." }
$videoDirectory = Join-Path $package 'Assets\Catalog\SystemVideos'
$videoCount = @(Get-ChildItem -LiteralPath $videoDirectory -File -Filter '*.mp4' -Force -ErrorAction SilentlyContinue).Count
if ($videoCount -ne 38) { Add-Failure "Quantidade inesperada de videos de sistema: $videoCount (esperado 38)." }

$backgroundDirectory = Join-Path $package 'Assets\BackgroundVideos'
$backgroundCount = @(Get-ChildItem -LiteralPath $backgroundDirectory -File -Filter '*.mp4' -Force -ErrorAction SilentlyContinue).Count
if ($backgroundCount -ne 15) {
    Add-Failure "Quantidade inesperada de videos de fundo: $backgroundCount (esperado 15)."
}

$integrityPath = Join-Path $root 'Assets\Catalog\SystemVideos\system-video-integrity.json'
if (-not (Test-Path -LiteralPath $integrityPath -PathType Leaf)) {
    Add-Failure 'Manifesto-fonte de integridade dos videos de sistema ausente.'
}
elseif (Test-Path -LiteralPath $videoDirectory -PathType Container) {
    $integrity = Get-Content -LiteralPath $integrityPath -Raw | ConvertFrom-Json -Depth 8
    foreach ($property in $integrity.PSObject.Properties) {
        $videoPath = Join-Path $videoDirectory $property.Name
        if (-not (Test-Path -LiteralPath $videoPath -PathType Leaf)) {
            Add-Failure "Video inventariado ausente: $($property.Name)"
            continue
        }
        $videoInfo = Get-Item -LiteralPath $videoPath
        if ($videoInfo.Length -ne [long]$property.Value.length) {
            Add-Failure "Tamanho de video divergente: $($property.Name)"
        }
        $videoHash = (Get-FileHash -LiteralPath $videoPath -Algorithm SHA256).Hash
        if (-not $videoHash.Equals([string]$property.Value.sha256, [StringComparison]::OrdinalIgnoreCase)) {
            Add-Failure "SHA-256 de video divergente: $($property.Name)"
        }
    }
}

$backgroundIntegrityPath = Join-Path $root 'Assets\BackgroundVideos\background-video-integrity.json'
if (-not (Test-Path -LiteralPath $backgroundIntegrityPath -PathType Leaf)) {
    Add-Failure 'Manifesto-fonte de integridade dos videos de fundo ausente.'
}
elseif (Test-Path -LiteralPath $backgroundDirectory -PathType Container) {
    $backgroundIntegrity = Get-Content -LiteralPath $backgroundIntegrityPath -Raw | ConvertFrom-Json -Depth 8
    foreach ($property in $backgroundIntegrity.PSObject.Properties) {
        $backgroundPath = Join-Path $backgroundDirectory $property.Name
        if (-not (Test-Path -LiteralPath $backgroundPath -PathType Leaf)) {
            Add-Failure "Video de fundo inventariado ausente: $($property.Name)"
            continue
        }
        $backgroundInfo = Get-Item -LiteralPath $backgroundPath
        if ($backgroundInfo.Length -ne [long]$property.Value.length) {
            Add-Failure "Tamanho de video de fundo divergente: $($property.Name)"
        }
        $backgroundHash = (Get-FileHash -LiteralPath $backgroundPath -Algorithm SHA256).Hash
        if (-not $backgroundHash.Equals([string]$property.Value.sha256, [StringComparison]::OrdinalIgnoreCase)) {
            Add-Failure "SHA-256 de video de fundo divergente: $($property.Name)"
        }
    }
}

$allowedPackagePath = '^(?:Turborama\.exe|Turborama\.spdx\.json|THIRD-PARTY-NOTICES\.txt|DOTNET-THIRD-PARTY-NOTICES\.txt|RELEASE-MANIFEST\.json|Assets/Catalog/catalog\.json|Assets/Catalog/Images/[A-Za-z0-9._-]+\.jpg|Assets/Catalog/MenuIcons/[A-Za-z0-9._-]+\.png|Assets/Catalog/SystemIcons/[A-Za-z0-9._-]+\.png|Assets/Catalog/GameDescriptions/[A-Za-z0-9._-]+\.xml|Assets/Catalog/SystemVideos/[A-Za-z0-9._-]+\.mp4|Assets/BackgroundVideos/[A-Za-z0-9._-]+\.mp4)$'
foreach ($entry in Get-ChildItem -LiteralPath $package -Recurse -Force) {
    if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        Add-Failure "Reparse point proibido no pacote: $([IO.Path]::GetRelativePath($package, $entry.FullName))"
    }
    $forbiddenAttributes = [IO.FileAttributes]::Hidden -bor
        [IO.FileAttributes]::System -bor
        [IO.FileAttributes]::Offline -bor
        [IO.FileAttributes]::Device
    if (($entry.Attributes -band $forbiddenAttributes) -ne 0) {
        Add-Failure "Atributo de arquivo proibido no pacote: $([IO.Path]::GetRelativePath($package, $entry.FullName))"
    }
    if (-not $entry.PSIsContainer) {
        $relativePackagePath = [IO.Path]::GetRelativePath(
            $package,
            $entry.FullName).Replace('\', '/')
        if ($relativePackagePath -notmatch $allowedPackagePath) {
            Add-Failure "Arquivo fora da allowlist do pacote: $relativePackagePath"
        }
    }
}

if ((Test-Path -LiteralPath $noticesPath -PathType Leaf) -and
    ((Get-Item -LiteralPath $noticesPath).Length -lt 100 -or
     (Get-Content -LiteralPath $noticesPath -Raw) -notmatch '(?i)SharpCompress[\s\S]+MIT' -or
     (Get-Content -LiteralPath $noticesPath -Raw) -notmatch
        '(?is)System\.Management 10\.0\.11.{0,300}Microsoft Corporation.{0,120}License: MIT')) {
    Add-Failure 'THIRD-PARTY-NOTICES esta vazio ou nao declara SharpCompress/System.Management sob MIT.'
}
if ((Test-Path -LiteralPath $dotNetNoticesPath -PathType Leaf) -and
    (Get-FileHash -LiteralPath $dotNetNoticesPath -Algorithm SHA256).Hash -ne
        '6D15E10A101C6BFFF2AB4429ED061BF76C456FC4B23AD6B03E0D0F8377148A21') {
    Add-Failure 'DOTNET-THIRD-PARTY-NOTICES nao corresponde ao notice upstream de System.Management 10.0.11.'
}

$forbiddenNames = @('key.txt', 'drawers.json', 'config.json', 'catalog.full.json', 'private-catalog.bin')
foreach ($file in Get-ChildItem -LiteralPath $package -Recurse -File -Force) {
    if ($forbiddenNames -contains $file.Name.ToLowerInvariant()) {
        Add-Failure "Arquivo privado proibido no pacote: $($file.Name)"
    }
}

if (Test-Path -LiteralPath $exe -PathType Leaf) {
    $forbiddenMarkers = @(
        'PrivateCatalogSecrets',
        'PRIVATE_CATALOG_EMBEDDED'
    )
    $needles = [System.Collections.Generic.List[byte[]]]::new()
    $needleLabels = [System.Collections.Generic.List[string]]::new()
    foreach ($marker in $forbiddenMarkers) {
        $needles.Add([Text.Encoding]::ASCII.GetBytes($marker))
        $needleLabels.Add($marker)
        $needles.Add([Text.Encoding]::Unicode.GetBytes($marker))
        $needleLabels.Add($marker)
    }
    $matchIndex = [Turborama.Release.BinaryNeedleScanner]::FindFirst(
        $exe,
        $needles.ToArray())
    if ($matchIndex -ge 0) {
        Add-Failure "Marcador proibido encontrado no executavel: $($needleLabels[$matchIndex])"
    }

    # Somente hashes ficam no fonte. Os nomes privados nunca entram no Git,
    # no manifesto nem nas mensagens do gate.
    $forbiddenOriginHostSha256 = @(
        '122193b87a9c6128a80c0e7ba0b6ccec8162c744047dcbc12786a6f7bc901d53',
        '04fea9ab06778f5d71fac78119ba36fe3d55408a26990b63da74c14967674ec4',
        'f12eaaa2ea14626453e54033b30c23843e2b01a2f47b2f9764ed1a0a639e01cd'
    )
    $forbiddenDomainHash =
        [Turborama.Release.BinaryNeedleScanner]::FindForbiddenDomainHash(
            $exe,
            $forbiddenOriginHostSha256)
    if (-not [string]::IsNullOrEmpty($forbiddenDomainHash)) {
        Add-Failure 'Dominio de origem privado encontrado no executavel.'
    }

    if ($authorityEmbeddedValues.Count -eq 3) {
        foreach ($embeddedValue in $authorityEmbeddedValues) {
            $embeddedNeedle = [Text.Encoding]::ASCII.GetBytes($embeddedValue)
            if ([Turborama.Release.BinaryNeedleScanner]::FindFirst(
                    $exe,
                    [byte[][]](,$embeddedNeedle)) -ne 0) {
                Add-Failure 'Metadado de autoridade nao foi incorporado exatamente.'
            }
        }
    }
    if ($contentAuthorityEmbeddedValues.Count -eq 3) {
        foreach ($embeddedValue in $contentAuthorityEmbeddedValues) {
            $embeddedNeedle = [Text.Encoding]::ASCII.GetBytes($embeddedValue)
            if ([Turborama.Release.BinaryNeedleScanner]::FindFirst(
                    $exe,
                    [byte[][]](,$embeddedNeedle)) -ne 0) {
                Add-Failure 'Metadado da autoridade de conteudo nao foi incorporado exatamente.'
            }
        }
    }
}

if ($null -ne $authorityConfigurationBytes) {
    [Security.Cryptography.CryptographicOperations]::ZeroMemory($authorityConfigurationBytes)
}
if ($null -ne $authorityIssuerSpkiBytes) {
    [Security.Cryptography.CryptographicOperations]::ZeroMemory($authorityIssuerSpkiBytes)
}
if ($null -ne $contentAuthorityConfigurationBytes) {
    [Security.Cryptography.CryptographicOperations]::ZeroMemory(
        $contentAuthorityConfigurationBytes)
}
if ($null -ne $contentAuthorityIssuerSpkiBytes) {
    [Security.Cryptography.CryptographicOperations]::ZeroMemory(
        $contentAuthorityIssuerSpkiBytes)
}

if ($failures.Count -ne 0) {
    Write-Error ("Pacote publicado reprovado:`n - " + ($failures -join "`n - ")) `
        -ErrorAction Continue
    exit 30
}

if ($AllowUnsigned) {
    Write-Host 'PASS: staging, hashes, capas, icones, videos, SBOM, autoridades e ausencia de Authenticode foram validados.'
}
else {
    Write-Host 'PASS: candidato, hashes, capas, icones, videos, SBOM, autoridade e Authenticode foram validados.'
}
