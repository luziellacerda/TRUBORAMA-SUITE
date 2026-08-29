# Capas Turborama por sistema

Esta pasta é o índice versionado das 902 capas do catálogo. Cada subpasta representa exatamente uma categoria de `Assets/Catalog/catalog.json` e contém um `index.json` com título, ID, caminho da imagem, tamanho e SHA-256.

As imagens canônicas continuam em `Assets/Catalog/Images`, evitando duplicar cerca de 500 MB no histórico Git. A subpasta `psp` também contém as 21 capas finais desta etapa porque elas formam o lote visual aprovado e normalizado.

Para regenerar e validar todos os índices:

```powershell
./tools/Organize-CoversBySystem.ps1
```

O arquivo `index.json` desta pasta resume sistemas e quantidades.
