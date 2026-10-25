# Relevância do inventário de processos

**READY — proposta pronta para revisão do plano; implementação e execução ainda pendentes.**

## Resultado e limite

Corrigir o bloqueio global causado por processos fora do domínio de intérpretes reconhecidos. Uma enumeração completa contendo processos comuns com acesso negado ou `ProductName` vazio deve continuar utilizável. Um candidato AutoHotkey reconhecido sem prova suficiente continua tornando a observação incompleta e impedindo iniciar uma possível duplicata.

Este adendo substitui somente a exigência de ambiguidade universal por imagem/SID/metadados ausentes do plano `process-status-and-row-preferences.md` (especialmente seus itens de relevância e negativos). Preserva suas demais decisões, incluindo identidade, elevação, preferências, autorização, encerramento gracioso e resultados incertos.

O catálogo administra arquivos `.ahk` executados por intérpretes reconhecidos. Não promete descobrir todo programa arbitrário compilado, renomeado ou completamente opaco. Uma aplicação compilada não prova que um arquivo fonte do catálogo esteja carregado; a ausência de metadados de produto não a transforma em candidato. Um binário que apresente um sinal positivo AutoHotkey continua conservadoramente candidato, mesmo que possa ser compilado.

## Evidência verificada

`E` identifica o pacote externo de evidências mantido pelo coordenador; dados particulares da máquina permanecem nele.

- Plano anterior: SHA256 `01f530eeeaf96eff20ee47a6c548044d3d51742862d369fd4c13dec51ab13e31`.
- `E/engine-final-v2/index.json`: SHA256 `9f536217f0a57b5f9cff5c0be434249dde38af54536f49d5161a1de5a9ccb083`; baseline declarado `9b23b272b6469bdeb5538ab556ee267e63e3d260`. As fontes decisivas abaixo coincidem com os hashes do índice e das cópias completas.
- `E/root-inventory-evidence-v1.json`: candidato canônico `sha256:c97f5619bcd81925f47a61cad67fad851ab5e80d59d56d900d9de376fa264af8`; hash do arquivo manifesto `3730517a8b27d22b443566fbe0ce27b8171a884d8ea29d2d964be3245cb79eb0`. São identificadores distintos.
- `E/root-inventory-logs-v1/inventory-v1.stdout.log`: SHA256 `868d040d0cb60e6c38112b25910b500afb5e76b7bbb3ac62f5d9af17fc7a4c56`. A seleção de contadores confirma enumeração WMI completa, falhas de leitura nativa completa, imagens sem produto e um candidato AutoHotkey sem argv. Não demonstra que todas essas falhas sejam de imagem nem que o candidato opaco seja seguro para adoção.
- O coordenador reteve a tentativa normal em `E/root-owned-run-v1` e `E/root-normal-owned-logs-v1`: `ObservationIncomplete` antes do primeiro despacho. Esse resultado não comprova execução dos comandos posteriores.

| Fonte e localização decisiva | SHA256 |
| --- | --- |
| `Presentation.WinUI/Infrastructure/ProcessObservation.cs`, 65–106 | `8f01aded61e7c2fbc7137100f11bef2e6d4fb1e3824803d9d50b203935dcd238` |
| `Presentation.WinUI/Infrastructure/NativeProcessIdentity.cs`, 99–110, 141–142 | `7d3446292de55b06492067b6813a924988657ee1c9da1a3e0949b4e486cc197a` |
| `Presentation.WinUI/Infrastructure/NativeMethods.Process.cs`, 9–18 | `223cc225d2425de20133996909db13a505c9a1442a69e0831f3b11399f173e04` |
| `Tests.Backend/BackendChecks.cs`, 25–35 | `05e2b0fbdd88c5868c4749353c6616645a2d0261cce0120d6ea64a563ffd77b7` |
| `Tests.Backend/OwnedAhkChecks.cs`, 335–379 | `771d0d4382a071820b22f9525fec3bce1406f21c5be72623f9dd29b0f076fd7b` |

A linha 74 de `ProcessObservation` marca qualquer falha de identidade completa como incompleta; a linha 84 faz o mesmo para produto ausente. `TryRead` obtém imagem e depois token/sessão, devolvendo apenas sucesso completo: falha de token pode esconder uma imagem legível. `OwnedAhkChecks.ScopedObservation` repete essas exclusões globais nas linhas 367/370.

