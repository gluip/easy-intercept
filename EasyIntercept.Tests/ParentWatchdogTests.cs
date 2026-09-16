using EasyIntercept.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EasyIntercept.Tests;

public class ParentWatchdogTests
{
    // No OS hands out this pid (macOS stops at 99998, Linux at 4194304), so it is reliably "gone".
    private const int DeadPid = int.MaxValue;

    [Fact]
    public void The_current_process_counts_as_alive()
    {
        Assert.True(ParentWatchdog.IsAlive(Environment.ProcessId));
    }

    [Fact]
    public void A_pid_nobody_has_counts_as_dead()
    {
        Assert.False(ParentWatchdog.IsAlive(DeadPid));
    }

    [Fact]
    public async Task A_dead_parent_stops_the_host()
    {
        var lifetime = new FakeLifetime();

        ParentWatchdog.Start(DeadPid, lifetime, NullLogger.Instance, TimeSpan.FromMilliseconds(20));

        await lifetime.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_live_parent_keeps_the_host_running()
    {
        var lifetime = new FakeLifetime();

        ParentWatchdog.Start(Environment.ProcessId, lifetime, NullLogger.Instance, TimeSpan.FromMilliseconds(20));
        await Task.Delay(200);

        Assert.False(lifetime.Stopped.Task.IsCompleted);
        lifetime.StopApplication(); // lets the watchdog thread finish
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stopping = new();

        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
            _stopping.Cancel();
            Stopped.TrySetResult();
        }
    }
}
