[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$SourceMapPath,

    [Parameter(Mandatory)]
    [string]$StagingDirectory,

    [string]$CatalogPath = (Join-Path $PSScriptRoot '..\Assets\Catalog\catalog.json'),
    [string]$ImagesDirectory = (Join-Path $PSScriptRoot '..\Assets\Catalog\Images'),
    [string]$OrganizedDirectory = (Join-Path $PSScriptRoot '..\Capas-Turborama-por-Sistema'),
    [ValidateRange(1, 100)]
    [long]$JpegQuality = 98,
    [switch]$Integrate
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function Open-BitmapDetached {
    param([Parameter(Mandatory)][string]$Path)

    $bytes = [IO.File]::ReadAllBytes($Path)
    $stream = [IO.MemoryStream]::new($bytes, $false)
    try {
        $loaded = [Drawing.Bitmap]::new($stream)
        try { return [Drawing.Bitmap]::new($loaded) }
        finally { $loaded.Dispose() }
    }
    finally { $stream.Dispose() }
}

function Save-JpegAtomic {
    param(
        [Parameter(Mandatory)][Drawing.Image]$Image,
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][long]$Quality
    )

    $codec = [Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() |
        Where-Object MimeType -eq 'image/jpeg' |
        Select-Object -First 1
    if ($null -eq $codec) { throw 'Codec JPEG não encontrado.' }

    $temporaryPath = Join-Path (Split-Path -Parent $Path) (
        '.' + [IO.Path]::GetFileName($Path) + '.' + [guid]::NewGuid().ToString('N') + '.tmp')
    $parameters = [Drawing.Imaging.EncoderParameters]::new(1)
    $parameters.Param[0] = [Drawing.Imaging.EncoderParameter]::new(
        [Drawing.Imaging.Encoder]::Quality,
        $Quality)
    try {
        $Image.Save($temporaryPath, $codec, $parameters)
        [IO.File]::Move($temporaryPath, $Path, $true)
    }
    finally {
        $parameters.Dispose()
        if (Test-Path -LiteralPath $temporaryPath) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
    }
}

function Get-RequiredStringProperty {
    param(
        [Parameter(Mandatory)]$Object,
        [Parameter(Mandatory)][string]$Name
    )

    $property = $Object.PSObject.Properties[$Name]
    $value = if ($null -eq $property) { '' } else { [string]$property.Value }
    if ([string]::IsNullOrWhiteSpace($value)) {
        throw "Propriedade obrigatória ausente no mapa: $Name."
    }
    return $value
}

$mapInfo = Get-Item -LiteralPath $SourceMapPath -Force
$map = Get-Content -LiteralPath $mapInfo.FullName -Raw | ConvertFrom-Json -Depth 32
$covers = @($map.covers)
if ($covers.Count -ne 902 -or [int]$map.coverCount -ne 902) {
    throw "O mapa precisa conter exatamente 902 capas; encontrou $($covers.Count)."
}

$catalog = Get-Content -LiteralPath $CatalogPath -Raw | ConvertFrom-Json -Depth 64
$catalogItems = @($catalog.items)
if ($catalogItems.Count -ne 902) {
    throw "O catálogo precisa conter exatamente 902 itens; encontrou $($catalogItems.Count)."
}

$catalogById = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
foreach ($item in $catalogItems) {
    if (-not $catalogById.TryAdd([string]$item.id, $item)) {
        throw "ID duplicado no catálogo: $($item.id)."
    }
}

$seenIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$seenSources = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$validated = [Collections.Generic.List[object]]::new()
foreach ($cover in $covers) {
    $id = Get-RequiredStringProperty $cover 'catalogId'
    $sourcePath = [IO.Path]::GetFullPath((Get-RequiredStringProperty $cover 'sourceFullPath'))
    $sourceSha256 = (Get-RequiredStringProperty $cover 'sha256').ToLowerInvariant()
    if ($sourceSha256 -notmatch '^[0-9a-f]{64}$') {
        throw "SHA-256 inválido para $id."
    }
    if (-not $seenIds.Add($id)) { throw "ID repetido no mapa: $id." }
    if (-not $seenSources.Add($sourcePath)) { throw "Fonte reutilizada no mapa: $sourcePath." }
    $catalogItem = $null
    if (-not $catalogById.TryGetValue($id, [ref]$catalogItem)) {
        throw "O mapa referencia um ID ausente do catálogo: $id."
    }
    if ([string]$catalogItem.categoryId -cne [string]$cover.categoryId -or
        [string]$catalogItem.title -cne [string]$cover.title) {
        throw "Categoria ou título divergente para $id."
    }
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        throw "Fonte ausente para ${id}: $sourcePath"
    }

    $sourceInfo = Get-Item -LiteralPath $sourcePath -Force
    if ($sourceInfo.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw "A fonte não pode ser um arquivo reparse point: $sourcePath"
    }
    if ($sourceInfo.Length -ne [long]$cover.bytes) {
        throw "Tamanho divergente para ${id}: $sourcePath"
    }
    $actualSha256 = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualSha256 -cne $sourceSha256) {
        throw "SHA-256 divergente para ${id}: $sourcePath"
    }

    $extension = [IO.Path]::GetExtension($sourcePath).ToLowerInvariant()
    if ($extension -notin @('.jpg', '.jpeg', '.png')) {
        throw "Formato de imagem não permitido para ${id}: $extension"
    }

    $sourceImage = Open-BitmapDetached -Path $sourcePath
    try {
        if ($sourceImage.Width -ne 1024 -or $sourceImage.Height -ne 1536) {
            throw "Dimensão inválida para ${id}: $($sourceImage.Width)x$($sourceImage.Height)."
        }
    }
    finally { $sourceImage.Dispose() }

    $validated.Add([pscustomobject]@{
            Id = $id
            CategoryId = [string]$catalogItem.categoryId
            Title = [string]$catalogItem.title
            SourcePath = $sourcePath
            Extension = $extension
            SourceSha256 = $sourceSha256
        })
}

