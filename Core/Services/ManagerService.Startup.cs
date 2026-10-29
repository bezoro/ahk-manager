using System.Collections.Immutable;
using Bezoro.AhkManager.Core.Infrastructure;
using Bezoro.AhkManager.Core.Models;

namespace Bezoro.AhkManager.Core.Services;

public sealed partial class ManagerService
{
    public Task<OperationResult> RunLoginAsync(CancellationToken cancellationToken = default) =>
        ResultAction(
            async () =>
            {
                async Task<OperationResult> Abort(
                    string stage,
                    OperationResult result,
                    ImmutableArray<Issue> issues = default
                )
                {
                    string detail =
                        $"Início com Windows interrompido ao {stage}. {LoginReason(result.Code)}";
                    if (!issues.IsDefaultOrEmpty)
                        detail +=
                            "\n"
                            + string.Join(
                                "\n",
                                issues
                                    .Take(8)
                                    .Select(issue =>
                                        issue.Detail is { Length: > 0 } reason
                                            ? reason[..Math.Min(reason.Length, 240)]
                                            : LoginReason(issue.Code)
                                    )
                            );
                    var explained = result with { Detail = detail };
                    await Log(null, ScriptOperation.Start, explained, CancellationToken.None)
                        .ConfigureAwait(false);
                    await Publish(false, CancellationToken.None).ConfigureAwait(false);
                    return explained;
                }
                var registration = document.Settings.LaunchManagerAtLogin
                    ? startup.RegistrationStatus(
                        true,
                        document.Settings.LaunchManagerAsAdministrator
                    )
                    : ReasonCode.StartupInactive;
                if (registration != ReasonCode.None)
                    return await Abort(
                            "conferir a configuração de início",
                            new(Outcome.Failed, registration)
                        )
                        .ConfigureAwait(false);
                if (!owner.Held || !owner.Trusted || owner.LoginKey.Length == 0)
                    return await Abort(
                            "obter permissão para gerenciar os scripts",
                            new(Outcome.Failed, ReasonCode.EffectOwnerUnavailable)
                        )
                        .ConfigureAwait(false);
                if (document.LastLoginKey == owner.LoginKey)
                    return new(Outcome.Confirmed, ReasonCode.StartupAlreadyProcessed);
                StartupInspection inspection;
                try
                {
                    inspection = await startup
                        .InspectAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return await Abort(
                            "conferir outras configurações de início",
                            new(Outcome.CancelledBeforeDispatch, ReasonCode.Cancelled)
                        )
                        .ConfigureAwait(false);
                }
                if (!inspection.Complete)
                    return await Abort(
                            "conferir outras configurações de início",
                            new(Outcome.Failed, ReasonCode.ExternalStartupConflict),
                            inspection.Issues
                        )
                        .ConfigureAwait(false);
                OperationResult reserve;
                try
                {
                    reserve = await Commit(
                            document with
                            {
                                LastLoginKey = owner.LoginKey,
                            },
                            null,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return await Abort(
                            "salvar o controle desta sessão",
                            new(Outcome.CancelledBeforeDispatch, ReasonCode.Cancelled)
                        )
                        .ConfigureAwait(false);
                }
                if (!reserve.Succeeded)
                    return await Abort("salvar o controle desta sessão", reserve)
                        .ConfigureAwait(false);
                int started = 0,
                    running = 0,
                    failed = 0;
                var details = new List<string>();
                var continuationCandidates = new List<ScriptRecord>();
                void Failure(ScriptRecord script, ReasonCode reason)
                {
                    failed++;
                    if (details.Count < 24)
                        details.Add($"{script.FriendlyName}: {LoginReason(reason)}");
                }
                foreach (var script in document.Scripts.Where(s => s.RunAtLogin == true).ToArray())
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        Failure(script, ReasonCode.Cancelled);
                        await Log(
                                script.Id,
                                ScriptOperation.Start,
                                new(
                                    Outcome.CancelledBeforeDispatch,
                                    ReasonCode.Cancelled,
                                    $"{script.FriendlyName}: {LoginReason(ReasonCode.Cancelled)}"
                                ),
                                CancellationToken.None
                            )
                            .ConfigureAwait(false);
                        continue;
                    }
                    await ObserveCore(CancellationToken.None).ConfigureAwait(false);
                    var reason =
                        cancellationToken.IsCancellationRequested ? ReasonCode.Cancelled
                        : script.RunAsAdministrator && !processes.IsAdministrator
                            ? ReasonCode.ManualElevationRequired
                        : inspection.ScriptPaths.Any(p => ScriptPaths.Equal(p, script.Path))
                            ? ReasonCode.ExternalStartupConflict
                        : Policy(script, ScriptOperation.Start, null);
                    if (reason == ReasonCode.AlreadyRunning)
                    {
                        running++;
                        continue;
                    }
                    if (reason != ReasonCode.None)
                    {
                        if (
                            reason == ReasonCode.ObservationIncomplete
                            && !script.RunAsAdministrator
                        )
                            continuationCandidates.Add(script);
                        Failure(script, reason);
                        await Log(
                                script.Id,
                                ScriptOperation.Start,
                                new(
                                    reason == ReasonCode.Cancelled
                                        ? Outcome.CancelledBeforeDispatch
                                        : Outcome.Failed,
                                    reason,
                                    $"{script.FriendlyName}: {LoginReason(reason)}"
                                ),
                                CancellationToken.None
                            )
                            .ConfigureAwait(false);
                        continue;
                    }
                    var execution = await ExecuteProcess(
                            script,
                            ScriptOperation.Start,
                            null,
                            Guid.NewGuid(),
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                    if (execution.Result.Succeeded)
                        started++;
                    else
                        Failure(script, execution.Result.Code);
                }
                string detail =
                    $"Iniciados: {started}. Já em execução: {running}. Não iniciados ou não confirmados: {failed}.";
                if (details.Count > 0)
                    detail += "\n\n" + string.Join("\n", details);
                if (failed > details.Count)
                    detail += "\nConsulte a atividade para os demais resultados.";
                if (
                    continuationCandidates.Count > 0
                    && !processes.IsAdministrator
                    && !journal.Records.Any(OperationJournal.IsPending)
                )
                {
                    try
                    {
                        await PrepareStartupContinuationCore(continuationCandidates)
                            .ConfigureAwait(false);
                        detail +=
                            "\n\nÉ possível solicitar permissão ao Windows para verificar os processos e continuar somente os scripts normais que ainda não foram iniciados.";
                    }
                    catch (Exception ex) when (SafeJsonFiles.Known(ex))
                    {
                        detail += "\nNão foi possível continuar a inicialização com segurança.";
                    }
                }
                var summary = new OperationResult(
                    failed > 0 ? Outcome.Partial : Outcome.Confirmed,
                    Detail: detail
                );
                await Log(null, ScriptOperation.Start, summary, CancellationToken.None)
                    .ConfigureAwait(false);
                await Publish(false, CancellationToken.None).ConfigureAwait(false);
                return summary;
            },
            cancellationToken
        );

