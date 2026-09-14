using EasyIntercept.Hosting;
using Xunit;

namespace EasyIntercept.Tests;

public class LauncherTests
{
    private const string SessionFile = "/data/sessions/0642_POST_example_com.json";

    [Fact]
    public void Reveal_on_macos_selects_the_file_in_finder()
    {
        var psi = Launcher.RevealCommand(SessionFile, Launcher.HostOs.MacOS);

        Assert.Equal("open", psi.FileName);
        Assert.Equal(["-R", SessionFile], psi.ArgumentList);
    }

    [Fact]
    public void Reveal_on_macos_passes_paths_with_spaces_as_one_argument()
    {
        const string path = "/Users/me/Library/Application Support/EasyIntercept/sessions/a.json";

        var psi = Launcher.RevealCommand(path, Launcher.HostOs.MacOS);

        Assert.Equal(path, psi.ArgumentList[1]);
    }

    [Fact]
    public void Reveal_on_windows_selects_the_file_in_explorer()
    {
        var psi = Launcher.RevealCommand(@"C:\data\sessions\a.json", Launcher.HostOs.Windows);

        Assert.Equal("explorer.exe", psi.FileName);
        Assert.Equal(@"/select,""C:\data\sessions\a.json""", psi.Arguments);
    }

    [Fact]
    public void Reveal_on_linux_opens_the_containing_folder()
    {
        var psi = Launcher.RevealCommand(SessionFile, Launcher.HostOs.Linux);

        Assert.Equal("xdg-open", psi.FileName);
        // Path.GetDirectoryName follows the host's separators ("\data\sessions" on the Windows CI runner);
        // the Linux branch only ever runs on Linux, so compare against the host's own result
        Assert.Equal([Path.GetDirectoryName(SessionFile)], psi.ArgumentList);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)] // e.g. open -R on a path that vanished, or xdg-open without a desktop handler
    [InlineData(null, true)] // still running after the wait: the file manager was handed the path
    public void Reveal_helper_exit_code_decides_success(int? exitCode, bool expected)
    {
        Assert.Equal(expected, Launcher.HelperSucceeded(exitCode));
    }
}
