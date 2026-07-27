// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Core.Syntax;
using Vixen.Ui.Markup.Syntax;
using Xunit;

namespace Vixen.Ui.Markup.Tests;

/// <summary>What every test in this assembly needs: a parse, and a way to read the result.</summary>
static class Vxml {
    public static SyntaxTree Parse(string source) => SyntaxTree.ParseText(source, "Test.vxml");

    /// <summary>Parses and fails the test if anything was reported.</summary>
    public static DocumentSyntax ParseClean(string source) {
        var tree = Parse(source);
        Assert.Empty(tree.Diagnostics.Select(d => d.ToString()));
        return tree.GetDocument();
    }

    public static IReadOnlyList<string> Ids(SyntaxTree tree) => [.. tree.Diagnostics.Select(d => d.Descriptor.Id)];

    /// <summary>
    ///     A syntax list as an ordinary sequence. <see cref="SyntaxList{TNode}" /> deliberately
    ///     enumerates through a struct rather than <c>IEnumerable&lt;T&gt;</c>, which is right for a
    ///     parser and wrong for an assertion.
    /// </summary>
    public static List<T> Items<T>(this SyntaxList<T> list) where T : SyntaxNode {
        List<T> items = new(list.Count);
        for (var i = 0; i < list.Count; i++) {
            items.Add(list[i]!);
        }

        return items;
    }

    /// <summary>
    ///     The tree as indented kinds, with each token's text beside it. Golden trees compare
    ///     against this, so a change in shape shows up as a diff rather than as a failed assertion
    ///     about one property.
    /// </summary>
    public static string Dump(SyntaxNode node) {
        var builder = new StringBuilder();
        Write(builder, node, 0);
        return builder.ToString();
    }

    static void Write(StringBuilder builder, SyntaxNode node, int depth) {
        builder.Append(' ', depth * 2);

        switch (node) {
            case SyntaxToken token:
                builder.Append(((SyntaxKind)token.RawKind).ToString());
                builder.Append(token.IsMissing ? " <missing>" : $" {Quote(token.Text)}");
                builder.AppendLine();
                return;
            case VxmlSyntaxNode vxml:
                builder.AppendLine(vxml.Kind.ToString());
                break;
            default:
                builder.AppendLine("List");
                break;
        }

        foreach (var child in node.ChildNodesAndTokens()) {
            Write(builder, child, depth + 1);
        }
    }

    static string Quote(string text) =>
        "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
