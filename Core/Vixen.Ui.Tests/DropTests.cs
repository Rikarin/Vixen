// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>A file dragged in from outside reaches the element it was let go over.</summary>
public class DropTests {
    static UiDocument Laid() {
        var document = new UiDocument(400f, 300f);

        document.Load("""
            root { width: 400px; height: 300px; }
            .outer { width: 200px; height: 200px; }
            .inner { width: 50px; height: 50px; }
        """);

        return document;
    }

    /// <summary>It goes to what is under it rather than to the focus.</summary>
    /// <remarks>
    ///     A drop is positional: the user chose the target by letting go over it, and the element
    ///     that happens to have the keyboard focus is somewhere else entirely.
    /// </remarks>
    [Fact]
    public void A_drop_goes_to_the_deepest_element_under_it() {
        using var document = Laid();
        var outer = document.Root.Add("div", classNames: "outer");
        var inner = outer.Add("div", classNames: "inner");
        document.Update();

        DropEvent? seen = null;
        inner.AddHandler<DropEvent>((_, args) => seen = args);

        var target = document.Dispatch(new DropEvent { X = 10f, Y = 10f, Files = ["/tmp/a.png"] });

        Assert.Same(inner, target);
        Assert.NotNull(seen);
        Assert.Equal("/tmp/a.png", Assert.Single(seen.Files));
    }

    /// <summary>And it bubbles, so a panel can accept what its children do not.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the half that decides whether the feature is usable.</b> The element under
    ///     the pointer when a file is let go over a panel is almost never the panel — it is a label,
    ///     a row background, an icon, whichever leaf the layout put there. A drop delivered only to
    ///     the hit-test result would mean every leaf in an application had to know about files.
    /// </remarks>
    [Fact]
    public void A_drop_bubbles_to_an_ancestor_that_wants_it() {
        using var document = Laid();
        var outer = document.Root.Add("div", classNames: "outer");
        var inner = outer.Add("div", classNames: "inner");
        document.Update();

        var reached = 0;
        outer.AddHandler<DropEvent>((_, _) => reached++);

        document.Dispatch(new DropEvent { X = 10f, Y = 10f, Text = "hello" });

        Assert.Equal(1, reached);
        Assert.Same(inner, document.HitTest(10f, 10f));
    }

    /// <summary>Nothing under the point is nothing dropped on.</summary>
    [Fact]
    public void A_drop_outside_the_surface_reaches_nobody() {
        using var document = Laid();
        document.Update();

        var reached = 0;
        document.Root.AddHandler<DropEvent>((_, _) => reached++);

        Assert.Null(document.Dispatch(new DropEvent { X = -1f, Y = 10f }));
        Assert.Equal(0, reached);
    }
}
