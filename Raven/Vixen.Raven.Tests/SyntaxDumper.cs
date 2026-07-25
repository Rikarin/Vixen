// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Raven.Syntax;
using Vixen.Core.Syntax;

namespace Tests;

/// <summary>
///     Serializes a Raven syntax tree into a stable, indented text form for
///     golden-file (snapshot) testing. Traversal is slot-based so the dump reflects
///     exactly what the tree exposes today; as the frontend grows (real tokens,
///     trivia, spans) the golden files change and make that growth reviewable.
/// </summary>
public static class SyntaxDumper {
    public static string Dump(SyntaxNode node) {
        var sb = new StringBuilder();
        Walk(node, sb, 0);
        return sb.ToString();
    }

    static void Walk(SyntaxNode node, StringBuilder sb, int indent) {
        sb.Append(' ', indent * 2);

        if (node is SyntaxToken token) {
            sb.Append("Token(").Append(token.Kind).Append(')');
            var text = SafeText(token);
            if (!string.IsNullOrEmpty(text)) {
                sb.Append(" \"").Append(text).Append('"');
            }

            sb.Append('\n');
            return;
        }

        // Any node, Raven-typed or a shared list node — go through the raw value.
        sb.Append((SyntaxKind)node.RawKind).Append('\n');

        for (var i = 0; i < node.SlotCount; i++) {
            var child = node.GetSlot(i);
            if (child != null) {
                Walk(child, sb, indent + 1);
            }
        }
    }

    // Token.Text is not fully modeled yet for every token kind; never let a
    // missing value crash a snapshot.
    static string? SafeText(SyntaxToken token) {
        try {
            return token.Text;
        } catch {
            return null;
        }
    }
}
