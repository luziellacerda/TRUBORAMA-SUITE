# Turborama Suite R25 — documentação mestre ponta a ponta

- Data de consolidação: 03/09/2026
- Produto: `TURBORAMA_SUITE`
- Cliente: Turborama 2.0.0 para Windows x64
- Objetivo: registrar, em um único lugar, como o ecossistema é construído, implantado, operado, testado e recuperado até o estado efetivamente comprovado nesta data.

> Este documento é a referência de estado datada da R25. Ele não transforma um binário não assinado em release comercial. Quando um handoff ou documento anterior disser que o backend, o catálogo ou os grants ainda não existem, prevalece aqui o estado posteriormente verificado. Os controles normativos de `SECURITY.md` e `docs/PRODUCTION-READINESS.md` continuam válidos onde não forem explicitamente atualizados por evidência posterior.

## 1. Regras de leitura e proteção

- Nenhuma senha, OTP, licença real, DeviceId real, token bearer, DSN, chave privada ou dado pessoal pertence ao Git, a logs ou a este documento.
- Hashes de artefatos públicos, commits, nomes de rotas, schemas e fingerprints de chaves públicas podem ser registrados.
- O código do cliente e o código do servidor ficam em repositórios diferentes. Um deploy só é rastreável quando registra os dois commits e o release realmente carregado pelo `systemd`.
- “Funcional em produção” significa que o fluxo técnico testado responde corretamente contra os serviços reais. “Liberado ao consumidor” exige, além disso, assinatura e aceites de distribuição.
- O preço, as credenciais do meio de pagamento e as regras comerciais permanecem na infraestrutura de vendas existente. Eles não devem ser hardcoded no EXE nem alterados durante manutenção do cliente Suite.

## 2. Fontes de verdade

| Camada | Repositório/artefato | Revisão observada |
|---|---|---|
| Cliente Windows | `luziellacerda/TRUBORAMA-SUITE` | branch `codex/cliente-r25-inventario-placa-mae-20260902`, commit `c2d41a96417454e715ee666a58c3c04727079c4d` |
| Servidor | `luziellacerda/Servidor-pix` | branch `codex/turborama-suite-vendas-producao-20260828`, referência remota `56b55b5571422ba7d16974bb8b0af28c28d07d8c` |
| Correção da sessão | migration `021_suite_inventory_challenge_session_history` | commit histórico `63c112b256d79954c034fee16f1da65e861d8ff5` |
| Catálogo visual do cliente | `Assets/Catalog/catalog.json` | 22 categorias, 902 itens, SHA-256 `f4792aa4d52cdceb99b7f257e73bae974626ac433a8bac4d38d08920b6de7e83` |
| Binário funcional auditado | `Turborama.exe` | 176.086.774 bytes, SHA-256 `dd0a83a695a7a20209335238c7e24d2e98a4e179f7dbf5336e0d047a1b652b02` |

O clone de diagnóstico do servidor estava limpo, porém seu checkout local estava atrás da referência remota. A inspeção deste documento usa o conteúdo da referência remota `56b55b5…`; nunca se deve compilar o servidor a partir de um checkout antigo apenas porque a pasta local parece limpa.

## 3. Estado executivo em 03/09/2026

| Área | Estado | Evidência/limite |
|---|---|---|
| Cliente, interface e assets | Implementado | WPF, 22 categorias, 902 itens, capas, ícones, vídeos, biblioteca local, downloads e player de música presentes no commit do cliente |
| Autoridades públicas | Incorporadas e validadas | envelopes de licenciamento e conteúdo incorporados no EXE funcional; chaves privadas não estão no cliente |
| Ativação e sessão reais | Funcional | challenge de sessão e abertura de sessão retornaram HTTP 200 no teste ponta a ponta |
| Catálogo autorizado | Funcional | todas as páginas de `catalog.read` foram autorizadas; resultado: 902 itens, sendo 898 `READY` e 4 `MAINTENANCE` |
| Download direto | Implementado | autorização curta, gateway e redirect direto existem; o aceite de download completo/Range após o último deploy precisa permanecer no checklist de liberação |
| Inventário R25 | Implementado | coleta e protocolo aditivo de placa-mãe/BIOS/Windows presentes; migration 020 aplicada no desenho do servidor |
| Presença/WhatsApp | Implementado no servidor | presença e outbox existem; idempotência do evento real deve ser verificada no aceite operacional de cada release |
| Venda/PIX | Sistema existente, fora da alteração atual | commerce inbox, deliveries, entitlement e painel integram a venda existente à licença Suite; não alterar preço/provedor como efeito colateral |
| Distribuição comercial do EXE | **Bloqueada** | o EXE auditado está `NotSigned`; falta Authenticode comercial, timestamp e aceite em Windows limpo |
| Integridade do instalador/pasta | **Bloqueada** | o diretório auditado permitia `Modify` a `Authenticated Users`; a instalação final deve usar ACL restrita e pacote integral assinado |

Conclusão: o ecossistema servidor-cliente já consegue autorizar sessão e catálogo reais. O arquivo auditado é um binário funcional de validação, não uma release assinada para consumidores.

## 4. Arquitetura geral

```text
Site/venda/PIX já existente
        |
        | evento comercial idempotente (compra paga/suspensa)
        v
TurboRamaSuiteAdminServer / BFF administrativo
        |
        +---- PostgreSQL schema suite
        |       licenças, entregas, dispositivos, sessões,
        |       entitlements, catálogo, grants, inventário,
        |       presença, auditoria e outbox
        |
        +---- painel protegido /admin/suite

Cliente WPF 2.0.0
        |
        +---- API Suite: ativação, challenge, sessão, heartbeat e inventário
        |
        +---- API de conteúdo: catálogo assinado e autorização de download
        |          |
        |          v
        |      gateway de conteúdo -- HTTP 307 --> hospedagem HTTPS permitida
        |                                      (arquivo não passa pelo servidor)
        |
        +---- disco local: parcial, retomada, extração e biblioteca de jogos

Nginx/Cloudflare expõem apenas as rotas públicas aprovadas.
Chaves/peppers/DSNs ficam em credenciais protegidas fora do Git.
```

