# Publicação final pelo GitHub — Turborama Suite R25

Este documento configura uma única rota de publicação: `.github/workflows/release-final.yml`. O workflow nunca cria tag, nunca publica pré-release e nunca converte staging sem assinatura em versão comercial. Ele publica a release estável somente depois que todos os controles Signed passam.

## 1. Proteções obrigatórias

No GitHub, crie dois ambientes protegidos:

- `production-signing`, que libera somente o build e o PFX para um job sem permissão de escrita no repositório;
- `production-release`, que exige nova aprovação e libera somente a publicação do pacote já validado, sem acesso ao PFX.

Configure nos dois ambientes, conforme a função de cada um:

- ao menos um aprovador obrigatório;
- impedir autoaprovação, quando o plano permitir;
- permitir implantação dos dois ambientes somente pela branch selecionada `main`, sem liberar tags no environment;
- manter a branch `main` sem force push e sem exclusão;
- criar um ruleset separado para `refs/tags/v*`, bloqueando atualização e exclusão de tags finais;
- exigir os workflows `Build and verify` e `CodeQL` antes de promoção;
- habilitar releases imutáveis no repositório, quando disponível.

Os segredos de assinatura ficam somente em `production-signing` e só são entregues ao job depois da aprovação. O ambiente `production-release` não recebe PFX nem outras chaves privadas.

## 2. Segredos do ambiente

Cadastre exatamente estes segredos em `production-signing`:

| Nome | Conteúdo |
|---|---|
| `AUTHENTICODE_PFX_BASE64` | PFX comercial de Code Signing codificado integralmente em Base64 |
| `AUTHENTICODE_PFX_PASSWORD` | senha forte do PFX |
| `RELEASE_TAG_PUBLIC_KEY_BASE64` | chave pública GPG que valida a tag, em Base64 |
| `SUITE_AUTHORITY_ENVELOPE_BASE64` | `suite-authority-envelope.json`, em Base64 |
| `SUITE_AUTHORITY_ISSUER_SPKI_BASE64` | `suite-authority-issuer.spki.der`, em Base64 |
| `CONTENT_AUTHORITY_ENVELOPE_BASE64` | `content-authority-envelope.json`, em Base64 |
| `CONTENT_AUTHORITY_ISSUER_SPKI_BASE64` | `content-authority-issuer.spki.der`, em Base64 |

O PFX precisa conter exatamente um certificado válido, confiável, com chave privada e EKU Code Signing. Certificados autoassinados, de TLS ou EFS não servem. Nenhuma chave privada das autoridades Suite/conteúdo deve ser cadastrada no GitHub.

Para converter uma entrada binária em Base64 sem alterar seus bytes:

```powershell
[Convert]::ToBase64String([IO.File]::ReadAllBytes('C:\caminho\arquivo')) |
  Set-Clipboard
```

## 3. Variáveis públicas pinadas

Cadastre exatamente estas variáveis em `production-signing`:

| Nome | Formato |
|---|---|
| `AUTHENTICODE_CERTIFICATE_THUMBPRINT` | SHA-1 de 40 hex do certificado comercial aprovado |
| `TIMESTAMP_URL` | URL HTTPS de timestamp RFC 3161 |
| `TIMESTAMP_CERTIFICATE_THUMBPRINT` | SHA-1 de 40 hex do certificado do timestamp |
| `RELEASE_TAG_SIGNER_FINGERPRINT` | fingerprint GPG da subchave que assina |
| `RELEASE_TAG_PRIMARY_KEY_FINGERPRINT` | fingerprint GPG da chave primária |
| `RELEASE_TAG_PUBLIC_KEY_SHA256` | SHA-256 da chave pública GPG fornecida |
| `POWERSHELL_SHA256` | SHA-256 do `pwsh.exe` do runner aprovado |
| `POWERSHELL_HOME_TREE_SHA256` | árvore V1 completa do PSHOME aprovado |
| `GIT_SHA256` | SHA-256 do `git.exe` aprovado |
| `GIT_TREE_SHA256` | árvore V1 completa do Git for Windows aprovado |
| `GPG_SHA256` | SHA-256 do `usr\bin\gpg.exe` aprovado |
| `DOTNET_SDK_TREE_SHA256` | árvore V1 do diretório que contém o `dotnet.exe` aprovado |
| `SIGNTOOL_SHA256` | SHA-256 do `signtool.exe` x64 aprovado |

Os quatro hashes das autoridades R25 estão fixados no próprio workflow e correspondem ao inventário aprovado de 02/09/2026. Toda troca de autoridade exige revisão de código e nova versão; não se alteram esses hashes diretamente na interface do GitHub.

Os hashes de árvore usam exatamente `TURBORAMA-DIRECTORY-TREE-SHA256-V1`, implementado em `tools/Build-Production.ps1`. Eles precisam ser calculados numa execução de inventário, comparados por canal independente e só então cadastrados. Uma atualização da imagem `windows-2025` pode alterar a toolchain; nesse caso a release para até nova aprovação dos pins.

## 4. Tag final

A tag precisa ser criada fora do runner, com a chave privada GPG mantida offline, depois que o commit final estiver em `main` e os checks estiverem verdes:

```powershell
git switch main
git pull --ff-only origin main
git status --short
git tag -s v2.0.0 HEAD -m 'Turborama Suite 2.0.0 R25'
git verify-tag v2.0.0
git push origin refs/tags/v2.0.0:refs/tags/v2.0.0
```

Nunca mova ou recrie uma tag final publicada.

## 5. Execução e resultado

Em **Actions → Release final assinada → Run workflow**, informe `v2.0.0`. Aprove primeiro `production-signing` e, somente após o pacote Signed passar, aprove `production-release`.

O job:

1. confere que `main`, `origin/main`, o commit acionado e a tag assinada são idênticos;
2. valida presença e formato de todas as entradas;
3. confere os hashes das autoridades e da chave pública GPG;
4. importa o PFX sem permitir exportação e o remove ao terminar;
5. chama diretamente `Build-Production.ps1` no modo Signed;
6. repete os gates do pacote, Authenticode, timestamp, manifesto e SBOM;
7. cria ZIP e seis assets finais com SHA-256;
8. transfere o pacote por artifact e o segundo job revalida hashes, manifesto, ZIP e Authenticode;
9. cria e verifica atestações de proveniência;
10. monta um draft não público, carrega os seis assets e compara nome, tamanho e digest remoto;
11. somente então publica `v2.0.0` como release estável e `Latest`, com `isPrerelease=false`;
12. para sem expor uma release pública se qualquer etapa anterior divergir.

Os testes internos do pipeline rodam sem depender do servidor real. A aprovação de `production-release` deve ocorrer somente depois que a matriz externa já registrar servidor implantado, ativação, sessão, catálogo e download reais no commit candidato.

O workflow não usa nem aceita `UNSIGNED-NOT-FOR-DISTRIBUTION`. A release RC4 antiga continua apenas como evidência histórica até a publicação Signed.
