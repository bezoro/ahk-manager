# Atualizações do AHK Manager

O aplicativo oferece apenas versões publicadas depois de uma execução bem-sucedida da CI em `main`. Cada push aprovado gera `1.2.<run_number>` e uma Release com tag própria. As versões anteriores não são sobrescritas. O aplicativo verifica as versões ao abrir e a cada 15 minutos; baixar e instalar exigem uma ação do usuário.

## Uso

Abra **Atualizações**, confira a versão e pressione **Atualizar**. O download informa progresso. O aplicativo pede confirmação, exige que alterações estejam salvas e operações tenham terminado, fecha o backend normalmente e reabre o Manager depois da troca. Scripts que já estão rodando continuam em execução. A reabertura usa `--foreground`, sem repetir o processamento de login dos scripts.

O histórico reúne Releases compatíveis e instaladores preservados neste computador. **Voltar para esta versão** usa o mesmo processo de verificação e instalação, permitindo downgrade somente após uma escolha explícita. Os dados do perfil e os scripts permanecem atuais; o retorno troca o aplicativo, sem desfazer edições feitas pelo usuário.

Releases de um repositório público podem ser consultadas e baixadas sem autenticação. Esse é o fluxo padrão. Se o repositório estiver privado ou o GitHub limitar as consultas, o acesso avançado aceita opcionalmente um token com acesso a `bezoro/ahk-manager` e permissão **Contents: Read-only**. A credencial fica cifrada por `safeStorage`, que usa a proteção do Windows. O aplicativo recusa armazenamento sem criptografia. O renderer só recebe a indicação de que o acesso foi configurado; não recebe o token salvo. **Remover acesso** apaga a credencial.

## Publicação e integridade

O job de compilação tem acesso de leitura. Um job separado, com `contents: write`, publica apenas depois de todos os testes do Windows passarem em um push para a `main` deste repositório. Pull requests nunca publicam. O job cria uma Release em rascunho, envia os assets e só depois a publica. Uma versão já publicada é conferida, sem substituir seus arquivos.

Os assets são `AHK-Manager-<version>-Setup-x64.exe` e `ahk-manager-release.json`. O manifesto de protocolo 1 contém versão, commit, data, notas, nome, tamanho e hashes SHA256/SHA512 do instalador. O cliente só consulta o repositório fixo, valida o manifesto e reconstrói as URLs da API a partir dos IDs dos assets. Aceita HTTPS, limita tamanho e tempo e retira credenciais nos redirecionamentos para os servidores de assets do GitHub.

O `electron-updater` 6.8.11 cuida do download, cache, progresso e verificação SHA512. O aplicativo verifica SHA256 antes de preservar e entregar o instalador ao aplicador. A instalação padrão não baixa nem instala atualizações ao sair. O aplicador independente não acessa a rede.

Para distribuir versões assinadas, configure os secrets `WINDOWS_CSC_LINK` e `WINDOWS_CSC_KEY_PASSWORD`, e a variável `WINDOWS_PUBLISHER_NAME` no GitHub. O certificado é uma credencial externa e não pertence ao repositório. Com ele configurado, o empacotamento exige assinatura e o atualizador verifica o editor, inclusive ao usar um instalador antigo do cache. Sem certificado configurado, os builds continuam sem assinatura de editor verificado; os hashes detectam corrupção, mas não substituem Authenticode.

## Aplicação e recuperação

O NSIS só aceita atualização de uma instalação por usuário com `UpdateProtocol=1`, caminho registrado correspondente e arquivos com identidade comprovada. A entrada Run e a tarefa administrativa são preservadas porque o executável permanece no mesmo caminho. A primeira migração de versões até 1.1 é manual, pois seus desinstaladores recusam `--updated`.

O aplicador é copiado para fora da instalação e recebe um plano limitado, com hash. Ele comprova PID, criação, usuário, sessão e imagem do processo pai antes de confirmar a preparação. O Manager só autoriza a aplicação depois de concluir as operações pendentes, tentar salvar a posição da janela e observar a saída do backend. O aplicador exige esse recibo vinculado à operação e à sessão, além da saída do Manager com código zero. Uma queda antes dessa confirmação não inicia a instalação. Depois disso, confere os instaladores novamente e preserva a instalação completa com hashes e as duas chaves próprias do Registro. Só então executa NSIS. Se o instalador encerrar com falha ou a instalação final não corresponder à versão esperada, o aplicador preserva os arquivos parciais e restaura a cópia anterior e seus registros.

Cancelamento ou falha de encerramento antes da aplicação não inicia o instalador. Um prazo excedido não autoriza matar o processo nem repetir a instalação. Resultado desconhecido preserva as cópias e bloqueia outra atualização automática até a investigação da operação.

Instaladores e recibos ficam em `%LOCALAPPDATA%/Bezoro/AhkManagerElectron/Updates/installers/<version>`. Planos, logs limitados, resultados e cópias ficam em `Updates/operations/<operationId>`. Esses dados permanecem após desinstalar, assim como o perfil e os scripts. Conserve as cópias enquanto uma atualização estiver pendente. Não exclua o histórico durante uma instalação.

## Validação

Os testes Node cobrem consulta privada, redirecionamentos, hashes, falhas, concorrência, preflights e recuperação por cache. O aplicador testa o protocolo e os estados de instalação/restauração com efeitos isolados. A CI instala dois pacotes reais no Windows descartável, atualiza e faz downgrade, sem abrir o aplicativo. As capturas do renderer cobrem a página de atualizações e o histórico sem backend. A sessão pessoal do Windows, UAC e o primeiro login real exigem validação separada.
