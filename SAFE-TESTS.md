# Testes nativos isolados

Feche jogos e Playnite antes dos testes gráficos ou com AutoHotkey. Execute sem elevação. Não use o perfil real nem a pasta publish anterior. O executável Tests.WinUI/bin/x64/Release/net10.0-windows10.0.26100.0/win-x64/Bezoro.AhkManager.WinUI.Tests.exe exige seletor explícito e uma raiz inexistente:

- `--core <pasta-nova>`: persistência, recuperação, análise, projeção e política com adaptadores isolados.
- `--ui <outra-pasta-nova>`: janela de produção com serviço falso de testes; controles e ContentDialog reais, capturas do HWND próprio. Linhas de exemplo existem apenas no projeto de testes.
- `--ahk-owned <outra-pasta-nova>`: scripts inofensivos criados nessa raiz, interpretador v2 conhecido e caminhos/hashes permitidos. O proprietário isolado existe apenas no projeto de testes. Não contorna o mutex do produto.

Não há escrita real em Run/Startup/tarefas nem UAC real. A limpeza solicita saída normal, nunca termina processos à força, e registra pendências. Preserve stdout, stderr, código de saída, checks.txt, capturas e resultados. Solicitação enviada não equivale a efeito confirmado. O aplicativo legado e seus scripts nunca são alvos. O antigo tools/Run-SafeTests.ps1 valida somente o pacote legado.
