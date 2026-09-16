using Xunit;

namespace ClaudeBuddy.Tests;

public class TestBootstrapTests
{
    [Fact]
    public void TheAssemblyTempDirectoryExists()
    {
        Assert.True(Directory.Exists(Path.GetTempPath()));
    }
}
