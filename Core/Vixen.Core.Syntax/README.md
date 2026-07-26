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
