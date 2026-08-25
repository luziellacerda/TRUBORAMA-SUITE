[CmdletBinding()]
param(
    [string]$GeneratedDirectory = (Join-Path $PSScriptRoot '..\Capas-Turborama-por-Sistema\psp\sources'),
    [string]$CatalogImagesDirectory = (Join-Path $PSScriptRoot '..\Assets\Catalog\Images'),
    [string]$OrganizedDirectory = (Join-Path $PSScriptRoot '..\Capas-Turborama-por-Sistema\psp'),
    [switch]$Integrate
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$ids = @(
    '0af875e2b85035406a736b416c026c23',
    '2616354c5df6741cfc14def90f6d94c0',
    '027ae9f2f81207772984ea478c24d347',
    'e7d49117f76e51db7e4b27c5ad93c2ec',
    'd246646c66ea921f3206f8a8f6367334',
    '1f09e9eaa75cf1975bc2bbe1e1bf02f4',
    '9ac1dbe5e7cf648ddcd0073d9454c520',
    '7eb3ee4875503326ea2d42d2561777fc',
    '93d9ce9f20b9aba478c4a7e5c603b76f',
    'ee3f19f89f951a92996a76106f7cce80',
    '0a614f4d7177f612d609ab3fba4d0752',
    '566999d8528ddf17465f637729a80e14',
    '65e6c73a647fcf79f5c1732eee339bfe',
    '2ef14d919e6d4f8f19d32306a0c2d19e',
    '5719aa9c4660a8171849cc940da37df6',
    '3eb76b9d76f0ece19caf361df9cfbcc2',
    'dcfd5ab1e4228695954134b730d2241a',
    'bc01e0c95b88bd5eaf33d39afd968eb3',
    'cf482603cf07fff51608d13b8c687a3a',
    'a281817d9e100fcb37e9ccadd3fb00fa',
    'c81de7d024bb732e68f8c82686d4fc70'
)

function Open-BitmapDetached {
    param([string]$Path)
    $bytes = [IO.File]::ReadAllBytes($Path)
    $stream = [IO.MemoryStream]::new($bytes, $false)
    try {
        $loaded = [System.Drawing.Bitmap]::new($stream)
        try { return [System.Drawing.Bitmap]::new($loaded) }
        finally { $loaded.Dispose() }
    }
    finally { $stream.Dispose() }
}

function Save-JpegAtomic {
    param(
        [System.Drawing.Image]$Image,
        [string]$Path,
        [long]$Quality = 98
    )
    $codec = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() |
        Where-Object MimeType -eq 'image/jpeg' |
        Select-Object -First 1
    $temporaryPath = Join-Path (Split-Path -Parent $Path) (
        '.' + [IO.Path]::GetFileName($Path) + '.' + [guid]::NewGuid().ToString('N') + '.tmp')
    $parameters = [System.Drawing.Imaging.EncoderParameters]::new(1)
    $parameters.Param[0] = [System.Drawing.Imaging.EncoderParameter]::new(
        [System.Drawing.Imaging.Encoder]::Quality,
        $Quality)
    try {
        $Image.Save($temporaryPath, $codec, $parameters)
        [IO.File]::Copy($temporaryPath, $Path, $true)
    }
    finally {
        $parameters.Dispose()
        if (Test-Path -LiteralPath $temporaryPath) { Remove-Item -LiteralPath $temporaryPath -Force }
    }
}

if ($Integrate) {
    New-Item -ItemType Directory -Force -Path $CatalogImagesDirectory, $OrganizedDirectory | Out-Null
}

foreach ($id in $ids) {
    $sourcePath = Join-Path $GeneratedDirectory "$id.png"
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        throw "Fonte PSP aprovada ausente: $sourcePath"
    }
    $image = Open-BitmapDetached -Path $sourcePath
    try {
        if ($image.Width -ne 1024 -or $image.Height -ne 1536) {
            throw "Dimensão inválida em ${sourcePath}: $($image.Width)x$($image.Height)"
        }
        if ($Integrate) {
            $catalogPath = Join-Path $CatalogImagesDirectory "$id.jpg"
            $organizedPath = Join-Path $OrganizedDirectory "$id.jpg"
            Save-JpegAtomic -Image $image -Path $catalogPath
            [IO.File]::Copy($catalogPath, $organizedPath, $true)
        }
    }
    finally { $image.Dispose() }
}

Write-Host 'Fontes PSP aprovadas: 21/21 em 1024x1536.'
Write-Host 'Nenhuma arte foi recortada, ampliada, substituída ou recomposta.'
if ($Integrate) {
    Write-Host "Catálogo atualizado: $CatalogImagesDirectory"
    Write-Host "Pasta organizada atualizada: $OrganizedDirectory"
}
