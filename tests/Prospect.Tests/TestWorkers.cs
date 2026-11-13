using System.Reflection;
using Prospect.Game.Client;
using Prospect.Game.Server;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// Waits for the work the game hands to worker threads: the server's check of an uploaded design and a client's
/// decode of a design it was sent. A test that steps intervals by hand passes a second of the game in about a
/// millisecond, while a worker takes the time the machine gives it: some 8 ms for a small design alone, and
/// several hundred when every core runs another test. A deadline counted in intervals then measures the worker's
/// luck. Waiting here before an interval is stepped takes the clock out of it: the result of work that was
/// running is there on the next interval, on a busy machine as on an idle one. Nothing is started, stopped or
/// held back, work that has not begun is not waited for, and the deadlines stay what the rules say.
/// </summary>
internal static class TestWorkers
{
    // This bounds the diagnostic wait, not teardown joins; test commands still need an outer process timeout.
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly FieldInfo Uploads = typeof(ServerHost).GetField("_uploads", Hidden)!;
    private static readonly FieldInfo UploadWorker = typeof(ServerHost).GetNestedType("EditUpload", BindingFlags.NonPublic)!.GetField("Worker")!;
    private static readonly FieldInfo Append = typeof(ClientSession).GetField("_append", Hidden)!;
    private static readonly FieldInfo Download = typeof(ClientSession).GetField("_download", Hidden)!;
    private static readonly FieldInfo AppendWorker = typeof(DesignAppend).GetField("_worker", Hidden)!;
    private static readonly FieldInfo DownloadWorker = typeof(DesignDownload).GetField("_worker", Hidden)!;

    /// <summary>Waits for every check of an uploaded design the server is running, one whose edit has ended included.</summary>
    internal static void Settle(ServerHost server)
    {
        foreach (var upload in (Array)Uploads.GetValue(server)!)
            Finish((Task?)UploadWorker.GetValue(upload), "the server's check of an uploaded design");
    }

    /// <summary>Waits for the decode a client is running, of a design it is let in with or of one the server added.</summary>
    internal static void Settle(ClientSession client, string name)
    {
        if (Download.GetValue(client) is DesignDownload download)
            Settle(download, name);
        if (Append.GetValue(client) is { } append)
            Finish((Task?)AppendWorker.GetValue(append), $"{name}'s decode of a design the server added");
    }

    /// <summary>Waits for the decode a download is running.</summary>
    internal static void Settle(DesignDownload download, string name) =>
        Finish((Task?)DownloadWorker.GetValue(download), $"{name}'s decode of a design it is let in with");

    private static void Finish(Task? worker, string what)
    {
        // The wait handle, not Wait: a worker that failed has finished too, and what its failure means is for the
        // code that reads its result to say.
        if (worker is null || worker.IsCompleted || ((IAsyncResult)worker).AsyncWaitHandle.WaitOne(Limit))
            return;
        Assert.Fail($"{what} was still running after {Limit.TotalSeconds:0} s");
    }
}
