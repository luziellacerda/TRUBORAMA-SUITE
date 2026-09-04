# Autoridades publicas Turborama Suite

Estes quatro arquivos sao exclusivamente material **publico** de verificacao.
Eles permitem que o cliente valide, sem confiar em configuracao local:

- a autoridade do protocolo de licenciamento (`suite-authority-*`);
- a autoridade do catalogo e dos downloads (`content-authority-*`).

Nenhuma chave privada, certificado Authenticode, senha ou segredo de servidor
fica neste diretorio. O build fixa os quatro SHA-256 no workflow, valida as
assinaturas dos dois envelopes e incorpora somente os dados publicos no EXE.

## SHA-256 aprovados

- `suite-authority-envelope.json`: `20F7F066B654AAD700C4733C9B011495A2BB9B52E7A8B3A77E806CDEDEBFA3E6`
- `suite-authority-issuer.spki.der`: `9BA572CC64CCFD9DCADA0699AB5E4F43E4662F84C1A82908A1125AA56C987B3A`
- `content-authority-envelope.json`: `56E7A1BD100E5B5A9CD1109C0B90EDFCAFAF6C1497AE3113551684D872A0BA07`
- `content-authority-issuer.spki.der`: `65631E0AAA9EFB75991098F7A68DDA462004E5BBC183AE6BF4FF9256397A8DC5`
