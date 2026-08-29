[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PackageRoot,

    [Parameter(Mandatory)]
    [string]$OutputPath,

    [switch]$Unsigned,

    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),

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

    [string]$SourceBranch = '',

    [string]$AuthorityConfigurationBase64 = '',

    [ValidatePattern('\A(?:|[0-9a-f]{64})\z')]
    [string]$AuthorityConfigurationSha256 = '',

    [string]$AuthorityIssuerSpkiBase64 = '',

    [string]$ContentAuthorityConfigurationBase64 = '',

    [ValidatePattern('\A(?:|[0-9a-f]{64})\z')]
    [string]$ContentAuthorityConfigurationSha256 = '',

    [string]$ContentAuthorityIssuerSpkiBase64 = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$package = (Resolve-Path -LiteralPath $PackageRoot).Path
$outputFullPath = [IO.Path]::GetFullPath($OutputPath)
$packagePrefix = $package.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $outputFullPath.StartsWith($packagePrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'O manifesto precisa ficar dentro do pacote.'
}

$projectPath = Join-Path $root 'TurboBoxManager.csproj'
[xml]$project = Get-Content -LiteralPath $projectPath -Raw
$version = [string](@($project.Project.PropertyGroup.Version) | Select-Object -First 1)
$gitArguments = @('-c', 'core.fsmonitor=false', '-c', 'core.hooksPath=NUL')
$commit = (& $GitPath @gitArguments -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$') {
    throw 'Nao foi possivel identificar o commit do pacote.'
}
$detectedBranch = (& $GitPath @gitArguments -C $root branch --show-current).Trim()
$branch = if ([string]::IsNullOrWhiteSpace($SourceBranch)) {
    $detectedBranch
} else {
    $SourceBranch
}
$tags = @(& $GitPath @gitArguments -C $root tag --points-at HEAD)
$expectedTag = "v$version"
$tag = if ($tags -contains $expectedTag) {
    $expectedTag
} else {
    [string](@($tags | Sort-Object -CaseSensitive) | Select-Object -First 1)
}
$dirty = @(& $GitPath @gitArguments -C $root `
    status --porcelain --untracked-files=all).Count -ne 0
$repositoryUrl = (& $GitPath @gitArguments -C $root remote get-url origin).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($repositoryUrl)) {
    throw 'Nao foi possivel identificar o remote origin do pacote.'
}
$dotnetVersion = (& $DotNetPath --version).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Nao foi possivel identificar o SDK .NET.' }

$toolchainPins = @(
    $PowerShellSha256,
    $PowerShellHomeTreeSha256,
    $GitSha256,
    $GitTreeSha256,
    $DotNetSdkTreeSha256,
    $SignToolSha256,
    $ReleaseTagPublicKeySha256)
$hasAnyToolchainPin = @($toolchainPins | Where-Object {
    -not [string]::IsNullOrWhiteSpace($_)
}).Count -ne 0
if ($Unsigned -and $hasAnyToolchainPin) {
    throw 'Um staging unsigned nao pode declarar pins de toolchain de producao.'
}
if (-not $Unsigned -and
    @($toolchainPins | Where-Object { [string]::IsNullOrWhiteSpace($_) }).Count -ne 0) {
    throw 'Um candidato assinado exige todos os pins de toolchain aprovados.'
}

$exePath = Join-Path $package 'Turborama.exe'
if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
    throw 'Turborama.exe ausente no pacote.'
}
$signature = Get-AuthenticodeSignature -LiteralPath $exePath
$signatureBlock = [ordered]@{
    required = -not $Unsigned.IsPresent
    status = [string]$signature.Status
    signerThumbprint = if ($null -ne $signature.SignerCertificate) {
        $signature.SignerCertificate.Thumbprint
    } else { $null }
    timestampThumbprint = if ($null -ne $signature.TimeStamperCertificate) {
        $signature.TimeStamperCertificate.Thumbprint
    } else { $null }
}

$authorityBlock = $null
$contentAuthorityBlock = $null
$hasAuthorityInput = -not [string]::IsNullOrWhiteSpace($AuthorityConfigurationBase64) -or
    -not [string]::IsNullOrWhiteSpace($AuthorityConfigurationSha256) -or
    -not [string]::IsNullOrWhiteSpace($AuthorityIssuerSpkiBase64)
$hasContentAuthorityInput = `
    -not [string]::IsNullOrWhiteSpace($ContentAuthorityConfigurationBase64) -or
    -not [string]::IsNullOrWhiteSpace($ContentAuthorityConfigurationSha256) -or
    -not [string]::IsNullOrWhiteSpace($ContentAuthorityIssuerSpkiBase64)
if ($Unsigned -and ($hasAuthorityInput -or $hasContentAuthorityInput)) {
    throw 'Um staging unsigned nao pode declarar configuracao de autoridade.'
}
if (-not $Unsigned -and (-not $hasAuthorityInput -or
        -not $hasContentAuthorityInput)) {
    throw 'Um candidato assinado exige as duas autoridades capturadas.'
}

if ($hasContentAuthorityInput) {
    if ([string]::IsNullOrWhiteSpace($ContentAuthorityConfigurationBase64) -or
        [string]::IsNullOrWhiteSpace($ContentAuthorityConfigurationSha256) -or
        [string]::IsNullOrWhiteSpace($ContentAuthorityIssuerSpkiBase64) -or
        $ContentAuthorityConfigurationBase64.Length -gt 10924 -or
        $ContentAuthorityIssuerSpkiBase64.Length -gt 1368 -or
        $ContentAuthorityConfigurationBase64 -match '\s' -or
        $ContentAuthorityIssuerSpkiBase64 -match '\s') {
        throw 'Envelope, hash e SPKI da autoridade de conteudo precisam ser informados juntos.'
    }

    $contentAuthorityConfigurationBytes = [Convert]::FromBase64String(
        $ContentAuthorityConfigurationBase64)
    $contentAuthorityIssuerSpkiBytes = [Convert]::FromBase64String(
        $ContentAuthorityIssuerSpkiBase64)
    try {
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
            throw 'Os bytes da autoridade de conteudo possuem tamanho invalido.'
        }
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
            $contentAuthorityBlock = [ordered]@{
                configurationSha256 = $ContentAuthorityConfigurationSha256
                issuerSpkiSha256 = [Convert]::ToHexString(
                    $contentIssuerHash).ToLowerInvariant()
            }
        }
        finally {
            [Security.Cryptography.CryptographicOperations]::ZeroMemory(
                $contentIssuerHash)
        }
    }
    finally {
        [Security.Cryptography.CryptographicOperations]::ZeroMemory(
            $contentAuthorityConfigurationBytes)
        [Security.Cryptography.CryptographicOperations]::ZeroMemory(
            $contentAuthorityIssuerSpkiBytes)
    }
}
if ($hasAuthorityInput) {
    if ([string]::IsNullOrWhiteSpace($AuthorityConfigurationBase64) -or
        [string]::IsNullOrWhiteSpace($AuthorityConfigurationSha256) -or
        [string]::IsNullOrWhiteSpace($AuthorityIssuerSpkiBase64) -or
        $AuthorityConfigurationBase64.Length -gt 10924 -or
        $AuthorityIssuerSpkiBase64.Length -gt 1368 -or
        $AuthorityConfigurationBase64 -match '\s' -or
        $AuthorityIssuerSpkiBase64 -match '\s') {
        throw 'Envelope, hash aprovado e SPKI da autoridade precisam ser informados juntos.'
    }

    $authorityConfigurationBytes = [Convert]::FromBase64String(
        $AuthorityConfigurationBase64)
    $authorityIssuerSpkiBytes = [Convert]::FromBase64String(
        $AuthorityIssuerSpkiBase64)
    try {
        if ($authorityConfigurationBytes.Length -lt 64 -or
            $authorityConfigurationBytes.Length -gt 8KB -or
            $authorityIssuerSpkiBytes.Length -lt 256 -or
            $authorityIssuerSpkiBytes.Length -gt 1KB -or
            -not [Convert]::ToBase64String($authorityConfigurationBytes).Equals(
                $AuthorityConfigurationBase64, [StringComparison]::Ordinal) -or
            -not [Convert]::ToBase64String($authorityIssuerSpkiBytes).Equals(
                $AuthorityIssuerSpkiBase64, [StringComparison]::Ordinal)) {
            throw 'Os bytes da autoridade possuem tamanho invalido para Release.'
        }
        $expectedConfigurationHash = [Convert]::FromHexString(
            $AuthorityConfigurationSha256)
        $actualConfigurationHash = [Security.Cryptography.SHA256]::HashData(
            $authorityConfigurationBytes)
        try {
            if (-not [Security.Cryptography.CryptographicOperations]::FixedTimeEquals(
                    $actualConfigurationHash,
                    $expectedConfigurationHash)) {
                throw 'O envelope da autoridade nao corresponde ao SHA-256 aprovado.'
            }
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
            $authorityBlock = [ordered]@{
                configurationSha256 = $AuthorityConfigurationSha256
                issuerSpkiSha256 = [Convert]::ToHexString($issuerHash).ToLowerInvariant()
            }
        }
        finally {
            [Security.Cryptography.CryptographicOperations]::ZeroMemory($issuerHash)
        }
    }
    finally {
        [Security.Cryptography.CryptographicOperations]::ZeroMemory($authorityConfigurationBytes)
        [Security.Cryptography.CryptographicOperations]::ZeroMemory($authorityIssuerSpkiBytes)
    }
}

