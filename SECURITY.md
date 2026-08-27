# Politica de seguranca

## Versoes suportadas

Somente a linha 2.x, depois de satisfazer integralmente o checklist de Release,
recebe correcoes de seguranca. Builds locais, artefatos de CI e pacotes marcados
`UNSIGNED-NOT-FOR-DISTRIBUTION` ou `SIGNED-RELEASE-CANDIDATE` nao sao releases
de producao.

## Como relatar

Nao abra uma issue publica para vulnerabilidades, credenciais, links privados
ou dados de clientes. Use o formulario privado **Report a vulnerability** na
aba **Security** deste repositorio:

https://github.com/luziellacerda/TRUBORAMA-SUITE/security/advisories/new

Inclua a versao, o SHA-256 do pacote, passos de reproducao e o impacto
observado. Nao inclua chaves, codigos de ativacao, tokens ou dados pessoais em
capturas e logs.

## Autenticidade de releases

Uma Release de producao precisa ter, simultaneamente:

- assinatura Authenticode valida e carimbo de tempo RFC 3161 no executavel;
- `RELEASE-MANIFEST.json` com commit e SHA-256 de todos os arquivos;
- `Turborama.spdx.json` (SPDX 2.3);
- dependencias restauradas em modo locked;
- build assinado iniciado diretamente por `pwsh.exe` PowerShell 7 absoluto com
  `-NoProfile -NonInteractive -File`, SHA-256 independente do executavel e hash
  versionado da arvore `PSHOME` aprovados antes do primeiro cmdlet;
- caminhos absolutos e pins independentes de Git, GnuPG, SDK .NET e SignTool,
  revalidados antes e depois das fases de uso; as toolchains devem residir em
  imagem ou volume imutavel, sem escrita para o principal de Release;
- todos os gates de fonte, testes offline e verificacao do pacote aprovados.
- assinatura verificavel do pacote completo, cobrindo tambem os assets externos
  ao executavel;
- backend Suite, catalogo assinado e grants de download aprovados nos testes de
  contrato e implantados no ambiente de producao.

A ausencia de qualquer item significa que o pacote nao deve ser distribuido
como producao.

## Confidencialidade da ativacao

Assinaturas dos envelopes da aplicacao comprovam autenticidade e integridade da
resposta, mas nao fornecem sigilo ao codigo de ativacao. A confidencialidade
depende do HTTPS de producao com cadeia, hostname e revogacao validos, pin SPKI
aprovado e transporte nao substituivel. Nunca envie codigos por handler de teste,
proxy de depuracao ou endpoint sem essas garantias.
