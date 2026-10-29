# Atualizações do AHK Manager

O aplicativo oferece dois canais em **Atualizações**. **Estável** é o padrão e recebe versões publicadas após uma execução bem-sucedida da CI em `main`. **Testes** também oferece prévias de pull requests do próprio repositório, depois dos mesmos testes do Windows. Escolher o canal não instala uma versão. O aplicativo consulta as versões ao abrir, ao trocar de canal e a cada 15 minutos; baixar e instalar exigem uma ação do usuário.

Cada push aprovado na `main` gera uma Release estável com tag `vX.Y.Z`, calculada pelos Conventional Commits desde a maior tag ancestral. `feat` incrementa a minor; `fix`, refatorações e demais mudanças incrementam o patch. `!` ou `BREAKING CHANGE` incrementa a major. As versões anteriores não são sobrescritas.

Reexecutar a CI de um commit estável já marcado reutiliza sua versão. A publicação reconhece uma Release já concluída do mesmo commit e preserva seus assets. Tags pertencentes a outro commit interrompem a publicação. O número da execução da CI não faz parte da versão estável. As notas vêm dos commits, em texto simples para leitura no histórico do aplicativo.

As prévias usam SemVer no formato `X.Y.Z-test.<execução>.<tentativa>.<PR>`, por exemplo `1.3.1-test.24.1.2`. A execução e a tentativa distinguem builds, e o último número identifica o pull request. O cálculo considera também a maior tag estável disponível, para uma branch anterior à última atualização continuar oferecendo uma prévia mais nova. Cada prévia tem tag e assets próprios; uma nova execução não substitui uma anterior. Publicá-la não muda a Release estável mais recente do GitHub.

## Uso

Abra **Atualizações**, confira a versão e pressione **Atualizar**. O download informa progresso. O aplicativo pede confirmação, exige que alterações estejam salvas e operações tenham terminado, fecha o backend normalmente e reabre o Manager depois da troca. Scripts que já estão rodando continuam em execução. A reabertura usa `--foreground`, sem repetir o processamento de login dos scripts.

Para experimentar uma mudança antes do merge, escolha **Testes**, aguarde a consulta e pressione **Atualizar** na versão desejada. O histórico permite escolher outra prévia. O canal escolhido permanece salvo após reiniciar ou atualizar o aplicativo. Prévias podem conter problemas ainda em revisão.

Entrar explicitamente no canal de testes pode oferecer uma prévia anterior à versão estável instalada. Depois de instalar uma versão estável, permanecer no canal de testes não faz o aplicativo recomendar novamente uma prévia mais antiga; ela continua disponível no histórico.

Para sair do canal de testes, escolha **Estável**. Se estiver executando uma prévia, o aplicativo oferece a versão estável mais recente, mesmo quando ela tem uma versão menor. A troca também exige pressionar o botão de instalação. O canal estável não oferece instaladores de teste preservados no cache.

Uma versão antiga que ainda não tem o seletor de canais precisa receber primeiro a atualização estável que adiciona esse recurso. Esse primeiro passo também usa o botão **Atualizar** existente. Depois disso, testar pull requests não exige baixar ou instalar arquivos manualmente.

O histórico reúne Releases compatíveis e instaladores preservados neste computador. **Voltar para esta versão** usa o mesmo processo de verificação e instalação, permitindo downgrade somente após uma escolha explícita. Os dados do perfil e os scripts permanecem atuais; o retorno troca o aplicativo, sem desfazer edições feitas pelo usuário.

Releases de um repositório público podem ser consultadas e baixadas sem autenticação. Esse é o fluxo padrão. Se o repositório estiver privado ou o GitHub limitar as consultas, o acesso avançado aceita opcionalmente um token com acesso a `bezoro/ahk-manager` e permissão **Contents: Read-only**. A credencial fica cifrada por `safeStorage`, que usa a proteção do Windows. O aplicativo recusa armazenamento sem criptografia. O renderer só recebe a indicação de que o acesso foi configurado; não recebe o token salvo. **Remover acesso** apaga a credencial.

## Publicação e integridade

O job de compilação tem acesso de leitura. Um job separado, com `contents: write`, publica a versão estável apenas depois de todos os testes do Windows passarem em um push para a `main` deste repositório.