$files = Get-ChildItem -LiteralPath $package -Recurse -File -Force |
    Where-Object { -not $_.FullName.Equals($outputFullPath, [StringComparison]::OrdinalIgnoreCase) } |
    ForEach-Object {
        [ordered]@{
            path = [IO.Path]::GetRelativePath($package, $_.FullName).Replace('\', '/')
            bytes = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    } |
    Sort-Object { $_.path }

$manifest = [ordered]@{
    schema = 'turborama.release-manifest/v1'
    product = 'TURBORAMA_SUITE'
    version = $version
    runtime = 'win-x64'
    selfContained = $true
    unsigned = $Unsigned.IsPresent
    buildUtc = [DateTime]::UtcNow.ToString('O')
    source = [ordered]@{
        repository = $repositoryUrl
        commit = $commit
        branch = $branch
        tag = [string]$tag
        dirty = $dirty
    }
    toolchain = [ordered]@{
        dotnetSdk = $dotnetVersion
        powerShellExecutableSha256 = if ($Unsigned) { $null } else {
            $PowerShellSha256.ToLowerInvariant()
        }
        powerShellHomeTreeSha256 = if ($Unsigned) { $null } else {
            $PowerShellHomeTreeSha256.ToLowerInvariant()
        }
        gitExecutableSha256 = if ($Unsigned) { $null } else {
            $GitSha256.ToLowerInvariant()
        }
        gitTreeSha256 = if ($Unsigned) { $null } else {
            $GitTreeSha256.ToLowerInvariant()
        }
        dotnetSdkTreeSha256 = if ($Unsigned) { $null } else {
            $DotNetSdkTreeSha256.ToLowerInvariant()
        }
        signToolExecutableSha256 = if ($Unsigned) { $null } else {
            $SignToolSha256.ToLowerInvariant()
        }
        releaseTagPublicKeySha256 = if ($Unsigned) { $null } else {
            $ReleaseTagPublicKeySha256.ToLowerInvariant()
        }
    }
    authenticode = $signatureBlock
    authority = $authorityBlock
    contentAuthority = $contentAuthorityBlock
    files = @($files)
}

[IO.File]::WriteAllText(
    $outputFullPath,
    ($manifest | ConvertTo-Json -Depth 12),
    [Text.UTF8Encoding]::new($false))
Write-Host "Manifesto de Release: $outputFullPath"
