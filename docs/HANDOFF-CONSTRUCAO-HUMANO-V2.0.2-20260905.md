# Handoff humano de construção e manutenção — Turborama Suite 2.0.2

Data: 05/09/2026.
Público: proprietário, desenvolvedor que receber o projeto e operador de compilação.
Objetivo: sair do código no GitHub, construir um pacote Windows completo, compreender os componentes e testar sem repetir os erros históricos.

Este documento não contém senhas, licenças reais, OTPs, tokens, DeviceIds de clientes ou chaves privadas.

## 1. Comece pela versão correta

A base funcional adotada é 86e7ae1ec54b304b080b3ecc71b37c7f0e3dd5ba.
Esse é o commit da correção 2.0.1 que incorporou as autoridades públicas.
A evolução atual está na branch codex/v2.0.2-music-cleanup-final.
Ela contém essa base e as correções posteriores; não precisa juntar fontes de outras branches.
O projeto tem versão 2.0.2.
O identificador preciso de uma entrega é o commit completo, acompanhado do SHA-256 do executável.

Não confundir:

- branch publicada com promoção para main;
- código-fonte com programa compilado;
- artifact do Actions com asset de uma Release;
- versão numérica com o hash do binário;
- build aprovado com teste real de login/download aprovado;
- ausência de Authenticode com ausência das autoridades internas.

## 2. Escopo deste commit

O pedido atual inclui:

1. Colocar jogos populares/reconhecidos primeiro em PS3, PS4, PS5, Xbox original, Xbox 360, Xbox One, Xbox Series, Switch e Windows.
2. Pesquisar referências públicas, mas usar apenas jogos existentes no catálogo.
3. Colocar Aperta Start como primeira faixa da playlist.
4. Sortear a faixa inicial: 40% para Aperta Start e 60% divididos igualmente entre as oito restantes.
5. Subir para o GitHub e executar a compilação lá.
6. Explicar a construção e a manutenção neste handoff.

Não faz parte deste pedido:

- mudar autenticação ou licenciamento;
- trocar/gerar autoridades ou chaves;
- reautorizar máquinas;
- alterar servidor, PIX, preços ou WhatsApp;
- modificar a autorização de downloads;
- apagar compilações, caches ou discos.

Os avisos de download por WhatsApp foram cancelados pelo proprietário no histórico.
Não reintroduzir esse requisito.
As mudanças de ordenação/música não exigem alteração de protocolo.

## 3. Decisão vigente sobre assinatura

O proprietário optou por gerar sem assinatura comercial Authenticode.
Isso NÃO significa executar sem autoridades de licença e conteúdo.
O workflow atual incorpora os quatro arquivos públicos e usa UnsignedStaging.

A saída conserva o nome técnico UNSIGNED-NOT-FOR-DISTRIBUTION.
Esse rótulo pertence ao pipeline existente; não indica que as autoridades foram omitidas.
Não renomear o pacote para aparentar assinatura digital inexistente.
O Windows pode apresentar avisos de editor desconhecido.

O modo Signed tem requisitos adicionais de Authenticode, timestamp, tag GPG e pins de ferramentas.
Ele é separado do caminho atual.
Não chamar release-final.yml como atalho sem atender aos seus requisitos.

## 4. O que é o programa

Turborama é um cliente desktop Windows em C# e WPF.
O executável se chama Turborama.exe.
O arquivo de projeto é TurboBoxManager.csproj.
O namespace histórico TurboBoxManager continua no código e não identifica outro produto.

O programa possui login, catálogo, navegação por sistemas, biblioteca, jogos locais, downloads, extração, vídeo de fundo e player musical.
O catálogo visual atual tem 22 categorias e 902 itens.
A presença de uma capa não autoriza baixar um jogo.
O conteúdo exige sessão, capacidade e descriptor/grant válidos.

O repositório não contém ROMs nem concede direitos sobre os jogos.
Compilar o cliente não cria clientes/licenças e não implanta o servidor.
A API e a operação comercial pertencem a outro repositório e outra implantação.

## 5. Fontes de verdade

| Assunto | Fonte |
|---|---|
| Cliente | https://github.com/luziellacerda/TRUBORAMA-SUITE |
| Servidor | https://github.com/luziellacerda/Servidor-pix |
| Linha desta evolução | codex/v2.0.2-music-cleanup-final |
| Base com autoridades | 86e7ae1ec54b304b080b3ecc71b37c7f0e3dd5ba |
| Catálogo visual | Assets/Catalog/catalog.json |
| Autoridades | authority/public e os hashes fixos no workflow |
| CI Windows | .github/workflows/build.yml |
| Construção consolidada | tools/Build-Production.ps1 |
| Pesquisa e sequência dos jogos | ORDEM-POPULARIDADE-20260905.md |
| Histórico técnico anterior | TURBORAMA-SUITE-R25-DOCUMENTACAO-MESTRA-PONTA-A-PONTA-20260903.md |
| Correção de autoridade | RELEASE-NOTES-v2.0.1.md |

