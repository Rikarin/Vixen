// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Text;

namespace Vixen.Ui.Controls.Tests;

/// <summary>What a test needs to look inside a field: its laid-out text, and a second direction.</summary>
static class FieldProbe {
    /// <summary>The Arabic face, registered as a fallback by whichever fixture wants two directions.</summary>
    /// <remarks>
    ///     ⚠ <b>It is here for its direction and not for its letters.</b> Every other face this
    ///     project links is left to right, so a line built from them has one direction and answers
    ///     both caret affinities with the same pixel — a fixture that could not tell a correct
    ///     answer from a wrong one. It is a fallback rather than the family, because a fallback is
    ///     what puts two runs on one line.
    /// </remarks>
    public static FontFace Aran { get; } = Load("TestShapeAran.ttf", "TestShapeAran");

    static FontFace Load(string resource, string name) {
        using var stream = typeof(FieldProbe).Assembly
                .GetManifestResourceStream($"Vixen.Ui.Controls.Tests.Fonts.{resource}")
            ?? throw new InvalidOperationException($"the test font '{resource}' is not embedded");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return FontFace.Load(memory.ToArray(), name: name);
    }

    /// <summary>The laid-out text of a field's own text part.</summary>
    /// <remarks>
    ///     Walked rather than reached for: <c>TextField.text</c> is private, and the block is on the
    ///     part rather than on the field, which is a control's normal shape.
    /// </remarks>
    public static TextLayout Block(TextField field) {
        foreach (var child in Walk(field)) {
            if (child.Block() is { } block) {
                return block;
            }
        }

        throw new InvalidOperationException("the field laid out no text");
    }

    static IEnumerable<UiElement> Walk(UiElement from) {
        foreach (var child in from.Children) {
            yield return child;

            foreach (var deeper in Walk(child)) {
                yield return deeper;
            }
        }
    }
}
