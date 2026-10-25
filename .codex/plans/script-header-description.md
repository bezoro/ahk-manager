# READY — descrição do cabeçalho do script

Pedido: implementar a ideia registrada e acrescentar descrições aos scripts existentes. Revisão independente deste plano antes da implementação; um implementador para fonte/checks, coordenador para Git, cabeçalhos privados e aplicação live. Trabalhar somente em main, sem branches/worktrees adicionais; preservar alterações de terceiros. Este plano não executa scripts nem modifica o perfil ou a instalação.

Base da fonte: main `6045dffd63cc1a2e88e1634a821c639d6bb6afda`. Baseline separado dos arquivos privados: `C:/Users/Bezor/Documents/Codex/AhkManager-header-description-2026-10-24/baseline-v1.json`, SHA256 `5da63163d9070343769b754a8c7536055497f5789de5cb191f620197fa8a0c9c`; não publicar seu conteúdo no GitHub. O coordenador já verificou ferramentas e versões; não repetir descoberta.

## Comportamento e formato definidos

```autohotkey
; @description Alterna os ícones da área de trabalho.
; @description Funciona por duplo clique no espaço vazio ou Ctrl+Alt+D.

#Requires AutoHotkey v2.0
```

Ler somente o cabeçalho do arquivo raiz. Aceitar indentação horizontal antes de `; @description`, marcador exatamente nessa caixa e com espaço entre `;` e `@description`; após o marcador, texto separado por espaço. Aparar espaços nas extremidades de cada texto e unir tags não vazias com `\n` real. Tags vazias não produzem descrição. Permitir BOM inicial, linhas vazias, comentários comuns e as diretivas iniciais #Requires/#SingleInstance, reconhecidas com limite de palavra. Ignorar conteúdo de comentários de bloco; código depois do fechamento encerra o cabeçalho. Encerrar na primeira linha executável ou outra diretiva, inclusive #Include. Nunca extrair tags de strings, corpo, continuação ou arquivos incluídos.

Limites: primeiras64 linhas físicas e8192 caracteres do cabeçalho, descrição até4000 caracteres. Não publicar fragmento como descrição completa quando exceder limite; retornar ausência e Issue PartialAnalysis. Rejeitar também o cabeçalho derivado inteiro se contiver U+0000 (NUL), inclusive no texto de uma tag: HeaderDescription=null e Issue PartialAnalysis com diagnóstico limitado, sem copiar NUL/conteúdo bruto para o diagnóstico. Não apagar apenas o caractere nem publicar texto parcial; preservar o fallback manual. Decode aceitar Unicode válido não garante que o texto satisfaça o DTO. Reutilizar os limites existentes de arquivos/bytes/includes e Decode para UTF8/BOM e UTF16 LE/BE; não acrescentar I/O duplicado ou parser completo de AHK. Erro de leitura/decodificação mantém ausência de cabeçalho e diagnóstico existente; cancelamento continua propagado. Erro em include não apaga cabeçalho válido já obtido do arquivo raiz.

**Precedência:** cabeçalho não vazio é principal; sem ele, usar a descrição manual existente. Manter `Description`/`description` como valor manual, persistido sem alteração. Acrescentar `HeaderDescription` nullable com default null ao resultado da análise e ao ScriptSnapshot; no TypeScript, `headerDescription` opcional/nullable, validado estritamente quando presente (string até4000 ou null). Não alterar StateDocuments, schema JSON, ScriptMetadata, protocolo de operações ou criar flag de override. Source generation existente serializa o campo; sem reflection.

Inspector apresenta texto React escapado, multiline e com quebra de palavras. Indicar “Descrição do cabeçalho” e que a edição desse texto ocorre no arquivo; rotular o textarea como descrição manual, explicando o fallback. O editor/draft usa exclusivamente `script.description`; atualizar cabeçalho não suja, substitui nem salva o draft manual. Remover tags do arquivo restaura imediatamente o fallback no próximo refresh.

Atualização: aproveitar análise assíncrona existente no Load, Add/Commit e Refresh/F5, inclusive eventos atuais que chamam Refresh. Seleção usa o snapshot já disponível, sem leitura síncrona no renderer. Documentar F5 após alteração externa; não prometer atualização instantânea nem adicionar watcher/timer. Alinhar apenas a busca de descrição entre backend e renderer: nome + caminho + descrição efetiva (cabeçalho ou fallback), preservando filtros e validação de seleção.

## Reuso e arquivos permitidos

Fonte verificada: `HotkeyAnalyzer.cs:8–39` já lê/decodifica raiz e includes em worker; `ManagerService.cs:90–93,121–133,245` reanalisa no refresh e separa análise de metadados persistidos. `renderer/model.ts:15–24` é o módulo real de draft; preservá-lo. `ViewProjection.cs:13` busca descrição manual enquanto o renderer busca só nome/caminho, exigindo o alinhamento acima. `Inspector.tsx:24,33` separa exibição e edição; `shared/validation.ts:14–17,26` já aceita propriedades opcionais declaradas sem aceitar campos arbitrários.

Um implementador pode editar somente estes caminhos, para a feature e seus checks:

