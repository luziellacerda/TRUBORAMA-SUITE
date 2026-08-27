# TURBORAMA SUITE

Aplicativo desktop WPF para Windows que reúne catálogo, biblioteca e gerenciamento de conteúdo da Turborama.

## Estado da versão 2.0.0

Esta árvore produz um **staging local não assinável para distribuição** e contém o pipeline de um **candidato assinado**. Nenhum dos dois deve ser anunciado como Release de produção enquanto os bloqueios de [prontidão de produção](docs/PRODUCTION-READINESS.md) permanecerem abertos.

O cliente opera de forma fail-closed:

- não existe chave demonstrativa, senha universal, modo offline ou URL privada incorporada;
- sem configuração de autoridade Suite assinada, vigente e vinculada ao SHA-256 exato aprovado para a Release, com chave on-line dedicada e pin TLS SPKI, o login permanece indisponível;
- respostas positivas de ativação, sessão e heartbeat são aceitas somente em envelopes RSA-PSS-SHA256 canônicos, assinados pela chave on-line autorizada e vinculados ao contexto e challenge atuais;
- sem sessão Suite válida e capacidade autorizada, a loja fecha e as operações são canceladas;
- os 850 itens do catálogo público não são baixáveis até receberem descriptors imutáveis e grants autenticados;
- tamanho e SHA-256 são obrigatórios antes de um artefato poder ser aceito;
- a política de extração vem exclusivamente do descriptor autorizado.

## Conteúdo visual incluído

- 22 categorias e 850 itens de catálogo;
- 851 imagens de capa, incluindo fallback e as 98 capas Windows verificadas por tamanho e SHA-256 nas duas cópias;
- 45 ícones de sistema e 22 ícones de menu;
- 38 vídeos de sistemas e 15 vídeos de fundo, todos inventariados por tamanho e SHA-256;
- vídeos de fundo recortados com preenchimento proporcional para acompanhar o quadrado disponível em qualquer tamanho de janela, sem depender da resolução original.

O pacote não inclui ROMs, jogos ou artefatos privados.

## Requisitos de desenvolvimento

- Windows 10 ou 11 x64;
- PowerShell 7;
- .NET SDK **10.0.400** exatamente, conforme `global.json`;
- acesso ao `nuget.org` para a primeira restauração.

As versões ficam fixadas por lock file. O `NuGet.Config` exige fonte HTTPS, mapeamento de origem e assinaturas confiáveis.

## Validar e compilar staging

O comando consolidado executa o gate de origem, restauração em modo locked, build Release, verificadores, publicação autocontida, SBOM, manifesto SHA-256 e gate do pacote:

```powershell
pwsh -File .\tools\Build-Production.ps1 -UnsignedStaging -AllowDirty
```

`-AllowDirty` só é aceito nesse modo local. O resultado recebe o sufixo `UNSIGNED-NOT-FOR-DISTRIBUTION` e não pode ser entregue a clientes.
Mesmo nesse modo, a árvore local é copiada para um snapshot temporário: restore, `obj`, build e publish não escrevem na árvore chamadora nem deixam `project.assets.json` apontando para o cache descartável.

Para validar apenas a árvore e os testes:

```powershell
pwsh -File .\tools\Test-ReleaseSource.ps1
dotnet restore .\tests\CatalogVerifier\CatalogVerifier.csproj --locked-mode -p:Configuration=Release
dotnet run --project .\tests\CatalogVerifier\CatalogVerifier.csproj -c Release --no-restore -- .\Assets\Catalog\catalog.json
```

## Gerar um candidato assinado

```powershell
& 'C:\Program Files\PowerShell\7\pwsh.exe' `
  -NoLogo -NoProfile -NonInteractive -File `
  'C:\caminho-absoluto\TRUBORAMA-SUITE\tools\Build-Production.ps1' `
  -CertificateThumbprint <SHA1_DO_CERTIFICADO> `
  -TimestampUrl <HTTPS_RFC3161> `
  -TimestampCertificateThumbprint <SHA1_DO_CERTIFICADO_DE_TIMESTAMP> `
  -ReleaseTagSignerFingerprint <FINGERPRINT_DA_CHAVE_QUE_ASSINOU> `
  -ReleaseTagPrimaryKeyFingerprint <FINGERPRINT_DA_CHAVE_PRIMARIA> `
  -ReleaseTagPublicKeyPath <ARQUIVO_DA_CHAVE_PUBLICA_GPG_APROVADA> `
  -ReleaseTagPublicKeySha256 <SHA256_EXATO_DA_CHAVE_PUBLICA_GPG_APROVADO_INDEPENDENTEMENTE> `
  -AuthorityConfigurationPath <ENVELOPE_ASSINADO_JSON> `
  -AuthorityConfigurationSha256 <SHA256_EXATO_DO_ENVELOPE_APROVADO_INDEPENDENTEMENTE> `
  -AuthorityIssuerSpkiPath <CHAVE_PUBLICA_SPKI_OFFLINE> `
  -AuthorityIssuerSpkiSha256 <SHA256_SPKI_OFFLINE_APROVADO_INDEPENDENTEMENTE> `
  -PowerShellSha256 <SHA256_DO_PWSH_EXE_APROVADO_INDEPENDENTEMENTE> `
  -PowerShellHomeTreeSha256 <SHA256_V1_DA_ARVORE_PSHOME_APROVADA> `
  -DotNetPath <CAMINHO_ABSOLUTO_DOTNET_EXE_OFICIAL> `
  -DotNetSdkTreeSha256 <SHA256_DA_ARVORE_SDK_APROVADA> `
  -GitPath <CAMINHO_ABSOLUTO_GIT_EXE_OFICIAL> `
  -GitSha256 <SHA256_DO_GIT_EXE_APROVADO> `
  -GitTreeSha256 <SHA256_DA_ARVORE_GIT_APROVADA> `
  -GpgSha256 <SHA256_DO_GPG_INTEGRADO_APROVADO> `
  -SignToolPath <CAMINHO_ABSOLUTO_SIGNTOOL_EXE_OFICIAL> `
  -SignToolSha256 <SHA256_DO_SIGNTOOL_EXE_APROVADO>
