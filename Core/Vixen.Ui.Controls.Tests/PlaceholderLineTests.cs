// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>A field's prompt is one line, as the text that replaces it is — a textarea's excepted (#1477).</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Found as a picture</b>: the editor hierarchy's filter showed "by" where its
///         reference showed "Fi". The prompt is "Filter by name…", and <c>field-placeholder</c> is
///         an auto-width absolute box placed by <c>left</c> alone — which since #1447 shrinks to the
///         room right of its <c>left</c>, as CSS 2 §10.3.7 says. In a narrow field that is less than
///         one word, so the prompt wrapped to three lines and the field, centring them, showed the
///         middle one. #1447 gave the other boxes of that shape <c>nowrap</c>; this one was missed.
///     </para>
///     <para>
///         The oracle is a one-word prompt in the same field: a long prompt must be exactly as tall.
///         A textarea's prompt wraps, like a textarea's text, and is the other half of the test —
///         without it, a rule that refused to wrap anything would pass.
///     </para>
/// </remarks>
public class PlaceholderLineTests {
    const string Long = "Filter by name and type";

    [Fact]
    public void A_long_prompt_in_a_narrow_search_box_is_one_line() {
        var oneWord = PromptOf<SearchBox>("Filter");
        var wide = PromptOf<SearchBox>(Long);

        Assert.True(oneWord.Height > 0f, "the one-word prompt is measured");
        Assert.True(wide.Width > 40f, $"the premise: the prompt is wider than the room it has, {wide.Width}");
        Assert.Equal(oneWord.Height, wide.Height, 0.5f);
    }

    [Fact]
    public void A_long_prompt_in_a_narrow_textarea_still_wraps() {
        var oneWord = PromptOf<TextArea>("Filter");
        var wide = PromptOf<TextArea>(Long);

        Assert.True(wide.Height > oneWord.Height * 1.5f, $"a textarea's prompt wraps: {wide.Height} against one line's {oneWord.Height}");
    }

    static (float Width, float Height) PromptOf<T>(string prompt) where T : TextField, new() {
        using var fixture = new ControlFixture(css: "search-box, textarea { width: 70px; }");
        var field = fixture.Add<T>();
        field.Placeholder = prompt;
        fixture.Update();
        fixture.Update();

        var shown = field.Children.Single(child => child.Tag == "field-placeholder");

        return (shown.Width, shown.Height);
    }
}
