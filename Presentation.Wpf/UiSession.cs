using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace Bezoro.AhkManager.Presentation;

public interface IUserInteraction
{
    string? PickScript();
    string? PickInterpreter();
    Task<bool> Confirm(string title, string message);
    Task<bool> Confirm(string title, string message, CancellationToken token) => Confirm(title, message).WaitAsync(token);
    void CopyPath(string path);
    void EditFile(string path);
}

public sealed class DesktopInteraction : IUserInteraction
{
    public Window? Owner { get; set; }
    public string? PickScript() => Pick("Scripts AutoHotkey (*.ahk)|*.ahk", "Cadastrar script sem executá-lo");
    public string? PickInterpreter() => Pick("Interpretador AutoHotkey (*.exe)|AutoHotkey*.exe", "Escolher interpretador instalado");
    string? Pick(string filter, string title)
    {
        var dialog = new OpenFileDialog { Filter = filter, Title = title, CheckFileExists = true };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }
    public Task<bool> Confirm(string title, string message)
    {
        var dialog = new ConfirmationWindow(title, message) { Owner = Owner };
        return Task.FromResult(dialog.ShowDialog() == true);
    }
    public Task<bool> Confirm(string title, string message, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var dialog = new ConfirmationWindow(title, message) { Owner = Owner };
        using var registration = token.Register(() => dialog.Dispatcher.BeginInvoke(new Action(() => { if (dialog.IsVisible) dialog.Close(); })));
        return Task.FromResult(dialog.ShowDialog() == true && !token.IsCancellationRequested);
    }
    public void CopyPath(string path) => Clipboard.SetText(path);
    public void EditFile(string path)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe")) { UseShellExecute = false };
        start.ArgumentList.Add(Paths.Normalize(path));
        using var editor = Process.Start(start);
    }
}

