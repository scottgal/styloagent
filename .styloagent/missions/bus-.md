Remove Kilo from Styloagent's fleet/MCP capability surface and prove explicit Codex Luna medium selection is valid.

Operator requirements:
- Kilo integration must be removed.
- No spawn path may silently map Codex to dead model gpt-5.
- runtime=codex, model=gpt-5.6-luna, effort=medium must validate and preserve those exact values. Do not launch a Luna agent merely to test.

Scope:
- Own bus/MCP files per .styloagent/ownership.yaml plus cross-cutting capability/model records that are unowned.
- Remove Kilo from agent_capabilities output, spawn_agent accepted runtimes/descriptions, fallback catalogs, runtime parsing/manifest serialization where coherent with your seam.
- Ensure Codex capabilities remain live-discovered and explicit model+effort values survive spawn validation unchanged.
- Remove or rewrite focused tests that assert Kilo support; add regression tests for Kilo rejection and Luna+medium acceptance.
- Do not edit session- or cockpit-owned files. Report exact cross-domain removals needed.
- Run focused tests, then relevant broader tests.
- Commit by explicit pathspec and report SHA plus remaining work to overview-.