if ($seenIds.Count -ne $catalogById.Count) {
    $missing = @($catalogItems | Where-Object { -not $seenIds.Contains([string]$_.id) })
    throw "O mapa não cobre todo o catálogo: $($missing.Count) IDs ausentes."
}
if (Test-Path -LiteralPath $StagingDirectory) {
    throw "A pasta de staging já existe; use uma pasta nova: $StagingDirectory"
}

$stagingRoot = [IO.Path]::GetFullPath($StagingDirectory)
$stagingImages = Join-Path $stagingRoot 'Images'
New-Item -ItemType Directory -Path $stagingImages -Force | Out-Null

$outputs = [Collections.Generic.List[object]]::new()
$processed = 0
foreach ($entry in $validated) {
    $processed++
    $destination = Join-Path $stagingImages "$($entry.Id).jpg"
    if ($entry.Extension -in @('.jpg', '.jpeg')) {
        [IO.File]::Copy($entry.SourcePath, $destination, $false)
    }
    else {
        $image = Open-BitmapDetached -Path $entry.SourcePath
        try { Save-JpegAtomic -Image $image -Path $destination -Quality $JpegQuality }
        finally { $image.Dispose() }
    }

    $outputImage = Open-BitmapDetached -Path $destination
    try {
        if ($outputImage.Width -ne 1024 -or $outputImage.Height -ne 1536) {
            throw "O JPEG gerado para $($entry.Id) perdeu a geometria esperada."
        }
    }
    finally { $outputImage.Dispose() }

    $outputInfo = Get-Item -LiteralPath $destination -Force
    $outputs.Add([ordered]@{
            catalogId = $entry.Id
            categoryId = $entry.CategoryId
            title = $entry.Title
            sourceSha256 = $entry.SourceSha256
            outputBytes = $outputInfo.Length
            outputSha256 = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
        })
    if ($processed % 50 -eq 0 -or $processed -eq 902) {
        Write-Host "Capas processadas: $processed/902"
    }
}

$stageFiles = @(Get-ChildItem -LiteralPath $stagingImages -File -Force)
if ($stageFiles.Count -ne 902) {
    throw "Staging incompleto: $($stageFiles.Count) JPEGs."
}

$evidence = [ordered]@{
    schemaVersion = 1
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    sourceMapSha256 = (Get-FileHash -LiteralPath $mapInfo.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    sourceMap = $mapInfo.Name
    coverCount = 902
    jpegQuality = $JpegQuality
    outputs = @($outputs)
}
$utf8 = [Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText(
    (Join-Path $stagingRoot 'cover-stage-evidence.json'),
    ($evidence | ConvertTo-Json -Depth 8),
    $utf8)

if ($Integrate) {
    foreach ($entry in $validated) {
        $staged = Join-Path $stagingImages "$($entry.Id).jpg"
        $catalogDestination = Join-Path $ImagesDirectory "$($entry.Id).jpg"
        if (-not (Test-Path -LiteralPath $catalogDestination -PathType Leaf)) {
            throw "Destino canônico ausente para $($entry.Id): $catalogDestination"
        }
        [IO.File]::Copy($staged, $catalogDestination, $true)

        $organizedDestination = Join-Path (
            (Join-Path $OrganizedDirectory $entry.CategoryId)) "$($entry.Id).jpg"
        if (Test-Path -LiteralPath $organizedDestination -PathType Leaf) {
            [IO.File]::Copy($staged, $organizedDestination, $true)
        }
    }

    & (Join-Path $PSScriptRoot 'Organize-CoversBySystem.ps1') `
        -CatalogPath $CatalogPath `
        -ImagesDirectory $ImagesDirectory `
        -OutputDirectory $OrganizedDirectory

    $canonicalFiles = @(Get-ChildItem -LiteralPath $ImagesDirectory -File -Force)
    if ($canonicalFiles.Count -ne 903) {
        throw "Conjunto canônico inesperado após integração: $($canonicalFiles.Count) arquivos."
    }
    foreach ($output in $outputs) {
        $path = Join-Path $ImagesDirectory "$($output.catalogId).jpg"
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($hash -cne [string]$output.outputSha256) {
            throw "Falha de verificação pós-integração para $($output.catalogId)."
        }
    }
    Write-Host 'Integração concluída: 902/902 capas e índices atualizados.'
}
else {
    Write-Host 'Staging concluído e validado; nenhuma capa do repositório foi alterada.'
}

Write-Host "Mapa: $($mapInfo.FullName)"
Write-Host "Staging: $stagingRoot"
