# AHK Manager — Windows 11 x64

Extraia a pasta inteira e abra **Bezoro.AhkManager.WinUI.exe**. Mantenha todos os arquivos juntos. Os runtimes estão incluídos; não é necessário instalar Visual Studio, SDK, certificado ou habilitar Modo de Desenvolvedor.

Para executar scripts, instale AutoHotkey **v2** ou escolha seu executável nas Preferências. Adicionar arquivos não os executa. Editar usa um editor seguro, não a associação Abrir de .ahk.

Tema e cor de destaque seguem o Windows. Ctrl+F pesquisa; F5 atualiza. Fechar pode manter o Manager na área de notificação; sair mantém os scripts rodando. Parar solicita saída normal e pode ser recusado; não há encerramento forçado automático.

Início com Windows é opcional e por usuário. Um Manager anterior aberto impede efeitos concorrentes. Importação pela Ajuda é explícita e não altera o catálogo anterior. Dados novos: %LOCALAPPDATA%/Bezoro/AhkManagerWinUI. Guarde uma cópia dessa pasta e da distribuição anterior. Não apague registros pendentes para repetir execuções incertas: use Verificar pendências. Atalhos e conflitos têm análise parcial.

payload-manifest.json identifica o conteúdo desta distribuição.

Para atualizar, escolha **Sair** na área de notificação e substitua a pasta da distribuição por uma cópia completa da nova versão. Sair do Manager preserva os scripts em execução. Guarde a versão anterior e os dados locais antes de atualizar.

Para remover, desative **Iniciar o Manager com o Windows** nas Preferências e salve. Essa ação remove somente a entrada de login que corresponde a este Manager; entradas de outros aplicativos não são alteradas. Depois escolha **Sair** e apague a pasta da distribuição. Os scripts continuam em execução e os dados em `%LOCALAPPDATA%/Bezoro/AhkManagerWinUI` são preservados; remova esses dados separadamente somente se não quiser mais o catálogo, histórico e preferências.