### Separação de responsabilidades

- O cliente apresenta a interface, mantém a chave privada da máquina, prova posse, verifica respostas assinadas e gerencia arquivos locais.
- A API Suite decide se licença, dispositivo e sessão estão autorizados.
- A API de conteúdo decide quais itens estão `READY` ou `MAINTENANCE` e emite grants de curta duração.
- O gateway consome o bearer de uso único, decifra o localizador permanente e devolve um único redirect HTTPS.
- A hospedagem entrega o arquivo diretamente ao computador.
- O servidor não armazena, replica, faz cache nem retransmite o arquivo do jogo no modo direto R2.
- O painel administrativo opera licença, emissão inicial, conteúdo e auditoria; ele não deve expor links permanentes nem segredos.

## 5. Fluxo funcional completo

### 5.1 Venda e provisionamento

1. O sistema de venda existente confirma a compra.
2. Um evento comercial idempotente entra em `suite_commerce_inbox`.
3. A entrega é representada em `suite_license_deliveries` para o SKU Suite vitalício de um dispositivo.
4. O servidor cria ou vincula uma licença `TURBORAMA_SUITE`, com termo `LIFETIME`, limite de um dispositivo e estado coerente com a situação financeira.
5. Um entitlement `FULL_CATALOG` é ligado à entrega elegível.
6. O cliente recebe o ID da licença e, para primeira ativação ou transferência controlada, um código de uso único.

Regras obrigatórias:

- reprocessar o mesmo evento não pode criar outra licença;
- suspensão financeira deve suspender a entrega conforme as regras comerciais existentes;
- erro de sessão não autoriza recriar licença, DeviceId ou fingerprint;
- alteração de preço não faz parte do build do cliente;
- licença vitalícia significa sem expiração comercial, não sessão de rede infinita.

### 5.2 Inicialização e tela de login

O `App.xaml` inicia `PremiumLoginWindow.xaml`.

1. `SuiteLicensingFactory.CreateDefault()` carrega e valida as duas autoridades públicas incorporadas.
2. O usuário informa o ID exato da licença.
3. Na primeira ativação, informa também o código de uso único. Em uma máquina já vinculada, entra sem novo código.
4. O código é apagado do campo imediatamente após a leitura.
5. Falta de autoridade, erro de rede e recusa criptográfica são apresentados por mensagens genéricas; detalhes internos não são exibidos.
6. Somente depois de sessão válida e catálogo autorizado o `StoreWindow` é aberto.

Não há senha administrativa, senha universal, licença demonstrativa nem bypass offline no cliente.

### 5.3 Identidade e ativação da máquina

`Licensing/SuiteMachineIdentity.cs` mantém uma chave RSA de assinatura no Windows CNG.

- `DeviceId` deriva da chave pública SPKI, e não de um número digitado pelo administrador.
- A chave é criada como chave de usuário, persistente e não exportável.
- O algoritmo de prova é RSA-PSS com SHA-256.
- O descriptor inclui DeviceId, binding, algoritmo, SPKI pública e fingerprint de hardware.
- O produto continua vinculado pelo `contextHash`; não se altera o domínio canônico v1 nem se adiciona `ProductId` diretamente aos bytes antigos da machine proof.
- Buffers sensíveis usados para assinatura são zerados.

Ativação:

1. `POST /v1/suite/activations/challenge` envia descriptor, licença e código.
2. O servidor valida licença, enrollment, código e contexto, e emite challenge assinado de 60 segundos.
3. O cliente assina os bytes canônicos com sua chave privada.
4. `POST /v1/suite/activations/complete` consome a prova uma única vez.
5. O servidor persiste o dispositivo e devolve resultado assinado.

O equipamento auditado apresentou binding efetivo `SOFTWARE_BOUND_ONLINE`. Isso prova persistência e não exportabilidade da chave CNG observada, mas não equivale a atestação remota de TPM.

### 5.4 Sessão e heartbeat

1. O cliente cria um `sessionId` aleatório.
2. Solicita `session.open` em `POST /v1/suite/challenges`.
3. Assina o challenge e envia o contexto em `POST /v1/suite/sessions`.
4. O servidor valida licença ativa, dispositivo ativo, fingerprint, prova e challenge.
5. Uma sessão autorizada vale 180 segundos e solicita heartbeat após 5 segundos.
6. Cada heartbeat repete challenge/prova com ação `session.heartbeat` e renova a autorização.
7. Expiração, revogação ou recusa cancela o token de autorização compartilhado; catálogo, download e janela devem falhar fechados.

A migration 021 remove a FK incorreta que ligava challenges históricos de inventário ao identificador mutável da sessão. Antes dela, trocar o `sessionId` podia gerar erro 500 mesmo com licença correta.

### 5.5 Inventário R25, placa-mãe e presença

O inventário é aditivo: ele não muda os bytes da prova v1 e não substitui a identidade CNG.

1. O cliente coleta fabricante/modelo da placa-base, versão, identificadores mascarados, fabricante/versão de BIOS, fabricante/modelo do sistema, Windows, arquitetura, versão do cliente e fonte da coleta.
2. Serial de placa-base e UUID completos são campos sensíveis; o protocolo e o servidor preveem proteção/cifragem, enquanto painel e logs usam formas mascaradas.
3. O inventário possui hash canônico e challenge separado `device.inventory`.
4. O cliente assina a prova com a mesma identidade autorizada.
5. O servidor classifica a comparação como `MATCH`, `ENRICHED`, `NON_IDENTITY_CHANGE`, `INCONCLUSIVE` ou `PROBABLE_BOARD_CHANGE`.
6. Sessão válida atualiza `suite_device_presence`.
7. Uma transição de conexão pode criar `device.connected` na outbox; o worker de WhatsApp processa de modo assíncrono.

