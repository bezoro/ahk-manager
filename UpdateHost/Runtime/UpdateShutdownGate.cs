using System.Diagnostics;

namespace Bezoro.AhkManager.UpdateHost;

internal static class UpdateShutdownGate
{
    internal static void CheckCancellation(string operationDirectory, Guid operationId)
    {
        string path = Path.Combine(operationDirectory, "cancel.json");
        if (!File.Exists(path))
            return;
        using var file = FileLease.Open(path, 1024);
        var cancel = UpdateProtocol.Read<UpdateCancel>(file.Read(1024));
        if (cancel.SchemaVersion != 1 || cancel.OperationId != operationId)
            throw new InvalidDataException("Cancellation identity changed.");
        throw new OperationCanceledException();
    }

    internal static async Task<bool> Wait(
        UpdatePlan plan,
        string operationDirectory,
        Func<bool> exited,
        Func<int> exitCode,
        TimeSpan timeout
    )
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            CheckCancellation(operationDirectory, plan.OperationId);
            if (exited())
            {
                if (exitCode() != 0)
                    throw new InvalidDataException("Manager did not exit normally.");
                using var file = FileLease.Open(
                    Path.Combine(operationDirectory, "commit.json"),
                    4096
                );
                UpdateProtocol.ValidateCommit(
                    plan,
                    UpdateProtocol.Read<UpdateCommit>(file.Read(4096))
                );
                CheckCancellation(operationDirectory, plan.OperationId);
                return true;
            }

            if (clock.Elapsed >= timeout)
                return false;
            await Task.Delay(100).ConfigureAwait(false);
        }
    }
}
