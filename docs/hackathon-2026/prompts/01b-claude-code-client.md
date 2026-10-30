# Prompt 01b: change to step 4 of prompt 01 (Claude client)

Paste into the build session. It replaces step 4 of prompt 01.

---

Change of plan for step 4, from the organisers at kickoff: **there is no Anthropic API key.** The
app's own AI calls go through our Claude subscription by running **Claude Code headless**. Re-read
`CLAUDE.md` (section "The agents' Claude calls", just updated) and pull the latest commit.

- **Don't use the Anthropic SDK or an API key.** If you've already added them, remove them.
- Build `ClaudeCodeClient` in `PricingTool.Agent`: runs `claude -p --output-format json` as a
  subprocess, prompt on stdin, and reads `result` (plus `total_cost_usd`, `usage`, `duration_ms`,
  `num_turns`) from the JSON.
  - Per call: model (`opus` for Alpha and Nova, `sonnet` for the run checker), the agent's fixed
    rules as a system prompt file, an optional `--json-schema`, `--tools ""`, `--no-session-persistence`,
    `--max-budget-usd`, a timeout, and optional `--strict-mcp-config --mcp-config` for our tools later.
  - Run it from a runtime folder outside the repo. **Prove isolation** with a test call: the agent
    must not see this repo's `CLAUDE.md` or the user's own instructions and memory (try `--bare`
    first; if `--bare` can't use the subscription login, use `--setting-sources` plus the runtime
    folder). Write down which flags worked.
  - Validate the JSON against the schema in our code; on failure retry once, then return a typed
    failure (the caller holds or blocks; it never publishes).
  - `AiCallLog` row for every call, success or failure, with the fields above. Never log the prompt's
    secrets or the token (there shouldn't be any in a prompt anyway).
- `claude --version` and `claude --help` on this machine: Claude Code 2.1.205 at
  `C:\Users\Admin\.local\bin\claude`. The machine is already logged in to the subscription.
- Tests: unit tests against a fake process runner; one integration test, skipped unless
  `RUN_CLAUDE_CODE_TESTS=1`, that makes a real tiny call and checks the log row.
- Keep calls rare: one real call to prove it works is enough tonight.

Done when: a real headless call returns parsed JSON, isolation is proven, the call is in
`AiCallLog`, and `dotnet test` is green.
