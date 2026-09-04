# Turborama Suite 2.0.1

Versao final corretiva para producao do cliente Windows.

## Correcao critica

- restaura no executavel as duas autoridades publicas obrigatorias: licenciamento e conteudo;
- valida criptograficamente os dois envelopes durante a compilacao e nos gates do pacote;
- impede que um novo build unsigned seja criado sem os oito insumos publicos obrigatorios;
- mantem o executavel sem assinatura comercial Authenticode, conforme decisao do proprietario;
- registra no manifesto hashes nao nulos das duas autoridades e confirma que os mesmos valores foram incorporados no EXE.

## Seguranca

Os quatro arquivos versionados em `authority/public` sao material publico de
verificacao. Nenhuma chave privada, senha, token ou segredo do servidor integra
o repositorio ou o pacote.

A versao 2.0.0 foi substituida porque seu EXE unsigned nao continha os metadados
publicos de autoridade e, por isso, interrompia o login antes de falar com o
servidor. Use somente a 2.0.1 ou superior.
