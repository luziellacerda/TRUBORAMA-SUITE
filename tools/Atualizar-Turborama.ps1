param(
    [string]$Drawers = "$([Environment]::GetFolderPath('Desktop'))\drawers.json",
    [string]$Config = "$([Environment]::GetFolderPath('Desktop'))\config.json",
    [string]$Key = "$([Environment]::GetFolderPath('Desktop'))\key.txt",
    [string]$BackgroundVideo = "$([Environment]::GetFolderPath('UserProfile'))\Downloads\videoplayback-ps3.mp4",
    [string]$SystemToolsBackgroundVideo = "$([Environment]::GetFolderPath('UserProfile'))\Downloads\sistemas e utilitarios.mp4",
    [string]$Ps2BackgroundVideo = "$([Environment]::GetFolderPath('UserProfile'))\Downloads\videoplayback-ps2.mp4",
    [string]$Ps4BackgroundVideo = "$([Environment]::GetFolderPath('UserProfile'))\Downloads\videoplayback-ps4.mp4",
    [string]$Ps5BackgroundVideo = "$([Environment]::GetFolderPath('UserProfile'))\Downloads\videoplayback-ps5.mp4",
    [string]$PspBackgroundVideo = "$([Environment]::GetFolderPath('UserProfile'))\Downloads\videoplayback-psp.mp4",
    [string]$PsVitaBackgroundVideo = "$([Environment]::GetFolderPath('UserProfile'))\Downloads\videoplayback-psvita.mp4",
    [string]$SegaSaturnBackgroundVideo = "$([Environment]::GetFolderPath('UserProfile'))\Downloads\videoplayback-saturn.mp4",
    [string]$XboxOneBackgroundVideo = "$([Environment]::GetFolderPath('UserProfile'))\Downloads\videoplayback-xboxonex.mp4",
    [string]$NintendoBackgroundVideo = "$([Environment]::GetFolderPath('UserProfile'))\Downloads\videoplayback-swith.mp4",
    [string]$NintendoWiiBackgroundVideo = "$([Environment]::GetFolderPath('UserProfile'))\Downloads\videoplayback-nintendo-wii.mp4",
    [string]$WindowsBackgroundVideo = "$([Environment]::GetFolderPath('UserProfile'))\Downloads\videoplayback-windows.mp4",
    [string]$RetroBackgroundVideo = "$([Environment]::GetFolderPath('UserProfile'))\Downloads\videoplayback-retro.mp4"
)

$ErrorActionPreference = 'Stop'
$localNugetPackages = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget\packages'
if (Test-Path -LiteralPath $localNugetPackages -PathType Container) {
    $env:NUGET_PACKAGES = $localNugetPackages
}
$repo = Split-Path -Parent $PSScriptRoot
$catalogPath = Join-Path $repo 'Assets\Catalog\catalog.json'
$project = Join-Path $repo 'TurboBoxManager.csproj'
$outputDirectory = Join-Path $repo 'dist-private'
$output = Join-Path $outputDirectory 'Turborama-Completo-850-Links.exe'
$backgroundVideoOutput = Join-Path $outputDirectory 'Turborama-background.mp4'
$systemToolsBackgroundVideoOutput = Join-Path $outputDirectory 'Turborama-background-system-tools.mp4'
$ps2BackgroundVideoOutput = Join-Path $outputDirectory 'Turborama-background-ps2.mp4'
$ps4BackgroundVideoOutput = Join-Path $outputDirectory 'Turborama-background-ps4.mp4'
$ps5BackgroundVideoOutput = Join-Path $outputDirectory 'Turborama-background-ps5.mp4'
$pspBackgroundVideoOutput = Join-Path $outputDirectory 'Turborama-background-psp.mp4'
$psVitaBackgroundVideoOutput = Join-Path $outputDirectory 'Turborama-background-ps-vita.mp4'
$segaSaturnBackgroundVideoOutput = Join-Path $outputDirectory 'Turborama-background-sega-saturn.mp4'
$xboxOneBackgroundVideoOutput = Join-Path $outputDirectory 'Turborama-background-xbox-one-x.mp4'
$nintendoBackgroundVideoOutput = Join-Path $outputDirectory 'Turborama-background-nintendo.mp4'
$nintendoWiiBackgroundVideoOutput = Join-Path $outputDirectory 'Turborama-background-nintendo-wii.mp4'
$windowsBackgroundVideoOutput = Join-Path $outputDirectory 'Turborama-background-windows.mp4'
$retroBackgroundVideoOutput = Join-Path $outputDirectory 'Turborama-background-retro.mp4'
$systemVideoSource = Join-Path $repo 'Assets\Catalog\SystemVideos'
$systemVideoOutput = Join-Path $outputDirectory 'Turborama-system-videos'
$systemVideoManifest = Join-Path $systemVideoSource 'system-videos.json'
$systemVideoIntegrity = Join-Path $systemVideoSource 'system-video-integrity.json'
$platformDescriptions = Join-Path $repo 'Assets\Catalog\platform-descriptions.json'
$gameDescriptionsDirectory = Join-Path $repo 'Assets\Catalog\GameDescriptions'