    static string LoginReason(ReasonCode reason) =>
        reason switch
        {
            ReasonCode.ManualElevationRequired =>
                "Início automático administrativo ignorado: o Manager está sem acesso de administrador. A execução manual ainda pode solicitar permissão ao Windows.",
            ReasonCode.ObservationIncomplete =>
                "O inventário de processos está incompleto; não é seguro iniciar outra instância.",
            ReasonCode.PendingOperation =>
                "Não iniciado: uma solicitação anterior ainda não teve o resultado confirmado. O Manager não repetirá a execução automaticamente.",
            ReasonCode.LaunchUncertain =>
                "O resultado da execução é incerto. Um novo início está bloqueado para evitar duplicação.",
            ReasonCode.AccessDenied => "O Windows negou acesso ao arquivo ou ao processo.",
            ReasonCode.MissingFile => "O arquivo não foi encontrado.",
            ReasonCode.UnsupportedInterpreter =>
                "O interpretador selecionado ou a versão exigida pelo script não é compatível.",
            ReasonCode.ExternalStartupConflict =>
                "Outra configuração de início automático pode duplicar este script.",
            ReasonCode.LegacyGuard =>
                "Uma proteção do catálogo ou o bloqueio de novas execuções impede o início.",
            ReasonCode.Cancelled => "O início foi cancelado.",
            ReasonCode.DispatchFailed => "O envio da execução falhou.",
            ReasonCode.EffectOwnerUnavailable =>
                "A autoridade para iniciar scripts não está disponível.",
            ReasonCode.StartupInactive =>
                "O início com Windows está desativado ou sem registro. Revise essa opção nas preferências.",
            ReasonCode.StartupEntryConflict =>
                "A entrada de início com Windows pertence a outra configuração. Revise-a nas preferências.",
            ReasonCode.ConcurrentSettingsChange =>
                "As configurações foram alteradas por outra instância. Atualize o estado antes de tentar novamente.",
            ReasonCode.ReadOnlySettings or ReasonCode.StorageFailure =>
                "Os dados precisam de recuperação antes de iniciar scripts.",
            _ => $"Início indisponível ({reason}). Consulte a atividade.",
        };
}