Leitura suplementar autorizada: `Presentation.WinUI/Services/AutoHotkeyDiscovery.cs`, SHA256 `e35f843d05cd9c02d5ce8cba0da376dfa766c20d318d6f9f8a382d021de92b37`. Suas linhas 54–56 enumeram os basenames `AutoHotkey.exe`, `AutoHotkey64.exe` e `AutoHotkey32.exe`; as linhas 20–28 exigem produto/versão v2 e fingerprint para validar o intérprete. A descoberta permanece inalterada.

## Contrato proposto

1. **Determinar relevância antes de exigir identidade completa.** Manter um único critério interno pequeno, reutilizável pela observação e pela guarda das fixtures. Não criar framework, store, IPC, configuração ou API pública novos.
2. **Sinais positivos de candidato:** caminho nativo/configurado correspondente; basename de intérprete configurado; um dos três basenames explícitos da descoberta; produto AutoHotkey obtido da imagem nativa; ou executável `argv[0]` WMI correspondente a caminho/basename reconhecido. Examinar também `Name` e `ExecutablePath` WMI como sinais de relevância. Comparações de basename usam `OrdinalIgnoreCase`, nunca prefixos livres.
3. **Imagem parcial independente de token.** Expor na fronteira nativa existente uma leitura de imagem pelo handle retido, sem exigir SID/token. Usar os P/Invokes existentes. Tentar essa leitura antes de excluir uma linha por seu nome WMI; consultar os metadados da imagem quando disponíveis. Assim, intérprete configurado/renomeado com WMI nulo continua candidato, inclusive se a falha posterior for de token. Não enfraquecer `TryRead`, `Matches`, direitos de controle ou a prova completa.
4. **Escopo de usuário/sessão.** Preservar exclusão por outra sessão comprovada na fonte existente e por outro SID/sessão comprovados nativamente. SID/sessão desconhecidos de candidato reconhecido não autorizam exclusão.
5. **Linhas fora do domínio.** Sem sinal positivo reconhecido, uma falha de handle/token/imagem/metadados ou produto vazio não altera `Complete`. Isso é limite de cobertura, não prova de que o programa não possa conter AHK. Metadados não AHK não anulam outro sinal positivo. Imagem nativa disponível deve ser considerada antes da classificação final.
6. **Candidatos não resolvidos.** Falha de identidade completa, metadados/validação de intérprete, argv/proveniência, correspondência WMI/nativa, criação, fingerprint, estabilidade ou HWND mantém `Complete=false` e `ObservationIncomplete`. Uma enumeração WMI incompleta/cancelada continua incompleta/cancelada; o classificador não a promove a completa. Não reutilizar resultados negativos entre observações.
7. **Adoção e controle preservados.** Só argv independente vinculado à criação/imagem nativa, ou proveniência completa de lançamento já aceita, estabelece o caminho do script. Revalidar dados parciais contra a identidade completa antes da adoção. Nome, produto, janela, caption e `argv[0]` isolados nunca autorizam adoção, despacho ou encerramento. Preservar handle estável, SID/sessão, hashes, janela única, revalidação e saída observada; sem force-kill.
8. **Semântica existente.** Não alterar `ObservedProcesses`, interfaces públicas, razões, `Unknown`, `Missing`, `Blocked`, seleção das linhas ou política de ações. `Complete` passa a descrever cobertura do domínio reconhecido. Candidato opaco continua bloqueando ausência/duplicata conforme a política existente; a correção não promete liberar esse caso. Não mudar contratos assíncronos, deadlines, cancelamento ou recuperação de resultados incertos.

## Trabalho e propriedade

| ID | Responsável e escrita permitida | Entrega e dependência |
| --- | --- | --- |
| R1 | Implementador de engine já designado: seção de inventário em `Tests.Backend/BackendChecks.cs` | Regressão executável com a API existente: enumeração completa, linha não candidata e identidade nativa indisponível. Exigir FAIL de comportamento antes de editar produção; erro de compilação não conta. |
| R2 | Mesmo implementador: `ProcessObservation.cs` e somente leitura parcial de imagem em `NativeProcessIdentity.cs` | Aplicar o contrato após R1. Manter nomes/assinaturas existentes; pequenas seams internas de leitura podem viabilizar casos determinísticos adicionais. |
| R3 | Mesmo implementador: regressões de inventário em `BackendChecks.cs` e guarda `ScopedObservation`/checagens de escopo em `OwnedAhkChecks.cs` | Cobrir R2 e alinhar a guarda das fixtures ao mesmo domínio, sem duplicar uma heurística divergente. Sequenciar com as demais edições desse proprietário nesses arquivos. |
| R4 | Coordenador; revisor de implementação distinto | Integrar com F1–F5 já atribuídos, obter revisão independente e executar validação normal própria somente após os gates anteriores. |

