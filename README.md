# TURBORAMA SUITE

Central desktop para organizar sistemas, biblioteca e downloads com identidade visual Turborama.

![Tela de login da Turborama](docs/images/login.png)

## Estado atual

- aplicativo WPF para Windows 10/11 x64;
- tela de acesso e interface Turborama;
- catálogo reutilizável com 22 categorias;
- busca sem diferenciação de acentos;
- quatro cards por página e paginação;
- seleção de pastas, Biblioteca e Downloads;
- ícone próprio multirresolução;
- publicação autocontida, sem exigir instalação separada do .NET.

A autenticação e as origens de download ainda são demonstrativas. Nesta versão, qualquer chave não vazia abre a interface e os cards sem URL exibem um aviso sem iniciar downloads. A validação real deverá ser conectada ao servidor Turborama.

## Download

O pacote pronto para Windows está em [Releases — Turborama 1.5.0](https://github.com/luziellacerda/TRUBORAMA-SUITE/releases/tag/v1.5.0).

## Compilar

Requisitos: Windows e .NET SDK 8.

```powershell
dotnet restore .\TurboBoxManager.csproj
dotnet build .\TurboBoxManager.csproj -c Release
```

## Validar o catálogo

```powershell
dotnet run --project .\tests\CatalogVerifier\CatalogVerifier.csproj -c Release -- .\Assets\Catalog\catalog.json
```

O verificador confere as 22 categorias, as contagens auditadas, busca e paginação.

## Publicar

```powershell
dotnet restore .\TurboBoxManager.csproj -r win-x64
dotnet publish .\TurboBoxManager.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -o .\publish
```

Mantenha `Assets/Catalog/catalog.json` junto ao executável publicado. O catálogo permanece externo para permitir atualização posterior.

## Origem do projeto

O repositório contém código, interface e materiais visuais próprios da Turborama. Não contém código decompilado, credenciais, URLs privadas ou assets extraídos de outros programas.
