using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Styloagent.Core.Git;

namespace Styloagent.Git;

/// <summary>Runs the test command through the platform shell, capturing combined output.</summary>
public sealed class ProcessTestRunner : ITestRunner
{
    public async Task<TestOutcome> RunAsync(string workingDir, string command, CancellationToken ct = default)
    {
        try
        {
            var (shell, flag) = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? ("cmd.exe", "/c")
                : ("/bin/sh", "-c");

            var psi = new ProcessStartInfo(shell)
            {
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add(flag);
            psi.ArgumentList.Add(command);

            using var proc = Process.Start(psi);
            if (proc is null) return new TestOutcome(false, "failed to start test process");

            var sb = new StringBuilder();
            sb.Append(await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false));
            sb.Append(await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false));
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            return new TestOutcome(proc.ExitCode == 0, sb.ToString());
        }
        catch (Exception ex)
        {
            return new TestOutcome(false, ex.Message);
        }
    }
}
