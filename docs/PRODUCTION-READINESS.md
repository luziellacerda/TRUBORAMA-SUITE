# Turborama Suite 2.0 — prontidão de produção

Data da auditoria: 27/08/2026.

## Veredito atual

A árvore pode gerar um **staging local não assinado** e contém um pipeline rigoroso para um **candidato assinado**. Ainda não existe uma Release funcional de produção.

Essa classificação é intencional: assinatura do executável não substitui autorização de conteúdo, autenticidade do catálogo, segurança do backend, licença de distribuição nem assinatura do pacote completo.

O cliente falha fechado. Nenhuma chave digitada localmente, URL permanente, segredo incorporado ou opção de teste concede acesso.

## Servidor existente e limite de integração

O servidor atual preserva um protocolo v1 com RSA-PSS-SHA256, challenge curto e sessão curta. Ele atende operações do produto PIX, mas não implementa os contratos exigidos pela Suite:

- `ProductId` e entitlement separados por produto;
- endpoints aditivos `/v1/suite/*`;
- catálogo assinado, sequência monotônica e proteção contra rollback;
- manifest imutável por artefato, com tamanho e SHA-256;
- grant curto e de uso único, prova de posse por Range e gateway privado;
- chave pública dedicada a catálogo/manifest;
- persistência transacional compartilhada para challenges, sessões e grants.

Reutilizar a sessão PIX como autorização da Suite misturaria produtos e não é aceitável. O cliente permanece indisponível sem uma autoridade Suite válida.

## Controles implementados no cliente e no build

- .NET 10 com versão e hash da árvore completa do SDK pinados; Git/GPG também exigem executável e árvore previamente aprovados;
- ambiente Signed substituído integralmente por allowlist controlada antes de gates/`dotnet`, com TEMP confiável, perfis/plugins/caches isolados, restauração em `finally`, metacaracteres MSBuild proibidos em paths, `NuGet.Config` explícito e dependências em lock files;
- argv Signed canônico, sem `-Command`/encoded/settings/wrappers/abreviações/common params/duplicatas, e chave pública da tag pinada pelos bytes SHA-256 além dos fingerprints; o status GPG exige exatamente `GOODSIG` + `VALIDSIG` e rejeita falha, expiração e revogação;
- SharpCompress restaurado como pacote assinado, sem DLL local de origem desconhecida;
- remoção de catálogo privado reversível, links permanentes e login demonstrativo;
- identidade CNG não exportável e protocolo Suite com bytes canônicos verificados;
- configuração offline assinada autoriza uma chave RSA on-line distinta para assertions e um pin SHA-256 do SPKI TLS; o build exige por canal independente o SHA-256 dos bytes exatos do envelope, incorpora essa âncora e a revalida no cliente, bloqueando rollback para outro envelope do mesmo issuer; a validade máxima é de 366 dias e reutilizar a chave offline como chave on-line é rejeitado;
- todo challenge, resultado positivo de ativação, abertura de sessão e heartbeat exige envelope RSA-PSS-SHA256, payload canônico, domínio/tipo separado, contexto integral e freshness; resposta sem assinatura, de outra chave, challenge ou contexto falha fechada;
- o transporte de licenciamento de produção não aceita handler substituível, mantém validação normal de cadeia, hostname e revogação, acrescenta pin SPKI e desabilita proxy, redirects, cookies e descompressão automática;
- sessão curta, expiração/revogação ativa e cancelamento das operações autorizadas;
- catálogo público estrito, sem download de teste e sem URL de conteúdo;
- descriptor imutável com produto, identidade, tamanho e SHA-256 obrigatórios;
- nova requisição autenticada a cada tentativa e redirects autenticados proibidos;
- sidecar sem URL/token/grant, rehash de arquivo pronto e lock entre processos;
- extração ZIP/RAR/7z com política recebida somente do descriptor autorizado, limites, cancelamento, staging e validação do mesmo arquivo; a assinatura de catálogo/manifests ainda é bloqueio;
- publicação autocontida com gate de origem, testes offline, SBOM SPDX 2.3, inventário SHA-256 e gate exato de conteúdo;
- Authenticode e timestamp RFC 3161 obrigatórios no modo de candidato assinado;
- 903 capas (incluindo 100 Windows), 38 vídeos de sistemas e 15 vídeos de fundo inventariados.

## Bloqueios internos antes de uma Release

