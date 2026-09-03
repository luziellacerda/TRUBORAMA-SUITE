# Turborama Suite 2.0 R25 — prontidão de produção

Data da consolidação: 03/09/2026.

## Veredito atual

A **fonte R25 está funcionalmente homologada** e consolidada para a branch canônica `main`. O cliente e o servidor reais já comprovaram ativação/sessão e leitura integral do catálogo autorizado: 902 itens, sendo 898 `READY` e quatro `MAINTENANCE` na evidência registrada.

O pacote funcional auditado continua sendo um **build não assinado**. Homologação funcional da fonte não equivale a assinatura para distribuição comercial. Somente um novo pacote produzido pelo caminho Signed, no commit final de `main`, pode ser avaliado como release para consumidores.

O cliente permanece fail-closed: nenhuma chave digitada localmente, URL permanente, segredo incorporado ou opção de teste concede acesso.

## Estado do servidor integrado

O backend Suite foi implementado de forma aditiva, sem alterar os bytes canônicos do domínio v1 existente. O fluxo integrado inclui:

- venda/entitlement já existente e licenças do produto `TURBORAMA_SUITE`;
- ativação, challenge, sessão curta e heartbeat;
- inventário R25 de placa-mãe/BIOS/Windows;
- catálogo autorizado e estados `READY`/`MAINTENANCE`;
- grants curtos e gateway com redirect HTTPS para a hospedagem;
- presença, outbox e painel administrativo.

O código do servidor permanece no repositório `luziellacerda/Servidor-pix`; ele não deve ser copiado para este repositório cliente. A documentação mestre registra a revisão de servidor observada, as migrations e os limites ainda conhecidos.

## Controles implementados no cliente e no build

- .NET `10.0.400` fixado em `global.json` e dependências em lock files;
- NuGet HTTPS, origem mapeada, assinaturas confiáveis e auditoria como erro;
- identidade CNG não exportável e protocolos Suite canônicos assinados com RSA-PSS-SHA256;
- autoridades públicas de licença e conteúdo incorporadas somente no build Signed;
- TLS normal com cadeia/hostname/revogação e pin SPKI para licenciamento;
- sessão curta, heartbeat, revogação e cancelamento das operações autorizadas;
- catálogo público sem URL permanente de download;
- autorização curta por tentativa e redirect autenticado não encaminhando bearer à origem;
- download retomável, arquivo parcial, validação de caminhos e extração ZIP/RAR/7z limitada;
- 22 categorias, 902 itens, 903 capas, 45 ícones de sistema, 22 ícones de menu, 38 vídeos de sistema, 15 vídeos de fundo e oito músicas;
- manifests internos de descrição/vídeo incorporados no assembly, sem cópias externas mutáveis;
- testes de catálogo, protocolo, inventário, WPF, download, extração e múltiplos volumes;
- publicação autocontida com SBOM SPDX 2.3, inventário SHA-256 e allowlist exata;
- Authenticode e timestamp RFC 3161 obrigatórios no modo Signed;
- tag anotada e assinada `v2.0.0`, toolchains pinadas e snapshot remoto isolado obrigatórios no modo Signed.

## Pendências para o pacote comercial

Estes itens não impedem que `main` seja a fonte homologada, mas impedem chamar um EXE não assinado de release comercial:

1. criar e publicar a tag GPG assinada `v2.0.0` exatamente no commit final aprovado;
2. executar `tools/Build-Production.ps1` no modo Signed com as autoridades públicas e pins aprovados por canal independente;
3. produzir Authenticode válido e timestamp RFC 3161;
4. cobrir o pacote/instalador integral, inclusive assets externos ao PE, por assinatura verificável;
5. instalar com ACL restrita, sem `Modify` para usuários não administrativos;
6. repetir o aceite em Windows 10/11 limpos sobre o SHA-256 exato do novo pacote;
7. registrar download completo e retomado, expiração/revogação, presença/outbox, upgrade e rollback após o deploy final;
8. confirmar que o CI do servidor aplica e testa todas as migrations efetivamente exigidas pelo release implantado;
9. manter backup/restauração, resposta a incidente, carga/soak e pentest com evidência ou aceitação formal de risco;
10. manter direitos de distribuição e obrigações comerciais aprovados pelo responsável.

## Riscos residuais documentados

- a URL de origem recebida após o redirect pode ser observada pelo cliente; o controle é autorização curta no gateway, não sigilo permanente da URL;
- a origem direta não possui digest oficial em todos os itens; alteração de bytes no mesmo localizador exige controle operacional da hospedagem;
- `TPM_BOUND` não representa atestação remota completa e a identidade CNG é vinculada ao perfil Windows;
- perda da chave local exige recovery/rebind administrativo;
- catálogo assinado antigo ainda válido exige proteção persistente contra rollback entre reinicializações;
- processos com os mesmos direitos do usuário podem usar/remover a chave não exportável;
- um pacote externo gravável pode ser adulterado mesmo quando o EXE está assinado.

## Definição objetiva de release comercial

Uma release para consumidores somente recebe esse nome quando, simultaneamente:

- árvore Git limpa e publicada em `main`;
- tag GPG assinada, publicada e apontando para o commit exato;
- workflows de build e análise verdes nesse commit;
- build Signed concluído sem bypass;
- Authenticode e timestamp válidos;
- manifesto, SBOM e hashes conferidos;
- pacote integral assinado e instalado com ACL restrita;
- backend/revisão/migrations identificados;
- ativação, sessão, heartbeat, catálogo, inventário e download real aprovados;
- recuperação e rollback exercitados;
- nenhuma credencial, chave privada, URL permanente ou dado pessoal no Git/pacote/log.

Qualquer diretório `UNSIGNED-NOT-FOR-DISTRIBUTION` permanece evidência funcional. Um diretório `SIGNED-RELEASE-CANDIDATE` ainda precisa do aceite final registrado.

O procedimento completo está em `TUTORIAL-FINAL-COMPILACAO-E-RELEASE-R25.md`; a arquitetura e evidência ponta a ponta estão em `TURBORAMA-SUITE-R25-DOCUMENTACAO-MESTRA-PONTA-A-PONTA-20260903.md`.
