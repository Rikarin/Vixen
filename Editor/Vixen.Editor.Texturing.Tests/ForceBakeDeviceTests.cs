// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Assets.Materials;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>Doc 48 § D4's refusal, and the control in the editor that answers it.</summary>
/// <remarks>
///     <para>
///         <b><a href="https://github.com/Rikarin/Vixen/issues/1019">#1019</a>.</b>
///         <c>ProjectMaterialBaker.Write</c> refuses an output whose bytes are no longer what the last
///         bake wrote — the point being that a file somebody <em>painted over</em> is flagged rather
///         than silently regenerated — and <c>force</c> is how a person says they meant it. The
///         command line carries one; a command handler takes no argument, so the editor's verb was
///         always called with the default and the notification could only send the artist away.
///     </para>
///     <para>
///         ⚠ <b>The refusal has to be produced rather than simulated, which is why this needs a
///         device.</b> The digest is written by a real bake and compared against real bytes on disk;
///         a fixture that wrote the sidecar by hand would be asserting against its own idea of the
///         format rather than against <c>MaterialProvenance</c>.
///     </para>
/// </remarks>
public class ForceBakeDeviceTests(ITestOutputHelper output) {
    /// <summary>What the fixture graph is called once it is an asset.</summary>
    const string Material = "Hull";