1. Implementar e revisar o cliente de catálogo assinado, incluindo sequência, validade, `keyId`, rotação e rollback.
2. Associar os 902 itens a manifests reais e imutáveis de artefato sem permitir que o catálogo visual altere a política autorizada.
3. Implementar o provedor real de grants e os testes de contrato contra o ambiente Suite.
4. Adotar um contêiner/distribuidor assinado para cobrir também catálogo, capas, XML, ícones e vídeos externos ao PE, e verificar essa assinatura antes da instalação/execução.
5. Executar testes end-to-end de instalação, upgrade, rollback, perda de sessão, falhas de disco e recuperação.
6. Separar compilação/testes da assinatura em ambientes de confiança distintos: o estágio de assinatura deve receber apenas bytes aprovados, usar chave em HSM/KMS e não executar código do repositório.
7. Congelar o threat model, o registro de riscos e a aceitação formal de todo risco residual antes do go-live.
8. Aprovar com a implementação do backend os vetores canônicos e assinados já exercitados pelo cliente e manter regressão byte a byte para cada extensão `/v1/suite/*`.

## Bloqueios externos antes de uma Release

1. Aprovar e implantar `/v1/suite/*` sem alterar os bytes canônicos das operações v1 existentes.
2. Migrar licenças existentes explicitamente para o produto legado e criar licenças `TURBORAMA_SUITE`; produto ausente deve negar.
3. Implantar storage privado/versionado e gateway com challenge/grant de uso único e estado atômico compartilhado.
4. Produzir catálogo e manifests assinados com tamanho e SHA-256 reais dos artefatos licenciados.
5. Fornecer trust roots públicos, pin TLS obtido por canal independente, política de rotação/revogação e custódia KMS/HSM separada para a chave offline da configuração e a chave on-line de assertions; o backend ainda precisa emitir os novos envelopes positivos.
6. Revogar todos os links e chaves expostos por versões anteriores.
7. Fornecer certificado Authenticode e serviço HTTPS de timestamp RFC 3161.
8. Confirmar os direitos de distribuição de capas, vídeos, jogos e ferramentas.
9. Aprovar migração, backup/restauração, observabilidade, limites, retenção e requisitos LGPD.
10. Exigir provenance verificável para tag/commit e aprovação protegida antes de liberar o estágio isolado de assinatura.
11. Proteger o plano administrativo com MFA, RBAC de menor privilégio, CSRF, step-up para ações críticas e trilha de auditoria imutável.
12. Aprovar e exercitar runbooks de incidente, rotação/revogação, backup, restauração e disaster recovery com RPO/RTO medidos.
13. Concluir testes de carga/soak, concorrência multi-instância, pentest independente e correção formal dos achados.
14. Executar o build assinado por caminho absoluto do PowerShell 7 com `-NoProfile -NonInteractive -File`, pins independentes do `pwsh.exe` e da árvore `PSHOME`, toolchains em volume/imagem imutável e principal de Release sem permissão de escrita nelas.

## Definição de Release de produção

Uma Release somente pode receber esse nome quando, simultaneamente:

- a árvore Git estiver limpa, publicada na branch protegida `main` e marcada com a tag anotada cujo objeto remoto, assinatura válida e fingerprint GPG pinado sejam exatamente os aprovados para a versão;
- backend, catálogo, grants e artefatos passarem pelos testes de contrato, replay, concorrência, restart, bloqueio e rollback;
- threat model, riscos residuais e controles administrativos tiverem aprovação formal, com vetores dourados comprovando que o protocolo v1 não regrediu;
- restauração, resposta a incidentes, carga/soak e pentest independente tiverem evidência aprovada;
- todos os bytes distribuídos estiverem cobertos por uma cadeia de assinatura verificável;
- todos os artefatos autorizados tiverem hash e tamanho verificados;
- `tools/Build-Production.ps1` concluir no modo assinado após atestar por BCL o processo PowerShell e as árvores versionadas de `PSHOME`, Git e SDK .NET antes e depois de cada fase que as utiliza;
- `tools/Test-PublishedPackage.ps1` aprovar conteúdo, SBOM, manifesto, Authenticode, timestamp e configuração da autoridade;
- a sessão real perder acesso e cancelar operações imediatamente quando expirar ou for negada.

Qualquer pacote `UNSIGNED-NOT-FOR-DISTRIBUTION` é apenas evidência local. Um pacote `SIGNED-RELEASE-CANDIDATE` continua bloqueado para distribuição até todos os itens acima serem comprovados.
