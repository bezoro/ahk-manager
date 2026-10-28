using System.Text;
using System.Text.Json;
using Bezoro.AhkManager.Backend;
using Bezoro.AhkManager.WinUI.Infrastructure;
using Bezoro.AhkManager.WinUI.Models;

namespace Tests.Backend;

internal static class StartupContinuationChecks
{
    internal static async Task RunAsync(string root, Action<bool, string> check)
    {
        string nonce = new('a', 64), hash = new('b', 64);
        var descriptor = new StartupAdoptionDescriptor(Guid.NewGuid(), nonce, 10, 100, hash);
        check(StartupAdoptionDescriptor.Parse(descriptor.Encode()) == descriptor, "adoption descriptor roundtrip selects one blocked operation");
        foreach (string invalid in new[] { "", descriptor.Encode() + "|extra", descriptor.Encode().Replace(nonce, "not-a-nonce"), descriptor.Encode().Replace("|10|100|", "|0|100|"), descriptor.Encode().Replace("|10|100|", "|10|0|") })
        {
            bool refused = false; try { _ = StartupAdoptionDescriptor.Parse(invalid); } catch (ProtocolException) { refused = true; }
            check(refused, "malformed adoption descriptor has no fallback authority");
        }
        var bootstrap = new ProcessStamp(10, 100, @"C:\fixture\backend.exe", "S-1-fixture", 1);
        var own = bootstrap with { Pid = 30, Created = 300 };
        var host = bootstrap with { Pid = 20, Created = 200, Executable = @"C:\fixture\Manager.exe" };
        var parent = new ParentProof(host.Pid, host.Created, host.Executable, own.Pid, own.Created, own.Sid, own.Session, AuthorityMode.Administrator);
        var high = new ProcessSecurity(true, 0x3000);
        check(StartupAdoptionPeer.Bootstrap(descriptor, parent, own, bootstrap, high, hash, hash), "adopter accepts actual matching elevated bootstrap proof");
        foreach (var spoof in new[] { bootstrap with { Pid = 11 }, bootstrap with { Created = 101 }, bootstrap with { Sid = "other" }, bootstrap with { Session = 2 }, bootstrap with { Executable = @"C:\evil.exe" } })
            check(!StartupAdoptionPeer.Bootstrap(descriptor, parent, own, spoof, high, hash, hash), "adopter rejects spoofed PID creation user session or image");
        check(!StartupAdoptionPeer.Bootstrap(descriptor, parent, own, bootstrap, new(false, 0x2000), hash, hash) && !StartupAdoptionPeer.Bootstrap(descriptor, parent, own, bootstrap, high, hash, "changed"), "adopter rejects medium peer and changed executable hash");
        check(StartupAdoptionPeer.Backend(bootstrap, host, host, own, high, high, host.Pid, host.Executable, own.Executable, hash, hash), "bootstrap accepts only launched host and its elevated backend");
        foreach (var spoof in new[] { own with { Sid = "other" }, own with { Session = 2 }, own with { Executable = @"C:\evil.exe" } })
            check(!StartupAdoptionPeer.Backend(bootstrap, host, host, spoof, high, high, host.Pid, host.Executable, own.Executable, hash, hash), "bootstrap rejects another backend user session or executable");
        check(!StartupAdoptionPeer.Backend(bootstrap, host, host with { Created = 201 }, own, high, high, host.Pid, host.Executable, own.Executable, hash, hash) && !StartupAdoptionPeer.Backend(bootstrap, host, host, own, high, high, 99, host.Executable, own.Executable, hash, hash), "bootstrap rejects reused host PID or wrong backend parent");
        check(!StartupAdoptionPeer.Backend(bootstrap, host, host, own, new(false, 0x2000), high, host.Pid, host.Executable, own.Executable, hash, hash), "bootstrap independently rejects downgraded host token");
        var frame = new StartupAdoptionFrame("AdoptionAccepted", descriptor.OperationId, nonce, TicketHash: hash);
        string receiptPath = Path.Combine(root, "adoption-pinned-receipt.json"); byte[] approved = Encoding.UTF8.GetBytes("approved immutable receipt");
        await File.WriteAllBytesAsync(receiptPath, Encoding.UTF8.GetBytes("replaced before lease"));
        bool replaced = false; try { using var lease = ManagerElevation.PinReceipt(receiptPath, approved, out _, out _); } catch (ProtocolException) { replaced = true; }
        check(replaced, "receipt authority binds original in-memory bytes even if file is replaced before lease");
        await File.WriteAllBytesAsync(receiptPath, approved);
        using (var lease = ManagerElevation.PinReceipt(receiptPath, approved, out var receiptHash, out _))
        {
            bool protectedWrite = false; try { await File.WriteAllTextAsync(receiptPath, "changed after lease"); } catch (IOException) { protectedWrite = true; }
            check(protectedWrite && receiptHash.Length == 64, "receipt lease prevents writes until authenticated commit is acknowledged");
        }
        foreach (var spoof in new[] { frame with { Kind = "Adopt" }, frame with { OperationId = Guid.NewGuid() }, frame with { Nonce = new string('c',64) }, frame with { TicketHash = "changed" } })
        {
            bool refused = false; try { StartupAdoptionWire.Check(spoof, "AdoptionAccepted", descriptor); } catch (ProtocolException) { refused = true; }
            check(refused, "adoption acknowledgement binds kind operation nonce and immutable ticket hash");
        }
        using (var stream = new MemoryStream())
        {
            await StartupAdoptionWire.Send(stream, frame, CancellationToken.None); stream.Position = 0;
            check(await StartupAdoptionWire.Receive(stream, CancellationToken.None) == frame, "adoption framing roundtrip preserves distinct Accepted receipt");
        }
        foreach (var body in new[] { Encoding.UTF8.GetBytes("{\"kind\":\"Adopt\",\"kind\":\"AdoptionAccepted\"}"), Array.Empty<byte>() })
        {
            using var stream = new MemoryStream(); stream.Write(BitConverter.GetBytes(body.Length)); stream.Write(body); stream.Position = 0;
            bool refused = false; try { await StartupAdoptionWire.Receive(stream, CancellationToken.None); } catch (Exception ex) when (ex is ProtocolException or JsonException) { refused = true; }
            check(refused, "adoption framing rejects duplicate fields and invalid bounded lengths");
        }
        int attempts = 0; var completed = new TaskCompletionSource<OperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cached = new StartupAdoption(descriptor, parent, () => { attempts++; return completed.Task; });
        var first = cached.RunAsync(null!); var replay = cached.RunAsync(null!); var status = cached.StatusAsync();
        completed.SetResult(new(Outcome.Confirmed));
        check(ReferenceEquals(first, replay) && ReferenceEquals(first, status) && (await first).Succeeded && attempts == 1, "lost adoption acknowledgement and concurrent status never replay the acceptance or execution task");
        var blocked = new StartupAdoption(descriptor, parent);
        await using var session = new BackendSession(new FakeManager(), Path.Combine(root, "adoption-blocked"), host.Executable, () => true, new FakeStartup(), blocked);
        using var payload = JsonDocument.Parse("{}");
        await session.ExecuteAsync("load", payload.RootElement, CancellationToken.None);
        foreach (string action in new[] { "runLogin", "refresh", "perform", "saveMetadata", "beginAdministration", "beginStartupContinuation", "recoverPending", "migrate" })
        {
            bool refused = false; try { await session.ExecuteAsync(action, payload.RootElement, CancellationToken.None); } catch (ProtocolException ex) when (ex.Code == "PendingOperation") { refused = true; }
            check(refused, "AdoptionPending blocks backend mutation " + action);
        }
        check((await blocked.StatusAsync()).Outcome == Outcome.Pending, "status before delivery is pending and cannot unlock normal effects");
        int unknownAttempts = 0; var unknownCompletion = new TaskCompletionSource<OperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var unknown = new StartupAdoption(descriptor, parent, () => { unknownAttempts++; return unknownCompletion.Task; });
        var executing = unknown.RunAsync(null!);
        await using var recoverySession = new BackendSession(new FakeManager(), Path.Combine(root,"adoption-recovery"), host.Executable, () => true, new FakeStartup(), unknown);
        await recoverySession.ExecuteAsync("load", payload.RootElement, CancellationToken.None);
        bool blockedDuringExecution = false; try { await recoverySession.ExecuteAsync("recoverPending", payload.RootElement, CancellationToken.None); } catch (ProtocolException ex) when (ex.Code == "PendingOperation") { blockedDuringExecution = true; }
        check(blockedDuringExecution, "adoption recovery remains blocked while execution task is still in progress");
        unknownCompletion.SetResult(new(Outcome.Unknown, ReasonCode.PendingOperation)); await executing;
        bool recoveryAvailable = false; try { recoveryAvailable = (await recoverySession.ExecuteAsync("recoverPending", payload.RootElement, CancellationToken.None)).Deserialize(Protocol.Json.OperationResult)!.Succeeded; } catch (ProtocolException) { }
        check(!unknown.Pending && recoveryAvailable && (await unknown.StatusAsync()).Outcome == Outcome.Unknown && unknownAttempts == 1, "completed unknown adoption allows controlled recovery without restarting its task");
    }
}
