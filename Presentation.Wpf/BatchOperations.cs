using System.IO;
namespace Bezoro.AhkManager.Presentation;

public enum BatchAction { Start, Stop, RestartRunning }
public sealed record BatchItem(Guid ScriptId, string Path, ProcessIdentity? Identity, bool Stopped, bool Successful, string Outcome);
public sealed record BatchResult(BatchAction Action, IReadOnlyList<BatchItem> Items, bool Complete, bool Cancelled, bool AutomaticPaused = false)
{
    public string Details => string.Join("\n\n", Items.Select(i => $"{Path.GetFileName(i.Path)}: {i.Outcome}\n{i.Path}"));
}
public sealed partial class UiSession
{
    public bool BatchActive { get; private set; }
    public const string PausedText = "Supervisão automática pausada nesta sessão; escolhas do próximo login preservadas";
    public const string StopExitConfirmation = "Parar agora os scripts normais gerenciados que puderem ser identificados com segurança e sair? As escolhas de inicialização serão mantidas para o próximo login. Scripts externos, elevados ou sem identidade segura podem continuar vivos.";
    bool RuntimeCurrent => catalog.Runtime.Decided && catalog.Runtime.LoginKey == supervision.Owner.LoginKey;
    public bool AutomaticPaused => RuntimeCurrent && catalog.Runtime.AutomaticPaused;
    public string PauseNotice => AutomaticPaused ? PausedText : RuntimeCurrent ? "Escolhas do próximo login separadas da execução nesta sessão." : "Decisão deste login ainda não persistida; supervisão desarmada.";
    void CurrentRuntime(Catalog next)
    {
        if (next.Runtime.LoginKey != supervision.Owner.LoginKey || !next.Runtime.Decided) next.Runtime = new() { LoginKey = supervision.Owner.LoginKey, Decided = true };
    }
    void SaveRuntimeIntent(Guid id, bool desired, bool pause = false)
    {
        var gate = ManualPrimary(); if (!gate.Success) throw new IOException(gate.Message);
        shutdown.Token.ThrowIfCancellationRequested();
        var next = CopyCatalog(); CurrentRuntime(next); next.Runtime.Set(id, desired); next.Runtime.AutomaticPaused |= pause;
        EnsurePrimaryCurrent(); store.Save(next); catalog = next;
    }
    public async Task PrepareLogin(bool explicitLogin)
    {
        if (!Begin("Persistindo decisão deste login.")) return;
        try
        {
            var gate = ManualPrimary(); if (!gate.Success) { Status = gate.Message; return; }
            if (RuntimeCurrent) return;
            var observed = await processes.Snapshot(shutdown.Token);
            gate = ManualPrimary(); if (!gate.Success) { Status = gate.Message; return; }
            var next = CopyCatalog(); CurrentRuntime(next);
            foreach (var entry in next.Scripts.Where(e => e.ManagementMode == ManagementMode.Normal))
                next.Runtime.Set(entry.Id, explicitLogin && entry.StartWithWindows == true && !entry.RunAsAdministrator && !entry.LaunchBlocked || !explicitLogin && !observed.Unknown && observed.For(entry.Path).Count == 1 && observed.For(entry.Path)[0].Elevated == false);
            shutdown.Token.ThrowIfCancellationRequested(); EnsurePrimaryCurrent(); store.Save(next); catalog = next;
            Snapshot = observed; ObservationStale = false; Status = "Decisão de login persistida antes de qualquer início.";
        }
        catch (Exception ex) { Fail(ex); } finally { Finish(); }
    }
    bool TrustedEntry(ScriptEntry entry)
    {
        var root = FixtureMode ? store.DirectoryPath : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AutoHotkey");
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(entry.Path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
    Result BatchGate(Snapshot observed, bool stopOnly)
    {
        shutdown.Token.ThrowIfCancellationRequested();
        return supervision.Gate(catalog, observed, ShuttingDown, Busy, manualStop: stopOnly);
    }
    public async Task<BatchResult> RunBatch(BatchAction action, bool forExit = false, Func<BatchResult, CancellationToken, Task>? acknowledge = null)
    {
        if (!Begin("Confirmando ação nesta sessão.")) return new(action, [], false, true);
        BatchActive = true;
        List<BatchItem> items = []; BatchItem? inFlight = null; bool attempted = false; bool cancelled = false; bool finalComplete = false;
        try
        {
            string title = forExit ? "Sair e parar todos os scripts" : action switch { BatchAction.Start => "Iniciar todos", BatchAction.Stop => "Parar todos", _ => "Reiniciar os que estão rodando" };
            string message = forExit ? StopExitConfirmation : title + " agora somente os scripts normais gerenciados elegíveis? As escolhas de inicialização serão mantidas para o próximo login. Scripts externos, elevados ou sem identidade segura podem continuar vivos.";
            if (!await interaction.Confirm(title, message, shutdown.Token)) { cancelled = true; return new(action, [], false, true); }
            var gate = BatchGate(new([], true, "Preflight"), action == BatchAction.Stop);
            if (!gate.Success)
            {
                Status = gate.Message;
                var refusal = new BatchResult(action, catalog.Scripts.Select(e => new BatchItem(e.Id, e.Path, null, false, false, gate.Message)).ToArray(), false, false, AutomaticPaused);
                if (acknowledge is not null) await acknowledge(refusal, shutdown.Token).WaitAsync(shutdown.Token);
                return refusal;
            }
            var observed = await processes.Snapshot(shutdown.Token);
            gate = BatchGate(observed, action == BatchAction.Stop);
            if (!gate.Success) { Status = gate.Message; items.AddRange(catalog.Scripts.Select(e => new BatchItem(e.Id, e.Path, null, false, false, "Não confirmado: " + gate.Message))); }
            else
            {
                var selected = catalog.Scripts.Select(e => e.Copy()).ToList();
                foreach (var entry in selected)
                {
                    shutdown.Token.ThrowIfCancellationRequested();
                    var initial = observed.For(entry.Path);
                    var target = initial.Count == 1 ? initial[0] : null;
                    string? skip = entry.ManagementMode != ManagementMode.Normal ? "Fonte externa, aposentada ou não gerenciada; preservada" : !TrustedEntry(entry) ? "Fora da raiz confiável" : authority.Find(entry.Path) is not null ? "Pedido pendente; preservado" : !Commands.IsInterpreter(entry.Interpreter) ? "Interpretador não confirmado" : !File.Exists(entry.Path) ? "Arquivo ausente" : entry.LaunchBlocked ? "Início local bloqueado; preservado" : action != BatchAction.Stop && entry.RunAsAdministrator ? "Preferência de administrador: início somente manual" : initial.Count > 1 ? "Múltiplas instâncias; sobreviventes" : target is not null && action != BatchAction.Start && target.Elevated != false ? "Privilégio elevado ou não confirmado; sobrevivente" : null;
                    if (skip is not null) { items.Add(new(entry.Id, entry.Path, target, false, initial.Count == 0 && action == BatchAction.Stop, skip)); continue; }
                    if (action == BatchAction.Start && initial.Count == 1) { items.Add(new(entry.Id, entry.Path, target, false, true, "Já rodando; nenhum início")); continue; }
                    if (action == BatchAction.RestartRunning && target is null) { items.Add(new(entry.Id, entry.Path, null, false, true, "Parado; nenhum efeito")); continue; }
                    var fresh = await processes.Snapshot(shutdown.Token);
                    gate = BatchGate(fresh, action == BatchAction.Stop);
                    if (!gate.Success) { items.Add(new(entry.Id, entry.Path, target, false, false, gate.Message)); break; }
                    if (action == BatchAction.Stop && target is null)
                    {
                        if (fresh.For(entry.Path).Count != 0) { items.Add(new(entry.Id, entry.Path, null, false, false, "Instância apareceu; parada não autorizada")); continue; }
                        SaveRuntimeIntent(entry.Id, false, pause: true); attempted = true;
                        items.Add(new(entry.Id, entry.Path, null, false, true, "Parado; intenção desta sessão persistida")); continue;
                    }
                    if (target is not null && (fresh.For(entry.Path).Count != 1 || !ProcessIdentity.SameCore(fresh.For(entry.Path)[0], target) || fresh.For(entry.Path)[0].Elevated != false && action != BatchAction.Start)) { items.Add(new(entry.Id, entry.Path, target, false, false, "Identidade mudou; nenhum retargeting")); continue; }
                    if (action == BatchAction.Start && fresh.For(entry.Path).Count != 0) { items.Add(new(entry.Id, entry.Path, null, false, false, "Instância apareceu; início recusado")); continue; }
                    if (action != BatchAction.Stop)
                    {
                        var ready = processes.Preflight(entry); if (!ready.Success) { items.Add(new(entry.Id, entry.Path, target, false, false, ready.Message)); continue; }
                        gate = BatchGate(fresh, false); if (!gate.Success) { items.Add(new(entry.Id, entry.Path, target, false, false, gate.Message)); break; }
                        var next = CopyCatalog(); var reserve = supervision.ReserveStart(next, entry);
                        if (!reserve.Success) { items.Add(new(entry.Id, entry.Path, target, false, false, reserve.Message)); continue; }
                        CurrentRuntime(next); next.Runtime.Set(entry.Id, true); next.Runtime.AutomaticPaused = true;
                        EnsurePrimaryCurrent(); store.Save(next); catalog = next;
                    }
                    else SaveRuntimeIntent(entry.Id, false, pause: true);
                    gate = BatchGate(fresh, action == BatchAction.Stop);
                    if (!gate.Success) { items.Add(new(entry.Id, entry.Path, target, false, false, gate.Message)); break; }
                    inFlight = new(entry.Id, entry.Path, target, false, false, "Tentativa interrompida; saída não confirmada");
                    attempted = true; bool stopped = false; Result effect;
                    if (action != BatchAction.Start)
                    {
                        effect = await processes.Stop(target!, shutdown.Token);
                        var after = await processes.Snapshot(shutdown.Token);
                        gate = BatchGate(after, action == BatchAction.Stop);
                        stopped = effect.Success && gate.Success && after.For(entry.Path).Count == 0;
                        if (!stopped) { items.Add(new(entry.Id, entry.Path, target, false, false, "Parada não confirmada; " + effect.Message)); continue; }
                        if (action == BatchAction.Stop) { items.Add(new(entry.Id, entry.Path, target, true, true, "Parado; saída verificada")); continue; }
                    }
                    effect = await controller.Execute(entry, ScriptAction.Start, true, shutdown.Token);
                    var outcomeGate = ManualPrimary(); if (!outcomeGate.Success) { items.Add(new(entry.Id, entry.Path, target, stopped, false, effect.Message + "; " + outcomeGate.Message)); break; }
                    var outcome = CopyCatalog(); supervision.RecordOutcome(outcome, entry, effect.Success); EnsurePrimaryCurrent(); store.Save(outcome); catalog = outcome;
                    items.Add(new(entry.Id, entry.Path, target, stopped, effect.Success, stopped && !effect.Success ? "Parado; reinício não concluído: " + effect.Message : effect.Message));
                }
                var final = await processes.Snapshot(shutdown.Token); Snapshot = final; ObservationStale = false;
                gate = BatchGate(final, action == BatchAction.Stop);
                finalComplete = gate.Success && items.Count == selected.Count && items.All(i => i.Successful) && (action != BatchAction.Stop || selected.All(e => final.For(e.Path).Count == 0));
                if (action == BatchAction.Stop)
                {
                    foreach (var process in final.Processes)
                    {
                        var entry = selected.SingleOrDefault(e => Paths.Equal(e.Path, process.Script));
                        if (entry is null || items.Any(i => i.ScriptId == entry.Id && i.Stopped)) items.Add(new(entry?.Id ?? Guid.Empty, process.Script ?? "AutoHotkey não identificado", process, false, false, "Sobrevivente observado na conferência final; nenhum novo alvo autorizado"));
                    }
                }
                finalComplete &= items.All(i => i.Successful);
                if (!gate.Success || action == BatchAction.Stop && selected.Any(e => final.For(e.Path).Count > 0)) finalComplete = false;
            }
            var report = new BatchResult(action, items.ToArray(), finalComplete, false, AutomaticPaused);
            Status = AutomaticPaused ? PausedText : "Ação sem efeitos confirmados; escolhas preservadas."; Error = !report.Complete; Notify();
            if (acknowledge is not null) await acknowledge(report, shutdown.Token).WaitAsync(shutdown.Token);
            return report;
        }
        catch (OperationCanceledException) { if (inFlight is not null && !items.Any(i => i.ScriptId == inFlight.ScriptId)) items.Add(inFlight); cancelled = true; Status = attempted ? PausedText + "; operação interrompida" : "Cancelado antes dos efeitos."; }
        catch (Exception ex) { Fail(ex); MarkObservationStale("Resultado de lote não confirmado."); }
        finally { BatchActive = false; Finish(); }
        return new(action, items.ToArray(), false, cancelled, AutomaticPaused);
    }
}