Os documentos de 03/09 são registros históricos.
Passagens que dizem “autoridades externas ao Git”, “oito músicas” ou “staging sem autoridade” não descrevem esta linha.
Agora existem quatro arquivos públicos versionados e nove músicas.
O workflow atual e a correção 2.0.1 prevalecem nesses pontos.

Não escolher um EXE só pela data da pasta ou pelo nome “final”.
Conferir commit, manifesto e SHA-256.
Um atalho antigo pode continuar abrindo a compilação errada.

## 6. Mapa de arquivos para manutenção

| Arquivo/pasta | Função |
|---|---|
| App.xaml / App.xaml.cs | Entrada e recursos WPF |
| PremiumLoginWindow.xaml | Layout de acesso, logo e iluminação |
| PremiumLoginWindow.xaml.cs | Ações de login e abertura de sessão |
| StoreWindow.xaml | Loja, navegação, carrossel, biblioteca, downloads e player |
| StoreWindow.xaml.cs | Coordenação das páginas, seleção e operações de mídia/conteúdo |
| Catalog/CatalogRepository.cs | Carga, pesquisa, ordenação e paginação |
| Catalog/CatalogModels.cs | Categorias, itens, descriptors e estados |
| Catalog/CatalogPopularityOrder.cs | Prioridades editoriais por categoria e ID |
| Catalog/CatalogImageResolver.cs | Imagens locais e fallback |
| Catalog/CatalogGameDescriptionStore.cs | Descrições |
| Catalog/SuiteContentClient.cs | Comunicação autenticada de conteúdo |
| Catalog/CatalogDownloadService.cs | Transferência, retomada e integridade |
| Catalog/CatalogDownloadJob.cs | Estado da operação |
| Catalog/CatalogArchivePolicy.cs | Reconhecimento de ZIP/RAR/7z autorizado |
| Catalog/CatalogArchiveExtractor.cs | Extração limitada, validada e confinada |
| Catalog/CatalogGamePackageOrganizer.cs | Organização final dos pacotes |
| Catalog/CatalogPackageTextSanitizer.cs | Tratamento restrito de TXT |
| Catalog/CatalogExtractionCompletionCleanup.cs | Limpeza após conclusão segura |
| Catalog/CatalogGameLibraryLocator.cs | Localização da biblioteca |
| Catalog/CatalogLocalLibraryService.cs | Inspeção e exclusão de jogos locais |
| EmbeddedMusicLibrary.cs | Recursos musicais, integridade, cache e sorteio |
| LocalDataPaths.cs | Configurações locais de pastas |
| PathIdentity.cs | Identidade de arquivos/diretórios e confinamento |
| Licensing/ | Autoridades, sessão, máquina, inventário e protocolos |
| Assets/ | Catálogo, capas, ícones, vídeos e músicas |
| tests/CatalogVerifier/ | Verificadores automatizados |
| tools/ | Build, gates, manifesto, SBOM e manutenção de assets |
| .github/workflows/ | Automação no GitHub |

StoreWindow.xaml.cs concentra bastante coordenação.
Por isso, uma alteração pequena deve ser isolada em método específico e testável.
Não reescrever a janela inteira para mudar uma ordem ou uma faixa.

## 7. Inicialização e login, passo a passo

1. Windows abre Turborama.exe.
2. A tela de login aparece.
3. O cliente carrega a autoridade incorporada no assembly.
4. A autoridade precisa existir, corresponder aos bytes aprovados, estar assinada e vigente.
5. O protocolo Suite trata licença, challenge e prova da máquina.
6. A resposta positiva deve obedecer ao envelope assinado esperado.
7. Com sessão válida, a loja é aberta.
8. O catálogo visual é combinado com autorizações de conteúdo.
9. A sessão é acompanhada por heartbeat.
10. Operações não continuam autorizadas quando a sessão deixa de ser válida.

Essas etapas não são equivalentes.
“ID já ativado” não prova que a abertura de sessão seguinte foi aceita.
“Temporariamente indisponível” não identifica sozinho se a causa é rede, TLS ou API.
“AUTORIDADE DO TURBORAMA SUITE INDISPONÍVEL NESTA COMPILAÇÃO” exige conferir configuração/verificação local.
Esse erro pode ocorrer antes de qualquer tentativa chegar ao servidor.

## 8. As autoridades e a identidade da máquina

### 8.1 Autoridade de licença

suite-authority-envelope.json contém a configuração pública aprovada.
suite-authority-issuer.spki.der permite verificar sua assinatura.
São arquivos públicos, não senhas.
Seus SHA-256 são fixados no build.

### 8.2 Autoridade de conteúdo

