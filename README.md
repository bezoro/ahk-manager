# Bezoro AHK Manager

Gerenciador Windows de scripts AutoHotkey, com interface em português. Abra `Abrir.cmd`. O executável fica em `publish\Bezoro.AhkManager.exe` e usa o .NET Desktop Runtime 8 já instalado. Não exige instalação global nem administrador para consultar os dados acessíveis.

Na primeira abertura, o aplicativo descobre arquivos em `Documents\AutoHotkey`, sem backups ou logs, e consulta fontes externas de inicialização. Usa a pasta Documentos do Windows quando a raiz AutoHotkey existe; se ela estiver ausente, usa a raiz convencional existente em `UserProfile\Documents\AutoHotkey`. A escolha permanece fixa durante a sessão. Também aceita cadastro manual. A busca considera nome, descrição e caminho. Selecione um script para conferir processos, fontes externas, interpretador e descrição. Descrições inferidas devem ser conferidas; salvar uma descrição altera apenas o catálogo do aplicativo.

## Ações

- **Iniciar** exige interpretador AutoHotkey existente, `#Requires AutoHotkey v1...` ou `v2...` compatível e nenhuma possível duplicata. Versões mínimas são verificadas. Requisitos adicionais não reconhecidos recusam o início.
- **Parar** interrompe uma única instância identificada. O aplicativo verifica PID, criação, executável e script novamente, e termina o processo pelo mesmo handle nativo. Não encerra árvores ou filhos.
- **Reiniciar** confere arquivo, interpretador e requisitos antes de parar, confirma a saída e confere novamente antes de iniciar. Uma falha previsível nesses requisitos preserva o processo atual. Falhas parciais aparecem na mensagem. Cliques concorrentes são recusados enquanto outra ação está em andamento.
- **Bloquear início somente pelo gerenciador** impede Iniciar e Reiniciar aqui. Não para processos nem altera atalhos, tarefas, registro ou supervisores externos.
- **Editar arquivo** abre o Bloco de Notas. Não usa a associação `.ahk`, que poderia executar o script. Salvar no editor altera o próprio arquivo.
- **Enviar à lixeira** exige script parado e inspeção suficiente. A confirmação mostra o caminho e tem Cancelar como padrão. Usa IFileOperation com RECYCLEONDELETE e um callback que recusa operação sem sinal de reciclagem. Não oferece exclusão permanente alternativa. Erro ou cancelamento preserva o registro; arquivo ausente continua no catálogo.

O supervisor convencional `Documents\AutoHotkey\startup.ahk` pode relançar filhos de `scripts\startup` em até 60 segundos. O gerenciador avisa antes de parar um filho. Iniciar ou reiniciar filho com supervisor ativo é bloqueado, assim como iniciar/reiniciar supervisor enquanto há filhos vivos. Parar o supervisor deixa os filhos vivos. Nenhuma configuração externa é alterada.

## Cobertura e limites

A descoberta cobre a raiz convencional, cadastro manual, pastas Startup do usuário e comum, atalhos resolvidos em consulta PowerShell somente leitura, Run/RunOnce acessíveis e ações de tarefas agendadas acessíveis. Não procura em todos os discos. Há limites de 1.000 arquivos examinados, 500 pastas e seis níveis. Um arquivo inacessível não interrompe a busca por outros arquivos ou subpastas. Comandos indiretos, wrappers, variáveis em comandos de startup, caminhos relativos, fontes inacessíveis, links, discos de rede e caminhos especiais permanecem desconhecidos ou são recusados. Linhas de comando de processos são lidas literalmente, incluindo sinais de porcentagem nos nomes de arquivo. Lacunas aparecem na faixa acima da lista; passe o mouse para detalhes.

A leitura limitada do script aceita UTF-8 com ou sem BOM e UTF-16 com BOM. UTF-32 é recusado com mensagem específica para execução; o cadastro e a descrição manual continuam disponíveis, sem interromper a descoberta dos outros arquivos.

O estado de tarefa agendada, inclusive a elevação configurada, não comprova a identidade de um processo. Processos AutoHotkey elevados ou inacessíveis aparecem na contagem de identidade desconhecida. Qualquer identidade desconhecida bloqueia iniciar, reiniciar e enviar à lixeira. Isso pode impedir iniciar scripts em uma sessão que já tenha um AHK elevado. Para ampliar a consulta, o usuário pode abrir o gerenciador como administrador pelo comando normal do Windows e aceitar o UAC. O aplicativo não se promove automaticamente.