    /// <summary>A painted-over map stops a re-bake, and the force verb gets past it.</summary>
    /// <remarks>
    ///     ⚠ <b>Three states and not two, and the middle one is where the defect lived.</b> The bake
    ///     that refuses must leave the artist's bytes alone; the force that follows must replace
    ///     them. A test that only asserted the third would be green against a route that never
    ///     refused at all, which is § D4 deleted rather than answered.
    /// </remarks>
    [Fact]
    public void The_force_verb_replaces_a_map_somebody_painted_over() {
        using var device = TexturingDevice.Open();
        var adapter = TexturingDevice.Adapter(device);

        using var fixture = new TexturingFixture(device);

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());
        Open(fixture);

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.BakeCommand), Say(fixture));

        var map = Path.Combine(
            fixture.Paths.Assets,
            MaterialMapNaming.DefaultFolder,
            Material + "_baseColor" + MaterialMapNaming.PortableExtension
        );

        Assert.True(File.Exists(map), $"{adapter}: {Say(fixture)}");

        // "Somebody painted over it", as far as the digest is concerned: the bytes under this name
        // are no longer the ones the bake recorded.
        var baked = File.ReadAllBytes(map);

        File.WriteAllBytes(map, [.. baked, 0]);

        var refusedAt = fixture.Shell.Notifications.History.Count;

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.BakeCommand));

        output.WriteLine($"{adapter}: the second bake said — {Since(fixture, refusedAt)}");

        // ⚠ The artist's bytes survive the refusal, which is the whole of what § D4 buys. A route
        // that flagged the overpaint after writing would report the same sentence over a file it had
        // already destroyed.
        Assert.Equal(baked.Length + 1, new FileInfo(map).Length);

        // And the sentence offers the control, rather than sending them to a command-line verb that
        // cannot re-bake a graph at all (#1020).
        Assert.Contains("Bake Material (Force)", Since(fixture, refusedAt), StringComparison.Ordinal);

        var forcedAt = fixture.Shell.Notifications.History.Count;

        Assert.True(
            fixture.Shell.Commands.Execute(TexturingModule.ForceBakeCommand),
            $"{adapter}: nothing answered '{TexturingModule.ForceBakeCommand}'."
        );

        output.WriteLine($"{adapter}: the forced bake said — {Since(fixture, forcedAt)}");

        Assert.Equal(baked, File.ReadAllBytes(map));
    }

    /// <summary>⚠ The forced run repeats the bake that was refused, not whatever is open by then.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The gap between the refusal and the answer is a gap an artist spends looking at
    ///         the file.</b> A painted-over refusal names a map; the ordinary next move is to go and
    ///         open it, or the graph beside it, and decide. So by the time <c>Bake Material
    ///         (Force)</c> is pressed, the document on the canvas is very often <em>not</em> the one
    ///         the refusal was about.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Which makes the closure's captured subject the load-bearing part of the whole
    ///         design.</b> The first version armed <c>() =&gt; BakeGraph(force: true)</c>, which
    ///         re-read the module's live field — so a verb whose entire purpose is overwriting
    ///         somebody's work would have overwritten the wrong work, silently, and the notification
    ///         would have named the second graph as though that had been the plan.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_forced_bake_answers_the_document_that_was_refused_and_not_the_one_now_open() {
        using var device = TexturingDevice.Open();
        var adapter = TexturingDevice.Adapter(device);

        using var fixture = new TexturingFixture(device);

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());
        Open(fixture);

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.BakeCommand), Say(fixture));

        var map = Path.Combine(
            fixture.Paths.Assets,
            MaterialMapNaming.DefaultFolder,
            Material + "_baseColor" + MaterialMapNaming.PortableExtension
        );

        var baked = File.ReadAllBytes(map);

        File.WriteAllBytes(map, [.. baked, 0]);

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.BakeCommand));
        Assert.Equal(baked.Length + 1, new FileInfo(map).Length);

        // The artist goes and looks at something else, which is what a refusal naming a file invites.
        var second = Open(fixture, "Keel");

        Assert.NotEqual(Material, Path.GetFileNameWithoutExtension(second.AssetPath));

        var forcedAt = fixture.Shell.Notifications.History.Count;

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.ForceBakeCommand));

        output.WriteLine($"{adapter}: the forced bake said — {Since(fixture, forcedAt)}");

        // The refused map is repaired ...
        Assert.Equal(baked, File.ReadAllBytes(map));

        // ... and the graph that merely happened to be open was not baked at all. ⚠ This is the
        // assertion the defect fails: under the live-field closure the second graph is what the
        // forced run wrote, and the first map keeps the artist's stray byte for ever.
        Assert.False(
            Directory.EnumerateFiles(
                    Path.Combine(fixture.Paths.Assets, MaterialMapNaming.DefaultFolder),
                    "Keel_*"
                )
                .Any(),
            $"{adapter}: the forced bake wrote the document that was open rather than the one refused."
        );
    }

    /// <summary>The force verb does nothing at all until a bake has been refused for that reason.</summary>
    /// <remarks>
    ///     ⚠ <b>This is what makes one verb safe enough to sit on the Tools menu, and it is the half
    ///     a plain "bake forced" twin could not have.</b> Armed by the refusal and disarmed by
    ///     running, the control cannot overwrite anything the artist has not just been told about —
    ///     so pressing it out of order writes nothing rather than replacing a map somebody is still
    ///     working on.
    /// </remarks>
    [Fact]
    public void The_force_verb_writes_nothing_when_no_bake_was_refused() {
        using var fixture = new TexturingFixture(graphics: true);

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.ForceBakeCommand));

        Assert.False(
            Directory.Exists(Path.Combine(fixture.Paths.Assets, MaterialMapNaming.DefaultFolder)),
            "the force verb baked something with no refusal behind it, which is a control that "
            + "overwrites an artist's paint whenever somebody reaches for it out of order."
        );

        Assert.Contains("Nothing to force", Say(fixture), StringComparison.Ordinal);
    }

    /// <summary>A refusal force cannot answer does not arm it.</summary>
    /// <remarks>
    ///     ⚠ <b>What the case above cannot distinguish.</b> An arming keyed on "the bake refused"
    ///     rather than on <c>MaterialBakeOutcome.Painted</c> would offer the control after a graph
    ///     that did not compile and after a host with no device — and force changes neither, so the
    ///     artist would press it and be told the same thing twice.
    /// </remarks>
    [Fact]
    public void A_refusal_force_cannot_answer_does_not_arm_it() {
        using var fixture = new TexturingFixture(graphics: true);

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());
        fixture.Project.Selection.Set(fixture.AddGraph(Material));

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenCommand));

        // This fixture publishes an `IEditorGraphics` with a null device, so the bake refuses for the
        // one reason force is most obviously useless against.
        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.BakeCommand));
        Assert.DoesNotContain("Bake Material (Force)", Say(fixture), StringComparison.Ordinal);

        var at = fixture.Shell.Notifications.History.Count;

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.ForceBakeCommand));
        Assert.Contains("Nothing to force", Since(fixture, at), StringComparison.Ordinal);
    }

    /// <summary>Scans the committed fixture in, opens it through the verb, and shrinks it.</summary>
    /// <param name="fixture">The host.</param>
    /// <remarks>
    ///     64² rather than the document's 1024² default, for <c>MaterialBakeRouteDeviceTests</c>'
    ///     reason: the base resolution is not stored in a <c>.vxtexgraph</c> (#719), so it is set on
    ///     the document and a bake at the default would spend the run on a mip chain this asserts
    ///     nothing about.
    /// </remarks>
    static TextureGraphDocument Open(TexturingFixture fixture) => Open(fixture, Material);

    /// <summary>Scans a copy of the committed fixture in under a given name and opens it.</summary>
    /// <param name="fixture">The host.</param>
    /// <param name="name">What to call the asset.</param>
    /// <returns>The document now on the canvas.</returns>
    static TextureGraphDocument Open(TexturingFixture fixture, string name) {
        fixture.Project.Selection.Set(
            fixture.AddGraph(
                name,
                File.ReadAllText(
                    Path.Combine(AppContext.BaseDirectory, "Fixtures", "BakeRoute" + TextureGraphDocument.Extension)
                )
            )
        );

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenCommand));

        var document = Assert.Single(
            fixture.Project.Documents.OfType<TextureGraphDocument>(),
            one => Path.GetFileNameWithoutExtension(one.AssetPath) == name
        );

        Assert.Empty(document.LoadDiagnostics);

        document.BaseWidth = 64;
        document.BaseHeight = 64;

        return document;
    }

    /// <summary>What the module last said, because every refusal here is a notification and not an exception.</summary>
    /// <param name="fixture">The host.</param>
    /// <returns>The sentences, or a note that there were none.</returns>
    static string Say(TexturingFixture fixture) => Since(fixture, 0);

    /// <summary>What the module said after a given point, so one gesture's answer can be read alone.</summary>
    /// <param name="fixture">The host.</param>
    /// <param name="from">How many notifications there already were.</param>
    /// <returns>The sentences since then, or a note that there were none.</returns>
    /// <remarks>
    ///     ⚠ <b>A mark rather than clearing the history, because the history is read-only and
    ///     rightly so.</b> Reading the whole of it after a second bake would make "the refusal
    ///     offers the control" green on the <em>first</em> bake's sentence, which does not carry it.
    ///     ⚠ <b>And it is <c>Take</c> and not <c>Skip</c>, because <c>NotificationCenter.History</c>
    ///     is newest first.</b> Skipping the mark reads the entries from <em>before</em> the gesture,
    ///     which made this file's first run report that a bake had succeeded when what it had done
    ///     was refuse — the instrument agreeing with the wrong half of its own subject.
    /// </remarks>
    static string Since(TexturingFixture fixture, int from) =>
        fixture.Shell.Notifications.History.Count > from
            ? string.Join(
                " · ",
                fixture.Shell.Notifications.History
                    .Take(fixture.Shell.Notifications.History.Count - from)
                    .Select(one => one.Message + ": " + one.Detail)
            )
            : "The module said nothing at all, which is itself the finding.";
}
