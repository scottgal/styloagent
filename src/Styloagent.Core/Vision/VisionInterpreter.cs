using System.Diagnostics;

namespace Styloagent.Core.Vision;

/// <summary>
/// Describes an image in words, so an agent whose own model has no vision (kilo, claude-deepseek) can still
/// "look at" a screenshot. Shells out to the codex CLI, which accepts image attachments, and pins the
/// lightest model that still reads UI detail reliably.
///
/// This replaces a local Ollama vision path (gemma4 on a LAN box): local was too slow to use repeatedly and
/// resolved screenshots too coarsely to read dashboard labels. Measured on a real 1100x640 cockpit capture,
/// <see cref="Model"/> at <see cref="Effort"/> returned every tab label and the agent name correctly in ~5s.
///
/// Deliberately does NOT render pages: the project already governs Playwright capture through the browser
/// broker, so callers compose an existing screenshot tool with this one rather than growing a second renderer.
/// </summary>
public static class VisionInterpreter
{
    /// <summary>Lightest codex model that still reads screenshot detail accurately.</summary>
    public const string Model = "gpt-5.6-luna";

    /// <summary>Interpretation is description, not reasoning — the lowest effort is the useful one.</summary>
    public const string Effort = "low";

    /// <summary>How long a single interpretation may take before it is abandoned.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The codex argv for one interpretation. Pure and separately tested because a wrong flag here fails
    /// only at runtime, inside a spawned process, where the error is easy to misread as "vision is broken".
    /// </summary>
    public static List<string> BuildArgs(string imagePath, string question, string outputPath)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) throw new ArgumentException("image path required", nameof(imagePath));
        if (string.IsNullOrWhiteSpace(question)) throw new ArgumentException("question required", nameof(question));
        // The question reaches us from an agent, whose context may contain text it read from elsewhere.
        // Verified against codex 0.147: a bare "--help" prompt prints help instead of reading the image.
        if (question.TrimStart().StartsWith('-'))
            throw new ArgumentException("question must not start with '-'", nameof(question));

        return
        [
            "exec",
            "-m", Model,
            "-c", $"model_reasoning_effort={Effort}",
            "--image", imagePath,
            "--sandbox", "read-only",
            "--skip-git-repo-check",
            "--output-last-message", outputPath,
            "--", // End of options: everything after this is the prompt, never a flag.
            question,
        ];
    }

    /// <summary>
    /// Answers <paramref name="question"/> about <paramref name="imagePath"/>. Best-effort: any failure comes
    /// back as a readable message rather than an exception, because the caller is an agent mid-turn.
    /// </summary>
    public static async Task<string> InterpretAsync(
        string imagePath, string question, CancellationToken ct = default)
    {
        if (!File.Exists(imagePath)) return $"vision failed: no such image '{imagePath}'";

        var outputPath = Path.Combine(Path.GetTempPath(), $"styloagent-vision-{Guid.NewGuid():N}.txt");
        try
        {
            var psi = new ProcessStartInfo("codex")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                // Never inherit the caller's cwd: a repo-rooted cwd invites codex to treat this as a code task.
                WorkingDirectory = Path.GetTempPath(),
            };
            foreach (var arg in BuildArgs(imagePath, question, outputPath)) psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi);
            if (process is null) return "vision failed: could not start the codex CLI";

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return $"vision failed: timed out after {Timeout.TotalSeconds:N0}s";
            }

            // The pretty transcript interleaves banner and token counts; the message file is the answer.
            if (File.Exists(outputPath))
            {
                var answer = (await File.ReadAllTextAsync(outputPath, ct).ConfigureAwait(false)).Trim();
                if (answer.Length > 0) return answer;
            }

            var stderr = (await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false)).Trim();
            return $"vision failed: codex produced no answer{(stderr.Length > 0 ? $" — {stderr}" : "")}";
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return "vision failed: the codex CLI is not installed or not on PATH";
        }
        finally
        {
            try { File.Delete(outputPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
