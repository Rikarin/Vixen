// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;
using Xunit;

namespace Vixen.Ui.Controls.Advanced.Tests;

/// <summary>
///     The completion popup opened near the editor's right edge is as wide as the same popup opened
///     at the start of a line (#1447).
/// </summary>
/// <remarks>
///     <see cref="CodeEditor" /> writes the popup's <c>left</c> at the caret and nothing else. Since
///     #1447 an auto-width absolute box shrinks to the room right of its <c>left</c>, as CSS 2 §10.3.7
///     says, so a popup at column 90 wrapped every "label  detail" row onto two lines. The popup is a
///     list of one-line entries, so <c>code-completion</c> says <c>white-space: nowrap</c> and keeps
///     the width it always had, overhanging the edge rather than folding.
/// </remarks>
public class CompletionPopupWidthTests {
    static CompletionItem[] Items => [
        new("StrengthOfTheBlend", "float  blend weight"),
        new("StretchToFit", "bool  scale to quad")
    ];

    static (float Width, float Height, float Left) Open(int column) {
        using var fixture = new AdvancedFixture();
        var editor = fixture.Add<CodeEditor>();
        editor.Source = new string(' ', column);

        fixture.Update();
        editor.Refresh();
        fixture.Update();
        fixture.Document.Focus(editor);

        editor.CompletionProvider = static (_, _) => Items;

        editor.Move(new TextPosition(0, column));
        fixture.TypeText("St");
        fixture.Type(InputKey.Space, ModifierKeys.Control);
        fixture.Update();

        Assert.True(editor.IsCompleting);

        var bounds = editor.Completion.Bounds;
        return (bounds.Width, bounds.Height, bounds.X - editor.Bounds.X);
    }

    [Fact]
    public void A_popup_at_the_right_edge_is_as_wide_as_one_at_the_start_of_a_line() {
        var start = Open(0);
        var edge = Open(90);

        // The premise: the popup really was opened near the edge, with less room than it needs.
        Assert.True(edge.Left + start.Width > 800f, $"the popup at x {edge.Left} has room for {start.Width}");

        Assert.Equal(start.Width, edge.Width, 0.5f);
        Assert.Equal(start.Height, edge.Height, 0.5f);
    }
}
