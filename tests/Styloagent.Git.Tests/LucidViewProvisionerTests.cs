using System.Diagnostics;
using Styloagent.Git;
using Xunit;

public class LucidViewProvisionerTests
{
    // Skip cleanly when git is unavailable so CI without git stays green.
    private static bool GitAvailable()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("git", "--version") { RedirectStandardOutput = true });
            p!.WaitForExit();
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    private static void Run(string dir, string args)
    {
        using var p = Process.Start(new ProcessStartInfo("git", args)
        { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true })!;
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"git {args}: {p.StandardError.ReadToEnd()}");
    }

    /// <summary>Creates a sibling lucidview git repo next to <paramref name="repoRoot"/> with one tracked source file and a gitignored build-output file.</summary>
    private static void SeedSourceRepo(string repoRoot)
    {
        var source = LucidViewProvisioner.SourcePathFor(repoRoot);
        Directory.CreateDirectory(source);
        Run(source, "init -b main");
        Run(source, "config user.email t@t.t");
        Run(source, "config user.name t");
        File.WriteAllText(Path.Combine(source, ".gitignore"), "obj/\nbin/\n");
        var proj = Path.Combine(source, "Naiad", "src", "Naiad");
        Directory.CreateDirectory(proj);
        File.WriteAllText(Path.Combine(proj, "Naiad.csproj"), "<Project />");
        // A gitignored build-output file that must NOT be copied into the checkout.
        Directory.CreateDirectory(Path.Combine(proj, "obj"));
        File.WriteAllText(Path.Combine(proj, "obj", "stale.cache"), "STALE");
        Run(source, "add -A");
        Run(source, "commit -m init");
    }

    [Fact]
    public async Task Provisions_head_tree_excluding_git_and_build_output()
    {
        if (!GitAvailable()) return;
        var baseDir = Path.Combine(Path.GetTempPath(), "lucidprov-" + Guid.NewGuid().ToString("N"));
        var repoRoot = Path.Combine(baseDir, "app");
        Directory.CreateDirectory(repoRoot);
        try
        {
            SeedSourceRepo(repoRoot);

            var result = await LucidViewProvisioner.EnsureAsync(repoRoot);

            Assert.Equal(LucidViewProvisionStatus.Provisioned, result.Status);
            Assert.True(result.Ok, result.Detail);

            var dest = LucidViewProvisioner.DestPathFor(repoRoot);
            Assert.True(File.Exists(Path.Combine(dest, "Naiad", "src", "Naiad", "Naiad.csproj")), "tracked source file should be present");
            Assert.False(Directory.Exists(Path.Combine(dest, ".git")), ".git must not be copied");
            Assert.False(File.Exists(Path.Combine(dest, "Naiad", "src", "Naiad", "obj", "stale.cache")), "gitignored build output must not be copied");
        }
        finally { TryDelete(baseDir); }
    }

    [Fact]
    public async Task Second_call_is_idempotent_then_reprovisions_when_head_moves()
    {
        if (!GitAvailable()) return;
        var baseDir = Path.Combine(Path.GetTempPath(), "lucidprov-" + Guid.NewGuid().ToString("N"));
        var repoRoot = Path.Combine(baseDir, "app");
        Directory.CreateDirectory(repoRoot);
        try
        {
            SeedSourceRepo(repoRoot);
            
            Assert.Equal(LucidViewProvisionStatus.Provisioned, (await LucidViewProvisioner.EnsureAsync(repoRoot)).Status);
            // Unchanged source HEAD → fast no-op.
            Assert.Equal(LucidViewProvisionStatus.AlreadyCurrent, (await LucidViewProvisioner.EnsureAsync(repoRoot)).Status);

            // Move the source HEAD by adding a new tracked file.
            var source = LucidViewProvisioner.SourcePathFor(repoRoot);
            File.WriteAllText(Path.Combine(source, "NEW.txt"), "added");
            Run(source, "add -A");
            Run(source, "commit -m second");

            Assert.Equal(LucidViewProvisionStatus.Provisioned, (await LucidViewProvisioner.EnsureAsync(repoRoot)).Status);
            var dest = LucidViewProvisioner.DestPathFor(repoRoot);
            Assert.True(File.Exists(Path.Combine(dest, "NEW.txt")), "re-provision must pick up the new HEAD");
        }
        finally { TryDelete(baseDir); }
    }

    [Fact]
    public async Task Missing_source_reports_SourceMissing_without_throwing()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "lucidprov-" + Guid.NewGuid().ToString("N"));
        var repoRoot = Path.Combine(baseDir, "app");
        Directory.CreateDirectory(repoRoot);   // no sibling lucidview
        try
        {
            var result = await LucidViewProvisioner.EnsureAsync(repoRoot);
            Assert.Equal(LucidViewProvisionStatus.SourceMissing, result.Status);
            Assert.False(result.Ok);
            Assert.False(Directory.Exists(LucidViewProvisioner.DestPathFor(repoRoot)), "no checkout should be created when the source is missing");
        }
        finally { TryDelete(baseDir); }
    }

    [Fact]
    public void ParseInitialisedSubmodulePaths_keeps_initialised_skips_uninitialised()
    {
        // A leading space / '+' / 'U' means initialised; '-' means uninitialised (no checkout to export).
        const string output =
            " 114070ab2cb5b3216ccedb60109639c2305a9cf4 external/LiveMarkdown.Avalonia (v1.9.2-2-g114070a)\n" +
            " 54525b13f838af25595a4647141cacb12e2b061d external/lucidRESUME (uitesting-v1.4.3)\n" +
            "-92341f2126d32d2483a3eda0c8c50a34d47c1483 external/lucidRESUME/lib/lucidrag\n" +
            "+abc0000000000000000000000000000000000000 external/changed (heads/x)\n";

        var paths = LucidViewProvisioner.ParseInitialisedSubmodulePaths(output).ToList();

        var expected = new List<string> { "external/LiveMarkdown.Avalonia", "external/lucidRESUME", "external/changed" };
        Assert.Equal(expected, paths);
    }

    [Fact]
    public async Task Provisions_submodule_contents_not_just_the_gitlink()
    {
        if (!GitAvailable()) return;
        var baseDir = Path.Combine(Path.GetTempPath(), "lucidprov-" + Guid.NewGuid().ToString("N"));
        var repoRoot = Path.Combine(baseDir, "app");
        Directory.CreateDirectory(repoRoot);
        try
        {
            var source = LucidViewProvisioner.SourcePathFor(repoRoot);
            // A submodule repo the superproject will embed under external/dep.
            var subRepo = Path.Combine(baseDir, "dep-origin");
            Directory.CreateDirectory(subRepo);
            Run(subRepo, "init -b main"); Run(subRepo, "config user.email t@t.t"); Run(subRepo, "config user.name t");
            File.WriteAllText(Path.Combine(subRepo, "SubFile.cs"), "// submodule content");
            Run(subRepo, "add -A"); Run(subRepo, "commit -m sub");

            Directory.CreateDirectory(source);
            Run(source, "init -b main"); Run(source, "config user.email t@t.t"); Run(source, "config user.name t");
            File.WriteAllText(Path.Combine(source, "Root.cs"), "// super");
            Run(source, "-c protocol.file.allow=always submodule add \"" + subRepo + "\" external/dep");
            Run(source, "add -A");   // stage Root.cs too (submodule add only stages .gitmodules + gitlink)
            Run(source, "commit -m super-with-sub");

            var result = await LucidViewProvisioner.EnsureAsync(repoRoot);
            Assert.Equal(LucidViewProvisionStatus.Provisioned, result.Status);

            var dest = LucidViewProvisioner.DestPathFor(repoRoot);
            Assert.True(File.Exists(Path.Combine(dest, "Root.cs")), "superproject file present");
            Assert.True(File.Exists(Path.Combine(dest, "external", "dep", "SubFile.cs")),
                "submodule contents must be materialised, not just the empty gitlink");
        }
        finally { TryDelete(baseDir); }
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }
}
