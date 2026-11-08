namespace GEE;

/// <summary>
/// Parallel loop for the per-frame work on the main thread, on threads of its own. The .NET thread pool is shared
/// with the lake/elevation/river builds (Parallel.For on big grids) and the loaders: a per-frame Parallel.For queued
/// behind those waited 50-85 ms while tiles streamed in. Here the calling thread takes indices from an atomic counter
/// as well, so the loop finishes even if the helpers are late; it only waits for an index a helper is working on.
/// </summary>
internal static class FrameParallel
{
    private sealed class Job(int count, Action<int> body)
    {
        public readonly int Count = count;
        public readonly Action<int> Body = body;
        public int Next, Done;
        public Exception? Error;

        public void Run()
        {
            int i;
            while ((i = Interlocked.Increment(ref Next) - 1) < Count)
            {
                try
                {
                    Body(i);
                }
                catch (Exception e)
                {
                    Interlocked.CompareExchange(ref Error, e, null);
                }
                Interlocked.Increment(ref Done);
            }
        }
    }

    private static readonly int HelperCount = Math.Clamp(Environment.ProcessorCount / 2 - 1, 1, 5);
    private static readonly SemaphoreSlim Wake = new(0);
    private static volatile Job? _job;
    private static Thread[]? _helpers;

    public static void For(int count, Action<int> body)
    {
        if (count <= 1)
        {
            for (int i = 0; i < count; i++)
                body(i);
            return;
        }
        EnsureHelpers();
        var job = new Job(count, body);
        _job = job;
        Wake.Release(Math.Min(HelperCount, count - 1));
        job.Run();
        // Only indices a helper claimed are outstanding now.
        var spin = new SpinWait();
        while (Volatile.Read(ref job.Done) < count)
            spin.SpinOnce(-1);
        if (job.Error is { } error)
            throw new AggregateException(error);
    }

    private static void EnsureHelpers()
    {
        if (_helpers != null)
            return;
        var threads = new Thread[HelperCount];
        for (int i = 0; i < threads.Length; i++)
        {
            threads[i] = new Thread(() =>
            {
                while (true)
                {
                    Wake.Wait();
                    _job?.Run();
                }
            }) { IsBackground = true, Name = "GEE frame work " + i, Priority = ThreadPriority.AboveNormal };
            threads[i].Start();
        }
        _helpers = threads;
    }
}
