<!--
SPDX-FileCopyrightText: Copyright (c) Rikarin
SPDX-License-Identifier: Apache-2.0
-->

# vixen-mcp

The Vixen engine's documentation graph as MCP tools — docs/plan/25 § Part 10.

The graph is one artefact with three consumers: [the site](../README.md), the build's gates, and
this. Nothing here reads the engine's source or the published site; it reads exactly what
`nuke Docs` emitted, so **the answers match the checkout rather than whatever a docs scraper last
saw** — the argument `@xui/mcp` already proved.

```bash
./build.sh Docs                    # produces artifacts/docs
node www/mcp/server.mjs --self-test
```

```json
{
  "mcpServers": {
    "vixen": { "command": "node", "args": ["www/mcp/server.mjs"] }
  }
}
```

| Tool | Answers |
|---|---|
| `vixen_meta` | Commit, configuration, counts per kind, guide pages, archived versions |
| `vixen_search` | By name, summary or **kind** — `kind="system"` is "what runs in the frame" |
| `vixen_symbol_get` | Signature, doc comment, members, facets, users, guide page, source link |
| `vixen_guide_get` | A written page, as markdown |
| `vixen_examples` | Fenced examples, and whether the build compiles them |
| `vixen_diff` | A release's table of added, removed, deprecated and breaking |

## Why an agent needs this and a search box will not do

An engine with **3 679 public types** is exactly the case where guessing a type name is the failure
mode rather than a shortcut. Two of the tools exist for that specifically:

- `vixen_search` filters by the [taxonomy](../../docs/plan/25-documentation-generator-and-site.md#23-the-taxonomy--the-opinionated-half),
  so a question can be a *shape* rather than a name — "what can a scene put on an entity" is
  `kind="scene-component"`, and it is answerable without knowing a single identifier.
- `vixen_symbol_get` returns the **kind-specific facts** a signature does not carry: a component's
  size in bytes and rows per chunk, a system's phase and declared access, a shader's descriptor sets.
  Those are what decide whether an answer is right, and no amount of reading the signature reveals them.

## Where the graph comes from

Resolved in this order, first hit wins:

1. `--docs <dir>`
2. `VIXEN_DOCS_DIR`
3. `data/` beside the server — what `pnpm pack` copies in, so a published package is self-contained
4. `../../artifacts/docs` — the checkout this file is in

If none of them has a `graph.json`, the server says so and exits 2 rather than starting and
answering everything with nothing.

## Notes

- The index is FlexSearch, built at startup from the index tier — about a second for 3 679 types
  plus the guide, and no dependency on the site's build.
- The page tier is read one namespace chunk at a time and cached, so `vixen_symbol_get` costs one
  small file rather than the 26 MB the whole graph weighs.
- `vixen_examples` reads the fences out of the guide bodies rather than storing a second copy of
  them, because a second copy is a second thing that can drift.

Licensed under Apache-2.0.
