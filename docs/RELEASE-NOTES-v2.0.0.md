# Turborama Suite 2.0.0 R25

Release estável para Windows 10/11 x64, produzida exclusivamente pelo pipeline Signed da branch `main`.

## Conteúdo

- cliente autocontido `win-x64`;
- integração de produção com licenciamento, sessão, heartbeat e catálogo autorizado;
- catálogo R25 com 22 categorias e 902 itens;
- capas, ícones, vídeos e músicas incorporados no pacote homologado;
- gerenciamento de downloads com retomada e extração controlada;
- inventário local de jogos instalados;
- manifesto de release, SBOM SPDX e avisos de terceiros.

## Integridade e procedência

- `Turborama.exe` possui Authenticode válido e timestamp RFC 3161;
- a tag `v2.0.0` é anotada, assinada por GPG e aponta para o commit exato de `main`;
- `SHA256SUMS-v2.0.0.txt` e `RELEASE-MANIFEST.json` permitem conferir os bytes baixados;
- os assets possuem atestações de proveniência emitidas pelo GitHub Actions;
- somente autoridades públicas são incorporadas no cliente; chaves privadas permanecem fora do repositório e do pacote.

Para verificar a procedência com GitHub CLI:

```text
gh attestation verify Turborama-Suite-v2.0.0-win-x64-SIGNED.zip --repo luziellacerda/TRUBORAMA-SUITE
```

Não distribua builds marcados `UNSIGNED-NOT-FOR-DISTRIBUTION` nem versões `rc` como pacote final.
