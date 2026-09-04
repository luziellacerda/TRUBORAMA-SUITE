# Turborama Suite R25 — tutorial final de compilação e release

Data de consolidação: 03/09/2026.

Este é o procedimento operacional único para obter, validar, compilar e publicar o cliente Windows Turborama Suite 2.0.0. A branch `main` é a fonte canônica. Depois da promoção desta R25, nenhuma branch `codex/*` é necessária para reconstruir o programa.

Este documento separa três estados que não devem ser confundidos:

1. **fonte homologada**: código, interface, catálogo, assets, testes e scripts aprovados e versionados em `main`;
2. **staging funcional**: pacote autocontido compilado sem Authenticode, útil para validação controlada e sempre marcado `UNSIGNED-NOT-FOR-DISTRIBUTION`;
3. **release comercial**: pacote produzido pelo caminho Signed, com tag GPG, Authenticode, timestamp e todas as evidências aprovadas.

A homologação funcional da R25 não transforma automaticamente um EXE antigo ou não assinado em release comercial. A identidade de cada pacote é o SHA-256 e o commit registrados no seu `RELEASE-MANIFEST.json`.

## 1. O que está completo em `main`

O repositório contém todos os insumos permanentes necessários para restaurar, testar e compilar o cliente:

- projeto WPF `TurboBoxManager.csproj` e todos os fontes C#/XAML;
- `global.json` fixando o SDK .NET em `10.0.400`;
- `NuGet.Config`, `packages.lock.json` e lock file dos testes;
- `Directory.Build.props` com build determinístico, warnings como erro e auditoria NuGet;
- código de licença, sessão, inventário R25, catálogo, download, retomada, extração e biblioteca local;
- catálogo público com 22 categorias e 902 itens;
- 903 capas, 45 ícones de sistema, 22 ícones de menu, 22 XMLs, 38 vídeos de sistema, 15 vídeos de fundo e oito músicas incorporadas;
- índices e fontes organizadas em `Capas-Turborama-por-Sistema`;
- testes em `tests/CatalogVerifier`;
- pipeline em `tools/Build-Production.ps1`;
- geradores de manifesto e SBOM e gate do pacote;
- workflows de build e CodeQL em `.github/workflows`;
- documentação de arquitetura, segurança, operação e recovery.

O Git é o manifesto dos arquivos de compilação. Para listar exatamente os bytes pertencentes à versão escolhida:

```powershell
git ls-tree -r --full-tree HEAD
git rev-parse HEAD
git rev-parse 'HEAD^{tree}'
```

Não copie arquivos de branches antigas por cima de `main`. Isso destruiria a correspondência entre fonte, testes e evidências.

## 2. O que deliberadamente não pertence ao Git

Os seguintes itens não são fonte e não devem ser commitados:

- `bin/`, `obj/`, `publish/`, `artifacts/`, caches e temporários;
- EXE, DLL, PDB, ZIP e pacotes gerados;
- certificado Authenticode com chave privada;
- chave GPG privada;
- chaves privadas das autoridades Suite;
- OTP, licença, DeviceId, token, senha, DSN, `.env` ou dados de cliente;
- links permanentes privados de conteúdo.

O `Turborama.exe` autocontido ultrapassa o limite normal de arquivo individual do GitHub e é um **resultado** da compilação. Ele deve ser publicado como artefato do workflow ou asset de GitHub Release, acompanhado do manifesto e dos hashes; não dentro da árvore de fontes.

## 3. Entradas públicas externas do modo Signed

O staging unsigned não exige autoridades de produção. A release Signed exige cinco entradas públicas, aprovadas fora do computador de assinatura:

1. envelope JSON assinado da autoridade de licença Suite;
2. SPKI pública offline que verifica esse envelope;
3. envelope JSON assinado da autoridade de conteúdo;
4. SPKI pública offline que verifica o envelope de conteúdo;
5. chave pública GPG que verifica a tag da release.

Esses arquivos não contêm chaves privadas, mas permanecem externos ao repositório porque sua aprovação por canal independente faz parte da cadeia de confiança. O operador deve receber também o SHA-256 exato de cada arquivo por um segundo canal. Nunca use como aprovação o hash calculado apenas no mesmo host que fará a assinatura.

As autoridades são incorporadas ao assembly no build Signed. Os quatro JSONs/manifests internos usados pela interface para descrições e vídeos também são `EmbeddedResource`; eles não são publicados como arquivos externos mutáveis.