content-authority-envelope.json e content-authority-issuer.spki.der são a cadeia separada para conteúdo.
Não trocar uma cadeia pela outra.
Login funcionando não dispensa a configuração de conteúdo.

### 8.3 Chaves privadas

Chaves privadas das autoridades pertencem à operação segura do servidor/cerimônia.
Não ficam no EXE e não entram no Git.
Uma chave perdida não autoriza criação/troca silenciosa de outra.
Rotação exige decisão humana, atualização coordenada e material público aprovado.

Este documento não registra localização de backups privados.
O Git não substitui backup seguro de chave privada.
Somente o material público necessário ao cliente está versionado aqui.

### 8.4 TPM e software

SuiteMachineIdentity.cs usa provedores CNG do Windows.
O código contempla TPM obrigatório, TPM preferido com fallback e software.
SOFTWARE_BOUND_ONLINE existe quando permitido pela política.
Não prometer funcionamento sem TPM para uma licença que exija TPM.

Número da placa-mãe e inventário são auxiliares.
Eles não são, isoladamente, todo o vínculo criptográfico da máquina.
Não apagar chaves CNG, configuração ou identidade para “consertar o EXE”.

### 8.5 Authenticode

É a assinatura do arquivo apresentada ao Windows.
Não substitui as autoridades internas nem autentica a licença do comprador.
Nesta linha, a ausência dessa assinatura é uma decisão do proprietário.
As verificações de licença/conteúdo continuam obrigatórias.

## 9. Quatro arquivos e quatro hashes obrigatórios

Todos os arquivos estão em authority/public.

| Arquivo | SHA-256 |
|---|---|
| suite-authority-envelope.json | 20F7F066B654AAD700C4733C9B011495A2BB9B52E7A8B3A77E806CDEDEBFA3E6 |
| suite-authority-issuer.spki.der | 9BA572CC64CCFD9DCADA0699AB5E4F43E4662F84C1A82908A1125AA56C987B3A |
| content-authority-envelope.json | 56E7A1BD100E5B5A9CD1109C0B90EDFCAFAF6C1497AE3113551684D872A0BA07 |
| content-authority-issuer.spki.der | 65631E0AAA9EFB75991098F7A68DDA462004E5BBC183AE6BF4FF9256397A8DC5 |

São oito entradas no script: quatro caminhos e quatro hashes.
Não atualizar hash apenas para fazer passar outro arquivo.
Uma rotação exige aprovação e compatibilidade com o servidor.
Guardar a configuração no Git não prolonga sua validade indefinidamente.
Compilações futuras podem exigir renovação autorizada dos envelopes.

## 10. Interface, capas, vídeos e decisões visuais

Capas ficam em Assets/Catalog/Images.
Ícones ficam em MenuIcons e SystemIcons.
Descrições, vídeos e seus índices são recursos separados.
Parte dos JSONs é incorporada no assembly; imagens e vídeos externos acompanham o pacote.

CreateResponsiveVideoPlayer usa o tamanho real do host.
Width e Height acompanham o espaço disponível.
UniformToFill preenche proporcionalmente: pode recortar bordas, mas não distorce e não reduz o painel.
Não trocar por Fill para “resolver tamanho”; isso pode esticar a imagem.

O mapeamento atual conserva vídeo Xbox nas quatro categorias Xbox.
As demais usam Turborama-background.mp4.
A camada de cor segue o tema nos contextos previstos.
Essas são as regras do código atual, não cada experimento mostrado nas capturas antigas.

A interface possui player global, texto separado da capa e foto do console ao lado.
Há estilos e testes próprios para controles, LEDs, descrição e carrossel.
Nesta alteração não houve reformulação visual.

O histórico de pedidos visuais foi iterativo.
Espelhamento de vídeo, fades, HUD e cores foram alterados várias vezes.
Não reaplicar uma preferência antiga quando uma mensagem posterior a substituiu.
Alterar um asset exige atualizar o inventário de integridade aplicável e reconstruir o pacote.

## 11. Ordem dos jogos nesta versão

CatalogPopularityOrder.cs contém a tabela imutável de prioridades.
A chave é categoria + ID exato do catálogo.
Foram priorizados 104 jogos em nove plataformas.

CatalogRepository.Query executa:

1. Filtrar categoria.
2. Filtrar pesquisa.
3. Ordenar por prioridade editorial.
4. Usar Order anterior para os demais casos.
5. Desempatar por título.
6. Calcular as páginas.
7. Retornar a página pedida.

Assim, pesquisa e navegação usam a mesma sequência.
Não foram removidos jogos.
As outras 13 categorias permanecem com a ordenação anterior.
Itens sem prioridade continuam depois dos destaques na ordem antiga.

A prioridade não modifica título, descriptor, autorização, hash, URL ou política de extração.
Também não torna baixável um item em manutenção.
Não há chamada a sites de ranking na abertura do programa.

