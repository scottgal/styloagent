using System.Text.Json;

namespace Styloagent.Core.Hooks;

/// <summary>OpenCode v2 pre-execution ownership gate; uses argv arrays, never shell interpolation.</summary>
public static class OpenCodeOwnershipPlugin
{
    public static string Source(string prefix, string repo, IReadOnlyList<string> command) => """
        import { spawn } from 'node:child_process';
        import path from 'node:path';
        import { realpathSync } from 'node:fs';
        const config =
        """ + JsonSerializer.Serialize(new { prefix, repo, command }) + ";\n" + """
        export default {
          id: 'styloagent.ownership',
          async setup(ctx) {
            const repo = realpathSync(config.repo);
            await ctx.permission.hook('evaluate', async (event) => {
              if (event.action !== 'edit') return;
              for (const resource of event.resources) {
                const file = path.resolve(ctx.location.directory, resource);
                const reason = await new Promise((resolve) => {
                  let output = '';
                  const child = spawn(config.command[0], [...config.command.slice(1), '--owner-gate', '--caller', config.prefix, '--root', repo], { stdio: ['pipe', 'pipe', 'ignore'] });
                  const timer = setTimeout(() => { child.kill(); resolve(null); }, 10000);
                  child.on('error', () => { clearTimeout(timer); resolve(null); });
                  child.stdout.on('data', d => output += d.toString());
                  child.on('close', () => {
                    clearTimeout(timer);
                    try { resolve(JSON.parse(output).hookSpecificOutput?.permissionDecisionReason ?? null); }
                    catch { resolve(null); }
                  });
                  child.stdin.on('error', () => {});
                  child.stdin.end(JSON.stringify({ hook_event_name: 'PreToolUse', tool_name: 'Write', tool_input: { file_path: file } }));
                });
                if (reason) { event.effect = 'deny'; event.message = reason; return; }
              }
            });
          }
        };
        """;
}
