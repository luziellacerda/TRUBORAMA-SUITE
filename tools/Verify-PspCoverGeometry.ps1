[CmdletBinding()]
param(
    [string]$GeneratedDirectory = (Join-Path $PSScriptRoot '..\Capas-Turborama-por-Sistema\psp\sources'),
    [string]$CatalogPath = (Join-Path $PSScriptRoot '..\Assets\Catalog\catalog.json'),
    [string]$CatalogImagesDirectory = (Join-Path $PSScriptRoot '..\Assets\Catalog\Images'),
    [string]$OrganizedDirectory = (Join-Path $PSScriptRoot '..\Capas-Turborama-por-Sistema\psp'),
    [string]$ApprovedGhostOfSparta = (Join-Path $PSScriptRoot '..\Capas-Turborama-por-Sistema\psp\sources\65e6c73a647fcf79f5c1732eee339bfe.png')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function Get-ImageSize {
    param([string]$Path)
    $stream = [IO.File]::OpenRead($Path)
    try {
        $image = [System.Drawing.Image]::FromStream($stream, $false, $true)
        try { return [pscustomobject]@{ Width = $image.Width; Height = $image.Height } }
        finally { $image.Dispose() }
    }
    finally { $stream.Dispose() }
}

$catalog = Get-Content -LiteralPath $CatalogPath -Raw | ConvertFrom-Json
$items = @($catalog.items | Where-Object categoryId -eq 'psp')
if ($items.Count -ne 21) { throw "Quantidade PSP inesperada: $($items.Count)" }

$ghostOfSpartaId = '65e6c73a647fcf79f5c1732eee339bfe'
$ghostRiderId = '0a614f4d7177f612d609ab3fba4d0752'
$approvedHash = (Get-FileHash -LiteralPath $ApprovedGhostOfSparta -Algorithm SHA256).Hash
$sourceGhostOfSparta = Join-Path $GeneratedDirectory "$ghostOfSpartaId.png"
if ((Get-FileHash -LiteralPath $sourceGhostOfSparta -Algorithm SHA256).Hash -ne $approvedHash) {
    throw 'Ghost of Sparta não corresponde à fonte aprovada pelo usuário.'
}

foreach ($item in $items) {
    $sourcePath = Join-Path $GeneratedDirectory "$($item.id).png"
    $catalogImagePath = Join-Path $CatalogImagesDirectory "$($item.id).jpg"
    $organizedImagePath = Join-Path $OrganizedDirectory "$($item.id).jpg"
    foreach ($path in @($sourcePath, $catalogImagePath, $organizedImagePath)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Capa ausente: $path" }
        $size = Get-ImageSize -Path $path
        if ($size.Width -ne 1024 -or $size.Height -ne 1536) {
            throw "Dimensão inválida em ${path}: $($size.Width)x$($size.Height)"
        }
    }
    if ((Get-FileHash $catalogImagePath -Algorithm SHA256).Hash -ne
        (Get-FileHash $organizedImagePath -Algorithm SHA256).Hash) {
        throw "Cópia organizada diverge do catálogo: $($item.id)"
    }
}

if (-not (Test-Path -LiteralPath (Join-Path $GeneratedDirectory "$ghostRiderId.png"))) {
    throw 'Ghost Rider aprovada ainda não foi adicionada às fontes.'
}
if (Test-Path -LiteralPath (Join-Path $PSScriptRoot '..\.tmp\psp-normalized\_qa-psp-21-capas.jpg')) {
    throw 'Prancha normalizada rejeitada ainda está presente.'
}

Write-Host 'PSP QA aprovado: 21 fontes diretas e 42 cópias em 1024x1536.'
Write-Host 'Ghost of Sparta confere com o PNG aprovado pelo usuário.'
Write-Host 'Ghost Rider está presente no lote; prancha normalizada rejeitada está bloqueada.'
Write-Host 'Catálogo e pasta organizada possuem SHA-256 idêntico para cada JPG.'