public sealed partial class UiSession
{
    readonly CatalogStore store;
    readonly IProcessAccess processes;
    readonly ActionController controller;
    readonly IUserInteraction interaction;
    readonly Func<Task<DiscoveryResult>> discover;
    readonly OperationLane lane = new();
    readonly CancellationTokenSource shutdown = new();
    readonly Supervision supervision;
    readonly RequestLedger authority;
    readonly IManualElevationDispatcher? elevation;
    readonly BootContextService bootContext;
    readonly BrushTaskControls? brush;
    Guid? checkedRequestNonce;
    string checkedContextKind = "";
    string requestProofNotice = "";
    public RequestLedger Authority => authority;
    public bool ShuttingDown { get; private set; }
    public Task WhenIdle => lane.Idle;
    public bool OwnsEffects => supervision.Owner.Held && supervision.Owner.Trusted;
    public bool NeedsSchemaConversion => store.LoadedSchema is 1 or 2 or 3 or 4 || authority.NeedsUpgrade || catalog.Authority?.Version == 1;
    public bool FixtureMode => supervision.Owner.Fixture;
    public Catalog CurrentCatalog => catalog.Copy();
    public string IntentState(ScriptEntry entry) => IsBrush(entry) ? brush!.Notice : supervision.Status(catalog, entry);
    public event Action? ExitRequested;
    Catalog catalog;
    public event Action? Changed;
    public event Action<Guid>? DescriptionSaved;
    public Snapshot Snapshot { get; private set; } = new([], false, "Consulta ainda não realizada.");
    public DiscoveryResult Discovery { get; private set; } = new([], [], []);
    public Guid? SelectedId { get; private set; }
    public string Search { get; private set; } = "";
    public bool Busy { get; private set; }
    public bool ReadOnly => store.ReadOnly;
    public string Status { get; private set; }
    public bool Error { get; private set; }
    public bool ObservationStale { get; private set; }
    public IReadOnlyList<ScriptEntry> Entries => catalog.Scripts;
    public int DisplayCount => catalog.Scripts.Count(DisplayEntry);
    static bool DisplayEntry(ScriptEntry entry) => entry.ManagementMode != ManagementMode.Retired;
    public List<ScriptEntry> Visible => catalog.Scripts.Where(e => DisplayEntry(e) && Paths.Matches(e, Search)).ToList();
    public ScriptEntry? Selected => catalog.Scripts.FirstOrDefault(e => e.Id == SelectedId && DisplayEntry(e) && Paths.Matches(e, Search));
    public UiSession(CatalogStore store, IProcessAccess processes, IRecycler recycler, IUserInteraction interaction, Func<Task<DiscoveryResult>> discover, IEffectOwner owner, IClock clock, RequestLedger? authority = null, IManualElevationDispatcher? elevation = null, BootContextService? bootContext = null, BrushTaskControls? brush = null)
    {
        if (owner.Fixture && (processes is WindowsProcesses || recycler is WindowsRecycler || interaction is DesktopInteraction || elevation is WindowsElevationDispatcher)) throw new ArgumentException("Sessão de demonstração recusa adapters reais antes de qualquer consulta ou efeito.");
        if (authority is not null && authority.Fixture != owner.Fixture || elevation is not null && elevation.Fixture != owner.Fixture) throw new ArgumentException("Owner, autoridade e dispatcher precisam do mesmo modo real ou demonstração.");
        this.store = store; this.processes = processes; controller = new(processes, recycler, EffectAuthority); this.interaction = interaction; this.discover = discover;
        this.authority = authority ?? (owner.Fixture ? RequestLedger.OpenFixture(Path.Combine(store.DirectoryPath, "fixture-authority"), owner, store.FilePath, store.FilePath + ".bak") : throw new ArgumentException("Autoridade real explícita necessária."));
        if (brush is not null && brush.Fixture != owner.Fixture) throw new ArgumentException("Brush adapter e owner precisam do mesmo modo.");
        this.brush = brush;
        this.elevation = elevation;
        this.bootContext = bootContext ?? BootContextService.Create(owner.Fixture);
        if (this.bootContext.Fixture != owner.Fixture) throw new ArgumentException("Contexto e sessão precisam do mesmo modo.");
        if (processes is WindowsProcesses native) native.StartAuthority = NormalLaunchAuthority;
        supervision = new(store, owner, clock, this.authority);
        catalog = store.Load();
        if (catalog.Authority is null) catalog.Authority = this.authority.Binding; SelectedId = catalog.Scripts.FirstOrDefault(DisplayEntry)?.Id;
        Status = !this.authority.Ready ? this.authority.Notice : this.authority.HasPending ? this.authority.PendingReason : string.IsNullOrEmpty(store.Notice) ? "Atualize a descoberta ou cadastre um arquivo .ahk para começar." : store.Notice;
        Error = store.ReadOnly || !this.authority.Ready;
    }
    public void Select(Guid? id) { if (Busy) return; SelectedId = catalog.Scripts.Any(e => e.Id == id && !DisplayEntry(e)) ? null : id; Notify(); }
    public void SetSearch(string text) { if (Busy) return; Search = text; Notify(); }
    void Notify() => Changed?.Invoke();
    bool Begin(string message)
    {
        if (ShuttingDown || !lane.TryEnter()) return false;
        Busy = true; Status = message; Error = false; Notify(); return true;
    }
    void Finish() { Busy = false; lane.Exit(); Notify(); }
    public async Task<bool> PrepareExit(TimeSpan timeout)
    {
        ShuttingDown = true; shutdown.Cancel(); Notify();
        if (await Task.WhenAny(lane.Idle, Task.Delay(timeout)) != lane.Idle) { Status = "Saída pendente: aguarde a operação em andamento. Scripts existentes permanecem vivos."; Notify(); return false; }
        return true;
    }
    public void RequestExit() => ExitRequested?.Invoke();
    void Fail(Exception exception)
    {
        Error = true;
        Status = exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException
            ? "Não foi possível concluir. " + exception.Message
            : "Erro inesperado na operação. O resultado não pôde ser confirmado; atualize para conferir o estado.";
    }
    void MarkObservationStale(string note)
    {
        ObservationStale = true;
        Snapshot = Snapshot with { Complete = false, Note = note };
    }
    public bool CanClose()
    {
        if (!Busy) return true;
        Status = "Aguarde a ação terminar antes de fechar."; Notify(); return false;
    }
    public Result Availability(ScriptAction action) => Selected is { } entry ? AvailabilityFor(entry, action) : Result.Fail("Selecione um script.");
    Result AvailabilityFor(ScriptEntry entry, ScriptAction action)
    {
        if (!OwnsEffects || ShuttingDown) return Result.Fail("Outro gerenciador possui os efeitos, ou a saída está em andamento.");
        if (ReadOnly || NeedsSchemaConversion) return Result.Fail("Catálogo somente leitura ou antigo: efeitos bloqueados.");
        var authorityGate = authority.Verify(catalog.Authority); if (!authorityGate.Success) return authorityGate;
        if (IsBrush(entry) && action is ScriptAction.Start or ScriptAction.Stop or ScriptAction.Restart)
        {
            var primary = BrushGate(); if (!primary.Success) return primary;
            return brush!.Available(entry, BrushAction(action), Snapshot);
        }
        if (entry.ManagementMode is ManagementMode.ExternalPending or ManagementMode.Retired) return Result.Fail(IntentState(entry));
        if (action is ScriptAction.Start or ScriptAction.Restart)
        {
            var trusted = ManualPrimary(); if (!trusted.Success) return trusted;
            if (authority.HasPending) return Result.Fail(authority.PendingReason);
            if (entry.RunAsAdministrator && action == ScriptAction.Start && elevation is null) return Result.Fail("Dispatcher manual indisponível; nenhum início normal alternativo.");
        }
        if (authority.Find(entry.Path) is not null && action is ScriptAction.Stop or ScriptAction.Recycle) return Result.Fail("Pedido deste script não confirmado; verifique a identidade correlacionada antes de interromper ou remover.");
        var policy = Policy.Check(entry, Snapshot, action, File.Exists(entry.Path));
        if (!policy.Success) return policy;
        if (ManagedStart(entry, action))
        {
            var gate = supervision.Gate(catalog, Snapshot, ShuttingDown, true);
            if (!gate.Success) return gate;
            return supervision.ReserveStart(CopyCatalog(), entry);
        }
        return policy;
    }
    static bool ManagedStart(ScriptEntry entry, ScriptAction action) => entry.ManagementMode == ManagementMode.Normal && action is ScriptAction.Start or ScriptAction.Restart;
    public void ReportTrayFailure()
    {
        Status = "Ícone da bandeja indisponível. Janela mantida aberta; nova tentativa no próximo ciclo."; Error = true; Notify();
    }
    public string State(ScriptEntry entry)
    {
        if (authority.Find(entry.Path) is not null || ObservationStale || Snapshot.Unknown) return "Não confirmado";
        int count = Snapshot.For(entry.Path).Count;
        return count == 0 ? "Parado" : count == 1 ? "Rodando" : $"Rodando · {count} instâncias; alvo ambíguo";
    }
    public string PrivilegeState(ScriptEntry entry)
    {
        var active = Snapshot.For(entry.Path);
        if (ObservationStale || Snapshot.Unknown || active.Count != 1) return "Não confirmado";
        return active[0].Elevated switch { false => "Normal", true => "Administrador", null => "Não confirmado" };
    }
    public string ExternalState(ScriptEntry entry)
    {
        var sources = Discovery.Sources.Where(s => Paths.Equal(s.Path, entry.Path)).ToList();
        return sources.Count == 0 ? "Não identificado" : sources.Any(s => s.Elevated) ? "Tarefa elevada" : "Configurado";
    }
    public string SupervisorWarning(ScriptEntry entry)
    {
        if (Snapshot.SupervisorPath is { } supervisor && Paths.ChildOf(entry.Path, supervisor) && Snapshot.Processes.Any(p => Paths.Equal(p.Script, supervisor)))
            return "O supervisor está ativo e pode relançar este filho em até 60 segundos. Início e reinício pelo gerenciador ficam bloqueados.";
        return Paths.Equal(entry.Path, Snapshot.SupervisorPath) ? "Encerrar o supervisor deixa os filhos vivos." : "";
    }
    public string Confirmation(ScriptEntry entry, ScriptAction action)
    {
        string text = action == ScriptAction.Recycle
            ? $"Enviar {entry.Name} à lixeira?\n{entry.Path}\n\nO registro e as fontes externas de inicialização serão mantidos."
            : $"{(action == ScriptAction.Stop ? "Encerrar" : "Reiniciar")} a instância verificável de {entry.Name}? O script será interrompido.\n{entry.Path}";
        var warning = SupervisorWarning(entry); return string.IsNullOrEmpty(warning) ? text : text + "\n\n" + warning;
    }
    public async Task Refresh()
    {
        if (!Begin("Consultando scripts, fontes externas e processos…")) return;
        try
        {
            var found = await discover(); var observed = await processes.Snapshot(CancellationToken.None);
            var next = CopyCatalog();
            foreach (var file in found.Paths)
                if (!next.Scripts.Any(e => Paths.Equal(e.Path, file))) next.Scripts.Add(new() { Path = file, Description = Bezoro.AhkManager.Discovery.Infer(file), Interpreter = Bezoro.AhkManager.Discovery.DefaultInterpreter() });
            if (!ReadOnly && !NeedsSchemaConversion) { EnsurePrimaryCurrent(); store.Save(next); }
            catalog = next; Discovery = found; Snapshot = observed; ObservationStale = false;
            brush?.Refresh();
            Status = ReadOnly || NeedsSchemaConversion ? store.Notice : $"Atualizado · {DisplayCount} scripts. Descrições inferidas precisam de conferência.";
            Error = ReadOnly;
        }
        catch (Exception ex) { Fail(ex); MarkObservationStale("Atualização não concluída. Os processos exibidos pertencem à observação anterior e não confirmam o estado atual."); }
        finally { Finish(); }
    }
    Catalog CopyCatalog() => catalog.Copy();
    void EnsurePrimaryCurrent()
    {
        if (store.LoadedSchema == 5 && !store.VerifyPrimary(catalog)) throw new IOException("Primário alterado ou inválido. Reabra para consulta; nenhum efeito autorizado.");
    }
    void Mutate(Guid id, Action<ScriptEntry> change)
    {
        EnsurePrimaryCurrent();
        var next = CopyCatalog(); change(next.Scripts.Single(e => e.Id == id)); store.Save(next); catalog = next;
        Status = "Metadados salvos. O script e o início externo permanecem intactos.";
    }
    public void SaveDescription(string text) => Metadata(entry =>
    {
        Mutate(entry.Id, next => { next.Description = text; next.DescriptionSource = "Informada"; });
        DescriptionSaved?.Invoke(entry.Id);
    });
    public void SetRunAsAdministrator(bool value)
    {
        if (!OwnsEffects || ShuttingDown || !authority.Verify(catalog.Authority).Success) { Status = "Preferência preservada: owner indisponível ou saída em andamento."; Error = true; Notify(); return; }
        Metadata(entry => Mutate(entry.Id, next => next.RunAsAdministrator = value));
    }
    public void SetBlocked(bool value) => Metadata(entry => Mutate(entry.Id, next => next.LaunchBlocked = value));
    void Metadata(Action<ScriptEntry> change)
    {
        if (Busy || ReadOnly || NeedsSchemaConversion || Selected is not { } entry || !Begin("Salvando metadados…")) return;
        try { change(entry.Copy()); } catch (Exception ex) { Fail(ex); } finally { Finish(); }
    }
    public void Register()
    {
        if (ReadOnly || NeedsSchemaConversion || !Begin("Escolha um arquivo .ahk…")) return;
        try
        {
            var picked = interaction.PickScript();
            if (picked is null) { Status = "Cadastro cancelado. Nenhuma alteração feita."; return; }
            var path = Paths.Normalize(picked); var existing = catalog.Scripts.FirstOrDefault(e => Paths.Equal(e.Path, path));
            if (existing is not null) { Search = ""; SelectedId = DisplayEntry(existing) ? existing.Id : null; Status = "Este arquivo já está cadastrado."; return; }
            var entry = new ScriptEntry { Path = path, Description = Bezoro.AhkManager.Discovery.Infer(path), Interpreter = Bezoro.AhkManager.Discovery.DefaultInterpreter() };
            EnsurePrimaryCurrent();
            var next = CopyCatalog(); next.Scripts.Add(entry); store.Save(next); catalog = next; Search = ""; SelectedId = entry.Id;
            Status = "Script cadastrado. Nenhum processo iniciado.";
        }
        catch (Exception ex) { Fail(ex); } finally { Finish(); }
    }
    public void ChooseInterpreter() => Metadata(entry =>
    {
        var file = interaction.PickInterpreter();
        if (file is null) { Status = "Escolha cancelada."; return; }
        if (!Commands.IsInterpreter(file)) { Error = true; Status = "Escolha um executável AutoHotkey."; return; }
        Mutate(entry.Id, next => next.Interpreter = file);
    });
    public void CopyPath()
    {
        if (Busy || Selected is not { } entry) return;
        try { interaction.CopyPath(entry.Path); Status = "Caminho copiado."; Error = false; } catch (Exception ex) { Fail(ex); } Notify();
    }
    public void Edit()
    {
        if (Selected is not { } current || !Begin("Abrindo editor…")) return;
        var entry = current.Copy();
        try
        {
            if (!File.Exists(entry.Path)) throw new IOException("Arquivo não encontrado.");
            interaction.EditFile(Paths.Normalize(entry.Path)); Status = "Arquivo aberto no Bloco de Notas. Salvar no editor altera o script.";
        }
        catch (Exception ex) { Fail(ex); } finally { Finish(); }
    }
    public async Task ConvertSchema()
    {
        if (!NeedsSchemaConversion || ReadOnly || !Begin("Confirmando conversão de metadados.")) return;
        try
        {
            if (!await interaction.Confirm("Converter catálogo", "Converter o catálogo para schema 5 e vinculá-lo à autoridade de pedidos deste usuário? Metadados, preferências e intenções existentes serão preservados. O schema 1 continua sem intenção automática inferida. Nenhum processo será iniciado pela conversão.")) { Status = "Conversão cancelada."; return; }
            if (ShuttingDown || !NeedsSchemaConversion || !store.VerifyLoadedPrimary()) { Status = "Conversão recusada: primário alterado, inválido ou encerramento iniciado. Arquivos preservados."; Error = true; return; }
            var upgraded = authority.Upgrade(catalog.Authority, () => !ShuttingDown && OwnsEffects && store.VerifyLoadedPrimary());
            if (!upgraded.Success) { Status = upgraded.Message; Error = true; return; }
            if (ShuttingDown || !OwnsEffects || !store.VerifyLoadedPrimary()) { Status = "Vínculo não salvo: owner ou primário mudou durante a conversão da autoridade. Arquivos preservados; efeitos continuam bloqueados."; Error = true; return; }
            var binding = authority.Binding; var ready = authority.Verify(binding);
            if (!ready.Success) { Status = ready.Message; Error = true; return; }
            var converted = CopyCatalog(); converted.Authority = binding; store.Save(converted); catalog = converted; Status = "Schema 5 e vínculo persistidos. Nenhum processo iniciado.";
        }
        catch (Exception ex) { Fail(ex); } finally { Finish(); }
    }
    public Task SetIntent(bool enabled) { if (Selected is { } entry) SetStartWithWindows(entry.Id, enabled); return Task.CompletedTask; }
    public void SetStartWithWindows(Guid id, bool? value)
    {
        if (catalog.Scripts.SingleOrDefault(e => e.Id == id) is { } taskEntry && IsBrush(taskEntry)) { if (value is { } enabled) _ = OperateBrush(taskEntry.Copy(), BrushOperation.SetLogonEnabled, enabled); return; }
        if (Busy || ShuttingDown || ReadOnly || NeedsSchemaConversion || !OwnsEffects || !authority.Verify(catalog.Authority).Success || catalog.Scripts.SingleOrDefault(e => e.Id == id) is not { ManagementMode: ManagementMode.Normal } || !Begin("Salvando escolha para o próximo login.")) return;
        try { Mutate(id, entry => entry.StartWithWindows = value); Status = "Escolha do próximo login salva; execução atual preservada."; }
        catch (Exception ex) { Fail(ex); } finally { Finish(); }
    }
    public void SetAdministratorPreference(Guid id, bool value)
    {
        if (Busy || ShuttingDown || ReadOnly || NeedsSchemaConversion || !OwnsEffects || !authority.Verify(catalog.Authority).Success || !Begin("Salvando preferência.")) return;
        try { Mutate(id, entry => entry.RunAsAdministrator = value); } catch (Exception ex) { Fail(ex); } finally { Finish(); }
    }
    public async Task Reconcile()
    {
        if (!Begin("Conferindo recuperação dos scripts gerenciados.")) return;
        try
        {
            // Validate durable prerequisites BEFORE any discovery/process adapter call.
            if (!supervision.Gate(catalog, new([], true, "Preflight"), ShuttingDown, true).Success) { Status = "Supervisão desarmada: primário/migração/owner não prontos."; return; }
            Snapshot = await processes.Snapshot(shutdown.Token); ObservationStale = false;
            var gate = supervision.Gate(catalog, Snapshot, ShuttingDown, true);
            if (!gate.Success) { Status = gate.Message; return; }
            if (!RuntimeCurrent || catalog.Runtime.AutomaticPaused) { Status = PauseNotice; return; }
            foreach (var entry in catalog.Scripts.Where(e => e.ManagementMode == ManagementMode.Normal && catalog.Runtime.Wants(e.Id)).Select(e => e.Copy()).ToList())
            {
                gate = supervision.Gate(catalog, Snapshot, ShuttingDown, true);
                if (!gate.Success) { Status = gate.Message; return; }
                if (entry.RunAsAdministrator) { Status = "Elevação somente manual; nenhum UAC automático."; continue; }
                if (Snapshot.For(entry.Path).Count > 0) continue;
                var action = ScriptAction.Start;
                var allowed = Policy.Check(entry, Snapshot, action, File.Exists(entry.Path));
                if (!allowed.Success) { Status = allowed.Message; continue; }
                var next = CopyCatalog();
                if (action == ScriptAction.Start)
                {
                    var reservation = supervision.ReserveStart(next, entry);
                    EnsurePrimaryCurrent(); store.Save(next); catalog = next;
                    if (!reservation.Success) { Status = reservation.Message; continue; }
                }
                else { store.Save(next); catalog = next; } // A writable primary is required for stopping too.
                if (ShuttingDown) return;
                var result = await controller.Execute(entry, action, true, shutdown.Token);
                Status = result.Message; Error = !result.Success;
                if (action == ScriptAction.Start)
                {
                    var current = ManualPrimary(); if (!current.Success) throw new IOException(current.Message);
                    next = CopyCatalog(); supervision.RecordOutcome(next, entry, result.Success);
                    EnsurePrimaryCurrent(); store.Save(next); catalog = next;
                }
                await ObserveAfterEffect(result);
                if (ObservationStale) return;
            }
        }
        catch (Exception ex) { string prior = Status; Fail(ex); Status = prior + " Supervisão interrompida: " + Status; MarkObservationStale("Supervisão interrompida; resultado ou observação pendente."); }
        finally { Finish(); }
    }
    public Result RetryAvailability() => RetryAvailability(SelectedId, false);
    Result RetryAvailability(Guid? id, bool ownsLane)
    {
        if (id is null || SelectedId != id || Selected is not { } entry || entry.Id != id) return Result.Fail("Selecione um script.");
        if ((!ownsLane && Busy) || ShuttingDown || !OwnsEffects || supervision.Owner.LoginKey.Length == 0) return Result.Fail("Recuperação indisponível durante outra operação ou sem dono confiável.");
        if (ReadOnly || NeedsSchemaConversion || !store.PrimaryValid || store.LastWriteFailed || store.LoadedSchema != 5 || !authority.Ready || authority.HasPending || !AuthorityBinding.Same(catalog.Authority, authority.Binding)) return Result.Fail("Catálogo ou autoridade não prontos para recuperação.");
        if (catalog.Migration.State != MigrationState.Completed || catalog.Migration.OwnerSid != supervision.Owner.Sid || !JournalStore.Compatible(store.DirectoryPath, catalog.Migration, supervision.Owner.Fixture)) return Result.Fail("Migração/journal não concluídos de forma compatível.");
        if (entry.ManagementMode != ManagementMode.Normal || !RuntimeCurrent || !catalog.Runtime.Wants(entry.Id) || AutomaticPaused || entry.RunAsAdministrator || entry.LaunchBlocked) return Result.Fail("Recuperação automática não solicitada ou pausada para este script.");
        if (ObservationStale || !Snapshot.Complete || Snapshot.Unknown) return Result.Fail("Observação atual insuficiente.");
        var policy = Policy.Check(entry, Snapshot, ScriptAction.Start, File.Exists(entry.Path));
        if (!policy.Success) return policy;
        try { _ = Paths.LexicalInterpreter(entry.Interpreter); }
        catch (ArgumentException) { return Result.Fail("Interpretador AutoHotkey inválido."); }
        if (!File.Exists(entry.Interpreter)) return Result.Fail("Interpretador não encontrado.");
        var budget = catalog.Recovery.SingleOrDefault(r => r.ScriptId == entry.Id);
        if (budget is null || budget.LoginKey != supervision.Owner.LoginKey || budget.Failures <= 0 && !budget.Suspended) return Result.Fail("Nenhuma recuperação pendente neste login.");
        var now = supervision.Clock.UtcNow;
        if (now.Ticks < budget.NextTryUtcTicks || budget.AttemptsUtcTicks.Count(t => t >= now.AddMinutes(-10).Ticks) >= 3) return Result.Fail("Aguarde o cooldown e o limite de tentativas.");
        return Result.Ok("Liberar a recuperação existente; nenhum script será iniciado agora.");
    }
    public void RetryRecovery()
    {
        var id = SelectedId;
        var available = RetryAvailability(id, false);
        if (!available.Success) { Status = available.Message; Notify(); return; }
        if (!Begin("Verificando limite de recuperação.")) return;
        try
        {
            available = RetryAvailability(id, true);
            if (!available.Success) { Status = available.Message; return; }
            var gate = supervision.Gate(catalog, Snapshot, ShuttingDown, true);
            if (!gate.Success) { Status = gate.Message; return; }
            available = RetryAvailability(id, true);
            if (!available.Success) { Status = available.Message; return; }
            var entry = catalog.Scripts.Single(e => e.Id == id);
            var next = CopyCatalog(); var result = supervision.Retry(next, entry);
            if (result.Success) { store.Save(next); catalog = next; }
            Status = result.Message; Error = !result.Success;
        }
        catch (Exception ex) { Fail(ex); } finally { Finish(); }
    }
    async Task ObserveAfterEffect(Result result)
    {
        try { Snapshot = await processes.Snapshot(CancellationToken.None); ObservationStale = false; }
        catch (Exception)
        {
            MarkObservationStale("A consulta posterior falhou. Estado anterior; resultado da acao preservado.");
            Status = result.Message + " Atualização falhou; observação anterior, execucao atual incerta."; Error = true;
        }
    }
    Result EffectAuthority(ScriptEntry entry, ScriptAction action)
    {
        var primary = ManualPrimary(); if (!primary.Success) return primary;
        if (action is ScriptAction.Start or ScriptAction.Restart && authority.HasPending) return Result.Fail(authority.PendingReason);
        if (action is ScriptAction.Stop or ScriptAction.Recycle && authority.Find(entry.Path) is not null) return Result.Fail("Pedido deste script não confirmado; efeito bloqueado.");
        return Result.Ok("Autoridade de efeito atual.");
    }
    Result NormalLaunchAuthority()
    {
        if (FixtureMode || authority.Fixture) return Result.Fail("Autoridade de demonstração não autoriza início nativo.");
        if (!Busy || lane.Idle.IsCompleted) return Result.Fail("Início nativo exige a lane de ação ocupada.");
        var primary = ManualPrimary(); if (!primary.Success) return primary;
        return authority.HasPending ? Result.Fail(authority.PendingReason) : Result.Ok("Início normal autorizado sem pedidos pendentes.");
    }
    Result ManualPrimary()
    {
        if (ShuttingDown || !OwnsEffects || ReadOnly || NeedsSchemaConversion || store.LoadedSchema != 5 || !store.PrimaryValid || store.LastWriteFailed || !store.VerifyPrimary(catalog)) return Result.Fail("Perfil/owner/encerramento não autoriza o início.");
        return authority.Verify(catalog.Authority);
    }
    public string RequestDetails(ScriptEntry? entry)
    {
        if (BrushPending(entry)) return brush!.Notice + "\nVerificar pedido usa somente consulta/prova atual. Ausencia ou prova insuficiente preserva o pedido; nenhuma nova solicitacao UAC.";
        var request = entry is null ? authority.Requests.FirstOrDefault() : authority.Find(entry.Path) ?? authority.Requests.FirstOrDefault();
        string location = "Autoridade de pedidos: " + authority.FilePath + "\n";
        if (request is null) return location + (authority.Ready ? authority.HasPending ? authority.PendingReason : "Nenhum pedido pendente." : authority.Notice);
        string candidate = request.Candidate is null ? "O Windows não forneceu identidade suficiente. O pedido permanece registrado após reabrir; repetir pode criar duplicata. Recuperar após reinicialização está disponível somente quando a prova de contexto e ausência puder ser confirmada. Pedidos legados ou sem prova suficiente permanecem bloqueados." : $"Candidato PID {request.Candidate.Pid}; não é prova de execução ou privilégio. Verificar pedido faz apenas consulta normal.";
        string proofHint = request.Context.Kind == "Legacy" ? "Pedido legado sem captura original. Recuperação por reinicialização indisponível; nenhuma prova retroativa será criada." : "Prova parcial de reinicialização. Verificar pedido não remove por ausência; recuperar exige confirmação própria.";
        if (checkedRequestNonce == request.Nonce && checkedContextKind == request.Context.Kind && requestProofNotice.Length > 0) proofHint = "Última consulta: " + requestProofNotice + "\n" + proofHint;
        return proofHint + "\n" + location + "Início solicitado; execução não confirmada.\n" + request.ScriptPath + "\n" + request.InterpreterPath + "\n" + candidate;
    }
    async Task DispatchManualElevation(ScriptEntry entry, bool managed, LaunchRequest prepared)
    {
        ManualLaunchPermit? permit = null;
        try
        {
            var valid = ManualPrimary(); if (!valid.Success) { Status = valid.Message; Error = true; return; }
            shutdown.Token.ThrowIfCancellationRequested();
            Snapshot = await processes.Snapshot(shutdown.Token); ObservationStale = false;
            var allowed = Policy.Check(entry, Snapshot, ScriptAction.Start, File.Exists(entry.Path));
            if (!allowed.Success || authority.HasPending) { Status = authority.HasPending ? authority.PendingReason : allowed.Message; Error = true; return; }
            var ready = processes.Preflight(entry); if (!ready.Success) { Status = ready.Message; Error = true; return; }
            if (elevation is null) { Status = "Dispatcher manual indisponível."; return; }
            permit = authority.Register(entry, prepared, bootContext, catalog, store, () => Busy && !lane.Idle.IsCompleted ? ManualPrimary() : Result.Fail("Lane manual encerrada."));
            var outcome = await controller.ExecuteElevation(entry, permit, elevation, shutdown.Token);
            if (outcome.Disposition is ElevationDisposition.Cancelled or ElevationDisposition.RefusedBeforeDispatch && !permit.DispatchStarted || outcome.Disposition == ElevationDisposition.Cancelled)
            {
                var cleared = authority.ClearOwn(permit, outcome);
                Status = outcome.Message + " " + cleared.Message; Error = !cleared.Success;
                // A cancelled UAC request retains its deliberate reservation, not failure/cooldown.
                return;
            }
            Status = outcome.Message;
            if (outcome.Candidate is not null) { var persisted = authority.SetCandidate(permit, outcome.Candidate); if (!persisted.Success) Status += " " + persisted.Message; }
            else Status = "Início solicitado; execução não confirmada. O Windows não forneceu identidade suficiente. O pedido permanece registrado após reabrir; repetir pode criar duplicata. Recuperar após reinicialização está disponível somente quando a prova de contexto e ausência puder ser confirmada. Pedidos legados ou sem prova suficiente permanecem bloqueados.";
            if (managed && outcome.Accepted)
            {
                var next = CopyCatalog(); supervision.RecordOutcome(next, entry, true); EnsurePrimaryCurrent(); store.Save(next); catalog = next;
            }
            await ObserveAfterEffect(Result.Ok(Status));
            // Do not clear merely because a query is complete or sees no process.
            if (outcome.Candidate is not null && !ShuttingDown) await VerifyPendingCore(entry.Path);
        }
        catch (Exception ex)
        {
            Fail(ex);
            if (permit is not null)
            {
                if (!permit.DispatchStarted) Status += " " + authority.ClearOwn(permit, new(ElevationDisposition.RefusedBeforeDispatch, "Não entregue ao SO.")).Message;
                else Status = "Pedido registrado; execução não confirmada. " + Status;
            }
        }
    }
    public async Task VerifyRequest()
    {
        var request = Selected is { } selected ? authority.Find(selected.Path) ?? authority.Requests.FirstOrDefault() : authority.Requests.FirstOrDefault();
        if (request is null || !Begin("Consultando identidade do pedido, sem elevação.")) return;
        try { await VerifyPendingCore(request.ScriptPath); } catch (Exception ex) { Fail(ex); } finally { Finish(); }
    }
    async Task VerifyPendingCore(string path)
    {
        var primary = ManualPrimary(); if (!primary.Success) { Status = primary.Message; Error = true; return; }
        var refresh = authority.Refresh(catalog.Authority); if (!refresh.Success) { Status = refresh.Message; Error = true; return; }
        var request = authority.Find(path);
        if (request is null) { Status = "Nenhum pedido pendente para este caminho."; return; }
        Snapshot = await processes.Snapshot(shutdown.Token); ObservationStale = false;
        if (request.Candidate is null) { checkedRequestNonce = request.Nonce; checkedContextKind = request.Context.Kind; requestProofNotice = RecoveryEligibility(request).Message; Status = RequestDetails(new() { Path = path }); return; }
        var matching = Snapshot.For(path);
        if (matching.Count != 1 || matching[0].Pid != request.Candidate.Pid || matching[0].Created != request.Candidate.Created || !Paths.Equal(matching[0].Executable, request.InterpreterPath)) { Status = "Observação separada: identidade não corresponde ao candidato. Pedido preservado."; return; }
        if (!FixtureMode && processes is not WindowsProcesses) { Status = "Consulta nativa positiva indisponível; pedido preservado."; return; }
        var proof = await processes.Prove(matching[0], shutdown.Token);
        if (!proof.Success) { Status = proof.Message + " Pedido preservado."; return; }
        var recent = await processes.Snapshot(shutdown.Token);
        if (recent.For(path).Count != 1 || !recent.Processes.Any(p => p == matching[0])) { Status = "Identidade mudou após prova; pedido preservado."; return; }
        primary = ManualPrimary(); if (!primary.Success) { Status = primary.Message; return; }
        var clear = authority.ClearPositive(request, matching[0]); Status = clear.Message; Error = !clear.Success;
        Snapshot = recent; ObservationStale = false;
    }
    LaunchRequest? RequestForSelection() => Selected is { } selected ? authority.Find(selected.Path) ?? authority.Requests.FirstOrDefault() : authority.Requests.FirstOrDefault();
    Result RecoveryEligibility(LaunchRequest request)
    {
        var valid = ManualPrimary(); if (!valid.Success) return valid;
        valid = bootContext.Verify(request, catalog.Authority!); if (!valid.Success) return valid;
        try { ulong sample = bootContext.Sample(); return sample < request.Context.Origin ? Result.Ok("Prova de contexto elegível para nova conferência manual. Verificar pedido não remove por ausência.") : Result.Fail("O contador atual não prova reinicialização. Pedido preservado; espera, suspensão e relógio civil não substituem a prova."); }
        catch (IOException e) { return Result.Fail(e.Message); }
    }
    public async Task RecoverAfterRestart()
    {
        var request = RequestForSelection(); if (request is null || !Begin("Confirmando recuperação manual, sem iniciar scripts.")) return;
        string expectedLedger = authority.Fingerprint, expectedProfile = store.LoadedFingerprint; Guid? selected = SelectedId;
        try
        {
            if (!await interaction.Confirm("Recuperar após reinicialização", "Repetir as verificações de contexto e ausência para este pedido?\n" + request.ScriptPath + "\n\nA recuperação depende de prova de reinicialização e ausência completa. Remover a última pendência pode permitir que regras de supervisão já configuradas atuem no próximo ciclo ordinário. Esta ação não inicia nem para scripts. Cancelar preserva tudo.")) { Status = "Recuperação cancelada; pedido preservado."; return; }
            var primary = ManualPrimary(); if (!primary.Success) { Status = primary.Message; Error = true; return; }
            if (SelectedId != selected || authority.Fingerprint != expectedLedger || store.LoadedFingerprint != expectedProfile) { Status = "Seleção ou fingerprint mudou durante a confirmação. Pedido preservado."; return; }
            shutdown.Token.ThrowIfCancellationRequested();
            var proof = bootContext.Verify(request, catalog.Authority!); if (!proof.Success) { Status = proof.Message; return; }
            ulong first = bootContext.Sample();
            var observed = await processes.Snapshot(shutdown.Token);
            ulong second = bootContext.Sample();
            if (request.Context.Origin == 0 || first > second || second >= request.Context.Origin || observed.Unknown || observed.For(request.ScriptPath).Count != 0) { Status = "Prova de reinicialização e ausência insuficiente. Pedido preservado; qualquer instância do mesmo script ou visibilidade parcial impede a recuperação."; return; }
            primary = ManualPrimary(); if (!primary.Success) { Status = primary.Message; return; }
            shutdown.Token.ThrowIfCancellationRequested();
            proof = bootContext.Verify(request, catalog.Authority!); if (!proof.Success || authority.Fingerprint != expectedLedger || store.LoadedFingerprint != expectedProfile || SelectedId != selected) { Status = "Prova/pedido mudou antes da gravação. Pedido preservado."; return; }
            var clear = authority.ClearAfterReset(request, expectedLedger); Status = clear.Message; Error = !clear.Success;
            Snapshot = observed; ObservationStale = false;
            // No refresh, callback, budget or supervision here. An ordinary timer always takes a new snapshot.
        }
        catch (Exception ex) { Fail(ex); } finally { Finish(); }
    }
    public bool IsBrush(ScriptEntry entry) => brush is not null && BrushTaskControls.Matches(entry);
    public bool? StartupChoice(ScriptEntry entry) => IsBrush(entry) ? brush!.Probe.LogonEnabled : entry.StartWithWindows;
    public bool StartupEditable(ScriptEntry entry) => entry.ManagementMode == ManagementMode.Normal || IsBrush(entry) && brush!.Probe.Ready && !brush.Pending;
    public string TaskNotice(ScriptEntry entry) => IsBrush(entry) ? brush!.Notice : "";
    static BrushOperation BrushAction(ScriptAction action) => action switch { ScriptAction.Start => BrushOperation.Start, ScriptAction.Stop => BrushOperation.Stop, ScriptAction.Restart => BrushOperation.Restart, _ => throw new ArgumentException("Acao Brush invalida.") };
    Result BrushGate()
    {
        var gate = ManualPrimary();
        if (!gate.Success) return gate;
        if (authority.HasPending) return Result.Fail(authority.PendingReason);
        return supervision.Owner.LoginKey.Length > 0 ? gate : Result.Fail("Login Brush nao confirmado.");
    }
    public bool BrushPending(ScriptEntry? entry) => entry is not null && IsBrush(entry) && brush!.Pending;
    public static (string Status, bool Error) BrushInspectionPresentation(BrushReply reply) => (reply.Message, !reply.Confirmed);
    public async Task InspectBrush()
    {
        if (Busy || ShuttingDown || brush is null || Selected is not { } selected || !IsBrush(selected) || !Begin("Verificando Brush somente leitura.")) return;
        try
        {
            Result Verify(BrushJournal j)
            {
                var valid = BrushGate(); using var current = Process.GetCurrentProcess();
                if (!valid.Success || !Busy || ShuttingDown || !IsBrush(catalog.Scripts.Single(e => e.Id == selected.Id)) || j.Fixture != FixtureMode || j.Sid != supervision.Owner.Sid || j.LoginKey != supervision.Owner.LoginKey || j.Session != current.SessionId || j.AuthorityId != catalog.Authority!.LedgerId) return Result.Fail("Owner/login/registro Brush mudou; pedido preservado.");
                return NormalMigrationEffects.FileHash(store.FilePath) == j.CatalogHash && NormalMigrationEffects.FileHash(authority.FilePath) == j.LedgerHash && NormalMigrationEffects.FileHash(authority.AnchorPath) == j.AnchorHash ? valid : Result.Fail("Perfil/autoridade Brush mudou; pedido preservado.");
            }
            var result = await brush.Settle(Verify, processes, shutdown.Token); (Status, Error) = BrushInspectionPresentation(result);
            await ObserveAfterEffect(new Result(result.Confirmed, result.Message)); brush.Refresh();
        }
        catch (Exception ex) { Fail(ex); }
        finally { Finish(); }
    }
    async Task OperateBrush(ScriptEntry entry, BrushOperation op, bool? enabled)
    {
        if (Busy || ShuttingDown || !IsBrush(entry)) return;
        var gate = BrushGate(); if (!gate.Success) { Status = gate.Message; Error = true; Notify(); return; }
        var availability = brush!.Available(entry, op, Snapshot);
        if (!availability.Success) { Status = availability.Message; Error = true; Notify(); return; }
        if (!Begin("Confirmando operacao da tarefa Brush.")) return;
        try
        {
            string effect = op switch { BrushOperation.Start => "Iniciar pela tarefa existente", BrushOperation.Stop => "Interromper somente a instancia Brush verificada", BrushOperation.Restart => "Interromper Brush e solicitar novo inicio", _ => (enabled == true ? "Marcar" : "Desmarcar") + " somente o gatilho de login" };
            if (!await interaction.Confirm("Brush: confirmar UAC", effect + "?\nTarefa: " + BrushTaskControls.TaskName + "\n" + entry.Path + "\n\nUm helper temporario solicita UAC. O gerenciador continua normal. Desmarcar login preserva o inicio manual. Resultado parcial ou nao confirmado bloqueia repeticao.", shutdown.Token)) { Status = "Cancelado. Nenhum efeito Brush."; return; }
            gate = BrushGate(); if (!gate.Success || ShuttingDown) { Status = gate.Message; Error = true; return; }
            brush.Refresh(); Snapshot = await processes.Snapshot(shutdown.Token); ObservationStale = false;
            availability = brush.Available(entry, op, Snapshot); if (!availability.Success) { Status = availability.Message; Error = true; return; }
            var owner = supervision.Owner;
            using var manager = Process.GetCurrentProcess();
            string exe = Environment.ProcessPath ?? throw new IOException("Executavel Manager nao confirmado.");
            var j = new BrushJournal { Fixture = FixtureMode, Nonce = Guid.NewGuid(), Operation = op, RequestedLogon = enabled, Sid = owner.Sid, LoginKey = owner.LoginKey, ManagerPid = manager.Id, ManagerCreated = manager.StartTime.ToUniversalTime().ToFileTimeUtc(), Session = manager.SessionId, Executable = exe, ExecutableHash = NormalMigrationEffects.FileHash(exe), AssemblyHash = NormalMigrationEffects.FileHash(Path.ChangeExtension(exe, ".dll")), CatalogHash = NormalMigrationEffects.FileHash(store.FilePath), LedgerHash = NormalMigrationEffects.FileHash(authority.FilePath), AnchorHash = NormalMigrationEffects.FileHash(authority.AnchorPath), AuthorityId = catalog.Authority!.LedgerId, DeadlineUtcTicks = DateTime.UtcNow.AddSeconds(90).Ticks };
            Result BoundGate()
            {
                var valid = BrushGate();
                if (!valid.Success || !Busy || ShuttingDown || !IsBrush(catalog.Scripts.Single(e => e.Id == entry.Id))) return Result.Fail("Lane/registro/owner Brush mudou.");
                return NormalMigrationEffects.FileHash(store.FilePath) == j.CatalogHash && NormalMigrationEffects.FileHash(authority.FilePath) == j.LedgerHash && NormalMigrationEffects.FileHash(authority.AnchorPath) == j.AnchorHash ? valid : Result.Fail("Vinculo Brush mudou.");
            }
            var result = await brush.Execute(j, BoundGate, shutdown.Token);
            Status = result.Message; Error = !result.Confirmed;
            await ObserveAfterEffect(new Result(result.Confirmed, result.Message));
        }
        catch (Exception ex) { Fail(ex); }
        finally { Finish(); }
    }
    public async Task Act(ScriptAction action)
    {
        if (Busy || Selected is not { } current) return;
        if (IsBrush(current) && action is ScriptAction.Start or ScriptAction.Stop or ScriptAction.Restart) { await OperateBrush(current.Copy(), BrushAction(action), null); return; }
        var entry = current.Copy(); var available = AvailabilityFor(entry, action);
        if (!available.Success) { Status = available.Message; Error = true; Notify(); return; }
        if (!Begin("Aguardando confirmação e verificando identidade.")) return;
        Result? effectResult = null;
        try
        {
            bool elevatedStart = entry.RunAsAdministrator && action == ScriptAction.Start;
            bool confirmed = elevatedStart ? await interaction.Confirm("Iniciar com UAC", $"Solicitar UAC ao Windows para iniciar manualmente como administrador?\nScript: {entry.Name}\n{entry.Path}\nInterpretador: {entry.Interpreter}\n\nO gerenciador continua normal. Cancelar preserva os dados; marcar a opção não prova privilégio atual.") : action == ScriptAction.Start || await interaction.Confirm(action == ScriptAction.Recycle ? "Enviar à lixeira" : "Confirmar ação", Confirmation(entry, action));
            if (!confirmed) { Status = "Cancelado. Nenhuma alteração feita."; return; }
            // The confirmation can outlive owner/policy/shutdown validity.
            available = AvailabilityFor(entry, action);
            if (!available.Success) { Status = available.Message; Error = true; return; }
            if (elevatedStart) { entry.Path = Paths.Normalize(entry.Path); entry.Interpreter = FixtureMode ? Paths.LexicalInterpreter(entry.Interpreter) : Paths.NormalizeInterpreter(entry.Interpreter); }
            LaunchRequest? prepared = null;
            if (elevatedStart)
            {
                var valid = ManualPrimary(); if (!valid.Success) { Status = valid.Message; Error = true; return; }
                shutdown.Token.ThrowIfCancellationRequested();
                prepared = authority.Prepare(entry, supervision.Clock.UtcNow);
                prepared.Context = bootContext.Capture(prepared, catalog.Authority!);
                shutdown.Token.ThrowIfCancellationRequested();
                valid = ManualPrimary(); if (!valid.Success) { Status = valid.Message; Error = true; return; }
            }
            bool managed = ManagedStart(entry, action);
            if (managed)
            {
                Snapshot = await processes.Snapshot(shutdown.Token); ObservationStale = false;
                available = AvailabilityFor(entry, action);
                if (!available.Success) { Status = available.Message; Error = true; return; }
                var ready = processes.Preflight(entry);
                if (!ready.Success) { Status = ready.Message; Error = true; return; }
                if (ShuttingDown || !OwnsEffects) { Status = "Início recusado pelo encerramento ou owner."; return; }
                var next = CopyCatalog(); var reservation = supervision.ReserveStart(next, entry);
                if (!reservation.Success) { Status = reservation.Message; Error = true; return; }
                // Reserve BEFORE Restart can terminate its healthy instance.
                EnsurePrimaryCurrent(); store.Save(next); catalog = next;
                var gate = supervision.Gate(catalog, Snapshot, ShuttingDown, true);
                if (!gate.Success) { Status = gate.Message; Error = true; return; }
            }
            if (ShuttingDown || !OwnsEffects) { Status = "Ação cancelada pelo encerramento ou owner."; return; }
            if (elevatedStart)
            {
                if (entry.ManagementMode == ManagementMode.Normal) SaveRuntimeIntent(entry.Id, true);
                await DispatchManualElevation(entry, managed, prepared!); return;
            }
            if (entry.ManagementMode == ManagementMode.Normal && action is ScriptAction.Start or ScriptAction.Stop or ScriptAction.Restart)
                SaveRuntimeIntent(entry.Id, action != ScriptAction.Stop);
            var result = await controller.Execute(entry, action, confirmed, shutdown.Token); effectResult = result;
            store.Log(action.ToString(), result.Success); Status = result.Message; Error = !result.Success;
            if (managed)
            {
                var next = CopyCatalog(); supervision.RecordOutcome(next, entry, result.Success);
                EnsurePrimaryCurrent(); store.Save(next); catalog = next;
            }
            await ObserveAfterEffect(result);
        }
        catch (Exception ex)
        {
            Fail(ex); if (effectResult is not null) Status = effectResult.Message + " Atualização/persistência posterior falhou. " + Status;
            MarkObservationStale("Resultado ou observação pendente; atualize para conferir. Nenhuma neutralidade de efeito presumida.");
        }
        finally { Finish(); }
    }
}