`NativeMethods.Process.cs`, `AutoHotkeyDiscovery.cs` e `Tests.WinUI/Integration/FixtureProcessObservation.cs` são referências sem alteração prevista. Qualquer expansão de arquivo/interface precisa ser justificada ao coordenador. O autor deste plano escreve apenas este documento. Preservar todas as outras alterações e a escolha de checkout do coordenador.

## Verificação e conclusão

Após o FAIL R1, exigir a mesma regressão e os testes afetados passando, com resultados vinculados aos bytes finais. Cobrir, com dados sintéticos e fixtures próprias:

- Processo não candidato com identidade inacessível; produto vazio; falha de metadados; imagem nativa legível com token indisponível: não congelam o inventário por si sós.
- Intérprete configurado renomeado com WMI nulo; nome WMI enganoso mas caminho nativo configurado; basenames reconhecidos em caixa diferente; sinal positivo por produto/argv: continuam candidatos. Produto não AHK não suprime nome/caminho reconhecido.
- Candidato reconhecido com identidade, SID/sessão, argv ou proveniência insuficientes: observação incompleta, nenhuma adoção e nenhuma duplicata. Outra sessão/SID comprovados não bloqueiam.
- Enumeração incompleta, divergência de imagem/criação, PID reutilizado, stamp/fingerprint alterado e janela ambígua: guardas mantidas para candidatos.
- Argv independente de B com caption de A continua B; caption sozinho nunca adota; recibo completo mantém somente a instância própria comprovada. Preservar as regressões existentes de `OwnedAhkChecks`.
- A guarda da fixture ignora apenas linhas fora do domínio ou exclusões exatas já permitidas pelo mecanismo de raiz nova. Um candidato reconhecido novo fora do argv aprovado, ou opaco sem recibo próprio, aborta antes de corpo de script ou efeito.

A execução real normal é do coordenador, com raiz nova, fixtures inertes revisadas e limites já aprovados: sequência de até 180s e cleanup de até 30s. Reutilizar a prova existente de ausência de despacho na tentativa anterior; não reutilizar seu scope antigo. O mecanismo existente de `FixtureProcessObservation` fixa raiz/arquivos novos e captura exclusões de processos opacos anteriores pela criação; sua seleção foi verificada no SHA256 `f46f00e4fffcd6c09e3c03e158ac1f77e80410a1aef7673a3c54f0804ba25c85`. Exclusões precisam corresponder à nova execução e nunca passam à produção.

Antes do primeiro despacho, registrar aprovação da guarda sobre a enumeração atual. Se houver candidato novo sem prova de escopo, abortar sem controlar processos alheios. Depois, confirmar os comandos normais próprios e seus resultados observados; `Accepted`/pendência não contam como conclusão. Falha, resultado parcial ou desconhecido deve reter evidência e seguir a recuperação existente, sem repetir efeito incerto.

**Concluído neste adendo:** contrato implementado, FAIL/PASS causal e regressões afetadas registrados, revisão fresca aprovada e execução normal própria confirmada. Se R4 permanecer impedido por condição externa, registrar a condição e manter essa validação pendente; o registro do impedimento não equivale a conclusão. A existência de candidato externo reconhecido opaco pode continuar limitando o uso real e deve ser relatada honestamente. Esta validação não substitui os gates de elevação, preferências e entrega do plano anterior; publicação/merge/release continuam sob o coordenador.

## Riscos e pressupostos

- Binários arbitrários renomeados/compilados e opacos sem nenhum sinal reconhecido ficam fora da cobertura. Este limite deliberado evita exigir prova impossível sobre todo processo Windows; não há promessa de detecção completa de conflitos globais de hotkeys.
- Sinais de nome/produto podem produzir candidatos falsos e bloqueio conservador. Nunca podem produzir autorização de processo.
- Leitura parcial e completa podem observar mudança de processo; handle retido e revalidação completa continuam obrigatórios.
- A falha ambiental e o fluxo completo de comandos ainda não foram reproduzidos/executados pelo autor deste plano. A causalidade dos dois ramos globais está demonstrada pelo código; R1 e R4 fornecem a prova comportamental pendente.
- Sem mudança em programas alheios, preferências do sistema, startup, perfis, scripts particulares, UAC ou agenda de desligamento. Não há decisão de implementação pendente do usuário.