A pesquisa completa está em [ORDEM-POPULARIDADE-20260905.md](ORDEM-POPULARIDADE-20260905.md).
Trata-se de curadoria com downloads/vendas históricos e reconhecimento público.
Não é telemetria de jogadores simultâneos.
A documentação identifica as fontes e preserva os nomes existentes, inclusive abreviações.

## 12. Música incorporada e sorteio

O áudio Aperta Start foi fornecido pelo proprietário.
A fonte executada é Assets/Music/Aperta Start.mp3.
Ela já foi convertida/incorporada nas correções anteriores.
Não é necessário converter o M4A novamente em cada build.

Tamanho do recurso: 4.601.143 bytes.
SHA-256: DC034CC88E8E567FE553EE5829F45CEB9BD353E1A1D4F278AE20B87CFE45311A.
Nome lógico: Turborama.Music.ApertaStart.mp3.

A playlist fixa é:

1. Aperta Start.
2. Turborama - Faixa 01.
3. Turborama - Faixa 02.
4. Turborama - Faixa 03.
5. Turborama - Faixa 04.
6. Turborama - Faixa 05.
7. Turborama - Faixa 06.
8. Turborama - Faixa 07.
9. Turborama - Faixa 08.

Primeira posição e primeira reprodução de toda abertura são coisas diferentes.
A posição é fixa; o ponto inicial é sorteado.
A ativação ocorre no fluxo existente do player na loja, após a entrada.
Não foi adicionado som à etapa de autenticação.

SelectStartupIndex sorteia um inteiro entre 0 e 99.
De 0 a 39, escolhe o índice zero, Aperta Start.
Nos outros resultados, escolhe uniformemente uma das oito restantes.
Aperta Start tem 40%; cada outra faixa tem 7,5%.

Não significa quatro aparições garantidas em cada dez aberturas.
Um sorteio independente permite repetições consecutivas.
A playlist inteira não é embaralhada, somente a faixa inicial.
Próxima/anterior, pausar, desligar e volume conservam o fluxo anterior.
Pastas musicais externas não recebem a preferência de Aperta Start.

O cache está em LocalAppData/Turborama/Music/built-in-v3.
Tamanho e hash são verificados.
Não distribuir a pasta Downloads do proprietário.
Não apagar arquivo de cache em uso pelo player.

## 13. Fluxo de download até instalação

1. Usuário escolhe um item.
2. Cliente exige sessão/capacidade e descriptor autorizado.
3. Serviço de conteúdo fornece autorização de download.
4. Arquivo é transferido com controles de destino e retomada.
5. Tamanho e SHA-256 são verificados.
6. Compactado reconhecido pode seguir para extração no fluxo de jogos.
7. Extrator usa área intermediária e valida caminhos/limites.
8. TXT elegíveis do pacote são tratados.
9. Conteúdo é organizado na biblioteca.
10. Marcador/inventário registra a instalação.
11. Limpeza ocorre após conclusão, preservando recuperação quando necessário.
12. Jogos locais passa a reconhecer o conteúdo físico.

HTTP 200 não prova instalação concluída.
Download pode terminar e extração falhar por espaço, conflito, caminho ou arquivo inválido.
Não marcar sucesso apenas pela resposta HTTP.
Não remover as verificações de integridade para concluir mais rápido.

## 14. Organização em TruboRoms/roms

Estrutura pretendida para pacotes no formato reconhecido:

~~~text
G:/
  TruboRoms/
    roms/
      Xbox One/
        arquivos ou subpastas do jogo
      PlayStation 4/
        arquivos ou subpastas do jogo
      outros sistemas/
~~~

Os nomes finais vêm da estrutura válida do pacote.
Não achatar subpastas necessárias ao funcionamento do jogo.
O invólucro de distribuição sistema/roms é removido quando corresponde ao formato suportado.

O erro anterior gerava uma sequência semelhante a:

~~~text
TruboRoms/roms/Xbox One/<id>/sistema/roms/<sistema>/<jogo>
~~~

CatalogGamePackageOrganizer publica o conteúdo validado de sistema/roms diretamente na raiz mestre.
Confere destinos, arquivos existentes e conflitos.
Conteúdo existente igual pode ser reutilizado; conteúdo diferente não deve ser sobrescrito silenciosamente.
Falhas parciais podem preservar recuperação em .turborama-recovery.
Comprovantes ficam em .turborama-installed.

Essas pastas não são caches de compilação.
Não apagá-las indiscriminadamente.
Participam da recuperação, inspeção e exclusão controlada.
Jogos locais mostra conteúdo físico reconhecido.
Depois de reinstalar e atualizar a análise, o jogo volta a aparecer.

## 15. Tratamento de TXT e remoção de videos