A identificação consulta processos cujo nome começa com AutoHotkey. Executáveis renomeados e scripts compilados não estão cobertos. Não há garantia contra outro aplicativo iniciar um script depois da última consulta; as ações locais são serializadas e a identidade é revalidada antes do efeito. O aplicativo não instala serviço, modifica segurança, migra scripts para Windhawk ou envia dados.

O catálogo fica em `%LOCALAPPDATA%\Bezoro\AhkManager`. Escrita atômica mantém `catalog.json.bak`. Catálogo corrompido é preservado, backup válido pode ser consultado e gravação é bloqueada para permitir recuperação manual. Um script bloqueado ou inacessível não invalida seus metadados nem impede salvar descrições de outros registros. Cadastro e ações fazem a verificação física do caminho; carregar e salvar o catálogo verificam sua estrutura. Um lock impede dois escritores na mesma pasta. Logs rotacionados contêm apenas horário, operação e resultado. Não incluem descrições, caminhos, argumentos ou conteúdo dos scripts. Para dados isolados, use `Bezoro.AhkManager.exe --data-dir "C:\pasta\isolada"`.

## Compilação e validação

SDK fixado em 8.0.425. Não há dependência NuGet externa; `NuGet.Config` remove fontes de rede. Fonte do aplicativo em seis arquivos coesos de `App`, e fixtures de console em `Tests`. JSON usa código gerado em compilação e fallback de reflexão desabilitado. Não há reflexão no C# escrito.

```powershell
$env:DOTNET_CLI_HOME = Join-Path $PWD '.tools'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
dotnet build Bezoro.AhkManager.sln -c Release
dotnet Tests\bin\Release\net8.0-windows\Bezoro.AhkManager.Tests.dll
dotnet publish App\Bezoro.AhkManager.csproj -c Release --no-restore -o publish
```

Antes de executar testes neste PC, confirmar que não há jogo ou Playnite aberto. O teste comum usa somente fixtures em Temp. `--native` é reservado à integração autorizada de um AHK temporário inofensivo: exercita consulta e handles nativos, sem contornar a política da interface. O modo interno `--render-fixture <PNG> --data-dir <pasta>` renderiza dados fictícios sem consultar ou iniciar scripts reais.

Veja `VALIDATION.md` e os logs completos em `evidence`. A renderização interna não comprova interação Windows, teclado ou DPI em tela. Lixeira real não foi exercitada sem confirmação interativa disponível.

Durante a primeira configuração do SDK, o CLI exibiu uma mensagem de certificado HTTPS de desenvolvimento instalado. A inspeção de metadados do supervisor encontrou certificado preexistente, sem nova criação confirmada e sem confiança nas lojas Root. Nenhuma ação de confiar, exportar ou remover certificado foi feita.

## Abrir a versão em V:\Repos

A abertura recomendada nesta cópia é pelo atalho `Abrir gerenciador AHK.lnk`, na pasta `V:\Repos\Bezoro.AhkManager`. Ele abre diretamente `publish\Bezoro.AhkManager.exe`, uma aplicação C#/WinForms com subsystem Windows GUI, sem CMD ou PowerShell como intermediário. Também é possível abrir esse executável diretamente. `Abrir.cmd` permanece somente como alternativa de compatibilidade.

O projeto original no workspace de criação foi preservado. Esta cópia portátil contém fontes, projetos, configuração, publicação local e evidências; caches de SDK e pastas bin/obj de compilação não foram copiados. As descrições e bloqueios locais continuam usando a pasta de dados do aplicativo por usuário. Nenhum script ou fonte externa de inicialização foi alterado nesta transferência.
## Ícone do aplicativo

A arte fornecida está preservada em `ahk-manager-icon.png` e copiada sem alterações para `Assets/AppIcon-master.png`. O executável e a janela usam `Assets/AppIcon.ico`, com transparência e tamanhos 16, 24, 32, 48 e 256 px. A prévia em fundos claro e escuro está em `Assets/AppIcon-preview.png`.

