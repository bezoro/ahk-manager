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
        CancellationToken stopping
    )
    {
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        using var responseDeadline = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        responseDeadline.CancelAfter(TimeSpan.FromSeconds(115));
        Task<byte[]> receipt = AgentProtocol.Read(pipe, AgentProtocol.MaxReceipt, connection.Token);
        if (receipt.IsCompleted)
        {
            _ = receipt.Exception;
            return;
        }

        bool forwarded = false;
        bool canceled = false;
        bool delivered = false;
        try
        {
            await send(request, AgentProtocol.MaxRequest);
            forwarded = true;
            await Task.WhenAny(pending, receipt).WaitAsync(responseDeadline.Token);
            if (receipt.IsCompleted)
            {
                await Cancel();
                // Preserve the concurrency bound through late host completion.
                await pending.WaitAsync(responseDeadline.Token);
                return;
            }
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
                if (!delivered)
                    await Cancel();
                var acknowledgment = new AgentReceipt(AgentProtocol.Version, requestId, delivered);
                await send(
                    JsonSerializer.SerializeToUtf8Bytes(
                        acknowledgment,
                        AgentJson.Default.AgentReceipt
                    ),
                    AgentProtocol.MaxReceipt
                );
            }
        }

        async Task Cancel()
        {
            if (canceled)
                return;
            canceled = true;
            var cancellation = new AgentCancellation(AgentProtocol.Version, requestId, true);
            await send(
                JsonSerializer.SerializeToUtf8Bytes(
                    cancellation,
                    AgentJson.Default.AgentCancellation
                ),
                AgentProtocol.MaxReceipt
            );
        }
    }
}