## 4. Requisitos do computador de build

Para validar e gerar staging:

- Windows 10 ou 11 x64 atualizado;
- Git for Windows;
- PowerShell 7;
- SDK .NET `10.0.400` exatamente;
- HTTPS liberado para `github.com` e `api.nuget.org`.

Para gerar release Signed, também são necessários:

- clone Git normal, não raso, em volume local confiável;
- branch `main` limpa e idêntica a `origin/main`;
- tag anotada e assinada `v2.0.0` exatamente no commit de release;
- GnuPG fornecido pelo Git for Windows;
- Windows SDK `signtool.exe`;
- certificado Authenticode válido instalado no repositório de certificados da máquina/usuário de assinatura;
- endpoint HTTPS RFC 3161 e thumbprint aprovado do certificado de timestamp;
- hashes independentes dos executáveis e das árvores PowerShell, Git e SDK .NET;
- as cinco entradas públicas da seção anterior.

Não altere `global.json` para adaptar a fonte a um SDK diferente. Instale a versão fixada.

## 5. Obter a fonte canônica

Use uma pasta nova. O clone pode ultrapassar 1 GiB porque as capas, vídeos e músicas homologados estão versionados em Git normal.

```powershell
$RepoUrl = 'https://github.com/luziellacerda/TRUBORAMA-SUITE.git'
$SourceRoot = Join-Path $env:USERPROFILE 'source\TRUBORAMA-SUITE'

git clone --branch main --single-branch $RepoUrl $SourceRoot
if ($LASTEXITCODE -ne 0) { throw 'Falha ao clonar o repositório.' }

Set-Location -LiteralPath $SourceRoot
git fetch --prune --tags origin
if ($LASTEXITCODE -ne 0) { throw 'Falha ao atualizar refs e tags.' }

git status --short --branch
git rev-parse HEAD
git rev-parse origin/main
```

Condições obrigatórias:

- `HEAD` e `origin/main` devem ser iguais;
- `git status --porcelain=v1` deve retornar vazio;
- nenhum submódulo deve existir;
- nenhum arquivo necessário deve vir de outra branch ou de uma pasta de build antiga.

Verificação adicional:

```powershell
git fsck --full --no-dangling --no-reflogs
git diff --check HEAD^ HEAD
dotnet --version
```

O último comando deve retornar `10.0.400`.

## 6. Gate da fonte

Execute primeiro o gate que verifica segurança, configuração, dependências, catálogo, assets, manifests, vídeos e scripts:

```powershell
pwsh -NoLogo -NoProfile -File .\tools\Test-ReleaseSource.ps1
if ($LASTEXITCODE -ne 0) { throw 'Gate da fonte reprovado.' }
```

Resultado esperado:

```text
PASS: fonte de Release sem bypass, teste remoto, chave incorporada ou URL privada permanente.
```

Falha nesse estágio interrompe a release. Não use `-ErrorAction SilentlyContinue`, não remova testes e não altere a allowlist para esconder arquivo inesperado.

## 7. Restaurar, compilar e executar os testes

As dependências são restauradas em modo locked e com a configuração NuGet do repositório:

```powershell
dotnet restore .\tests\CatalogVerifier\CatalogVerifier.csproj `
  --locked-mode --no-http-cache `
  --configfile .\NuGet.Config `
  -p:Configuration=Release
if ($LASTEXITCODE -ne 0) { throw 'Restore locked dos testes falhou.' }

dotnet restore .\TurboBoxManager.csproj `
  -r win-x64 `
  --locked-mode --no-http-cache `
  --configfile .\NuGet.Config `
  -p:Configuration=Release
if ($LASTEXITCODE -ne 0) { throw 'Restore locked do cliente falhou.' }

dotnet build .\TurboBoxManager.csproj -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Build Release falhou.' }

dotnet run `
  --project .\tests\CatalogVerifier\CatalogVerifier.csproj `
  -c Release --no-restore -- `
  .\Assets\Catalog\catalog.json
if ($LASTEXITCODE -ne 0) { throw 'Verificadores da R25 falharam.' }
```

O verificador cobre, entre outros pontos:

- 22 categorias, 902 itens e resolução das 902 capas;
- contratos criptográficos de licença, sessão, conteúdo e inventário;
- templates e comportamentos WPF;
- manifests incorporados de vídeos/descrições;
- retomada de download e `Range`/`If-Range`;
- limites e segurança da extração ZIP/RAR/7z;
- identidade de caminhos, volumes diferentes e biblioteca local.

