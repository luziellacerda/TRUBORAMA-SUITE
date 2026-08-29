# Changelog

## 2.0.0 — candidato em 2026-08-27

- removidos login demonstrativo, catálogo privado reversível, URL permanente e arquivo de chave incorporado;
- introduzido protocolo de licença Suite fail-closed com configuração de autoridade assinada, identidade CNG e sessões curtas;
- catálogo público estrito ampliado para 902 itens: os cards sem descriptor autorizado ficam indisponíveis e não simulam downloads;
- provedor de conteúdo integrado ao catálogo assinado e aos grants efêmeros; o modo direto preserva `Range` e `If-Range`, não encaminha o bearer à origem e continua fail-closed sem sessão válida;
- extração ZIP, RAR e 7z reforçada com limites, cancelamento, staging e política proveniente do descriptor autorizado; assinatura de catálogo/manifests continua bloqueio de produção;
- incluídos 15 vídeos de fundo com enquadramento responsivo, roteamento semântico por sistema e derivado Nintendo Switch otimizado para 1080p30, além dos 38 vídeos de sistemas;
- conferidas e indexadas 100 capas Windows; o catálogo completo possui 902 capas e 902 descrições com índices, tamanhos e SHA-256 verificados;
- migração para .NET 10, dependências bloqueadas e assinadas, SBOM SPDX 2.3 e pipeline de candidato Authenticode;
- adicionado checklist explícito que impede chamar o pacote de produção enquanto backend, catálogo/grants, direitos e assinatura do pacote completo estiverem pendentes.

## 1.7.0 — 2026-08-25

- Central de downloads com a capa exata de cada jogo em andamento ou preservado;
- botão de download com pulso neon leve e varredura laser acelerada por cache de bitmap;
- vídeos novos integrados para Sistema e utilitários, PlayStation 2 e SEGA Saturn;
- índices versionados das 850 capas em `Capas-Turborama-por-Sistema`;
- lote PSP finalizado com 21 fontes aprovadas, integradas diretamente sem recorte ou redimensionamento;
- Ghost of Sparta restaurado pela fonte aprovada e Ghost Rider concluída sem elementos duplicados;
- validação automatizada de dimensões, fonte aprovada e SHA-256 das cópias PSP.

## 1.6.0 — 2026-08-20

- catálogo ampliado para 22 categorias e 850 itens identificados;
- 850 capas locais 1200×900 com composição e branding Turborama no pacote da Release;
- busca sem acentos e paginação compacta para categorias grandes;
- fila de download com progresso, cancelamento, histórico e ação para abrir arquivo;
- download público de teste ativo nos 850 cards, com 92 bytes e SHA-256 verificado;
- nomes de arquivos baixados legíveis, com sufixo curto do ID para evitar colisões;
- fallback Turborama para capas ausentes ou incompatíveis;
- verificador automatizado atualizado para o catálogo completo e o downloader seguro.

## 1.5.0 — 2026-08-20

- tela de login Turborama com caça e identidade visual própria;
- catálogo único alimentando 22 categorias de sistemas;
- busca sem acentos, contagem, estado vazio e paginação;
- páginas Início, Biblioteca e Downloads;
- cards e ícones originais Turborama;
- executável autocontido para Windows x64;
- verificador do catálogo e workflow de build.
