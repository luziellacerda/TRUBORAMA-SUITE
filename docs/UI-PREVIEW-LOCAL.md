# Prévia local da interface Turborama

`Turborama-UI-Preview.exe` é uma ferramenta interna e separada do aplicativo de produção. Ela existe somente para revisar navegação, capas e vídeos locais enquanto a autoridade do servidor é implementada.

## Fronteira da prévia

- o projeto não referencia o executável, assembly ou código-fonte do cliente principal;
- o assembly da prévia não referencia cliente HTTP, serviço de download, instalação, extração, abertura de pasta, execução de processo ou persistência de configuração;
- lê o conteúdo visual somente sob `AppContext.BaseDirectory\Assets`; credencial e manifesto ficam na raiz do pacote;
- aceita apenas uma pasta física em disco local fixo e rejeita UNC, unidade de rede mapeada, traversal e reparse point;
- valida manifesto, hashes, commit, catálogo, capas e vídeos antes de abrir;
- exige senha aleatória de 192 bits, cujo verificador é protegido por DPAPI `CurrentUser`, ligado ao manifesto e ao commit exato;
- limita a credencial a no máximo 72 horas e encerra uma sessão aberta usando relógio monotônico e UTC;
- mantém o aviso “PRÉVIA LOCAL — SOMENTE VISUALIZAÇÃO — NÃO É LICENÇA”.

O executável não solicita elevação. A publicação self-contained inclui bibliotecas do runtime .NET, inclusive componentes que não são chamados diretamente pela prévia. A ausência de referências de rede no assembly da aplicação reduz a superfície, mas somente uma regra de firewall ou sandbox do sistema operacional constitui uma garantia externa de zero tráfego. O pacote não deve ser iniciado de compartilhamento ou unidade de rede.

## Limites assumidos

A senha é uma trava operacional de conveniência, não uma autorização comercial e não protege contra alguém que controle a mesma conta e possa depurar ou alterar o processo. O limite de cinco tentativas vale por processo. Sem servidor, relógio confiável ou estado persistente, retroceder o relógio entre reinícios pode permitir reutilização até a data exibida; durante uma sessão já aberta, o relógio monotônico impede prolongamento por retrocesso do relógio de parede.

Toda autorização do produto continua pertencendo ao servidor. `PremiumLoginWindow`, a validação de licença e o comportamento fail-closed do `Turborama.exe` não são alterados.

## Geração

O repositório deve estar limpo e no commit que será entregue. Use o SDK fixado em `global.json`:

```powershell
pwsh -NoProfile -File .\tools\Build-UiPreview.ps1
```

O script executa os testes Release, publica uma versão self-contained em staging, cria e vincula o manifesto, gera a credencial DPAPI, executa uma verificação ponta a ponta e somente então move a pasta para o destino final. A senha é mostrada uma única vez. `ui-preview.credential` é ignorado globalmente pelo Git.

## Proibição de distribuição

Não enviar a prévia para GitHub Releases, updater, servidor, cliente ou usuário final. Não assinar com certificado Authenticode de produção. Antes do release oficial, execute os gates normais e confirme que o pacote contém somente o aplicativo licenciado.
