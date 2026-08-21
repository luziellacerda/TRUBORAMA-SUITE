param(
    [string]$Drawers = "$([Environment]::GetFolderPath('Desktop'))\drawers.json",
    [string]$Config = "$([Environment]::GetFolderPath('Desktop'))\config.json",
    [string]$Key = "$([Environment]::GetFolderPath('Desktop'))\key.txt"
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$catalogPath = Join-Path $repo 'Assets\Catalog\catalog.json'
$privateCatalogPath = Join-Path $repo 'Assets\Catalog\catalog.full.json'
$project = Join-Path $repo 'TurboBoxManager.csproj'
$template = Join-Path $PSScriptRoot 'SingleExeBootstrapperTemplate'
$outputDirectory = Join-Path $repo 'dist-private'
$output = Join-Path $outputDirectory 'Turborama-Completo-850-Links.exe'
$work = Join-Path ([IO.Path]::GetTempPath()) ("TurboramaUpdate-" + [Guid]::NewGuid().ToString('N'))

function Require-File([string]$Path, [string]$Label) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Label não encontrado: $Path"
    }
}

function Get-ContentTree([string]$Root) {
    [string[]]$paths = Get-ChildItem -LiteralPath $Root -Recurse -File |
        ForEach-Object { $_.FullName }
    [Array]::Sort($paths, [StringComparer]::Ordinal)

    $hash = [Security.Cryptography.IncrementalHash]::CreateHash(
        [Security.Cryptography.HashAlgorithmName]::SHA256)
    [long]$totalBytes = 0
    try {
        foreach ($path in $paths) {
            $relative = [IO.Path]::GetRelativePath($Root, $path).Replace('\', '/')
            $info = [IO.FileInfo]$path
            $totalBytes += $info.Length
            $hash.AppendData([Text.Encoding]::UTF8.GetBytes($relative))
            $hash.AppendData([byte[]]@(0))
            $hash.AppendData([BitConverter]::GetBytes([long]$info.Length))
            $hash.AppendData([byte[]]@(0))
            $stream = [IO.File]::OpenRead($path)
            try {
                $buffer = New-Object byte[] (1024 * 1024)
                while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                    $hash.AppendData($buffer, 0, $read)
                }
            }
            finally { $stream.Dispose() }
        }
        [pscustomobject]@{
            FileCount = $paths.Count
            TotalBytes = $totalBytes
            Sha256 = [Convert]::ToHexString($hash.GetHashAndReset())
        }
    }
    finally { $hash.Dispose() }
}

Require-File $Drawers 'drawers.json'
Require-File $Config 'config.json'
Require-File $Key 'key.txt'
Require-File $catalogPath 'Catálogo público'

