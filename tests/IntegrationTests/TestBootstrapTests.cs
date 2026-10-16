using Xunit;

namespace Orbweaver.Tests;

public class TestBootstrapTests
{
    [Fact]
    public void TheAssemblyTempDirectoryExists()
    {
        Assert.True(Directory.Exists(Path.GetTempPath()));
    }
}
