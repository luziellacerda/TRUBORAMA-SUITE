[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),

    [string]$GitPath = 'git'
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$trustedGitConfiguration = @(
    '-c', 'core.fsmonitor=false',
    '-c', 'core.untrackedCache=false',
    '-c', 'core.hooksPath=NUL')

function Add-Failure([System.Collections.Generic.List[string]]$Failures, [string]$Message) {
    $Failures.Add($Message)
}

function Invoke-CapturedProcess(
    [Diagnostics.ProcessStartInfo]$StartInfo,
    [int]$TimeoutMilliseconds = 20000) {
    $process = [Diagnostics.Process]::Start($StartInfo)
    try {
        $standardOutput = $process.StandardOutput.ReadToEndAsync()
        $standardError = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutMilliseconds)) {
            $process.Kill($true)
            $process.WaitForExit()
            return [pscustomobject]@{
                TimedOut = $true
                ExitCode = -1
                Output = $standardOutput.GetAwaiter().GetResult() + "`n" +
                    $standardError.GetAwaiter().GetResult()
            }
        }
        return [pscustomobject]@{
            TimedOut = $false
            ExitCode = $process.ExitCode
            Output = $standardOutput.GetAwaiter().GetResult() + "`n" +
                $standardError.GetAwaiter().GetResult()
        }
    }
    finally {
        $process.Dispose()
    }
}

function Get-SourceTextFiles {
    $extensions = @(
        '.cs', '.csproj', '.props', '.targets', '.ps1', '.cmd', '.json',
        '.yml', '.yaml', '.xaml', '.xml', '.config', '.resx', '.txt')
    $paths = & $GitPath @trustedGitConfiguration -C $root `
        ls-files --cached --others --exclude-standard
    if ($LASTEXITCODE -ne 0) { throw 'Nao foi possivel enumerar os arquivos-fonte.' }
    foreach ($relative in $paths) {
        if ($extensions -contains [IO.Path]::GetExtension($relative).ToLowerInvariant()) {
            $candidate = Join-Path $root $relative
            if (Test-Path -LiteralPath $candidate -PathType Leaf) { $candidate }
        }
    }
}

$failures = [System.Collections.Generic.List[string]]::new()
$projectPath = Join-Path $root 'TurboBoxManager.csproj'
$catalogPath = Join-Path $root 'Assets\Catalog\catalog.json'
$globalJsonPath = Join-Path $root 'global.json'
$lockPath = Join-Path $root 'packages.lock.json'
$testLockPath = Join-Path $root 'tests\CatalogVerifier\packages.lock.json'
$nugetConfigPath = Join-Path $root 'NuGet.Config'
$buildPropsPath = Join-Path $root 'Directory.Build.props'
$buildProductionPath = Join-Path $root 'tools\Build-Production.ps1'

$sourceEntries = @(& $GitPath @trustedGitConfiguration -C $root `
    ls-files --cached --others --exclude-standard)
if ($LASTEXITCODE -ne 0) { throw 'Nao foi possivel enumerar o snapshot de Release.' }
foreach ($relativePath in $sourceEntries) {
    $sourcePath = Join-Path $root $relativePath
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) { continue }
    $sourceInfo = Get-Item -LiteralPath $sourcePath -Force
    if ($sourceInfo.Length -ge 100MB) {
        Add-Failure $failures "Arquivo excede o limite individual de 100 MiB: $relativePath"
    }
    if (($sourceInfo.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        Add-Failure $failures "Reparse point proibido no snapshot de Release: $relativePath"
    }
}
$gitIndexEntries = @(& $GitPath @trustedGitConfiguration -C $root ls-files --stage)
if ($LASTEXITCODE -ne 0) { throw 'Nao foi possivel inspecionar os modos do indice Git.' }
foreach ($indexEntry in $gitIndexEntries) {
    if ($indexEntry -match '^120000\s') {
        Add-Failure $failures "Link simbolico proibido no snapshot de Release: $indexEntry"
    }
}