function Require-File([string]$Path, [string]$Label) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Label não encontrado: $Path"
    }
}

function Get-BuildWorkRoot {
    $drive = Get-PSDrive -PSProvider FileSystem |
        Where-Object { $_.Free -gt 2GB } |
        Sort-Object Free -Descending |
        Select-Object -First 1
    if ($null -eq $drive) {
        throw 'Não há pelo menos 2 GB livres para a compilação temporária.'
    }
    $parent = Join-Path $drive.Root 'CodexTemp\TurboramaBuild'
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    return Join-Path $parent ("TurboramaUpdate-" + [Guid]::NewGuid().ToString('N'))
}

function New-PrivateCatalogBundle(
    [string]$CatalogJsonPath,
    [string]$KeyPath,
    [string]$ResourcePath,
    [string]$GeneratedSourcePath
) {
    $keyText = [IO.File]::ReadAllText($KeyPath).Trim()
    if ([string]::IsNullOrWhiteSpace($keyText)) { throw 'key.txt está vazio.' }

    $keyTextBytes = [Text.Encoding]::UTF8.GetBytes($keyText)
    $catalogBytes = [IO.File]::ReadAllBytes($CatalogJsonPath)
    $plain = New-Object byte[] (4 + $keyTextBytes.Length + $catalogBytes.Length)
    [Buffer]::BlockCopy([BitConverter]::GetBytes([int]$keyTextBytes.Length), 0, $plain, 0, 4)
    [Buffer]::BlockCopy($keyTextBytes, 0, $plain, 4, $keyTextBytes.Length)
    [Buffer]::BlockCopy($catalogBytes, 0, $plain, 4 + $keyTextBytes.Length, $catalogBytes.Length)

    $aesKey = New-Object byte[] 32
    $nonce = New-Object byte[] 12
    $tag = New-Object byte[] 16
    $ciphertext = New-Object byte[] $plain.Length
    [Security.Cryptography.RandomNumberGenerator]::Fill($aesKey)
    [Security.Cryptography.RandomNumberGenerator]::Fill($nonce)
    $additionalData = [Text.Encoding]::ASCII.GetBytes('TURBORAMA-PRIVATE-CATALOG-V1')
    $aes = [Security.Cryptography.AesGcm]::new($aesKey, 16)
    try { $aes.Encrypt($nonce, $plain, $ciphertext, $tag, $additionalData) }
    finally {
        $aes.Dispose()
        [Security.Cryptography.CryptographicOperations]::ZeroMemory($plain)
        [Security.Cryptography.CryptographicOperations]::ZeroMemory($keyTextBytes)
        [Security.Cryptography.CryptographicOperations]::ZeroMemory($catalogBytes)
    }

    $magic = [Text.Encoding]::ASCII.GetBytes('TRBCAT01')
    $bundle = New-Object byte[] ($magic.Length + $nonce.Length + $tag.Length + $ciphertext.Length)
    [Buffer]::BlockCopy($magic, 0, $bundle, 0, $magic.Length)
    [Buffer]::BlockCopy($nonce, 0, $bundle, $magic.Length, $nonce.Length)
    [Buffer]::BlockCopy($tag, 0, $bundle, $magic.Length + $nonce.Length, $tag.Length)
    [Buffer]::BlockCopy($ciphertext, 0, $bundle, $magic.Length + $nonce.Length + $tag.Length, $ciphertext.Length)
    [IO.File]::WriteAllBytes($ResourcePath, $bundle)

    $keyLiterals = ($aesKey | ForEach-Object { '0x{0:X2}' -f $_ }) -join ', '
    $generatedSource = @"
namespace TurboBoxManager.Catalog;

internal static class PrivateCatalogSecrets
{
    internal static byte[] CreateKey() => [$keyLiterals];
}
"@
    [IO.File]::WriteAllText($GeneratedSourcePath, $generatedSource, [Text.UTF8Encoding]::new($false))
    [Security.Cryptography.CryptographicOperations]::ZeroMemory($aesKey)
    [Security.Cryptography.CryptographicOperations]::ZeroMemory($ciphertext)
    [Security.Cryptography.CryptographicOperations]::ZeroMemory($bundle)
}

