using EasyIntercept.Hosting;
using Xunit;

namespace EasyIntercept.Tests;

public class StartupOptionsTests
{
    [Fact]
    public void Parent_pid_is_read_from_its_flag()
    {
        var options = StartupOptions.Parse(["--no-browser", "--parent-pid=4242"]);

        Assert.Equal(4242, options.ParentPid);
        Assert.True(options.NoBrowser);
    }

    [Theory]
    [InlineData("--parent-pid=")]
    [InlineData("--parent-pid=abc")]
    [InlineData("--parent-pid=0")]
    [InlineData("--parent-pid=-5")]
    public void Unusable_parent_pid_is_ignored(string flag)
    {
        Assert.Null(StartupOptions.Parse([flag]).ParentPid);
    }

    [Fact]
    public void Parent_pid_is_absent_by_default()
    {
        Assert.Null(StartupOptions.Parse([]).ParentPid);
    }

    [Fact]
    public void Own_flags_are_stripped_but_configuration_arguments_stay()
    {
        var rest = StartupOptions.StripOwnFlags(["--UiPort=1338", "--parent-pid=4242", "--no-browser", "--autostart"]);

        Assert.Equal(["--UiPort=1338"], rest);
    }
}
