---
title: The Raven syntax tree
slug: raven/syntax
kind: guide
area: Raven
summary: What parsing produces — a Roslyn-shaped tree generated from one grammar file, why the node classes are not written by hand, how an edit reparses only the members it touched, and the two traps that come with reuse and with a language whose newline ends a statement.
api: [T:Vixen.Raven.Syntax.SyntaxTree, T:Vixen.Raven.Syntax.SyntaxKind, T:Vixen.Raven.Syntax.RavenSyntaxNode, T:Vixen.Raven.Syntax.RavenSyntaxExtensions, T:Vixen.Raven.Syntax.SyntaxFactory, T:Vixen.Raven.Syntax.SyntaxFacts, T:Vixen.Raven.Syntax.SyntaxVisitor, T:Vixen.Raven.Syntax.SyntaxVisitor`1, T:Vixen.Raven.Syntax.SyntaxRewriter, T:Vixen.Raven.Syntax.CompilationUnitSyntax, T:Vixen.Raven.Syntax.PackageDirectiveSyntax, T:Vixen.Raven.Syntax.ImportDirectiveSyntax, T:Vixen.Raven.Syntax.MemberDeclarationSyntax, T:Vixen.Raven.Syntax.ShaderDeclarationSyntax, T:Vixen.Raven.Syntax.StructDeclarationSyntax, T:Vixen.Raven.Syntax.ProtocolDeclarationSyntax, T:Vixen.Raven.Syntax.EnumDeclarationSyntax, T:Vixen.Raven.Syntax.EnumMemberDeclarationSyntax, T:Vixen.Raven.Syntax.MethodDeclarationSyntax, T:Vixen.Raven.Syntax.ConstructorDeclarationSyntax, T:Vixen.Raven.Syntax.OperatorDeclarationSyntax, T:Vixen.Raven.Syntax.PropertyDeclarationSyntax, T:Vixen.Raven.Syntax.AccessorDeclarationSyntax, T:Vixen.Raven.Syntax.AccessorListSyntax, T:Vixen.Raven.Syntax.BaseFieldDeclarationSyntax, T:Vixen.Raven.Syntax.FieldDeclarationSyntax, T:Vixen.Raven.Syntax.VariableDeclarationSyntax, T:Vixen.Raven.Syntax.EqualsValueClauseSyntax, T:Vixen.Raven.Syntax.ArrowExpressionClauseSyntax, T:Vixen.Raven.Syntax.ParameterListSyntax, T:Vixen.Raven.Syntax.ParameterSyntax, T:Vixen.Raven.Syntax.TypeParameterListSyntax, T:Vixen.Raven.Syntax.TypeParameterSyntax, T:Vixen.Raven.Syntax.TypeParameterConstraintClauseSyntax, T:Vixen.Raven.Syntax.TypeParameterConstraintSyntax, T:Vixen.Raven.Syntax.TypeConstraintSyntax, T:Vixen.Raven.Syntax.DefaultConstraintSyntax, T:Vixen.Raven.Syntax.BaseListSyntax, T:Vixen.Raven.Syntax.BaseTypeSyntax, T:Vixen.Raven.Syntax.SimpleBaseTypeSyntax, T:Vixen.Raven.Syntax.AttributeListSyntax, T:Vixen.Raven.Syntax.AttributeSyntax, T:Vixen.Raven.Syntax.AttributeArgumentListSyntax, T:Vixen.Raven.Syntax.AttributeArgumentSyntax, T:Vixen.Raven.Syntax.NameSyntax, T:Vixen.Raven.Syntax.SimpleNameSyntax, T:Vixen.Raven.Syntax.IdentifierNameSyntax, T:Vixen.Raven.Syntax.GenericNameSyntax, T:Vixen.Raven.Syntax.QualifiedNameSyntax, T:Vixen.Raven.Syntax.NameColonSyntax, T:Vixen.Raven.Syntax.TypeSyntax, T:Vixen.Raven.Syntax.PredefinedTypeSyntax, T:Vixen.Raven.Syntax.ArrayTypeSyntax, T:Vixen.Raven.Syntax.ArrayRankSpecifierSyntax, T:Vixen.Raven.Syntax.TupleTypeSyntax, T:Vixen.Raven.Syntax.TupleElementSyntax, T:Vixen.Raven.Syntax.TypeArgumentListSyntax, T:Vixen.Raven.Syntax.ExpressionSyntax, T:Vixen.Raven.Syntax.LiteralExpressionSyntax, T:Vixen.Raven.Syntax.BinaryExpressionSyntax, T:Vixen.Raven.Syntax.PrefixUnaryExpressionSyntax, T:Vixen.Raven.Syntax.PostfixUnaryExpressionSyntax, T:Vixen.Raven.Syntax.AssignmentExpressionSyntax, T:Vixen.Raven.Syntax.ConditionalExpressionSyntax, T:Vixen.Raven.Syntax.CastExpressionSyntax, T:Vixen.Raven.Syntax.DefaultExpressionSyntax, T:Vixen.Raven.Syntax.ParenthesizedExpressionSyntax, T:Vixen.Raven.Syntax.InvocationExpressionSyntax, T:Vixen.Raven.Syntax.ElementAccessExpressionSyntax, T:Vixen.Raven.Syntax.MemberAccessExpressionSyntax, T:Vixen.Raven.Syntax.RangeExpressionSyntax, T:Vixen.Raven.Syntax.TupleExpressionSyntax, T:Vixen.Raven.Syntax.CollectionExpressionSyntax, T:Vixen.Raven.Syntax.CollectionElementSyntax, T:Vixen.Raven.Syntax.ExpressionElementSyntax, T:Vixen.Raven.Syntax.SpreadElementSyntax, T:Vixen.Raven.Syntax.InstanceExpressionSyntax, T:Vixen.Raven.Syntax.SelfExpressionSyntax, T:Vixen.Raven.Syntax.BaseExpressionSyntax, T:Vixen.Raven.Syntax.BaseArgumentListSyntax, T:Vixen.Raven.Syntax.ArgumentListSyntax, T:Vixen.Raven.Syntax.BracketedArgumentListSyntax, T:Vixen.Raven.Syntax.ArgumentSyntax, T:Vixen.Raven.Syntax.StatementSyntax, T:Vixen.Raven.Syntax.BlockSyntax, T:Vixen.Raven.Syntax.LocalDeclarationStatementSyntax, T:Vixen.Raven.Syntax.ExpressionStatementSyntax, T:Vixen.Raven.Syntax.EmptyStatementSyntax, T:Vixen.Raven.Syntax.DiscardStatementSyntax, T:Vixen.Raven.Syntax.IfStatementSyntax, T:Vixen.Raven.Syntax.ElseClauseSyntax, T:Vixen.Raven.Syntax.ForStatementSyntax, T:Vixen.Raven.Syntax.WhileStatementSyntax, T:Vixen.Raven.Syntax.RepeatStatementSyntax, T:Vixen.Raven.Syntax.SwitchStatementSyntax, T:Vixen.Raven.Syntax.SwitchSectionSyntax, T:Vixen.Raven.Syntax.SwitchLabelSyntax, T:Vixen.Raven.Syntax.CaseSwitchLabelSyntax, T:Vixen.Raven.Syntax.DefaultSwitchLabelSyntax, T:Vixen.Raven.Syntax.BreakStatementSyntax, T:Vixen.Raven.Syntax.ContinueStatementSyntax, T:Vixen.Raven.Syntax.ReturnStatementSyntax]
tags: [raven, shaders, compiler, tooling]
since: 0.1
status: preview
related: [raven/compiling-a-shader, raven/symbols, raven/ir]
---

## What it is

`Vixen.Raven.Syntax` is what parsing produces: a lossless, Roslyn-shaped tree over a `.rvn` file.
`SyntaxTree.ParseText` builds one, `SyntaxTree.GetRoot` hands back its `CompilationUnitSyntax`, and
`SyntaxTree.Diagnostics` is what the lexer and parser had to say about it.

**Almost none of the node classes are written by hand.** `Syntax.xml` beside them is the grammar —
one `<Node>` per production, with its fields, its `SyntaxKind` and its documentation — and the node
classes, the visitor's methods, the rewriter's methods and `SyntaxFactory`'s builders are generated
from it. That is why this namespace is a hundred types and one idea.

A compilation unit holds an optional `PackageDirectiveSyntax`, its `ImportDirectiveSyntax` list, and
its members. A `MemberDeclarationSyntax` is a `ShaderDeclarationSyntax`, `StructDeclarationSyntax`,
`ProtocolDeclarationSyntax` or `EnumDeclarationSyntax` at the top, and inside one of those a
`MethodDeclarationSyntax`, `ConstructorDeclarationSyntax`, `OperatorDeclarationSyntax`,
`PropertyDeclarationSyntax` with its `AccessorListSyntax` of `AccessorDeclarationSyntax`, or a
`FieldDeclarationSyntax` over a `VariableDeclarationSyntax`. ⚠ `EnumMemberDeclarationSyntax` is a
`MemberDeclarationSyntax` too, and that turns out to matter — see *Using it*.

Around a declaration sit its clauses: `AttributeListSyntax` of `AttributeSyntax` with
`AttributeArgumentListSyntax`; `ParameterListSyntax` of `ParameterSyntax`; `TypeParameterListSyntax`
of `TypeParameterSyntax` with `TypeParameterConstraintClauseSyntax` over `TypeConstraintSyntax` and
`DefaultConstraintSyntax`; `BaseListSyntax` of `SimpleBaseTypeSyntax`; `EqualsValueClauseSyntax` for
an initialiser and `ArrowExpressionClauseSyntax` for an expression body.

Names are `IdentifierNameSyntax`, `GenericNameSyntax` with its `TypeArgumentListSyntax`, and
`QualifiedNameSyntax`; `NameColonSyntax` is the `label:` on a named argument or tuple element. Types
are `PredefinedTypeSyntax`, `ArrayTypeSyntax` with its `ArrayRankSpecifierSyntax`, `TupleTypeSyntax`
of `TupleElementSyntax`, and any `NameSyntax`.

Expressions are the usual set — `LiteralExpressionSyntax`, `BinaryExpressionSyntax`,
`PrefixUnaryExpressionSyntax` and `PostfixUnaryExpressionSyntax`, `AssignmentExpressionSyntax`,
`ConditionalExpressionSyntax`, `CastExpressionSyntax`, `DefaultExpressionSyntax`,
`ParenthesizedExpressionSyntax`, `InvocationExpressionSyntax` and `ElementAccessExpressionSyntax`
over an `ArgumentListSyntax` or `BracketedArgumentListSyntax` of `ArgumentSyntax`,
`MemberAccessExpressionSyntax`, `RangeExpressionSyntax`, `TupleExpressionSyntax`,
`CollectionExpressionSyntax` of `ExpressionElementSyntax` and `SpreadElementSyntax`, and the two
instance expressions `SelfExpressionSyntax` and `BaseExpressionSyntax`.

Statements are `BlockSyntax`, `LocalDeclarationStatementSyntax`, `ExpressionStatementSyntax`,
`EmptyStatementSyntax`, `DiscardStatementSyntax`, `IfStatementSyntax` with its `ElseClauseSyntax`,
`ForStatementSyntax`, `WhileStatementSyntax`, `RepeatStatementSyntax`, `SwitchStatementSyntax` over
`SwitchSectionSyntax` and its `CaseSwitchLabelSyntax`/`DefaultSwitchLabelSyntax`, and
`BreakStatementSyntax`, `ContinueStatementSyntax` and `ReturnStatementSyntax`.

The machinery around the nodes is small: `RavenSyntaxNode` is the base every generated node derives
from, `SyntaxKind` names every node and token, `SyntaxFacts.GetText` gives a fixed-text token its
canonical spelling, `SyntaxFactory` builds nodes, `SyntaxVisitor` and `SyntaxVisitor<TResult>` walk
them, `SyntaxRewriter` rebuilds them, and `RavenSyntaxExtensions` gives a shared `SyntaxToken` the
same `Kind` a Raven node has.

## What it is for

**It is the only model that still knows what the author typed.** The tree is lossless: every token,
every space and every comment is in it, so a formatter, an editor and a diagnostic with a squiggle
all work from this and nothing else. Binding throws that away — a
[symbol](symbols.md) is what a name *means*, not where it was written — and lowering throws away more
still.

**The nodes are generated because a hand-written node is a place for the tree and the grammar to
disagree.** `Syntax.xml` is the single declaration of a production's fields, their types, their kinds
and their prose, so adding a construct is an edit to the grammar rather than an edit to seven files
that have to stay in step. The generated `Accept` bodies are also why `RavenSyntaxNode` exists at all
rather than the shared `SyntaxNode`: a body calls `visitor.VisitIdentifierName(this)`, so it needs
*Raven's* visitor type, which the shared tree cannot know about. It is the same split Roslyn makes
between `SyntaxNode` and `CSharpSyntaxNode`.

**The tree is shared with the rest of the engine below `RavenSyntaxNode`.** `Vixen.Core.Syntax`
supplies the red/green machinery, `SyntaxToken` and the list node, and VXML's front end uses the same
one. So `SyntaxToken` does *not* derive from `RavenSyntaxNode`, carries no `Accept`, and is routed by
`SyntaxVisitor.Visit`'s single type test rather than by an override on every generated node.

## Using it

⚠ **A newline ends a statement, so the tree is not the one C# would have built.** `x = x` on one line
and `+ y` on the next is **two** statements and the second is discarded — a defect that shipped in
real shaders. Trailing the operator instead is `RVN1001`; this arrangement was not caught by
anything. When a tree does not look like the source, count the statements before doubting the parser.

**`WithChangedText` is the incremental path, and it is what a hot reload calls.** An editor holds the
previous `SourceText`, applies its changes and hands the result over; member declarations whose text
the edit did not touch are taken from the old tree's green nodes rather than reparsed, so editing one
function body reparses that member and shifts the rest. A change range that is not against the
immediate predecessor text conservatively reports the whole document and parses fresh.

⚠ **Only a member whose own parse reported nothing may be reused, and leaving that gate out is
silent.** A reused subtree keeps its nodes and loses its *diagnostics*, because those came from the
parse that is not being run again — so offering a member that reported an error yields a tree that
still contains the broken construct and a diagnostic list that no longer mentions it. An author
editing one function would watch the errors elsewhere in the file disappear, and a hot reload would
bind a tree with fabricated tokens in it while reporting no syntax error to explain them. `Vixen.Fuzz`'s
`raven` target found this thirty-two ways in four hundred cases; `IncrementalParseTests` did not,
because every shader it edits parses cleanly and zero equals zero.

⚠ **A reuse candidate is tagged with the loop that parsed it, because `MemberDeclarationSyntax` is
the base of two grammars.** `EnumMemberDeclarationSyntax` derives from it, but only an enum's
comma-separated loop parses one — never the member-list loop both reuse sites run. An untagged enum
member is therefore within reach of a member list, and an edit only has to dissolve the `enum` header
above it: the members' text is untouched, lexes identically, and now begins at a member boundary, so
neither the span test nor the token comparison objects. What a full parse does with those characters
is skip them.

**A rewriter returns the original node to mean "unchanged".** `SyntaxRewriter.VisitList` starts
copying only once something differs, so an untouched subtree keeps its identity and a caller can use
a reference comparison to detect "no edit". ⚠ And a rewriter may not *remove* a node from a list —
returning `null` or a `None` node is a bug the rewriter asserts against rather than a way to delete.

**`SyntaxFactory.Token` gives a fixed-text token its canonical text** from `SyntaxFacts.GetText`, so a
tree built by hand round-trips to source the way a parsed one does. A token built without it prints
as nothing.

## Examples

Parsing a file and reading what came back:

```csharp no-compile="a fragment; `source` is the text of a .rvn file"
var tree = SyntaxTree.ParseText(source, path: "Lit.rvn");

foreach (var diagnostic in tree.Diagnostics) {
    Console.WriteLine(diagnostic);
}

var unit = (CompilationUnitSyntax)tree.GetRoot();

Console.WriteLine(unit.Package?.PackageName.ToString() ?? "<global>");
```

Counting every entry point's declaration without binding anything, which is a visitor's whole shape:

```csharp no-compile="a fragment; `tree` comes from SyntaxTree.ParseText"
sealed class MethodCounter : SyntaxVisitor {
    public int Methods { get; private set; }

    public override void DefaultVisit(SyntaxNode node) {
        if (node is MethodDeclarationSyntax) {
            Methods++;
        }

        foreach (var child in node.ChildNodesAndTokens()) {
            Visit(child);
        }
    }
}
```

⚠ The override is `DefaultVisit` and not a generated `VisitMethodDeclaration`, because a generated
override visits *that* node and nothing under it: descending is the walker's job, and a visitor that
overrode one node kind and forgot to recurse would report the top-level methods and none of the
nested ones — a real answer, quietly short.

Reparsing after an edit, which is the path a hot reload takes:

```csharp no-compile="a fragment; `tree` is the previous tree and `edited` the new text"
var next = tree.WithChangedText(edited);

// Unchanged clean members come back as the same green nodes, so this is usually
// far less work than ParseText — and `next.Diagnostics` is still the whole file's.
foreach (var diagnostic in next.Diagnostics) {
    Console.WriteLine(diagnostic);
}
```

## See also

- [Compiling a shader](compiling-a-shader.md) — the four phases this is the first of, and
  `Compilation`, which is what a tree is handed to.
- [The Raven symbol model](symbols.md) — what binding turns these nodes into, and where a
  `DeclaringSyntax` points back to.
- [The target-independent IR](ir.md) — what is left after two more phases have thrown this away.
- `Raven/README.md` in the repository — the language itself, and ⚠ the newline rule above in its
  original form.
