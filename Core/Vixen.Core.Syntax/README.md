# Vixen.Core.Syntax

Language-agnostic syntax tree infrastructure: the green/red tree Roslyn popularised,
source text with span and line mapping, and a diagnostics model — without a language
attached.

Vixen has three front ends (the Raven shading language, and the VXML and VCSS markup
languages behind its UI framework). This package is what they share, so they are three
grammars over one tested tree rather than three trees.

## What is here

| | |
|---|---|
| `GreenNode` | Immutable, position-independent, width-caching nodes. Identical subtrees can be shared between trees. |
| `SyntaxNode` | The public *red* overlay: adds parent and absolute position, realised lazily and cached, so navigation is cheap and identity-stable. |
| `SyntaxToken`, `SyntaxTrivia` | Terminals with full trivia fidelity — a parsed tree round-trips to its source byte for byte. |
| `SyntaxList<T>`, `SeparatedSyntaxList<T>` | Typed views over runs of siblings. A one-element list allocates no wrapper. |
| `SourceText`, `TextSpan`, `LinePosition` | Offsets, spans, and the line index that turns them into line/column. |
| `Diagnostic`, `DiagnosticBag`, `Location` | One diagnostics model for every front end, so an error list has one implementation. |
| `SlidingTextWindow`, `SyntaxParser`, `Blender` | What a hand-written front end is built out of: a character window, a token stream with lookahead and mark/reset, and node reuse for an incremental reparse. |

## Navigating a tree

Everything an editor asks of a parse tree is here rather than in a language, because all
three front ends ask the same two questions:

| | |
|---|---|
| `FindToken(position)` | The token under a caret. Answers for *every* position in the file, trivia included — a caret in a comment belongs to the token that comment leads. |
| `FindNode(span)` | The innermost node covering a selection: what a refactoring acts on, and what maps a span of generated source back to the construct that produced it. |
| `ChildNodes`, `ChildTokens`, `DescendantNodes`, `DescendantTokens`, … | Traversal with list nodes flattened away — a list is a shape of the tree, not a construct of the language. `ChildNodesAndTokens` remains the raw slot walk. |
| `Ancestors`, `FirstAncestorOrSelf<T>`, `Contains` | Upward navigation: which declaration is the caret in. |
| `SyntaxToken.LeadingTrivia` / `TrailingTrivia` | The whitespace and comments the parser set aside, positioned, for highlighting and formatting. |
| `SyntaxToken.IsMissing` | Whether the parser fabricated this token during recovery — what tells a squiggle which tokens the author actually wrote. |
| `IsEquivalentTo` | Same structure and same token text, ignoring trivia. Reformatting does not change what a tree says. |

## Adding a language

Kinds are plain integers here (`RawKind`); each language brings its own enum and projects
it back with a cast. Two rules follow from that:

- **Reserve the list kind.** The tree builds anonymous list nodes without knowing your
  enum, so your list member must equal `SyntaxKinds.List` — otherwise casting a list
  node's `RawKind` names the wrong member. Ask `GreenNode.IsList`, never compare kinds.
- **`Accept` is yours, not ours.** A generated `Accept` calls
  `visitor.VisitYourNodeType(this)`, so its parameter must be your visitor type.
  `SyntaxNode` therefore declares no `Accept`; derive a base node that does, exactly as
  Roslyn's `CSharpSyntaxNode` does over `SyntaxNode`.

Node classes are generated from a `Syntax.xml` model by `Vixen.Core.Syntax.Generator`,
which reads your output namespace and base node from the `<Tree>` element.

Licensed under Apache-2.0.
