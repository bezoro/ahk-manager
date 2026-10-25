## Versão 1.0.4 — recuperação de arquivos e atalhos legíveis

Arquivos ausentes mostram **Localizar script…** e **Remover da lista** no painel. Localizar preserva o registro e suas preferências; remover da lista preserva o arquivo. Para arquivos existentes, **Excluir arquivo…** solicita confirmação e usa a Lixeira do Windows. Se o script estiver em execução, a mensagem explica que ele será parado primeiro. A exclusão só prossegue após o encerramento comprovado; recusa de saída, identidade incerta ou mudança de arquivo interrompem a operação.

Os atalhos identificados usam nomes legíveis para modificadores, teclas e mouse. Por exemplo, `!^#+u` aparece como **Ctrl + Alt + Shift + Win + U**; **F13** continua F13. A sintaxe original, origem e limitações ficam nos detalhes. A análise não executa o código nem deduz gestos complexos a partir de `LButton Up`.

Para declarar um clique duplo, coloque a anotação imediatamente antes da hotkey de mouse correspondente:

```autohotkey
; @gesture double-click
~LButton Up:: {
    ; A implementação do gesto continua sendo responsabilidade do script.
}
```

A interface mostra **Clique duplo com o botão esquerdo** como gesto informado pelo script. Uma anotação inválida ou separada da declaração não vira uma afirmação sobre o comportamento do código. Veja os resultados e limites da entrega em [VALIDATION.md](VALIDATION.md).

## Versão 1.0.3 — histórico da instalação anterior

O painel mostra apenas a descrição quando há `; @description` no cabeçalho. Sem ela, apresenta a descrição manual e um exemplo do cabeçalho. As colunas **Iniciar com Windows** e **Administrador** usam checkboxes; falhas ao salvar preservam as alterações ainda não salvas no painel. Buscar pelo texto do cabeçalho também permite operar o script encontrado.

A observação distingue intérpretes AutoHotkey reconhecidos de processos comuns sem metadados legíveis. Um candidato AutoHotkey sem identidade suficiente continua não confirmado. **Reabrir como administrador** solicita confirmação do Windows quando o acesso elevado é necessário. A checkbox é uma preferência para a próxima execução; não altera o privilégio do processo atual nem provoca UAC ao salvar ou no login. Scripts administrativos continuam manuais. O gerenciador elevado solicita privilégio padrão para scripts desmarcados, sem fallback silencioso para administrador.

Passaram 126 testes Node, 45 cenários de interface, 36 verificações corretivas, 188 de core e 169 de backend. A integração normal real passou por 101 verificações com scripts temporários, incluindo início, recusa de duplicata, encerramento, reinício e limpeza confirmada. Em um gerenciador elevado, scripts temporários desmarcados iniciaram com privilégio padrão e marcados iniciaram elevados; ambos encerraram normalmente, com saída comprovada.

O pacote NSIS 1.0.3 foi instalado no local existente, com os 270 arquivos previstos conferidos e preservação do perfil, da entrada de login e dos arquivos alheios ao pacote. A abertura normal produziu uma captura própria com os quatro registros e as checkboxes. A transferência real para administrador foi confirmada pelo usuário, pelo recibo consumido e pelo token elevado do backend. O usuário confirmou os status corretos na janela administrativa. A composição Mica, outras escalas de DPI e o próximo login real não foram revalidados nesta entrega; consulte [VALIDATION.md](VALIDATION.md).
## Descrição do cabeçalho (1.0.2)

O Inspector usa primeiro as linhas `; @description Texto` do cabeçalho do arquivo raiz, unidas em múltiplas linhas. Sem uma tag não vazia, usa a descrição manual salva. A descrição manual continua editável e independente; o texto derivado não é salvo no catálogo. Edite o cabeçalho no arquivo e pressione F5 para atualizar; não há watcher nem promessa de atualização instantânea.

```autohotkey
; @description Alterna os ícones da área de trabalho.
; @description Funciona com duplo clique ou Ctrl+Alt+D.
#Requires AutoHotkey v2.0
```

