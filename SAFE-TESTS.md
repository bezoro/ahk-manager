# Testes nativos isolados

O workflow [Windows CI](.github/workflows/windows-ci.yml) executa em `windows-2025` a cada push em `main`, pull request ou acionamento manual. Usa o SDK de `global.json`, Node 24.22.0 e dependências travadas. Publica o backend e seus testes, executa `--backend`, `--core`, `--corrections` e `--startup-inspection`, verifica os tipos TypeScript, roda os testes Node com os snapshots reais produzidos pela suíte .NET e gera o bundle de produção. Verificações independentes continuam após falhas de outras suítes; o job permanece reprovado. Os testes Node integrados exigem que a suíte backend tenha produzido seus snapshots com sucesso.

O AutoHotkey v2.0.30 portátil vem da release oficial, com SHA256 fixado. Ele é extraído somente na máquina descartável do runner para a descoberta e leitura dos metadados de versão. Nenhum instalador, script AHK ou seletor `--ahk-owned` é executado. A consulta WMI de startup é somente leitura; os testes de política usam adaptadores falsos e perfis temporários. Logs e snapshots ficam no artefato `windows-ci-evidence` por 14 dias, inclusive em falhas. A CI não valida UAC interativo, login/logoff real, composição gráfica, cenários do harness Electron ou instalação NSIS.

O produto ativo é Electron com backend .NET. Os testes de TypeScript usam o runner Node e adaptadores falsos: em `Presentation.Electron`, execute `npm.cmd run test -- --out <pasta-absoluta-nova>`. Não abrem janelas, executam scripts ou escrevem Run/Startup. `npm.cmd run typecheck` verifica a fronteira compartilhada e o renderer. Os testes novos do backend ficam em `Tests.Backend`; seguem os seletores explícitos daquele projeto e usam somente perfis temporários e adaptadores de inicialização falsos.

Não há argumento de produto para trocar perfil, ignorar o proprietário de efeitos, desativar o sandbox ou injetar código de teste. Integração com o executável empacotado exige nome e layout finais, parentela nativa comprovada e o novo perfil normal. A primeira execução normal é uma etapa controlada após revisão; testes isolados anteriores não dependem dela.

O instalador deve ser ensaiado em destino isolado, com o Manager fechado. Inicialização real permanece desautorizada nos testes. Verificar instalação, atualização, desinstalação e preservação dos dados é diferente de apenas compilar o NSIS. Uma confirmação de shutdown só vale junto com a saída observada do backend; prazo excedido é desconhecido e não autoriza matar processos ou repetir efeitos.

O seletor atual `Tests.Backend.exe --inventory-scoped-readonly --root <pasta-absoluta-inexistente>` consulta uma vez o inventário delimitado do ensaio, com prazo de 30 segundos. Cria apenas seus arquivos inertes de diagnóstico e não inicia, encerra ou exclui scripts. O JSON contém contadores e códigos fixos, sem caminhos ou argumentos de scripts externos.

O ensaio `--ahk-owned --root <outra-pasta-absoluta-inexistente>` mantém um conjunto fechado de identidades externas comprovadamente anteriores à raiz. Só as exclui da observação do ensaio enquanto identidade e argumentos permanecem iguais; isso não modifica a política de observação do produto. Novas instâncias, identidades alteradas ou informações insuficientes continuam bloqueando uma conclusão segura. Nunca reutilize a raiz de uma tentativa anterior.

A etapa nativa de reciclagem exige o helper Electron de diagnóstico auditado, definido por `AHK_OWNED_TRASH_HELPER`, com caminho e hash fixados no ensaio. Esse helper e seus recibos pertencem à preparação local de validação e não são distribuídos no aplicativo. O comando não constitui um teste portátil sem essas dependências previamente preparadas; os testes comuns de core/backend/Node continuam separados.

Os seletores WinUI abaixo são históricos e não validam a interface Electron:

Feche jogos e Playnite antes dos testes gráficos ou com AutoHotkey. Execute sem elevação. Não use o perfil real nem a pasta publish anterior. O executável Tests.WinUI/bin/x64/Release/net10.0-windows10.0.26100.0/win-x64/Bezoro.AhkManager.WinUI.Tests.exe exige seletor explícito e uma raiz inexistente:

- `--core <pasta-nova>`: persistência, recuperação, análise, projeção e política com adaptadores isolados.
- `--ui <outra-pasta-nova>`: janela de produção com serviço falso de testes; controles e ContentDialog reais, capturas do HWND próprio. Linhas de exemplo existem apenas no projeto de testes.
- `--ahk-owned <outra-pasta-nova>`: scripts inofensivos criados nessa raiz, interpretador v2 conhecido e caminhos/hashes permitidos. O proprietário isolado existe apenas no projeto de testes. Não contorna o mutex do produto.

Não há escrita real em Run/Startup/tarefas nem UAC real. A limpeza solicita saída normal, nunca termina processos à força, e registra pendências. Preserve stdout, stderr, código de saída, checks.txt, capturas e resultados. Solicitação enviada não equivale a efeito confirmado. O aplicativo legado e seus scripts nunca são alvos. O antigo tools/Run-SafeTests.ps1 valida somente o pacote legado.
