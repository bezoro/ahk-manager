# Descrição automática pelo cabeçalho do script

**Status:** ideia registrada a pedido do usuário em 24/10/2026; ainda não implementada.

Permitir que o autor escreva a descrição em um comentário com formato definido no início do arquivo `.ahk`. Ao selecionar o script, o AHK Manager lê esse cabeçalho e apresenta a informação na descrição do painel de detalhes, sem exigir cadastro manual do mesmo texto.

Formato ainda a definir. Exemplo de proposta:

```autohotkey
; @description Controla o volume com atalhos globais.
; @description Inclui ações para silenciar e restaurar o áudio.

#Requires AutoHotkey v2.0
```

A leitura deve ser conservadora, sem executar nem modificar o script. Scripts sem esse cabeçalho devem continuar funcionando normalmente, preservando os metadados já cadastrados.

Na implementação, definir suporte a múltiplas linhas, limites de leitura, atualização quando o arquivo mudar e precedência entre a descrição do cabeçalho e uma descrição editada manualmente. O formato acima é ilustrativo, não uma sintaxe já suportada pelo aplicativo.
