// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Texturing.Layers;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>A stack that did not read is never written over by the empty stand-in it opened as.</summary>
public class LayerStackSaveTests {
    /// <summary>
    ///     ⚠ The save is refused and the file keeps every byte, where it used to be replaced by an empty stack
    ///     (Patina #288). A clean stack beside it still saves, so the refusal is about the diagnostic and not every
    ///     save.
    /// </summary>
    [Fact]
    public void A_stack_with_load_diagnostics_refuses_to_save_over_its_file() {
        using var fixture = new TexturingFixture();
        const string Unreadable = "version: 1\nname: Future\nsets:\n  - name: S\n    layers:\n      - id: l\n"
            + "        kind: Fill\n        blend: Hologram\n";
        var path = Path.Combine(fixture.Paths.Assets, "Future" + LayerStackDocument.Extension);
        var broken = new LayerStackDocument(
            fixture.Project,
            LayerStackPanelTests.AddStack(fixture, "Future", Unreadable),
            path
        );

        Assert.Single(broken.LoadDiagnostics);

        var refusal = Assert.Throws<IOException>(broken.Save);

        Assert.Contains("did not read", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(Unreadable, File.ReadAllText(path));

        var clean = new LayerStackDocument(
            fixture.Project,
            LayerStackPanelTests.AddStack(fixture, "Plain"),
            Path.Combine(fixture.Paths.Assets, "Plain" + LayerStackDocument.Extension)
        );

        clean.Save();

        Assert.NotEmpty(File.ReadAllText(Path.Combine(fixture.Paths.Assets, "Plain" + LayerStackDocument.Extension)));
    }
}