## 8. Gerar staging funcional autocontido

Em árvore limpa:

```powershell
$OutputRoot = Join-Path $env:TEMP 'turborama-r25-artifacts'

pwsh -NoLogo -NoProfile -File .\tools\Build-Production.ps1 `
  -UnsignedStaging `
  -OutputRoot $OutputRoot `
  -DotNetPath 'C:\Program Files\dotnet\dotnet.exe'
if ($LASTEXITCODE -ne 0) { throw 'Build/gate do staging falhou.' }
```

Não use `-AllowDirty` para registrar uma evidência homologável. Essa opção serve apenas para diagnóstico local de alterações ainda não commitadas.

O script executa novamente os gates em snapshot isolado, publica `win-x64` self-contained/single-file, gera SBOM e hashes, valida a allowlist e reprova qualquer arquivo inesperado.

Saída esperada no diretório final:

- `Turborama.exe`;
- `Turborama.spdx.json`;
- `THIRD-PARTY-NOTICES.txt`;
- `DOTNET-THIRD-PARTY-NOTICES.txt`;
- `PACKAGE-SHA256SUMS.txt`;
- catálogo, capas, ícones, XMLs e vídeos permitidos.

O diretório recebe `UNSIGNED-NOT-FOR-DISTRIBUTION`. Confirme:

```powershell
$Package = Get-ChildItem -LiteralPath $OutputRoot -Directory |
  Sort-Object LastWriteTimeUtc -Descending |
  Select-Object -First 1

Get-AuthenticodeSignature -LiteralPath (Join-Path $Package.FullName 'Turborama.exe') |
  Select-Object Status,StatusMessage
Get-FileHash -LiteralPath (Join-Path $Package.FullName 'Turborama.exe') -Algorithm SHA256
Get-Content -LiteralPath (Join-Path $Package.FullName 'PACKAGE-SHA256SUMS.txt')
```

`NotSigned` é esperado somente no staging. Nunca renomeie esse diretório para parecer uma release assinada.

## 9. Preparar o commit e a tag de release

O build Signed exige `main`, upstream exato e tag GPG assinada. Antes de criar a tag:

```powershell
git switch main
git pull --ff-only origin main
if ($LASTEXITCODE -ne 0) { throw 'main não pôde ser atualizada por fast-forward.' }

if (git status --porcelain=v1) { throw 'Árvore Git não está limpa.' }

$ReleaseCommit = (git rev-parse HEAD).Trim()
$RemoteCommit = (git rev-parse origin/main).Trim()
if ($ReleaseCommit -ne $RemoteCommit) { throw 'HEAD não coincide com origin/main.' }
```

Crie a tag uma única vez com a identidade GPG aprovada:

```powershell
git tag -s v2.0.0 $ReleaseCommit -m 'Turborama Suite 2.0.0 R25'
if ($LASTEXITCODE -ne 0) { throw 'Assinatura da tag falhou.' }

git verify-tag v2.0.0
if ($LASTEXITCODE -ne 0) { throw 'Verificação da tag falhou.' }

if ((git rev-parse 'v2.0.0^{}').Trim() -ne $ReleaseCommit) {
  throw 'A tag não aponta para o commit exato.'
}

git push origin refs/tags/v2.0.0:refs/tags/v2.0.0
if ($LASTEXITCODE -ne 0) { throw 'Publicação da tag falhou.' }
```

Nunca recrie, mova ou force uma tag publicada. Qualquer alteração posterior exige nova versão.

## 10. Conferir entradas e hashes Signed

Coloque as entradas públicas numa pasta de cerimônia fora do clone. Não use pendrive perdido como única cópia. Mantenha pelo menos duas cópias cifradas e um inventário com hashes.

Exemplo de inventário local, sem revelar o conteúdo:

```powershell
$CeremonyRoot = 'X:\TURBORAMA-R25-PUBLIC'
$PublicInputs = @(
  'suite-authority-envelope.json',
  'suite-authority-issuer.spki',
  'content-authority-envelope.json',
  'content-authority-issuer.spki',
  'release-tag-public-key.asc'
)

