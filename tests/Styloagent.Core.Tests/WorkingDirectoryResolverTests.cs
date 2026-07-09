using Styloagent.Core.Sessions;
using Xunit;

public class WorkingDirectoryResolverTests
{
    private static string Norm(string p) => Path.TrimEndingDirectorySeparator(p);

    [Fact]
    public void Empty_preferred_uses_fallback_when_it_exists()
    {
        var tmp = Path.GetTempPath();
        Assert.Equal(Norm(tmp), Norm(WorkingDirectoryResolver.Resolve("", tmp)));
    }

    [Fact]
    public void Valid_preferred_is_used()
    {
        var tmp = Path.GetTempPath();
        Assert.Equal(Norm(tmp), Norm(WorkingDirectoryResolver.Resolve(tmp, "/does/not/exist")));
    }

    [Fact]
    public void Nonexistent_preferred_and_fallback_use_current_directory()
    {
        var result = WorkingDirectoryResolver.Resolve("/no/such/dir", "/also/missing");
        Assert.Equal(Directory.GetCurrentDirectory(), result);
        Assert.True(Directory.Exists(result));
    }

    [Fact]
    public void Result_is_always_an_existing_directory()
    {
        Assert.True(Directory.Exists(WorkingDirectoryResolver.Resolve(null)));
        Assert.True(Directory.Exists(WorkingDirectoryResolver.Resolve("")));
        Assert.True(Directory.Exists(WorkingDirectoryResolver.Resolve("   ")));
    }
}