O conversor local `Assets/IconPackager` usa apenas .NET/System.Drawing; ele redimensiona a imagem existente e empacota o ICO, sem redesenhar a arte. O PNG é o arquivo mestre desta versão. Para gerar uma nova saída, compile o conversor e execute `dotnet Assets/IconPackager/bin/Release/net8.0-windows/IconPackager.dll Assets/AppIcon-master.png <diretório-novo>`; a saída precisa estar livre para evitar sobrescritas.

Os atalhos existentes continuam abrindo diretamente `publish/Bezoro.AhkManager.exe`. A versão anterior dos binários foi preservada em `evidence/icon-integration/pre-icon-publish/f694217daf58408486d241f3fb445871`.
## Nova apresentação WPF

A interface moderna está no projeto paralelo `Presentation.Wpf/Bezoro.AhkManager.Wpf.csproj`, sem dependências NuGet. Os quatro arquivos de domínio em App são ligados diretamente e permanecem intactos. O projeto WinForms e seus testes continuam disponíveis para retorno. A publicação WPF foi preparada em staging e aguarda a revisão independente antes da ativação autorizada; os atalhos continuam apontando para `publish/Bezoro.AhkManager.exe`.

Build: `dotnet build Tests.WpfUI/Tests.WpfUI.csproj -c Release`; isso compila também a apresentação WPF. O harness `Tests.WpfUI` recebe obrigatoriamente um diretório isolado e usa somente processos/lixeira/diálogos fake. Para capturar uma imagem sem consultar scripts reais, o executável aceita `--data-dir <pasta-isolada> --render-fixture <imagem.png>`, com `--render-small` e `--scale 1.5` ou `2` opcionais. A escala é uma simulação de render, não um teste em monitor real.

Checklist manual após abrir a versão final: percorrer os controles com Tab, navegar a lista com as setas, redimensionar a janela/divisor, conferir descrições não salvas e cancelar confirmações com Enter/Esc/fechar. Interação nativa e monitores de DPI diferente ainda não foram testados.

A nova interface mantém descrições ainda não salvas em rascunhos por script enquanto a janela está aberta. Copiar caminho, cancelar uma confirmação, trocar seleção ou falhar ao salvar não descarta o texto. Apenas salvar com sucesso confirma o rascunho no catálogo; esses rascunhos em memória não sobrevivem ao fechamento do app.

Se a consulta de processos falhar depois de uma ação, o resultado da ação permanece visível. A lista/instâncias ficam identificadas como observação anterior; use Atualizar para recuperar o estado atual. Nenhum resultado anterior é tratado como confirmação de execução atual.

## WPF ativado — 2026-10-05T16:17:41.3080734Z

A versão WPF revisada está aplicada em `publish/Bezoro.AhkManager.exe`. Abra pelo atalho existente `Abrir gerenciador AHK.lnk` ou pela entrada AHK Manager no menu/Paleta. O destino, os argumentos e o diretório de trabalho dos atalhos foram mantidos. A janela normal não foi aberta automaticamente.

Retorno simples ao WinForms: feche o gerenciador e abra `V:\Repos\Bezoro.AhkManager\publish-winforms-0e72ca3b7f9240429a5935d2bc412cdc\Bezoro.AhkManager.exe`. Em PowerShell: `& 'V:\Repos\Bezoro.AhkManager\publish-winforms-0e72ca3b7f9240429a5935d2bc412cdc\Bezoro.AhkManager.exe'`. A publicação WinForms inteira permanece nesse diretório, com manifesto de hashes em evidence. O mesmo catálogo normal continua protegido pelas regras de lock/persistência existentes; não abra as duas versões simultaneamente.

Para compilar a versão atual: `dotnet build Presentation.Wpf/Bezoro.AhkManager.Wpf.csproj -c Release`. Para checks de UI use Tests.WpfUI com diretório isolado, seguindo VALIDATION; os scripts reais não fazem parte das fixtures.

Fase funcional em staging (2026-10-05, aguardando revisão)

A publicação principal e os atalhos continuam na versão anterior. O novo executável está em `evidence\functional-a1a9ba70c50d45b5b15ad7843948bcfe\staged-publish\Bezoro.AhkManager.exe`; não abra esse executável normal contra o catálogo real antes da conclusão da migração.