foreach ($Name in $PublicInputs) {
  $Path = Join-Path $CeremonyRoot $Name
  if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
    throw "Entrada pública ausente: $Name"
  }
  Get-FileHash -LiteralPath $Path -Algorithm SHA256
}
```

Compare cada resultado com o valor aprovado por canal independente. O build recebe os hashes esperados como argumentos e para em qualquer divergência.

O certificado Authenticode deve aparecer no store e possuir chave privada acessível somente ao principal de assinatura:

```powershell
Get-ChildItem Cert:\CurrentUser\My,Cert:\LocalMachine\My |
  Where-Object HasPrivateKey |
  Select-Object Subject,Thumbprint,NotAfter,HasPrivateKey
```

Não exporte o certificado com a chave privada para o repositório nem para o diretório do pacote.

## 11. Gerar o candidato Signed

Execute o script diretamente por caminho absoluto do PowerShell 7, sem wrapper e sem perfil. Substitua os marcadores pelos valores aprovados; não cole segredos, pois o comando aceita apenas material público e identificadores.

```powershell
& 'C:\Program Files\PowerShell\7\pwsh.exe' `
  -NoLogo -NoProfile -NonInteractive -File `
  'C:\fonte\TRUBORAMA-SUITE\tools\Build-Production.ps1' `
  -CertificateThumbprint <SHA1_AUTHENTICODE> `
  -TimestampUrl <URL_HTTPS_RFC3161> `
  -TimestampCertificateThumbprint <SHA1_CERTIFICADO_TIMESTAMP> `
  -ReleaseTagSignerFingerprint <FINGERPRINT_SUBCHAVE_GPG> `
  -ReleaseTagPrimaryKeyFingerprint <FINGERPRINT_CHAVE_GPG_PRIMARIA> `
  -ReleaseTagPublicKeyPath <ARQUIVO_CHAVE_PUBLICA_GPG> `
  -ReleaseTagPublicKeySha256 <SHA256_CHAVE_PUBLICA_GPG> `
  -AuthorityConfigurationPath <ENVELOPE_LICENCA_JSON> `
  -AuthorityConfigurationSha256 <SHA256_ENVELOPE_LICENCA> `
  -AuthorityIssuerSpkiPath <SPKI_OFFLINE_LICENCA> `
  -AuthorityIssuerSpkiSha256 <SHA256_SPKI_LICENCA> `
  -ContentAuthorityConfigurationPath <ENVELOPE_CONTEUDO_JSON> `
  -ContentAuthorityConfigurationSha256 <SHA256_ENVELOPE_CONTEUDO> `
  -ContentAuthorityIssuerSpkiPath <SPKI_OFFLINE_CONTEUDO> `
  -ContentAuthorityIssuerSpkiSha256 <SHA256_SPKI_CONTEUDO> `
  -PowerShellSha256 <SHA256_PWSH_EXE> `
  -PowerShellHomeTreeSha256 <SHA256_V1_PSHOME> `
  -DotNetPath <CAMINHO_ABSOLUTO_DOTNET_EXE> `
  -DotNetSdkTreeSha256 <SHA256_V1_ARVORE_DOTNET> `
  -GitPath <CAMINHO_ABSOLUTO_GIT_EXE> `
  -GitSha256 <SHA256_GIT_EXE> `
  -GitTreeSha256 <SHA256_V1_ARVORE_GIT> `
  -GpgSha256 <SHA256_GPG_EXE> `
  -SignToolPath <CAMINHO_ABSOLUTO_SIGNTOOL_EXE> `
  -SignToolSha256 <SHA256_SIGNTOOL_EXE>
```

Os hashes de árvore usam o formato `TURBORAMA-DIRECTORY-TREE-SHA256-V1` implementado pelo próprio pipeline. Eles devem ser calculados e aprovados numa estação limpa separada, registrados na cerimônia e só depois fornecidos ao host de assinatura.

O resultado correto termina como `SIGNED-RELEASE-CANDIDATE`. O script confirma:

- branch, upstream, commit e tag remota;
- assinatura GPG da tag e chave pública pinada;
- hashes das toolchains antes e depois do uso;
- envelopes e SPKIs públicos aprovados;
- assinatura Authenticode e timestamp;
- manifesto, SBOM, notices, conteúdo e hashes do pacote;
- cópia final byte a byte após promoção atômica.

## 12. Aceite final do pacote

Antes de publicar a clientes, registre uma matriz com estes resultados e o SHA-256 do pacote:

