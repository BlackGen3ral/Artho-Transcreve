namespace Artho.Transcreve;

internal sealed class BrowserControl
{
    private readonly object gate = new();
    private readonly CancellationTokenSource lifetime = new();
    private TaskCompletionSource<bool> changed = WakeSignal();
    private string? request;
    private bool dispatched;
    private long requestedAt;
    private long seenAt = -60_000;
    public bool Connected { get { lock (gate) return Environment.TickCount64 - seenAt < 18_000; } }
    public bool Waiting { get { lock (gate) return request != null; } }

    public bool RequestStart()
    {
        lock (gate)
        {
            if (request != null) return false;
            request = Guid.NewGuid().ToString("N");
            dispatched = false;
            requestedAt = Environment.TickCount64;
            changed.TrySetResult(true);
            changed = WakeSignal();
            return true;
        }
    }

    public async Task<string?> WaitAsync(bool hasTikTokTab, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        while (!deadline.IsCancellationRequested)
        {
            Task signal;
            lock (gate)
            {
                seenAt = Environment.TickCount64;
                if (hasTikTokTab && request != null && !dispatched)
                {
                    dispatched = true;
                    return request;
                }
                signal = changed.Task;
            }

            try { await signal.WaitAsync(deadline.Token); }
            catch (OperationCanceledException) { return null; }
        }
        return null;
    }

    public bool Complete(string? id)
    {
        lock (gate)
        {
            if (request == null || !dispatched || request != id) return false;
            request = null;
            return true;
        }
    }

    public bool Expire()
    {
        lock (gate)
        {
            if (request == null || Environment.TickCount64 - requestedAt < 16_000) return false;
            request = null;
            return true;
        }
    }

    public void Stop() => lifetime.Cancel();

    private static TaskCompletionSource<bool> WakeSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
