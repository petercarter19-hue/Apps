using System.Collections.Concurrent;

namespace PhotoTwins.Core.Native;

/// <summary>
/// Windows' shell (Recycle Bin, sharing status) must be talked to from a single-threaded apartment. This is one background
/// thread of that kind; work is queued to it and runs one job at a time.
/// </summary>
public static class StaRunner
{
    private static readonly BlockingCollection<Action> Jobs = new();
    private static readonly Lazy<Thread> Worker = new(() =>
    {
        var t = new Thread(() =>
        {
            foreach (var job in Jobs.GetConsumingEnumerable()) job();
        }) { IsBackground = true, Name = "PhotoTwins shell thread" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return t;
    });

    public static T Run<T>(Func<T> work)
    {
        _ = Worker.Value;
        // Already on the shell thread (a job calling another job): just run it.
        if (Thread.CurrentThread == Worker.Value) return work();
        var done = new ManualResetEventSlim();
        T result = default!;
        Exception? error = null;
        Jobs.Add(() =>
        {
            try { result = work(); }
            catch (Exception ex) { error = ex; }
            finally { done.Set(); }
        });
        done.Wait();
        if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        return result;
    }

    public static void Run(Action work) => Run<int>(() => { work(); return 0; });
}
