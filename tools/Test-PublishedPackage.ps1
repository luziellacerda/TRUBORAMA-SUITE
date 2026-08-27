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
                    'authority', 'files')
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
$authorityConfigurationBytes = $null
$authorityIssuerSpkiBytes = $null
$authorityConfigurationHash = ''
$authorityIssuerSpkiHash = ''
$authorityEmbeddedValues = @()
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

if ($AllowUnsigned -and $hasAuthorityInput) {
    Add-Failure 'Um staging unsigned nao pode receber configuracao de autoridade.'
}
if ($AllowUnsigned -and $hasAnyToolchainPin) {
    Add-Failure 'Um staging unsigned nao pode receber pins de toolchain de producao.'
}
if (-not $AllowUnsigned) {
    if ([string]::IsNullOrWhiteSpace($CertificateThumbprint) -or
        [string]::IsNullOrWhiteSpace($TimestampCertificateThumbprint) -or
        -not $hasAuthorityInput -or
        [string]::IsNullOrWhiteSpace($AuthorityConfigurationBase64) -or
        [string]::IsNullOrWhiteSpace($AuthorityConfigurationSha256) -or
        [string]::IsNullOrWhiteSpace($AuthorityIssuerSpkiBase64) -or
        [string]::IsNullOrWhiteSpace($AuthorityVerifierAssemblyPath)) {
        Add-Failure 'O gate assinado exige thumbprint, autoridade Base64 e o verificador compilado.'
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
    }
    if (@($toolchainPinParameters | Where-Object {
            [string]::IsNullOrWhiteSpace($_)
        }).Count -ne 0) {
        Add-Failure 'O gate assinado exige todos os pins independentes de toolchain.'
    }
}

if (-not ('Turborama.Release.BinaryNeedleScanner' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.IO;

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
    }
}
'@
}

$exe = Join-Path $package 'Turborama.exe'
$manifestPath = Join-Path $package 'RELEASE-MANIFEST.json'
$sbomPath = Join-Path $package 'Turborama.spdx.json'
$noticesPath = Join-Path $package 'THIRD-PARTY-NOTICES.txt'
foreach ($required in @($exe, $manifestPath, $sbomPath, $noticesPath)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        Add-Failure "Arquivo obrigatorio ausente: $([IO.Path]::GetFileName($required))"
    }
}

$signature = $null
if (Test-Path -LiteralPath $exe -PathType Leaf) {
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe)
    if ($version.FileVersion -ne '2.0.0.0') {
        Add-Failure "FileVersion inesperada: $($version.FileVersion)"
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $exe
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
            if ([string]$applicationPackage.versionInfo -ne '2.0.0') {
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
        $manifest.version -ne '2.0.0' -or
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
    if (-not $AllowUnsigned -and $null -eq $manifest.authority) {
        Add-Failure 'Pacote de producao sem hashes da configuracao de autoridade.'
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
    }
    if (-not $AllowUnsigned) {
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
            [string]$manifest.source.tag -ne 'v2.0.0' -or
            [string]$manifest.source.branch -ne 'main') {
            Add-Failure 'Manifesto assinado exige snapshot limpo, branch main e tag v2.0.0.'
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
if ($imageCount -ne 851) { Add-Failure "Quantidade inesperada de capas: $imageCount (esperado 851)." }
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

$allowedPackagePath = '^(?:Turborama\.exe|Turborama\.spdx\.json|THIRD-PARTY-NOTICES\.txt|RELEASE-MANIFEST\.json|Assets/Catalog/catalog\.json|Assets/Catalog/Images/[A-Za-z0-9._-]+\.jpg|Assets/Catalog/MenuIcons/[A-Za-z0-9._-]+\.png|Assets/Catalog/SystemIcons/[A-Za-z0-9._-]+\.png|Assets/Catalog/GameDescriptions/[A-Za-z0-9._-]+\.xml|Assets/Catalog/SystemVideos/[A-Za-z0-9._-]+\.mp4|Assets/BackgroundVideos/[A-Za-z0-9._-]+\.mp4)$'
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
     (Get-Content -LiteralPath $noticesPath -Raw) -notmatch '(?i)SharpCompress[\s\S]+MIT')) {
    Add-Failure 'THIRD-PARTY-NOTICES esta vazio ou nao declara SharpCompress/MIT.'
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
        'PRIVATE_CATALOG_EMBEDDED',
        'miami.sambox.buzz',
        'detroit.sambox.club',
        'cucunot.sambox.club'
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

    if (-not $AllowUnsigned -and $authorityEmbeddedValues.Count -eq 3) {
        foreach ($embeddedValue in $authorityEmbeddedValues) {
            $embeddedNeedle = [Text.Encoding]::ASCII.GetBytes($embeddedValue)
            if ([Turborama.Release.BinaryNeedleScanner]::FindFirst(
                    $exe,
                    [byte[][]](,$embeddedNeedle)) -ne 0) {
                Add-Failure 'Metadado de autoridade nao foi incorporado exatamente.'
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

if ($failures.Count -ne 0) {
    Write-Error ("Pacote publicado reprovado:`n - " + ($failures -join "`n - ")) `
        -ErrorAction Continue
    exit 30
}

if ($AllowUnsigned) {
    Write-Host 'PASS: staging, hashes, capas, icones, videos, SBOM e ausencia de Authenticode foram validados.'
}
else {
    Write-Host 'PASS: candidato, hashes, capas, icones, videos, SBOM, autoridade e Authenticode foram validados.'
}