Para rever a interface com processos, editor, confirmações e lixeira fictícios, execute do diretório do projeto:

```powershell
& '.\evidence\functional-a1a9ba70c50d45b5b15ad7843948bcfe\staged-publish\Bezoro.AhkManager.exe' --fixture-ui --data-dir '.\evidence\functional-a1a9ba70c50d45b5b15ad7843948bcfe\revisao-isolada' --scenario pending
```

Esse modo exige perfil explícito e cria sempre um subperfil novo, sem abrir o catálogo existente nessa pasta. Todos os efeitos são fictícios. Para uma imagem sem interação, substitua `--fixture-ui` por `--render-fixture '.\evidence\functional-a1a9ba70c50d45b5b15ad7843948bcfe\revisao.png'`. Cenários: `normal`, `enabled`, `disabled`, `pending`, `suspended`, `readonly`, `unknown`, `stress`, `empty`.

A intenção Ativado/Desativado fica separada da execução e do bloqueio local de início. Desativar pede confirmação, persiste a intenção primeiro e só tenta parar com todos os gates seguros. Fontes externas ainda podem iniciar o script enquanto a migração estiver pendente. Scripts antigos continuam Não gerenciados após conversão explícita; scripts novos começam Não gerenciados. Parar um script Ativado orienta usar Desativar; Iniciar/Reiniciar um Desativado orienta usar Ativar.

Na versão normal compilada, X esconde na bandeja e mantém a supervisão; Sair deixa os scripts vivos. `--tray` é o argumento preparado para login normal, mas nenhum atalho de login foi criado. Recuperação automática exige catálogo primário válido e gravável, schema2, journal compatível concluído, owner exclusivo por SID/login e snapshot completo sem AHK desconhecido. São no máximo três inícios por script em dez minutos, com cooldown de 60/120/240 segundos e suspensão após a terceira falha. Reabrir no mesmo login conserva o limite.

O manifesto e o journal preparados em `evidence\functional-a1a9ba70c50d45b5b15ad7843948bcfe` são propostas revisáveis. O coordenador de migração aceita somente adaptadores fictícios nesta fase. O supervisor legado e o Startup reais foram preservados. O pincel está Externo/Pendente: nenhuma tarefa elevada, ACL, helper ou ação privilegiada foi implementada ou executada.


Correções da revisão funcional — staging final (2026-10-05)

A demonstração segura está no atalho `evidence\functional-exit-review-2ad243a80d704856b9a834d96ee6bbf9\staged-publish\Demonstrar supervisão segura.lnk`. Ele abre somente a interface fictícia, com perfil explícito e sempre um subperfil novo. Não foi instalado no Menu/Startup nem executado durante a validação. A publicação principal permanece na versão anterior; não use o executável normal de staging com o catálogo real antes da migração.

Iniciar/Reiniciar scripts Normal+Ativado agora compartilha os gates, cooldown e orçamento da recuperação automática. Reiniciar reserva duravelmente a tentativa antes de parar, preservando a instância se bloqueado. A confirmação é revalidada quando retorna, e o encerramento cancela o token já aceito pelo controller. Converter um catálogo antigo exige que os bytes do primário permaneçam iguais aos carregados; recusa preserva também o backup.

Quando o Explorer recriar a barra, o host tenta reinscrever o ícone. Se isso falhar, a janela fica acessível e uma nova tentativa ocorre no próximo ciclo. X com bandeja disponível continua escondendo. Sem bandeja, X solicita a mesma saída confirmada, com Cancelar padrão: cancelar mantém janela/owner/produtores; confirmar suspende produtores e espera a fila antes de encerrar o gerenciador, deixando scripts vivos. O comportamento nativo da bandeja/Explorer/logout não foi testado nesta fase.


## Preferência de administrador, versão intermediária de 5 de outubro

O staging revisável salva a opção "Executar como administrador" por script no schema 3. Marcar a opção não inicia processos, não solicita UAC e não altera tarefas, privilégios ou fontes externas. Nesta versão intermediária, **o início como administrador está indisponível**. Iniciar e Reiniciar ficam bloqueados para scripts marcados, inclusive nas APIs de controle. Não há início normal alternativo, solicitação automática de UAC nem consumo do limite de recuperação nesses ciclos. A tarefa externa do pincel continua pendente.