Falha do WhatsApp nunca deve invalidar sessão ou licença. Troca provável de placa deve abrir revisão/transferência controlada, não gerar vínculo novo automaticamente.

### 5.6 Catálogo autorizado

O catálogo visual local contém nomes, categorias, capas, descrições e ordem. Ele contém 902 valores vazios para `downloadUrl` e 902 valores vazios para `sha256`; portanto, o EXE e o JSON não carregam links permanentes nem hashes oficiais dos arquivos hospedados.

Após a sessão:

1. O cliente calcula o contexto da página do catálogo.
2. Solicita challenge com ação `catalog.read`.
3. Envia a prova a `POST /v1/suite-content/catalog/current`.
4. Verifica assinatura, `keyId`, produto, licença, dispositivo, sessão, contexto, challenge, validade e sequência.
5. Acumula até 64 páginas, com no máximo 64 itens por página, exigindo cobertura exata dos 902 IDs locais.
6. Item `READY` recebe descriptor assinado sem URL; item indisponível recebe `MAINTENANCE` e razão `CONTENT_TEMPORARILY_UNAVAILABLE`.

Distribuição atual do snapshot validado: 898 itens `READY`, 4 itens `MAINTENANCE`, total 902.

### 5.7 Autorização e download direto R2

1. O usuário seleciona um item `READY`.
2. O cliente solicita challenge `download.authorize`, vinculando catálogo, item, artefato, versão, manifest, descriptor, offset e validadores da retomada.
3. `POST /v1/suite-content/downloads/authorize` valida sessão e entitlement e emite grant assinado por 60 segundos.
4. O cliente cria `GET /v1/suite-content/artifacts/{grantId}` com bearer somente em memória.
5. O gateway consome o grant de uso único, valida Range/If-Range, decifra o localizador e verifica a política de origem.
6. O gateway responde com um único HTTP 307 para uma URI HTTPS na porta 443.
7. O cliente remove o bearer e faz o GET direto na hospedagem. Cookies e credenciais Suite não são enviados à origem.
8. Um segundo redirect da hospedagem é recusado.
9. O parcial, ETag, Last-Modified, Range e estado de retomada são controlados localmente.
10. Ao terminar, o cliente calcula SHA-256 do arquivo recebido, valida coerência local e publica por operação atômica antes da extração/instalação.

O gateway marca o grant como concluído depois de autorizar/devolver o redirect; ele não observa o fim do download feito diretamente na hospedagem. Portanto, a métrica de grant concluído significa “autorização entregue”, não “arquivo inteiro recebido pelo cliente”.

Limite deliberado do modo R2: tamanho e SHA-256 oficiais não fazem parte do descriptor do servidor. O valor é descoberto/calculado a partir da transferência atual e protege a retomada e o arquivo local daquela execução, mas não detecta que o conteúdo legítimo ou malicioso foi substituído no mesmo localizador permanente. A URL decifrada necessariamente chega ao computador no header `Location` e pode ser capturada/reutilizada enquanto continuar válida na hospedagem. Essa é uma aceitação de risco explícita, não uma garantia de sigilo do link ou de integridade de origem.

### 5.8 Extração, instalação e Jogos locais

- A política `NONE` ou `EXTRACT_ARCHIVE` vem do descriptor autorizado, nunca do catálogo visual.
- `CatalogArchiveExtractor` trata ZIP/RAR/7z com limites, staging, cancelamento e defesa contra travessia de caminho/reparse points.
- `PathIdentity` ancora diretórios e arquivos por handle para reduzir trocas de caminho, junctions e links durante download, publicação, movimentação e exclusão.
- Download incompleto permanece retomável; pausar não apaga o parcial.
- Descartar/apagar exige identificar exatamente o artefato dentro da raiz aprovada.
- `Jogos locais` varre a pasta de ROMs escolhida, mostra apenas conteúdo encontrado, permite excluir e volta a mostrar o jogo se ele for instalado novamente.
- A biblioteca visual continua contendo todos os itens autorizados, independentemente do que já está instalado.

## 6. Telas e comportamento do cliente

### 6.1 Login — `PremiumLoginWindow.xaml(.cs)`

- ID da licença e código de ativação de uso único;
- estado da autoridade incorporada;
- mensagens genéricas e fail-closed;
- arte da aeronave, logo e LED do cartão;
- abre a loja somente após sessão e catálogo válidos;
- não salva OTP e não adiciona senha administrativa.

### 6.2 Janela principal — `StoreWindow.xaml(.cs)`

Navegação fixa:

- **Início**: entrada e resumo do catálogo;
- **Biblioteca**: catálogo visual completo e seleção por sistema;
- **Jogos locais**: análise, filtro e exclusão segura do conteúdo instalado;
- **Downloads**: fila, progresso, pausa, retomada, nova tentativa, abertura de pasta e descarte;
- **Sistemas e coleções**: 22 categorias carregadas do catálogo.

Elementos globais:

- sessão ativa e licença mascarada;
- player persistente com oito músicas incorporadas, reprodução automática, anterior, play/pause, próxima, stop, pasta alternativa e volume;
- seleção de pasta de instalação, pasta temporária e pasta de biblioteca;
- botão de suporte e abertura de pasta; no commit auditado, o handler de suporte ainda informa que o canal não está configurado;
- capas responsivas, carrossel, imagem do console, tema por plataforma, vídeo de fundo e LEDs;
- vídeos com preenchimento proporcional centralizado (`UniformToFill`) e sem depender da resolução original.

O player extrai cada música incorporada para cache controlado, confere identidade/hash antes de tocar e mantém o handle verificado durante o uso.

### 6.3 Categorias e contagem atual