As prévias usam um workflow de publicação separado, acionado pela conclusão bem-sucedida da CI. Ele executa o código de publicação da `main`, confere o repositório de origem, o pull request ainda aberto, o commit atual e a identidade do artefato da execução aprovada. Builds de forks, PRs fechados ou commits substituídos não são publicados. O publicador não executa o instalador nem scripts vindos do PR e não entrega credenciais de publicação ao build do PR.

O publicador cria uma Release em rascunho, envia os assets e só depois a publica. Prévias têm a marcação de pré-lançamento e não podem substituir a Release estável mais recente. Uma versão já publicada é conferida, sem substituir seus arquivos.

Os assets são `AHK-Manager-<version>-Setup-x64.exe` e `ahk-manager-release.json`. O manifesto de protocolo 1 contém versão, commit, data, notas, nome, tamanho e hashes SHA256/SHA512 do instalador. O cliente só consulta o repositório fixo, valida o manifesto e reconstrói as URLs da API a partir dos IDs dos assets. Aceita HTTPS, limita tamanho e tempo e retira credenciais nos redirecionamentos para os servidores de assets do GitHub.

O `electron-updater` 6.8.11 cuida do download, cache, progresso e verificação SHA512. O aplicativo verifica SHA256 antes de preservar e entregar o instalador ao aplicador. A instalação padrão não baixa nem instala atualizações ao sair. O aplicador independente não acessa a rede.

Para distribuir versões assinadas, configure os secrets `WINDOWS_CSC_LINK` e `WINDOWS_CSC_KEY_PASSWORD`, e a variável `WINDOWS_PUBLISHER_NAME` no GitHub. O certificado é uma credencial externa e não pertence ao repositório. Com ele configurado, o empacotamento estável exige assinatura. O publicador de prévias assina o instalador no job de confiança e atualiza os hashes do manifesto antes da publicação. O atualizador verifica o editor nos dois canais, inclusive ao usar um instalador antigo do cache. Sem certificado configurado, os builds continuam sem assinatura de editor verificado; os hashes detectam corrupção, mas não substituem Authenticode.

## Aplicação e recuperação

O NSIS só aceita atualização de uma instalação por usuário com `UpdateProtocol=1`, caminho registrado correspondente e arquivos com identidade comprovada. A entrada Run e a tarefa administrativa são preservadas porque o executável permanece no mesmo caminho. A primeira migração de versões até 1.1 é manual, pois seus desinstaladores recusam `--updated`.

O aplicador é copiado para fora da instalação e recebe um plano limitado, com hash. Ele comprova PID, criação, usuário, sessão e imagem do processo pai antes de confirmar a preparação. O Manager só autoriza a aplicação depois de concluir as operações pendentes, tentar salvar a posição da janela e observar a saída do backend. O aplicador exige esse recibo vinculado à operação e à sessão, além da saída do Manager com código zero. Uma queda antes dessa confirmação não inicia a instalação. Depois disso, confere os instaladores novamente e preserva a instalação completa com hashes e as duas chaves próprias do Registro. Só então executa NSIS. Se o instalador encerrar com falha ou a instalação final não corresponder à versão esperada, o aplicador preserva os arquivos parciais e restaura a cópia anterior e seus registros.

Cancelamento ou falha de encerramento antes da aplicação não inicia o instalador. Um prazo excedido não autoriza matar o processo nem repetir a instalação. Resultado desconhecido preserva as cópias e bloqueia outra atualização automática até a investigação da operação.

Instaladores e recibos ficam em `%LOCALAPPDATA%/Bezoro/AhkManagerElectron/Updates/installers/<version>`. Planos, logs limitados, resultados e cópias ficam em `Updates/operations/<operationId>`. Esses dados permanecem após desinstalar, assim como o perfil e os scripts. Conserve as cópias enquanto uma atualização estiver pendente. Não exclua o histórico durante uma instalação.

## Validação

Os testes Node cobrem isolamento entre canais, persistência da seleção, ordenação SemVer, retorno ao estável, consulta privada, redirecionamentos, hashes, falhas, concorrência, preflights e recuperação por cache. Os testes de publicação simulam a API do GitHub e conferem a origem e a identidade das prévias. O aplicador testa o protocolo e os estados de instalação/restauração com efeitos isolados. A CI instala pacotes reais no Windows descartável, atualiza e faz downgrade, sem abrir o aplicativo. As capturas do renderer cobrem a seleção de canal, a página de atualizações e o histórico sem backend. A sessão pessoal do Windows, UAC e o primeiro login real exigem validação separada.