Atualização solicitada em 05/09: pastas com o nome exato images, em qualquer nível do pacote, também são descartadas em todas as extrações ZIP/RAR/7z do cliente.
A comparação ignora maiúsculas/minúsculas. images-backup e arquivos PNG/JPG fora dessas pastas não são removidos.
O extrator lê e valida os bytes descartados, mas não os publica no destino nem no inventário final.
A recuperação autenticada usa a mesma regra; limites, hash do compactado e cancelamento permanecem.
Essa regra aplica-se somente ao conteúdo do pacote, nunca aos Assets do Turborama.
Se um pacote tiver recursos indispensáveis dentro de uma pasta images, o fornecedor deve adequar sua estrutura: a regra pedida remove essa pasta independentemente do tipo dos arquivos.
Os novos diretórios de comprovantes .turborama-installed são ocultos no Windows, mas não apagados.

Na conferência específica de H:/TruboRoms/roms, .staging e Xbox One estavam vazias; artifacts guardava uma atestação residual.
xboxone/Cuphead.bat apontava para xboxonexboxseriesxinstallars/Cuphead; essas duas pastas são relacionadas e não devem ser renomeadas ou mescladas arbitrariamente.
A remoção das 125 imagens auxiliares desse pacote exige retirar somente suas entradas do comprovante local, preservando o restante.

CatalogPackageTextSanitizer inspeciona somente .txt até 4 MiB.
Binários e textos sem padrões reconhecidos são preservados.

Transformações existentes:

- Sambox passa para Turbobox no nome/conteúdo elegível.
- Site antigo turbobox.club passa para https://turbobox.lzgames.com.br/.
- Telefone antigo reconhecido passa para 82993474007.
- A instrução de descompactar/substituir vira tutorial simples de TruboRoms/roms.
- Texto identificado pela marca e que não possa ser tratado com segurança pode ser descartado.
- O tutorial orienta conferir/excluir pela opção Jogos locais.

Não é uma substituição global de palavras no computador.
O tratamento ocorre dentro do pacote durante a extração.
Um compactado apenas baixado ainda não passou por isso.

Na organização de sistema/roms, pastas videos sob esse conteúdo são removidas antes da publicação final.
Isso não autoriza excluir Assets/BackgroundVideos ou Assets/Catalog/SystemVideos.
Os vídeos da interface continuam obrigatórios.

## 16. Histórico de erros e correções rastreáveis

| Commit | Significado |
|---|---|
| 981eafd | Consolidação histórica da Suite R25 homologada |
| 72c72f6 | Autoplay testado sem depender da confirmação de codec do CI |
| c133f49, d98d9c7, 80c7b81 | Ajustes do staging em tag/detached HEAD e manifesto |
| 7433b5d | Preparação do caminho assinado, não a correção de autoridade do unsigned |
| 86e7ae1 | Arquivos públicos e incorporação obrigatória de autoridades no build unsigned |
| 908c3ca | Faixa adicional e saneamento de TXT dos pacotes |
| a3b44bf | Reprodução da nova faixa e evolução da extração |
| f3d03b7 | Organização direta em TruboRoms/roms |
| c07a4a2 | Início dos downloads corrigido e teste de regressão de destino |
| Commit deste handoff | Prioridades, sorteio 40/60, testes e documentação humana |

Esse registro explica alterações verificáveis.
Não atribui culpa a uma pessoa ou ao servidor sem evidência.
Pedidos de aparência que foram substituídos no histórico não são requisitos cumulativos.

### 16.1 Autoridade ausente antes da rede

Sintoma: mensagem local de autoridade indisponível e servidor sem tentativa correspondente.
Correção: usar a base 2.0.1 ou descendente e o pipeline com as oito entradas públicas.
Prevenção: não entregar resultado de dotnet publish simples sem os parâmetros necessários.

### 16.2 Música ausente e extração não executada

Copiar áudio para Downloads não incorpora recurso.
É necessário arquivo no projeto, entrada na playlist e hash/tamanho coerentes.
Baixar ZIP também não equivale a extrair.
A linha atual inclui essas correções anteriores.
Este commit altera a posição e o sorteio, não refaz a infraestrutura de extração.

### 16.3 Regressão no início dos downloads

c07a4a2 distinguiu novamente a política original do descriptor da decisão de extrair.
ResolveGameDownloadRootForDownload conserva a raiz apropriada para cada caso.
DownloadResumeVerifier cobre o cenário.
Ordenar jogos não precisa tocar nesse método.

### 16.4 Fechamento inesperado

Houve relato de encerramento.
Sessão inválida pode causar fechamento controlado.
Mídia e exceções também precisam de evidência para diagnóstico.
Sem log/evento/reprodução, não declarar uma causa única.
O aceite real deve ser repetido com servidor acessível.

### 16.5 Caminho indisponível no Explorer