| Categoria | Itens | Categoria | Itens |
|---|---:|---|---:|
| Sistema e utilitários | 2 | Emuladores | 17 |
| Jogos retrô | 45 | Nintendo 3DS | 23 |
| Nintendo GameCube | 32 | PlayStation 1 | 119 |
| PlayStation 2 | 70 | PlayStation 2 • BR | 51 |
| PlayStation 3 | 29 | PlayStation 4 | 26 |
| PlayStation 5 | 58 | PSP | 21 |
| PS Vita | 23 | Sega Saturn | 32 |
| Nintendo Switch | 110 | Nintendo Wii | 9 |
| Nintendo Wii U | 9 | Windows | 100 |
| Xbox | 54 | Xbox 360 | 24 |
| Xbox One | 19 | Xbox Series | 29 |

Total: 902.

### 6.4 Assets versionados

- 902 capas mapeadas mais fallback;
- 45 ícones de sistema e 22 ícones de menu;
- 38 vídeos de sistemas;
- 15 vídeos de fundo;
- oito faixas MP3 incorporadas;
- descrições de plataforma e de jogos;
- manifests de integridade para vídeos e mapas de origem das capas.

O pacote não inclui ROMs, jogos nem o localizador permanente da hospedagem.

## 7. Mapa do código do cliente

| Arquivo/camada | Responsabilidade |
|---|---|
| `App.xaml` / `App.xaml.cs` | bootstrap WPF e janela inicial |
| `PremiumLoginWindow.xaml(.cs)` | login, ativação, abertura de sessão e carregamento inicial do catálogo |
| `StoreWindow.xaml(.cs)` | páginas, navegação, temas, mídia, catálogo, local games e orquestração dos downloads |
| `TurboBoxManager.csproj` | versão, WPF, dependências, recursos, assets e metadata das autoridades |
| `Licensing/SuiteAuthorityConfiguration.cs` | envelope público da autoridade de licença |
| `Licensing/SuiteContentAuthorityConfiguration.cs` | envelope público independente da autoridade de conteúdo |
| `Licensing/SuiteProtocol.cs` | contratos v1, serialização canônica e verificação das assertions |
| `Licensing/SuiteMachineIdentity.cs` | chave CNG, DeviceId, descriptor e assinatura RSA-PSS |
| `Licensing/SuiteLicenseClient.cs` | HTTP pinado, ativação, challenge e sessão |
| `Licensing/SuiteSession.cs` | runtime, estado autorizado, heartbeat, expiração e cancelamento |
| `Licensing/SuiteMotherboardInventory.cs` | coleta limitada e normalização do inventário Windows |
| `Licensing/SuiteDeviceInventoryProtocol.cs` | contrato e domínios canônicos do inventário R25 |
| `Licensing/SuiteLicenseClient.Inventory.cs` | publicação do inventário após sessão |
| `Licensing/SuiteInventoryPublicationStateStore.cs` | cache DPAPI do estado de publicação |
| `Catalog/CatalogRepository.cs` | leitura estrita do catálogo visual e associação de assets |
| `Catalog/SuiteContentClient.cs` | catálogo assinado, grants e requisições efêmeras |
| `Catalog/CatalogDownloadService.cs` | fila, Range, retomada, hash local, publicação atômica e estado DPAPI |
| `Catalog/CatalogDownloadJob.cs` | estado observável exibido na página Downloads |
| `Catalog/CatalogArchiveExtractor.cs` | extração segura e controlada |
| `Catalog/CatalogLocalLibraryService.cs` | inspeção e exclusão de jogos instalados |
| `Catalog/CatalogGameLibraryLocator.cs` | localização segura de conteúdo na pasta de ROMs |
| `PathIdentity.cs` | validação de identidade por handle e proteção contra troca de caminho |
| `EmbeddedMusicLibrary.cs` | playlist incorporada, cache e verificação das faixas |
| `LocalDataPaths.cs` | pastas/configuração local com formato e raízes controlados |

## 8. Componentes do servidor

| Projeto | Papel |
|---|---|
| `src/TurboRamaPixOnlineServer` | serviço PIX legado/atual, login administrativo e BFF das páginas Suite |
| `src/TurboRamaSuiteOnlineServer` | API Suite de ativação, sessão, inventário, catálogo e autorização |
| `src/TurboRamaSuiteAdminServer` | operações administrativas, commerce, conteúdo e auditoria isolados |
| `src/TurboRamaSuiteContentGateway` | consumo do grant, decifragem do localizador e redirect direto |
| `src/TurboRamaSuiteContentPublisher` | criação/validação/publicação do snapshot de 902 itens |
| `src/TurboRamaSuiteContentMonitor` | checagem periódica de DNS, TLS, HTTP, Range e metadados sem baixar o arquivo inteiro |
| `src/TurboRamaSuiteContentJanitor` | retenção e limpeza controlada de estado temporário |
| `src/TurboRamaSuiteContentAuthorityTool` | geração/verificação dos artefatos públicos da autoridade de conteúdo |

Serviços esperados no Linux:

- `turborama-pix.service`;
- `turborama-suite-api.service`;
- `turborama-suite-admin.service`;
- `turborama-suite-content-gateway.service`;
- timers/services de publisher, monitor, candidate, reconcile e janitor;
- worker/timer de conexão WhatsApp;
- Nginx e Cloudflare Tunnel como borda;
- PostgreSQL como fonte transacional.

## 9. Rotas

### 9.1 API Suite pública

