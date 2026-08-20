# TURBORAMA SUITE

Central desktop WPF para organizar sistemas retrô, catálogo, biblioteca e downloads com identidade visual Turborama.

![Tela de login da Turborama](docs/images/login.png)

## Versão 1.6.0

- Windows 10/11 x64;
- login e interface Turborama;
- 22 categorias e 850 itens identificados individualmente;
- capas 1200×900 compostas no padrão Turborama;
- busca sem diferenciação de acentos;
- quatro cards por página e paginação compacta;
- fila, progresso, cancelamento e histórico de downloads;
- verificação de tamanho e SHA-256 antes de concluir;
- publicação autocontida, sem exigir instalação separada do .NET.

O acesso ainda é demonstrativo: qualquer chave não vazia abre a interface. Os 850 botões de download estão ativos para teste e baixam somente um arquivo público Turborama de 92 bytes. Eles não baixam, extraem nem executam jogos. A validação de licença e as origens autorizadas de produção devem ser conectadas ao servidor Turborama.

## Download para teste

Baixe o pacote pronto em [Releases — Turborama 1.6.0](https://github.com/luziellacerda/TRUBORAMA-SUITE/releases/tag/v1.6.0) e confira o SHA-256 publicado junto ao ZIP.

As 850 capas completas acompanham o pacote da Release. Para evitar 118 MiB de binários gerados em cada revisão do Git, o repositório mantém somente o fallback global e o manifesto do catálogo.

## Compilar

Requisitos: Windows e .NET SDK 8.

```powershell
dotnet restore .\TurboBoxManager.csproj --source https://api.nuget.org/v3/index.json
dotnet build .\TurboBoxManager.csproj -c Release --no-restore
```

## Validar o catálogo

```powershell
dotnet run --project .\tests\CatalogVerifier\CatalogVerifier.csproj -c Release -- .\Assets\Catalog\catalog.json
```

O verificador confere as 22 categorias, 850 itens, busca, paginação, fallback visual e o download público seguro.

## Publicar

```powershell
dotnet restore .\TurboBoxManager.csproj -r win-x64 --source https://api.nuget.org/v3/index.json
dotnet publish .\TurboBoxManager.csproj -c Release -r win-x64 --self-contained true --no-restore -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -o .\publish
```

Mantenha `Assets/Catalog/catalog.json` e `Assets/Catalog/Images` junto ao executável publicado. Esses arquivos permanecem externos para permitir atualização posterior do catálogo.

## Segurança do pacote

O manifesto público não contém credenciais nem URLs privadas de download. Cada download permitido usa HTTPS, lista de hosts autorizados, arquivo parcial, limite de tamanho, caminho canônico e verificação SHA-256. As capas são composições Turborama vinculadas aos nomes fornecidos para este projeto.