- Authenticode `Valid` e timestamp válido;
- `RELEASE-MANIFEST.json` aponta para o commit/tag exatos;
- hashes de todos os arquivos conferem;
- instalação em Windows 10 e 11 x64 limpos;
- execução por usuário sem privilégio administrativo;
- ativação, reabertura e sessão já ativa;
- heartbeat e revogação;
- inventário R25 com e sem TPM, respeitando a política da licença;
- catálogo completo com 902 itens;
- itens `MAINTENANCE` indisponíveis sem quebrar o restante do catálogo;
- download real completo e retomado após interrupção;
- extração, pasta escolhida e reaparecimento em Jogos locais;
- exclusão local sem apagar catálogo/entitlement;
- expiração de sessão cancela operação autorizada;
- atualização, rollback e recuperação de chave/dispositivo;
- pasta instalada com ACL sem `Modify` para usuários não administrativos.

O servidor deve registrar as tentativas esperadas, sem expor OTP, tokens, links permanentes ou dados pessoais em logs. O contrato detalhado e as rotas estão em `TURBORAMA-SUITE-R25-DOCUMENTACAO-MESTRA-PONTA-A-PONTA-20260903.md`.

## 13. Publicar no GitHub

Publique como GitHub Release associada à tag verificada. Anexe o pacote final e, no mínimo:

- `Turborama.exe` ou instalador integralmente assinado;
- `RELEASE-MANIFEST.json`;
- `SHA256SUMS-v2.0.0.txt`, gerado como asset externo após o gate;
- `Turborama.spdx.json`;
- notices de terceiros;
- registro resumido dos testes sem dados sensíveis.

Não acrescente o checksum dentro do diretório já aprovado: a allowlist do pacote é fechada e qualquer mutação posterior invalida o gate. Não anexe o diretório `UNSIGNED-NOT-FOR-DISTRIBUTION` e não chame um artefato apenas funcional de pacote assinado.

A automação protegida, os nomes exatos dos secrets/variables e a promoção draft → release estável estão em `GITHUB-RELEASE-FINAL-R25.md` e `.github/workflows/release-final.yml`.

## 14. Atualização futura sem voltar às branches antigas

Toda alteração futura começa a partir de `main`:

```powershell
git switch main
git pull --ff-only origin main
git switch -c feature/<descricao-curta>
```

Depois da revisão e dos gates, integre por processo que preserve a proveniência definida para a próxima versão. Nunca recupere arquivos manualmente de branches R18–R25; o histórico delas já é ancestral de `main` após a promoção.

## 15. Rollback

Não mova `main` para trás e não force uma tag. Para desfazer uma alteração publicada, crie uma branch a partir de `main`, faça `git revert`, execute todos os gates e integre o novo commit auditável.

No cliente instalado, rollback significa reinstalar um pacote anterior ainda confiável e compatível com o backend. No servidor, use release anterior, kill switch e correções forward; não execute down migration destrutiva como atalho.

## 16. Diagnóstico rápido

| Falha | Causa provável | Ação |
|---|---|---|
| SDK compatível não encontrado | .NET 10.0.400 ausente | instalar a versão fixada; não alterar `global.json` |
| restore locked falha | cache/rede/lock divergente | usar `NuGet.Config`, rede HTTPS e não atualizar pacote silenciosamente |
| gate acusa arquivo fora da allowlist | pacote contém saída inesperada | corrigir o projeto/pipeline; não ampliar allowlist sem justificar |
| autoridade indisponível antes de HTTP | envelope/SPKI/hash/validade incorporados incorretos | refazer build Signed com os cinco arquivos públicos aprovados |
| servidor não vê tentativa | falha local, DNS, TLS ou pin antes do request | conferir relógio, autoridade, DNS, cadeia TLS e SPKI |
| licença já ativa seguida de não autorizado | sessão, dispositivo ou resposta assinada incompatível | correlacionar challenge/sessão no servidor e validar commit dos dois lados |
| catálogo abre e download falha | grant, gateway, redirect ou origem | validar sessão, grant de uso único, HTTP 307, host e Range |
| GitHub Actions falha sem job | workflow YAML/contexto inválido | abrir a anotação do run; `runner.*` deve estar em escopo de job/step |

## 17. Critério de encerramento

A fonte final está consolidada quando:

- `main` contém o histórico completo da R25 por fast-forward;
- o clone limpo não depende de nenhuma outra branch;
- gate de fonte, testes e staging terminam com sucesso no commit final;
- workflows do mesmo commit terminam verdes;
- não há binários gerados, caches ou material privado no commit;
- este tutorial e a documentação mestre permitem repetir todo o processo.

A distribuição comercial está encerrada somente quando o pacote Signed e a matriz de aceite da seção 12 também estiverem aprovados.