| Método e rota | Função |
|---|---|
| `GET /health` | processo vivo |
| `GET /ready` | licenciamento habilitado |
| `GET /ready/content` | chave, gateway e snapshot de 902 itens prontos |
| `POST /v1/suite/activations/challenge` | primeiro passo da ativação |
| `POST /v1/suite/activations/complete` | prova e conclusão da ativação |
| `POST /v1/suite/challenges` | challenges de sessão, catálogo e download |
| `POST /v1/suite/sessions` | abrir/renovar sessão |
| `POST /v1/suite/devices/inventory/challenge` | challenge aditivo do inventário |
| `POST /v1/suite/devices/inventory` | envio assinado do inventário |
| `POST /v1/suite-content/catalog/current` | página assinada do catálogo atual |
| `POST /v1/suite-content/downloads/authorize` | grant assinado de download |

### 9.2 Gateway

| Método e rota | Função |
|---|---|
| `GET /health` | processo vivo |
| `GET /ready` | banco, keyring e política prontos |
| `GET /ready/keyring` e provas internas | compatibilidade operacional do keyring |
| `GET /v1/suite-content/artifacts/{grantId}` | consome bearer e retorna redirect direto 307 |

### 9.3 Painel e BFF

| Rota | Função |
|---|---|
| `/admin/login`, `/admin/logout`, `/admin` | autenticação e painel principal |
| `/admin/suite` | licenças, vendas, clientes, presença e ações Suite |
| `/admin/suite/actions/issue-otp` | emissão controlada de código de uso único |
| `/admin/suite/actions/issue-first-claim` | primeira vinculação por venda elegível |
| `/admin/suite/content` | estado e gestão do catálogo/origens |
| `/admin/suite/content/health` | saúde resumida do conteúdo |
| `/admin/suite/content/actions/check` | validar origem candidata |
| `/admin/suite/content/actions/replace` | substituir espelho/localizador com trilha |
| `/admin/suite/content/actions/version` | publicar nova versão controlada |
| `/admin/clientes/{licenseId}` | detalhe administrativo de cliente |
| `/admin/suite/export/audit.csv` | exportação autorizada da auditoria |

As rotas internas do `TurboRamaSuiteAdminServer`, como `/commerce/events`, `/commerce/deliveries/...`, `/content/items` e `/customer-activity/...`, ficam atrás do BFF/socket/credencial de serviço; não devem ser expostas como API pública de navegador.

## 10. Banco de dados e migrations

### 10.1 Blocos de migrations

| Versão | Finalidade |
|---|---|
| 001 | fundação: licenças, dispositivos, challenges, sessões, auditoria e outbox |
| 002 | licença vitalícia com um dispositivo ativo |
| 003 | emissão administrativa/OTP e auditoria |
| 004–009 | lifecycle comercial, deliveries, permissões, transferências e provisionamento |
| 010–016 | catálogo, publicação, permissões, gestão, retenção, modo direto R2 e total 902 |
| 017 | permissões de sessão usadas pelo commerce/runtime |
| 018 | lock/concorrência na transferência de enrollment |
| 019 | atividade do cliente no painel |
| 020 | inventário R25, revisão de mudança, presença e outbox de conexão |
| 021 | preserva histórico de inventário sem bloquear rotação de `sessionId` |

### 10.2 Tabelas críticas

- identidade/autorização: `suite_licenses`, `suite_license_enrollments`, `suite_devices`, `suite_challenges`, `suite_activation_completions`, `suite_sessions`;
- comércio: `suite_commerce_inbox`, `suite_license_deliveries`, `suite_lifecycle_commands`, `suite_transfer_history`;
- conteúdo: snapshots, estado ativo do catálogo, itens, entitlements, grants, origens cifradas, candidatos, jobs e auditoria de conteúdo;
- inventário: `suite_device_inventory_challenges`, `suite_device_inventory`, `suite_device_inventory_events`, `suite_machine_change_reviews`;
- operação: `suite_device_presence`, `suite_connection_notification_outbox`, `suite_audit_events`, `suite_outbox`, `schema_migrations` e ledger de checksums.

### 10.3 Regras de migration

- aplicar em ordem e somente no banco realmente usado pelo serviço;
- conferir SHA-256 dos scripts e ledger;
- fazer backup antes de qualquer alteração;
- usar roles separadas para API, admin, publisher, monitor e gateway;
- não editar tabela manualmente para “liberar” cliente;
- preservar o release e o backup anteriores até o aceite;
- migration forward não deve ser desfeita por apagar histórico.

## 11. Segurança implementada

### 11.1 Confiança e transporte

- duas autoridades independentes: licenciamento e conteúdo;
- envelopes públicos assinados por issuer offline e chaves on-line separadas;
- RSA-PSS-SHA256, JSON estrito/canônico, domínio por operação e `contextHash`;
- TLS normal com cadeia/hostname/revogação mais pin SPKI para metadata Suite;
- proxy, cookies, redirects automáticos e descompressão desabilitados nas chamadas de metadata;
- respostas positivas somente são aceitas com assinatura e contexto exatos;
- rate limit, timeout, correlation ID e mensagens públicas sanitizadas no servidor.

Âncoras públicas incorporadas no binário auditado:

- configuração da autoridade de licença: `20f7f066b654aad700c4733c9b011495a2bb9b52e7a8b3a77e806cdedebfa3e6`;
- SPKI do issuer de licença: `9ba572cc64ccfd9dcada0699ab5e4f43e4662f84c1a82908a1125aa56c987b3a`;
- configuração da autoridade de conteúdo: `56e7a1bd100e5b5a9cd1109c0b90edfcafaf6c1497ae3113551684d872a0ba07`;
- SPKI do issuer de conteúdo: `65631e0aaa9efb75991098f7a68dda462004e5bbc183ae6bf4ff9256397a8dc5`.

Esses são hashes públicos, não chaves privadas.

### 11.2 Segredos do servidor

Chaves privadas, peppers, keyring de URL, credenciais de BFF, chave de inventário e DSNs são lidos de arquivos/credenciais protegidos. Em produção, valores diretos de configuração para segredos de conteúdo são recusados. O Git contém apenas `.env.example` e exemplos de unidades.

### 11.3 Estado local

