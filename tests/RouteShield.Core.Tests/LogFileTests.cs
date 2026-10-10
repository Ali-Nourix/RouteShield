using Xunit;

namespace RouteShield.Tests;

public sealed class LogFileTests : IDisposable
{
    private static readonly string NewLine = Environment.NewLine;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "routeshield-logs-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Reads the way the diagnostics export does: shared, while the log is still open for writing.</summary>
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public void Lines_stay_in_memory_until_a_flush()
    {
        var file = new LogFile(Path.Combine(_directory, "app.log"));

        file.WriteLine("first");
        file.WriteLine("second");
        Assert.Equal(string.Empty, ReadShared(file.FilePath));

        file.Flush();
        Assert.Equal($"first{NewLine}second{NewLine}", ReadShared(file.FilePath));
    }

    [Fact]
    public void Later_lines_are_appended_after_a_flush()
    {
        var file = new LogFile(Path.Combine(_directory, "app.log"));

        file.WriteLine("one");
        file.Flush();
        file.WriteLine("two");
        file.Flush();

        Assert.Equal($"one{NewLine}two{NewLine}", ReadShared(file.FilePath));
    }

    [Fact]
    public void A_full_file_is_moved_aside_and_a_new_one_started()
    {
        var file = new LogFile(Path.Combine(_directory, "sing-box.log"), maxBytes: 100);

        // Each line is 16 characters and a line break: the seventh crosses 100 bytes.
        for (var index = 0; index < 9; index++)
        {
            file.WriteLine($"line {index:00} ........");
        }

        file.Flush();

        Assert.EndsWith("sing-box.previous.log", file.PreviousPath);
        var previous = ReadShared(file.PreviousPath);
        var current = ReadShared(file.FilePath);

        Assert.StartsWith("line 00", previous);
        Assert.Contains("line 05", previous);
        Assert.StartsWith("line 06", current);
        Assert.Contains("line 08", current);
        Assert.DoesNotContain("line 06", previous);
    }

    [Fact]
    public void An_oversized_file_from_an_earlier_run_is_moved_aside_before_writing()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "app.log");
        File.WriteAllText(path, new string('x', 500));

        var file = new LogFile(path, maxBytes: 100);
        file.WriteLine("fresh");
        file.Flush();

        Assert.Equal($"fresh{NewLine}", ReadShared(path));
        Assert.Equal(500, new FileInfo(file.PreviousPath).Length);
    }

    [Fact]
    public void Writes_from_many_threads_all_arrive_whole()
    {
        var file = new LogFile(Path.Combine(_directory, "app.log"));

        Parallel.For(0, 2000, index => file.WriteLine($"line {index}"));
        file.Flush();

        var lines = ReadShared(file.FilePath).Split(NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2000, lines.Length);
        Assert.Equal(2000, lines.Distinct().Count());
        Assert.All(lines, line => Assert.StartsWith("line ", line));
    }
}
