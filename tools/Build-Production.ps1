[CmdletBinding(DefaultParameterSetName = 'Signed')]
param(
    [Parameter(ParameterSetName = 'Unsigned', Mandatory)]
    [switch]$UnsignedStaging,

    [Parameter(ParameterSetName = 'Unsigned')]
    [switch]$AllowDirty,

    [Parameter(ParameterSetName = 'Signed', Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{40}$')]
    [string]$CertificateThumbprint,

    [Parameter(ParameterSetName = 'Signed', Mandatory)]
    [ValidatePattern('^https://')]
    [string]$TimestampUrl,

    [Parameter(ParameterSetName = 'Signed', Mandatory)]
    [ValidatePattern('^(?:[0-9A-Fa-f]{40}|[0-9A-Fa-f]{64})$')]
    [string]$ReleaseTagSignerFingerprint,

    [Parameter(ParameterSetName = 'Signed', Mandatory)]
    [ValidatePattern('^(?:[0-9A-Fa-f]{40}|[0-9A-Fa-f]{64})$')]
    [string]$ReleaseTagPrimaryKeyFingerprint,

    [Parameter(ParameterSetName = 'Signed', Mandatory)]
    [string]$ReleaseTagPublicKeyPath,

    [Parameter(ParameterSetName = 'Signed', Mandatory)]
    [ValidatePattern('\A[0-9A-Fa-f]{64}\z')]
    [string]$ReleaseTagPublicKeySha256,

    [Parameter(ParameterSetName = 'Signed', Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{40}$')]
    [string]$TimestampCertificateThumbprint,

    [Parameter(ParameterSetName = 'Signed', Mandatory)]
    [string]$AuthorityConfigurationPath,

    [Parameter(ParameterSetName = 'Signed', Mandatory)]
    [ValidatePattern('\A[0-9A-Fa-f]{64}\z')]
    [string]$AuthorityConfigurationSha256,

    [Parameter(ParameterSetName = 'Signed', Mandatory)]
    [string]$AuthorityIssuerSpkiPath,

    [Parameter(ParameterSetName = 'Signed', Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$AuthorityIssuerSpkiSha256,

    [Parameter(ParameterSetName = 'Signed', Mandatory)]
    [string]$ContentAuthorityConfigurationPath,

    [Parameter(ParameterSetName = 'Signed', Mandatory)]
    [ValidatePattern('\A[0-9A-Fa-f]{64}\z')]
    [string]$ContentAuthorityConfigurationSha256,

    [Parameter(ParameterSetName = 'Signed', Mandatory)]
    [string]$ContentAuthorityIssuerSpkiPath,

    [Parameter(ParameterSetName = 'Signed', Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$ContentAuthorityIssuerSpkiSha256,

    [string]$OutputRoot = '',

    [Parameter(ParameterSetName = 'Signed', Mandatory)]
    [Parameter(ParameterSetName = 'Unsigned')]
    [string]$DotNetPath = 'dotnet',

    [Parameter(ParameterSetName = 'Signed', Mandatory)]
    [Parameter(ParameterSetName = 'Unsigned')]
    [string]$GitPath = 'git',

    [Parameter(ParameterSetName = 'Signed', Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$GitSha256,

    [Parameter(ParameterSetName = 'Signed', Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$GitTreeSha256,

    [Parameter(ParameterSetName = 'Signed', Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$GpgSha256,

    [Parameter(ParameterSetName = 'Signed', Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$DotNetSdkTreeSha256,

    [Parameter(ParameterSetName = 'Signed', Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$SignToolSha256,

    [Parameter(ParameterSetName = 'Signed', Mandatory)]
    [string]$SignToolPath,

    [Parameter(ParameterSetName = 'Signed', Mandatory)]
    [ValidatePattern('\A[0-9A-Fa-f]{64}\z')]
    [string]$PowerShellSha256,

    [Parameter(ParameterSetName = 'Signed', Mandatory)]
    [ValidatePattern('\A[0-9A-Fa-f]{64}\z')]
    [string]$PowerShellHomeTreeSha256
)

$ErrorActionPreference = 'Stop'
$isUnsigned = $PSCmdlet.ParameterSetName -eq 'Unsigned'

# Parse the raw process command line before any command, AST inspection or
# module import. PowerShell's normal binder intentionally accepts aliases,
# abbreviations, common parameters and Name:value syntax; none of those forms
# are part of the signed build protocol.
$trustedPowerShellHome = $null
if (-not $isUnsigned) {
    $processPath = [System.Environment]::ProcessPath
    if ([string]::IsNullOrWhiteSpace($processPath) -or
        -not [System.IO.Path]::IsPathFullyQualified($processPath) -or
        -not [System.IO.Path]::GetFileName($processPath).Equals(
            'pwsh.exe',
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'BOOTSTRAP_POWERSHELL_PROCESS_INVALID: Signed exige o host absoluto pwsh.exe.'
    }
    $processPath = [System.IO.Path]::GetFullPath($processPath)

    $commandLineArguments = [System.Environment]::GetCommandLineArgs()
    $rawArgumentIndex = 1
    if ($rawArgumentIndex -lt $commandLineArguments.Length -and
        [string]::Equals(
            $commandLineArguments[$rawArgumentIndex],
            '-NoLogo',
            [System.StringComparison]::Ordinal)) {
        $rawArgumentIndex++
    }
    foreach ($requiredHostArgument in @('-NoProfile', '-NonInteractive', '-File')) {
        if ($rawArgumentIndex -ge $commandLineArguments.Length -or
            -not [string]::Equals(
                $commandLineArguments[$rawArgumentIndex],
                $requiredHostArgument,
                [System.StringComparison]::Ordinal)) {
            throw 'BOOTSTRAP_POWERSHELL_INVOCATION_INVALID: use somente pwsh [-NoLogo] -NoProfile -NonInteractive -File.'
        }
        $rawArgumentIndex++
    }
    if ($rawArgumentIndex -ge $commandLineArguments.Length) {
        throw 'BOOTSTRAP_POWERSHELL_INVOCATION_INVALID: -File nao recebeu o script absoluto.'
    }
    $invokedScriptArgument = $commandLineArguments[$rawArgumentIndex]
    $rawArgumentIndex++
    if (-not [System.IO.Path]::IsPathFullyQualified($invokedScriptArgument)) {
        throw 'BOOTSTRAP_POWERSHELL_INVOCATION_INVALID: -File exige Build-Production.ps1 absoluto.'
    }
    $invokedScriptPath = [System.IO.Path]::GetFullPath($invokedScriptArgument)
    $currentScriptPath = [System.IO.Path]::GetFullPath($PSCommandPath)
    if (-not $invokedScriptPath.Equals(
            $currentScriptPath,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'BOOTSTRAP_POWERSHELL_INVOCATION_INVALID: -File deve apontar diretamente para Build-Production.ps1.'
    }

    $canonicalSignedParameterNames = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::Ordinal)
    foreach ($canonicalSignedParameterName in @(
            '-CertificateThumbprint',
            '-TimestampUrl',
            '-ReleaseTagSignerFingerprint',
            '-ReleaseTagPrimaryKeyFingerprint',
            '-ReleaseTagPublicKeyPath',
            '-ReleaseTagPublicKeySha256',
            '-TimestampCertificateThumbprint',
            '-AuthorityConfigurationPath',
            '-AuthorityConfigurationSha256',
            '-AuthorityIssuerSpkiPath',
            '-AuthorityIssuerSpkiSha256',
            '-ContentAuthorityConfigurationPath',
            '-ContentAuthorityConfigurationSha256',
            '-ContentAuthorityIssuerSpkiPath',
            '-ContentAuthorityIssuerSpkiSha256',
            '-OutputRoot',
            '-DotNetPath',
            '-GitPath',
            '-GitSha256',
            '-GitTreeSha256',
            '-GpgSha256',
            '-DotNetSdkTreeSha256',
            '-SignToolSha256',
            '-SignToolPath',
            '-PowerShellSha256',
            '-PowerShellHomeTreeSha256')) {
        [void]$canonicalSignedParameterNames.Add($canonicalSignedParameterName)
    }
    $requiredCanonicalSignedParameterNames = [System.Collections.Generic.HashSet[string]]::new(
        $canonicalSignedParameterNames,
        [System.StringComparer]::Ordinal)
    [void]$requiredCanonicalSignedParameterNames.Remove('-OutputRoot')
    $seenCanonicalSignedParameterNames = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::Ordinal)
    while ($rawArgumentIndex -lt $commandLineArguments.Length) {
        $rawParameterName = $commandLineArguments[$rawArgumentIndex]
        $rawArgumentIndex++
        if (-not $canonicalSignedParameterNames.Contains($rawParameterName) -or
            -not $seenCanonicalSignedParameterNames.Add($rawParameterName)) {
            throw "BOOTSTRAP_POWERSHELL_INVOCATION_INVALID: parametro Signed nao canonico ou duplicado: $rawParameterName"
        }
        if ($rawArgumentIndex -ge $commandLineArguments.Length -or
            [string]::IsNullOrWhiteSpace($commandLineArguments[$rawArgumentIndex]) -or
            $commandLineArguments[$rawArgumentIndex].StartsWith(
                '-',
                [System.StringComparison]::Ordinal)) {
            throw "BOOTSTRAP_POWERSHELL_INVOCATION_INVALID: valor ausente para $rawParameterName"
        }
        $rawArgumentIndex++
    }
    foreach ($requiredCanonicalSignedParameterName in $requiredCanonicalSignedParameterNames) {
        if (-not $seenCanonicalSignedParameterNames.Contains(
                $requiredCanonicalSignedParameterName)) {
            throw "BOOTSTRAP_POWERSHELL_INVOCATION_INVALID: parametro Signed obrigatorio ausente: $requiredCanonicalSignedParameterName"
        }
    }
}

# The signed bootstrap deliberately uses only PowerShell language constructs and
# BCL APIs. No cmdlet, module, profile, alias, function, PATH entry or module
# auto-loading decision is trusted until the running PowerShell installation has
# matched both independently supplied pins.
$getBclFileSha256 = {
    param([string]$Path, [ref]$CapturedLength)

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if (-not [System.IO.Path]::IsPathFullyQualified($fullPath) -or
        -not [System.IO.File]::Exists($fullPath)) {
        throw "Arquivo confiavel ausente ou nao absoluto: $Path"
    }

    $attributes = [System.IO.File]::GetAttributes($fullPath)
    if (($attributes -band [System.IO.FileAttributes]::Directory) -ne 0 -or
        ($attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Arquivo confiavel possui tipo inseguro: $fullPath"
    }

    $fileInfo = [System.IO.FileInfo]::new($fullPath)
    $expectedLength = $fileInfo.Length
    $stream = [System.IO.FileStream]::new(
        $fullPath,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::Read,
        1MB,
        [System.IO.FileOptions]::SequentialScan)
    try {
        if ($stream.Length -ne $expectedLength) {
            throw "Arquivo confiavel mudou antes do hash: $fullPath"
        }
        $hash = [System.Security.Cryptography.SHA256]::HashData($stream)
        $fileInfo.Refresh()
        if ($stream.Length -ne $expectedLength -or
            $fileInfo.Length -ne $expectedLength -or
            [System.IO.File]::GetAttributes($fullPath) -ne $attributes) {
            [System.Security.Cryptography.CryptographicOperations]::ZeroMemory($hash)
            throw "Arquivo confiavel mudou durante o hash: $fullPath"
        }
        $CapturedLength.Value = $expectedLength
        return ,$hash
    }
    finally {
        $stream.Dispose()
    }
}

$getBclDirectoryTreeSha256 = {
    param([string]$DirectoryPath, [System.Management.Automation.ScriptBlock]$FileHasher)

    $directory = [System.IO.Path]::GetFullPath($DirectoryPath)
    if (-not [System.IO.Path]::IsPathFullyQualified($directory) -or
        -not [System.IO.Directory]::Exists($directory)) {
        throw "Arvore confiavel ausente ou nao absoluta: $DirectoryPath"
    }

    $ancestor = [System.IO.DirectoryInfo]::new($directory)
    while ($null -ne $ancestor) {
        if (($ancestor.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "A arvore confiavel atravessa um reparse point: $($ancestor.FullName)"
        }
        $ancestor = $ancestor.Parent
    }

    $paths = [System.Collections.Generic.List[string]]::new()
    $pending = [System.Collections.Generic.Stack[string]]::new()
    $pending.Push($directory)
    while ($pending.Count -gt 0) {
        $currentDirectory = $pending.Pop()
        foreach ($entryPath in [System.IO.Directory]::EnumerateFileSystemEntries($currentDirectory)) {
            $fullEntryPath = [System.IO.Path]::GetFullPath($entryPath)
            $entryAttributes = [System.IO.File]::GetAttributes($fullEntryPath)
            if (($entryAttributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "A arvore confiavel contem reparse point: $fullEntryPath"
            }
            $paths.Add($fullEntryPath)
            if (($entryAttributes -band [System.IO.FileAttributes]::Directory) -ne 0) {
                $pending.Push($fullEntryPath)
            }
        }
    }
    $paths.Sort([System.StringComparer]::Ordinal)

    $hasher = [System.Security.Cryptography.IncrementalHash]::CreateHash(
        [System.Security.Cryptography.HashAlgorithmName]::SHA256)
    $strictUtf8 = [System.Text.UTF8Encoding]::new($false, $true)
    try {
        $formatHeader = $strictUtf8.GetBytes('TURBORAMA-DIRECTORY-TREE-SHA256-V1' + "`n")
        try {
            $hasher.AppendData($formatHeader)
        }
        finally {
            [System.Security.Cryptography.CryptographicOperations]::ZeroMemory(
                $formatHeader)
        }

        foreach ($entryPath in $paths) {
            $entryAttributes = [System.IO.File]::GetAttributes($entryPath)
            if (($entryAttributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "A arvore confiavel mudou para reparse point: $entryPath"
            }
            $relativePath = [System.IO.Path]::GetRelativePath(
                $directory,
                $entryPath).Replace('\', '/')
            $relativePathBytes = $strictUtf8.GetBytes($relativePath)
            $record = $null
            $fileHash = $null
            try {
                if (($entryAttributes -band [System.IO.FileAttributes]::Directory) -ne 0) {
                    $record = $strictUtf8.GetBytes(
                        "D`0$($relativePathBytes.Length)`0$relativePath`n")
                }
                else {
                    [long]$capturedLength = 0
                    [byte[]]$fileHash = & $FileHasher `
                        $entryPath ([ref]$capturedLength)
                    $record = $strictUtf8.GetBytes(
                        "F`0$($relativePathBytes.Length)`0$relativePath`0$capturedLength`0$([System.Convert]::ToHexString($fileHash))`n")
                }
                $hasher.AppendData($record)
            }
            finally {
                [System.Security.Cryptography.CryptographicOperations]::ZeroMemory(
                    $relativePathBytes)
                if ($null -ne $record) {
                    [System.Security.Cryptography.CryptographicOperations]::ZeroMemory($record)
                }
                if ($null -ne $fileHash) {
                    [System.Security.Cryptography.CryptographicOperations]::ZeroMemory($fileHash)
                }
            }
        }
        return ,$hasher.GetHashAndReset()
    }
    finally {
        $hasher.Dispose()
    }
}

$assertPinnedHash = {
    param([byte[]]$ActualHash, [string]$ExpectedSha256, [string]$ErrorMessage)

    $expectedHash = [System.Convert]::FromHexString($ExpectedSha256)
    try {
        if (-not [System.Security.Cryptography.CryptographicOperations]::FixedTimeEquals(
                $ActualHash,
                $expectedHash)) {
            throw $ErrorMessage
        }
    }
    finally {
        [System.Security.Cryptography.CryptographicOperations]::ZeroMemory($expectedHash)
    }
}

$assertPinnedFile = {
    param([string]$Path, [string]$ExpectedSha256, [string]$ErrorMessage)

    [long]$capturedLength = 0
    [byte[]]$actualHash = & $getBclFileSha256 $Path ([ref]$capturedLength)
    try {
        & $assertPinnedHash $actualHash $ExpectedSha256 $ErrorMessage
    }
    finally {
        [System.Security.Cryptography.CryptographicOperations]::ZeroMemory($actualHash)
    }
}

$assertPinnedDirectoryTree = {
    param([string]$Path, [string]$ExpectedSha256, [string]$ErrorMessage)

    [byte[]]$actualHash = & $getBclDirectoryTreeSha256 $Path $getBclFileSha256
    try {
        & $assertPinnedHash $actualHash $ExpectedSha256 $ErrorMessage
    }
    finally {
        [System.Security.Cryptography.CryptographicOperations]::ZeroMemory($actualHash)
    }
}

if (-not $isUnsigned) {
    $trustedPowerShellHome = [System.IO.Path]::GetDirectoryName($processPath)
    $powerShellVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($processPath)
    if ($powerShellVersion.ProductMajorPart -ne 7) {
        throw 'BOOTSTRAP_POWERSHELL_PROCESS_INVALID: Signed exige PowerShell 7.'
    }

    [long]$powerShellLength = 0
    [byte[]]$actualPowerShellHash = & $getBclFileSha256 `
        $processPath ([ref]$powerShellLength)
    try {
        & $assertPinnedHash $actualPowerShellHash $PowerShellSha256 `
            'BOOTSTRAP_POWERSHELL_HASH_MISMATCH: pwsh.exe difere do pin aprovado.'
    }
    finally {
        [System.Security.Cryptography.CryptographicOperations]::ZeroMemory($actualPowerShellHash)
    }

    [byte[]]$actualPowerShellTreeHash = & $getBclDirectoryTreeSha256 `
        $trustedPowerShellHome $getBclFileSha256
    try {
        & $assertPinnedHash $actualPowerShellTreeHash $PowerShellHomeTreeSha256 `
            'BOOTSTRAP_POWERSHELL_TREE_MISMATCH: PSHOME difere do pin aprovado.'
    }
    finally {
        [System.Security.Cryptography.CryptographicOperations]::ZeroMemory(
            $actualPowerShellTreeHash)
    }

    $PSModuleAutoloadingPreference = 'None'
    $PSDefaultParameterValues = @{}
    $trustedModuleRoot = [System.IO.Path]::Combine($trustedPowerShellHome, 'Modules')
    $env:PSModulePath = $trustedModuleRoot
    foreach ($trustedModuleName in @(
            'Microsoft.PowerShell.Management',
            'Microsoft.PowerShell.Security',
            'Microsoft.PowerShell.Utility')) {
        $trustedModuleManifest = [System.IO.Path]::Combine(
            $trustedModuleRoot,
            $trustedModuleName,
            "$trustedModuleName.psd1")
        if (-not [System.IO.File]::Exists($trustedModuleManifest)) {
            throw "BOOTSTRAP_POWERSHELL_MODULE_INVALID: modulo ausente em PSHOME: $trustedModuleName"
        }
        Microsoft.PowerShell.Core\Import-Module -Name $trustedModuleManifest `
            -Force -Scope Local -ErrorAction Stop
        $loadedTrustedModules = @(Microsoft.PowerShell.Core\Get-Module `
            -Name $trustedModuleName)
        if ($loadedTrustedModules.Count -lt 1) {
            throw "BOOTSTRAP_POWERSHELL_MODULE_INVALID: modulo nao carregado: $trustedModuleName"
        }
        foreach ($loadedTrustedModule in $loadedTrustedModules) {
            $loadedModulePath = [System.IO.Path]::GetFullPath($loadedTrustedModule.Path)
            if (-not $loadedModulePath.Equals(
                    $trustedModuleManifest,
                    [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "BOOTSTRAP_POWERSHELL_MODULE_INVALID: origem inesperada: $trustedModuleName"
            }
        }
    }

    # Detect a runtime/module swap in the interval between attestation and load.
    [byte[]]$revalidatedPowerShellTreeHash = & $getBclDirectoryTreeSha256 `
        $trustedPowerShellHome $getBclFileSha256
    try {
        & $assertPinnedHash $revalidatedPowerShellTreeHash $PowerShellHomeTreeSha256 `
            'BOOTSTRAP_POWERSHELL_TREE_MISMATCH: PSHOME mudou durante o bootstrap.'
    }
    finally {
        [System.Security.Cryptography.CryptographicOperations]::ZeroMemory(
            $revalidatedPowerShellTreeHash)
    }
    Microsoft.PowerShell.Core\Set-StrictMode -Version Latest
}
else {
    Set-StrictMode -Version Latest
}

$root = [IO.Path]::GetFullPath([IO.Path]::Combine($PSScriptRoot, '..'))
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = [IO.Path]::Combine($root, 'artifacts')
}
$project = [IO.Path]::Combine($root, 'TurboBoxManager.csproj')
$testProject = [IO.Path]::Combine(
    $root,
    'tests',
    'CatalogVerifier',
    'CatalogVerifier.csproj')
$catalog = [IO.Path]::Combine($root, 'Assets', 'Catalog', 'catalog.json')
$sourceGate = [IO.Path]::Combine($root, 'tools', 'Test-ReleaseSource.ps1')
$packageGate = [IO.Path]::Combine($root, 'tools', 'Test-PublishedPackage.ps1')
$sbomTool = [IO.Path]::Combine($root, 'tools', 'New-ReleaseSbom.ps1')
$manifestTool = [IO.Path]::Combine($root, 'tools', 'New-ReleaseManifest.ps1')

if ($isUnsigned) {
    $dotNetCommand = Get-Command $DotNetPath -CommandType Application -ErrorAction Stop |
        Select-Object -First 1
    $DotNetPath = (Resolve-Path -LiteralPath $dotNetCommand.Source).Path
    $gitCommand = Get-Command $GitPath -CommandType Application -ErrorAction Stop |
        Select-Object -First 1
    $GitPath = (Resolve-Path -LiteralPath $gitCommand.Source).Path
}
else {
    if (-not [IO.Path]::IsPathFullyQualified($DotNetPath) -or
        -not [IO.File]::Exists($DotNetPath) -or
        -not [IO.Path]::IsPathFullyQualified($GitPath) -or
        -not [IO.File]::Exists($GitPath)) {
        throw 'Signed exige DotNetPath e GitPath absolutos, existentes e independentes de PATH.'
    }
    $DotNetPath = [IO.Path]::GetFullPath($DotNetPath)
    $GitPath = [IO.Path]::GetFullPath($GitPath)
}

$dotNetInfo = [IO.FileInfo]::new($DotNetPath)
$dotNetSignature = Microsoft.PowerShell.Security\Get-AuthenticodeSignature `
    -LiteralPath $DotNetPath
if (-not $dotNetInfo.Name.Equals('dotnet.exe', [StringComparison]::OrdinalIgnoreCase) -or
    $dotNetSignature.Status -ne 'Valid' -or
    $null -eq $dotNetSignature.SignerCertificate -or
    $dotNetSignature.SignerCertificate.Subject -notmatch '(?:^|,\s*)CN=\.NET(?:,|$)' -or
    $dotNetSignature.SignerCertificate.Subject -notmatch '(?:^|,\s*)O=Microsoft Corporation(?:,|$)' -or
    $dotNetInfo.VersionInfo.CompanyName -ne 'Microsoft Corporation' -or
    $dotNetInfo.VersionInfo.OriginalFilename -ne '.NET Host') {
    throw 'DotNetPath precisa apontar para o host dotnet.exe oficial com Authenticode Microsoft valido.'
}

$gitInfo = [IO.FileInfo]::new($GitPath)
$gitSignature = Microsoft.PowerShell.Security\Get-AuthenticodeSignature `
    -LiteralPath $GitPath
if (-not $gitInfo.Name.Equals('git.exe', [StringComparison]::OrdinalIgnoreCase) -or
    $gitSignature.Status -ne 'Valid' -or
    $null -eq $gitSignature.SignerCertificate -or
    $gitSignature.SignerCertificate.Subject -notmatch '(?:^|,\s*)CN=Johannes Schindelin(?:,|$)' -or
    $gitInfo.VersionInfo.CompanyName -ne 'The Git Development Community' -or
    $gitInfo.VersionInfo.OriginalFilename -ne 'git.exe' -or
    $gitInfo.VersionInfo.ProductName -ne 'Git') {
    throw 'GitPath precisa apontar para o git.exe oficial do Git for Windows com Authenticode valido.'
}

function Invoke-Checked([string]$Command, [string[]]$Arguments) {
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Comando externo falhou com codigo ${LASTEXITCODE}: $Command"
    }
}

function Set-ExactProcessEnvironment([Collections.IDictionary]$Variables) {
    $environmentNames = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($environmentName in [Environment]::GetEnvironmentVariables('Process').Keys) {
        [void]$environmentNames.Add([string]$environmentName)
    }
    foreach ($environmentName in $Variables.Keys) {
        [void]$environmentNames.Add([string]$environmentName)
    }
    foreach ($environmentName in $environmentNames) {
        [Environment]::SetEnvironmentVariable(
            $environmentName,
            [Management.Automation.Language.NullString]::Value,
            [EnvironmentVariableTarget]::Process)
    }
    foreach ($entry in $Variables.GetEnumerator()) {
        [Environment]::SetEnvironmentVariable(
            [string]$entry.Key,
            [string]$entry.Value,
            'Process')
    }
}

function Assert-NoMsBuildPropertyMetacharacters(
    [string]$Path,
    [string]$Description) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    if ($fullPath.IndexOfAny([char[]]';,%') -ge 0) {
        throw "$Description contem metacaractere proibido para uma propriedade MSBuild."
    }
    return $fullPath
}

function Assert-ReleaseTagGpgStatus(
    [string[]]$StatusLines,
    [string]$ExpectedSigningKey,
    [string]$ExpectedPrimaryKey) {
    $negativeStatusPattern =
        '^\[GNUPG:\]\s+(?:BADSIG|ERRSIG|EXPSIG|EXPKEYSIG|REVKEYSIG|KEYREVOKED|NO_PUBKEY|KEYEXPIRED|SIGEXPIRED|FAILURE|NODATA)(?:\s|$)'
    if (@($StatusLines | Where-Object { $_ -match $negativeStatusPattern }).Count -ne 0) {
        throw 'A verificacao GPG retornou status explicito de assinatura/chave invalida.'
    }

    $rawGoodSignatures = @($StatusLines | Where-Object {
        $_ -match '^\[GNUPG:\]\s+GOODSIG(?:\s|$)'
    })
    if ($rawGoodSignatures.Count -ne 1) {
        throw 'A tag exige exatamente um registro bruto GOODSIG.'
    }
    $goodSignatures = @($rawGoodSignatures | ForEach-Object {
        if ($_ -match '^\[GNUPG:\]\s+GOODSIG\s+([0-9A-Fa-f]{8,64})(?:\s|$)') {
            $Matches[1].ToUpperInvariant()
        }
    })
    if ($goodSignatures.Count -ne 1 -or
        -not $ExpectedSigningKey.EndsWith(
            $goodSignatures[0],
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'A tag exige exatamente um GOODSIG da chave de assinatura aprovada.'
    }

    $rawValidSignatures = @($StatusLines | Where-Object {
        $_ -match '^\[GNUPG:\]\s+VALIDSIG(?:\s|$)'
    })
    if ($rawValidSignatures.Count -ne 1) {
        throw 'A tag exige exatamente um registro bruto VALIDSIG.'
    }
    $validTagSignatures = @($rawValidSignatures | ForEach-Object {
        if ($_ -match ('^\[GNUPG:\]\s+VALIDSIG\s+' +
                '([0-9A-Fa-f]{40}|[0-9A-Fa-f]{64})' +
                '(?:\s+\S+){8}(?:\s+([0-9A-Fa-f]{40}|[0-9A-Fa-f]{64}))?(?:\s|$)')) {
            [pscustomobject]@{
                SigningKey = $Matches[1].ToUpperInvariant()
                PrimaryKey = if ([string]::IsNullOrWhiteSpace($Matches[2])) {
                    $Matches[1].ToUpperInvariant()
                } else { $Matches[2].ToUpperInvariant() }
            }
        }
    })
    if ($validTagSignatures.Count -ne 1 -or
        -not $validTagSignatures[0].SigningKey.Equals(
            $ExpectedSigningKey,
            [StringComparison]::OrdinalIgnoreCase) -or
        -not $validTagSignatures[0].PrimaryKey.Equals(
            $ExpectedPrimaryKey,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'A tag exige exatamente um VALIDSIG dos fingerprints primario e de assinatura aprovados.'
    }
}

function Assert-NoReparseAncestry([string]$Path) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    $pathRoot = [IO.Path]::GetPathRoot($fullPath)
    if ([string]::IsNullOrWhiteSpace($pathRoot) -or
        $fullPath.Substring($pathRoot.Length).Contains(':')) {
        throw "Caminho local invalido: $Path"
    }

    $current = $pathRoot
    $segments = @($fullPath.Substring($pathRoot.Length) -split '[\\/]' |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    foreach ($segment in $segments) {
        $current = Join-Path $current $segment
        $entry = Get-Item -LiteralPath $current -Force -ErrorAction Stop
        if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "O caminho atravessa um reparse point: $current"
        }
    }
}

function ConvertTo-GitMsysPath([string]$Path) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    if ($fullPath -notmatch '^([A-Za-z]):\\(.*)$') {
        throw "O GnuPG incorporado ao Git exige um caminho local absoluto: $Path"
    }

    return '/' + $Matches[1].ToLowerInvariant() + '/' +
        $Matches[2].Replace([IO.Path]::DirectorySeparatorChar, [char]'/')
}

function New-GpgReleaseWrapperBytes(
    [string]$GpgExecutablePath,
    [string]$GnuPgHomePath) {
    $convertToMsysPath = {
        param([string]$Path)
        $fullPath = [IO.Path]::GetFullPath($Path)
        if ($fullPath -notmatch '^([A-Za-z]):\\(.*)$') {
            throw "O wrapper GPG exige um caminho local absoluto: $Path"
        }
        return '/' + $Matches[1].ToLowerInvariant() + '/' +
            $Matches[2].Replace([IO.Path]::DirectorySeparatorChar, [char]'/')
    }
    $quotePosix = {
        param([string]$Value)
        $singleQuote = [string][char]39
        $doubleQuote = [string][char]34
        $embeddedSingleQuote =
            $singleQuote + $doubleQuote + $singleQuote + $doubleQuote + $singleQuote
        return $singleQuote +
            $Value.Replace($singleQuote, $embeddedSingleQuote) +
            $singleQuote
    }

    $gpgForShell = & $convertToMsysPath $GpgExecutablePath
    $homeForShell = & $convertToMsysPath $GnuPgHomePath
    $quotedGpg = & $quotePosix $gpgForShell
    $quotedHome = & $quotePosix $homeForShell
    $wrapperText =
        "#!/usr/bin/sh`n" +
        "exec $quotedGpg --no-options --batch --no-tty --no-autostart " +
        "--homedir $quotedHome --no-auto-key-retrieve `"`$@`"`n"
    return [Text.UTF8Encoding]::new($false, $true).GetBytes($wrapperText)
}

function Read-BoundedInput(
    [string]$Path,
    [int]$MinimumLength,
    [int]$MaximumLength,
    [string]$Description) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    Assert-NoReparseAncestry $fullPath
    $entry = Get-Item -LiteralPath $fullPath -Force -ErrorAction Stop
    if ($entry.PSIsContainer -or
        ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        $entry.Length -lt $MinimumLength -or
        $entry.Length -gt $MaximumLength) {
        throw "$Description possui tipo ou tamanho invalido."
    }

    $stream = [IO.FileStream]::new(
        $fullPath,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::Read,
        4096,
        [IO.FileOptions]::SequentialScan)
    $bytes = $null
    try {
        Assert-NoReparseAncestry $fullPath
        if ($stream.Length -ne $entry.Length -or
            $stream.Length -lt $MinimumLength -or
            $stream.Length -gt $MaximumLength) {
            throw "$Description mudou durante a captura."
        }

        $bytes = [byte[]]::new([int]$stream.Length)
        $offset = 0
        while ($offset -lt $bytes.Length) {
            $read = $stream.Read($bytes, $offset, $bytes.Length - $offset)
            if ($read -le 0) { throw "$Description terminou antes do tamanho declarado." }
            $offset += $read
        }
        if ($stream.ReadByte() -ne -1) { throw "$Description cresceu durante a captura." }
        Assert-NoReparseAncestry $fullPath
        return ,$bytes
    }
    catch {
        if ($null -ne $bytes) {
            [Security.Cryptography.CryptographicOperations]::ZeroMemory($bytes)
        }
        throw
    }
    finally {
        $stream.Dispose()
    }
}

function Assert-ApprovedSha256(
    [byte[]]$Bytes,
    [string]$ExpectedSha256,
    [string]$Description) {
    $expected = [Convert]::FromHexString($ExpectedSha256)
    $actual = [Security.Cryptography.SHA256]::HashData($Bytes)
    try {
        if (-not [Security.Cryptography.CryptographicOperations]::FixedTimeEquals(
                $actual,
                $expected)) {
            throw "$Description nao corresponde ao SHA-256 aprovado independentemente."
        }
    }
    finally {
        [Security.Cryptography.CryptographicOperations]::ZeroMemory($actual)
        [Security.Cryptography.CryptographicOperations]::ZeroMemory($expected)
    }
}

function Write-ProtectedCapturedInput(
    [string]$Path,
    [byte[]]$Bytes,
    [string]$Description) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    $writer = [IO.FileStream]::new(
        $fullPath,
        [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write,
        [IO.FileShare]::None,
        4096,
        [IO.FileOptions]::WriteThrough)
    try {
        $writer.Write($Bytes, 0, $Bytes.Length)
        $writer.Flush($true)
    }
    finally {
        $writer.Dispose()
    }

    Assert-NoReparseAncestry $fullPath
    $expectedHash = [Security.Cryptography.SHA256]::HashData($Bytes)
    $lease = $null
    $actualHash = $null
    try {
        # Keep a read lease without write/delete sharing until GnuPG has
        # consumed the captured bytes. The named file cannot be replaced in
        # the validation-to-import interval.
        $lease = [IO.FileStream]::new(
            $fullPath,
            [IO.FileMode]::Open,
            [IO.FileAccess]::Read,
            [IO.FileShare]::Read,
            4096,
            [IO.FileOptions]::SequentialScan)
        if ($lease.Length -ne $Bytes.Length) {
            throw "$Description mudou durante a captura protegida."
        }
        $actualHash = [Security.Cryptography.SHA256]::HashData($lease)
        if (-not [Security.Cryptography.CryptographicOperations]::FixedTimeEquals(
                $actualHash,
                $expectedHash)) {
            throw "$Description mudou durante a captura protegida."
        }
        $lease.Position = 0
        return $lease
    }
    catch {
        if ($null -ne $lease) { $lease.Dispose() }
        throw
    }
    finally {
        [Security.Cryptography.CryptographicOperations]::ZeroMemory($expectedHash)
        if ($null -ne $actualHash) {
            [Security.Cryptography.CryptographicOperations]::ZeroMemory($actualHash)
        }
    }
}

function Get-DirectoryTreeSha256([string]$DirectoryPath) {
    [byte[]]$treeHash = & $getBclDirectoryTreeSha256 `
        $DirectoryPath $getBclFileSha256
    try {
        return [Convert]::ToHexString($treeHash)
    }
    finally {
        [Security.Cryptography.CryptographicOperations]::ZeroMemory($treeHash)
    }
}

function Copy-BclRegularFileSnapshot([string]$SourcePath, [string]$DestinationPath) {
    $source = [IO.Path]::GetFullPath($SourcePath)
    $destination = [IO.Path]::GetFullPath($DestinationPath)
    $attributes = [IO.File]::GetAttributes($source)
    if (($attributes -band [IO.FileAttributes]::Directory) -ne 0 -or
        ($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "O snapshot local contem arquivo inseguro: $source"
    }

    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) > $null
    $reader = [IO.FileStream]::new(
        $source,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::Read,
        1MB,
        [IO.FileOptions]::SequentialScan)
    $writer = $null
    try {
        $capturedLength = $reader.Length
        $writer = [IO.FileStream]::new(
            $destination,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write,
            [IO.FileShare]::None,
            1MB,
            [IO.FileOptions]::WriteThrough)
        $reader.CopyTo($writer, 1MB)
        $writer.Flush($true)
        if ($reader.Length -ne $capturedLength -or
            $writer.Length -ne $capturedLength -or
            [IO.File]::GetAttributes($source) -ne $attributes) {
            throw "O arquivo mudou durante a copia do snapshot local: $source"
        }
    }
    finally {
        if ($null -ne $writer) { $writer.Dispose() }
        $reader.Dispose()
    }
}

function Copy-BclRegularDirectorySnapshot(
    [string]$SourceDirectory,
    [string]$DestinationDirectory) {
    $sourceRoot = [IO.Path]::GetFullPath($SourceDirectory)
    $destinationRoot = [IO.Path]::GetFullPath($DestinationDirectory)
    if (-not [IO.Directory]::Exists($sourceRoot) -or
        ([IO.File]::GetAttributes($sourceRoot) -band
            [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Diretorio inseguro ou ausente no snapshot local: $sourceRoot"
    }

    [IO.Directory]::CreateDirectory($destinationRoot) > $null
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($sourceRoot)
    while ($pending.Count -gt 0) {
        $currentSource = $pending.Pop()
        foreach ($sourceEntry in [IO.Directory]::EnumerateFileSystemEntries(
                $currentSource)) {
            $entryAttributes = [IO.File]::GetAttributes($sourceEntry)
            if (($entryAttributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "O snapshot local contem reparse point: $sourceEntry"
            }
            $relativeEntry = [IO.Path]::GetRelativePath($sourceRoot, $sourceEntry)
            $destinationEntry = [IO.Path]::Combine($destinationRoot, $relativeEntry)
            if (($entryAttributes -band [IO.FileAttributes]::Directory) -ne 0) {
                [IO.Directory]::CreateDirectory($destinationEntry) > $null
                $pending.Push($sourceEntry)
            }
            else {
                Copy-BclRegularFileSnapshot $sourceEntry $destinationEntry
            }
        }
    }
}

function Test-BclFileSha256([string]$Path, [string]$ExpectedSha256) {
    [long]$capturedLength = 0
    [byte[]]$actualHash = & $getBclFileSha256 $Path ([ref]$capturedLength)
    $expectedHash = [Convert]::FromHexString($ExpectedSha256)
    try {
        return [Security.Cryptography.CryptographicOperations]::FixedTimeEquals(
            $actualHash,
            $expectedHash)
    }
    finally {
        [Security.Cryptography.CryptographicOperations]::ZeroMemory($actualHash)
        [Security.Cryptography.CryptographicOperations]::ZeroMemory($expectedHash)
    }
}

function Find-SignTool([string]$CandidatePath, [string]$ExpectedSha256) {
    if (-not [IO.Path]::IsPathFullyQualified($CandidatePath) -or
        -not [IO.File]::Exists($CandidatePath)) {
        throw 'Signed exige SignToolPath absoluto, existente e independente de PATH.'
    }

    $candidate = [IO.Path]::GetFullPath($CandidatePath)
    Assert-NoReparseAncestry $candidate
    $candidateInfo = [IO.FileInfo]::new($candidate)
    $candidateSignature = Microsoft.PowerShell.Security\Get-AuthenticodeSignature `
        -LiteralPath $candidate
    if ($candidateSignature.Status -ne 'Valid' -or
        $null -eq $candidateSignature.SignerCertificate -or
        $candidateSignature.SignerCertificate.Subject -notmatch '(?:^|,\s*)O=Microsoft Corporation(?:,|$)' -or
        $candidateInfo.VersionInfo.CompanyName -ne 'Microsoft Corporation' -or
        $candidateInfo.VersionInfo.OriginalFilename -ne 'SignTool.exe' -or
        -not (Test-BclFileSha256 $candidate $ExpectedSha256)) {
        throw 'signtool.exe nao possui provenance Microsoft e SHA-256 exatamente aprovados.'
    }
    return $candidate
}

if (-not $isUnsigned) {
    $root = Assert-NoMsBuildPropertyMetacharacters `
        $root 'O caminho raiz do snapshot Signed'
    $OutputRoot = Assert-NoMsBuildPropertyMetacharacters `
        $OutputRoot 'O destino do candidato Signed'
}

Assert-NoReparseAncestry $DotNetPath
Assert-NoReparseAncestry $GitPath
$gitInstallationRoot = Split-Path -Parent (Split-Path -Parent $GitPath)
$gpgPath = $null
if (-not $isUnsigned) {
    if (-not (Test-BclFileSha256 $GitPath $GitSha256)) {
        throw 'O hash do git.exe nao corresponde ao pin aprovado para a Release.'
    }

    $actualGitTreeHash = Get-DirectoryTreeSha256 $gitInstallationRoot
    if (-not $actualGitTreeHash.Equals(
            $GitTreeSha256,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'A arvore completa do Git for Windows nao corresponde ao pin aprovado para a Release.'
    }
    $gpgPath = Join-Path $gitInstallationRoot 'usr\bin\gpg.exe'
    Assert-NoReparseAncestry $gpgPath
    $gpgInfo = [IO.FileInfo]::new($gpgPath)
    if (-not $gpgInfo.Exists -or
        ($gpgInfo.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        -not (Test-BclFileSha256 $gpgPath $GpgSha256)) {
        throw 'O gpg.exe incorporado ao Git nao corresponde ao hash aprovado para a Release.'
    }
    $shPath = Join-Path $gitInstallationRoot 'usr\bin\sh.exe'
    Assert-NoReparseAncestry $shPath
    $shInfo = [IO.FileInfo]::new($shPath)
    if (-not $shInfo.Exists -or
        ($shInfo.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'O sh.exe incorporado e coberto pela arvore Git aprovada esta ausente.'
    }

    $dotNetSdkRoot = Split-Path -Parent $DotNetPath
    $actualDotNetSdkTreeHash = Get-DirectoryTreeSha256 $dotNetSdkRoot
    if (-not $actualDotNetSdkTreeHash.Equals(
            $DotNetSdkTreeSha256,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'A arvore completa do SDK .NET nao corresponde ao pin aprovado para a Release.'
    }
}

$trustedGitConfiguration = @(
    '-c', 'core.fsmonitor=false',
    '-c', 'core.untrackedCache=false',
    '-c', 'core.sparseCheckout=false',
    '-c', 'core.sparseCheckoutCone=false',
    '-c', 'core.hooksPath=NUL',
    '-c', 'core.attributesFile=NUL',
    '-c', 'credential.helper=',
    '-c', 'http.sslVerify=true',
    '-c', 'http.sslBackend=schannel',
    '-c', 'http.schannelUseSSLCAInfo=false',
    '-c', 'http.proxy=',
    '-c', 'remote.origin.proxy=',
    '-c', 'http.extraHeader=',
    '-c', 'http.https://github.com/.sslVerify=true',
    '-c', 'http.https://github.com/.proxy=',
    '-c', 'http.https://github.com/.extraHeader=',
    '-c', 'protocol.allow=never',
    '-c', 'protocol.https.allow=always',
    '-c', 'protocol.ext.allow=never')
$gitEnvironmentNames = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::OrdinalIgnoreCase)
$signedEnvironmentSnapshot = $null
if (-not $isUnsigned) {
    $signedEnvironmentSnapshot = @{}
    foreach ($environmentName in [Environment]::GetEnvironmentVariables('Process').Keys) {
        $signedEnvironmentSnapshot[[string]$environmentName] =
            [Environment]::GetEnvironmentVariable([string]$environmentName, 'Process')
    }
}
$temporaryParent = $null
$releaseTrustRoot = $null
$releaseTagPublicKeyLease = $null
$releaseGpgWrapperLease = $null
$savedGitEnvironment = @{}

try {
foreach ($environmentName in [Environment]::GetEnvironmentVariables('Process').Keys) {
    if ([string]$environmentName -match '^(?:GIT_|GNUPG|GPG_|GCM_|SSH_ASKPASS$)') {
        [void]$gitEnvironmentNames.Add([string]$environmentName)
    }
}
foreach ($environmentName in @(
    'GIT_CONFIG_NOSYSTEM', 'GIT_CONFIG_GLOBAL', 'GIT_CONFIG_SYSTEM',
    'GIT_TERMINAL_PROMPT', 'GCM_INTERACTIVE', 'GNUPGHOME')) {
    [void]$gitEnvironmentNames.Add($environmentName)
}
foreach ($environmentName in $gitEnvironmentNames) {
    $savedGitEnvironment[$environmentName] =
        [Environment]::GetEnvironmentVariable($environmentName, 'Process')
    [Environment]::SetEnvironmentVariable($environmentName, $null, 'Process')
}
$env:GIT_CONFIG_NOSYSTEM = '1'
$env:GIT_CONFIG_GLOBAL = 'NUL'
$env:GIT_CONFIG_SYSTEM = 'NUL'
$env:GIT_TERMINAL_PROMPT = '0'
$env:GCM_INTERACTIVE = 'Never'

$temporaryParent = if ($isUnsigned) {
    [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
}
else {
    $windowsDirectory = [IO.Path]::GetDirectoryName([Environment]::SystemDirectory)
    [IO.Path]::GetFullPath([IO.Path]::Combine($windowsDirectory, 'Temp'))
}
if (-not (Test-Path -LiteralPath $temporaryParent -PathType Container)) {
    throw "A raiz temporaria confiavel nao existe: $temporaryParent"
}
Assert-NoReparseAncestry $temporaryParent
if (-not $isUnsigned) {
    $temporaryParent = Assert-NoMsBuildPropertyMetacharacters `
        $temporaryParent 'A raiz temporaria confiavel do build Signed'
}
if (-not $isUnsigned) {
    $windowsSystemDirectory = [Environment]::SystemDirectory
    $windowsDirectory = [IO.Path]::GetDirectoryName($windowsSystemDirectory)
    $windowsDrive = [IO.Path]::GetPathRoot($windowsDirectory).TrimEnd(
        [IO.Path]::DirectorySeparatorChar)
    $programFiles = [Environment]::GetFolderPath(
        [Environment+SpecialFolder]::ProgramFiles)
    $programFilesX86 = [Environment]::GetFolderPath(
        [Environment+SpecialFolder]::ProgramFilesX86)
    $programData = [Environment]::GetFolderPath(
        [Environment+SpecialFolder]::CommonApplicationData)
    $signedEnvironmentBase = [ordered]@{
        SystemRoot = $windowsDirectory
        windir = $windowsDirectory
        SystemDrive = $windowsDrive
        ComSpec = [IO.Path]::Combine($windowsSystemDirectory, 'cmd.exe')
        OS = 'Windows_NT'
        PROCESSOR_ARCHITECTURE = 'AMD64'
        NUMBER_OF_PROCESSORS = [Environment]::ProcessorCount.ToString(
            [Globalization.CultureInfo]::InvariantCulture)
        ProgramFiles = $programFiles
        'ProgramFiles(x86)' = $programFilesX86
        ProgramW6432 = $programFiles
        ProgramData = $programData
        PATH = [IO.Path]::Combine($gitInstallationRoot, 'usr', 'bin') + ';' +
            [IO.Path]::Combine($gitInstallationRoot, 'mingw64', 'bin') + ';' +
            [IO.Path]::Combine($gitInstallationRoot, 'cmd') + ';' +
            [IO.Path]::GetDirectoryName($DotNetPath) + ';' +
            $windowsSystemDirectory + ';' + $windowsDirectory
        PATHEXT = '.COM;.EXE;.BAT;.CMD'
        PSModulePath = $trustedModuleRoot
        GIT_CONFIG_NOSYSTEM = '1'
        GIT_CONFIG_GLOBAL = 'NUL'
        GIT_CONFIG_SYSTEM = 'NUL'
        GIT_TERMINAL_PROMPT = '0'
        GCM_INTERACTIVE = 'Never'
        TEMP = $temporaryParent
        TMP = $temporaryParent
        HOME = $temporaryParent
        USERPROFILE = $temporaryParent
        APPDATA = $temporaryParent
        LOCALAPPDATA = $temporaryParent
    }
    Set-ExactProcessEnvironment $signedEnvironmentBase
}

foreach ($required in @($project, $testProject, $catalog, $sourceGate, $packageGate, $sbomTool, $manifestTool)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Entrada de build ausente: $required"
    }
}

[xml]$projectXml = Get-Content -LiteralPath $project -Raw
$version = [string](@($projectXml.Project.PropertyGroup.Version) | Select-Object -First 1)
if (-not $isUnsigned) {
    & $assertPinnedDirectoryTree $trustedPowerShellHome `
        $PowerShellHomeTreeSha256 `
        'PSHOME mudou antes da fase de proveniencia Git.'
    & $assertPinnedDirectoryTree $gitInstallationRoot $GitTreeSha256 `
        'A arvore Git mudou antes da fase de proveniencia.'
    & $assertPinnedFile $GitPath $GitSha256 `
        'git.exe mudou antes da fase de proveniencia.'
    & $assertPinnedFile $gpgPath $GpgSha256 `
        'gpg.exe mudou antes da fase de proveniencia.'
}
$commit = (& $GitPath @trustedGitConfiguration -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$') {
    throw 'HEAD Git invalido.'
}
$shortCommit = $commit.Substring(0, 12)
$sourceBranch = (& $GitPath @trustedGitConfiguration -C $root branch --show-current).Trim()
if (-not $isUnsigned -and
    ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($sourceBranch))) {
    throw 'A branch Git de origem nao pode estar destacada.'
}
$dirtyEntries = @(& $GitPath @trustedGitConfiguration -C $root status --porcelain --untracked-files=all)
$authorityConfigurationFullPath = $null
$authorityIssuerSpkiFullPath = $null
$authorityConfigurationBase64 = $null
$authorityIssuerSpkiBase64 = $null
$contentAuthorityConfigurationFullPath = $null
$contentAuthorityIssuerSpkiFullPath = $null
$contentAuthorityConfigurationBase64 = $null
$contentAuthorityIssuerSpkiBase64 = $null

if (-not $isUnsigned) {
    if ($dirtyEntries.Count -ne 0) { throw 'Build de producao exige arvore Git limpa.' }
    if (-not $sourceBranch.Equals('main', [StringComparison]::Ordinal)) {
        throw 'Candidato assinado exige a branch protegida main.'
    }
    $commonGitDirectoryValue = (& $GitPath @trustedGitConfiguration -C $root `
        rev-parse --git-common-dir).Trim()
    $commonGitDirectory = if ([IO.Path]::IsPathFullyQualified($commonGitDirectoryValue)) {
        [IO.Path]::GetFullPath($commonGitDirectoryValue)
    } else { [IO.Path]::GetFullPath((Join-Path $root $commonGitDirectoryValue)) }
    $expectedGitDirectory = [IO.Path]::GetFullPath((Join-Path $root '.git'))
    if ($LASTEXITCODE -ne 0 -or
        -not $commonGitDirectory.Equals(
            $expectedGitDirectory,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Build assinado exige clone Git normal, sem metadata externa ou worktree de origem.'
    }
    Assert-NoReparseAncestry $commonGitDirectory
    $localAttributesPath = Join-Path $commonGitDirectory 'info\attributes'
    if (Test-Path -LiteralPath $localAttributesPath -PathType Leaf) {
        $localAttributesInfo = Get-Item -LiteralPath $localAttributesPath -Force
        if ($localAttributesInfo.Length -ne 0) {
            throw 'Atributos Git locais nao versionados sao proibidos no build assinado.'
        }
    }
    $upstream = (& $GitPath @trustedGitConfiguration -C $root rev-parse '@{u}').Trim()
    if ($LASTEXITCODE -ne 0 -or $upstream -ne $commit) {
        throw 'Build de producao exige HEAD identico ao upstream publicado.'
    }
    $exactTag = (& $GitPath @trustedGitConfiguration -C $root `
        describe --tags --exact-match HEAD 2>$null).Trim()
    if ($LASTEXITCODE -ne 0 -or $exactTag -ne "v$version") {
        throw "Build de producao exige a tag exata v$version no HEAD."
    }

    # Never verify a Release tag with keys or options inherited from the user
    # profile. Capture the approved public key once, import only its minimal
    # public material into a fresh homedir, and keep every named bootstrap file
    # leased against replacement while Git invokes the pinned gpg.exe.
    $releaseTagPublicKeyFullPath = [IO.Path]::GetFullPath($ReleaseTagPublicKeyPath)
    $releaseTagPublicKeyBytes = Read-BoundedInput `
        $releaseTagPublicKeyFullPath 256 128KB 'A chave publica da tag de Release'
    try {
        Assert-ApprovedSha256 `
            $releaseTagPublicKeyBytes `
            $ReleaseTagPublicKeySha256 `
            'A chave publica exata da tag de Release'
        Assert-NoReparseAncestry $temporaryParent
        $releaseTrustRoot = Join-Path $temporaryParent (
            'TurboramaReleaseTrust-' + [Guid]::NewGuid().ToString('N'))
        $isolatedGnuPgHome = Join-Path $releaseTrustRoot 'gnupg'
        New-Item -ItemType Directory -Path $releaseTrustRoot | Out-Null
        New-Item -ItemType Directory -Path $isolatedGnuPgHome | Out-Null
        Assert-NoReparseAncestry $isolatedGnuPgHome

        $capturedPublicKeyPath = Join-Path $releaseTrustRoot 'release-tag-public-key.bin'
        $releaseTagPublicKeyLease = Write-ProtectedCapturedInput `
            $capturedPublicKeyPath `
            $releaseTagPublicKeyBytes `
            'A chave publica da tag de Release'

        # Git for Windows ships an MSYS GnuPG. Direct PowerShell invocation
        # must pass its data paths in /drive/... form; a native C:\... path is
        # interpreted by gpg as relative to the current directory.
        $isolatedGnuPgHomeForGpg = ConvertTo-GitMsysPath $isolatedGnuPgHome
        $capturedPublicKeyPathForGpg = ConvertTo-GitMsysPath $capturedPublicKeyPath
        $gpgWrapperBytes = New-GpgReleaseWrapperBytes $gpgPath $isolatedGnuPgHome
        try {
            $releaseGpgWrapperPath = Join-Path $releaseTrustRoot 'gpg-release-verify.sh'
            $releaseGpgWrapperLease = Write-ProtectedCapturedInput `
                $releaseGpgWrapperPath `
                $gpgWrapperBytes `
                'O wrapper isolado do GnuPG'
        }
        finally {
            [Security.Cryptography.CryptographicOperations]::ZeroMemory($gpgWrapperBytes)
        }
    }
    finally {
        [Security.Cryptography.CryptographicOperations]::ZeroMemory($releaseTagPublicKeyBytes)
    }

    $env:GNUPGHOME = $isolatedGnuPgHomeForGpg
    $isolatedGpgArguments = @(
        '--no-options',
        '--batch',
        '--no-tty',
        '--no-autostart',
        '--homedir', $isolatedGnuPgHomeForGpg,
        '--no-auto-key-retrieve')
    Invoke-Checked $gpgPath ($isolatedGpgArguments + @(
        '--import-options', 'import-minimal',
        '--import', $capturedPublicKeyPathForGpg))

    $keyListingOutput = @(& $gpgPath @isolatedGpgArguments `
        --with-colons --fingerprint --list-keys 2>&1 |
        ForEach-Object { [string]$_ })
    $keyListingExitCode = $LASTEXITCODE
    if ($keyListingExitCode -ne 0) {
        throw 'Nao foi possivel inventariar a chave publica isolada da tag.'
    }
    $primaryFingerprints = [Collections.Generic.List[string]]::new()
    $allImportedFingerprints = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    $expectPrimaryFingerprint = $false
    foreach ($line in $keyListingOutput) {
        $fields = @($line -split ':')
        if ($fields.Count -eq 0) { continue }
        if ($fields[0] -eq 'pub') {
            $expectPrimaryFingerprint = $true
            continue
        }
        if ($fields[0] -ne 'fpr' -or $fields.Count -le 9) { continue }
        $fingerprint = [string]$fields[9]
        if ($fingerprint -notmatch '^(?:[0-9A-Fa-f]{40}|[0-9A-Fa-f]{64})$') {
            throw 'O keyring isolado retornou uma fingerprint invalida.'
        }
        $fingerprint = $fingerprint.ToUpperInvariant()
        [void]$allImportedFingerprints.Add($fingerprint)
        if ($expectPrimaryFingerprint) {
            $primaryFingerprints.Add($fingerprint)
            $expectPrimaryFingerprint = $false
        }
    }
    if ($primaryFingerprints.Count -ne 1 -or
        -not $primaryFingerprints[0].Equals(
            $ReleaseTagPrimaryKeyFingerprint,
            [StringComparison]::OrdinalIgnoreCase) -or
        -not $allImportedFingerprints.Contains($ReleaseTagSignerFingerprint)) {
        throw 'A chave publica capturada nao contem exatamente a identidade primaria e a chave de assinatura aprovadas.'
    }

    $secretKeyListing = @(& $gpgPath @isolatedGpgArguments `
        --with-colons --list-secret-keys 2>&1 |
        ForEach-Object { [string]$_ })
    $secretKeyListingExitCode = $LASTEXITCODE
    if ($secretKeyListingExitCode -ne 0 -or
        @($secretKeyListing | Where-Object { $_ -match '^sec:' }).Count -ne 0) {
        throw 'A entrada da chave publica nao pode importar material secreto.'
    }

    & $assertPinnedDirectoryTree $gitInstallationRoot $GitTreeSha256 `
        'A arvore Git/sh/GPG mudou antes da verificacao da tag.'
    $tagVerificationOutput = @(& $GitPath @trustedGitConfiguration `
        -c "gpg.program=$releaseGpgWrapperPath" -c 'gpg.format=openpgp' `
        -C $root verify-tag --raw $exactTag 2>&1 |
        ForEach-Object { [string]$_ })
    $tagVerificationExitCode = $LASTEXITCODE
    if ($tagVerificationExitCode -ne 0) {
        throw "Build de producao exige a tag assinada e confiavel $exactTag."
    }
    Assert-ReleaseTagGpgStatus `
        $tagVerificationOutput `
        $ReleaseTagSignerFingerprint `
        $ReleaseTagPrimaryKeyFingerprint
    & $assertPinnedDirectoryTree $gitInstallationRoot $GitTreeSha256 `
        'A arvore Git/sh/GPG mudou durante a verificacao da tag.'
    $localTagObject = (& $GitPath @trustedGitConfiguration -C $root `
        rev-parse "refs/tags/$exactTag").Trim()
    $localTagType = (& $GitPath @trustedGitConfiguration -C $root `
        cat-file -t $localTagObject).Trim()
    if ($LASTEXITCODE -ne 0 -or $localTagObject -notmatch '^[0-9a-f]{40}$' -or
        $localTagType -ne 'tag') {
        throw 'A tag local de Release precisa ser um objeto anotado e assinado.'
    }
    $originUrl = (& $GitPath @trustedGitConfiguration -C $root `
        remote get-url origin).Trim().TrimEnd('/')
    if ($LASTEXITCODE -ne 0 -or
        $originUrl -notin @(
            'https://github.com/luziellacerda/TRUBORAMA-SUITE.git',
            'https://github.com/luziellacerda/TRUBORAMA-SUITE')) {
        throw "Remote origin de producao inesperado: '$originUrl'."
    }
    $transportOriginUrl = (& $GitPath @trustedGitConfiguration -C $root `
        ls-remote --get-url $originUrl).Trim().TrimEnd('/')
    if ($LASTEXITCODE -ne 0 -or
        -not $transportOriginUrl.Equals($originUrl, [StringComparison]::Ordinal)) {
        throw 'Uma regra Git local tentou reescrever a URL oficial do origin.'
    }
    $remoteBranchLine = @(& $GitPath @trustedGitConfiguration -C $root `
        ls-remote --heads $originUrl "refs/heads/$sourceBranch")
    if ($LASTEXITCODE -ne 0 -or $remoteBranchLine.Count -ne 1 -or
        -not $remoteBranchLine[0].StartsWith("$commit`t", [StringComparison]::OrdinalIgnoreCase)) {
        throw 'O commit nao esta publicado na branch origin esperada.'
    }
    $remoteTagLines = @(& $GitPath @trustedGitConfiguration -C $root `
        ls-remote --tags $originUrl "refs/tags/v$version" "refs/tags/v$version^{}")
    if ($LASTEXITCODE -ne 0 -or $remoteTagLines.Count -eq 0) {
        throw "A tag v$version nao esta publicada no origin."
    }
    $remoteTagObject = @($remoteTagLines | Where-Object {
        $_ -match "`trefs/tags/v$([regex]::Escape($version))$"
    })
    $remoteTagCommit = @($remoteTagLines | Where-Object {
        $_ -match "`trefs/tags/v$([regex]::Escape($version))\^\{\}$"
    })
    if ($remoteTagObject.Count -ne 1 -or
        -not $remoteTagObject[0].StartsWith("$localTagObject`t", [StringComparison]::OrdinalIgnoreCase) -or
        $remoteTagCommit.Count -ne 1 -or
        -not $remoteTagCommit[0].StartsWith("$commit`t", [StringComparison]::OrdinalIgnoreCase)) {
        throw "O origin nao publicou exatamente o objeto de tag assinada v$version e seu commit."
    }
    & $assertPinnedFile $GitPath $GitSha256 `
        'git.exe mudou durante a fase de proveniencia.'
    & $assertPinnedFile $gpgPath $GpgSha256 `
        'gpg.exe mudou durante a fase de proveniencia.'
    & $assertPinnedDirectoryTree $gitInstallationRoot $GitTreeSha256 `
        'A arvore Git mudou durante a fase de proveniencia.'
    & $assertPinnedDirectoryTree $trustedPowerShellHome `
        $PowerShellHomeTreeSha256 `
        'PSHOME mudou durante a fase de proveniencia Git.'

    $authorityConfigurationFullPath = [IO.Path]::GetFullPath($AuthorityConfigurationPath)
    $authorityConfigurationBytes = Read-BoundedInput $authorityConfigurationFullPath 64 8KB `
        'O envelope assinado da autoridade'
    $authorityIssuerSpkiBytes = $null
    $authorityConfigurationSha256Normalized = $AuthorityConfigurationSha256.ToLowerInvariant()
    try {
        # Pin the exact captured envelope before signature verification, embedding
        # or inventory. A different still-valid envelope from the same issuer is
        # therefore a rollback and cannot become a production candidate.
        Assert-ApprovedSha256 `
            $authorityConfigurationBytes `
            $authorityConfigurationSha256Normalized `
            'O envelope assinado da autoridade'

        $authorityIssuerSpkiFullPath = [IO.Path]::GetFullPath($AuthorityIssuerSpkiPath)
        $authorityIssuerSpkiBytes = Read-BoundedInput $authorityIssuerSpkiFullPath 256 1KB `
            'A chave SPKI da autoridade'
        Assert-ApprovedSha256 `
            $authorityIssuerSpkiBytes `
            $AuthorityIssuerSpkiSha256 `
            'A chave SPKI offline da autoridade'

        # Keep the signed public configuration small enough for a single native
        # Windows command line. The same captured bytes are verified, embedded and
        # inventoried without any mutable intermediate file.
        $authorityConfigurationBase64 = [Convert]::ToBase64String($authorityConfigurationBytes)
        $authorityIssuerSpkiBase64 = [Convert]::ToBase64String($authorityIssuerSpkiBytes)
    }
    finally {
        [Security.Cryptography.CryptographicOperations]::ZeroMemory(
            $authorityConfigurationBytes)
        if ($null -ne $authorityIssuerSpkiBytes) {
            [Security.Cryptography.CryptographicOperations]::ZeroMemory(
                $authorityIssuerSpkiBytes)
        }
    }

    $contentAuthorityConfigurationFullPath = [IO.Path]::GetFullPath(
        $ContentAuthorityConfigurationPath)
    $contentAuthorityConfigurationBytes = Read-BoundedInput `
        $contentAuthorityConfigurationFullPath 64 8KB `
        'O envelope assinado da autoridade de conteudo'
    $contentAuthorityIssuerSpkiBytes = $null
    $contentAuthorityConfigurationSha256Normalized = `
        $ContentAuthorityConfigurationSha256.ToLowerInvariant()
    try {
        Assert-ApprovedSha256 `
            $contentAuthorityConfigurationBytes `
            $contentAuthorityConfigurationSha256Normalized `
            'O envelope assinado da autoridade de conteudo'

        $contentAuthorityIssuerSpkiFullPath = [IO.Path]::GetFullPath(
            $ContentAuthorityIssuerSpkiPath)
        $contentAuthorityIssuerSpkiBytes = Read-BoundedInput `
            $contentAuthorityIssuerSpkiFullPath 256 1KB `
            'A chave SPKI da autoridade de conteudo'
        Assert-ApprovedSha256 `
            $contentAuthorityIssuerSpkiBytes `
            $ContentAuthorityIssuerSpkiSha256 `
            'A chave SPKI offline da autoridade de conteudo'

        $contentAuthorityConfigurationBase64 = [Convert]::ToBase64String(
            $contentAuthorityConfigurationBytes)
        $contentAuthorityIssuerSpkiBase64 = [Convert]::ToBase64String(
            $contentAuthorityIssuerSpkiBytes)
    }
    finally {
        [Security.Cryptography.CryptographicOperations]::ZeroMemory(
            $contentAuthorityConfigurationBytes)
        if ($null -ne $contentAuthorityIssuerSpkiBytes) {
            [Security.Cryptography.CryptographicOperations]::ZeroMemory(
                $contentAuthorityIssuerSpkiBytes)
        }
    }
}
elseif ($dirtyEntries.Count -ne 0 -and -not $AllowDirty) {
    throw 'A arvore Git esta suja. Use -AllowDirty somente para staging local nao publicavel.'
}

$outputParent = [IO.Path]::GetFullPath($OutputRoot)
if (-not (Test-Path -LiteralPath $outputParent -PathType Container)) {
    New-Item -ItemType Directory -Path $outputParent | Out-Null
}
Assert-NoReparseAncestry $outputParent
$label = if ($isUnsigned) { 'UNSIGNED-NOT-FOR-DISTRIBUTION' } else { 'SIGNED-RELEASE-CANDIDATE' }
$finalPath = Join-Path $outputParent "Turborama-$version-win-x64-$shortCommit-$label"
if (Test-Path -LiteralPath $finalPath) {
    throw "O destino ja existe; nenhum artefato antigo sera sobrescrito: $finalPath"
}
$partialFinalPath = Join-Path $outputParent (
    ".$(Split-Path -Leaf $finalPath).partial-" + [Guid]::NewGuid().ToString('N'))
$promotedFinalPathNeedsCleanup = $false

$work = Join-Path $temporaryParent ("TurboramaRelease-" + [Guid]::NewGuid().ToString('N'))
if (-not $isUnsigned) {
    $work = Assert-NoMsBuildPropertyMetacharacters `
        $work 'O diretorio de trabalho do build Signed'
}
$publish = Join-Path $work 'package'
New-Item -ItemType Directory -Path $work | Out-Null
Assert-NoReparseAncestry $work

$oldSkipNetwork = $env:TURBORAMA_SKIP_NETWORK_TESTS
$oldNuGetPackages = $env:NUGET_PACKAGES
$oldNuGetHttpCache = $env:NUGET_HTTP_CACHE_PATH
$oldNuGetPluginsCache = $env:NUGET_PLUGINS_CACHE_PATH
$oldNuGetScratch = $env:NUGET_SCRATCH
$oldDotNetCliHome = $env:DOTNET_CLI_HOME
$oldTemp = $env:TEMP
$oldTmp = $env:TMP
$oldUserProfile = $env:USERPROFILE
$oldHome = $env:HOME
$oldAppData = $env:APPDATA
$oldLocalAppData = $env:LOCALAPPDATA
$oldMsBuildSdksPath = $env:MSBuildSDKsPath
$oldMsBuildExePath = $env:MSBUILD_EXE_PATH
$oldMsBuildExtensionsPath = $env:MSBuildExtensionsPath
$oldMsBuildUserExtensionsPath = $env:MSBuildUserExtensionsPath
$oldDotNetHostPath = $env:DOTNET_HOST_PATH
$oldDotNetRoot = $env:DOTNET_ROOT
$oldDotNetRootX64 = $env:DOTNET_ROOT_X64
$oldDotNetRootX86 = $env:DOTNET_ROOT_X86
$oldDotNetMultilevelLookup = $env:DOTNET_MULTILEVEL_LOOKUP
$oldDotNetRollForward = $env:DOTNET_ROLL_FORWARD
$oldDotNetRollForwardPrerelease = $env:DOTNET_ROLL_FORWARD_TO_PRERELEASE
$oldDotNetResolverSdksDir = $env:DOTNET_MSBUILD_SDK_RESOLVER_SDKS_DIR
$oldDotNetResolverCliDir = $env:DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR
$oldDotNetResolverSdksVer = $env:DOTNET_MSBUILD_SDK_RESOLVER_SDKS_VER
$oldDotNetNoMsBuildServer = $env:DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER
$oldMsBuildDisableNodeReuse = $env:MSBUILDDISABLENODEREUSE
$oldDotNetTelemetry = $env:DOTNET_CLI_TELEMETRY_OPTOUT
$oldDotNetNoLogo = $env:DOTNET_NOLOGO
$oldDotNetSkipFirstRun = $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE
$oldDotNetGenerateCertificate = $env:DOTNET_GENERATE_ASPNET_CERTIFICATE
$oldDotNetAddToolsPath = $env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH
$oldDotNetWorkloadNotify = $env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE
$runtimeEnvironmentNames = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::OrdinalIgnoreCase)
foreach ($environmentName in [Environment]::GetEnvironmentVariables('Process').Keys) {
    if ([string]$environmentName -match '^(?:COMPlus_|DOTNET_|CORECLR_|COR_)') {
        [void]$runtimeEnvironmentNames.Add([string]$environmentName)
    }
}
# Include every controlled variable even when initially absent, so cleanup also
# removes values introduced by the build itself.
foreach ($environmentName in @(
    'DOTNET_CLI_HOME',
    'DOTNET_HOST_PATH',
    'DOTNET_ROOT',
    'DOTNET_ROOT_X64',
    'DOTNET_ROOT_X86',
    'DOTNET_MULTILEVEL_LOOKUP',
    'DOTNET_ROLL_FORWARD',
    'DOTNET_ROLL_FORWARD_TO_PRERELEASE',
    'DOTNET_MSBUILD_SDK_RESOLVER_SDKS_DIR',
    'DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR',
    'DOTNET_MSBUILD_SDK_RESOLVER_SDKS_VER',
    'DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER',
    'DOTNET_CLI_TELEMETRY_OPTOUT',
    'DOTNET_NOLOGO',
    'DOTNET_SKIP_FIRST_TIME_EXPERIENCE',
    'DOTNET_GENERATE_ASPNET_CERTIFICATE',
    'DOTNET_ADD_GLOBAL_TOOLS_TO_PATH',
    'DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE',
    'DOTNET_NUGET_SIGNATURE_VERIFICATION')) {
    [void]$runtimeEnvironmentNames.Add($environmentName)
}
$savedRuntimeEnvironment = @{}
foreach ($environmentName in $runtimeEnvironmentNames) {
    $savedRuntimeEnvironment[$environmentName] =
        [Environment]::GetEnvironmentVariable($environmentName, 'Process')
}
$blockedAmbientVariableNames = @(
    'CustomBeforeDirectoryBuildProps',
    'CustomAfterDirectoryBuildProps',
    'CustomBeforeDirectoryBuildTargets',
    'CustomAfterDirectoryBuildTargets',
    'CustomBeforeMicrosoftCommonProps',
    'CustomAfterMicrosoftCommonProps',
    'CustomBeforeMicrosoftCommonTargets',
    'CustomAfterMicrosoftCommonTargets',
    'CustomBeforeMicrosoftCSharpTargets',
    'CustomAfterMicrosoftCSharpTargets',
    'CustomBeforeMicrosoftVisualBasicTargets',
    'CustomAfterMicrosoftVisualBasicTargets',
    'MSBuildProjectExtensionsPath',
    'MSBuildStartupDirectory',
    'MSBuildExtensionsPath32',
    'MSBuildExtensionsPath64',
    'MSBuildOverrideTasksPath',
    'MSBuildOverrideTasksPath64',
    'MSBuildToolsPath',
    'RoslynTargetsPath',
    'CscToolPath',
    'CscToolExe',
    'VbcToolPath',
    'VbcToolExe',
    'DOTNET_STARTUP_HOOKS',
    'DOTNET_ADDITIONAL_DEPS',
    'DOTNET_SHARED_STORE',
    'CORECLR_ENABLE_PROFILING',
    'CORECLR_PROFILER',
    'CORECLR_PROFILER_PATH',
    'CORECLR_PROFILER_PATH_32',
    'CORECLR_PROFILER_PATH_64',
    'COR_ENABLE_PROFILING',
    'COR_PROFILER',
    'COR_PROFILER_PATH',
    'COR_PROFILER_PATH_32',
    'COR_PROFILER_PATH_64',
    'NUGET_PLUGIN_PATHS',
    'NUGET_CREDENTIALPROVIDERS_PATH',
    'VSS_NUGET_EXTERNAL_FEED_ENDPOINTS')
$savedBlockedAmbientVariables = @{}
foreach ($variableName in $blockedAmbientVariableNames) {
    $savedBlockedAmbientVariables[$variableName] =
        [Environment]::GetEnvironmentVariable($variableName, 'Process')
}
$oldNuGetCertRevocationMode = $env:NUGET_CERT_REVOCATION_MODE
$oldDotNetNuGetSignatureVerification = $env:DOTNET_NUGET_SIGNATURE_VERIFICATION
$sourceSnapshot = $null
try {
    $isolatedNuGetPackages = Join-Path $work 'nuget-packages'
    $isolatedNuGetHttpCache = Join-Path $work 'nuget-http-cache'
    $isolatedNuGetPluginsCache = Join-Path $work 'nuget-plugins-cache'
    $isolatedNuGetPluginDirectory = Join-Path $work 'nuget-plugins-disabled'
    $isolatedDotNetCliHome = Join-Path $work 'dotnet-cli-home'
    $isolatedNuGetScratch = Join-Path $work 'nuget-scratch'
    $isolatedTemp = Join-Path $work 'temp'
    $isolatedUserProfile = Join-Path $work 'profile'
    $isolatedAppData = Join-Path $isolatedUserProfile 'AppData\Roaming'
    $isolatedLocalAppData = Join-Path $isolatedUserProfile 'AppData\Local'
    $isolatedGitTemplate = Join-Path $work 'git-template-empty'
    foreach ($isolatedCache in @(
        $isolatedNuGetPackages,
        $isolatedNuGetHttpCache,
        $isolatedNuGetPluginsCache,
        $isolatedNuGetPluginDirectory,
        $isolatedDotNetCliHome,
        $isolatedNuGetScratch,
        $isolatedTemp,
        $isolatedUserProfile,
        $isolatedAppData,
        $isolatedLocalAppData,
        $isolatedGitTemplate)) {
        New-Item -ItemType Directory -Path $isolatedCache | Out-Null
    }
    if (-not $isUnsigned) {
        $controlledSignedEnvironment = [ordered]@{}
        foreach ($entry in $signedEnvironmentBase.GetEnumerator()) {
            $controlledSignedEnvironment[[string]$entry.Key] = [string]$entry.Value
        }
        $controlledSignedEnvironment.GNUPGHOME = $isolatedGnuPgHomeForGpg
        Set-ExactProcessEnvironment $controlledSignedEnvironment
    }
    else {
        foreach ($environmentName in $runtimeEnvironmentNames) {
            [Environment]::SetEnvironmentVariable($environmentName, $null, 'Process')
        }
    }
    $env:NUGET_PACKAGES = $isolatedNuGetPackages
    $env:NUGET_HTTP_CACHE_PATH = $isolatedNuGetHttpCache
    $env:NUGET_PLUGINS_CACHE_PATH = $isolatedNuGetPluginsCache
    $env:NUGET_SCRATCH = $isolatedNuGetScratch
    $env:DOTNET_CLI_HOME = $isolatedDotNetCliHome
    $env:TEMP = $isolatedTemp
    $env:TMP = $isolatedTemp
    $env:USERPROFILE = $isolatedUserProfile
    $env:HOME = $isolatedUserProfile
    $env:APPDATA = $isolatedAppData
    $env:LOCALAPPDATA = $isolatedLocalAppData
    $env:MSBuildSDKsPath = $null
    $env:MSBUILD_EXE_PATH = $null
    $env:MSBuildExtensionsPath = $null
    $env:MSBuildUserExtensionsPath = $null
    $env:DOTNET_HOST_PATH = $DotNetPath
    $env:DOTNET_ROOT = Split-Path -Parent $DotNetPath
    $env:DOTNET_ROOT_X64 = Split-Path -Parent $DotNetPath
    $env:DOTNET_ROOT_X86 = $null
    $env:DOTNET_MULTILEVEL_LOOKUP = '0'
    $env:DOTNET_ROLL_FORWARD = 'LatestPatch'
    $env:DOTNET_ROLL_FORWARD_TO_PRERELEASE = '0'
    $env:DOTNET_MSBUILD_SDK_RESOLVER_SDKS_DIR = $null
    $env:DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR = $null
    $env:DOTNET_MSBUILD_SDK_RESOLVER_SDKS_VER = $null
    $env:DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER = '1'
    $env:MSBUILDDISABLENODEREUSE = '1'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    $env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
    $env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = '1'
    if ($isUnsigned) {
        foreach ($variableName in $blockedAmbientVariableNames) {
            [Environment]::SetEnvironmentVariable($variableName, $null, 'Process')
        }
    }
    $env:NUGET_PLUGIN_PATHS = $isolatedNuGetPluginDirectory
    $env:NUGET_CREDENTIALPROVIDERS_PATH = $isolatedNuGetPluginDirectory
    $env:NUGET_CERT_REVOCATION_MODE = 'online'
    $env:DOTNET_NUGET_SIGNATURE_VERIFICATION = 'true'

    $buildRoot = $root
    if (-not $isUnsigned) {
        # Production candidates are fetched again from the authenticated HTTPS
        # origin into a repository with an empty template and sanitized config.
        # The local object database, filters, attributes and checkout config are
        # therefore outside the signed build's trust boundary.
        & $assertPinnedDirectoryTree $trustedPowerShellHome `
            $PowerShellHomeTreeSha256 `
            'PSHOME mudou antes da fase de snapshot Git.'
        & $assertPinnedDirectoryTree $gitInstallationRoot $GitTreeSha256 `
            'A arvore Git mudou antes da fase de snapshot.'
        & $assertPinnedFile $GitPath $GitSha256 `
            'git.exe mudou antes da fase de snapshot.'
        $sourceSnapshot = Join-Path $work 'source'
        Invoke-Checked $GitPath ($trustedGitConfiguration + @(
            'init', '--quiet', "--initial-branch=$sourceBranch",
            "--template=$isolatedGitTemplate", $sourceSnapshot))
        Invoke-Checked $GitPath ($trustedGitConfiguration + @(
            '-C', $sourceSnapshot, 'remote', 'add', 'origin', $originUrl))
        Invoke-Checked $GitPath ($trustedGitConfiguration + @(
            '-C', $sourceSnapshot, 'fetch', '--no-tags', '--depth=1',
            '--no-recurse-submodules', 'origin',
            "refs/heads/${sourceBranch}:refs/remotes/origin/$sourceBranch"))
        $fetchedBranchCommit = (& $GitPath @trustedGitConfiguration `
            -C $sourceSnapshot rev-parse "refs/remotes/origin/$sourceBranch").Trim()
        if ($LASTEXITCODE -ne 0 -or $fetchedBranchCommit -ne $commit) {
            throw 'O snapshot isolado nao reproduziu o commit remoto aprovado.'
        }
        Invoke-Checked $GitPath ($trustedGitConfiguration + @(
            '-C', $sourceSnapshot, 'fetch', '--no-tags', '--depth=1',
            '--no-recurse-submodules', 'origin',
            "refs/tags/v${version}:refs/tags/v$version"))
        $fetchedTagObject = (& $GitPath @trustedGitConfiguration `
            -C $sourceSnapshot rev-parse "refs/tags/v$version").Trim()
        $fetchedTagCommit = (& $GitPath @trustedGitConfiguration `
            -C $sourceSnapshot rev-parse "refs/tags/v$version^{}").Trim()
        if ($LASTEXITCODE -ne 0 -or
            $fetchedTagObject -ne $localTagObject -or
            $fetchedTagCommit -ne $commit) {
            throw 'O snapshot isolado nao reproduziu o objeto de tag assinado aprovado.'
        }
        Invoke-Checked $GitPath ($trustedGitConfiguration + @(
            '-C', $sourceSnapshot, 'checkout', '--force', '-B',
            $sourceBranch, $commit))
        $snapshotDirtyEntries = @(& $GitPath @trustedGitConfiguration `
            -C $sourceSnapshot status --porcelain --untracked-files=all)
        if ($LASTEXITCODE -ne 0 -or $snapshotDirtyEntries.Count -ne 0) {
            throw 'O snapshot remoto isolado nao ficou byte-estavel apos o checkout.'
        }
        $buildRoot = $sourceSnapshot

        & $assertPinnedFile $GitPath $GitSha256 `
            'git.exe mudou durante a fase de snapshot.'
        & $assertPinnedDirectoryTree $gitInstallationRoot $GitTreeSha256 `
            'A arvore Git mudou durante a fase de snapshot.'
        & $assertPinnedDirectoryTree $trustedPowerShellHome `
            $PowerShellHomeTreeSha256 `
            'PSHOME mudou durante a fase de snapshot Git.'
    }
    else {
        # Never restore/build in the caller's dirty working tree. project.assets.json
        # contains absolute package-cache paths; writing it in the caller and then
        # deleting the isolated cache would corrupt subsequent --no-restore builds.
        # Copy exactly the Git-visible local snapshot plus its metadata into work.
        $sourceSnapshot = Join-Path $work 'source'
        [IO.Directory]::CreateDirectory($sourceSnapshot) > $null
        $sourceGitMetadata = Join-Path $root '.git'
        if (-not [IO.Directory]::Exists($sourceGitMetadata)) {
            throw 'Staging unsigned isolado exige clone Git normal com diretorio .git.'
        }
        Copy-BclRegularDirectorySnapshot `
            $sourceGitMetadata (Join-Path $sourceSnapshot '.git')

        $localSnapshotEntries = @(& $GitPath @trustedGitConfiguration `
            -C $root ls-files --cached --others --exclude-standard)
        if ($LASTEXITCODE -ne 0) {
            throw 'Nao foi possivel enumerar o snapshot local para staging unsigned.'
        }
        $rootPrefix = $root.TrimEnd([IO.Path]::DirectorySeparatorChar) +
            [IO.Path]::DirectorySeparatorChar
        $snapshotPrefix = $sourceSnapshot.TrimEnd(
            [IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        foreach ($relativeSnapshotEntry in $localSnapshotEntries) {
            if ([string]::IsNullOrWhiteSpace($relativeSnapshotEntry) -or
                [IO.Path]::IsPathFullyQualified($relativeSnapshotEntry) -or
                $relativeSnapshotEntry.Contains(':')) {
                throw "Entrada Git local nao canonica: $relativeSnapshotEntry"
            }
            $relativeSegments = @($relativeSnapshotEntry -split '[\\/]')
            if ($relativeSegments.Count -eq 0 -or
                @($relativeSegments | Where-Object {
                    $_ -in @('', '.', '..')
                }).Count -ne 0) {
                throw "Entrada Git local insegura: $relativeSnapshotEntry"
            }

            $localSourcePath = [IO.Path]::GetFullPath(
                [IO.Path]::Combine($root, $relativeSnapshotEntry))
            $localDestinationPath = [IO.Path]::GetFullPath(
                [IO.Path]::Combine($sourceSnapshot, $relativeSnapshotEntry))
            if (-not $localSourcePath.StartsWith(
                    $rootPrefix,
                    [StringComparison]::OrdinalIgnoreCase) -or
                -not $localDestinationPath.StartsWith(
                    $snapshotPrefix,
                    [StringComparison]::OrdinalIgnoreCase)) {
                throw "Entrada Git local escapou do snapshot: $relativeSnapshotEntry"
            }
            if ([IO.File]::Exists($localSourcePath)) {
                Copy-BclRegularFileSnapshot $localSourcePath $localDestinationPath
            }
            elseif ([IO.Directory]::Exists($localSourcePath)) {
                throw "Submodulo/diretorio versionado nao e permitido: $relativeSnapshotEntry"
            }
            # A tracked file may be intentionally deleted in the dirty snapshot.
        }

        $snapshotCommit = (& $GitPath @trustedGitConfiguration `
            -C $sourceSnapshot rev-parse HEAD).Trim()
        $copiedDirtyEntries = @(& $GitPath @trustedGitConfiguration `
            -C $sourceSnapshot status --porcelain --untracked-files=all)
        if ($LASTEXITCODE -ne 0 -or $snapshotCommit -ne $commit) {
            throw 'O staging unsigned isolado nao preservou o commit local.'
        }
        $expectedDirtyEntries = @($dirtyEntries | Sort-Object -CaseSensitive)
        $actualDirtyEntries = @($copiedDirtyEntries | Sort-Object -CaseSensitive)
        if ($expectedDirtyEntries.Count -ne $actualDirtyEntries.Count) {
            throw 'O staging unsigned isolado nao preservou a arvore suja local.'
        }
        for ($dirtyIndex = 0; $dirtyIndex -lt $expectedDirtyEntries.Count; $dirtyIndex++) {
            if (-not $expectedDirtyEntries[$dirtyIndex].Equals(
                    $actualDirtyEntries[$dirtyIndex],
                    [StringComparison]::Ordinal)) {
                throw 'O staging unsigned isolado divergiu da arvore suja local.'
            }
        }
        $buildRoot = $sourceSnapshot
    }

    $project = Join-Path $buildRoot 'TurboBoxManager.csproj'
    $testProject = Join-Path $buildRoot 'tests\CatalogVerifier\CatalogVerifier.csproj'
    $catalog = Join-Path $buildRoot 'Assets\Catalog\catalog.json'
    $sourceGate = Join-Path $buildRoot 'tools\Test-ReleaseSource.ps1'
    $packageGate = Join-Path $buildRoot 'tools\Test-PublishedPackage.ps1'
    $sbomTool = Join-Path $buildRoot 'tools\New-ReleaseSbom.ps1'
    $manifestTool = Join-Path $buildRoot 'tools\New-ReleaseManifest.ps1'

    $directoryBuildPropsPath = Join-Path $buildRoot 'Directory.Build.props'
    if (-not $isUnsigned) {
        $buildRoot = Assert-NoMsBuildPropertyMetacharacters `
            $buildRoot 'O snapshot de origem do build Signed'
        $directoryBuildPropsPath = Assert-NoMsBuildPropertyMetacharacters `
            $directoryBuildPropsPath 'DirectoryBuildPropsPath do build Signed'
    }
    $msBuildSecurityProperties = @(
        "-p:DirectoryBuildPropsPath=$directoryBuildPropsPath",
        '-p:ImportDirectoryBuildProps=true',
        '-p:ImportDirectoryBuildTargets=false',
        '-p:MSBuildLoadMicrosoftTargetsReadOnly=true',
        '-p:UseSharedCompilation=false')

    if (-not $isUnsigned) {
        & $assertPinnedDirectoryTree $trustedPowerShellHome `
            $PowerShellHomeTreeSha256 `
            'PSHOME mudou antes do gate de fonte.'
        & $assertPinnedDirectoryTree $gitInstallationRoot $GitTreeSha256 `
            'A arvore Git mudou antes do gate de fonte.'
    }
    & $sourceGate -RepositoryRoot $buildRoot -GitPath $GitPath
    if ($LASTEXITCODE -ne 0) { throw 'O gate de fonte reprovou a compilacao.' }
    if (-not $isUnsigned) {
        & $assertPinnedDirectoryTree $gitInstallationRoot $GitTreeSha256 `
            'A arvore Git mudou durante o gate de fonte.'
        & $assertPinnedDirectoryTree $trustedPowerShellHome `
            $PowerShellHomeTreeSha256 `
            'PSHOME mudou durante o gate de fonte.'
        & $assertPinnedDirectoryTree $dotNetSdkRoot $DotNetSdkTreeSha256 `
            'A arvore do SDK .NET mudou antes da fase de build.'
    }

    $sdkVersion = (& $DotNetPath --version).Trim()
    if ($LASTEXITCODE -ne 0 -or $sdkVersion -ne '10.0.400') {
        throw "SDK incorreto: esperado 10.0.400, encontrado '$sdkVersion'."
    }

    New-Item -ItemType Directory -Path $publish | Out-Null
    $env:TURBORAMA_SKIP_NETWORK_TESTS = '1'
    $nuGetConfiguration = Join-Path $buildRoot 'NuGet.Config'
    Invoke-Checked $DotNetPath (@('restore', $testProject, '--locked-mode', '--no-http-cache', '--packages', $isolatedNuGetPackages, '--configfile', $nuGetConfiguration, '-p:Configuration=Release') + $msBuildSecurityProperties)
    # Restore the runtime-specific application graph last. Restoring the test
    # project afterwards would replace the app assets file with a graph that
    # lacks the win-x64 target required by --no-restore publish.
    Invoke-Checked $DotNetPath (@('restore', $project, '-r', 'win-x64', '--locked-mode', '--no-http-cache', '--packages', $isolatedNuGetPackages, '--configfile', $nuGetConfiguration, '-p:Configuration=Release') + $msBuildSecurityProperties)
    Invoke-Checked $DotNetPath (@('build', $project, '-c', 'Release', '--no-restore') + $msBuildSecurityProperties)
    Invoke-Checked $DotNetPath (@('run', '--project', $testProject, '-c', 'Release', '--no-restore') + $msBuildSecurityProperties + @('--', $catalog))
    $authorityVerifierAssembly = Join-Path `
        (Split-Path -Parent $testProject) 'bin\Release\net10.0-windows\CatalogVerifier.dll'
    if (-not $isUnsigned) {
        if (-not (Test-Path -LiteralPath $authorityVerifierAssembly -PathType Leaf)) {
            throw 'O build nao produziu o verificador criptografico da autoridade.'
        }
        Invoke-Checked $DotNetPath (@(
            $authorityVerifierAssembly, '--verify-authority-base64',
            $authorityConfigurationBase64, $authorityIssuerSpkiBase64,
            $authorityConfigurationSha256Normalized))
        Invoke-Checked $DotNetPath (@(
            $authorityVerifierAssembly, '--verify-content-authority-base64',
            $contentAuthorityConfigurationBase64,
            $contentAuthorityIssuerSpkiBase64,
            $contentAuthorityConfigurationSha256Normalized))
    }
    $publishArguments = @(
        'publish', $project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '--no-restore',
        '-p:PublishSingleFile=true', '-p:PublishTrimmed=false', '-p:PublishReadyToRun=false',
        '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:DebugType=None', '-p:DebugSymbols=false',
        '-o', $publish)
    $publishArguments += $msBuildSecurityProperties
    if (-not $isUnsigned) {
        $publishArguments += "-p:SuiteAuthorityConfigurationBase64=$authorityConfigurationBase64"
        $publishArguments += "-p:SuiteAuthorityConfigurationSha256=$authorityConfigurationSha256Normalized"
        $publishArguments += "-p:SuiteAuthorityIssuerSpkiBase64=$authorityIssuerSpkiBase64"
        $publishArguments += "-p:SuiteContentAuthorityConfigurationBase64=$contentAuthorityConfigurationBase64"
        $publishArguments += "-p:SuiteContentAuthorityConfigurationSha256=$contentAuthorityConfigurationSha256Normalized"
        $publishArguments += "-p:SuiteContentAuthorityIssuerSpkiBase64=$contentAuthorityIssuerSpkiBase64"
    }
    Invoke-Checked $DotNetPath $publishArguments
    if (-not $isUnsigned) {
        & $assertPinnedDirectoryTree $dotNetSdkRoot $DotNetSdkTreeSha256 `
            'A arvore do SDK .NET mudou durante a fase de build.'
        & $assertPinnedDirectoryTree $trustedPowerShellHome `
            $PowerShellHomeTreeSha256 `
            'PSHOME mudou durante a fase de build .NET.'
    }

    $exe = Join-Path $publish 'Turborama.exe'
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
        throw 'A publicacao nao gerou Turborama.exe.'
    }

    if (-not $isUnsigned) {
        & $assertPinnedFile $SignToolPath $SignToolSha256 `
            'signtool.exe mudou antes da fase de assinatura.'
        $signTool = Find-SignTool $SignToolPath $SignToolSha256
        Invoke-Checked $signTool @(
            'sign', '/sha1', $CertificateThumbprint.ToUpperInvariant(), '/fd', 'SHA256',
            '/tr', $TimestampUrl, '/td', 'SHA256', '/d', 'Turborama', '/v', $exe)
        Invoke-Checked $signTool @('verify', '/pa', '/all', '/v', $exe)
        $signature = Microsoft.PowerShell.Security\Get-AuthenticodeSignature `
            -LiteralPath $exe
        if ($signature.Status -ne 'Valid' -or
            $null -eq $signature.SignerCertificate -or
            -not $signature.SignerCertificate.Thumbprint.Equals($CertificateThumbprint, [StringComparison]::OrdinalIgnoreCase) -or
            $null -eq $signature.TimeStamperCertificate -or
            -not $signature.TimeStamperCertificate.Thumbprint.Equals(
                $TimestampCertificateThumbprint,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw 'A assinatura ou o carimbo RFC 3161 nao corresponde ao certificado solicitado.'
        }
        & $assertPinnedFile $SignToolPath $SignToolSha256 `
            'signtool.exe mudou durante a fase de assinatura.'
    }

    # The executable hash recorded in the SBOM must describe the final signed
    # bytes. Generating it before Authenticode would make the production SBOM
    # stale by construction.
    if (-not $isUnsigned) {
        & $assertPinnedDirectoryTree $trustedPowerShellHome `
            $PowerShellHomeTreeSha256 `
            'PSHOME mudou antes da fase de gates do pacote.'
        & $assertPinnedDirectoryTree $gitInstallationRoot $GitTreeSha256 `
            'A arvore Git mudou antes da fase de gates do pacote.'
        & $assertPinnedDirectoryTree $dotNetSdkRoot $DotNetSdkTreeSha256 `
            'A arvore do SDK .NET mudou antes da fase de gates do pacote.'
    }
    & $sbomTool -PackageRoot $publish `
        -OutputPath (Join-Path $publish 'Turborama.spdx.json') `
        -RepositoryRoot $buildRoot -GitPath $GitPath
    if ($LASTEXITCODE -ne 0) { throw 'Falha ao gerar SBOM.' }

    $manifestArguments = @{
        PackageRoot = $publish
        OutputPath = (Join-Path $publish 'RELEASE-MANIFEST.json')
        RepositoryRoot = $buildRoot
        DotNetPath = $DotNetPath
        GitPath = $GitPath
        SourceBranch = $sourceBranch
    }
    if ($isUnsigned) { $manifestArguments.Unsigned = $true }
    else {
        $manifestArguments.AuthorityConfigurationBase64 = $authorityConfigurationBase64
        $manifestArguments.AuthorityConfigurationSha256 = $authorityConfigurationSha256Normalized
        $manifestArguments.AuthorityIssuerSpkiBase64 = $authorityIssuerSpkiBase64
        $manifestArguments.ContentAuthorityConfigurationBase64 = $contentAuthorityConfigurationBase64
        $manifestArguments.ContentAuthorityConfigurationSha256 = $contentAuthorityConfigurationSha256Normalized
        $manifestArguments.ContentAuthorityIssuerSpkiBase64 = $contentAuthorityIssuerSpkiBase64
        $manifestArguments.PowerShellSha256 = $PowerShellSha256
        $manifestArguments.PowerShellHomeTreeSha256 = $PowerShellHomeTreeSha256
        $manifestArguments.GitSha256 = $GitSha256
        $manifestArguments.GitTreeSha256 = $GitTreeSha256
        $manifestArguments.DotNetSdkTreeSha256 = $DotNetSdkTreeSha256
        $manifestArguments.SignToolSha256 = $SignToolSha256
        $manifestArguments.ReleaseTagPublicKeySha256 = $ReleaseTagPublicKeySha256
    }
    & $manifestTool @manifestArguments
    if ($LASTEXITCODE -ne 0) { throw 'Falha ao gerar manifesto.' }

    $gateArguments = @{
        PackageRoot = $publish
        RepositoryRoot = $buildRoot
        GitPath = $GitPath
    }
    if ($isUnsigned) { $gateArguments.AllowUnsigned = $true }
    else {
        $gateArguments.CertificateThumbprint = $CertificateThumbprint
        $gateArguments.TimestampCertificateThumbprint = $TimestampCertificateThumbprint
        $gateArguments.AuthorityConfigurationBase64 = $authorityConfigurationBase64
        $gateArguments.AuthorityConfigurationSha256 = $authorityConfigurationSha256Normalized
        $gateArguments.AuthorityIssuerSpkiBase64 = $authorityIssuerSpkiBase64
        $gateArguments.AuthorityVerifierAssemblyPath = $authorityVerifierAssembly
        $gateArguments.ContentAuthorityConfigurationBase64 = $contentAuthorityConfigurationBase64
        $gateArguments.ContentAuthorityConfigurationSha256 = $contentAuthorityConfigurationSha256Normalized
        $gateArguments.ContentAuthorityIssuerSpkiBase64 = $contentAuthorityIssuerSpkiBase64
        $gateArguments.DotNetPath = $DotNetPath
        $gateArguments.PowerShellSha256 = $PowerShellSha256
        $gateArguments.PowerShellHomeTreeSha256 = $PowerShellHomeTreeSha256
        $gateArguments.GitSha256 = $GitSha256
        $gateArguments.GitTreeSha256 = $GitTreeSha256
        $gateArguments.DotNetSdkTreeSha256 = $DotNetSdkTreeSha256
        $gateArguments.SignToolSha256 = $SignToolSha256
        $gateArguments.ReleaseTagPublicKeySha256 = $ReleaseTagPublicKeySha256
    }
    & $packageGate @gateArguments
    if ($LASTEXITCODE -ne 0) { throw 'O pacote publicado foi reprovado.' }

    # Copy into a hidden-by-name sibling, validate the copied bytes once more,
    # then atomically rename on the destination volume. A cross-volume move
    # could otherwise expose a partially copied final package.
    New-Item -ItemType Directory -Path $partialFinalPath | Out-Null
    Get-ChildItem -LiteralPath $publish -Force |
        Microsoft.PowerShell.Management\Copy-Item `
            -Destination $partialFinalPath -Recurse -Force
    $copiedGateArguments = @{
        PackageRoot = $partialFinalPath
        RepositoryRoot = $buildRoot
        GitPath = $GitPath
    }
    if ($isUnsigned) { $copiedGateArguments.AllowUnsigned = $true }
    else {
        $copiedGateArguments.CertificateThumbprint = $CertificateThumbprint
        $copiedGateArguments.TimestampCertificateThumbprint = $TimestampCertificateThumbprint
        $copiedGateArguments.AuthorityConfigurationBase64 = $authorityConfigurationBase64
        $copiedGateArguments.AuthorityConfigurationSha256 = $authorityConfigurationSha256Normalized
        $copiedGateArguments.AuthorityIssuerSpkiBase64 = $authorityIssuerSpkiBase64
        $copiedGateArguments.AuthorityVerifierAssemblyPath = $authorityVerifierAssembly
        $copiedGateArguments.ContentAuthorityConfigurationBase64 = $contentAuthorityConfigurationBase64
        $copiedGateArguments.ContentAuthorityConfigurationSha256 = $contentAuthorityConfigurationSha256Normalized
        $copiedGateArguments.ContentAuthorityIssuerSpkiBase64 = $contentAuthorityIssuerSpkiBase64
        $copiedGateArguments.DotNetPath = $DotNetPath
        $copiedGateArguments.PowerShellSha256 = $PowerShellSha256
        $copiedGateArguments.PowerShellHomeTreeSha256 = $PowerShellHomeTreeSha256
        $copiedGateArguments.GitSha256 = $GitSha256
        $copiedGateArguments.GitTreeSha256 = $GitTreeSha256
        $copiedGateArguments.DotNetSdkTreeSha256 = $DotNetSdkTreeSha256
        $copiedGateArguments.SignToolSha256 = $SignToolSha256
        $copiedGateArguments.ReleaseTagPublicKeySha256 = $ReleaseTagPublicKeySha256
    }
    & $packageGate @copiedGateArguments
    if ($LASTEXITCODE -ne 0) { throw 'A copia final do pacote foi reprovada.' }
    Rename-Item -LiteralPath $partialFinalPath -NewName (Split-Path -Leaf $finalPath)
    $promotedFinalPathNeedsCleanup = $true
    $promotedGateArguments = $copiedGateArguments.Clone()
    $promotedGateArguments.PackageRoot = $finalPath
    & $packageGate @promotedGateArguments
    if ($LASTEXITCODE -ne 0) { throw 'O pacote promovido foi reprovado apos o rename atomico.' }
    if (-not $isUnsigned) {
        & $assertPinnedDirectoryTree $dotNetSdkRoot $DotNetSdkTreeSha256 `
            'A arvore do SDK .NET mudou durante os gates do pacote.'
        & $assertPinnedDirectoryTree $gitInstallationRoot $GitTreeSha256 `
            'A arvore Git mudou durante os gates do pacote.'
        & $assertPinnedDirectoryTree $trustedPowerShellHome `
            $PowerShellHomeTreeSha256 `
            'PSHOME mudou durante os gates do pacote.'
    }
    $promotedFinalPathNeedsCleanup = $false
    $partialFinalPath = $null
    Write-Host "PACOTE: $finalPath"
    Write-Host "EXE: $(Join-Path $finalPath 'Turborama.exe')"
    if ($isUnsigned) {
        Write-Warning 'STAGING SEM ASSINATURA: NAO PUBLICAR NEM DISTRIBUIR COMO PRODUCAO.'
    }
    else {
        Write-Warning 'CANDIDATO ASSINADO: somente distribuir apos backend Suite, artefatos autorizados e assinatura do pacote completo passarem pelo checklist.'
    }
}
finally {
    $env:TURBORAMA_SKIP_NETWORK_TESTS = $oldSkipNetwork
    $env:NUGET_PACKAGES = $oldNuGetPackages
    $env:NUGET_HTTP_CACHE_PATH = $oldNuGetHttpCache
    $env:NUGET_PLUGINS_CACHE_PATH = $oldNuGetPluginsCache
    $env:NUGET_SCRATCH = $oldNuGetScratch
    $env:DOTNET_CLI_HOME = $oldDotNetCliHome
    $env:TEMP = $oldTemp
    $env:TMP = $oldTmp
    $env:USERPROFILE = $oldUserProfile
    $env:HOME = $oldHome
    $env:APPDATA = $oldAppData
    $env:LOCALAPPDATA = $oldLocalAppData
    $env:MSBuildSDKsPath = $oldMsBuildSdksPath
    $env:MSBUILD_EXE_PATH = $oldMsBuildExePath
    $env:MSBuildExtensionsPath = $oldMsBuildExtensionsPath
    $env:MSBuildUserExtensionsPath = $oldMsBuildUserExtensionsPath
    $env:DOTNET_HOST_PATH = $oldDotNetHostPath
    $env:DOTNET_ROOT = $oldDotNetRoot
    $env:DOTNET_ROOT_X64 = $oldDotNetRootX64
    $env:DOTNET_ROOT_X86 = $oldDotNetRootX86
    $env:DOTNET_MULTILEVEL_LOOKUP = $oldDotNetMultilevelLookup
    $env:DOTNET_ROLL_FORWARD = $oldDotNetRollForward
    $env:DOTNET_ROLL_FORWARD_TO_PRERELEASE = $oldDotNetRollForwardPrerelease
    $env:DOTNET_MSBUILD_SDK_RESOLVER_SDKS_DIR = $oldDotNetResolverSdksDir
    $env:DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR = $oldDotNetResolverCliDir
    $env:DOTNET_MSBUILD_SDK_RESOLVER_SDKS_VER = $oldDotNetResolverSdksVer
    $env:DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER = $oldDotNetNoMsBuildServer
    $env:MSBUILDDISABLENODEREUSE = $oldMsBuildDisableNodeReuse
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = $oldDotNetTelemetry
    $env:DOTNET_NOLOGO = $oldDotNetNoLogo
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = $oldDotNetSkipFirstRun
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = $oldDotNetGenerateCertificate
    $env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = $oldDotNetAddToolsPath
    $env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = $oldDotNetWorkloadNotify
    foreach ($variableName in $blockedAmbientVariableNames) {
        [Environment]::SetEnvironmentVariable(
            $variableName,
            $savedBlockedAmbientVariables[$variableName],
            'Process')
    }
    $env:NUGET_CERT_REVOCATION_MODE = $oldNuGetCertRevocationMode
    $env:DOTNET_NUGET_SIGNATURE_VERIFICATION = $oldDotNetNuGetSignatureVerification
    $currentRuntimeEnvironmentNames = [Collections.Generic.HashSet[string]]::new(
        $runtimeEnvironmentNames,
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($environmentName in [Environment]::GetEnvironmentVariables('Process').Keys) {
        if ([string]$environmentName -match '^(?:COMPlus_|DOTNET_|CORECLR_|COR_)') {
            [void]$currentRuntimeEnvironmentNames.Add([string]$environmentName)
        }
    }
    foreach ($environmentName in $currentRuntimeEnvironmentNames) {
        [Environment]::SetEnvironmentVariable($environmentName, $null, 'Process')
    }
    foreach ($environmentName in $savedRuntimeEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable(
            $environmentName,
            $savedRuntimeEnvironment[$environmentName],
            'Process')
    }
    if (-not $isUnsigned -and $null -ne $signedEnvironmentSnapshot) {
        Set-ExactProcessEnvironment $signedEnvironmentSnapshot
    }
    $authorityConfigurationBase64 = $null
    $authorityIssuerSpkiBase64 = $null
    $contentAuthorityConfigurationBase64 = $null
    $contentAuthorityIssuerSpkiBase64 = $null
    if ($promotedFinalPathNeedsCleanup -and (Test-Path -LiteralPath $finalPath)) {
        $resolvedRejectedFinal = [IO.Path]::GetFullPath($finalPath)
        $outputPrefix = $outputParent.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        $expectedFinalLeaf = "Turborama-$version-win-x64-$shortCommit-$label"
        if ($resolvedRejectedFinal.StartsWith($outputPrefix, [StringComparison]::OrdinalIgnoreCase) -and
            (Split-Path -Leaf $resolvedRejectedFinal).Equals(
                $expectedFinalLeaf,
                [StringComparison]::Ordinal)) {
            Remove-Item -LiteralPath $resolvedRejectedFinal -Recurse -Force
            Write-Warning 'O pacote promovido reprovado foi removido integralmente.'
        }
        else {
            Write-Warning 'O pacote promovido reprovado nao passou pela validacao de seguranca para limpeza.'
        }
    }
    if (-not [string]::IsNullOrWhiteSpace([string]$partialFinalPath) -and
        (Test-Path -LiteralPath $partialFinalPath)) {
        $resolvedPartial = [IO.Path]::GetFullPath($partialFinalPath)
        $outputPrefix = $outputParent.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        $partialLeaf = Split-Path -Leaf $resolvedPartial
        if ($resolvedPartial.StartsWith($outputPrefix, [StringComparison]::OrdinalIgnoreCase) -and
            $partialLeaf.StartsWith('.Turborama-', [StringComparison]::Ordinal) -and
            $partialLeaf -match '\.partial-[0-9a-f]{32}$') {
            Remove-Item -LiteralPath $resolvedPartial -Recurse -Force
        }
        else {
            Write-Warning 'O staging parcial nao passou pela validacao de seguranca para limpeza.'
        }
    }
    $resolvedWork = [IO.Path]::GetFullPath($work)
    $expectedPrefix = $temporaryParent.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $canRemoveWork = $true
    if ($null -ne $sourceSnapshot) {
        $resolvedSnapshot = [IO.Path]::GetFullPath($sourceSnapshot)
        $workPrefix = $resolvedWork.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if (-not $resolvedSnapshot.StartsWith($workPrefix, [StringComparison]::OrdinalIgnoreCase) -or
            -not (Split-Path -Leaf $resolvedSnapshot).Equals(
                'source',
                [StringComparison]::OrdinalIgnoreCase)) {
            Write-Warning 'O caminho do snapshot remoto nao passou pela validacao de seguranca.'
            $canRemoveWork = $false
        }
    }
    if ($canRemoveWork -and
        $resolvedWork.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $resolvedWork).StartsWith('TurboramaRelease-', [StringComparison]::Ordinal) -and
        (Test-Path -LiteralPath $resolvedWork)) {
        Remove-Item -LiteralPath $resolvedWork -Recurse -Force
    }
}
}
finally {
    if ($null -ne $releaseGpgWrapperLease) {
        try { $releaseGpgWrapperLease.Dispose() }
        catch { Write-Warning 'Nao foi possivel liberar imediatamente o lease do wrapper GnuPG.' }
        $releaseGpgWrapperLease = $null
    }
    if ($null -ne $releaseTagPublicKeyLease) {
        try { $releaseTagPublicKeyLease.Dispose() }
        catch { Write-Warning 'Nao foi possivel liberar imediatamente o lease da chave publica.' }
        $releaseTagPublicKeyLease = $null
    }
    if (-not [string]::IsNullOrWhiteSpace([string]$releaseTrustRoot) -and
        (Test-Path -LiteralPath $releaseTrustRoot)) {
        $canRemoveReleaseTrust = $false
        try {
            $resolvedReleaseTrust = [IO.Path]::GetFullPath($releaseTrustRoot)
            $temporaryPrefix = $temporaryParent.TrimEnd(
                [IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
            Assert-NoReparseAncestry $resolvedReleaseTrust
            $releaseTrustLeaf = Split-Path -Leaf $resolvedReleaseTrust
            $releaseTrustReparseEntries = @(Get-ChildItem `
                -LiteralPath $resolvedReleaseTrust -Recurse -Force |
                Where-Object {
                    ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
                })
            $canRemoveReleaseTrust =
                $resolvedReleaseTrust.StartsWith(
                    $temporaryPrefix,
                    [StringComparison]::OrdinalIgnoreCase) -and
                $releaseTrustLeaf -match '^TurboramaReleaseTrust-[0-9a-f]{32}$' -and
                $releaseTrustReparseEntries.Count -eq 0
        }
        catch {
            Write-Warning 'O keyring GnuPG temporario nao passou pela validacao para limpeza.'
        }
        if ($canRemoveReleaseTrust) {
            try {
                Remove-Item -LiteralPath $resolvedReleaseTrust -Recurse -Force
            }
            catch {
                Write-Warning 'Nao foi possivel remover integralmente o keyring GnuPG temporario.'
            }
        }
        else {
            Write-Warning 'O keyring GnuPG temporario foi preservado porque o alvo de limpeza nao era seguro.'
        }
    }
    foreach ($environmentName in $gitEnvironmentNames) {
        [Environment]::SetEnvironmentVariable($environmentName, $null, 'Process')
    }
    foreach ($environmentName in $savedGitEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable(
            $environmentName,
            $savedGitEnvironment[$environmentName],
            'Process')
    }
    if (-not $isUnsigned -and $null -ne $signedEnvironmentSnapshot) {
        Set-ExactProcessEnvironment $signedEnvironmentSnapshot
    }
}
