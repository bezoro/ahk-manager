using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Bezoro.AhkManager.WinUI.Infrastructure;
using Bezoro.AhkManager.WinUI.Models;

namespace Bezoro.AhkManager.WinUI.Services;

public sealed partial class ManagerService
{
    internal sealed class AcceptedStartupContinuation
    {
        internal readonly StartupContinuationTicket Ticket;
        internal AcceptedStartupContinuation(StartupContinuationTicket ticket) => Ticket = ticket;
    }
    AcceptedStartupContinuation? acceptedStartup;
    StartupContinuationTicket? preparedStartup;
    bool startupRecoveryPending, startupResumeActive;
    Issue? startupRecoveryIssue;
    StartupContinuationStore Continuations => new(DataRoot);
    // Window placement is saved while draining the host for UAC. Bind every other
    // profile field, including script flags and LastLoginKey, across that save.
    string StartupProfileHash() => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(document with { Settings = document.Settings with { Window = new() } }, ManagerJson.Default.SettingsDocument))).ToLowerInvariant();
    async Task PrepareStartupContinuationCore(IReadOnlyCollection<ScriptRecord> candidates)
    {
        if (await Continuations.FindAsync(owner.LoginKey).ConfigureAwait(false) is not null) return;
        if (!await store.VerifyLoadedAsync().ConfigureAwait(false)) throw new ConcurrentWriteException();
        var items = candidates.Select(script =>
        {
            var interpreter = AutoHotkeyDiscovery.Validate(script.InterpreterPath ?? document.Settings.InterpreterPath ?? "");
            return new StartupContinuationItem(script.Id, script.FriendlyName, ScriptPaths.Canonical(script.Path), interpreter.Path, ScriptPaths.Fingerprint(script.Path), interpreter.Hash);
        }).ToImmutableArray();
        var ticket = new StartupContinuationTicket(1, Guid.NewGuid(), DataRoot, StartupProfileHash(), owner.Sid, Process.GetCurrentProcess().SessionId, owner.LoginKey, DateTimeOffset.UtcNow.AddMinutes(10), items);
        await Continuations.WriteAsync(new(ticket, StartupContinuationStore.Hash(ticket), StartupContinuationPhase.Prepared, items.Select(_ => StartupContinuationItemState.NotDispatched).ToImmutableArray()), null).ConfigureAwait(false);
        preparedStartup = ticket;
    }
    internal Task<StartupContinuationOffer?> GetStartupContinuationAsync() => Exclusive(async () =>
    {
        var record = await Continuations.FindAsync(document.LastLoginKey).ConfigureAwait(false);
        if (record is null) return null;
        bool prepared = preparedStartup is not null && record.TicketHash == StartupContinuationStore.Hash(preparedStartup) && record.Phase == StartupContinuationPhase.Prepared && record.Ticket.ExpiresAt > DateTimeOffset.UtcNow && record.Ticket.ProfileHash == StartupProfileHash() && !journal.Records.Any(OperationJournal.IsPending);
        return new StartupContinuationOffer(record.Ticket.OperationId, record.Ticket.Items.Select(i => i.Name).ToImmutableArray(), prepared && !processes.IsAdministrator, prepared ? ReasonCode.ObservationIncomplete : ReasonCode.PendingOperation);
    }, CancellationToken.None);
    internal Task<StartupContinuationTicket?> GetPreparedStartupContinuationAsync(Guid id) => Exclusive(async () =>
    {
        var record = await Continuations.ReadAsync(id).ConfigureAwait(false);
        return preparedStartup is not null && record is not null && record.TicketHash == StartupContinuationStore.Hash(preparedStartup) && record.Phase == StartupContinuationPhase.Prepared && record.Items.All(i => i == StartupContinuationItemState.NotDispatched) && await StartupTicketCurrent(record.Ticket, false).ConfigureAwait(false) ? preparedStartup : null;
    }, CancellationToken.None);
    internal Task<bool> CommitStartupContinuationAsync(StartupContinuationTicket ticket) => Exclusive(async () =>
    {
        var record = await Continuations.ReadAsync(ticket.OperationId).ConfigureAwait(false);
        if (preparedStartup is null || StartupContinuationStore.Hash(preparedStartup) != StartupContinuationStore.Hash(ticket) || record is null || record.Phase != StartupContinuationPhase.Prepared || record.TicketHash != StartupContinuationStore.Hash(ticket) || !await StartupTicketCurrent(ticket, false).ConfigureAwait(false)) return false;
        await Continuations.WriteAsync(record with { Phase = StartupContinuationPhase.Committed }, record).ConfigureAwait(false); preparedStartup = null; return true;
    }, CancellationToken.None);
    async Task<bool> StartupTicketCurrent(StartupContinuationTicket ticket, bool elevated, bool allItems = true)
    {
        if (ticket.Version != 1 || ticket.OperationId == Guid.Empty || ticket.Items.IsDefaultOrEmpty || ticket.Items.Length > StartupContinuationStore.MaximumItems || ticket.ExpiresAt <= DateTimeOffset.UtcNow || ticket.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(10) || !ScriptPaths.Equal(ticket.ProfileRoot, DataRoot) || ticket.UserSid != owner.Sid || ticket.SessionId != Process.GetCurrentProcess().SessionId || ticket.LoginKey != document.LastLoginKey || ticket.ProfileHash != StartupProfileHash() || !owner.Held || !owner.Trusted || store.ReadOnly || !journal.Ready || journal.Records.Any(OperationJournal.IsPending) || elevated != processes.IsAdministrator || !await store.VerifyLoadedAsync().ConfigureAwait(false)) return false;
        if ((await Continuations.PendingAsync().ConfigureAwait(false)).Any(record => record.Ticket.OperationId != ticket.OperationId)) return false;
        foreach (var item in allItems ? ticket.Items : [])
        {
            using var files = StartupItemLease.Open(item);
            if (files is null || !StartupItemCurrent(item)) return false;
        }
        return true;
    }
    bool StartupItemCurrent(StartupContinuationItem item)
    {
        var script = document.Scripts.SingleOrDefault(s => s.Id == item.ScriptId);
        return script is not null && script.RunAtLogin == true && !script.RunAsAdministrator && !script.LegacyGuard && !script.LaunchBlocked && ScriptPaths.Equal(script.Path, item.ScriptPath) && ScriptPaths.Equal(script.InterpreterPath ?? document.Settings.InterpreterPath, item.InterpreterPath);
    }
    sealed class StartupItemLease : IDisposable
    {
        readonly FileStream script, interpreter;
        StartupItemLease(StartupContinuationItem item)
        {
            script = new(ScriptPaths.Canonical(item.ScriptPath), FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                interpreter = new(ScriptPaths.Canonical(item.InterpreterPath, false), FileMode.Open, FileAccess.Read, FileShare.Read);
                try
                {
                    if (Convert.ToHexString(SHA256.HashData(script)).ToLowerInvariant() != item.ScriptHash || Convert.ToHexString(SHA256.HashData(interpreter)).ToLowerInvariant() != item.InterpreterHash) throw new InvalidDataException("Continuation bytes changed.");
                }
                catch { interpreter.Dispose(); throw; }
            }
            catch { script.Dispose(); throw; }
        }
        internal static StartupItemLease? Open(StartupContinuationItem item)
        {
            try { return new(item); }
            catch (Exception ex) when (SafeJsonFiles.Known(ex) || ex is System.ComponentModel.Win32Exception) { return null; }
        }
        public void Dispose() { interpreter.Dispose(); script.Dispose(); }
    }
    // Only Backend's authenticated live adoption channel calls this seam. The
    // persistent record is a replay barrier, never the source of authority.
    internal Task<AcceptedStartupContinuation?> AcceptStartupContinuationAsync(StartupContinuationTicket ticket) => Exclusive(async () =>
    {
        var record = await Continuations.ReadAsync(ticket.OperationId).ConfigureAwait(false);
        if (acceptedStartup is not null || record is null || record.Phase != StartupContinuationPhase.Committed || record.TicketHash != StartupContinuationStore.Hash(ticket) || !await StartupTicketCurrent(ticket, true).ConfigureAwait(false)) return null;
        await Continuations.WriteAsync(record with { Phase = StartupContinuationPhase.Accepted }, record).ConfigureAwait(false);
        startupRecoveryPending = true;
        return acceptedStartup = new(ticket);
    }, CancellationToken.None);
    internal Task<OperationResult> ResumeNormalLoginAsync(AcceptedStartupContinuation authority, CancellationToken ct) => ResultAction(async () =>
    {
        if (!ReferenceEquals(authority, acceptedStartup)) return new(Outcome.Failed, ReasonCode.PendingOperation);
        acceptedStartup = null;
        var ticket = authority.Ticket;
        var record = await Continuations.ReadAsync(ticket.OperationId).ConfigureAwait(false);
        if (record is null || record.Phase != StartupContinuationPhase.Accepted || record.TicketHash != StartupContinuationStore.Hash(ticket)) return new(Outcome.Unknown, ReasonCode.IdentityChanged);
        startupResumeActive = true;
        try
        {
        int started = 0, running = 0, failed = 0; var details = new List<string>();
        for (int i = 0; i < ticket.Items.Length; i++)
        {
            var item = ticket.Items[i]; var script = document.Scripts.SingleOrDefault(s => s.Id == item.ScriptId);
            using var fileLease = StartupItemLease.Open(item);
            await ObserveCore(CancellationToken.None).ConfigureAwait(false);
            var reason = ct.IsCancellationRequested ? ReasonCode.Cancelled : journal.Records.Any(OperationJournal.IsPending) ? ReasonCode.PendingOperation : !await StartupTicketCurrent(ticket, true, false).ConfigureAwait(false) || fileLease is null || !StartupItemCurrent(item) || script is null ? ReasonCode.IdentityChanged : Policy(script, ScriptOperation.Start, null);
            if (reason != ReasonCode.None)
            {
                if (reason == ReasonCode.AlreadyRunning) running++; else { failed++; details.Add(item.Name + ": " + LoginReason(reason)); }
                record = await SaveItem(record, i, reason == ReasonCode.AlreadyRunning ? StartupContinuationItemState.KnownRunning : StartupContinuationItemState.RefusedBeforeExecution).ConfigureAwait(false);
                await Log(item.ScriptId, ScriptOperation.Start, new(reason == ReasonCode.AlreadyRunning ? Outcome.Confirmed : Outcome.Failed, reason, item.Name + ": " + LoginReason(reason)), CancellationToken.None).ConfigureAwait(false);
                continue;
            }
            // Persist before entering ExecuteProcess; a crash afterwards is never
            // reconstructed as permission to dispatch from missing journal evidence.
            record = await SaveItem(record, i, StartupContinuationItemState.EnteringExecution).ConfigureAwait(false);
            var execution = await ExecuteProcess(script! with { RunAsAdministrator = false }, ScriptOperation.Start, null, Guid.NewGuid(), ct).ConfigureAwait(false);
            bool uncertain = execution.Result.Outcome is Outcome.Unknown or Outcome.Pending or Outcome.Accepted or Outcome.Partial;
            if (execution.Result.Succeeded) started++; else { failed++; details.Add(item.Name + ": " + LoginReason(execution.Result.Code)); }
            record = await SaveItem(record, i, execution.Result.Succeeded ? StartupContinuationItemState.KnownStarted : uncertain ? StartupContinuationItemState.Uncertain : StartupContinuationItemState.KnownFailed).ConfigureAwait(false);
        }
        string detail = $"Continuação do início com Windows. Iniciados: {started}. Já em execução: {running}. Não iniciados ou não confirmados: {failed}.\n" + string.Join("\n", details.Take(24));
        var summary = new OperationResult(failed == 0 ? Outcome.Confirmed : Outcome.Partial, Detail: detail[..Math.Min(detail.Length, 16000)]);
        await Continuations.WriteAsync(record with { Phase = failed == 0 ? StartupContinuationPhase.Completed : StartupContinuationPhase.Partial, Result = summary }, record).ConfigureAwait(false);
        startupRecoveryPending = !(await Continuations.PendingAsync().ConfigureAwait(false)).IsEmpty;
        startupResumeActive = false;
        await Log(null, ScriptOperation.Start, summary, CancellationToken.None).ConfigureAwait(false); await Publish(false, CancellationToken.None).ConfigureAwait(false);
        return summary;
        }
        finally
        {
            startupResumeActive = false;
            try { startupRecoveryPending = !(await Continuations.PendingAsync().ConfigureAwait(false)).IsEmpty; }
            catch (Exception ex) when (SafeJsonFiles.Known(ex)) { startupRecoveryPending = true; }
        }
    }, CancellationToken.None);
    async Task<StartupContinuationRecord> SaveItem(StartupContinuationRecord record, int index, StartupContinuationItemState state)
    {
        var next = record with { Items = record.Items.SetItem(index, state) }; await Continuations.WriteAsync(next, record).ConfigureAwait(false); return next;
    }
    internal Task<StartupContinuationRecord?> StartupContinuationStatusAsync(Guid id) => Exclusive(() => Continuations.ReadAsync(id), CancellationToken.None);
    async Task<ReasonCode> InvalidatePreparedStartupForStart(Guid scriptId, ScriptOperation operation)
    {
        var ticket = preparedStartup;
        if (ticket is null || operation is not (ScriptOperation.Start or ScriptOperation.Restart) || !ticket.Items.Any(item => item.ScriptId == scriptId)) return ReasonCode.None;
        // The original loop's proof ceases to apply as soon as another start enters
        // ExecuteProcess, even when that process later exits or dispatch is refused.
        preparedStartup = null; startupRecoveryPending = true;
        var record = await Continuations.ReadAsync(ticket.OperationId).ConfigureAwait(false);
        if (record is not null)
        {
            if (record.Phase != StartupContinuationPhase.Prepared || record.TicketHash != StartupContinuationStore.Hash(ticket)) return ReasonCode.PendingOperation;
            var invalidated = new OperationResult(Outcome.CancelledBeforeDispatch, ReasonCode.StaleSelection, "A oferta de continuação foi cancelada porque um dos scripts recebeu outro pedido de início. Confira o estado e use uma nova ação manual para os demais scripts; essa oferta não será repetida.");
            await Continuations.WriteAsync(record with { Phase = StartupContinuationPhase.Recovered, Result = invalidated }, record).ConfigureAwait(false);
            await Log(null, ScriptOperation.Start, invalidated, CancellationToken.None).ConfigureAwait(false);
        }
        startupRecoveryPending = !(await Continuations.PendingAsync().ConfigureAwait(false)).IsEmpty;
        return startupRecoveryPending ? ReasonCode.PendingOperation : ReasonCode.None;
    }
    async Task LoadStartupRecovery()
    {
        try { startupRecoveryPending = !(await Continuations.PendingAsync().ConfigureAwait(false)).IsEmpty; }
        catch (Exception ex) when (SafeJsonFiles.Known(ex)) { RememberStartupRecoveryFailure(ex); }
    }
    void RememberStartupRecoveryFailure(Exception ex)
    {
        startupRecoveryPending = true;
        if (startupRecoveryIssue is not null) storageIssues = storageIssues.Remove(startupRecoveryIssue);
        startupRecoveryIssue = new(ReasonCode.PendingOperation, "Não foi possível confirmar os registros de continuação. Novas execuções estão bloqueadas. Use Recuperar pendências para conferir os processos e preservar os registros inválidos, sem repetir execuções.\n" + ex.Message);
        storageIssues = storageIssues.Add(startupRecoveryIssue);
    }
    async Task SettleExpiredStartupContinuationsCore()
    {
        if (!owner.Held || !owner.Trusted || !journal.Ready || !observed.Complete || startupResumeActive || journal.Records.Any(OperationJournal.IsPending)) return;
        try
        {
            foreach (var record in await Continuations.PendingAsync().ConfigureAwait(false))
            {
                if (record.Ticket.ExpiresAt > DateTimeOffset.UtcNow || record.Items.Any(item => item is not (StartupContinuationItemState.NotDispatched or StartupContinuationItemState.KnownRunning or StartupContinuationItemState.KnownStarted or StartupContinuationItemState.KnownFailed or StartupContinuationItemState.RefusedBeforeExecution))) continue;
                await Continuations.WriteAsync(record with { Phase = StartupContinuationPhase.Recovered, Result = new(Outcome.Partial, ReasonCode.Cancelled, "A tentativa de continuação expirou. Nenhuma solicitação de execução foi repetida.") }, record).ConfigureAwait(false);
                if (preparedStartup?.OperationId == record.Ticket.OperationId) preparedStartup = null;
                if (acceptedStartup?.Ticket.OperationId == record.Ticket.OperationId) acceptedStartup = null;
            }
            startupRecoveryPending = (await Continuations.PendingAsync().ConfigureAwait(false)).Any(record => preparedStartup is null || record.Phase != StartupContinuationPhase.Prepared || record.TicketHash != StartupContinuationStore.Hash(preparedStartup));
        }
        catch (Exception ex) when (SafeJsonFiles.Known(ex)) { RememberStartupRecoveryFailure(ex); }
    }
    async Task<bool> RecoverStartupContinuationCore()
    {
        if (startupResumeActive || acceptedStartup is not null || !owner.Held || !owner.Trusted || !journal.Ready || !observed.Complete || journal.Records.Any(OperationJournal.IsPending)) return false;
        try
        {
            var preserved = await Continuations.QuarantineInvalidAsync().ConfigureAwait(false);
            // Snapshot and validate every remaining record before changing any of
            // them. Corruption must not produce a partially recovered inventory.
            var pending = await Continuations.PendingAsync().ConfigureAwait(false);
            foreach (var record in pending)
                await Continuations.WriteAsync(record with { Phase = StartupContinuationPhase.Recovered, Result = new(Outcome.Partial, ReasonCode.LaunchUncertain, "A solicitação anterior foi encerrada após conferir os processos. Nenhuma execução foi repetida.") }, record).ConfigureAwait(false);
            preparedStartup = null;
            acceptedStartup = null;
            startupRecoveryPending = !(await Continuations.PendingAsync().ConfigureAwait(false)).IsEmpty;
            if (startupRecoveryPending) return false;
            if (startupRecoveryIssue is not null) { storageIssues = storageIssues.Remove(startupRecoveryIssue); startupRecoveryIssue = null; }
            if (!preserved.IsEmpty)
                await Log(null, ScriptOperation.Recover, new(Outcome.Confirmed, Detail: $"Recuperação explícita: {preserved.Length} registro(s) inválido(s) preservado(s) em {Path.GetDirectoryName(preserved[0])}. Nenhum script foi iniciado ou repetido."), CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (SafeJsonFiles.Known(ex))
        {
            RememberStartupRecoveryFailure(ex);
            await Log(null, ScriptOperation.Recover, new(Outcome.Failed, ReasonCode.StorageFailure, startupRecoveryIssue!.Detail), CancellationToken.None).ConfigureAwait(false);
            return false;
        }
    }
}