- cache auxiliar de inventário usa DPAPI `CurrentUser`;
- arquivos de retomada não persistem bearer, URL permanente nem grant;
- a atestação local do download concluído usa DPAPI;
- pastas e arquivos críticos são revalidados por handle;
- código de ativação e buffers criptográficos são limpos quando possível.

### 11.4 Painel

- Cloudflare Access na borda administrativa;
- login próprio da aplicação como segunda barreira;
- cookie `Secure`, `HttpOnly`, `SameSite=Strict` e sessão curta;
- antiforgery em mutações;
- claims/permissões, step-up para ações críticas e auditoria;
- OTP de uso único não deve voltar a ser exibido nem gravado em logs.

## 12. Limitações e riscos conhecidos

1. **EXE sem Authenticode.** O arquivo funcional auditado tem status `NotSigned`. Não é entrega final ao consumidor.
2. **Pacote externo não possui uma assinatura integral independente.** O manifesto SHA-256 existe, mas precisa ser ancorado por assinatura verificável.
3. **ACL da pasta auditada é permissiva.** Uma distribuição em pasta modificável por `Authenticated Users` permite substituição local; instalar sob ACL restrita.
4. **Download direto não tem digest oficial nem sigilo permanente no cliente.** A troca de bytes no mesmo localizador não é detectada por uma fonte de verdade externa, e a URL entregue no redirect pode ser capturada/reutilizada enquanto permanecer válida.
5. **Redirect externo amplia a confiança.** O cliente aceita um destino absoluto HTTPS:443 autorizado pelo gateway, usa validação TLS normal e não permite redirect adicional; o handler direto não possui pin próprio e pode obedecer à configuração de proxy do Windows.
6. **Rollback do catálogo entre reinicializações.** A sequência é validada durante o snapshot atual, mas não existe high-water mark persistente comprovado para impedir retorno a snapshot antigo ainda assinado após reiniciar.
7. **`TPM_BOUND` não é atestação remota.** Não existem quote/AK/EK verificáveis no protocolo atual; binding é declaração validada localmente.
8. **Identidade é por perfil Windows.** A chave CNG é `UserKey`; outro SID no mesmo computador cria outra identidade.
9. **Perda da chave cria identidade nova.** Ainda falta fluxo explícito de recovery/rebind que diferencie incidente de primeira instalação.
10. **`TPM_PREFERRED` pode permanecer em software.** Depois do fallback, não há migração automática para TPM quando ele volta.
11. **Processos do mesmo usuário.** “Não exportável” impede exportar os bytes da chave, mas não isola seu uso/remoção de todo processo com os mesmos direitos do perfil.
12. **Política de enrollment atual.** O servidor inspecionado exige `SOFTWARE_ONLY` no enrollment prebound; habilitar autoridade `TPM_REQUIRED`/`TPM_PREFERRED` sem mudar e testar o servidor causará incompatibilidade.
13. **Cobertura de migration/CI do servidor.** O workflow `suite-candidate.yml` inspecionado aplica a lista de migrations somente até 014, `apply-suite-content-migrations.sh` termina em 016 e o gatilho de push não cobre nominalmente a branch atual de vendas. A auditoria também não encontrou execução do projeto `TurboRamaSuitePostgres.Tests` nesse workflow. As migrations 017–021 e a branch efetivamente implantada precisam entrar no gate contínuo. Isso é crítico porque a gravação transacional de sessão também escreve presença/outbox, cujas tabelas e permissões surgem na migration 020; binário novo sobre banco até 016 pode resultar em HTTP 500. O `/ready` atual apenas confirma o kill switch e não prova banco/schema/grants; `/ready/content` não cobre presence/outbox.
14. **Documentação antiga está desatualizada.** `docs/PRODUCTION-READINESS.md` ainda descreve catálogo/backend como não implementados. Use seus critérios de release, mas use este documento para o estado funcional de 03/09/2026.
15. **Aceites finais independentes.** Ainda precisam de evidência registrada: download real completo/Range após o último deploy, presença/outbox idempotente, instalação/upgrade/rollback em Windows limpo, carga/soak e pentest. Os testes existentes não substituem E2E HTTP real de sessão expirada, transação sessão+presença+outbox, catálogo 902 e grant+307.
16. **Privilégio do worker de WhatsApp.** O desenho inspecionado entrega ao worker um token administrativo amplo e o usa via argumento de `curl`. Deve existir credencial separada, limitada somente a lease/complete da outbox, e nenhum token deve aparecer em argv.
17. **Recovery/PITR ainda é gate documental.** Não há evidência consolidada de restore automatizado nem de ensaio completo com RPO/RTO medidos.
18. **Suporte e atualização do cliente.** O botão Suporte ainda informa canal não configurado, e `Atualizar-Turborama.cmd` é apenas um aviso de processo removido, não um updater seguro funcional.
19. **Edge e unidades incompletos no repositório.** O exemplo Nginx cobre as rotas de conteúdo, mas não documenta sozinho toda a exposição de ativação/challenge/session; também não há unit completa versionada para a API Suite principal. O estado real precisa ser obtido de `systemctl cat` e da configuração de borda privada.
20. **Down migrations destrutivas existem.** Não devem ser usadas como rollback de produção. O rollback seguro usa binário compatível/kill switch e correções forward, com backup testado.
21. **Inventário e geração de revogação.** A validação de inventário exige sessão ativa, mas a auditoria não encontrou comparação explícita da geração de revogação da sessão com a licença nesse fluxo; manter teste e correção dedicados antes de elevar esse inventário a sinal de autorização.

## 13. Construção do cliente

### 13.1 Pré-requisitos

- Windows 10/11 x64;
- PowerShell 7;
- SDK .NET `10.0.400` conforme `global.json`;
- dependências restauradas pelo `NuGet.Config` e `packages.lock.json`;
- árvore Git limpa para candidato assinado.