```

Esse modo recusa `pwsh` resolvido por `PATH`, execução em shell já aberto, `-Command`, `-EncodedCommand`, `-CommandWithArgs`, arquivos de configuração/settings, alteração de diretório, abreviações, `Name:value`, parâmetros comuns, duplicatas, perfil carregado, modo interativo e wrapper. A linha de comando aceita somente a gramática exibida acima, com `-NoLogo` opcional e `-File` apontando diretamente para o script absoluto. Antes do primeiro cmdlet, APIs BCL conferem em tempo constante o SHA-256 do `pwsh.exe` em execução e o hash determinístico da árvore completa de seu `PSHOME`; só então o autoload é desligado e os módulos oficiais são importados por caminhos absolutos dentro do `PSHOME` aprovado. `DotNetPath`, `GitPath` e `SignToolPath` também são absolutos e nunca são descobertos por `PATH` no modo assinado.

O formato de árvore é `TURBORAMA-DIRECTORY-TREE-SHA256-V1`: ordenação ordinal por caminho relativo normalizado com `/`, UTF-8 estrito, registro versionado de cada diretório e de cada arquivo com comprimento e SHA-256, sem timestamps ou caminho raiz; qualquer reparse point é rejeitado. Os pins de `PSHOME`, Git e SDK .NET são revalidados imediatamente antes e depois das fases que os utilizam, inclusive após gates auxiliares, para detectar troca de um arquivo já lido. Isso reduz a janela de TOCTOU, mas não substitui uma imagem/volume de toolchain imutável, ACL sem escrita para o principal de Release e atestação independente dos pins. Calcule e aprove os pins em uma estação limpa separada; nunca aceite como aprovação o valor produzido pelo próprio host que será verificado.

Além disso, o modo assinado exige árvore limpa, commit idêntico ao upstream, tag anotada e assinada exata `v2.0.0` publicada como o mesmo objeto no origin, fingerprints GPG primário e da chave de assinatura informados explicitamente, a chave **pública** correspondente e o SHA-256 dos bytes exatos dessa chave aprovado por canal independente. O parser exige exatamente um `GOODSIG` e um `VALIDSIG` coerentes e reprova explicitamente assinaturas ruins, expiradas, revogadas, ausentes ou com falha. Também são obrigatórios hashes previamente aprovados do executável e de toda a árvore Git/GPG, do `signtool.exe` e de toda a árvore do SDK .NET, origem oficial, certificado Authenticode, certificado de timestamp pinado e configuração de autoridade válida. O SHA-256 dos bytes **exatos do envelope** e o SHA-256 da chave SPKI **offline** que o assina são obrigatórios, devem ser obtidos por canal independente e são conferidos em tempo constante antes de qualquer verificação, incorporação ou inventário. O hash exato do envelope também é incorporado ao cliente e revalidado no carregamento; assim, um envelope antigo ainda assinado pelo mesmo issuer não substitui o aprovado. Rotacionar a configuração exige aprovar um novo hash. O envelope pode valer no máximo 366 dias. O hash da SPKI offline não é o `keyId` da chave **on-line** autorizada dentro do envelope. A configuração assinada deve conter uma chave RSA on-line separada para assertions e o SHA-256 do SPKI TLS do servidor. A chave pública GPG é capturada em arquivo limitado, importada com material mínimo em um `GNUPGHOME` temporário isolado e descartado, sem consultar o keyring do perfil ou recuperar chaves pela rede. Nunca forneça uma chave privada. A compilação ocorre em um worktree isolado do commit publicado. Antes de qualquer gate ou chamada `dotnet`, o modo assinado substitui todo o ambiente do processo por uma allowlist controlada, usa uma raiz temporária fixa do Windows e perfis/caches isolados; o snapshot completo do ambiente chamador é restaurado em `finally`. Caminhos interpolados em propriedades MSBuild rejeitam `;`, `,` e `%`.

Mesmo após passar, o resultado é `SIGNED-RELEASE-CANDIDATE`: ainda precisa de backend Suite implantado, catálogo e manifests assinados, grants de download, artefatos licenciados e assinatura/verificação do pacote completo.

## Saídas verificáveis

Cada diretório gerado contém:

- `Turborama.exe`, autocontido para `win-x64`;
- `RELEASE-MANIFEST.json`, com identidade Git, SHA-256 dos arquivos e pins das toolchains atestadas no candidato assinado;
- `Turborama.spdx.json`, SBOM SPDX 2.3;
- `THIRD-PARTY-NOTICES.txt`;
- somente os assets explicitamente permitidos pelo gate do pacote.

Consulte também [SECURITY.md](SECURITY.md) e [docs/PRODUCTION-READINESS.md](docs/PRODUCTION-READINESS.md).