public sealed class ConfirmationWindow : Window
{
    public System.Windows.Controls.Button CancelButton { get; }
    public System.Windows.Controls.Button ConfirmButton { get; }
    public ConfirmationWindow(string title, string message)
    {
        Title = title; Width = 560; SizeToContent = SizeToContent.Height; MaxHeight = 650; WindowStartupLocation = WindowStartupLocation.CenterOwner; ResizeMode = ResizeMode.NoResize;
        Background = (System.Windows.Media.Brush)Application.Current.FindResource("BackgroundBrush"); Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextBrush"); FontFamily = new("Segoe UI Variable, Segoe UI"); FontSize = 13;
        var panel = new System.Windows.Controls.StackPanel { Margin = new(24) };
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = "Confirme a ação", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 16) });
        panel.Children.Add(new System.Windows.Controls.ScrollViewer { MaxHeight = 420, VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto, Content = new System.Windows.Controls.TextBlock { Text = message, TextWrapping = TextWrapping.Wrap } });
        var buttons = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 24, 0, 0) };
        CancelButton = new() { Content = "Cancelar", IsCancel = true, IsDefault = true, Margin = new(0, 0, 8, 0) };
        ConfirmButton = new() { Content = title == "Enviar à lixeira" ? "Enviar à lixeira" : "Confirmar" };
        CancelButton.Click += (_, _) => DialogResult = false; ConfirmButton.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(CancelButton); buttons.Children.Add(ConfirmButton); panel.Children.Add(buttons); Content = panel;
        Loaded += (_, _) => CancelButton.Focus();
    }
}