A conversão dos schemas 1 e 2 exige confirmação. O schema 1 continua sem intenção automática inferida. O schema 2 conserva suas intenções, migração e limites de recuperação. O campo booleano passa a ser obrigatório e único em cada registro do schema 3. Aplicativos antigos não devem ser usados para regravar esse catálogo; retorno à versão antiga requer preservar separadamente os dados anteriores, sem downgrade automático.

A publicação principal e os atalhos instalados continuam na versão anterior. Para experimentar somente dados e efeitos fictícios, use o novo atalho dentro deste staging, sem instalar em Inicializar ou Menu Iniciar:

`V:\Repos\Bezoro.AhkManager\evidence\elevation-01448105e4f1442b90c05746ca5f989a\staged-publish\Demonstrar preferência de administrador.lnk`

Alternativa em PowerShell, também isolada:

```powershell
& 'V:\Repos\Bezoro.AhkManager\evidence\elevation-01448105e4f1442b90c05746ca5f989a\staged-publish\Bezoro.AhkManager.exe' --fixture-ui --data-dir 'V:\Repos\Bezoro.AhkManager\evidence\elevation-01448105e4f1442b90c05746ca5f989a\demonstracao-data' --scenario normal
```

Essa demonstração sempre cria um catálogo filho novo; não abre o catálogo real nem consulta processos reais. O lançamento manual com UAC aguarda a decisão sobre como conservar uma solicitação aceita sem identidade de processo verificável. Ele não foi implementado nem testado nesta entrega.


### Staging intermediário corrigido após revisão

Use somente a demonstração corrigida `V:\Repos\Bezoro.AhkManager\evidence\elevation-01448105e4f1442b90c05746ca5f989a\review-schema-fadade3365324cf4acf1ae86e70226bf\staged-publish\Demonstrar preferência de administrador.lnk`. O staging anterior citado acima permanece como evidência histórica e não é a entrega final. A demonstração corrigida usa o executável novo e somente catálogo/efeitos fictícios.

Precisão do comportamento: quando Ativado e marcado para administrador, nenhum início automático, UAC automático ou reserva de tentativa acontece. Quando Desativado, a parada normal de uma instância verificável continua dependendo dos mesmos gates de segurança. Marcar ou desmarcar a preferência sozinho não inicia nem para processos. O lançamento manual elevado continua indisponível nesta versão intermediária.

```powershell
& 'V:\Repos\Bezoro.AhkManager\evidence\elevation-01448105e4f1442b90c05746ca5f989a\review-schema-fadade3365324cf4acf1ae86e70226bf\staged-publish\Bezoro.AhkManager.exe' --fixture-ui --data-dir 'V:\Repos\Bezoro.AhkManager\evidence\elevation-01448105e4f1442b90c05746ca5f989a\review-schema-fadade3365324cf4acf1ae86e70226bf\demonstracao-data' --scenario normal
```


### Demonstração final desta versão intermediária

A última correção exige que o catálogo ativo mantenha também o fingerprint dos bytes originais. Até alterações externas apenas de espaços ou ordem das propriedades bloqueiam gravações e efeitos dependentes do catálogo, até consulta explícita. Os estágios anteriores ficam preservados como histórico.

Use somente o novo atalho fixture `V:\Repos\Bezoro.AhkManager\evidence\elevation-01448105e4f1442b90c05746ca5f989a\review-fingerprint-d59281723c5744c6a1addeb56018e746\staged-publish\Demonstrar preferência de administrador.lnk` ou:

```powershell
& 'V:\Repos\Bezoro.AhkManager\evidence\elevation-01448105e4f1442b90c05746ca5f989a\review-fingerprint-d59281723c5744c6a1addeb56018e746\staged-publish\Bezoro.AhkManager.exe' --fixture-ui --data-dir 'V:\Repos\Bezoro.AhkManager\evidence\elevation-01448105e4f1442b90c05746ca5f989a\review-fingerprint-d59281723c5744c6a1addeb56018e746\demonstracao-data' --scenario normal
```

