# AHK Manager — WinUI 3

Aplicativo nativo para Windows 11 x64, C#/.NET 10 e Windows App SDK estável. Gerencia scripts AutoHotkey **v2**, coleções, metadados, atalhos parcialmente analisados e atividade local. Tema, contraste e cor de destaque seguem o Windows por padrão; Preferências permite escolher claro ou escuro.

Abra **Bezoro.AhkManager.WinUI.exe** na pasta completa da distribuição. Mantenha todos os arquivos juntos: .NET e Windows App SDK estão incluídos. Não exige Visual Studio, SDK, certificado ou instalação administrativa. AutoHotkey v2 é um requisito separado para executar scripts. A distribuição é uma pasta x64, não um EXE único.

Adicionar arquivos apenas cadastra caminhos. Iniciar valida o interpretador e o arquivo. Parar solicita saída normal e confirma o encerramento; OnExit pode recusar. Reiniciar só inicia uma substituição depois da saída comprovada. Não há encerramento forçado automático. Pausa e suspensão são diferentes; funções sem observação comprovável ficam indisponíveis com explicação.

A identidade do processo e a seleção são verificadas novamente após confirmações. Acesso insuficiente, múltiplas instâncias e observação incompleta aparecem como não confirmados. Não repita uma execução incerta: use Verificar pendências. Um Manager anterior aberto impede efeitos concorrentes; feche-o antes de operar pelo novo aplicativo.

Editar usa um editor configurado ou a associação segura **Editar** do Windows. Não usa a associação Abrir de .ahk, que pode executar o arquivo. Argumentos do editor são informados um por linha; {file} representa o caminho, passado como um único argumento. Metadados não reescrevem o script.

Os dados novos ficam em `%LOCALAPPDATA%/Bezoro/AhkManagerWinUI`, com JSON versionado, substituição atômica e cópia de segurança. Configuração inválida abre para recuperação sem sobrescrever a origem. A importação do catálogo anterior pela Ajuda é explícita, verifica seu hash e não altera o arquivo antigo ou seus agendamentos.

O início com Windows é opcional e por usuário. Iniciar o Manager, mostrar sua janela e iniciar cada script são preferências separadas. Scripts administrativos exigem confirmação manual e não iniciam automaticamente. Desabilitar o Manager no logon mantém as preferências dos scripts, mas deixa-as inativas. Fechar pode manter o aplicativo na área de notificação; **Sair do Manager não encerra scripts**.

A análise estática de atalhos é limitada. Não avalia código e não cobre todos os contextos, atalhos dinâmicos ou programas externos. Atalhos manuais são apenas metadados. A atividade registra solicitações e resultados observados, não eventos internos inventados.

Desenvolvimento: .NET SDK **10.0.402**, Windows SDK **26100**. Pacotes oficiais bloqueados: Windows App SDK **2.5.1**, CommunityToolkit.Mvvm **8.4.2**, BuildTools **10.0.26100.9169**. Execute `tools/Build-WinUI.ps1`, depois `tools/Package-WinUI.ps1 -Destination <pasta-nova>`. A solução ativa contém somente os dois projetos WinUI. App/Presentation.Wpf/Tests.WpfUI preservam código legado fora da distribuição.

Ctrl+F pesquisa; F5 atualiza; Tab percorre os controles. Consulte [SAFE-TESTS.md](SAFE-TESTS.md) e [VALIDATION.md](VALIDATION.md).

Para atualizar, extraia a distribuição completa em uma pasta nova. Saia do Manager pela área de notificação antes de substituir ou remover a pasta anterior; os scripts continuam executando. Não sobreponha arquivos de uma instância aberta. Abra o novo executável e confira suas preferências; a entrada de login deve corresponder à nova localização.

Para remover, desative **Iniciar o Manager com Windows** nas Preferências da própria instalação, removendo somente a entrada de login que corresponde a ela. Saia do Manager e exclua apenas sua pasta de distribuição. Scripts e arquivos cadastrados não são apagados. Os dados locais em `%LOCALAPPDATA%/Bezoro/AhkManagerWinUI` permanecem disponíveis para reinstalação; sua remoção é opcional e deve ser feita separadamente pelo usuário.
