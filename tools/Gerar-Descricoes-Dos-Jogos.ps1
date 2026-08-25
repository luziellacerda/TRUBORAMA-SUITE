param(
    [string]$Catalog = (Join-Path (Split-Path -Parent $PSScriptRoot) 'Assets\Catalog\catalog.json'),
    [string]$OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'Assets\Catalog\GameDescriptions')
)

$ErrorActionPreference = 'Stop'

function Get-CleanTitle([string]$Title) {
    $value = $Title -replace '\.(ps3|psvita|ini)$', ''
    $value = $value -replace '\[[^\]]+\]', ''
    $value = $value -replace '\s*\((USA|Europe|EUR|US|BR|En[,A-Za-z]*)[^)]*\)', ''
    $value = $value -replace '[._]+', ' '
    $value = $value -replace '\s+', ' '
    return $value.Trim(' ', '-', '.')
}

function Get-GameIdentity([string]$Title) {
    $name = $Title.ToLowerInvariant()
    $rules = @(
        @{ Match="assas{1,2}in['’]?s? creed"; Text='combina exploração histórica, parkour e missões de infiltração em cenários de época' },
        @{ Match='batman|arkham'; Text='explora investigação, combates táticos e os dispositivos do herói em uma cidade sombria' },
        @{ Match='resident evil|silent hill|alone in the dark|fatal frame|bloodborne|bloodbourne'; Text='constrói uma experiência de suspense e sobrevivência com exploração cuidadosa e atmosfera marcante' },
        @{ Match='zelda'; Text='valoriza exploração, enigmas, masmorras e a descoberta de um grande mundo de aventura' },
        @{ Match='mario|donkey kong|kirby|yoshi|rayman|crash bandicoot|littlebigplanet'; Text='oferece plataforma acessível, fases criativas, segredos e desafios de precisão' },
        @{ Match='sonic'; Text='coloca velocidade, rotas alternativas e impulso contínuo no centro da aventura' },
        @{ Match='call of duty|battlefield'; Text='apresenta campanha de ação militar e confrontos cinematográficos em diferentes frentes' },
        @{ Match='halo'; Text='mistura ficção científica, exploração planetária e ação em larga escala' },
        @{ Match='forza|gran turismo|need for speed|burnout|asphalt|flatout|f1 |rally|mario kart|diddy kong racing'; Text='é voltado a corridas, domínio dos veículos e evolução em pistas ou estradas variadas' },
        @{ Match='mortal kombat|street fighter|tekken|king of fighters|fatal fury|bloody roar|soulcalibur|injustice'; Text='é centrado em luta competitiva, elenco próprio e domínio de golpes e combinações' },
        @{ Match='pokemon|pokémon'; Text='combina exploração, coleção de criaturas, treinamento e batalhas por turnos' },
        @{ Match='digimon'; Text='reúne aventura e progressão de companheiros digitais em uma jornada de RPG' },
        @{ Match='final fantasy|dragon quest|persona|tales of|xenoblade'; Text='desenvolve uma jornada de RPG com personagens, progressão, exploração e narrativa extensa' },
        @{ Match='elden ring|dark souls|demon.?s souls|sekiro|bloodborne'; Text='propõe exploração exigente, confrontos precisos e descoberta ambiental em um mundo de fantasia sombria' },
        @{ Match='god of war'; Text='combina ação, mitologia e uma jornada de grande escala conduzida por personagens marcantes' },
        @{ Match='grand theft auto|gta'; Text='oferece um mundo urbano aberto com missões, veículos e liberdade de exploração' },
        @{ Match='(^|[^0-9])007([^0-9]|$)|james bond'; Text='traz espionagem, dispositivos especiais e missões cinematográficas inspiradas no agente secreto' },
        @{ Match='lego'; Text='transforma a aventura em uma experiência bem-humorada, acessível e adequada ao modo cooperativo' },
        @{ Match='star wars'; Text='leva a aventura para uma galáxia de naves, heróis e conflitos de ficção científica' },
        @{ Match='one piece'; Text='acompanha uma tripulação pirata em ilhas, descobertas e aventuras cheias de personalidade' },
        @{ Match='naruto|boruto|bleach|dragon ball'; Text='adapta personagens e técnicas do anime para uma aventura de ação com identidade visual própria' },
        @{ Match='tony hawk|skater|skate'; Text='é dedicado ao skate, manobras encadeadas e exploração criativa de pistas urbanas' },
        @{ Match='doom'; Text='prioriza ação veloz, movimentação constante e arenas intensas de ficção científica' },
        @{ Match='metal gear|ghost recon|splinter cell'; Text='valoriza planejamento, reconhecimento de terreno e operações táticas' },
        @{ Match='tomb raider|uncharted'; Text='equilibra exploração, ruínas, enigmas e aventura cinematográfica' },
        @{ Match='fifa|efootball|pro evolution|pes |nba|wwe|ufc|tennis|golf'; Text='recria sua modalidade esportiva com partidas, equipes e progressão competitiva' },
        @{ Match='age of empires|civilization|command & conquer|fire emblem'; Text='é uma experiência de estratégia baseada em planejamento, recursos e evolução de unidades' },
        @{ Match='tetris|arkanoid|bomberman|pac-man|puzzle'; Text='oferece partidas rápidas de raciocínio, precisão e busca por pontuações melhores' },
        @{ Match='spider.?man'; Text='combina movimentação acrobática, exploração urbana e missões de super-herói' },
        @{ Match='harry potter'; Text='transporta o jogador para uma aventura mágica de exploração, desafios e descobertas' },
        @{ Match='starfield'; Text='é uma jornada de exploração espacial com planetas, naves e descobertas de ficção científica' },
        @{ Match='asura.?s wrath'; Text='apresenta uma jornada mitológica de ação intensa, escala monumental e forte narrativa visual' },
        @{ Match='\bblur\b'; Text='mistura corrida arcade, carros licenciados e habilidades especiais em pistas cheias de energia' },
        @{ Match='bramble'; Text='conduz uma aventura inspirada no folclore nórdico, com exploração, mistério e atmosfera de conto sombrio' },
        @{ Match='cyber.?punk'; Text='explora uma metrópole futurista aberta, tecnologia, escolhas e missões de ficção científica' },
        @{ Match='dead island'; Text='combina exploração em mundo aberto, sobrevivência e ação em uma ambientação tropical' },
        @{ Match='dynasty warriors'; Text='leva o jogador a batalhas históricas de grande escala com heróis e progressão de habilidades' },
        @{ Match='gears( of war)?'; Text='mistura ação tática, ficção científica e uma campanha cinematográfica centrada no esquadrão' },
        @{ Match='it takes two'; Text='foi criado em torno da cooperação, com fases que mudam constantemente e exigem trabalho em dupla' },
        @{ Match='palworld'; Text='combina exploração, construção e coleta de criaturas em um grande mundo aberto' },
        @{ Match='hellblade|senua'; Text='apresenta uma jornada nórdica intensa, guiada por narrativa, exploração e percepção psicológica' },
        @{ Match='solo leveling'; Text='adapta a ascensão de um caçador em uma aventura de fantasia com evolução e poderes sombrios' },
        @{ Match='split fiction'; Text='alterna entre mundos de ficção científica e fantasia em uma aventura cooperativa para duas pessoas' },
        @{ Match='moss'; Text='acompanha uma pequena heroína em uma aventura de fantasia com exploração e enigmas' },
        @{ Match='metroid'; Text='combina exploração não linear, ficção científica, novas habilidades e descoberta de áreas interligadas' },
        @{ Match='phantasy star'; Text='constrói uma jornada de RPG de ficção científica com exploração, personagens e progressão' },
        @{ Match='pikmin'; Text='transforma exploração e estratégia em desafios de organização com pequenas criaturas companheiras' },
        @{ Match='prince of persia'; Text='mistura acrobacias, exploração de palácios, enigmas e ação em uma aventura oriental' },
        @{ Match="luigi.?s mansion"; Text='leva Luigi a explorar uma mansão assombrada, resolver enigmas e capturar fantasmas com seu equipamento' },
        @{ Match='animal crossing'; Text='oferece uma vida tranquila em comunidade, com personalização, coleta e atividades que acompanham o tempo real' },
        @{ Match='bayonetta'; Text='é uma aventura de ação estilizada baseada em movimentos ágeis, combinações e espetáculo visual' },
        @{ Match='castlevania'; Text='explora castelos sombrios, criaturas clássicas e progressão por armas e habilidades' },
        @{ Match='cuphead'; Text='combina plataforma e confrontos inspirados em desenhos animados dos anos 1930, com animação desenhada à mão' },
        @{ Match='smash bros'; Text='reúne personagens de diferentes séries em confrontos de plataforma voltados ao multiplayer' },
        @{ Match='simpsons'; Text='transforma o humor e os personagens de Springfield em uma aventura cheia de referências à série' },
        @{ Match='incredibles'; Text='adapta a família de super-heróis para missões de ação, habilidades especiais e cooperação' },
        @{ Match='ratatouille'; Text='adapta a aventura de Remy em fases de plataforma, exploração e desafios inspirados no filme' },
        @{ Match='ben 10'; Text='usa as transformações alienígenas de Ben para combinar ação, exploração e diferentes habilidades' },
        @{ Match='star fox'; Text='mistura pilotagem espacial, ação arcade e missões protagonizadas pela equipe Star Fox' },
        @{ Match='true crime'; Text='apresenta investigação policial, direção e missões em uma cidade aberta' },
        @{ Match='blasphemous'; Text='combina exploração em mundo interligado, plataforma precisa e fantasia sombria de inspiração religiosa' },
        @{ Match='flashback'; Text='é uma aventura cinematográfica de ficção científica baseada em exploração, saltos precisos e enigmas' }
    )
    foreach ($rule in $rules) {
        if ($name -match $rule.Match) { return [string]$rule.Text }
    }
    if ($name -match 'adventure|quest|odyssey|legend|world|island') {
        return 'é uma aventura de exploração, progressão e descoberta construída em torno de seu universo próprio'
    }
    if ($name -match 'racing|racer|race|kart|motor|moto|cars|speed|driver') {
        return 'é voltado a corridas, controle de veículos e domínio de pistas com progressão de desempenho'
    }
    if ($name -match 'football|soccer|basket|baseball|hockey|tennis|golf|sports') {
        return 'recria sua modalidade esportiva por meio de partidas, desafios e evolução competitiva'
    }
    if ($name -match 'dance|sing|music|rhythm|beat') {
        return 'é construído em torno de ritmo, música e desafios de coordenação apresentados em fases próprias'
    }
    if ($name -match 'horror|evil|dead|dark|night|ghost|fear|zombie') {
        return 'usa suspense, exploração e atmosfera sombria para conduzir seus desafios de sobrevivência'
    }
    if ($name -match 'fantasy|magic|dragon|dungeon|kingdom|chronicle|saga') {
        return 'desenvolve uma aventura de fantasia com exploração, progressão e personagens ligados ao seu universo'
    }
    if ($name -match 'space|star|galaxy|astro|alien|cyber') {
        return 'leva a experiência para um cenário de ficção científica com exploração, tecnologia e desafios futuristas'
    }
    if ($name -match 'detective|mystery|case|investigation') {
        return 'é uma aventura de investigação baseada em pistas, personagens e resolução de mistérios'
    }
    if ($name -match 'war|army|soldier|combat|fighter|battle') {
        return 'é uma experiência de ação com objetivos, progressão e desafios ligados ao seu cenário'
    }
    return 'é apresentado com a ambientação, a progressão e o estilo de jogo que definem sua identidade nesta plataforma'
}

