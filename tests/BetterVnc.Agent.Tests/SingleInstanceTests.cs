using BetterVnc.Agent;
using Xunit;

namespace BetterVnc.Agent.Tests;

/// <summary>The lock that keeps a second agent with the same config from serving.</summary>
public class SingleInstanceTests
{
    [Fact]
    public void Lock_sits_next_to_the_config()
    {
        var dir = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "BetterVNC"));
        Assert.Equal(Path.Combine(dir, "bettervnc-agent.lock"), SingleInstance.LockPath(Path.Combine(dir, "bettervnc-agent.json")));
    }

    [Fact]
    public void Second_copy_does_not_get_the_lock_until_the_first_lets_go()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bettervnc-lock-test-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "bettervnc-agent.lock");
        try
        {
            using (var first = SingleInstance.TryAcquire(path))
            {
                Assert.NotNull(first);
                Assert.Null(SingleInstance.TryAcquire(path));
            }
            using var again = SingleInstance.TryAcquire(path);
            Assert.NotNull(again);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
