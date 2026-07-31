<!--
SPDX-FileCopyrightText: Copyright (c) Rikarin
SPDX-License-Identifier: Apache-2.0
-->

# App-local components

Most of what this site is made of comes from `@xui/*`. Three things do not, because the packages are
being written — [docs/plan/25 § Part 9](../../../../docs/plan/25-documentation-generator-and-site.md#part-9--what-xui-needs)
specifies them as **X1**, **X2** and **X3**:

| Here | Becomes | Delete when |
|---|---|---|
| [`code-block.ts`](code-block.ts) | `@xui/code-block` (X1) | The package accepts `tokens: Token[][]` — pre-classified runs, which is the requirement this site has and the reason X1 is specified at all |
| [`prose.ts`](prose.ts) | `@xui/prose` (X2) | The package styles a markdown render against xUI's semantic tokens |
| [`table-of-contents.ts`](table-of-contents.ts) | `@xui/toc` (X3) | The package has scroll-spy |

⚠ **These are stand-ins, not prototypes.** They do the least that makes the page work, deliberately:
anything more here is work that has to be thrown away when the package lands, and a divergence to
reconcile in the meantime. Each carries the same class names and inputs the specification names, so
the swap is an import change.

Everything else — badges, breadcrumbs, signatures, facet panels — is this site's own and stays.