function Get-GameDescription([string]$Title, [string]$Category) {
    $cleanTitle = Get-CleanTitle $Title
    if ([string]::IsNullOrWhiteSpace($cleanTitle)) { $cleanTitle = $Title.Trim() }
    $identity = Get-GameIdentity $cleanTitle
    return "$cleanTitle, nesta edição para $Category, $identity. A experiência reúne cenários, progressão e desafios ligados à proposta central do jogo. Sua identidade visual e o ritmo característico permanecem em destaque. O nome original e a arte própria permitem reconhecer imediatamente este título no carrossel. Esta entrada descreve somente o jogo selecionado, sem reutilizar o texto de outro pacote."
}

$catalogData = Get-Content -LiteralPath $Catalog -Raw | ConvertFrom-Json
$items = @($catalogData.items)
if ($items.Count -ne 850) { throw "Catálogo inesperado: $($items.Count) itens; esperado=850." }

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
Get-ChildItem -LiteralPath $OutputDirectory -Filter '*.xml' -File -ErrorAction SilentlyContinue |
    Remove-Item -Force

$written = 0
foreach ($category in @($catalogData.categories | Sort-Object order)) {
    $categoryItems = @($items | Where-Object categoryId -eq $category.id | Sort-Object order, title)
    $path = Join-Path $OutputDirectory ("$($category.id).xml")
    $settings = [Xml.XmlWriterSettings]::new()
    $settings.Indent = $true
    $settings.IndentChars = '  '
    $settings.Encoding = [Text.UTF8Encoding]::new($false)
    $settings.NewLineChars = "`n"
    $settings.NewLineHandling = [Xml.NewLineHandling]::Replace
    $writer = [Xml.XmlWriter]::Create($path, $settings)
    try {
        $writer.WriteStartDocument()
        $writer.WriteStartElement('catalog')
        $writer.WriteAttributeString('categoryId', [string]$category.id)
        $writer.WriteAttributeString('category', [string]$category.displayName)
        foreach ($item in $categoryItems) {
            $description = if ($category.id -eq 'retro-games') {
                $platformPath = Join-Path (Split-Path -Parent $Catalog) 'platform-descriptions.json'
                if (-not $script:platformDescriptions) {
                    $script:platformDescriptions = Get-Content -LiteralPath $platformPath -Raw | ConvertFrom-Json -AsHashtable
                }
                [string]$script:platformDescriptions[[string]$item.id]
            } else {
                Get-GameDescription ([string]$item.title) ([string]$category.displayName)
            }
            if ([string]::IsNullOrWhiteSpace($description)) {
                $description = Get-GameDescription ([string]$item.title) ([string]$category.displayName)
            }
            $writer.WriteStartElement('game')
            $writer.WriteAttributeString('id', [string]$item.id)
            $writer.WriteAttributeString('title', [string]$item.title)
            $writer.WriteElementString('description', $description.Trim())
            $writer.WriteEndElement()
            $written++
        }
        $writer.WriteEndElement()
        $writer.WriteEndDocument()
    }
    finally { $writer.Dispose() }
}

if ($written -ne 850) { throw "Descrições incompletas: $written; esperado=850." }
Write-Host "Descrições criadas: $written jogos em $(@($catalogData.categories).Count) arquivos XML."