A análise termina no primeiro código ou diretiva não permitida, inclusive `#Include`. Não importa descrições de includes, strings ou comentários de bloco. O limite é de 64 linhas/8192 caracteres de cabeçalho e 4000 de descrição; excesso ou NUL rejeita o valor inteiro e mantém o fallback manual com diagnóstico parcial. A busca usa nome, caminho e descrição efetiva.

A versão 1.0.2 foi revisada, empacotada e instalada no mesmo local, preservando perfil, scripts e inicialização com Windows. O usuário confirmou a descrição ao selecionar um script e a atualização por F5 na janela instalada. As referências à validação visual 1.0.1 abaixo são históricas.

# AHK Manager — Windows 11

**Versão 1.0.1:** a correção da tela cinza foi instalada e validada no Windows 11. Foram verificados lista, detalhes, busca, menus, páginas, temas claro/escuro, fechamento para a bandeja e reabertura da mesma instância. A preferência final é **Seguir o sistema**. A integração com AutoHotkey real passou por 97 verificações com scripts temporários inofensivos. Ainda há limites de aceitação, descritos em [VALIDATION.md](VALIDATION.md); esta não é uma aprovação geral de distribuição.

Gerenciador de scripts AutoHotkey **v2** para Windows 11 x64. A interface usa Electron, TypeScript, React e Fluent UI; o backend .NET conserva a autoridade de processos, identidade, persistência e recuperação. Os controles Fluent são controles web. A moldura e os botões da janela, seletores de arquivos, confirmações e área de notificação usam integração do Windows. Mica é solicitado pela API suportada no Windows 11 22H2 ou superior; há fundo sólido quando indisponível ou em alto contraste. Tema e cor de destaque acompanham o sistema por padrão.

Instale pelo **AHK-Manager-1.0.4-Setup-x64.exe**, por usuário, sem privilégios administrativos. A pasta instalada contém **Bezoro.AhkManager.exe**, Electron e o backend autossuficiente em `backend/AhkManager.Backend.exe`. Não exige Node, npm, SDK, Visual Studio ou .NET instalado. AutoHotkey v2 é um requisito separado para executar scripts. A distribuição não tem assinatura digital de editor verificado; o Windows pode apresentar SmartScreen. Não há atualização automática.

Adicionar arquivos apenas cadastra caminhos. Iniciar valida o interpretador e o arquivo. Parar solicita saída normal e confirma o encerramento; OnExit pode recusar. Reiniciar só inicia uma substituição depois da saída comprovada. Não há encerramento forçado automático. Pausa e suspensão são diferentes; funções sem observação comprovável ficam indisponíveis com explicação.

A identidade do processo e a seleção são verificadas novamente após confirmações. Acesso insuficiente, múltiplas instâncias e observação incompleta aparecem como não confirmados. Não repita uma execução incerta: use Verificar pendências. Um Manager anterior aberto impede efeitos concorrentes; feche-o antes de operar pelo novo aplicativo.

A bandeja oferece **Sair mantendo os scripts** e **Parar scripts e sair…**. A segunda opção considera todo o catálogo, independentemente dos filtros, pede confirmação nativa com Cancelar por padrão e solicita saída apenas das identidades verificadas e aprovadas. Você pode cancelar novos pedidos pela bandeja; isso não reinicia scripts já parados. Mudanças de identidade, recusa de OnExit, operações pendentes ou resultados incertos mantêm o gerenciador aberto. Só a observação final de todo o escopo parado permite sair. Fechar a janela ou sair por outro caminho mantém a semântica de preservar scripts. Não há encerramento forçado. A lógica foi exercitada com adaptadores falsos; o ciclo nativo completo de **Parar scripts e sair…** ainda exige validação isolada. Nenhum teste autoriza parar scripts operacionais.

Editar usa um editor configurado ou a associação segura **Editar** do Windows. Não usa a associação Abrir de .ahk, que pode executar o arquivo. Argumentos do editor são informados um por linha; {file} representa o caminho, passado como um único argumento. Metadados não reescrevem o script.

