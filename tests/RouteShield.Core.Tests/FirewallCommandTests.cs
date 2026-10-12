using System.Text;
using RouteShield.Services;
using Xunit;

namespace RouteShield.Tests;

public class FirewallCommandTests
{
    [Fact]
    public void Elevated_script_is_an_argument_not_a_mutable_file_or_a_shell_command()
    {
        const string script = "$programs = @('C:\\Apps\\a''b.exe'); Write-Output 'سلام'";
        var start = FirewallService.CreateStartInfo(script, "/trusted/system");
        Assert.Equal(Path.Combine("/trusted/system", "WindowsPowerShell", "v1.0", "powershell.exe"), start.FileName);
        Assert.False(start.UseShellExecute);
        Assert.Contains("-NoProfile", start.ArgumentList);
        Assert.Contains("-NonInteractive", start.ArgumentList);
        Assert.DoesNotContain("-File", start.ArgumentList);
        Assert.DoesNotContain("-ExecutionPolicy", start.ArgumentList);
        var arguments = start.ArgumentList.ToArray();
        var encoded = arguments[Array.IndexOf(arguments, "-EncodedCommand") + 1];
        Assert.Equal(script, Encoding.Unicode.GetString(Convert.FromBase64String(encoded)));
    }
}