### 13.2 Gate de origem e testes

```powershell
pwsh -NoProfile -File .\tools\Test-ReleaseSource.ps1
dotnet restore .\tests\CatalogVerifier\CatalogVerifier.csproj --locked-mode -p:Configuration=Release
dotnet run --project .\tests\CatalogVerifier\CatalogVerifier.csproj -c Release --no-restore -- .\Assets\Catalog\catalog.json
```

O verificador cobre catálogo/assets, contratos Suite, conteúdo, inventário, templates WPF, downloads retomáveis, extração, caminhos e movimentação entre volumes.

### 13.3 Build funcional não distribuível

```powershell
pwsh -NoProfile -File .\tools\Build-Production.ps1 -UnsignedStaging -AllowDirty
```

Esse modo produz evidência local e deve continuar marcado como não aprovado para distribuição. `-AllowDirty` é permitido somente nesse staging local; quando a árvore estiver limpa, ele pode ser omitido.

### 13.4 Candidato assinado

O modo assinado de `tools/Build-Production.ps1` exige:

- certificado Authenticode e timestamp RFC 3161;
- tag anotada e assinada `v2.0.0`, publicada e idêntica ao upstream;
- chave pública/fingerprints GPG aprovados;
- hashes independentes das toolchains PowerShell, PSHOME, Git, GPG, SDK .NET e SignTool;
- envelopes públicos e hashes exatos das duas autoridades;
- worktree isolado, ambiente allowlist e origem limpa;
- manifesto, SBOM, third-party notices e gate do pacote.

O comando completo e todos os argumentos obrigatórios permanecem no `README.md`. Nunca passe chave privada ao script, ao Git ou ao chat.

### 13.5 Saída esperada

- `Turborama.exe` autocontido e single-file;
- assets permitidos fora do PE;
- `RELEASE-MANIFEST.json` em candidato assinado, ou evidência funcional equivalente no staging auditado;
- `PACKAGE-SHA256SUMS.txt`;
- `Turborama.spdx.json` SPDX 2.3;
- `THIRD-PARTY-NOTICES.txt` e notices do runtime;
- evidência do commit, branch, autoridade, catálogo e resultado de cada gate.

## 14. Construção e implantação do servidor

### 14.1 Antes de compilar

1. `git fetch --all --prune`;
2. conferir branch, commit remoto, diff e worktree;
3. registrar o release carregado atualmente por `systemctl cat`;
4. fazer backup de schema/configuração e gerar checksum fora da área pública;
5. confirmar que nenhum segredo está versionado;
6. restaurar todos os projetos em modo locked.

### 14.2 Testes mínimos de código

- builds .NET 8 com warnings tratados;
- `TurboRamaSuiteOnlineServer.Tests`;
- `TurboRamaSuitePostgres.Tests` com PostgreSQL real de teste;
- self-tests de PIX, admin, publisher, monitor, janitor e authority tool;
- `tests/Test-TransportPolicy.ps1`;
- `tests/check-suite-content-permissions.sql`;
- integração do admin e teste de browser;
- auditoria de pacotes vulneráveis;
- migrations 001–021 em banco vazio e upgrade de uma cópia do schema de produção.

### 14.3 Deploy atômico

1. publicar cada serviço em novo diretório de release imutável;
2. conferir owner/ACL e hashes;
3. aplicar migrations aprovadas no banco correto;
4. validar roles e credenciais protegidas;
5. alterar o drop-in do `systemd` para o novo release;
6. `systemctl daemon-reload`;
7. reiniciar somente os serviços afetados;
8. conferir `WorkingDirectory`, `ExecStart`, PID, DLL e hash carregados;
9. validar `/health`, `/ready`, `/ready/content` e gateway;
10. executar o teste real de sessão, catálogo e download controlado;
11. manter release anterior e backup até o aceite.

Health 200 comprova somente que o processo responde. O `/ready` atual também não consulta banco, schema ou grants: ele comprova que o serviço Suite está habilitado. Nem health nem readiness substituem uma sessão real e uma consulta explícita às migrations/tabelas/permissões.

## 15. Testes de aceite ponta a ponta

### 15.1 Identidade e ativação

- primeira ativação válida;
- código errado, expirado e reutilizado;
- entrada posterior sem código;
- licença suspensa/revogada;
- DeviceId, SPKI ou fingerprint divergentes;
- reinstalação no mesmo perfil e transferência de hardware pelo painel;
- equipamento sem TPM usando somente a política explicitamente aprovada.

### 15.2 Sessão

- `session.open`: challenge 200 e sessão 200;
- heartbeat contínuo e renovação;
- replay, challenge expirado e contexto trocado;
- encerramento do cliente e expiração;
- revogação durante download;
- somente uma sessão/dispositivo conforme a licença.

### 15.3 Inventário e presença

- coleta CIM e fallback permitido;
- dados sensíveis cifrados e painel mascarado;
- mesma placa classificada corretamente;
- provável troca de placa abre revisão sem autorizar automaticamente;
- presença ONLINE/OFFLINE dentro da janela esperada;
- apenas um evento de conexão por janela de idempotência;
- falha do WhatsApp sem impacto na sessão.

### 15.4 Catálogo e conteúdo

- cobertura exata de 902 IDs;
- `READY + MAINTENANCE = 902`;
- paginação completa, ordem, assinatura, expiração e keyId;
- item em manutenção não oferece botão funcional;
- grant de uso único, expirado e replay negado;
- bearer não aparece no disco/log e não é encaminhado à hospedagem;
- redirect adicional, HTTP, porta diferente e URI malformada negados;
- download inicial 200, retomada 206, ETag/Last-Modified e Content-Range coerentes;
- interrupção, pausa, retomada, falta de espaço, queda de rede e reinício;
- extração segura e exclusão controlada;
- arquivo instalado aparece em Jogos locais, desaparece ao excluir e reaparece ao reinstalar.