function Test-BinaryContains([string]$Path, [string]$Text) {
    $needle = [Text.Encoding]::ASCII.GetBytes($Text)
    $stream = [IO.File]::OpenRead($Path)
    try {
        $buffer = New-Object byte[] (1024 * 1024 + $needle.Length)
        $carry = 0
        while (($read = $stream.Read($buffer, $carry, 1024 * 1024)) -gt 0) {
            $length = $carry + $read
            for ($index = 0; $index -le $length - $needle.Length; $index++) {
                $matches = $true
                for ($offset = 0; $offset -lt $needle.Length; $offset++) {
                    if ($buffer[$index + $offset] -ne $needle[$offset]) { $matches = $false; break }
                }
                if ($matches) { return $true }
            }
            $carry = [Math]::Min($needle.Length - 1, $length)
            if ($carry -gt 0) { [Buffer]::BlockCopy($buffer, $length - $carry, $buffer, 0, $carry) }
        }
        return $false
    }
    finally { $stream.Dispose() }
}

function Test-SystemVideoAssets {
    Require-File $systemVideoManifest 'Mapa de vídeos por sistema'
    Require-File $systemVideoIntegrity 'Integridade dos vídeos por sistema'
    Require-File $platformDescriptions 'Descrições das plataformas'
    $videoMap = @{}
    $integrityMap = @{}
    $descriptionMap = @{}
    (Get-Content -LiteralPath $systemVideoManifest -Raw | ConvertFrom-Json).PSObject.Properties |
        ForEach-Object { $videoMap[$_.Name] = $_.Value }
    (Get-Content -LiteralPath $systemVideoIntegrity -Raw | ConvertFrom-Json).PSObject.Properties |
        ForEach-Object { $integrityMap[$_.Name] = $_.Value }
    (Get-Content -LiteralPath $platformDescriptions -Raw | ConvertFrom-Json).PSObject.Properties |
        ForEach-Object { $descriptionMap[$_.Name] = $_.Value }
    if ($videoMap.Count -ne 45 -or $descriptionMap.Count -ne 45) {
        throw "Mídia retrô incompleta: vídeos=$($videoMap.Count); descrições=$($descriptionMap.Count); esperado=45."
    }

    $referencedFiles = @($videoMap.Values | Sort-Object -Unique)
    if ($referencedFiles.Count -ne 38 -or $integrityMap.Count -ne 38) {
        throw "Conjunto de vídeos inesperado: referências=$($referencedFiles.Count); integridade=$($integrityMap.Count); esperado=38."
    }

    foreach ($fileName in $referencedFiles) {
        if ([IO.Path]::GetFileName([string]$fileName) -cne [string]$fileName -or
            [IO.Path]::GetExtension([string]$fileName) -cne '.mp4') {
            throw "Nome de vídeo inseguro no mapa: $fileName"
        }
        $videoPath = Join-Path $systemVideoSource ([string]$fileName)
        Require-File $videoPath "Vídeo de sistema $fileName"
        $videoFile = Get-Item -LiteralPath $videoPath
        $expected = $integrityMap[[string]$fileName]
        if ($null -eq $expected -or [long]$expected.length -ne $videoFile.Length) {
            throw "Tamanho ou integridade ausente para $fileName."
        }
        $actualHash = (Get-FileHash -LiteralPath $videoPath -Algorithm SHA256).Hash
        if (-not $actualHash.Equals([string]$expected.sha256, [StringComparison]::OrdinalIgnoreCase)) {
            throw "O vídeo $fileName foi alterado após a preparação."
        }
        $header = New-Object byte[] 12
        $stream = [IO.File]::OpenRead($videoPath)
        try { $read = $stream.Read($header, 0, $header.Length) }
        finally { $stream.Dispose() }
        if ($read -ne 12 -or [Text.Encoding]::ASCII.GetString($header, 4, 4) -cne 'ftyp') {
            throw "O vídeo $fileName não é um MP4 válido."
        }
    }
    return [pscustomobject]@{
        Map = $videoMap
        Files = $referencedFiles
        TotalBytes = ($referencedFiles | ForEach-Object { (Get-Item -LiteralPath (Join-Path $systemVideoSource $_)).Length } | Measure-Object -Sum).Sum
    }
}