New-Item -ItemType Directory -Force -Path $work, $outputDirectory | Out-Null
try {
    Write-Host '1/6 Validando catálogo e links...'
    $catalog = Get-Content -LiteralPath $catalogPath -Raw | ConvertFrom-Json
    $drawersData = @(Get-Content -LiteralPath $Drawers -Raw | ConvertFrom-Json)
    $links = @{}
    foreach ($drawer in $drawersData) {
        foreach ($file in @($drawer.Files)) {
            if ([string]::IsNullOrWhiteSpace([string]$file.Id) -or
                [string]::IsNullOrWhiteSpace([string]$file.Url)) { continue }
            if (-not $links.ContainsKey([string]$file.Id)) {
                $url = [string]$file.Url
                if ($url.StartsWith('ttps://', [StringComparison]::OrdinalIgnoreCase)) {
                    $url = 'h' + $url
                }
                $uri = $null
                if (-not [Uri]::TryCreate($url, [UriKind]::Absolute, [ref]$uri) -or
                    $uri.Scheme -ne 'https') {
                    throw "Link inválido para $($file.Id): $url"
                }
                $links[[string]$file.Id] = $url
            }
        }
    }

    foreach ($item in @($catalog.items)) {
        if (-not $links.ContainsKey([string]$item.id)) {
            throw "Falta link para o jogo: $($item.title) [$($item.id)]"
        }
        $item.downloadUrl = $links[[string]$item.id]
        $uri = [Uri]$item.downloadUrl
        $extension = [IO.Path]::GetExtension($uri.AbsolutePath)
        if ($extension -match '^\.[A-Za-z0-9]{1,9}$') {
            $item.downloadFileExtension = $extension.ToLowerInvariant()
        }
        $item.sha256 = ''
    }
    if (@($catalog.categories).Count -ne 22 -or @($catalog.items).Count -ne 850) {
        throw 'O catálogo esperado precisa ter exatamente 22 sistemas e 850 jogos.'
    }
    [IO.File]::WriteAllText(
        $privateCatalogPath,
        ($catalog | ConvertTo-Json -Depth 20),
        (New-Object Text.UTF8Encoding($false)))

    Write-Host '2/6 Compilando o Turborama e incluindo imagens/bibliotecas...'
    $payload = Join-Path $work 'payload'
    dotnet publish $project -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=None -p:DebugSymbols=false -o $payload
    if ($LASTEXITCODE -ne 0) { throw "Falha ao compilar o Turborama ($LASTEXITCODE)." }

    $data = Join-Path $payload 'Data'
    New-Item -ItemType Directory -Force -Path $data | Out-Null
    Copy-Item -LiteralPath $Config -Destination (Join-Path $data 'config.json') -Force
    Copy-Item -LiteralPath $Key -Destination (Join-Path $data 'key.txt') -Force

    $publishedCatalog = Get-Content -LiteralPath (Join-Path $payload 'Assets\Catalog\catalog.full.json') -Raw |
        ConvertFrom-Json
    $linkCount = @($publishedCatalog.items | Where-Object { $_.downloadUrl }).Count
    $imageCount = @(Get-ChildItem -LiteralPath (Join-Path $payload 'Assets\Catalog\Images') -File).Count
    if ($linkCount -ne 850 -or $imageCount -ne 851) {
        throw "Pacote incompleto: links=$linkCount, imagens=$imageCount."
    }

    Write-Host '3/6 Compactando o conteúdo privado...'
    $payloadZip = Join-Path $work 'payload.zip'
    Compress-Archive -Path (Join-Path $payload '*') -DestinationPath $payloadZip -CompressionLevel Optimal
    $payloadInfo = Get-Item -LiteralPath $payloadZip
    $payloadHash = (Get-FileHash -LiteralPath $payloadZip -Algorithm SHA256).Hash
    $tree = Get-ContentTree $payload
    $mainExeHash = (Get-FileHash -LiteralPath (Join-Path $payload 'Turborama.exe') -Algorithm SHA256).Hash
    $packageVersion = '1.6.1-private-' + (Get-Date -Format 'yyyyMMddHHmmss')

    Write-Host '4/6 Criando o inicializador seguro...'
    $bootstrapper = Join-Path $work 'bootstrapper'
    Copy-Item -LiteralPath $template -Destination $bootstrapper -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $repo 'Assets\turborama-app-icon.ico') `
        -Destination (Join-Path $bootstrapper 'turborama-app-icon.ico') -Force
    $programPath = Join-Path $bootstrapper 'Program.cs'
    $program = [IO.File]::ReadAllText($programPath)
    $replacements = @{
        '__PACKAGE_VERSION__' = $packageVersion
        '__PAYLOAD_LENGTH__' = [string]$payloadInfo.Length
        '__PAYLOAD_SHA256__' = $payloadHash
        '__EXPECTED_FILE_COUNT__' = [string]$tree.FileCount
        '__EXPECTED_CONTENT_BYTES__' = [string]$tree.TotalBytes
        '__EXPECTED_TREE_SHA256__' = $tree.Sha256
        '__EXPECTED_MAIN_EXE_SHA256__' = $mainExeHash
    }
    foreach ($entry in $replacements.GetEnumerator()) {
        $program = $program.Replace($entry.Key, $entry.Value)
    }
    [IO.File]::WriteAllText($programPath, $program, (New-Object Text.UTF8Encoding($false)))
    $launcherPublish = Join-Path $work 'launcher-publish'
    dotnet publish (Join-Path $bootstrapper 'SingleExeBootstrapper.csproj') `
        -c Release -r win-x64 -o $launcherPublish
    if ($LASTEXITCODE -ne 0) { throw "Falha ao compilar o inicializador ($LASTEXITCODE)." }

    Write-Host '5/6 Montando o executável único...'
    $stub = Join-Path $launcherPublish 'Turborama-Launcher.exe'
    $temporaryOutput = Join-Path $work 'Turborama-Completo-850-Links.exe'
    $magic = [Text.Encoding]::ASCII.GetBytes('TURBORAMA-PKG-V1')
    $source = [IO.File]::OpenRead($stub)
    $package = [IO.File]::OpenRead($payloadZip)
    $destination = [IO.File]::Open(
        $temporaryOutput, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $source.CopyTo($destination)
        $package.CopyTo($destination)
        $destination.Write($magic, 0, $magic.Length)
        $length = [BitConverter]::GetBytes([long]$package.Length)
        $destination.Write($length, 0, $length.Length)
        $hashBytes = [Convert]::FromHexString($payloadHash)
        $destination.Write($hashBytes, 0, $hashBytes.Length)
        $destination.Flush($true)
    }
    finally {
        $destination.Dispose()
        $package.Dispose()
        $source.Dispose()
    }
    Copy-Item -LiteralPath $temporaryOutput -Destination $output -Force

    Write-Host '6/6 Concluído e verificado.'
    Write-Host "Arquivo: $output"
    Write-Host "SHA-256: $((Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash)"
    Write-Host 'Conteúdo: 22 sistemas, 850 jogos, 850 links e 851 imagens.'
}
finally {
    if (Test-Path -LiteralPath $privateCatalogPath) {
        Remove-Item -LiteralPath $privateCatalogPath -Force
    }
    $resolvedWork = [IO.Path]::GetFullPath($work)
    $resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if ($resolvedWork.StartsWith($resolvedTemp, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedWork)) {
        Remove-Item -LiteralPath $resolvedWork -Recurse -Force
    }
}
