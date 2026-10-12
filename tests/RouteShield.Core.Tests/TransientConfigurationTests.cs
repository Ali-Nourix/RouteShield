using Xunit;

namespace RouteShield.Tests;

public class TransientConfigurationTests
{
    [Fact]
    public void Existing_and_absent_files_are_cleaned_without_throwing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rs-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "test-only private engine input");
            Assert.True(TransientConfiguration.Delete(path));
            Assert.False(File.Exists(path));
            Assert.True(TransientConfiguration.Delete(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Cleanup_does_not_remove_directories()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        try
        {
            Assert.False(TransientConfiguration.Delete(path));
            Assert.True(Directory.Exists(path));
        }
        finally
        {
            Directory.Delete(path);
        }
    }
}
