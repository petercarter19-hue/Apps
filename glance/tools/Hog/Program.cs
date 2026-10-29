// Test helper only: holds a few GB of private memory so the smoke test has a safe row to end.
// "GlanceHog.exe 3 --child <exe>" also starts <exe> 1 as a child, to check that End takes the app's children with it.
int gb = args.Length > 0 && int.TryParse(args[0], out int n) ? n : 3;
int c = Array.IndexOf(args, "--child");
if (c >= 0 && c + 1 < args.Length) System.Diagnostics.Process.Start(args[c + 1], "1")?.Dispose();
var blocks = new List<byte[]>();
for (int i = 0; i < gb; i++)
{
    var b = new byte[1 << 30];
    for (int j = 0; j < b.Length; j += 4096) b[j] = 1; // touch every page so it really counts
    blocks.Add(b);
}
Console.WriteLine($"holding {gb} GB, pid {Environment.ProcessId}");
Thread.Sleep(TimeSpan.FromMinutes(10));
GC.KeepAlive(blocks);