“Local não disponível” significa que o caminho não está acessível.
Não comprova sozinho falta de espaço.
Uma pasta .staging pode ter sido movida/removida após publicação.
Conferir resultado final, estado da operação e espaço.
Não recriar staging manualmente para mascarar o erro.

## 17. Compilar no GitHub: roteiro para uma pessoa

1. Abrir TRUBORAMA-SUITE no GitHub.
2. Selecionar codex/v2.0.2-music-cleanup-final.
3. Conferir o commit com este documento.
4. Abrir Actions.
5. Selecionar “Build and verify”.
6. O push nessa branch dispara o workflow automaticamente.
7. Para refazer, usar “Run workflow” e escolher a mesma branch.
8. Abrir a execução e conferir o SHA completo.
9. Aguardar o job Windows.
10. Conferir “Build and gate unsigned evidence”.
11. Continuar somente se a execução correta terminar com success.
12. Abrir Artifacts.
13. Baixar Turborama-win-x64-unsigned- seguido do SHA.
14. Extrair todo o ZIP em pasta nova.
15. Manter Turborama.exe junto dos assets e arquivos publicados.
16. Ler RELEASE-MANIFEST.json.
17. Executar o aceite humano deste documento.

O runner é windows-2025.
O SDK é 10.0.400.
O publish é autocontido para win-x64.
O cliente final não precisa instalar o SDK.

“Single file” não significa que todos os assets externos desapareceram.
Copiar somente o EXE pode quebrar capas e vídeos.
O ZIP automático “Source code” de uma Release é fonte, não programa instalado.

A retenção do artifact é de sete dias.
Para guardar uma entrega por mais tempo, conservar o pacote completo e seus hashes ou publicá-lo como asset aprovado de Release.
A expiração do artifact não apaga o código, mas exige reconstrução para obter novo pacote.

## 18. Etapas internas do pipeline

1. Checkout da revisão exata.
2. Instalação do SDK fixado.
3. Registro da imagem de runner.
4. Chamada de Build-Production.ps1 com UnsignedStaging.
5. Conferência dos quatro arquivos/hashes públicos.
6. Validação das assinaturas e vigência.
7. Preparação da árvore/temporários de build.
8. Gate de fonte Test-ReleaseSource.ps1.
9. Restauração em modo locked.
10. Build Release com warnings como erros.
11. CatalogVerifier e verificadores integrados.
12. Validação das autoridades com assembly compilado.
13. Publish autocontido com metadados de autoridade.
14. SBOM SPDX e manifesto SHA-256.
15. Gate do pacote.
16. Upload do artifact.

TURBORAMA_SKIP_NETWORK_TESTS=1 evita dependência de testes externos.
Não cria modo offline no produto.
Não desativa licença nem grants de conteúdo.
Build verde comprova os gates daquele commit, não a sessão real de um comprador.

## 19. Reconstrução manual no Windows

Requisitos:

- Windows x64;
- PowerShell 7;
- Git;
- SDK .NET 10.0.400;
- acesso às fontes HTTPS de dependências;
- espaço para código, snapshot, restauração e saída.

G: foi escolhida nesta estação por capacidade.
Outra máquina pode usar outra unidade.
Temporários em C: ainda podem afetar o build.
Não limpar discos como efeito colateral de uma compilação.

Para um clone novo em pasta inexistente:

~~~powershell
git clone --branch codex/v2.0.2-music-cleanup-final https://github.com/luziellacerda/TRUBORAMA-SUITE.git G:\TurboramaFonte
Set-Location G:\TurboramaFonte
git status --short
git rev-parse HEAD
dotnet --version
~~~

Anotar o SHA.
Para reproduzir uma entrega exata, selecionar seu SHA em clone limpo/separado.
Não fazer checkout por cima de trabalho não salvo.
Se a pasta já existir, conferir origem, branch e alterações antes de usá-la.

Com a fonte correta, executar no PowerShell 7:

~~~powershell
$turboramaBuild = @{
    UnsignedStaging = $true
    OutputRoot = 'G:\TurboramaSaidas'
    DotNetPath = 'dotnet'
    AuthorityConfigurationPath = '.\authority\public\suite-authority-envelope.json'
    AuthorityConfigurationSha256 = '20F7F066B654AAD700C4733C9B011495A2BB9B52E7A8B3A77E806CDEDEBFA3E6'
    AuthorityIssuerSpkiPath = '.\authority\public\suite-authority-issuer.spki.der'
    AuthorityIssuerSpkiSha256 = '9BA572CC64CCFD9DCADA0699AB5E4F43E4662F84C1A82908A1125AA56C987B3A'
    ContentAuthorityConfigurationPath = '.\authority\public\content-authority-envelope.json'
    ContentAuthorityConfigurationSha256 = '56E7A1BD100E5B5A9CD1109C0B90EDFCAFAF6C1497AE3113551684D872A0BA07'
    ContentAuthorityIssuerSpkiPath = '.\authority\public\content-authority-issuer.spki.der'
    ContentAuthorityIssuerSpkiSha256 = '65631E0AAA9EFB75991098F7A68DDA462004E5BBC183AE6BF4FF9256397A8DC5'
}
.\tools\Build-Production.ps1 @turboramaBuild
~~~