try {
    $buildCommand = Get-Command -Name $buildProductionPath -CommandType ExternalScript `
        -ErrorAction Stop
    $authorityHashParameter = $buildCommand.Parameters['AuthorityConfigurationSha256']
    if ($null -eq $authorityHashParameter -or
        $authorityHashParameter.ParameterType -ne [string]) {
        Add-Failure $failures `
            'Build assinado nao exige AuthorityConfigurationSha256 textual.'
    }
    else {
        $parameterAttributes = @($authorityHashParameter.Attributes | Where-Object {
            $_ -is [Management.Automation.ParameterAttribute]
        })
        $signedBindings = @($parameterAttributes | Where-Object {
            $_.ParameterSetName -eq 'Signed' -and $_.Mandatory
        })
        $unsignedBindings = @($parameterAttributes | Where-Object {
            $_.ParameterSetName -eq 'Unsigned'
        })
        $hashPatterns = @($authorityHashParameter.Attributes | Where-Object {
            $_ -is [Management.Automation.ValidatePatternAttribute]
        })
        if ($signedBindings.Count -ne 1 -or
            $unsignedBindings.Count -ne 0 -or
            $hashPatterns.Count -ne 1 -or
            $hashPatterns[0].RegexPattern -ne '\A[0-9A-Fa-f]{64}\z') {
            Add-Failure $failures `
                'AuthorityConfigurationSha256 deve ser obrigatorio somente em Signed e aceitar exatamente 64 hex.'
        }
    }
}
catch {
    Add-Failure $failures `
        "Nao foi possivel validar os parameter sets do build: $($_.Exception.Message)"
}

try {
    $buildCommand = Get-Command -Name $buildProductionPath `
        -CommandType ExternalScript -ErrorAction Stop
    $contentAuthorityHashParameter =
        $buildCommand.Parameters['ContentAuthorityConfigurationSha256']
    if ($null -eq $contentAuthorityHashParameter -or
        $contentAuthorityHashParameter.ParameterType -ne [string]) {
        Add-Failure $failures `
            'Build assinado nao exige ContentAuthorityConfigurationSha256 textual.'
    }
    else {
        $parameterAttributes = @(
            $contentAuthorityHashParameter.Attributes | Where-Object {
                $_ -is [Management.Automation.ParameterAttribute]
            })
        $signedBindings = @($parameterAttributes | Where-Object {
            $_.ParameterSetName -eq 'Signed' -and $_.Mandatory
        })
        $unsignedBindings = @($parameterAttributes | Where-Object {
            $_.ParameterSetName -eq 'Unsigned'
        })
        $hashPatterns = @(
            $contentAuthorityHashParameter.Attributes | Where-Object {
                $_ -is [Management.Automation.ValidatePatternAttribute]
            })
        if ($signedBindings.Count -ne 1 -or
            $unsignedBindings.Count -ne 0 -or
            $hashPatterns.Count -ne 1 -or
            $hashPatterns[0].RegexPattern -ne '\A[0-9A-Fa-f]{64}\z') {
            Add-Failure $failures `
                'ContentAuthorityConfigurationSha256 deve ser obrigatorio somente em Signed e aceitar 64 hex.'
        }
    }
}
catch {
    Add-Failure $failures `
        "Nao foi possivel validar os parametros da autoridade de conteudo: $($_.Exception.Message)"
}

try {
    $buildCommand = Get-Command -Name $buildProductionPath -CommandType ExternalScript `
        -ErrorAction Stop
    foreach ($hashParameterName in @(
            'PowerShellSha256',
            'PowerShellHomeTreeSha256',
            'ReleaseTagPublicKeySha256')) {
        $hashParameter = $buildCommand.Parameters[$hashParameterName]
        $parameterAttributes = @(if ($null -ne $hashParameter) {
            $hashParameter.Attributes | Where-Object {
                $_ -is [Management.Automation.ParameterAttribute]
            }
        })
        $signedBindings = @($parameterAttributes | Where-Object {
            $_.ParameterSetName -eq 'Signed' -and $_.Mandatory
        })
        $unsignedBindings = @($parameterAttributes | Where-Object {
            $_.ParameterSetName -eq 'Unsigned'
        })
        $hashPatterns = @(if ($null -ne $hashParameter) {
            $hashParameter.Attributes | Where-Object {
                $_ -is [Management.Automation.ValidatePatternAttribute]
            }
        })
        if ($null -eq $hashParameter -or
            $hashParameter.ParameterType -ne [string] -or
            $signedBindings.Count -ne 1 -or
            $unsignedBindings.Count -ne 0 -or
            $hashPatterns.Count -ne 1 -or
            $hashPatterns[0].RegexPattern -ne '\A[0-9A-Fa-f]{64}\z') {
            Add-Failure $failures `
                "$hashParameterName deve ser obrigatorio somente em Signed e aceitar exatamente 64 hex."
        }
    }

    $signToolPathParameter = $buildCommand.Parameters['SignToolPath']
    $signToolPathAttributes = @(if ($null -ne $signToolPathParameter) {
        $signToolPathParameter.Attributes | Where-Object {
            $_ -is [Management.Automation.ParameterAttribute]
        }
    })
    if ($null -eq $signToolPathParameter -or
        $signToolPathParameter.ParameterType -ne [string] -or
        @($signToolPathAttributes | Where-Object {
            $_.ParameterSetName -eq 'Signed' -and $_.Mandatory
        }).Count -ne 1 -or
        @($signToolPathAttributes | Where-Object {
            $_.ParameterSetName -eq 'Unsigned'
        }).Count -ne 0) {
        Add-Failure $failures `
            'SignToolPath absoluto deve ser obrigatorio somente no modo Signed.'
    }

    $buildAst = $buildCommand.ScriptBlock.Ast
    $importCommand = @($buildAst.FindAll({
        param($node)
        $node -is [Management.Automation.Language.CommandAst] -and
        $node.GetCommandName() -eq 'Microsoft.PowerShell.Core\Import-Module'
    }, $true) | Sort-Object { $_.Extent.StartOffset } | Select-Object -First 1)
    if ($importCommand.Count -ne 1) {
        Add-Failure $failures `
            'Bootstrap Signed nao importa modulos oficiais por caminho qualificado.'
    }
    else {
        $commandsBeforeTrustedImport = @($buildAst.FindAll({
            param($node)
            $node -is [Management.Automation.Language.CommandAst] -and
            $node.Extent.StartOffset -lt $importCommand[0].Extent.StartOffset -and
            $null -ne $node.GetCommandName()
        }, $true))
        if ($commandsBeforeTrustedImport.Count -ne 0) {
            Add-Failure $failures (
                'Fase zero do bootstrap executa comando antes de atestar PSHOME: ' +
                (($commandsBeforeTrustedImport | ForEach-Object {
                    $_.GetCommandName()
                }) -join ', '))
        }
    }

    $buildSource = [IO.File]::ReadAllText($buildProductionPath)
    foreach ($requiredBootstrapFragment in @(
            '[System.Environment]::ProcessPath',
            '[System.Environment]::GetCommandLineArgs()',
            "'-NoProfile'",
            "'-NonInteractive'",
            "'-File'",
            "'-ReleaseTagPublicKeySha256'",
            '$PSModuleAutoloadingPreference = ''None''',
            '$PSDefaultParameterValues = @{}',
            'Set-ExactProcessEnvironment',
            'Assert-NoMsBuildPropertyMetacharacters',
            'Assert-ReleaseTagGpgStatus',
            'New-GpgReleaseWrapperBytes',
            '#!/usr/bin/sh',
            'BADSIG|ERRSIG|EXPSIG|EXPKEYSIG|REVKEYSIG|KEYREVOKED|NO_PUBKEY',
            'Microsoft.PowerShell.Security\Get-AuthenticodeSignature',
            'Microsoft.PowerShell.Management\Copy-Item',
            'TURBORAMA-DIRECTORY-TREE-SHA256-V1')) {
        if (-not $buildSource.Contains(
                $requiredBootstrapFragment,
                [StringComparison]::Ordinal)) {
            Add-Failure $failures `
                "Bootstrap Signed perdeu requisito critico: $requiredBootstrapFragment"
        }
    }
    if ($buildSource -match '(?m)^\s*(?:Get-FileHash|Get-AuthenticodeSignature|Copy-Item)\b') {
        Add-Failure $failures `
            'Operacao critica voltou a depender de comando nao qualificado/sombreavel.'
    }
    $signedEnvironmentEntryOffset = $buildSource.IndexOf(
        'Set-ExactProcessEnvironment $signedEnvironmentBase',
        [StringComparison]::Ordinal)
    $firstProvenanceGitOffset = $buildSource.IndexOf(
        '$commit = (& $GitPath',
        [StringComparison]::Ordinal)
    if ($signedEnvironmentEntryOffset -lt 0 -or
        $firstProvenanceGitOffset -lt 0 -or
        $signedEnvironmentEntryOffset -ge $firstProvenanceGitOffset -or
        $buildSource.Contains(
            'gpg-release-verify.cmd',
            [StringComparison]::Ordinal) -or
        -not $buildSource.Contains(
            "'usr', 'bin'",
            [StringComparison]::Ordinal) -or
        -not $buildSource.Contains(
            '$shPath = Join-Path $gitInstallationRoot',
            [StringComparison]::Ordinal)) {
        Add-Failure $failures `
            'Git/GPG precisa rodar depois da allowlist exata, com sh coberto pela arvore e wrapper shebang.'
    }
    if (@([regex]::Matches(
            $buildSource,
            '&\s+\$assertPinnedDirectoryTree\b')).Count -lt 12) {
        Add-Failure $failures `
            'Pins de arvore nao sao revalidados antes e depois das fases de toolchain.'
    }
    if (@([regex]::Matches(
            $buildSource,
            '\$buildRoot\s*=\s*\$sourceSnapshot\b')).Count -lt 2 -or
        -not $buildSource.Contains(
            'Copy-BclRegularDirectorySnapshot',
            [StringComparison]::Ordinal) -or
        -not $buildSource.Contains(
            'ls-files --cached --others --exclude-standard',
            [StringComparison]::Ordinal)) {
        Add-Failure $failures `
            'Signed e Unsigned devem restaurar/compilar somente em snapshots isolados de work.'
    }
    foreach ($toolchainGatePath in @(
            (Join-Path $root 'tools\New-ReleaseManifest.ps1'),
            (Join-Path $root 'tools\Test-PublishedPackage.ps1'))) {
        $toolchainGateSource = [IO.File]::ReadAllText($toolchainGatePath)
        foreach ($toolchainField in @(
                'powerShellExecutableSha256',
                'powerShellHomeTreeSha256',
                'gitExecutableSha256',
                'gitTreeSha256',
                'dotnetSdkTreeSha256',
                'signToolExecutableSha256',
                'releaseTagPublicKeySha256')) {
            if (-not $toolchainGateSource.Contains(
                    $toolchainField,
                    [StringComparison]::Ordinal)) {
                Add-Failure $failures `
                    "Manifesto/gate perdeu pin de toolchain: $toolchainField em $toolchainGatePath"
            }
        }
    }

    $fileHasherAssignment = @($buildAst.FindAll({
        param($node)
        $node -is [Management.Automation.Language.AssignmentStatementAst] -and
        $node.Left -is [Management.Automation.Language.VariableExpressionAst] -and
        $node.Left.VariablePath.UserPath -eq 'getBclFileSha256'
    }, $true) | Select-Object -First 1)
    $treeHasherAssignment = @($buildAst.FindAll({
        param($node)
        $node -is [Management.Automation.Language.AssignmentStatementAst] -and
        $node.Left -is [Management.Automation.Language.VariableExpressionAst] -and
        $node.Left.VariablePath.UserPath -eq 'getBclDirectoryTreeSha256'
    }, $true) | Select-Object -First 1)
    if ($fileHasherAssignment.Count -ne 1 -or $treeHasherAssignment.Count -ne 1) {
        Add-Failure $failures 'Nao foi possivel localizar os hashers BCL do bootstrap.'
    }
    else {
        $fileHasherFactory = [scriptblock]::Create(
            $fileHasherAssignment[0].Right.Extent.Text)
        $treeHasherFactory = [scriptblock]::Create(
            $treeHasherAssignment[0].Right.Extent.Text)
        $runtimeFileHasher = & $fileHasherFactory
        $runtimeTreeHasher = & $treeHasherFactory
        $mutationRoot = Join-Path ([IO.Path]::GetTempPath()) (
            'TurboramaTreeMutation-' + [Guid]::NewGuid().ToString('N'))
        try {
            [IO.Directory]::CreateDirectory($mutationRoot) > $null
            $mutationFile = Join-Path $mutationRoot 'already-read.bin'
            [IO.File]::WriteAllBytes($mutationFile, [byte[]](1, 2, 3, 4, 5, 6, 7, 8))
            [byte[]]$beforeMutation = & $runtimeTreeHasher `
                $mutationRoot $runtimeFileHasher
            # Same path and length: only content identity distinguishes the swap.
            [IO.File]::WriteAllBytes($mutationFile, [byte[]](8, 7, 6, 5, 4, 3, 2, 1))
            [byte[]]$afterMutation = & $runtimeTreeHasher `
                $mutationRoot $runtimeFileHasher
            try {
                if ([Security.Cryptography.CryptographicOperations]::FixedTimeEquals(
                        $beforeMutation,
                        $afterMutation)) {
                    Add-Failure $failures `
                        'Rehash de arvore nao detectou troca de arquivo ja lido com mesmo tamanho.'
                }
            }
            finally {
                [Security.Cryptography.CryptographicOperations]::ZeroMemory(
                    $beforeMutation)
                [Security.Cryptography.CryptographicOperations]::ZeroMemory(
                    $afterMutation)
            }
        }
        finally {
            if ([IO.Directory]::Exists($mutationRoot)) {
                [IO.Directory]::Delete($mutationRoot, $true)
            }
        }
    }

    $environmentFunctionAst = @($buildAst.FindAll({
        param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq 'Set-ExactProcessEnvironment'
    }, $true))
    if ($environmentFunctionAst.Count -ne 1) {
        Add-Failure $failures `
            'Nao foi possivel localizar a limpeza exata do ambiente Signed.'
    }
    else {
        $environmentProbe = [scriptblock]::Create(
            $environmentFunctionAst[0].Extent.Text +
            "`nSet-ExactProcessEnvironment `$args[0]")
        $environmentBeforeProbe = @{}
        foreach ($environmentName in [Environment]::GetEnvironmentVariables('Process').Keys) {
            $environmentBeforeProbe[[string]$environmentName] =
                [Environment]::GetEnvironmentVariable([string]$environmentName, 'Process')
        }
        try {
            foreach ($hostileEnvironmentName in @(
                    'AlternateCommonProps',
                    'AfterMicrosoftNetSdkProps',
                    'AfterMicrosoftNETSdkTargets',
                    'NUGET_FALLBACK_PACKAGES',
                    'RestoreFallbackFolders',
                    'RestoreAdditionalProjectFallbackFolders',
                    'BaseIntermediateOutputPath',
                    'IntermediateOutputPath',
                    'LanguageTargets')) {
                [Environment]::SetEnvironmentVariable(
                    $hostileEnvironmentName,
                    'C:\hostile\import.targets',
                    'Process')
            }
            $environmentAllowlistProbe = [ordered]@{
                TURBORAMA_EXACT_ENVIRONMENT_PROBE = 'trusted'
            }
            & $environmentProbe $environmentAllowlistProbe
            $probeEnvironment = [Environment]::GetEnvironmentVariables('Process')
            if ($probeEnvironment.Count -ne 1 -or
                [string]$probeEnvironment['TURBORAMA_EXACT_ENVIRONMENT_PROBE'] -ne 'trusted') {
                Add-Failure $failures `
                    'A allowlist Signed preservou variavel MSBuild/NuGet nao autorizada.'
            }
        }
        finally {
            & $environmentProbe $environmentBeforeProbe
        }
    }

    $gpgStatusFunctionAst = @($buildAst.FindAll({
        param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq 'Assert-ReleaseTagGpgStatus'
    }, $true))
    if ($gpgStatusFunctionAst.Count -ne 1) {
        Add-Failure $failures 'Nao foi possivel localizar o parser de status GPG.'
    }
    else {
        $gpgStatusProbe = [scriptblock]::Create(
            $gpgStatusFunctionAst[0].Extent.Text +
            "`nAssert-ReleaseTagGpgStatus @args")
        $probeSigningFingerprint = '1111111111111111111111111111111111111111'
        $probePrimaryFingerprint = '2222222222222222222222222222222222222222'
        $validGpgStatus = @(
            '[GNUPG:] GOODSIG 1111111111111111 Release Probe',
            "[GNUPG:] VALIDSIG $probeSigningFingerprint 20260827 0 0 4 0 1 10 00 $probePrimaryFingerprint")
        try {
            & $gpgStatusProbe `
                $validGpgStatus $probeSigningFingerprint $probePrimaryFingerprint
        }
        catch {
            Add-Failure $failures `
                "O parser GPG rejeitou GOODSIG+VALIDSIG canônicos: $($_.Exception.Message)"
        }
        foreach ($negativeGpgStatus in @(
                'BADSIG', 'ERRSIG', 'EXPSIG', 'EXPKEYSIG', 'REVKEYSIG',
                'KEYREVOKED', 'NO_PUBKEY', 'KEYEXPIRED', 'SIGEXPIRED',
                'FAILURE', 'NODATA')) {
            $negativeStatusWasRejected = $false
            try {
                & $gpgStatusProbe `
                    ($validGpgStatus + "[GNUPG:] $negativeGpgStatus 1111111111111111") `
                    $probeSigningFingerprint `
                    $probePrimaryFingerprint
            }
            catch {
                $negativeStatusWasRejected = $true
            }
            if (-not $negativeStatusWasRejected) {
                Add-Failure $failures `
                    "O parser GPG aceitou status negativo: $negativeGpgStatus"
            }
        }
        $invalidGpgStatusSets = [Collections.Generic.List[object]]::new()
        [void]$invalidGpgStatusSets.Add([string[]]@(
            $validGpgStatus | Where-Object { $_ -notmatch 'GOODSIG' }))
        [void]$invalidGpgStatusSets.Add([string[]]@(
            $validGpgStatus + $validGpgStatus[0]))
        [void]$invalidGpgStatusSets.Add([string[]]@(
            $validGpgStatus + $validGpgStatus[1]))
        [void]$invalidGpgStatusSets.Add([string[]]@(
            $validGpgStatus + '[GNUPG:] GOODSIG'))
        [void]$invalidGpgStatusSets.Add([string[]]@(
            $validGpgStatus + '[GNUPG:] VALIDSIG malformed'))
        foreach ($invalidGpgStatus in $invalidGpgStatusSets) {
            $invalidStatusWasRejected = $false
            try {
                & $gpgStatusProbe `
                    $invalidGpgStatus $probeSigningFingerprint $probePrimaryFingerprint
            }
            catch {
                $invalidStatusWasRejected = $true
            }
            if (-not $invalidStatusWasRejected) {
                Add-Failure $failures `
                    'O parser GPG aceitou GOODSIG/VALIDSIG ausente ou duplicado.'
            }
        }
    }

    $gpgWrapperFunctionAst = @($buildAst.FindAll({
        param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq 'New-GpgReleaseWrapperBytes'
    }, $true))
    if ($gpgWrapperFunctionAst.Count -ne 1) {
        Add-Failure $failures 'Nao foi possivel localizar o gerador do wrapper GPG.'
    }
    else {
        $gitExecutable = [IO.Path]::GetFullPath($GitPath)
        $gitInstallationRoot = [IO.Path]::GetDirectoryName(
            [IO.Path]::GetDirectoryName($gitExecutable))
        $probeGpgPath = Join-Path $gitInstallationRoot 'usr\bin\gpg.exe'
        $probeShPath = Join-Path $gitInstallationRoot 'usr\bin\sh.exe'
        if (-not [IO.File]::Exists($probeGpgPath) -or
            -not [IO.File]::Exists($probeShPath)) {
            Add-Failure $failures `
                'Git for Windows nao contem gpg.exe/sh.exe para o probe do wrapper.'
        }
        else {
            $gpgWrapperProbe = [scriptblock]::Create(
                $gpgWrapperFunctionAst[0].Extent.Text +
                "`nNew-GpgReleaseWrapperBytes @args")
            $wrapperFixtureRoot = Join-Path ([IO.Path]::GetTempPath()) (
                'TurboramaGpgWrapperProbe-' + [Guid]::NewGuid().ToString('N'))
            $wrapperFixtureRepository = Join-Path $wrapperFixtureRoot 'repository'
            $wrapperFixtureHome = Join-Path $wrapperFixtureRoot 'gnupg'
            $wrapperFixturePath = Join-Path $wrapperFixtureRoot 'gpg-verify-probe.sh'
            $savedShellEnvironment = @{}
            foreach ($shellEnvironmentName in @(
                    'PATH', 'BASH_ENV', 'ENV', 'CDPATH', 'GLOBIGNORE',
                    'SHELLOPTS', 'BASHOPTS')) {
                $savedShellEnvironment[$shellEnvironmentName] =
                    [Environment]::GetEnvironmentVariable(
                        $shellEnvironmentName,
                        'Process')
            }
            try {
                [IO.Directory]::CreateDirectory($wrapperFixtureRoot) > $null
                [IO.Directory]::CreateDirectory($wrapperFixtureHome) > $null
                [byte[]]$wrapperFixtureBytes = @(& $gpgWrapperProbe `
                    $probeGpgPath $wrapperFixtureHome)
                $wrapperFixtureText = [Text.UTF8Encoding]::new(
                    $false,
                    $true).GetString($wrapperFixtureBytes)
                if (-not $wrapperFixtureText.StartsWith(
                        "#!/usr/bin/sh`n",
                        [StringComparison]::Ordinal) -or
                    $wrapperFixtureText.Contains("`r", [StringComparison]::Ordinal) -or
                    -not $wrapperFixtureText.EndsWith("`"`$@`"`n", [StringComparison]::Ordinal)) {
                    Add-Failure $failures `
                        'O wrapper GPG nao usa shebang/LF/forwarding POSIX canonicos.'
                }
                [IO.File]::WriteAllBytes($wrapperFixturePath, $wrapperFixtureBytes)
                [Security.Cryptography.CryptographicOperations]::ZeroMemory(
                    $wrapperFixtureBytes)

                & $gitExecutable @trustedGitConfiguration init --quiet `
                    $wrapperFixtureRepository
                if ($LASTEXITCODE -ne 0) { throw 'git init falhou no probe do wrapper.' }
                [IO.File]::WriteAllText(
                    (Join-Path $wrapperFixtureRepository 'probe.txt'),
                    'probe',
                    [Text.UTF8Encoding]::new($false))
                & $gitExecutable @trustedGitConfiguration `
                    -C $wrapperFixtureRepository add -- probe.txt
                if ($LASTEXITCODE -ne 0) { throw 'git add falhou no probe do wrapper.' }
                & $gitExecutable @trustedGitConfiguration `
                    -c 'user.name=Turborama Probe' `
                    -c 'user.email=probe@example.invalid' `
                    -C $wrapperFixtureRepository commit --quiet -m probe
                if ($LASTEXITCODE -ne 0) { throw 'git commit falhou no probe do wrapper.' }
                $fixtureCommit = (& $gitExecutable @trustedGitConfiguration `
                    -C $wrapperFixtureRepository rev-parse HEAD).Trim()
                if ($LASTEXITCODE -ne 0 -or $fixtureCommit -notmatch '^[0-9a-f]{40}$') {
                    throw 'Commit invalido no probe do wrapper.'
                }
                $fixtureTagText =
                    "object $fixtureCommit`n" +
                    "type commit`n" +
                    "tag wrapper-probe`n" +
                    "tagger Turborama Probe <probe@example.invalid> 1787827200 +0000`n" +
                    "`nwrapper probe`n" +
                    "-----BEGIN PGP SIGNATURE-----`n" +
                    "invalid`n" +
                    "-----END PGP SIGNATURE-----`n"
                $fixtureTagPath = Join-Path $wrapperFixtureRoot 'tag-object.txt'
                [IO.File]::WriteAllText(
                    $fixtureTagPath,
                    $fixtureTagText,
                    [Text.UTF8Encoding]::new($false))
                $fixtureTagObject = (& $gitExecutable @trustedGitConfiguration `
                    -C $wrapperFixtureRepository hash-object -t tag -w `
                    $fixtureTagPath).Trim()
                if ($LASTEXITCODE -ne 0 -or $fixtureTagObject -notmatch '^[0-9a-f]{40}$') {
                    throw 'Objeto de tag invalido no probe do wrapper.'
                }
                & $gitExecutable @trustedGitConfiguration `
                    -C $wrapperFixtureRepository update-ref `
                    refs/tags/wrapper-probe $fixtureTagObject
                if ($LASTEXITCODE -ne 0) { throw 'update-ref falhou no probe do wrapper.' }

                $env:PATH = (Join-Path $gitInstallationRoot 'usr\bin') + ';' +
                    (Join-Path $gitInstallationRoot 'mingw64\bin') + ';' +
                    (Join-Path $gitInstallationRoot 'cmd') + ';' +
                    [Environment]::SystemDirectory
                $env:BASH_ENV = $null
                $env:ENV = $null
                $env:CDPATH = $null
                $env:GLOBIGNORE = $null
                $env:SHELLOPTS = $null
                $env:BASHOPTS = $null
                $wrapperVerificationOutput = @(& $gitExecutable `
                    @trustedGitConfiguration `
                    -c "gpg.program=$wrapperFixturePath" `
                    -c 'gpg.format=openpgp' `
                    -C $wrapperFixtureRepository `
                    verify-tag --raw wrapper-probe 2>&1 |
                    ForEach-Object { [string]$_ })
                $wrapperVerificationExitCode = $LASTEXITCODE
                $wrapperVerificationText = $wrapperVerificationOutput -join "`n"
                if ($wrapperVerificationExitCode -eq 0 -or
                    $wrapperVerificationText -notmatch '(?m)^\[GNUPG:\]\s+(?:NODATA|FAILURE)(?:\s|$)' -or
                    $wrapperVerificationText -match '(?i)cannot (?:run|spawn)|bad exe|not a valid win32') {
                    Add-Failure $failures `
                        "Git nao executou funcionalmente o wrapper shebang GPG: $wrapperVerificationText"
                }
            }
            catch {
                Add-Failure $failures `
                    "Probe funcional do wrapper GPG falhou: $($_.Exception.Message)"
            }
            finally {
                foreach ($shellEnvironmentName in $savedShellEnvironment.Keys) {
                    $restoredShellEnvironmentValue =
                        $savedShellEnvironment[$shellEnvironmentName]
                    [Environment]::SetEnvironmentVariable(
                        $shellEnvironmentName,
                        $(if ($null -eq $restoredShellEnvironmentValue) {
                            [Management.Automation.Language.NullString]::Value
                        } else { [string]$restoredShellEnvironmentValue }),
                        [EnvironmentVariableTarget]::Process)
                }
                if ([IO.Directory]::Exists($wrapperFixtureRoot)) {
                    $resolvedWrapperFixtureRoot = [IO.Path]::GetFullPath(
                        $wrapperFixtureRoot)
                    $temporaryPrefix = [IO.Path]::GetFullPath(
                        [IO.Path]::GetTempPath()).TrimEnd(
                            [IO.Path]::DirectorySeparatorChar) +
                        [IO.Path]::DirectorySeparatorChar
                    if (-not $resolvedWrapperFixtureRoot.StartsWith(
                            $temporaryPrefix,
                            [StringComparison]::OrdinalIgnoreCase) -or
                        [IO.Path]::GetFileName($resolvedWrapperFixtureRoot) -notmatch
                            '^TurboramaGpgWrapperProbe-[0-9a-f]{32}$') {
                        throw 'O fixture GPG nao passou pela validacao de limpeza.'
                    }
                    foreach ($fixtureFile in [IO.Directory]::EnumerateFiles(
                            $resolvedWrapperFixtureRoot,
                            '*',
                            [IO.SearchOption]::AllDirectories)) {
                        [IO.File]::SetAttributes($fixtureFile, [IO.FileAttributes]::Normal)
                    }
                    [IO.Directory]::Delete($resolvedWrapperFixtureRoot, $true)
                }
            }
        }
    }

    $currentPowerShellPath = [Environment]::ProcessPath
    if (-not [string]::IsNullOrWhiteSpace($currentPowerShellPath) -and
        [IO.Path]::GetFileName($currentPowerShellPath).Equals(
            'pwsh.exe',
            [StringComparison]::OrdinalIgnoreCase)) {
        $shadowRoot = Join-Path ([IO.Path]::GetTempPath()) (
            'TurboramaBootstrapShadow-' + [Guid]::NewGuid().ToString('N'))
        $shadowMarker = Join-Path $shadowRoot 'shadow-command-ran.txt'
        $shadowWrapper = Join-Path $shadowRoot 'contaminated-profile-wrapper.ps1'
        try {
            [IO.Directory]::CreateDirectory($shadowRoot) > $null
            $escapedMarker = $shadowMarker.Replace("'", "''")
            $escapedBuildPath = $buildProductionPath.Replace("'", "''")
            $shadowWrapperText = @"
`$ErrorActionPreference = 'Stop'
function global:Get-FileHash { [IO.File]::WriteAllText('$escapedMarker', 'Get-FileHash'); throw 'shadow' }
function global:Get-AuthenticodeSignature { [IO.File]::WriteAllText('$escapedMarker', 'Get-AuthenticodeSignature'); throw 'shadow' }
function global:Copy-Item { [IO.File]::WriteAllText('$escapedMarker', 'Copy-Item'); throw 'shadow' }
function global:Invoke-Checked { [IO.File]::WriteAllText('$escapedMarker', 'Invoke-Checked'); throw 'shadow' }
try { & '$escapedBuildPath' @args; exit 0 }
catch { [Console]::Error.WriteLine(`$_.Exception.Message); exit 73 }
"@
            [IO.File]::WriteAllText(
                $shadowWrapper,
                $shadowWrapperText,
                [Text.UTF8Encoding]::new($false))

            $zero40 = '0' * 40
            $zero64 = '0' * 64
            $processStart = [Diagnostics.ProcessStartInfo]::new()
            $processStart.FileName = [IO.Path]::GetFullPath($currentPowerShellPath)
            $processStart.UseShellExecute = $false
            $processStart.CreateNoWindow = $true
            $processStart.RedirectStandardOutput = $true
            $processStart.RedirectStandardError = $true
            $canonicalSignedProbeArguments = @(
                    '-CertificateThumbprint', $zero40,
                    '-TimestampUrl', 'https://timestamp.invalid',
                    '-ReleaseTagSignerFingerprint', $zero40,
                    '-ReleaseTagPrimaryKeyFingerprint', $zero40,
                    '-ReleaseTagPublicKeyPath', $shadowWrapper,
                    '-ReleaseTagPublicKeySha256', $zero64,
                    '-TimestampCertificateThumbprint', $zero40,
                    '-AuthorityConfigurationPath', $shadowWrapper,
                    '-AuthorityConfigurationSha256', $zero64,
                    '-AuthorityIssuerSpkiPath', $shadowWrapper,
                    '-AuthorityIssuerSpkiSha256', $zero64,
                    '-ContentAuthorityConfigurationPath', $shadowWrapper,
                    '-ContentAuthorityConfigurationSha256', $zero64,
                    '-ContentAuthorityIssuerSpkiPath', $shadowWrapper,
                    '-ContentAuthorityIssuerSpkiSha256', $zero64,
                    '-OutputRoot', (Join-Path $shadowRoot 'out'),
                    '-DotNetPath', $currentPowerShellPath,
                    '-GitPath', $currentPowerShellPath,
                    '-GitSha256', $zero64,
                    '-GitTreeSha256', $zero64,
                    '-GpgSha256', $zero64,
                    '-DotNetSdkTreeSha256', $zero64,
                    '-SignToolSha256', $zero64,
                    '-SignToolPath', $currentPowerShellPath,
                    '-PowerShellSha256', $zero64,
                    '-PowerShellHomeTreeSha256', $zero64)
            foreach ($argument in (@(
                    '-NoProfile', '-NonInteractive', '-File', $shadowWrapper) +
                    $canonicalSignedProbeArguments)) {
                [void]$processStart.ArgumentList.Add([string]$argument)
            }
            $shadowProcess = [Diagnostics.Process]::Start($processStart)
            try {
                $standardOutput = $shadowProcess.StandardOutput.ReadToEndAsync()
                $standardError = $shadowProcess.StandardError.ReadToEndAsync()
                if (-not $shadowProcess.WaitForExit(20000)) {
                    $shadowProcess.Kill($true)
                    Add-Failure $failures `
                        'Regressao adversarial do bootstrap excedeu 20 segundos.'
                }
                else {
                    $shadowOutput = $standardOutput.GetAwaiter().GetResult() + "`n" +
                        $standardError.GetAwaiter().GetResult()
                    if ($shadowProcess.ExitCode -eq 0 -or
                        -not $shadowOutput.Contains(
                            'BOOTSTRAP_POWERSHELL_INVOCATION_INVALID',
                            [StringComparison]::Ordinal) -or
                        [IO.File]::Exists($shadowMarker)) {
                        Add-Failure $failures `
                            'Runspace contaminado por perfil/shadows nao foi bloqueado antes de comandos criticos.'
                    }
                }
            }
            finally {
                $shadowProcess.Dispose()
            }

            $canonicalProcessStart = [Diagnostics.ProcessStartInfo]::new()
            $canonicalProcessStart.FileName = [IO.Path]::GetFullPath(
                $currentPowerShellPath)
            $canonicalProcessStart.UseShellExecute = $false
            $canonicalProcessStart.CreateNoWindow = $true
            $canonicalProcessStart.RedirectStandardOutput = $true
            $canonicalProcessStart.RedirectStandardError = $true
            foreach ($argument in (@(
                    '-NoLogo', '-NoProfile', '-NonInteractive', '-File',
                    $buildProductionPath) + $canonicalSignedProbeArguments)) {
                [void]$canonicalProcessStart.ArgumentList.Add([string]$argument)
            }
            $canonicalResult = Invoke-CapturedProcess $canonicalProcessStart
            if ($canonicalResult.TimedOut -or
                $canonicalResult.ExitCode -eq 0 -or
                -not $canonicalResult.Output.Contains(
                    'BOOTSTRAP_POWERSHELL_HASH_MISMATCH',
                    [StringComparison]::Ordinal) -or
                $canonicalResult.Output.Contains(
                    'BOOTSTRAP_POWERSHELL_INVOCATION_INVALID',
                    [StringComparison]::Ordinal)) {
                Add-Failure $failures `
                    'A invocacao Signed canonica nao alcancou exclusivamente o primeiro pin BCL.'
            }

            $quotedProbeArguments = [Collections.Generic.List[string]]::new()
            for ($probeArgumentIndex = 0;
                 $probeArgumentIndex -lt $canonicalSignedProbeArguments.Count;
                 $probeArgumentIndex += 2) {
                [void]$quotedProbeArguments.Add(
                    [string]$canonicalSignedProbeArguments[$probeArgumentIndex])
                [void]$quotedProbeArguments.Add(
                    "'" +
                    ([string]$canonicalSignedProbeArguments[$probeArgumentIndex + 1]).Replace(
                        "'", "''") +
                    "'")
            }
            $fakeCommandText =
                "function global:Get-FileHash { throw 'shadow' }; " +
                "try { & '$escapedBuildPath' " +
                ($quotedProbeArguments -join ' ') +
                " } catch { [Console]::Error.WriteLine(`$_.Exception.Message); exit 73 }; #"
            $fakeFileProcessStart = [Diagnostics.ProcessStartInfo]::new()
            $fakeFileProcessStart.FileName = [IO.Path]::GetFullPath(
                $currentPowerShellPath)
            $fakeFileProcessStart.UseShellExecute = $false
            $fakeFileProcessStart.CreateNoWindow = $true
            $fakeFileProcessStart.RedirectStandardOutput = $true
            $fakeFileProcessStart.RedirectStandardError = $true
            foreach ($argument in @(
                    '-NoProfile', '-NonInteractive', '-Command', $fakeCommandText,
                    '-File', $buildProductionPath)) {
                [void]$fakeFileProcessStart.ArgumentList.Add([string]$argument)
            }
            $fakeFileResult = Invoke-CapturedProcess $fakeFileProcessStart
            if ($fakeFileResult.TimedOut -or
                $fakeFileResult.ExitCode -eq 0 -or
                -not $fakeFileResult.Output.Contains(
                    'BOOTSTRAP_POWERSHELL_INVOCATION_INVALID',
                    [StringComparison]::Ordinal) -or
                $fakeFileResult.Output.Contains(
                    'BOOTSTRAP_POWERSHELL_HASH_MISMATCH',
                    [StringComparison]::Ordinal)) {
                Add-Failure $failures `
                    ('A combinacao adversarial -Command ... -File atravessou a gramatica Signed. Saida: ' +
                     $fakeFileResult.Output.Trim())
            }

            $nonCanonicalArgumentSets = @(
                ,(@('-NoP', '-NonInteractive', '-File', $buildProductionPath) +
                    $canonicalSignedProbeArguments),
                ,(@('-NoProfile', '-NonInteractive', '-File', $buildProductionPath,
                    '-Cert', $zero40) + $canonicalSignedProbeArguments[2..($canonicalSignedProbeArguments.Count - 1)]),
                ,(@('-NoProfile', '-NonInteractive', '-File', $buildProductionPath,
                    "-CertificateThumbprint:$zero40") + $canonicalSignedProbeArguments[2..($canonicalSignedProbeArguments.Count - 1)]),
                ,(@('-NoProfile', '-NonInteractive', '-File', $buildProductionPath) +
                    $canonicalSignedProbeArguments + @('-CertificateThumbprint', $zero40)),
                ,(@('-NoProfile', '-NonInteractive', '-File', $buildProductionPath) +
                    $canonicalSignedProbeArguments + @('-Verbose')))
            foreach ($nonCanonicalArguments in $nonCanonicalArgumentSets) {
                $nonCanonicalProcessStart = [Diagnostics.ProcessStartInfo]::new()
                $nonCanonicalProcessStart.FileName = [IO.Path]::GetFullPath(
                    $currentPowerShellPath)
                $nonCanonicalProcessStart.UseShellExecute = $false
                $nonCanonicalProcessStart.CreateNoWindow = $true
                $nonCanonicalProcessStart.RedirectStandardOutput = $true
                $nonCanonicalProcessStart.RedirectStandardError = $true
                foreach ($argument in $nonCanonicalArguments) {
                    [void]$nonCanonicalProcessStart.ArgumentList.Add([string]$argument)
                }
                $nonCanonicalResult = Invoke-CapturedProcess $nonCanonicalProcessStart
                if ($nonCanonicalResult.TimedOut -or
                    $nonCanonicalResult.ExitCode -eq 0 -or
                    $nonCanonicalResult.Output.Contains(
                        'BOOTSTRAP_POWERSHELL_HASH_MISMATCH',
                        [StringComparison]::Ordinal)) {
                    Add-Failure $failures `
                        'Abreviacao, Name:value, duplicata ou common parameter atravessou a gramatica Signed.'
                }
            }
        }
        finally {
            if ([IO.Directory]::Exists($shadowRoot)) {
                [IO.Directory]::Delete($shadowRoot, $true)
            }
        }
    }
}
catch {
    Add-Failure $failures `
        "Nao foi possivel validar o bootstrap PowerShell: $($_.Exception.Message) em $($_.ScriptStackTrace)"
}

if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
    Add-Failure $failures 'TurboBoxManager.csproj ausente.'
}
else {
    [xml]$project = Get-Content -LiteralPath $projectPath -Raw
    $targetFramework = [string]$project.Project.PropertyGroup.TargetFramework | Select-Object -First 1
    $version = [string]$project.Project.PropertyGroup.Version | Select-Object -First 1
    $packageReferences = @($project.SelectNodes('/Project/ItemGroup/PackageReference'))
    $sharpCompress = $packageReferences |
        Where-Object { $_.Include -eq 'SharpCompress' -and $_.Version -eq '0.50.4' }
    if ($targetFramework -ne 'net10.0-windows') {
        Add-Failure $failures "TargetFramework de producao inesperado: '$targetFramework'."
    }
    if ($version -ne '2.0.0') {
        Add-Failure $failures "Versao de producao inesperada: '$version'."
    }
    if ($packageReferences.Count -ne 1 -or $sharpCompress.Count -ne 1) {
        Add-Failure $failures 'A Release permite somente SharpCompress fixado como PackageReference 0.50.4.'
    }
    if (@($project.SelectNodes('/Project/ItemGroup/Reference') |
            Where-Object { $_.Include -eq 'SharpCompress' }).Count -ne 0) {
        Add-Failure $failures 'Referencia binaria local de SharpCompress nao e permitida.'
    }
}

if (-not (Test-Path -LiteralPath $globalJsonPath -PathType Leaf)) {
    Add-Failure $failures 'global.json ausente.'
}
else {
    $globalJson = Get-Content -LiteralPath $globalJsonPath -Raw | ConvertFrom-Json -Depth 8
    if ([string]$globalJson.sdk.version -ne '10.0.400' -or
        [string]$globalJson.sdk.rollForward -ne 'disable' -or
        $globalJson.sdk.allowPrerelease -ne $false) {
        Add-Failure $failures 'SDK de Release precisa estar fixado exatamente em 10.0.400 sem roll-forward ou preview.'
    }
}

if (-not (Test-Path -LiteralPath $buildPropsPath -PathType Leaf)) {
    Add-Failure $failures 'Directory.Build.props ausente.'
}
else {
    [xml]$buildProps = Get-Content -LiteralPath $buildPropsPath -Raw
    $properties = @($buildProps.Project.PropertyGroup) | Select-Object -First 1
    $auditWarnings = [string]$properties.WarningsAsErrors
    $releaseWarnings = $properties.TreatWarningsAsErrors
    $releaseLockedRestore = $properties.RestoreLockedMode
    if ([string]$properties.Deterministic -ne 'true' -or
        [string]$releaseWarnings.InnerText -ne 'true' -or
        [string]$releaseWarnings.Condition -ne '''$(Configuration)'' == ''Release''' -or
        [string]$properties.RestorePackagesWithLockFile -ne 'true' -or
        [string]$releaseLockedRestore.InnerText -ne 'true' -or
        [string]$releaseLockedRestore.Condition -ne '''$(Configuration)'' == ''Release''' -or
        [string]$properties.NuGetAudit -ne 'true' -or
        [string]$properties.NuGetAuditMode -ne 'all' -or
        $auditWarnings -notmatch 'NU1900' -or
        $auditWarnings -notmatch 'NU1904') {
        Add-Failure $failures 'Build precisa exigir determinismo, lock file e auditoria NuGet completa como erro.'
    }
}

if ((Test-Path -LiteralPath $projectPath -PathType Leaf) -and
    (Test-Path -LiteralPath $buildPropsPath -PathType Leaf)) {
    $securityConfigurationText = (Get-Content -LiteralPath $projectPath -Raw) + "`n" +
        (Get-Content -LiteralPath $buildPropsPath -Raw)
    if ($securityConfigurationText -match '(?i)<\s*(?:NoWarn|RestoreSources|RestoreAdditionalProjectSources|RestoreIgnoreFailedSources|NuGetAuditSuppress)\b') {
        Add-Failure $failures 'Projeto ou props tentam suprimir auditoria ou substituir as fontes NuGet confiaveis.'
    }
}

if (-not (Test-Path -LiteralPath $lockPath -PathType Leaf)) {
    Add-Failure $failures 'packages.lock.json ausente.'
}
else {
    $lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json -Depth 32
    $framework = @($lock.dependencies.PSObject.Properties) | Select-Object -First 1
    $lockedSharpCompress = if ($null -ne $framework) {
        $framework.Value.PSObject.Properties['SharpCompress'].Value
    } else { $null }
    if ($null -eq $lockedSharpCompress -or [string]$lockedSharpCompress.resolved -ne '0.50.4') {
        Add-Failure $failures 'O lock file nao fixa SharpCompress 0.50.4.'
    }
}

if (-not (Test-Path -LiteralPath $testLockPath -PathType Leaf)) {
    Add-Failure $failures 'Lock file do verificador ausente.'
}

if (-not (Test-Path -LiteralPath $nugetConfigPath -PathType Leaf)) {
    Add-Failure $failures 'NuGet.Config de confianca ausente.'
}
else {
    [xml]$nugetConfig = Get-Content -LiteralPath $nugetConfigPath -Raw
    $signatureMode = @($nugetConfig.configuration.config.add | Where-Object {
        $_.key -eq 'signatureValidationMode'
    })
    $allConfigEntries = @($nugetConfig.configuration.config.add)
    $nugetSource = @($nugetConfig.configuration.packageSources.add | Where-Object {
        $_.key -eq 'nuget.org' -and $_.value -eq 'https://api.nuget.org/v3/index.json'
    })
    $allNugetSources = @($nugetConfig.configuration.packageSources.add)
    $auditSource = @($nugetConfig.configuration.auditSources.add | Where-Object {
        $_.key -eq 'nuget.org' -and $_.value -eq 'https://api.nuget.org/v3/index.json'
    })
    $allAuditSources = @($nugetConfig.configuration.auditSources.add)
    $repositorySigner = @($nugetConfig.configuration.trustedSigners.repository | Where-Object {
        $_.name -eq 'nuget.org' -and $_.serviceIndex -eq 'https://api.nuget.org/v3/index.json'
    })
    $sourceMappings = @($nugetConfig.configuration.packageSourceMapping.packageSource | Where-Object {
        $_.key -eq 'nuget.org' -and @($_.package | Where-Object { $_.pattern -eq '*' }).Count -eq 1
    })
    $trustedCertificateNodes = @(
        $nugetConfig.configuration.trustedSigners.author.certificate
        $nugetConfig.configuration.trustedSigners.repository.certificate
    )
    $trustedCertificates = @($trustedCertificateNodes | ForEach-Object { [string]$_.fingerprint })
    $requiredFingerprints = @(
        '3F9001EA83C560D712C24CF213C3D312CB3BFF51EE89435D3430BD06B5D0EECE',
        'AA12DA22A49BCE7D5C1AE64CC1F3D892F150DA76140F210ABD2CBFFCA2C18A27',
        '566A31882BE208BE4422F7CFD66ED09F5D4524A5994F50CCC8B05EC0528C1353',
        '0E5F38F57DC1BCC806D8494F4F90FBCEDD988B46760709CBEEC6F4219AA6157D',
        '5A2901D6ADA3D18260B9C6DFE2133C95D74B9EEF6AE0E5DC334C8454D1477DF4',
        '1F4B311D9ACC115C8DC8018B5A49E00FCE6DA8E2855F9F014CA6F34570BC482D'
    )
    if ($signatureMode.Count -ne 1 -or $allConfigEntries.Count -ne 1 -or
        $signatureMode[0].value -ne 'require' -or
        $nugetSource.Count -ne 1 -or $allNugetSources.Count -ne 1 -or
        [string]$nugetSource[0].protocolVersion -ne '3' -or
        $auditSource.Count -ne 1 -or $allAuditSources.Count -ne 1 -or
        [string]$auditSource[0].protocolVersion -ne '3' -or
        $null -eq $nugetConfig.configuration.packageSources.clear -or
        $null -eq $nugetConfig.configuration.auditSources.clear -or
        $sourceMappings.Count -ne 1 -or
        @($nugetConfig.configuration.packageSourceMapping.packageSource).Count -ne 1 -or
        $repositorySigner.Count -ne 1 -or
        @($nugetConfig.configuration.trustedSigners.repository).Count -ne 1 -or
        @($nugetConfig.configuration.trustedSigners.author).Count -ne 1 -or
        [string]$nugetConfig.configuration.trustedSigners.author.name -ne 'microsoft' -or
        $trustedCertificates.Count -ne $requiredFingerprints.Count -or
        @($trustedCertificates | Select-Object -Unique).Count -ne $requiredFingerprints.Count -or
        $trustedCertificateNodes.Count -eq 0 -or
        @($trustedCertificateNodes | Where-Object {
            $_.hashAlgorithm -ne 'SHA256' -or $_.allowUntrustedRoot -ne 'false'
        }).Count -ne 0) {
        Add-Failure $failures 'NuGet precisa exigir assinatura e usar somente o repositorio HTTPS nuget.org confiavel.'
    }
    foreach ($fingerprint in $requiredFingerprints) {
        if ($trustedCertificates -notcontains $fingerprint) {
            Add-Failure $failures "Certificado NuGet confiavel ausente: $fingerprint"
        }
    }
}

function Test-VideoSet(
    [System.Collections.Generic.List[string]]$Failures,
    [string]$DirectoryPath,
    [string]$IntegrityPath,
    [int]$ExpectedCount,
    [string]$Label)
{
    if (-not (Test-Path -LiteralPath $DirectoryPath -PathType Container) -or
        -not (Test-Path -LiteralPath $IntegrityPath -PathType Leaf)) {
        Add-Failure $Failures "${Label}: pasta ou manifesto de integridade ausente."
        return
    }
    $integrity = Get-Content -LiteralPath $IntegrityPath -Raw | ConvertFrom-Json -Depth 8
    $entries = @($integrity.PSObject.Properties)
    $videos = @(Get-ChildItem -LiteralPath $DirectoryPath -File -Filter '*.mp4' -Force)
    if ($entries.Count -ne $ExpectedCount -or $videos.Count -ne $ExpectedCount) {
        Add-Failure $Failures "${Label}: arquivos=$($videos.Count), hashes=$($entries.Count), esperado=$ExpectedCount."
        return
    }
    foreach ($entry in $entries) {
        if ([IO.Path]::GetFileName($entry.Name) -cne $entry.Name -or
            [IO.Path]::GetExtension($entry.Name) -cne '.mp4') {
            Add-Failure $Failures "${Label}: nome inseguro $($entry.Name)."
            continue
        }
        $path = Join-Path $DirectoryPath $entry.Name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            Add-Failure $Failures "${Label}: arquivo ausente $($entry.Name)."
            continue
        }
        $file = Get-Item -LiteralPath $path
        if ($file.Length -ne [long]$entry.Value.length) {
            Add-Failure $Failures "${Label}: tamanho divergente $($entry.Name)."
        }
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        if (-not $hash.Equals([string]$entry.Value.sha256, [StringComparison]::OrdinalIgnoreCase)) {
            Add-Failure $Failures "${Label}: SHA-256 divergente $($entry.Name)."
        }
    }
}

Test-VideoSet $failures `
    (Join-Path $root 'Assets\Catalog\SystemVideos') `
    (Join-Path $root 'Assets\Catalog\SystemVideos\system-video-integrity.json') `
    38 `
    'Videos de sistema'
Test-VideoSet $failures `
    (Join-Path $root 'Assets\BackgroundVideos') `
    (Join-Path $root 'Assets\BackgroundVideos\background-video-integrity.json') `
    15 `
    'Videos de fundo'

$coverDirectory = Join-Path $root 'Assets\Catalog\Images'
$coverCount = @(Get-ChildItem -LiteralPath $coverDirectory -File -Force -ErrorAction SilentlyContinue).Count
if ($coverCount -ne 903) {
    Add-Failure $failures "Conjunto de capas incompleto: $coverCount (esperado 903)."
}

$organizedCoverRoot = Join-Path $root 'Capas-Turborama-por-Sistema'
$coverSystems = @(Get-ChildItem -LiteralPath $organizedCoverRoot -Directory -Force `
    -ErrorAction SilentlyContinue)
$indexedCoverNames = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::OrdinalIgnoreCase)
if ($coverSystems.Count -ne 22) {
    Add-Failure $failures "Indices de capas por sistema incompletos: $($coverSystems.Count) (esperado 22)."
}
foreach ($coverSystem in $coverSystems) {
    $indexPath = Join-Path $coverSystem.FullName 'index.json'
    if (-not (Test-Path -LiteralPath $indexPath -PathType Leaf)) {
        Add-Failure $failures "Indice de capas ausente: $($coverSystem.Name)."
        continue
    }
    try {
        $coverIndex = Get-Content -LiteralPath $indexPath -Raw | ConvertFrom-Json -Depth 16
        $coverItems = @($coverIndex.items)
        if ([int]$coverIndex.schemaVersion -ne 1 -or
            [string]$coverIndex.systemId -cne $coverSystem.Name -or
            [int]$coverIndex.coverCount -ne $coverItems.Count) {
            Add-Failure $failures "Cabecalho do indice de capas invalido: $($coverSystem.Name)."
            continue
        }
        if ($coverSystem.Name -ceq 'windows' -and $coverItems.Count -ne 100) {
            Add-Failure $failures "O indice Windows precisa conter exatamente 100 capas; encontrou $($coverItems.Count)."
        }
        $organizedItemCount = @($coverItems | Where-Object {
            -not [string]::IsNullOrWhiteSpace([string]$_.organizedImage)
        }).Count
        $organizedFileCount = @(Get-ChildItem -LiteralPath $coverSystem.FullName `
            -File -Filter '*.jpg' -Force).Count
        if ($organizedItemCount -ne $organizedFileCount) {
            Add-Failure $failures "Copias organizadas divergentes em $($coverSystem.Name): indice=$organizedItemCount, arquivos=$organizedFileCount."
        }
        foreach ($coverItem in $coverItems) {
            $fileName = [string]$coverItem.fileName
            $organizedImage = [string]$coverItem.organizedImage
            if ([IO.Path]::GetFileName($fileName) -cne $fileName -or
                [IO.Path]::GetExtension($fileName) -cne '.jpg' -or
                -not $indexedCoverNames.Add($fileName) -or
                (-not [string]::IsNullOrWhiteSpace($organizedImage) -and
                 $organizedImage -cne $fileName) -or
                [string]$coverItem.catalogImage -cne "../../Assets/Catalog/Images/$fileName") {
                Add-Failure $failures "Entrada insegura ou duplicada no indice $($coverSystem.Name): $fileName."
                continue
            }

            $catalogCover = Join-Path $coverDirectory $fileName
            $organizedCover = Join-Path $coverSystem.FullName $fileName
            $coverPaths = @($catalogCover)
            if (-not [string]::IsNullOrWhiteSpace($organizedImage)) {
                $coverPaths += $organizedCover
            }
            foreach ($coverPath in $coverPaths) {
                if (-not (Test-Path -LiteralPath $coverPath -PathType Leaf)) {
                    Add-Failure $failures "Capa indexada ausente: $coverPath."
                    continue
                }
                $coverInfo = Get-Item -LiteralPath $coverPath -Force
                if (($coverInfo.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
                    $coverInfo.Length -ne [long]$coverItem.bytes) {
                    Add-Failure $failures "Tipo ou tamanho divergente para a capa: $coverPath."
                }
                $coverHash = (Get-FileHash -LiteralPath $coverPath -Algorithm SHA256).Hash
                if (-not $coverHash.Equals(
                        [string]$coverItem.sha256,
                        [StringComparison]::OrdinalIgnoreCase)) {
                    Add-Failure $failures "SHA-256 divergente para a capa: $coverPath."
                }
            }
        }
    }
    catch {
        Add-Failure $failures "Indice de capas ilegivel ($($coverSystem.Name)): $($_.Exception.Message)"
    }
}
if ($indexedCoverNames.Count -ne 902) {
    Add-Failure $failures "Indices por sistema cobrem $($indexedCoverNames.Count) capas unicas (esperado 902)."
}

if (-not (Test-Path -LiteralPath $catalogPath -PathType Leaf)) {
    Add-Failure $failures 'Manifesto publico do catalogo ausente.'
}
else {
    $catalogInfo = Get-Item -LiteralPath $catalogPath
    if ($catalogInfo.Length -gt 8MB) { Add-Failure $failures 'Manifesto publico excede 8 MB.' }
    $catalog = Get-Content -LiteralPath $catalogPath -Raw | ConvertFrom-Json -Depth 32
    if ($catalog.PSObject.Properties.Name -contains 'enableTestDownloads' -or
        $catalog.PSObject.Properties.Name -contains 'testDownload') {
        Add-Failure $failures 'Contrato legado de download de teste e proibido em Release.'
    }
    $remoteUrls = @($catalog.items | Where-Object {
        -not [string]::IsNullOrWhiteSpace([string]$_.downloadUrl)
    })
    if ($remoteUrls.Count -ne 0) {
        Add-Failure $failures "O catalogo publico contem $($remoteUrls.Count) URL(s) permanente(s)."
    }
}

$forbiddenPatterns = [ordered]@{
    'bypass skipLogin' = '\bskipLogin\b'
    'catalogo privado com chave no cliente' = 'PrivateCatalogSecrets|PRIVATE_CATALOG_EMBEDDED|TryReadPackagedKey'
    'entrada key.txt' = '(?i)\bkey\.txt\b'
    'modo de teste remoto' = '(?i)enableTestDownloads\s*["'']?\s*[:=]\s*(true|\$true)'
}

# Os dominios privados sao comparados somente por SHA-256. Assim o proprio
# gate nao publica os nomes que deve impedir no cliente.
$forbiddenOriginHostSha256 = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::OrdinalIgnoreCase)
foreach ($hash in @(
        '122193b87a9c6128a80c0e7ba0b6ccec8162c744047dcbc12786a6f7bc901d53',
        '04fea9ab06778f5d71fac78119ba36fe3d55408a26990b63da74c14967674ec4',
        'f12eaaa2ea14626453e54033b30c23843e2b01a2f47b2f9764ed1a0a639e01cd')) {
    [void]$forbiddenOriginHostSha256.Add($hash)
}

$allowedPatternFiles = @(
    (Join-Path $root 'tools\Test-ReleaseSource.ps1'),
    (Join-Path $root 'tools\Test-PublishedPackage.ps1')
)

foreach ($path in Get-SourceTextFiles) {
    $text = Get-Content -LiteralPath $path -Raw
    $domainScanText = $text.Replace('\.', '.', [StringComparison]::Ordinal)
    foreach ($domainMatch in [regex]::Matches(
            $domainScanText,
            '(?i)(?<![a-z0-9-])(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}(?![a-z0-9-])')) {
        $domain = $domainMatch.Value.ToLowerInvariant()
        $domainBytes = [Text.Encoding]::UTF8.GetBytes($domain)
        try {
            $domainHash = [Convert]::ToHexString(
                [Security.Cryptography.SHA256]::HashData($domainBytes))
        }
        finally {
            [Security.Cryptography.CryptographicOperations]::ZeroMemory($domainBytes)
        }
        if ($forbiddenOriginHostSha256.Contains($domainHash)) {
            $relative = [IO.Path]::GetRelativePath($root, $path)
            Add-Failure $failures "dominio de origem privado: $relative"
            break
        }
    }
    if ($allowedPatternFiles -contains $path) { continue }
    foreach ($entry in $forbiddenPatterns.GetEnumerator()) {
        if ($text -match $entry.Value) {
            $relative = [IO.Path]::GetRelativePath($root, $path)
            Add-Failure $failures "$($entry.Key): $relative"
        }
    }
}

$workflowFiles = @(
    Get-ChildItem -LiteralPath (Join-Path $root '.github\workflows') -File -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.Extension -in '.yml', '.yaml' }
)
foreach ($workflow in $workflowFiles) {
    $workflowText = Get-Content -LiteralPath $workflow.FullName -Raw
    $usesMatches = [regex]::Matches(
        $workflowText,
        '(?m)^\s*-?\s*uses:\s*["'']?([^"''#\s]+)["'']?\s*(?:#.*)?$')
    foreach ($usesMatch in $usesMatches) {
        $actionReference = $usesMatch.Groups[1].Value
        if (-not $actionReference.StartsWith('./', [StringComparison]::Ordinal) -and
            $actionReference -notmatch '@[0-9a-fA-F]{40}$') {
            Add-Failure $failures "Action sem SHA imutavel em $($workflow.Name): $actionReference"
        }
    }
}

if ($failures.Count -ne 0) {
    Write-Error ("Gate de Release falhou:`n - " + ($failures -join "`n - "))
    exit 20
}

Write-Host 'PASS: fonte de Release sem bypass, teste remoto, chave incorporada ou URL privada permanente.'