function Get-GameDescriptionMap {
    if (-not (Test-Path -LiteralPath $gameDescriptionsDirectory -PathType Container)) {
        throw "Pasta de descrições dos jogos não encontrada: $gameDescriptionsDirectory"
    }
    $map = @{}
    $xmlFiles = @(Get-ChildItem -LiteralPath $gameDescriptionsDirectory -Filter '*.xml' -File | Sort-Object Name)
    if ($xmlFiles.Count -ne 22) {
        throw "Conjunto de descrições inesperado: $($xmlFiles.Count) XML; esperado=22."
    }
    foreach ($xmlFile in $xmlFiles) {
        if ($xmlFile.Length -le 0 -or $xmlFile.Length -gt 256KB) {
            throw "XML de descrições inválido: $($xmlFile.Name)."
        }
        $document = [Xml.Linq.XDocument]::Load($xmlFile.FullName)
        foreach ($game in @($document.Root.Elements('game'))) {
            $id = [string]$game.Attribute('id').Value
            $description = ([string]$game.Element('description').Value).Trim()
            if ([string]::IsNullOrWhiteSpace($id) -or
                [string]::IsNullOrWhiteSpace($description) -or
                $map.ContainsKey($id)) {
                throw "Descrição ausente ou duplicada em $($xmlFile.Name)."
            }
            $map[$id] = $description
        }
    }
    if ($map.Count -ne 850) {
        throw "Descrições incompletas: $($map.Count); esperado=850."
    }
    return $map
}

Require-File $Drawers 'drawers.json'
Require-File $Key 'key.txt'
Require-File $catalogPath 'Catálogo público'
Require-File $BackgroundVideo 'Vídeo universal'
Require-File $SystemToolsBackgroundVideo 'Vídeo de sistemas e utilitários'
Require-File $Ps2BackgroundVideo 'Vídeo do PlayStation 2'
Require-File $Ps4BackgroundVideo 'Vídeo do PlayStation 4'
Require-File $Ps5BackgroundVideo 'Vídeo do PlayStation 5'
Require-File $PspBackgroundVideo 'Vídeo do PSP'
Require-File $PsVitaBackgroundVideo 'Vídeo do PS Vita'
Require-File $SegaSaturnBackgroundVideo 'Vídeo do SEGA Saturn'
Require-File $XboxOneBackgroundVideo 'Vídeo do Xbox One X'
Require-File $NintendoBackgroundVideo 'Vídeo do Nintendo Switch'
Require-File $NintendoWiiBackgroundVideo 'Vídeo do Nintendo Wii'
Require-File $WindowsBackgroundVideo 'Vídeo do Windows'
Require-File $RetroBackgroundVideo 'Vídeo dos jogos retrô'
$systemVideos = Test-SystemVideoAssets
foreach ($platformVideo in @(
    [pscustomobject]@{ Label = 'universal/PlayStation 3'; Path = $BackgroundVideo },
    [pscustomobject]@{ Label = 'Sistemas e utilitários'; Path = $SystemToolsBackgroundVideo },
    [pscustomobject]@{ Label = 'PlayStation 2'; Path = $Ps2BackgroundVideo },
    [pscustomobject]@{ Label = 'PlayStation 4'; Path = $Ps4BackgroundVideo },
    [pscustomobject]@{ Label = 'PlayStation 5'; Path = $Ps5BackgroundVideo },
    [pscustomobject]@{ Label = 'PSP'; Path = $PspBackgroundVideo },
    [pscustomobject]@{ Label = 'PS Vita'; Path = $PsVitaBackgroundVideo },
    [pscustomobject]@{ Label = 'SEGA Saturn'; Path = $SegaSaturnBackgroundVideo },
    [pscustomobject]@{ Label = 'Xbox One X'; Path = $XboxOneBackgroundVideo },
    [pscustomobject]@{ Label = 'Nintendo Switch'; Path = $NintendoBackgroundVideo },
    [pscustomobject]@{ Label = 'Nintendo Wii'; Path = $NintendoWiiBackgroundVideo },
    [pscustomobject]@{ Label = 'Windows'; Path = $WindowsBackgroundVideo },
    [pscustomobject]@{ Label = 'Jogos retrô'; Path = $RetroBackgroundVideo }
)) {
    $videoHeader = New-Object byte[] 12
    $videoStream = [IO.File]::OpenRead($platformVideo.Path)
    try { $videoHeaderLength = $videoStream.Read($videoHeader, 0, $videoHeader.Length) }
    finally { $videoStream.Dispose() }
    if ($videoHeaderLength -ne $videoHeader.Length -or
        [Text.Encoding]::ASCII.GetString($videoHeader, 4, 4) -cne 'ftyp') {
        throw "O vídeo $($platformVideo.Label) não é um MP4 válido: $($platformVideo.Path)"
    }
}

