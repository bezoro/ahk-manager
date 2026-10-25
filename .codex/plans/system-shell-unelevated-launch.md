# Caminho do Explorer no lançamento com token médio

**READY — breve pronto para revisão do plano; implementação e validação real pendentes.**

## Resultado e evidência

Aceitar múltiplos hard links no Explorer legítimo, exclusivamente para obter o token do shell. Scripts e intérpretes AHK continuam sujeitos à normalização estrita existente.

As fontes atuais foram lidas seletivamente e comparadas às cópias completas indicadas por `E/final-engine-source-map-v5.json`, SHA256 `34f32c9c432843ddfaa50ee69bd52355dff0e26ab42733cc7fef316c50815462`:

| Fonte | SHA256 / prova |
| --- | --- |
| `Presentation.WinUI/Infrastructure/UnelevatedProcessLauncher.cs` | `2412b860bd967e080a8ff69c73cf6dae27c0714fd16413b299ffce1b16c5b66e`; `GetShell:36` usa `ScriptPaths.Canonical` antes das guardas de token. |
| `Presentation.WinUI/Infrastructure/NativeProcessIdentity.cs` | `634a286246dec0de83f3ad1ef34870560dcb5b704a4a50f9246e58d2434d599a`; `Canonical:28–38` rejeita `Links != 1`; `TryRead:107–118` lê imagem nativa sem essa guarda de arquivo. |
| `Tests.Backend/CorrectionChecks.cs` | `570b9743ec1e646effdb64c64ce72d30a3449f6529155da5df181ebce44cef00`; não cobre o caminho do shell. |

`E/system-explorer-link-metadata-v1.json`, SHA256 `0995fc55f8da23c2328bd8f21b9fc7a9c165e9c660a64db4b8bf38322eeeb886`, prova arquivo regular com dois links somente por metadados. A guarda atual necessariamente rejeita esse Explorer antes de `CanImpersonate`.

O probe normal já executado uma vez comprovou imagem, criação, SID/sessão, Medium não elevado, liveness/revalidação e abertura QUERY|DUPLICATE do shell: `E/shell-token-probe-logs-v1/readonly-shell-v1.stdout.log`, SHA256 `a69c7992b3d6895f26746cd8485a82fc9fcfb34d4696bbcf36d28b5625991007`. É evidência de um instante, não substitui revalidação.

A batch HIGH anterior terminou com exit 1 sem motivo preciso da guarda. `E/engine-published-acceptance-v6/HighOwned.cs`, SHA256 `ee23ca1180257584ec2029d87c5a52bc60106909d8b8240f0656d90f1f7f7cc5`, verifica capacidade na linha 118, cria intenção na 119 e só despacha nas 127/132. A ausência dos marcadores de intenção/filhos, retida pelo coordenador, confirma que não houve lançamento AHK. A causa atual está demonstrada pela fonte; a atribuição histórica exclusiva permanece limitada pelo log.

## Correção e propriedade

O implementador atual será o único proprietário de:

- `UnelevatedProcessLauncher.cs`: pequeno helper interno específico do caminho esperado do Explorer e sua chamada em `GetShell`.
- `CorrectionChecks.cs`: regressão de caminho do shell e negativos correspondentes.

O caminho de produção deriva exclusivamente de `Environment.SpecialFolder.Windows` acrescida de `explorer.exe`. Preservar caminho local absoluto/normalizado, arquivo existente regular e recusa de reparse point no arquivo e ancestrais. Aceitar mais de um hard link somente nessa finalidade. Uma entrada interna de diretório pode servir às fixtures; o fluxo de produção usa apenas o diretório do Windows obtido do sistema. Não criar normalizador geral, opção pública `allowHardlinks` ou nova configuração.

Manter intactos `ScriptPaths.Canonical`, `NativeProcessIdentity`, `CanImpersonate`, `CompatibleShell`, HWND/PID/handle/criação, imagem nativa igual ao caminho esperado, mesmo SID/sessão, token Medium não elevado, QUERY|DUPLICATE, lease de leitura e revalidação antes do uso. Preservar guardas AHK de `Launch:73`, prova do filho e recibo de criação. Nenhum arquivo Windows, metadata, privilégio ou política será alterado.

**Questão de API separada:** [PrivilegeCheck](https://learn.microsoft.com/en-us/windows/win32/api/securitybaseapi/nf-securitybaseapi-privilegecheck) documenta token de thread impersonando cliente; o código passa o token primário do processo. Nesta máquina, o probe normal observou sucesso da API com resultado false, coerente com SeImpersonate ausente/desabilitado nesse caller. Não há falha nativa observada dessa chamada nem prova sobre os privilégios do caller HIGH encerrado. Não alterar essa estratégia neste corretivo. Se surgir falha independente, a alternativa somente de leitura [GetTokenInformation/TokenPrivileges](https://learn.microsoft.com/en-us/windows/win32/api/securitybaseapi/nf-securitybaseapi-gettokeninformation) deve ser avaliada separadamente com buffer limitado e negativos, sem habilitar privilégios. [CreateProcessWithTokenW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-createprocesswithtokenw) continua exigindo SeImpersonate e os direitos do token primário de destino. Nunca fazer fallback HIGH.

## Ordem, checks e conclusão

1. **Isolar o FAIL antes da correção da regra.** Expor somente a expressão atual de `GetShell:36` por extração interna que ainda delega à mesma `Canonical`, sem mudança comportamental. Registrar essa instrumentação e seus bytes como baseline instrumentado, sem chamá-lo de pristine. Executar uma regressão focal de normalização com o caminho fixo real do Explorer: com dois links, a versão instrumentada lança IOException e falha na aceitação esperada. Não consultar capacidade global de `UnavailableReason` como oráculo do caminho, não alterar seams de privilégios, não lançar filho/UAC e não contar erro de compilação como FAIL.
2. **Aplicar a regra específica e obter PASS do mesmo caso.** Cobrir também hard links em arquivos próprios representando o caminho esperado; reparse no arquivo/ancestral; caminho ausente/incorreto; e recusa estrita de scripts/intérpretes próprios com hard links por `Canonical`. Reutilizar os negativos existentes de `CompatibleShell` para SID/sessão/imagem/token errado e shell substituído. Não modificar arquivos do Windows para preparar casos.
3. **Revisão fresca independente e checks afetados** de correções/elevação. Verificar que o helper específico está ligado ao `GetShell` real e que a normalização compartilhada permaneceu idêntica. Resultados anteriores continuam válidos somente para entradas inalteradas; não validam o novo launcher.
4. **Coordenador: uma batch HIGH real, após gates aprovados**, em raiz nova `high-owned-run-v2`, usando candidato publicado ligado às novas fontes. Preservar integralmente `high-owned-run-v1`, recibos/marcadores e perfil normal privado. Confirmar lançamentos médio e alto, tokens/identidades reais e encerramento gracioso das fixtures dentro dos budgets existentes. Accepted/intenção não comprovam criação; ausência de recibo após dispatch é Unknown, sem retry cego ou limpeza por PID/nome.

Concluir somente com FAIL→PASS causal, negativos preservados, revisão independente e batch real confirmada. Refazer publish/package vinculados às fontes finais; aplicação da instalação mantém seus gates existentes. Falha distinta de token/direitos/ambiente exige evidência e decisão específica, sem relaxar guardas para obter PASS. Nenhuma alteração de programas alheios, startup/perfis, instalação atual ou agenda de desligamento; desligamento permanece cancelado.