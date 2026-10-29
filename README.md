# AHK Manager

Gerenciador de scripts AutoHotkey v2 para Windows 11 x64. A interface usa Electron, React e Fluent UI; o backend .NET controla processos, persistência e recuperação.

[Baixar a versão mais recente](https://github.com/bezoro/ahk-manager/releases/latest)

## Usar o aplicativo

- Cadastre scripts e escolha quais iniciam com o Windows.
- Inicie, reinicie e pare cada script, com privilégios normais ou administrativos.
- Edite no VS Code detectado automaticamente ou configure outro editor e seus argumentos.
- Consulte atalhos, atividade e problemas de observação dos processos.
- Atualize pelo aplicativo, escolha o canal estável ou de testes e use o histórico para voltar a uma versão preservada.

O Manager inicia com Windows e como administrador por padrão. As preferências são independentes e configuráveis. O primeiro uso administrativo solicita a confirmação do Windows. Scripts sem a opção administrativa continuam usando o privilégio padrão do usuário.

Fechar a janela pode manter o aplicativo na bandeja. **Sair mantendo os scripts** preserva os processos. **Parar scripts e sair** solicita saída normal das identidades verificadas e mantém o Manager aberto se não conseguir confirmar o resultado.

Adicionar e analisar arquivos não executa os scripts. AutoHotkey v2 é um requisito separado para iniciá-los. O instalador inclui Electron e o backend autossuficiente; o usuário final não precisa instalar Node ou .NET.

## Atualizações e dados

O aplicativo consulta as Releases públicas ao abrir e a cada 15 minutos, sem exigir login. O canal **Estável**, selecionado por padrão, recebe alterações da `main` que passaram pela CI. Em **Atualizações**, escolha **Testes** para receber prévias de pull requests antes do merge. Os dois canais usam o botão de atualização e permitem retornar pelo histórico. Voltar ao canal estável oferece a versão estável mais recente, mesmo quando ela é anterior à prévia instalada.

O download e a instalação dependem de uma ação do usuário. Perfis, scripts e preferências de inicialização são preservados; uma falha confirmada na instalação restaura a cópia anterior. Versões sem o seletor de canais recebem primeiro uma atualização estável pelo botão existente. Consulte [UPDATES.md](UPDATES.md) para publicação, assinatura, primeira atualização de versões antigas e recuperação.

Os dados operacionais ficam em `%LOCALAPPDATA%/Bezoro/AhkManagerElectron`. O armazenamento Chromium usa `%APPDATA%/Bezoro/AhkManagerElectron/Chromium`. A migração de um perfil anterior exige seleção, prévia e confirmação. A desinstalação preserva perfis e scripts.

## Desenvolver

Leia o [guia de contribuição](CONTRIBUTING.md) para preparar o ambiente, formatar, testar e gerar o instalador. O [mapa da arquitetura](ARCHITECTURE.md) explica as dependências e os fluxos do programa.

| Caminho                               | Responsabilidade                                                     |
| ------------------------------------- | -------------------------------------------------------------------- |
| `Core/`                               | Contratos, catálogo, persistência, processos e integração do Windows |
| `Backend/`                            | Sessão do host, protocolo de comandos e coordenação administrativa   |
| `Presentation.Electron/src/main/`     | Composição do aplicativo e integração com o desktop                  |
| `Presentation.Electron/src/preload/`  | API limitada entre host e interface                                  |
| `Presentation.Electron/src/renderer/` | Páginas, componentes e hooks da interface                            |
| `UpdateHost/`                         | Aplicação de atualizações e restauração da instalação                |
| `Tests.Backend/`                      | Testes do backend, do núcleo e adaptadores de ensaio                 |
| `tools/`                              | Build, distribuição e validação isolada                              |

Use Windows x64, PowerShell 7, Node 24 e o SDK .NET fixado em `global.json`. O build usa os locks de dependências do repositório. Na raiz:

```powershell
$build = Join-Path $env:TEMP ('ahk-manager-' + [guid]::NewGuid().ToString('N'))
./tools/Build-Electron.ps1 -OutputRoot $build
./tools/Package-Electron.ps1 -BuildRoot $build -OutputRoot "${build}-package"
```

A CI Windows verifica formatação, dependências entre camadas, tipos, testes .NET e Node, capturas da interface e instalação, atualização e rollback. Os artefatos incluem instalador, logs e capturas. [SAFE-TESTS.md](SAFE-TESTS.md) descreve o escopo dos ensaios e [VALIDATION.md](VALIDATION.md) conserva os registros de validação.

Os projetos WinUI, WPF e suas ferramentas anteriores permanecem no repositório como implementações históricas, fora da solução e da distribuição ativas. O código compartilhado atual pertence ao `Core/`. A [documentação histórica](docs/history.md) preserva as notas das versões anteriores.