Essa demonstração não abre catálogo real nem consulta/inicia/encerra scripts reais. Preferência marcada continua com início elevado indisponível. Quando Ativado, não há início automático nem reserva de tentativa; Desativado conserva a parada normal verificável sob os gates existentes. Checkbox sozinha não provoca efeitos. A publicação principal não foi substituída.

## Início manual com UAC — pacote isolado de revisão

A implementação schema 4 está no pacote versionado `evidence/manual-uac-8009ffe973a54b9a889961855baf466c/staged-publish`. `Abrir AHK Manager.lnk` desse pacote abre a versão real normalmente, sem instalar Startup; `Demonstrar UAC seguro.lnk` abre somente o mundo de fixture, com dados isolados e lançadores falsos. Os atalhos foram preparados e inspecionados, sem execução. A publicação principal e seus atalhos continuam intactos.

“Executar como administrador” é uma preferência persistida; UAC acontece somente por Iniciar manual confirmado, com Cancelar padrão. Não prova privilégio atual, não altera tarefas/processos existentes, não oferece fallback normal e bloqueia Reiniciar antes de qualquer parada. A conversão 1/2/3→4 é explícita e preserva metadados e intenções existentes; schema 1 não recebe intenção automática inferida. O perfil passa a guardar apenas o vínculo com a autoridade fixa por usuário em `%LOCALAPPDATA%/Bezoro/AhkManager`; `--data-dir` não troca essa autoridade.

Um pedido sem confirmação suspende GLOBALMENTE todos os efeitos automáticos e todos os novos Iniciar/Reiniciar, em qualquer perfil. Reabertura, timeout, snapshot vazio, desmarcar a preferência ou remover/reimportar o cadastro não removem a pendência. Parar/Desativar manual outro caminho verificável mantém os gates normais e não pede UAC. “Verificar pedido” é consulta normal; exige candidato correlacionado e identidade positiva antes de uma remoção durável, sem alegar privilégio. Sem candidato confiável, a recuperação ainda não está disponível. Autoridade inválida fica bloqueada; apagar arquivos não é uma recuperação.

O pacote inclui `RECOVERY.md`, proposta futura e não implementada de consulta pontual para candidatos correlacionáveis. Não foram implementados helper, tarefa/ACL nova ou cutover real. Todos os testes elevados foram fakes; UAC real, GUI normal, teclado/bandeja/DPI físico e lixeira real permanecem não testados nesta fase. A compilação e os testes não constituem aprovação de ativação; a fonte está congelada para revisão independente.

## Entrega final de revisão manual UAC

O pacote final substitui os estágios intermediários desta fase: `evidence/manual-uac-8009ffe973a54b9a889961855baf466c/review-composition-eaf8914aeee44af088cfd2a65038284b/staged-publish`. Seus atalhos novos `Abrir AHK Manager.lnk` (abertura real normal sem argumentos) e `Demonstrar UAC seguro.lnk` (fixture isolada) não foram lançados. Live/atalhos instalados continuam intactos. Fonte congelada 37338C e DLL 5104FA passaram build/publishRelease e regressões focadas de revisão; sem UAC real ou helper.

A atualização da autoridade não aceita rebase externo para apagar pedidos conhecidos na sessão. O anchor não é proteção persistente contra restauração externa de um primário antigo válido com o mesmo vínculo após reabrir: restaurar arquivos antigos NÃO é recuperação suportada, nem preserva essa garantia. Preserve os arquivos atuais e consulte backups somente leitura. Operações normais, fechamento/reabertura, alteração da preferência ou do cadastro mantêm pedidos; nenhuma pendência expira ou é limpa por snapshot vazio. Limites e proposta futura estão em `RECOVERY.md` do pacote.


## 2026-10-05 — recuperação manual de pedidos

Staging somente, fonte congelada para revisão: `evidence/boot-recovery-6022eded63c345c3829529f39858b970/staged-publish`. Consulte o README curto do pacote e `VALIDATION-boot.md` na evidence. Autoridade versão2 migra apenas por confirmação explícita; pedidos antigos permanecem Legacy. Recuperação manual exige prova parcial de contador/DPAPI e ausência completa, sem iniciar scripts. DPAPI/boot/UAC reais não foram executados; publish ativo, scripts, tarefas, Startup e catálogo reais preservados. Binário antigo não pode operar dados versão2; não rebaixar/apagar/restaurar autoridade para desbloquear.