$work = Get-BuildWorkRoot
New-Item -ItemType Directory -Force -Path $work, $outputDirectory | Out-Null
$buildIntermediatePath = (Join-Path $work 'obj') + [IO.Path]::DirectorySeparatorChar
$previousTemp = $env:TEMP
$previousTmp = $env:TMP
$env:TEMP = $work
$env:TMP = $work
try {
    Write-Host '1/4 Preparando os 850 links sem criar catálogo externo...'
    $catalog = Get-Content -LiteralPath $catalogPath -Raw | ConvertFrom-Json
    $gameDescriptions = Get-GameDescriptionMap
    $drawersData = @(Get-Content -LiteralPath $Drawers -Raw | ConvertFrom-Json)
    $links = @{}
    $extractFlags = @{}
    foreach ($drawer in $drawersData) {
        foreach ($file in @($drawer.Files)) {
            if ([string]::IsNullOrWhiteSpace([string]$file.Id) -or
                [string]::IsNullOrWhiteSpace([string]$file.Url) -or
                $links.ContainsKey([string]$file.Id)) { continue }
            $url = [string]$file.Url
            if ($url.StartsWith('ttps://', [StringComparison]::OrdinalIgnoreCase)) { $url = 'h' + $url }
            $uri = $null
            if (-not [Uri]::TryCreate($url, [UriKind]::Absolute, [ref]$uri) -or $uri.Scheme -ne 'https') {
                throw "Link inválido para $($file.Id)."
            }
            $links[[string]$file.Id] = $url
            $extractFlags[[string]$file.Id] = [bool]$file.Extract
        }
    }

    foreach ($item in @($catalog.items)) {
        if (-not $links.ContainsKey([string]$item.id)) {
            throw "Falta link para o jogo: $($item.title) [$($item.id)]"
        }
        $item.downloadUrl = $links[[string]$item.id]
        $uri = [Uri]$item.downloadUrl
        $extension = [IO.Path]::GetExtension($uri.AbsolutePath)
        if ($extension -match '^\.[A-Za-z0-9]{1,9}$') { $item.downloadFileExtension = $extension.ToLowerInvariant() }
        Add-Member -InputObject $item -NotePropertyName 'extract' -NotePropertyValue ([bool]$extractFlags[[string]$item.id]) -Force
        if (-not $gameDescriptions.ContainsKey([string]$item.id)) {
            throw "Falta descrição para o jogo: $($item.title) [$($item.id)]"
        }
        Add-Member -InputObject $item -NotePropertyName 'description' `
            -NotePropertyValue ([string]$gameDescriptions[[string]$item.id]) -Force
        $item.sha256 = ''
    }

    $linkCount = @($catalog.items | Where-Object { $_.downloadUrl }).Count
    $extractCount = @($catalog.items | Where-Object { $_.extract }).Count
    if (@($catalog.categories).Count -ne 22 -or @($catalog.items).Count -ne 850 -or
        $linkCount -ne 850 -or $extractCount -ne 342) {
        throw "Catálogo incompleto: sistemas=$(@($catalog.categories).Count); jogos=$(@($catalog.items).Count); links=$linkCount; extrações=$extractCount."
    }

    $privateCatalogJson = Join-Path $work 'catalog.full.json'
    $privateCatalogResource = Join-Path $work 'private-catalog.bin'
    $privateCatalogKeySource = Join-Path $work 'PrivateCatalogSecrets.g.cs'
    [IO.File]::WriteAllText($privateCatalogJson, ($catalog | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))
    New-PrivateCatalogBundle -CatalogJsonPath $privateCatalogJson -KeyPath $Key `
        -ResourcePath $privateCatalogResource -GeneratedSourcePath $privateCatalogKeySource
    Remove-Item -LiteralPath $privateCatalogJson -Force

    Write-Host '2/4 Compilando o executável direto, sem inicializador e com vídeo universal...'
    $payload = Join-Path $work 'publish'
    $privateProperties = @(
        "-p:PrivateCatalogResourcePath=$privateCatalogResource",
        "-p:PrivateCatalogKeySourcePath=$privateCatalogKeySource",
        '-p:PublishSingleFile=true',
        '-p:PublishReadyToRun=false',
        '-p:SelfContained=false',
        "-p:BaseIntermediateOutputPath=$buildIntermediatePath",
        "-p:MSBuildProjectExtensionsPath=$buildIntermediatePath"
    )
    dotnet restore $project -r win-x64 --source https://api.nuget.org/v3/index.json `
        --ignore-failed-sources @privateProperties
    if ($LASTEXITCODE -ne 0) { throw "Falha ao restaurar as bibliotecas do Turborama ($LASTEXITCODE)." }

    dotnet publish $project -c Release -r win-x64 --self-contained false --no-restore `
        -p:PublishSingleFile=true `
        -p:PublishReadyToRun=false `
        -p:IncludeNativeLibrariesForSelfExtract=false -p:EnableCompressionInSingleFile=false `
        -p:DebugType=None -p:DebugSymbols=false `
        "-p:BaseIntermediateOutputPath=$buildIntermediatePath" `
        "-p:MSBuildProjectExtensionsPath=$buildIntermediatePath" `
        "-p:PrivateCatalogResourcePath=$privateCatalogResource" `
        "-p:PrivateCatalogKeySourcePath=$privateCatalogKeySource" -o $payload
    if ($LASTEXITCODE -ne 0) { throw "Falha ao compilar o Turborama ($LASTEXITCODE)." }

    Write-Host '3/4 Verificando privacidade e ausência de cache do programa...'
    $publishedFiles = @(Get-ChildItem -LiteralPath $payload -Recurse -File)
    $mainExecutable = Join-Path $payload 'Turborama.exe'
    Require-File $mainExecutable 'Executável publicado'
    if ($publishedFiles.Count -ne 1 -or
        $publishedFiles[0].FullName -cne ([IO.Path]::GetFullPath($mainExecutable))) {
        throw "A publicação deveria conter somente Turborama.exe; encontrados $($publishedFiles.Count) arquivos."
    }
    foreach ($privateName in @('catalog.full.json', 'drawers.json', 'config.json', 'key.txt', 'system-videos.json')) {
        if (Get-ChildItem -LiteralPath $payload -Recurse -File -Filter $privateName) {
            throw "Arquivo externo proibido encontrado: $privateName"
        }
    }
    foreach ($addressPattern in @('miami.sambox.buzz/', 'detroit.sambox.club/', 'cucunot.sambox.club/')) {
        if (Test-BinaryContains $mainExecutable $addressPattern) {
            throw "Um endereço privado apareceu sem criptografia no executável: $addressPattern"
        }
    }

    Write-Host '4/4 Copiando a versão final...'
    Copy-Item -LiteralPath $mainExecutable -Destination $output -Force
    Copy-Item -LiteralPath $BackgroundVideo -Destination $backgroundVideoOutput -Force
    Copy-Item -LiteralPath $SystemToolsBackgroundVideo -Destination $systemToolsBackgroundVideoOutput -Force
    Copy-Item -LiteralPath $Ps2BackgroundVideo -Destination $ps2BackgroundVideoOutput -Force
    Copy-Item -LiteralPath $Ps4BackgroundVideo -Destination $ps4BackgroundVideoOutput -Force
    Copy-Item -LiteralPath $Ps5BackgroundVideo -Destination $ps5BackgroundVideoOutput -Force
    Copy-Item -LiteralPath $PspBackgroundVideo -Destination $pspBackgroundVideoOutput -Force
    Copy-Item -LiteralPath $PsVitaBackgroundVideo -Destination $psVitaBackgroundVideoOutput -Force
    Copy-Item -LiteralPath $SegaSaturnBackgroundVideo -Destination $segaSaturnBackgroundVideoOutput -Force
    Copy-Item -LiteralPath $XboxOneBackgroundVideo -Destination $xboxOneBackgroundVideoOutput -Force
    Copy-Item -LiteralPath $NintendoBackgroundVideo -Destination $nintendoBackgroundVideoOutput -Force
    Copy-Item -LiteralPath $NintendoWiiBackgroundVideo -Destination $nintendoWiiBackgroundVideoOutput -Force
    Copy-Item -LiteralPath $WindowsBackgroundVideo -Destination $windowsBackgroundVideoOutput -Force
    Copy-Item -LiteralPath $RetroBackgroundVideo -Destination $retroBackgroundVideoOutput -Force
    $canonicalVideoOutput = [IO.Path]::GetFullPath($systemVideoOutput)
    $canonicalOutputDirectory = [IO.Path]::GetFullPath($outputDirectory) + [IO.Path]::DirectorySeparatorChar
    if ($canonicalVideoOutput.StartsWith($canonicalOutputDirectory, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $canonicalVideoOutput -PathType Container)) {
        Remove-Item -LiteralPath $canonicalVideoOutput -Recurse -Force
    }
    Write-Host "Arquivo: $output"
    Write-Host "Vídeo universal/PlayStation 3: $backgroundVideoOutput"
    Write-Host "Vídeo de sistemas e utilitários: $systemToolsBackgroundVideoOutput"
    Write-Host "Vídeo PlayStation 2: $ps2BackgroundVideoOutput"
    Write-Host "Vídeo PlayStation 4: $ps4BackgroundVideoOutput"
    Write-Host "Vídeo PlayStation 5: $ps5BackgroundVideoOutput"
    Write-Host "Vídeo PSP: $pspBackgroundVideoOutput"
    Write-Host "Vídeo PS Vita: $psVitaBackgroundVideoOutput"
    Write-Host "Vídeo SEGA Saturn: $segaSaturnBackgroundVideoOutput"
    Write-Host "Vídeo Xbox One X: $xboxOneBackgroundVideoOutput"
    Write-Host "Vídeo Nintendo Switch: $nintendoBackgroundVideoOutput"
    Write-Host "Vídeo Nintendo Wii: $nintendoWiiBackgroundVideoOutput"
    Write-Host "Vídeo Windows: $windowsBackgroundVideoOutput"
    Write-Host "Vídeo retrô: $retroBackgroundVideoOutput"
    Write-Host 'Janela demonstrativa individual removida; permanecem somente vídeos de fundo leves por plataforma.'
    Write-Host "SHA-256: $((Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash)"
    Write-Host 'Conteúdo: 22 coleções em carrossel, 850 jogos, 850 textos e 850 links criptografados no EXE; vídeos verificados sem cache do programa.'
    if (Test-Path -LiteralPath $Config -PathType Leaf) {
        Write-Host 'config.json não foi incluído no executável.'
    }
}
finally {
    $env:TEMP = $previousTemp
    $env:TMP = $previousTmp
    $resolvedWork = [IO.Path]::GetFullPath($work)
    $expectedParent = [IO.Path]::GetFullPath((Split-Path -Parent $work))
    if ((Split-Path -Parent $resolvedWork).Equals($expectedParent, [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $resolvedWork).StartsWith('TurboramaUpdate-', [StringComparison]::Ordinal) -and
        (Test-Path -LiteralPath $resolvedWork)) {
        $cleanupComplete = $false
        for ($cleanupAttempt = 1; $cleanupAttempt -le 5 -and -not $cleanupComplete; $cleanupAttempt++) {
            try {
                Remove-Item -LiteralPath $resolvedWork -Recurse -Force -ErrorAction Stop
                $cleanupComplete = $true
            }
            catch {
                if ($cleanupAttempt -lt 5) { Start-Sleep -Milliseconds 600 }
                else { Write-Warning "Não foi possível remover o cache temporário: $resolvedWork" }
            }
        }
    }
}
