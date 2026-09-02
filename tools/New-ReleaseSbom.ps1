[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PackageRoot,

    [Parameter(Mandatory)]
    [string]$OutputPath,

    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),

    [string]$GitPath = 'git'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$package = (Resolve-Path -LiteralPath $PackageRoot).Path
$lockPath = Join-Path $root 'packages.lock.json'
$projectPath = Join-Path $root 'TurboBoxManager.csproj'
$executablePath = Join-Path $package 'Turborama.exe'
$assetsPath = Join-Path $root 'obj\project.assets.json'

foreach ($required in @($lockPath, $projectPath, $executablePath, $assetsPath)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Arquivo obrigatorio para o SBOM ausente: $required"
    }
}

[xml]$project = Get-Content -LiteralPath $projectPath -Raw
$versionNode = @($project.Project.PropertyGroup.Version) | Select-Object -First 1
$version = [string]$versionNode
$commit = (& $GitPath -c 'core.fsmonitor=false' -c 'core.hooksPath=NUL' `
    -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$') {
    throw 'Nao foi possivel identificar o commit para o SBOM.'
}

$lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json -Depth 32
$framework = $lock.dependencies.PSObject.Properties['net10.0-windows7.0']
if ($null -eq $framework) {
    throw 'O lock file nao contem o target base net10.0-windows7.0.'
}

$packages = [System.Collections.Generic.List[object]]::new()
$relationships = [System.Collections.Generic.List[object]]::new()
$includedPackageKeys = [System.Collections.Generic.HashSet[string]]::new(
    [StringComparer]::OrdinalIgnoreCase)
$appId = 'SPDXRef-Package-Turborama'
$exeHash = (Get-FileHash -LiteralPath $executablePath -Algorithm SHA256).Hash
$packages.Add([ordered]@{
    name = 'Turborama'
    SPDXID = $appId
    versionInfo = $version
    downloadLocation = 'NOASSERTION'
    filesAnalyzed = $false
    checksums = @([ordered]@{ algorithm = 'SHA256'; checksumValue = $exeHash })
    licenseConcluded = 'NOASSERTION'
    licenseDeclared = 'NOASSERTION'
    copyrightText = 'NOASSERTION'
})

foreach ($dependency in @($framework.Value.PSObject.Properties | Sort-Object Name)) {
    if ([string]$dependency.Value.type -eq 'Project') { continue }
    $name = [string]$dependency.Name
    $resolved = [string]$dependency.Value.resolved
    if ([string]::IsNullOrWhiteSpace($resolved)) {
        throw "Dependencia sem versao resolvida no lock file: $name"
    }
    $safeId = ($name -replace '[^A-Za-z0-9.-]', '-')
    $dependencyId = "SPDXRef-Package-$safeId"
    $license = if ($name -in @('SharpCompress', 'System.Management')) {
        'MIT'
    } else { 'NOASSERTION' }
    $entry = [ordered]@{
        name = $name
        SPDXID = $dependencyId
        versionInfo = $resolved
        downloadLocation = "https://www.nuget.org/packages/$name/$resolved"
        filesAnalyzed = $false
        licenseConcluded = $license
        licenseDeclared = $license
        copyrightText = 'NOASSERTION'
        externalRefs = @([ordered]@{
            referenceCategory = 'PACKAGE-MANAGER'
            referenceType = 'purl'
            referenceLocator = "pkg:nuget/$name@$resolved"
        })
    }
    $contentHash = [string]$dependency.Value.contentHash
    if (-not [string]::IsNullOrWhiteSpace($contentHash)) {
        $hashBytes = [Convert]::FromBase64String($contentHash)
        $entry.checksums = @([ordered]@{
            algorithm = 'SHA512'
            checksumValue = [Convert]::ToHexString($hashBytes)
        })
    }
    $packages.Add($entry)
    [void]$includedPackageKeys.Add("$name/$resolved")
    $relationships.Add([ordered]@{
        spdxElementId = $appId
        relationshipType = 'DEPENDS_ON'
        relatedSpdxElement = $dependencyId
    })
}

$assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json -Depth 100
$runtimeDependencies = @(
    $assets.project.frameworks.PSObject.Properties |
        ForEach-Object { @($_.Value.downloadDependencies) } |
        Where-Object { $null -ne $_ } |
        Sort-Object name
)
foreach ($runtimeDependency in $runtimeDependencies) {
    $name = [string]$runtimeDependency.name
    $versionRange = [string]$runtimeDependency.version
    if ($versionRange -notmatch '^\[([^,\]]+),\s*\1\]$') {
        throw "Runtime pack sem versao exata no assets file: $name $versionRange"
    }
    $resolved = $Matches[1]
    if (-not $includedPackageKeys.Add("$name/$resolved")) { continue }
    $safeId = ($name -replace '[^A-Za-z0-9.-]', '-')
    $dependencyId = "SPDXRef-Package-$safeId"
    $packages.Add([ordered]@{
        name = $name
        SPDXID = $dependencyId
        versionInfo = $resolved
        downloadLocation = "https://www.nuget.org/packages/$name/$resolved"
        filesAnalyzed = $false
        licenseConcluded = 'NOASSERTION'
        licenseDeclared = 'NOASSERTION'
        copyrightText = 'NOASSERTION'
        externalRefs = @([ordered]@{
            referenceCategory = 'PACKAGE-MANAGER'
            referenceType = 'purl'
            referenceLocator = "pkg:nuget/$name@$resolved"
        })
    })
    $relationships.Add([ordered]@{
        spdxElementId = $appId
        relationshipType = 'DEPENDS_ON'
        relatedSpdxElement = $dependencyId
    })
}

$relationships.Insert(0, [ordered]@{
    spdxElementId = 'SPDXRef-DOCUMENT'
    relationshipType = 'DESCRIBES'
    relatedSpdxElement = $appId
})

$sbom = [ordered]@{
    spdxVersion = 'SPDX-2.3'
    dataLicense = 'CC0-1.0'
    SPDXID = 'SPDXRef-DOCUMENT'
    name = "Turborama-$version-win-x64"
    documentNamespace = "https://github.com/luziellacerda/TRUBORAMA-SUITE/spdx/$version/$commit/$($exeHash.ToLowerInvariant())"
    creationInfo = [ordered]@{
        created = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
        creators = @('Tool: Turborama-New-ReleaseSbom/2.0.0')
    }
    packages = $packages
    relationships = $relationships
}

$outputFullPath = [IO.Path]::GetFullPath($OutputPath)
$outputParent = Split-Path -Parent $outputFullPath
if (-not (Test-Path -LiteralPath $outputParent -PathType Container)) {
    New-Item -ItemType Directory -Path $outputParent | Out-Null
}
[IO.File]::WriteAllText(
    $outputFullPath,
    ($sbom | ConvertTo-Json -Depth 20),
    [Text.UTF8Encoding]::new($false))
Write-Host "SBOM SPDX 2.3: $outputFullPath"
