# Using the Vixen MCP server

`vixen-mcp` exposes the engine's API graph — 3 679 public types, their doc comments, the
kind-specific facts, the written guide and the release tables — as MCP tools. It reads what
`nuke Docs` emitted from the engine's own source, so the answers match the checkout rather than a
scraped web page, and it covers every type rather than the handful anybody has written prose about.

## Configuration

Inside a Vixen checkout, after `./build.sh Docs`:

```json
{
  "mcpServers": {
    "vixen": { "command": "node", "args": ["www/mcp/server.mjs"] }
  }
}
```

Published:

```json
{
  "mcpServers": {
    "vixen": { "command": "npx", "args": ["-y", "vixen-mcp"] }
  }
}
```

## Where its answers come from

The server looks for `graph.json` in this order and stops at the first hit: `--docs <dir>`, then
`VIXEN_DOCS_DIR`, then `data/` beside the server (what a published package carries), then
`artifacts/docs` in the checkout it is running from. A published package's data is the graph of the
release it was published from; a checkout's is whatever `nuke Docs` last produced.

`node www/mcp/server.mjs --self-test` prints what it found and exits — the fastest way to tell
whether a configuration is pointing at anything.

## The tools

| Tool | Answers |
|---|---|
| `vixen_meta` | What this index is: commit, configuration, counts per kind, guide pages, archived versions. **Call it first** — the counts are the shape of the engine |
| `vixen_search` | Name, summary or **kind**. `kind="scene-component"` is "what can a scene put on an entity"; `kind="system"` is "what runs in the frame". Ranks an exact name first, then by how much of the engine uses it |
| `vixen_symbol_get` | One symbol in full: signature, doc comment, members, base and interfaces, the kind-specific facets, what uses it, its guide page, and a GitHub link at the documented commit |
| `vixen_guide_get` | A written page as markdown — what it is, what it is for, how to use it, examples, what next |
| `vixen_examples` | Fenced examples, with whether the build compiles them. A compiled block is checked against the engine on every CI run; an exempt one carries its reason |
| `vixen_diff` | A release's table: added, removed, deprecated, and the four kinds of breaking change |

## The queries worth knowing

- **"What does this engine offer?"** → `vixen_meta`, then `vixen_search` with a `kind` for each row
  of the taxonomy that interests you.
- **"Is there already a component for X?"** → `vixen_search { query: "X", kind: "scene-component" }`.
  Guessing costs more than asking: there are 3 679 types and the naming is not always the obvious one.
- **"How big is this component?"** → `vixen_symbol_get` → `facets.SizeBytes` and
  `facets.EntitiesPerChunk`. That number is why a component is a struct of primitives.
- **"When does this system run, and what does it touch?"** → `vixen_symbol_get` → `facets.Phase`,
  `facets.Reads`, `facets.Writes`, `facets.RunsBefore`, `facets.RunsAfter`.
- **"Can I copy this example?"** → `vixen_examples { compiledOnly: true }`. Everything it returns
  compiled against this engine on the last CI run.
- **"Is upgrading safe?"** → `vixen_diff { breakingOnly: true }`. Read the `engine-break` rows first:
  their signatures are identical and they are the ones that break a saved scene or a frame.