### 15.5 Release Windows

- assinatura Authenticode válida e timestamp verificável;
- SmartScreen/antivírus e instalação em usuário comum;
- ACL do diretório sem escrita para usuários não autorizados;
- instalação limpa, upgrade e rollback;
- manifesto/SBOM/notices e todos os assets verificados;
- nenhuma chave privada, URL permanente ou credencial encontrada no pacote;
- interface responsiva em resoluções/DPI suportados e vídeos sem distorção.

## 16. Evidência funcional consolidada

O probe compatível com o cliente real confirmou, contra produção:

- metadata da autoridade de conteúdo disponível e assinaturas válidas;
- challenge `session.open` HTTP 200;
- `POST /v1/suite/sessions` HTTP 200;
- sessão autorizada;
- catálogo local com 902 itens;
- todos os challenges `catalog.read` HTTP 200;
- todas as páginas de `/v1/suite-content/catalog/current` HTTP 200;
- resultado `AUTHORIZED`;
- 898 itens `READY` e 4 `MAINTENANCE`.

Pacote funcional auditado:

- fonte limpa no commit do cliente informado na seção 2;
- produção backend habilitada;
- EXE SHA-256 `dd0a83a695a7a20209335238c7e24d2e98a4e179f7dbf5336e0d047a1b652b02`;
- SBOM SHA-256 `77b1216df667a6aebda5e0d7908a20455f1944d067b72d229a043eb53b2e6264`;
- cinco arquivos raiz conferidos contra o manifesto funcional;
- auditoria NuGet sem pacote vulnerável reportado no conjunto restaurado;
- `consumerDistributionApproved: false` e `authenticodeStatus: NotSigned` registrados pela própria evidência do build.

## 17. Diagnóstico rápido

| Sintoma | Camada provável | Verificação |
|---|---|---|
| autoridade indisponível antes de qualquer request | cliente/build | metadata incorporada, validade, hash e relógio |
| nenhum request no servidor | DNS/TLS/borda/cliente | Nginx, Cloudflare, pin e correlação |
| challenge 403 | licença/enrollment/dispositivo | produto, status, DeviceId, binding e fingerprint |
| challenge 200 e sessão 500 | banco/release/migration | journal com correlação, migration 021, grants e DLL ativa |
| `ACTION_INVALID` em `catalog.read` | código ou constraint antigos | allowlist do `SuiteService`, constraint de challenge e release realmente carregado |
| sessão 200 e catálogo 409 | sessão não corrente/contexto | sessionId, expiração e challenge |
| catálogo 200 e item em manutenção | conteúdo | estado do item, último monitor e painel de conteúdo |
| grant 200 e download falha | gateway/origem/Range | grant, keyring, allowlist, HTTP 307, TLS da origem e validadores |
| jogo baixado não aparece localmente | pasta/locator local | raiz escolhida, extensão, extração e scanner |

Registrar sempre horário UTC, endpoint, status, commit, release e correlation ID. Não registrar corpos contendo dados de cliente ou credenciais.

## 18. Backup, recovery e rollback

Antes de cada deploy:

- backup consistente do PostgreSQL/schema com checksum;
- cópia dos drop-ins e nomes dos arquivos de credenciais, sem copiar segredos para Git;
- commit, hash dos binários e lista de migrations;
- release anterior preservado;
- exportação segura do material público das autoridades;
- backup cifrado das chaves privadas conforme runbook offline e teste periódico de restauração.

Rollback do servidor:

1. parar somente o serviço afetado;
2. apontar o drop-in para o release anterior conhecido;
3. `daemon-reload` e reinício;
4. validar PID, DLL/hash, health e readiness;
5. repetir challenge/sessão controlados;
6. não reverter migration destrutivamente sem runbook e backup testado.

Recovery de cliente:

- parcial íntegro deve continuar retomável;
- perda da chave CNG exige transferência/rebind administrativo, não criação silenciosa de licença;
- troca de placa usa o fluxo de transferência do painel;
- não enviar chave privada de autoridade ao computador do cliente.

## 19. Critério objetivo para liberar ao consumidor

Somente liberar quando todos estiverem verdadeiros:

- [ ] commit do cliente aprovado, limpo, publicado e tag assinada;
- [ ] commit/release do servidor identificado e CI cobrindo migrations 001–021;
- [ ] Authenticode comercial válido e timestamp RFC 3161;
- [ ] pacote completo/instalador assinado e ACL de instalação restrita;
- [ ] sessão, heartbeat, catálogo e inventário aprovados no Windows limpo;
- [ ] download real completo e retomado aprovado após o deploy final;
- [ ] presença e outbox idempotente confirmadas;
- [ ] backup e rollback exercitados;
- [ ] carga/soak e pentest aprovados ou risco formalmente aceito;
- [ ] riscos do download direto e da identidade software formalmente aceitos;
- [ ] nenhuma credencial, chave privada, URL permanente ou dado pessoal no Git/pacote/log;
- [ ] direitos de distribuição e termos comerciais aprovados pelo responsável.

Até lá, o nome correto do artefato auditado é **build funcional de produção não assinado**, e não release final para consumidor.

## 20. Ordem segura para próximas alterações

1. preservar e publicar primeiro o estado atual;
2. abrir branch específica;
3. declarar exatamente qual camada será alterada;
4. atualizar código e testes da mesma camada;
5. não alterar design, preço, contrato v1, licença ou autoridades por efeito colateral;
6. rodar gates do cliente e/ou servidor;
7. gerar novo artefato e hashes;
8. testar contra staging ou operação controlada;
9. implantar atomicamente;
10. registrar aceite e rollback no mesmo retorno.

Esse procedimento evita repetir mudanças fragmentadas, recompilações com autoridade errada e divergência entre o código no Git, o EXE entregue e o DLL realmente executado no servidor.
