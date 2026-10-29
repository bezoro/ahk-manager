# Contribuir

Comece por [ARCHITECTURE.md](ARCHITECTURE.md) para localizar a implementação ativa. Alterações no produto atual ficam em `Core/`, `Backend/`, `UpdateHost/` e `Presentation.Electron/`. Os projetos WinUI e WPF são históricos e não validam a interface Electron.

## Ambiente

Clone o repositório com seu histórico e tags. Os builds calculam a versão a partir do Git. Se as tags não estiverem disponíveis, execute `git fetch --tags`; para um build local sem histórico, informe `-ReleaseVersion` explicitamente a `tools/Build-Electron.ps1`.

Use Windows x64 e PowerShell 7 para compilar, empacotar e executar os testes nativos. A CI usa Node 24.22.0 e o SDK .NET fixado em `global.json`. Instale as dependências pelos locks existentes. AutoHotkey v2 é necessário para os testes que descobrem e verificam o interpretador; a CI prepara uma cópia portátil oficial apenas para leitura.

Na raiz do repositório, execute `dotnet tool restore --configfile NuGet.Config` para instalar a versão fixada do CSharpier, usada na formatação C#.

Os exemplos abaixo partem da raiz do repositório. Cada saída precisa ser uma pasta absoluta ainda inexistente. Assim, uma nova execução preserva as evidências da anterior.

```powershell
$work = Join-Path $env:TEMP ('ahk-manager-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
```

## Frontend

```powershell
Push-Location Presentation.Electron
npm.cmd ci --ignore-scripts
npm.cmd run format:check
npm.cmd run architecture:check
npm.cmd run typecheck
npm.cmd test -- --out "$work/node-tests"
npm.cmd run build -- --out "$work/frontend"
Pop-Location
```

`npm.cmd run format` aplica o Prettier conforme `.prettierrc.json` e `.prettierignore`. `.editorconfig` define a indentação dos demais arquivos. `architecture:check` verifica as dependências entre camadas e o isolamento das fontes legadas.

Os testes Node usam adaptadores e perfis temporários. Em Linux e macOS, o ensaio de identidade de arquivos do Windows fica explicitamente ignorado. A CI Windows executa esse ensaio. Três verificações de integração aceitam snapshots produzidos pelos testes .NET; sem essas entradas elas ficam ignoradas. Para executá-las, rode primeiro `--backend` conforme abaixo e defina as entradas antes de `npm.cmd test`:

```powershell
$env:AHK_HEADER_SNAPSHOT_INPUT = "$work/backend/header-description-snapshot.json"
$env:AHK_FOREGROUND_ARGUMENTS_INPUT = "$work/backend/foreground-host-arguments.txt"
$env:AHK_STARTUP_RECOVERY_INPUT = "$work/backend/startup-recovery-snapshot.json"
```

## Commits e versões

Use Conventional Commits. O tipo identifica o efeito da mudança; o escopo é opcional.

```text
fix(startup): preserve the configured login preference
feat(updates): add a release history filter
refactor(core): separate process observation from catalog operations
build: update the Windows packaging tools
```

Os tipos aceitos são `feat`, `fix`, `perf`, `refactor`, `docs`, `ci`, `build`, `style`, `test`, `chore` e `revert`. Descreva o motivo no corpo quando o título não explicar a decisão. Uma mudança incompatível usa `!`, como `feat!: change the profile schema`, ou um rodapé `BREAKING CHANGE:` que explique a incompatibilidade e a migração necessária.

A CI valida somente os novos commits do PR ou push. As mensagens antigas permanecem no histórico. Versões estáveis seguem Semantic Versioning: `feat` incrementa a minor, mudanças incompatíveis incrementam a major e os demais tipos incrementam o patch. O nível mais alto entre os commits prevalece.

Em `Presentation.Electron`:

```powershell
npm.cmd run commits:check
npm.cmd run test:release
npm.cmd run release:identity
```

`release:identity` faz somente leitura do Git e mostra a versão prevista. `node scripts/compute-release.cjs --json` produz os mesmos dados em JSON para ferramentas. O script de build local também usa esse cálculo; `package.json.version` não determina a versão oficial. O job de publicação roda apenas na `main`, após a aprovação de toda a CI Windows. Consulte [UPDATES.md](UPDATES.md) para reexecuções, imutabilidade e assinatura.

## Backend e Core