- C# ativo: `Presentation.WinUI/Services/{HotkeyAnalyzer.cs,ManagerService.cs,ViewProjection.cs}` e `Presentation.WinUI/Models/ManagerContracts.cs`. Colocar a transformação pura de cabeçalho no arquivo HotkeyAnalyzer existente; Backend já o compila por link, sem novo projeto/dependência.
- Fronteira: `Presentation.Electron/src/shared/{contracts.ts,validation.ts}`.
- Renderer: `Presentation.Electron/src/renderer/{model.ts,styles.css,resources/pt-BR.ts,components/Inspector.tsx}`. Nenhuma alteração em startup, tray, privilégios ou ações de processo.
- Checks: `Tests.WinUI/Core/{DomainChecks.cs,ManagerChecks.cs}`, `Tests.Backend/BackendChecks.cs`; `Presentation.Electron/tests/renderer/{model.test.ts,scenarios.ts}` e `tests/main/{fixtures.ts,backend.test.ts,presentation-harness.ts}`. Usar links/ProjectReference existentes de Tests.Backend; não compilar/executar UI WinUI arquivada.
- Documentação: `README.md` e `VALIDATION.md`, somente formato, precedência, F5 e resultados/limites desta entrega.

Coordenador controla versão1.0.2 em `Presentation.Electron/{package.json,package-lock.json}`, pacote, fonte/Git e instalação. Qualquer expansão de escopo deve vir de necessidade concreta, não de abstração especulativa.

## Cabeçalhos privados: entrega separada

O coordenador já tem scout e originais completos. Acrescentar apenas comentários aos três arquivos presentes, após revisão das descrições e hash pré-escrita. Preservar BOM, encoding, newline e **todos os bytes originais após o prefixo inserido**, inclusive mistura de newlines no Unity. Sem formatar corpo, recarregar, reiniciar ou executar script. O executável em andamento continua executando sua versão carregada.

Usar os três prefixos aprovados em `C:/Users/Bezor/Documents/Codex/AhkManager-header-description-2026-10-24/private-header-proposals-v1.json` (SHA256 `9da4c9e6e9ab032ac8b5d19f7aed0c162eb8d380fd0b117022d093490f905151`), com caminhos/hashes originais vinculados pelo handoff privado schema1 `private-script-scout-handoff-v1.json` (SHA256 `088e42444718c7efc3e463393cf8673f078cc013443433964672881bbbd73534`). Não repetir análise nem copiar conteúdo privado para este plano/repositório. O baseline e essas referências determinam os caminhos exatos, evitando inferir diretórios.

A quarta entrada, startup.ahk, está ausente: preservar ausência e entrada de catálogo; não recriar, remover ou adivinhar descrição. Não incluir arquivos privados ou snapshots no commit do produto. As propostas externas usam comentários simples `; @description`; preservar o restante do arquivo byte a byte.
## Checks e conclusão

Implementador prepara/builda/testa sequencialmente, sem interferir no Manager33500/backend48284 ou scripts operacionais:

1. Parser: tags antes/depois de diretivas permitidas, multiline/whitespace/vazias, ausência, marcador incorreto, BOM UTF8 e UTF16 LE/BE, strings/corpo/bloco/includes excluídos, limites, leitura/decodificação inválida e cancelamento; preservar análise de hotkeys. Incluir a entrada `; @description a\0b` com NUL real: rejeitar o cabeçalho inteiro, manter null e diagnóstico limitado sem NUL; não aceitar a versão com o caractere removido.
2. Manager fake/temporário: Add/Load/Refresh obtêm cabeçalho; modificação e remoção de tags/missing file atualizam no F5; include inválido não apaga raiz válida; fallback manual, save/reload e draft não persistem texto derivado. Busca efetiva concorda com seleção/projeção. Zero execução AHK e zero escrita real de startup.
3. DTO: roundtrip source-generated e strict validator aceitam ausência/null/texto válido do novo campo e rejeitam tipo/tamanho inválidos; desconhecidos continuam recusados. Regressão integrada: o arquivo com tag contendo NUL deve gerar snapshot com headerDescription=null, descrição manual intacta e Issue limitado; esse snapshot passa serialização/validação e não derruba a conexão. Um payload de snapshot que tente enviar NUL no campo deve continuar rejeitado pelo validador estrito existente; não relaxar text(). Node verifica metadataOf/newDraft/reconcileDraft: cabeçalho nunca vira manual, dirty draft sobrevive a mudanças e filtros.
4. Rodar os seletores fake `Tests.Backend --backend` e `--core`, cada um com `--root <raiz-absoluta-nova>`, pois núcleo/análise/contrato foram afetados. Em Presentation.Electron: `npm.cmd run typecheck` e `npm.cmd run test -- --out <raiz-absoluta-nova>`. Não rodar --ahk-owned, processos AHK, startup real ou suites arquivadas.
5. Harness Electron existente, sem backend/perfil real: cenários/scenarios mostram cabeçalho com fallback manual ainda editável, preservam draft ao refresh, removem cabeçalho, exibem `<script>`/HTML como texto e multiline sem cortar conteúdo em claro/escuro. Usar frontend/scenarios compilados e `--output` novo; manter recibo de processo próprio e resultados/capturas. Sem input global, fechamento de Playnite ou alteração de tema do Windows.

Após checks e revisão independente do candidato, coordenador produz payload self-contained1.0.2 em staging novo usando tools/Package-Electron.ps1 e ferramentas já verificadas. Instalar/aplicar somente pelo workflow live preparado; sem force-kill nem parar scripts. Manter checkpoints/rollback e honrar o main limpo pedido pelo usuário. O pacote inclui também as correções já revisadas em main ainda ausentes no1.0.1; identificá-las honestamente na entrega, sem reabrir implementação fora desta feature.

Conclusão exige: comentário exibido corretamente ao selecionar; manual preservado e fallback comprovado; atualização por F5; checks ligados ao candidato e revisão aprovados; três prefixos privados conferidos contra os originais sem mudar o restante; ausência do quarto preservada; pacote/app atualizado e Git main versionado pelo coordenador. Fonte/checks, edição privada e aplicação live têm evidências e estados separados. Não considerar apenas documentação ou código fonte como entrega concluída.