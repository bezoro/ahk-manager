using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;

namespace Bezoro.AhkManager.AgentCli;

internal sealed class AgentClient(AgentLayout layout, AgentPeer local)
{
    readonly string endpoint = layout.PipeName(local);

    internal async Task<AgentResponse> Invoke(
        AgentRequest request,
        bool start,
        CancellationToken ct
    )
    {
        if (request.Command == "app.start")
            return (await EnsureReady(ct)) with { RequestId = request.RequestId };
        if (start)
            _ = await EnsureReady(ct);
        return await Send(request, TimeSpan.FromSeconds(120), ct)
            ?? throw new AgentFailure("HostUnavailable");
    }

    async Task<AgentResponse> EnsureReady(CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(50));
        NamedPipeServerStream? launchLease = null;
        bool launchAttempted = false;
        var status = new AgentRequest(
            AgentProtocol.Version,
            Guid.NewGuid(),
            "app.status",
            AgentProtocol.Empty
        );
        try
        {
            while (true)
            {
                var current = await ReadStatus(status, budget.Token);
                if (
                    current is { Outcome: "Confirmed", Result.ValueKind: JsonValueKind.Object }
                    && current.Result.Value.TryGetProperty("ready", out var ready)
                    && ready.ValueKind == JsonValueKind.True
                )
                    return current;
                if (
                    current is null
                    && !launchAttempted
                    && launchLease is null
                    && !layout.HasExistingHost()
                )
                {
                    try
                    {
                        launchLease = AgentPipe.Listen(
                            endpoint + ".startup",
                            local.Stamp.Sid,
                            first: true,
                            instances: 1
                        );
                    }
                    catch (Win32Exception ex) when (ex.NativeErrorCode is 5 or 231)
                    {
                        // A peer may be taking the same startup lease. Poll only.
                    }
                    if (
                        launchLease is not null
                        && AgentPipeAbsent()
                        && !layout.HasExistingHost()
                        && local.Live
                    )
                    {
                        launchAttempted = true;
                        await Launch(budget.Token);
                    }
                }
                await Task.Delay(150, budget.Token);
            }
        }
        catch (OperationCanceledException)
        {
            throw new AgentFailure(launchAttempted ? "StartupUnconfirmed" : "HostUnavailable");
        }
        finally
        {
            launchLease?.Dispose();
        }
    }

    async Task<AgentResponse?> ReadStatus(AgentRequest status, CancellationToken ct)
    {
        try
        {
            return await Send(
                status with
                {
                    RequestId = Guid.NewGuid(),
                },
                TimeSpan.FromSeconds(5),
                ct
            );
        }
        catch (AgentFailure ex) when (ex.Code is "HostBusy" or "ReplyUnconfirmed")
        {
            // An occupied or unresolved endpoint is never evidence of absence.
            return new AgentResponse(
                AgentProtocol.Version,
                status.RequestId,
                "",
                null,
                "Unknown",
                ex.Code,
                null
            );
        }
    }

    bool AgentPipeAbsent()
    {
        // Discard an authenticated probe attempt without dispatching any command.
        // A busy endpoint also counts as present.
        try
        {
            using var probe = AgentPipe.Connect(endpoint);
            return probe is null;
        }
        catch (AgentFailure ex) when (ex.Code == "HostBusy")
        {
            return false;
        }
    }

    async Task Launch(CancellationToken ct)
    {
        var start = new ProcessStartInfo(layout.Launcher)
        {
            WorkingDirectory = layout.Root,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("--agent-host");
        using var launcher = Process.Start(start) ?? throw new AgentFailure("StartupFailed");
        try
        {
            await launcher.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(10), ct);
        }
        catch (TimeoutException)
        {
            throw new AgentFailure("StartupUnconfirmed");
        }
        if (launcher.ExitCode != 0)
            throw new AgentFailure("StartupFailed");
    }

    async Task<AgentResponse?> Send(AgentRequest request, TimeSpan budget, CancellationToken ct)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            request,
            AgentJson.Default.AgentRequest
        );
        if (payload.Length > AgentProtocol.MaxRequest)
            throw new AgentFailure("InvalidInput");
        using var pipe = await Connect(ct);
        if (pipe is null)
            return null;
        using var server = new AgentPeer(AgentPipe.ServerPid(pipe.SafePipeHandle));
        server.RequirePeer(local, layout.Cli);
        using var host = AgentPeer.AuthenticateParent(server, local, layout.Host);
        bool quit = request.Command is "app.quit" or "app.stopScriptsAndQuit";
        using var observed = quit ? host.RetainObservableProcess() : null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(budget);
        ct.ThrowIfCancellationRequested();
        AgentResponse response;
        try
        {
            if (!server.Live || !host.Live)
                throw new AgentFailure("PeerUnverified");
            await AgentProtocol.Write(pipe, payload, AgentProtocol.MaxRequest, deadline.Token);
            response = AgentProtocol.ReadResponse(
                await AgentProtocol.Read(pipe, AgentProtocol.MaxResponse, deadline.Token)
            );
            if (response.RequestId != request.RequestId)
                throw new InvalidDataException("MismatchedRequest");
            var receipt = new AgentReceipt(AgentProtocol.Version, request.RequestId, true);
            await AgentProtocol.Write(
                pipe,
                JsonSerializer.SerializeToUtf8Bytes(receipt, AgentJson.Default.AgentReceipt),
                AgentProtocol.MaxReceipt,
                deadline.Token
            );
        }
        catch (Exception ex) when (AgentFailure.Expected(ex) && ex is not AgentFailure)
        {
            throw new AgentFailure("ReplyUnconfirmed");
        }
        if (observed is null || response.Outcome is "Partial" or "Failed" or "Unknown")
            return response;
        if (response is not { Outcome: "Accepted", Code: "ShutdownAccepted" })
            return response with { Outcome = "Unknown", Code = "ShutdownUnconfirmed" };
        try
        {
            await observed.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(60), ct);
            return observed.ExitCode == 0
                ? response with
                {
                    Outcome = "Confirmed",
                    Code = "ShutdownConfirmed",
                }
                : response with
                {
                    Outcome = "Failed",
                    Code = "HostExitFailed",
                };
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            return response with { Outcome = "Unknown", Code = "ShutdownUnconfirmed" };
        }
    }

    async Task<NamedPipeClientStream?> Connect(CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return AgentPipe.Connect(endpoint);
            }
            catch (AgentFailure ex)
                when (ex.Code == "HostBusy" && clock.Elapsed < TimeSpan.FromSeconds(3))
            {
                await Task.Delay(100, ct);
            }
        }
    }
}
