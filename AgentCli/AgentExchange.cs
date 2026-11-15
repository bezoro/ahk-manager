using System.Text.Json;

namespace Bezoro.AhkManager.AgentCli;

// A single duplex read detects caller loss during execution and becomes the
// correlated response receipt if both sides remain live.
internal static class AgentExchange
{
    internal static async Task Relay(
        Stream pipe,
        byte[] request,
        Guid requestId,
        Task<byte[]> pending,
        Func<byte[], int, Task> send,
        CancellationToken stopping,
        TimeSpan? responseTimeout = null
    )
    {
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        using var responseDeadline = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        responseDeadline.CancelAfter(responseTimeout ?? TimeSpan.FromSeconds(115));
        Task<byte[]> receipt = AgentProtocol.Read(pipe, AgentProtocol.MaxReceipt, connection.Token);
        if (receipt.IsCompleted)
        {
            _ = receipt.Exception;
            return;
        }

        bool forwarded = false;
        bool delivered = false;
        try
        {
            await send(request, AgentProtocol.MaxRequest);
            forwarded = true;
            try
            {
                await Task.WhenAny(pending, receipt).WaitAsync(responseDeadline.Token);
            }
            catch (OperationCanceledException) when (!stopping.IsCancellationRequested)
            {
                // The public client has its own reply deadline. Do not forward
                // a late host result to it after this relay has timed out.
                return;
            }
            if (receipt.IsCompleted)
                return;
            byte[] response = await pending;
            using var delivery = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            delivery.CancelAfter(TimeSpan.FromSeconds(5));
            await AgentProtocol.Write(pipe, response, AgentProtocol.MaxResponse, delivery.Token);
            var confirmation = AgentProtocol.ReadReceipt(await receipt.WaitAsync(delivery.Token));
            delivered = confirmation.RequestId == requestId && confirmation.Delivered;
        }
        finally
        {
            connection.Cancel();
            _ = receipt.ContinueWith(
                fault => _ = fault.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted
                    | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default
            );
            if (forwarded && !stopping.IsCancellationRequested)
            {
                try
                {
                    if (!delivered)
                    {
                        var cancellation = new AgentCancellation(
                            AgentProtocol.Version,
                            requestId,
                            true
                        );
                        await send(
                            JsonSerializer.SerializeToUtf8Bytes(
                                cancellation,
                                AgentJson.Default.AgentCancellation
                            ),
                            AgentProtocol.MaxReceipt
                        );
                    }
                }
                finally
                {
                    // The host only expects a receipt after producing its reply.
                    // Keep this pipe slot reserved until then or until the host stops.
                    await pending.WaitAsync(stopping);
                    if (!stopping.IsCancellationRequested)
                    {
                        var acknowledgment = new AgentReceipt(
                            AgentProtocol.Version,
                            requestId,
                            delivered
                        );
                        await send(
                            JsonSerializer.SerializeToUtf8Bytes(
                                acknowledgment,
                                AgentJson.Default.AgentReceipt
                            ),
                            AgentProtocol.MaxReceipt
                        );
                    }
                }
            }
        }
    }
}
