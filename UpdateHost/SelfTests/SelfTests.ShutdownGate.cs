using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Bezoro.AhkManager.UpdateHost;

internal static partial class SelfTests
{
    static async Task CheckShutdownAuthorization(
        string root,
        UpdatePlan plan,
        Action<bool, string> check
    )
    {
        // Native exit evidence and the production file reader run together.
        // These fixed, inert cmd processes never start a Manager or installer.
        using var normalParent = StartExitProbe(0);
        using var failedParent = StartExitProbe(7);
        using var liveParent = Process.GetCurrentProcess();
        if (
            !Native.GetProcessTimes(
                normalParent.SafeHandle,
                out long normalCreated,
                out _,
                out _,
                out _
            )
            || !Native.GetProcessTimes(
                failedParent.SafeHandle,
                out long failedCreated,
                out _,
                out _,
                out _
            )
            || !Native.GetProcessTimes(
                liveParent.SafeHandle,
                out long liveCreated,
                out _,
                out _,
                out _
            )
        )
            throw new System.ComponentModel.Win32Exception(
                System.Runtime.InteropServices.Marshal.GetLastWin32Error()
            );
        await Task.WhenAll(normalParent.WaitForExitAsync(), failedParent.WaitForExitAsync())
            .WaitAsync(TimeSpan.FromSeconds(10));
        check(
            normalParent.ExitCode == 0 && failedParent.ExitCode == 7,
            "native inert process fixtures retain exact zero and abnormal exit evidence"
        );
        foreach (
            string scenario in new[]
            {
                "normal",
                "missing-commit",
                "abnormal-exit",
                "live-parent",
                "wrong-operation",
                "wrong-pid",
                "wrong-created",
                "wrong-schema",
                "malformed",
                "duplicate",
                "unknown-property",
                "cancel",
                "cancel-abnormal-exit",
                "malformed-cancel",
                "linked-commit",
            }
        )
        {
            bool abnormal = scenario is "abnormal-exit" or "cancel-abnormal-exit";
            Process probe =
                abnormal ? failedParent
                : scenario == "live-parent" ? liveParent
                : normalParent;
            var candidate = plan with
            {
                OperationId = Guid.NewGuid(),
                ParentPid = probe.Id,
                ParentStartedAtUtc = DateTimeOffset
                    .FromFileTime(
                        abnormal ? failedCreated
                        : scenario == "live-parent" ? liveCreated
                        : normalCreated
                    )
                    .ToUniversalTime(),
            };
            string operation = Path.Combine(root, "shutdown-gate-" + scenario);
            Directory.CreateDirectory(operation);
            var commit = new UpdateCommit(
                1,
                candidate.OperationId,
                candidate.ParentPid,
                candidate.ParentStartedAtUtc
            );
            commit = scenario switch
            {
                "wrong-operation" => commit with { OperationId = Guid.NewGuid() },
                "wrong-pid" => commit with { ParentPid = candidate.ParentPid + 1 },
                "wrong-created" => commit with
                {
                    ParentStartedAtUtc = candidate.ParentStartedAtUtc.AddMilliseconds(1),
                },
                "wrong-schema" => commit with { SchemaVersion = 2 },
                _ => commit,
            };
            string json = JsonSerializer.Serialize(commit, UpdateProtocol.Json);
            json = scenario switch
            {
                "malformed" => "{}",
                "duplicate" => json.Replace(
                    "\"schemaVersion\":1",
                    "\"schemaVersion\":1,\"schemaVersion\":1",
                    StringComparison.Ordinal
                ),
                "unknown-property" => json.Replace(
                    "\"schemaVersion\":1",
                    "\"schemaVersion\":1,\"extra\":true",
                    StringComparison.Ordinal
                ),
                _ => json,
            };
            if (scenario != "missing-commit")
                await File.WriteAllTextAsync(
                    Path.Combine(operation, "commit.json"),
                    json,
                    new UTF8Encoding(false)
                );
            if (scenario is "cancel" or "cancel-abnormal-exit")
                Evidence.WriteNew(
                    Path.Combine(operation, "cancel.json"),
                    new UpdateCancel(1, candidate.OperationId)
                );
            if (scenario == "malformed-cancel")
                await File.WriteAllTextAsync(Path.Combine(operation, "cancel.json"), "{}");
            if (
                scenario == "linked-commit"
                && !Native.CreateHardLink(
                    Path.Combine(operation, "commit-staging.tmp"),
                    Path.Combine(operation, "commit.json"),
                    0
                )
            )
                throw new System.ComponentModel.Win32Exception(
                    System.Runtime.InteropServices.Marshal.GetLastWin32Error()
                );
            var effects = new FakeEffects("backup-failed", root)
            {
                ShutdownGate = () =>
                    UpdateShutdownGate.Wait(
                        candidate,
                        operation,
                        () => probe.HasExited,
                        () => probe.ExitCode,
                        scenario == "live-parent" ? TimeSpan.Zero : TimeSpan.FromSeconds(1)
                    ),
            };
            var result = await ApplyEngine.Run(candidate, effects);
            check(
                result.Outcome == "Failed"
                    && effects.Backups == (scenario == "normal" ? 1 : 0)
                    && effects.Installs == 0
                    && effects.Restores == 0,
                "production shutdown gate permits only committed normal exit before any snapshot or installer: "
                    + scenario
            );
            if (scenario is "cancel" or "cancel-abnormal-exit")
                check(
                    result.Detail.Contains("cancelled", StringComparison.Ordinal),
                    "cancellation takes precedence over shutdown commit and native exit evidence: "
                        + scenario
                );
        }
    }

    static Process StartExitProbe(int code)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add("/d");
        info.ArgumentList.Add("/c");
        info.ArgumentList.Add(code == 0 ? "exit 0" : "exit 7");
        var process =
            Process.Start(info)
            ?? throw new InvalidOperationException("Native inert process fixture did not start.");
        _ = process.SafeHandle;
        return process;
    }
}
