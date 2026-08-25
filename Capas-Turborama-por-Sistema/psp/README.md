# PSP — fontes aprovadas

As 21 capas desta pasta são conversões diretas, sem redimensionamento, das fontes 1024 × 1536 aprovadas e versionadas em `sources`. Elas foram copiadas byte a byte do lote validado em `.tmp/psp-generated`. Nenhuma arte principal é recortada, ampliada ou recomposta durante a integração.

O Asphalt — Urban GT 2 permanece apenas como referência visual de proporção, perspectiva, margem e linha de base do case. O processo não copia pixels, paisagens, veículos, títulos ou lombadas do Asphalt para outras capas.

Para validar as fontes e atualizar catálogo e pasta organizada:

```powershell
./tools/Normalize-PspCovers.ps1 -Integrate
```

O processo não gera prancha normalizada nem aplica `Draw-CleanCentralArt`/`Draw-StandardCase`.