Os dados operacionais novos ficam em `%LOCALAPPDATA%/Bezoro/AhkManagerElectron`, com JSON versionado, substituição atômica e cópia de segurança. O armazenamento Chromium separado fica em `%APPDATA%/Bezoro/AhkManagerElectron/Chromium`. Nenhum perfil anterior é copiado silenciosamente. A migração de perfil WinUI exige seleção, prévia, confirmação e backup exato, preservando operações pendentes e a origem. A importação de catálogo legado é um fluxo separado, também explícito. Configuração inválida abre para recuperação sem sobrescrever a origem.

O início com Windows é opcional e por usuário. Iniciar o Manager, mostrar sua janela e iniciar cada script são preferências separadas. Scripts administrativos exigem confirmação manual e não iniciam automaticamente. Desabilitar o Manager no logon mantém as preferências dos scripts, mas deixa-as inativas. Uma entrada WinUI anterior permanece em conflito até uma transferência explicitamente confirmada e verificada; não é criada entrada duplicada. Fechar pode manter o aplicativo na área de notificação; **Sair do Manager não encerra scripts**.

A análise estática de atalhos é limitada. Não avalia código e não cobre todos os contextos, atalhos dinâmicos ou programas externos. Atalhos manuais são apenas metadados. A atividade registra solicitações e resultados observados, não eventos internos inventados.

Desenvolvimento: Node **24.22**, npm **12.2**, .NET SDK **10.0.402**, runtime **10.0.13** e Windows SDK **26100**. `package-lock.json` e os locks NuGet fixam as dependências. Execute `tools/Build-Electron.ps1 -OutputRoot <pasta-nova>` e `tools/Package-Electron.ps1 -BuildRoot <mesma-pasta> -OutputRoot <outra-pasta-nova>`. Use `npm.cmd`, sem contornar políticas do PowerShell. O download oficial `electron/install.js` verifica o runtime; `npm ci --ignore-scripts` mantém hooks arbitrários desativados. A solução ativa contém Backend e Tests.Backend. Fontes WinUI/WPF e seus testes permanecem arquivados e fora do pacote Electron.

Integração real opcional: `Tests.Backend.exe --ahk-owned --root <pasta-absoluta-inexistente>`, em Windows com token normal e AutoHotkey v2 conhecido. O seletor usa arquivos e perfis temporários, sem hotkeys, envio de entrada, escrita de inicialização ou UAC. A sequência tem prazo de 180 segundos e limpeza independente de 30 segundos; supervisione com margem de inicialização, sem matar processos ao vencer um prazo. Falhas e saídas incertas conservam recibos para recuperação explícita, sem repetição automática. Não crie o marcador OnExit manualmente para contornar uma falha. O ensaio não valida UI, bandeja ou descoberta operacional completa.

Ctrl+F pesquisa; F5 atualiza; Tab percorre os controles. Consulte [SAFE-TESTS.md](SAFE-TESTS.md) e [VALIDATION.md](VALIDATION.md).

A atualização é manual: escolha **Sair mantendo os scripts**, desinstale a versão anterior preservando os perfis e scripts e instale a nova versão. Esse fluxo foi validado nas substituições 1.0.0 → 1.0.1, 1.0.1 → 1.0.2 e 1.0.2 → 1.0.3, preservando o catálogo, os arquivos dos scripts e a entrada de login existente. A atualização in-place é recusada antes de remover arquivos ou alterar a entrada de login. Guarde a distribuição anterior e uma cópia dos dados; depois da reinstalação, confira a preferência de inicialização com Windows e confirme novamente seu registro, se desejar usá-la. O instalador não encerra árvores de processos.

Para remover, saia do Manager e use o desinstalador do Windows. Ele oferece remover somente a entrada de login que corresponde exatamente à instalação atual, com confirmação padrão Cancelar/Não. Antes de qualquer remoção, verifica a lista exata de arquivos empacotados, seus hashes e caminhos sem redirecionamento. Arquivos alterados ou caminhos inacessíveis interrompem a remoção; arquivos alheios à lista e diretórios não vazios são preservados. Os perfis, backups e scripts permanecem disponíveis; sua remoção deve ser feita separadamente pelo usuário.