```powershell
dotnet restore Tests.Backend/Tests.Backend.csproj --locked-mode --configfile NuGet.Config -r win-x64
dotnet publish Tests.Backend/Tests.Backend.csproj -c Release -r win-x64 --self-contained true --no-restore -o "$work/test-bin"
& "$work/test-bin/Tests.Backend.exe" --backend --root "$work/backend"
& "$work/test-bin/Tests.Backend.exe" --core --root "$work/core"
& "$work/test-bin/Tests.Backend.exe" --corrections --root "$work/corrections"
& "$work/test-bin/Tests.Backend.exe" --startup-inspection --root "$work/startup-inspection"
```

Esses seletores usam perfis temporários e adaptadores de efeitos. `--startup-inspection` consulta o Windows por WMI, somente para leitura. `--core` e `--corrections` também precisam do AutoHotkey v2 disponível para descoberta. Os testes de Core ficam em `Tests.Backend/Core`; novos adaptadores compartilhados ficam em `Tests.Backend/Fixtures`.

Após restaurar a ferramenta, em `Presentation.Electron`, execute `npm.cmd run format:dotnet:check -- Core Backend Tests.Backend` para conferir a formatação com CSharpier. `npm.cmd run format:dotnet -- Core Backend Tests.Backend` aplica os ajustes. Sem a lista de projetos, esses comandos incluem também o UpdateHost. A formatação não exige restore dos projetos.

O seletor `--ahk-owned` inicia scripts temporários reais e tem preparação adicional. Ele não faz parte da CI comum. Consulte [SAFE-TESTS.md](SAFE-TESTS.md) antes de usá-lo. Não use o perfil pessoal como raiz dos ensaios.

Para alterações no aplicador de atualizações:

```powershell
dotnet restore UpdateHost/AhkManager.UpdateHost.csproj --locked-mode --configfile NuGet.Config -r win-x64
dotnet publish UpdateHost/AhkManager.UpdateHost.csproj -c Release -r win-x64 --self-contained true --no-restore -o "$work/update-host"
& "$work/update-host/AhkManager.UpdateHost.exe" --self-test --root "$work/update-host-tests"
```

## Interface e pacote Windows

Para abrir a interface de produção com os dados sintéticos do harness, use o bundle gerado acima:

```powershell
Push-Location Presentation.Electron
node.exe node_modules/electron/install.js
Pop-Location
./tools/Test-ElectronPresentation.ps1 -FrontendRoot "$work/frontend" -HarnessRoot "$work/ui-harness" -OutputRoot "$work/ui-evidence"
```

O ensaio abre uma janela própria, exercita o renderer e salva capturas e resultados. Não conecta ao backend nem executa scripts. Ele cobre temas, largura reduzida e zoom; não comprova UAC, login real ou composição Mica.

Para gerar a distribuição completa:

```powershell
./tools/Build-Electron.ps1 -OutputRoot "$work/build"
./tools/Package-Electron.ps1 -BuildRoot "$work/build" -OutputRoot "$work/package"
```

O build baixa o runtime Electron pelo script oficial, verifica tipos, executa os testes Node e publica Backend e UpdateHost. O empacotamento cria o instalador NSIS e o inventário de arquivos próprios. A opção `-DirectoryOnly` de `Package-Electron.ps1` gera apenas a pasta empacotada.

`tools/Test-UpdateInstallation.ps1` instala, atualiza e retorna entre dois pacotes reais. Execute esse ensaio somente em Windows descartável, sem instalação ou perfil anterior, conforme [SAFE-TESTS.md](SAFE-TESTS.md). A CI faz essa preparação. Criar o instalador localmente não publica uma Release; a política de publicação está em [UPDATES.md](UPDATES.md).

## Preparar uma alteração

Mantenha regras de processo e persistência no Core, validação do protocolo no Backend, integração nativa no processo principal e apresentação no renderer. O preload expõe métodos específicos; não acrescente execução arbitrária, caminhos de módulo ou acesso direto ao Node.

Ao mudar contratos, confira os tipos em `Core/Models`, `Backend/Protocol.cs` e `Presentation.Electron/src/shared`, além dos respectivos contextos JSON e validadores. Preserve os nomes de perfil, Registro e mutex existentes. Mudanças de formato precisam exercitar perfis anteriores e dados inválidos.

Inclua a reprodução do problema e os resultados das verificações relevantes no pull request. Para alterações visuais, confira as capturas do harness. A CI do Windows executa as suítes completas e preserva logs e artefatos mesmo quando falha. [VALIDATION.md](VALIDATION.md) registra os limites dos ensaios nativos existentes.
