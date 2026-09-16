using System.Diagnostics;

namespace EasyIntercept.Hosting;

/// <summary>
/// Stops the host when the process that started us goes away. Used by the macOS menu bar app, which
/// runs the server as a child: macOS has no PR_SET_PDEATHSIG, so without this a crashed shell would
/// leave an invisible server holding the UI and proxy ports.
/// </summary>
public static class ParentWatchdog
{
    public static void Start(int parentPid, IHostApplicationLifetime lifetime, ILogger logger, TimeSpan? interval = null)
    {
        var every = interval ?? TimeSpan.FromSeconds(2);
        var thread = new Thread(() =>
        {
            while (!lifetime.ApplicationStopping.IsCancellationRequested)
            {
                if (!IsAlive(parentPid))
                {
                    logger.LogInformation("Parent process {ParentPid} is gone; shutting down", parentPid);
                    lifetime.StopApplication();
                    return;
                }
                Thread.Sleep(every);
            }
        })
        { IsBackground = true, Name = "EasyIntercept parent watchdog" };
        thread.Start();
    }

    public static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false; // no such process
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