Não usar AllowDirty para declarar reprodução exata de um commit.
Essa opção serve a desenvolvimento local identificado como alterado.
Não executar o modo Signed sem seus requisitos.
Não desativar validações para contornar erro de certificado, autoridade ou NuGet.
O procedimento de Signed e sua gramática de execução são separados deste exemplo Unsigned.

## 20. Conteúdo do pacote

A fonte definitiva é RELEASE-MANIFEST.json.
O conjunto inclui:

- Turborama.exe;
- Assets com catálogo e recursos externos aprovados;
- RELEASE-MANIFEST.json;
- Turborama.spdx.json;
- THIRD-PARTY-NOTICES.txt e demais avisos publicados;
- demais arquivos inventariados e aceitos pelo gate.

Não adicionar DLL, música ou capa ao ZIP depois do manifesto.
Isso altera bytes sem atualizar a evidência.
Modificar fonte/inventários e reconstruir.

As autoridades são incorporadas no EXE.
Não é necessário colar os arquivos públicos ao lado de um EXE antigo para fazê-lo reconhecê-los.
A correção exige build correto.

## 21. Testes específicos deste commit

CatalogPopularityVerifier confere:

- nove categorias esperadas;
- existência dos IDs nas categorias corretas;
- sequência dos destaques;
- ausência de duplicação/perda;
- preservação das instâncias e descriptors;
- ordem residual dos demais;
- busca e paginação;
- isolamento entre plataformas;
- comportamento de ID futuro/desconhecido.

MusicStartupVerifier confere:

- nove faixas;
- Aperta Start em primeiro;
- tamanho/hash do recurso incorporado;
- todas as 100 possibilidades do primeiro sorteio e oito posições alternativas;
- 320 escolhas de Aperta Start em 800 combinações;
- 60 escolhas de cada outra faixa em 800 combinações;
- limites para lista vazia, unitária e sorteio real.

É verificação exaustiva da regra, não simulação probabilística instável.
WpfTemplateVerifier aceita qualquer índice inicial válido.
Confere que a faixa aberta corresponde ao índice escolhido e conserva os testes de controles.

Os verificadores anteriores de protocolo, conteúdo, inventário, caminhos, retomada, extração e biblioteca continuam ativos.
Não substituir teste de áudio real apenas pelo hash.
Não substituir login real apenas pela validação criptográfica do build.

## 22. Aceite humano: identificar o pacote

1. Baixar artifact do run correto.
2. Registrar URL do run e SHA do commit.
3. Registrar SHA-256 do EXE.
4. Conferir versão 2.0.2.
5. Conferir autoridades não vazias no manifesto.
6. Extrair em pasta nova.
7. Preservar pacote aprovado anterior.
8. Confirmar destino do atalho.
9. Não misturar arquivos de versões diferentes.

## 23. Aceite humano: login e estabilidade

1. Garantir que o servidor está acessível.
2. Usar credenciais legítimas no programa.
3. Não salvar OTP/licença real em log público ou Git.
4. Confirmar entrada na loja.
5. Navegar e aguardar o acompanhamento da sessão.
6. Registrar mensagem e horário com fuso em caso de erro.
7. Correlacionar com o servidor.
8. Não repetir ativação nem trocar chave às cegas.

O relato de servidor offline não pode ser convertido em teste ponta a ponta aprovado.
Uma nova compilação não liga o servidor.

## 24. Aceite humano: jogos e música

Para a ordem:

1. Abrir as nove plataformas.
2. Comparar os primeiros títulos com ORDEM-POPULARIDADE-20260905.md.
3. Navegar além dos destaques.
4. Confirmar jogos restantes.
5. Pesquisar título prioritário e não prioritário.
6. Conferir categoria não alterada, como PS1.
7. Distinguir item em manutenção de falha de ordem.

Para música:

1. Entrar na loja e ouvir reprodução automática.
2. Reabrir em sessões diferentes para observar variação.
3. Não exigir proporção exata em amostra pequena.
4. Confirmar título/áudio de Aperta Start.
5. Testar próxima/anterior.
6. Pausar e confirmar que não avança sozinha.
7. Desligar e confirmar que callback atrasado não religa.
8. Ajustar volume.
9. Testar pasta externa, se utilizada.

## 25. Aceite humano: download e organização

