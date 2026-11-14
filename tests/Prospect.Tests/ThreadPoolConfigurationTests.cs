using Xunit;

namespace Prospect.Tests;

/// <summary>
/// Pool threads stay alive for the life of the process. Without this, the pool that the client builds at startup
/// (BRUTAL's runners and the minimum thread count) expires 20 s into a quiet stretch, and the runtime takes 64 bytes
/// of managed memory as each worker leaves. That showed up as <c>make bench-scans</c> failing "Allocated, process"
/// at random, with the render thread, the tick, the survey worker and every job clean.
/// </summary>
public sealed class ThreadPoolConfigurationTests
{
    /// <summary>
    /// The setting reaches the runtime of every shipped program, and of the test host that measures allocation. Only
    /// the runtime's own configuration shows it: nothing the test can run in a few seconds would see a thread expire.
    /// </summary>
    [Fact]
    public void PoolThreadsAreNeverRetired()
    {
        Assert.Equal("-1", AppContext.GetData("System.Threading.ThreadPool.ThreadsToKeepAlive")?.ToString());
    }
}
