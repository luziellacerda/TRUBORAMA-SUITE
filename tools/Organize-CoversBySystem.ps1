[CmdletBinding()]
param(
    [string]$CatalogPath = (Join-Path $PSScriptRoot '..\Assets\Catalog\catalog.json'),
    [string]$ImagesDirectory = (Join-Path $PSScriptRoot '..\Assets\Catalog\Images'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\Capas-Turborama-por-Sistema')
)

$ErrorActionPreference = 'Stop'
$catalog = Get-Content -LiteralPath $CatalogPath -Raw | ConvertFrom-Json
$utf8 = [Text.UTF8Encoding]::new($false)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$totalCovers = 0

$systems = foreach ($category in ($catalog.categories | Sort-Object order)) {
    $directory = Join-Path $OutputDirectory $category.id
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    $items = foreach ($item in ($catalog.items | Where-Object categoryId -eq $category.id | Sort-Object title)) {
        $fileName = "$($item.id).jpg"
        $catalogImagePath = Join-Path $ImagesDirectory $fileName
        if (-not (Test-Path -LiteralPath $catalogImagePath -PathType Leaf)) {
            throw "Capa ausente para $($item.title): $catalogImagePath"
        }
        $file = Get-Item -LiteralPath $catalogImagePath
        $organizedImagePath = Join-Path $directory $fileName
        $sourceImagePath = Join-Path (Join-Path $directory 'sources') "$($item.id).png"
        [ordered]@{
            id = $item.id
            title = $item.title
            fileName = $fileName
            catalogImage = "../../Assets/Catalog/Images/$fileName"
            organizedImage = if (Test-Path -LiteralPath $organizedImagePath -PathType Leaf) { $fileName } else { $null }
            sourceImage = if (Test-Path -LiteralPath $sourceImagePath -PathType Leaf) { "sources/$($item.id).png" } else { $null }
            bytes = $file.Length
            sha256 = (Get-FileHash -LiteralPath $catalogImagePath -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
    $manifest = [ordered]@{
        schemaVersion = 1
        systemId = $category.id
        displayName = $category.displayName
        shortCode = $category.shortCode
        coverCount = @($items).Count
        items = @($items)
    }
    [IO.File]::WriteAllText(
        (Join-Path $directory 'index.json'),
        ($manifest | ConvertTo-Json -Depth 5),
        $utf8)
    $totalCovers += @($items).Count
    [ordered]@{
        id = $category.id
        displayName = $category.displayName
        shortCode = $category.shortCode
        coverCount = @($items).Count
        manifest = "$($category.id)/index.json"
    }
}

$rootManifest = [ordered]@{
    schemaVersion = 1
    generatedFrom = '../Assets/Catalog/catalog.json'
    systemCount = @($systems).Count
    coverCount = $totalCovers
    systems = @($systems)
}
[IO.File]::WriteAllText(
    (Join-Path $OutputDirectory 'index.json'),
    ($rootManifest | ConvertTo-Json -Depth 5),
    $utf8)

Write-Host "Sistemas organizados: $(@($systems).Count)"
Write-Host "Capas indexadas: $($rootManifest.coverCount)"
Write-Host "Índice: $(Join-Path $OutputDirectory 'index.json')"
