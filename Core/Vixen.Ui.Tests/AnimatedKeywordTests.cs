// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>An animated or transitioned value with a keyword in it, written back into the cascade.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Two intern tables, and the animator wrote with the one its parser had not read
///         with</b> (#1445). <c>StyleValueParser</c> interns an identifier in the <i>keywords</i> table
///         (<c>StyleEngine.Names</c>) and the animator formatted the value it overlays with the
///         <i>values</i> table, so a keyword id was looked up in a table it did not come from. Out of
///         range it threw out of <c>UiDocument.Update</c>; in range it named some other word, which
///         is the worse of the two because nothing says so.
///     </para>
///     <para>
///         So every assertion here reads the overlaid text back and asks for the keyword the
///         stylesheet wrote — not merely that the frame did not throw.
///     </para>
/// </remarks>
public class AnimatedKeywordTests {
    const string Base = """
        root { width: 400px; height: 300px; }
        #box { width: 100px; height: 40px; background-color: #202020; }
        """;

    static UiElement Settled(UiDocument document, string css) {
        document.Load(Base + css);

        var box = document.Create("div", document.Root, "box");

        Frame(document, 0.0);

        return box;
    }

    static void Frame(UiDocument document, double seconds) {
        document.Tick(TimeSpan.FromSeconds(seconds));
        document.Update();
    }

    static string Rotate(UiDocument document, UiElement box) {
        Assert.True(box.Style.TryGet(document.Styles.Properties.Lookup("rotate"), out var value), "rotate never arrived");

        return document.Styles.Values.NameOf(value);
    }

    /// <summary>A keyframe over <c>rotate: z 90deg</c> overlays a value that still says <c>z</c>.</summary>
    [Fact]
    public void A_keyframe_value_with_a_keyword_reads_back_its_keyword() {
        using var document = new UiDocument(400f, 300f);
        var box = Settled(
            document,
            """
            @keyframes turn { from { rotate: z 0deg; } to { rotate: z 90deg; } }
            #box { animation-name: turn; animation-duration: 1s; animation-timing-function: linear; }
            """
        );

        Frame(document, 0.25);

        Assert.StartsWith("z ", Rotate(document, box), StringComparison.Ordinal);
    }

    /// <summary>A transition over a keyword-bearing value overlays the keyword, running and withdrawing.</summary>
    /// <remarks>
    ///     <c>allow-discrete</c> so the run starts whether or not the pair interpolates — what is under
    ///     test is the text the overlay writes, through both of the animator's transition branches:
    ///     the property still declared (<c>TryOverlay</c>), and the property the cascade has stopped
    ///     giving the element while its transition back runs (<c>Withdrawing</c>).
    /// </remarks>
    [Fact]
    public void A_transition_value_with_a_keyword_reads_back_its_keyword() {
        using var document = new UiDocument(400f, 300f);
        var box = Settled(
            document,
            """
            #box { transition-property: rotate; transition-duration: 1s;
                   transition-timing-function: linear; transition-behavior: allow-discrete; }
            #box.on { rotate: z 90deg; }
            """
        );

        box.AddClass("on");
        Frame(document, 1.0);
        Frame(document, 1.25);

        Assert.StartsWith("z ", Rotate(document, box), StringComparison.Ordinal);

        Frame(document, 3.0);
        box.RemoveClass("on");
        Frame(document, 3.0);
        Frame(document, 3.25);

        Assert.StartsWith("z ", Rotate(document, box), StringComparison.Ordinal);
    }
}