1. Escolher pacote pequeno autorizado.
2. Confirmar início e crescimento dos bytes.
3. Pausar e retomar.
4. Esperar verificação e extração.
5. Conferir destino final em TruboRoms/roms.
6. Confirmar ausência do invólucro id/sistema/roms no pacote organizado.
7. Conferir TXT com padrões antigos, caso o pacote de teste os contenha.
8. Conferir remoção de videos do pacote.
9. Confirmar que vídeos da interface continuam presentes.
10. Abrir Jogos locais e atualizar.
11. Excluir somente o jogo de teste pela interface.
12. Reinstalar e confirmar reaparecimento.

Não usar toda a biblioteca do comprador como experimento destrutivo.
Não executar varredura geral para testar uma alteração de prioridade musical.

## 26. Diagnóstico por sintoma

| Sintoma | Verificação inicial | Não fazer automaticamente |
|---|---|---|
| Autoridade indisponível | Commit, workflow, manifesto e metadados | Criar chave ou reautorizar cliente |
| Licença ativa, sessão recusada | Challenge/etapa e log correlacionado | Tratar ativação como prova de sessão |
| Servidor sem tentativa | Falha local, DNS/TLS e horário | Culpar banco sem requisição |
| Download não inicia | Descriptor, capacidade e raiz | Relaxar autenticação |
| HTTP 200, jogo ausente | Hash, extração, espaço, conflito | Marcar sucesso só pelo HTTP |
| Staging sumiu | Destino final e estado da publicação | Recriar staging manualmente |
| Capas/vídeos ausentes | Assets e manifesto | Distribuir só EXE |
| Música ausente | Recurso, playlist e hash | Copiar somente para Downloads |
| Ordem antiga | SHA, branch e atalho | Editar catálogo do servidor |
| Programa fecha | Eventos/exceção, mídia e sessão | Declarar causa sem evidência |
| Gate falha | Primeiro erro do job correto | Ignorar exit code |
| Espaço insuficiente | Temporários em C: e volume destino | Apagar ROMs/identidade/autoridades |

## 27. Evidência que deve acompanhar um problema

Recolher de uma vez:

- versão;
- SHA do EXE;
- commit;
- URL/run ID;
- Windows;
- resolução/DPI, se visual;
- data, hora e fuso;
- mensagem exata sanitizada;
- etapa: login, catálogo, download, verificação, extração ou publicação;
- espaço livre nos volumes envolvidos;
- caminho sanitizado;
- categoria/item de teste;
- log correlacionado do servidor, se houve requisição.

Não publicar licença, OTP, DeviceId, bearer, URL privada ou corpo com dados do comprador.
Separar “observado”, “inferido” e “a testar”.
Não pedir ao outro operador para adivinhar qual binário foi usado.

## 28. Como fazer próximos commits

Antes de adicionar arquivos:

~~~powershell
git status --short
git diff --stat
git diff --check
~~~

Revisar cada arquivo.
Adicionar explicitamente fonte, testes e documentação.
Não adicionar bin, obj, artifacts, ZIP, cache ou segredo.
Os quatro arquivos públicos da autoridade são inclusão intencional já aprovada.

A mensagem do commit deve informar finalidade, limites e testes.
Depois:

~~~powershell
git push origin codex/v2.0.2-music-cleanup-final
~~~

Acompanhar Actions pelo SHA.
Se houver falha, registrar a primeira causa e corrigir o necessário.
Não anunciar “compilado” antes dos gates e upload.

Para curadoria, atualizar tabela de IDs e documento de fontes juntos.
Para áudio, atualizar recurso, integridade e teste.
Para autoridade, seguir aprovação própria; não aproveitar manutenção visual para rotacionar chaves.

## 29. Preservação e rollback

Conservar o pacote anterior inteiro e seus hashes até aprovar o novo.
Não misturar assets de uma versão com EXE de outra.
Para retornar, fechar o programa e selecionar o pacote anterior íntegro.
Respeitar compatibilidade com a autoridade/servidor vigente.
Não apagar identidade CNG nem sessão como efeito colateral.

Bin, obj e temporários identificados podem ser regeneráveis.
Antes de excluir, conferir caminho absoluto, uso, idade e existência de backup.
Não remover raiz de C:, G:, perfil, repositório ou biblioteca.
Não classificar .turborama-installed, .turborama-recovery e autoridades como lixo.
Não há autorização de limpeza nesta entrega.

## 30. Estado de aceite e conclusão correta

Este handoff descreve implementação, construção e roteiro.
Não é certificado de disponibilidade do servidor.
O resultado do build está no run do Actions ligado ao commit.
Login/download reais exigem serviço acessível e sessão legítima.

A conclusão deve separar:

1. Código commitado.
2. Push realizado.
3. CI concluído ou falhou.
4. Artifact disponível.
5. Autoridades verificadas no pacote.
6. Aceite humano real aprovado ou pendente.

Um operador novo deve começar pela referência Git correta, construir pelo workflow, conferir o manifesto e executar o aceite.
Ele não deve precisar refazer a conversa nem reconstruir arquivos de branches antigas.
