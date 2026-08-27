[CmdletBinding()]
param(
    [string]$DotNetPath = 'dotnet',
    [string]$OutputRoot,
    [ValidateRange(1, 72)]
    [int]$ValidityHours = 24
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectPath = Join-Path $PSScriptRoot 'UiPreview\Turborama.UiPreview.csproj'
$testProjectPath = Join-Path $PSScriptRoot 'UiPreview.Tests\Turborama.UiPreview.Tests.csproj'
if (-not $IsWindows) {
    throw 'A prévia usa DPAPI CurrentUser e só pode ser gerada no Windows.'
}
foreach ($requiredPath in @($projectPath, $testProjectPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Arquivo obrigatório ausente: $requiredPath"
    }
}

$gitStatus = @(& git -C $repoRoot status --porcelain=v1 --untracked-files=all)
if ($LASTEXITCODE -ne 0) {
    throw 'Não foi possível consultar o estado do Git.'
}
if ($gitStatus.Count -ne 0) {
    throw 'O repositório precisa estar limpo para gerar uma prévia entregável.'
}

$commit = (& git -C $repoRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$') {
    throw 'Não foi possível obter o commit Git canônico.'
}
$shortCommit = $commit.Substring(0, 12)

$sdkVersion = (& $DotNetPath --version).Trim()
if ($LASTEXITCODE -ne 0 -or $sdkVersion -ne '10.0.400') {
    throw "SDK .NET 10.0.400 obrigatório; encontrado: $sdkVersion"
}

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot 'artifacts\ui-preview'
}
$canonicalOutputRoot = [IO.Path]::GetFullPath($OutputRoot)
if ($canonicalOutputRoot.StartsWith('\\', [StringComparison]::Ordinal) -or
    [IO.Path]::GetPathRoot($canonicalOutputRoot) -eq $canonicalOutputRoot) {
    throw 'A saída deve ser uma pasta específica de um disco local.'
}
$driveRoot = [IO.Path]::GetPathRoot($canonicalOutputRoot)
$drive = [IO.DriveInfo]::new($driveRoot)
if ($drive.DriveType -ne [IO.DriveType]::Fixed) {
    throw 'A saída deve estar em um disco físico local.'
}
if (-not (Test-Path -LiteralPath $canonicalOutputRoot -PathType Container)) {
    $parent = [IO.Directory]::GetParent($canonicalOutputRoot)
    if ($null -eq $parent -or -not $parent.Exists -or
        ($parent.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'A pasta-pai da saída não é válida.'
    }
    [void](New-Item -ItemType Directory -Path $canonicalOutputRoot)
}
$outputInfo = Get-Item -LiteralPath $canonicalOutputRoot -Force
while ($null -ne $outputInfo) {
    if (($outputInfo.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'A pasta de saída não pode atravessar um link ou junction.'
    }
    $outputInfo = $outputInfo.Parent
}

$stamp = [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss')
$finalDirectory = Join-Path $canonicalOutputRoot "Turborama-UI-Preview-$shortCommit-$stamp"
$stagingDirectory = Join-Path $canonicalOutputRoot ('.ui-preview-staging-' + [Guid]::NewGuid().ToString('N'))
if ((Test-Path -LiteralPath $finalDirectory) -or (Test-Path -LiteralPath $stagingDirectory)) {
    throw 'A pasta inédita de saída já existe.'
}

$issuedAtUtc = [DateTimeOffset]::UtcNow
$expiresAtUtc = $issuedAtUtc.AddHours($ValidityHours)
$passwordRandom = [Security.Cryptography.RandomNumberGenerator]::GetBytes(24)
$salt = [Security.Cryptography.RandomNumberGenerator]::GetBytes(32)
$passwordBytes = $null
$passwordHash = $null
$payloadBytes = $null
$protectedPayload = $null
$entropy = $null
$manifestBytes = $null
$completed = $false
try {
    & $DotNetPath restore $testProjectPath `
        --locked-mode `
        --no-http-cache `
        --configfile (Join-Path $repoRoot 'NuGet.Config') `
        -p:Configuration=Release `
        -p:RuntimeIdentifier=win-x64
    if ($LASTEXITCODE -ne 0) {
        throw 'Restore bloqueado da prévia e dos testes falhou.'
    }

    & $DotNetPath run `
        --project $testProjectPath `
        -c Release `
        -r win-x64 `
        --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw 'Testes da prévia falharam.'
    }

    & $DotNetPath publish $projectPath `
        -c Release `
        -r win-x64 `
        --self-contained true `
        --no-restore `
        -o $stagingDirectory `
        -p:TurboramaPreviewSourceRevision=$commit `
        -p:DebugType=None `
        -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) {
        throw 'Publish da prévia falhou.'
    }

    $previewExe = Join-Path $stagingDirectory 'Turborama-UI-Preview.exe'
    if (-not (Test-Path -LiteralPath $previewExe -PathType Leaf)) {
        throw 'O executável de prévia não foi produzido.'
    }
    $runtimeDumpTool = Join-Path $stagingDirectory 'createdump.exe'
    if (Test-Path -LiteralPath $runtimeDumpTool -PathType Leaf) {
        Remove-Item -LiteralPath $runtimeDumpTool -Force
    }
    $forbiddenFiles = @(
        Get-ChildItem -LiteralPath $stagingDirectory -File -Recurse -Force |
            Where-Object {
                $_.Name -in @('Turborama.exe', 'Turborama.dll', 'SharpCompress.dll') -or
                ($_.Extension -eq '.exe' -and $_.Name -ne 'Turborama-UI-Preview.exe')
            }
    )
    if ($forbiddenFiles.Count -ne 0) {
        throw 'O pacote contém um executável ou biblioteca não aprovado.'
    }
    $depsPath = Join-Path $stagingDirectory 'Turborama-UI-Preview.deps.json'
    $depsText = [IO.File]::ReadAllText($depsPath)
    foreach ($forbiddenDependency in @('SharpCompress', 'TurboBoxManager')) {
        if ($depsText.Contains($forbiddenDependency, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Dependência proibida no pacote: $forbiddenDependency"
        }
    }

    $readme = @"
TURBORAMA UI PREVIEW — SOMENTE VISUALIZAÇÃO LOCAL

Commit: $commit
Emitida em UTC: $($issuedAtUtc.ToString('O'))
Expira em UTC: $($expiresAtUtc.ToString('O'))

Esta ferramenta não é uma licença e não substitui o servidor.
O assembly da prévia não referencia cliente HTTP, rotina de download, instalação, extração,
abertura de pastas ou início de outros processos. Execute somente desta pasta local.
A credencial é protegida pelo DPAPI e só funciona nesta conta do Windows.
Não distribuir, publicar, assinar como produção ou enviar para GitHub Releases.
"@
    [IO.File]::WriteAllText(
        (Join-Path $stagingDirectory 'LEIA-ME-PRIMEIRO.txt'),
        $readme,
        [Text.UTF8Encoding]::new($false))

    $manifestPath = Join-Path $stagingDirectory 'ui-preview-manifest.json'
    $credentialPath = Join-Path $stagingDirectory 'ui-preview.credential'
    $manifestItems = @(
        Get-ChildItem -LiteralPath $stagingDirectory -File -Recurse -Force |
            Where-Object {
                -not $_.FullName.Equals($manifestPath, [StringComparison]::OrdinalIgnoreCase) -and
                -not $_.FullName.Equals($credentialPath, [StringComparison]::OrdinalIgnoreCase)
            } |
            Sort-Object FullName |
            ForEach-Object {
                [ordered]@{
                    path = [IO.Path]::GetRelativePath($stagingDirectory, $_.FullName).Replace('\', '/')
                    bytes = $_.Length
                    sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                }
            }
    )
    $manifest = [ordered]@{
        schemaVersion = 1
        marker = 'LOCAL-ADMIN-PREVIEW-NOT-FOR-DISTRIBUTION'
        commit = $commit
        expiresAtUtc = $expiresAtUtc.ToString('O', [Globalization.CultureInfo]::InvariantCulture)
        files = $manifestItems
    }
    $manifestBytes = [Text.UTF8Encoding]::new($false).GetBytes(
        ($manifest | ConvertTo-Json -Depth 8 -Compress))
    [IO.File]::WriteAllBytes($manifestPath, $manifestBytes)
    $manifestSha256 = [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData($manifestBytes)).ToLowerInvariant()

    $password = [Convert]::ToBase64String($passwordRandom).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    $passwordBytes = [Text.Encoding]::UTF8.GetBytes($password)
    $passwordHash = [Security.Cryptography.Rfc2898DeriveBytes]::Pbkdf2(
        $passwordBytes,
        $salt,
        600000,
        [Security.Cryptography.HashAlgorithmName]::SHA256,
        32)
    $payload = [ordered]@{
        schemaVersion = 1
        purpose = 'Turborama.UI.Preview'
        commit = $commit
        issuedAtUtc = $issuedAtUtc.ToString('O', [Globalization.CultureInfo]::InvariantCulture)
        expiresAtUtc = $expiresAtUtc.ToString('O', [Globalization.CultureInfo]::InvariantCulture)
        iterations = 600000
        salt = [Convert]::ToBase64String($salt)
        passwordHash = [Convert]::ToBase64String($passwordHash)
        manifestSha256 = $manifestSha256
    }
    $payloadBytes = [Text.Encoding]::UTF8.GetBytes(($payload | ConvertTo-Json -Compress))
    $entropy = [Text.Encoding]::UTF8.GetBytes('TurboRama.UI.Preview.Credential/v1')
    $protectedPayload = [Security.Cryptography.ProtectedData]::Protect(
        $payloadBytes,
        $entropy,
        [Security.Cryptography.DataProtectionScope]::CurrentUser)
    [IO.File]::WriteAllBytes($credentialPath, $protectedPayload)

    $password | & $DotNetPath run `
        --project $testProjectPath `
        -c Release `
        -r win-x64 `
        --no-build `
        --no-restore `
        -- `
        --verify-generated `
        $stagingDirectory `
        $commit
    if ($LASTEXITCODE -ne 0) {
        throw 'A verificação ponta a ponta do pacote e da credencial falhou.'
    }

    $finalGitStatus = @(& git -C $repoRoot status --porcelain=v1 --untracked-files=all)
    $finalStatusExitCode = $LASTEXITCODE
    $finalCommit = (& git -C $repoRoot rev-parse HEAD).Trim()
    $finalCommitExitCode = $LASTEXITCODE
    if ($finalStatusExitCode -ne 0 -or $finalCommitExitCode -ne 0 -or
        $finalGitStatus.Count -ne 0 -or
        -not $finalCommit.Equals($commit, [StringComparison]::Ordinal)) {
        throw 'A origem Git mudou durante o build; a prévia foi recusada.'
    }

    Move-Item -LiteralPath $stagingDirectory -Destination $finalDirectory
    $completed = $true
    [pscustomobject]@{
        OutputDirectory = $finalDirectory
        Executable = Join-Path $finalDirectory 'Turborama-UI-Preview.exe'
        Password = $password
        Commit = $commit
        ExpiresAtUtc = $expiresAtUtc.ToString('O', [Globalization.CultureInfo]::InvariantCulture)
        ManifestSha256 = $manifestSha256
    }
}
finally {
    foreach ($buffer in @(
        $passwordRandom,
        $salt,
        $passwordBytes,
        $passwordHash,
        $payloadBytes,
        $protectedPayload,
        $entropy,
        $manifestBytes)) {
        if ($null -ne $buffer -and $buffer.Length -gt 0) {
            [Security.Cryptography.CryptographicOperations]::ZeroMemory($buffer)
        }
    }
    if (-not $completed -and (Test-Path -LiteralPath $stagingDirectory -PathType Container)) {
        $resolvedStaging = [IO.Path]::GetFullPath($stagingDirectory)
        $rootPrefix = $canonicalOutputRoot.TrimEnd('\') + '\'
        if (-not $resolvedStaging.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase) -or
            -not [IO.Path]::GetFileName($resolvedStaging).StartsWith('.ui-preview-staging-', [StringComparison]::Ordinal)) {
            throw 'A pasta temporária recusou a validação de segurança para limpeza.'
        }
        Remove-Item -LiteralPath $resolvedStaging -Recurse -Force
    }
}
