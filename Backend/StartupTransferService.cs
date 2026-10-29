using System.Diagnostics;
using System.Security.Cryptography;
using Bezoro.AhkManager.Core.Infrastructure;
using Bezoro.AhkManager.Core.Models;
using Bezoro.AhkManager.Core.Services;

namespace Bezoro.AhkManager.Backend;

internal sealed class StartupTransferService(
    IUserStartupAdapter adapter,
    string hostExecutable,
    Func<bool> effectsAllowed,
    Func<ManagerSnapshot> snapshot,
    Func<string, bool>? oldProductProof = null
)
{
    sealed record Token(
        StartupTransferPreview Preview,
        long Revision,
        string OldHash,
        string NewHash,
        DateTimeOffset Time
    );

    readonly Dictionary<Guid, Token> tokens = [];
    readonly string newCommand = new UserStartupService(adapter, hostExecutable).Command;

    public StartupTransferPreview Preview()
    {
        Prune();
        Guid id = Guid.NewGuid();
        string? old = adapter.ReadEntry();
        string? executable = null;
        if (tokens.Count >= Protocol.MaximumPending)
            throw new ProtocolException("QueueFull");
        ReasonCode reason = ReasonCode.StartupEntryConflict;
        try
        {
            var state = snapshot();
            if (!effectsAllowed())
                reason = ReasonCode.EffectOwnerUnavailable;
            else if (!state.Settings.LaunchManagerAtLogin)
                reason = ReasonCode.StartupInactive;
            else if (old is not null)
            {
                string[] args = ScriptPaths.SplitCommand(old);
                if (args.Length == 3 && args[1] == "--login" && args[2] == "--background")
                {
                    executable = ScriptPaths.Canonical(args[0], false);
                    bool product =
                        oldProductProof?.Invoke(executable)
                        ?? FileVersionInfo.GetVersionInfo(executable).ProductName
                            == "Bezoro.AhkManager.WinUI";
                    if (
                        product
                        && !ScriptPaths.Equal(executable, hostExecutable)
                        && old == new UserStartupService(adapter, executable).Command
                    )
                    {
                        var preview = new StartupTransferPreview(
                            id,
                            executable,
                            hostExecutable,
                            old,
                            newCommand,
                            true,
                            ReasonCode.None
                        );
                        tokens.Add(
                            id,
                            new(
                                preview,
                                state.Revision,
                                Hash(executable),
                                Hash(hostExecutable),
                                DateTimeOffset.UtcNow
                            )
                        );
                        return preview;
                    }
                }
            }
        }
        catch (Exception ex)
            when (SafeJsonFiles.Known(ex) || ex is System.ComponentModel.Win32Exception)
        {
            reason = ReasonCode.StartupEntryConflict;
        }
        return new(id, executable, hostExecutable, old, newCommand, false, reason);
    }

    public OperationResult Commit(Guid id, bool confirmed)
    {
        Prune();
        if (!tokens.Remove(id, out var token))
            return new(Outcome.Failed, ReasonCode.StaleSelection);
        if (!confirmed)
            return new(Outcome.CancelledBeforeDispatch, ReasonCode.Cancelled);
        if (!effectsAllowed())
            return new(Outcome.Failed, ReasonCode.EffectOwnerUnavailable);
        var state = snapshot();
        if (state.Revision != token.Revision || !state.Settings.LaunchManagerAtLogin)
            return new(Outcome.Failed, ReasonCode.StaleSelection);
        try
        {
            using var oldLease = new FileStream(
                token.Preview.OldExecutable!,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read
            );
            using var newLease = new FileStream(
                hostExecutable,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read
            );
            if (Hash(oldLease) != token.OldHash || Hash(newLease) != token.NewHash)
                return new(Outcome.Failed, ReasonCode.IdentityChanged);
            if (!effectsAllowed())
                return new(Outcome.Failed, ReasonCode.EffectOwnerUnavailable);
            return adapter.CompareExchangeEntry(token.Preview.OldCommand, newCommand)
                ? new(Outcome.Confirmed)
                : new(Outcome.Failed, ReasonCode.StartupEntryConflict);
        }
        catch (Exception ex) when (SafeJsonFiles.Known(ex))
        {
            return new(Outcome.Failed, ReasonCode.StartupEntryConflict, ex.Message);
        }
    }

    static string Hash(string path)
    {
        using var file = File.OpenRead(path);
        return Hash(file);
    }

    static string Hash(Stream input)
    {
        if (input.Length > 512L * 1024 * 1024)
            throw new InvalidDataException("Executable exceeds identity bound.");
        return Convert.ToHexString(SHA256.HashData(input));
    }

    void Prune()
    {
        foreach (
            var id in tokens
                .Where(p => DateTimeOffset.UtcNow - p.Value.Time > Protocol.TokenLifetime)
                .Select(p => p.Key)
                .ToArray()
        )
            tokens.Remove(id);
    }
}
