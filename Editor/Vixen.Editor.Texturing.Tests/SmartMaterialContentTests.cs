// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.NodeGraph;
using Vixen.Editor.TextureGraph;
using Vixen.Editor.Texturing.Layers;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>
///     Doc 48 § M10's five smart materials, as content: they read, they are portable, every generator
///     they name ships, and something puts them where a verb can reach them.
/// </summary>
/// <remarks>
///     <para>
///         <b>The files are the subject, not a fixture.</b> Every case below reads the committed
///         <c>.vxsmartmat</c>s out of the assembly's own manifest and takes them through the real
///         reader and the real <c>Prepare</c> — so a mask kind that stops parsing, or a compound that
///         is renamed out from under a generator mask, is red here rather than at the first artist who
///         drops <c>Rusted Iron</c> onto a model.
///     </para>
///     <para>
///         ⚠ <b>Ask what this file prints on the day the folder ships nothing.</b> Every case but the
///         first is a loop over <see cref="SmartMaterial.Shipped" />, and an empty <c>Shipped</c>
///         satisfies all of them — the vacuous-roll-call shape this workstream has now shipped
///         several times. <see cref="The_shelf_this_assembly_ships_is_the_folder_and_not_a_list" /> is
///         the instrument: it names the five § M10 asks for and compares the manifest with the disk.
///     </para>
///     <para>
///         ⚠ <b>And the joining case is
///         <see cref="Every_generator_a_shipped_smart_material_names_is_a_compound_that_ships" />.</b>
///         A generator mask carries a node-type path as a <em>string</em>; nothing checks it until a
///         bake compiles the stack, and a path naming no published compound is a mask that refuses on
///         somebody else's machine. That string is the only edge between this assembly's content and
///         <c>Vixen.Editor.TextureGraph</c>'s, and it is the one a rename breaks silently.
///     </para>
/// </remarks>
public class SmartMaterialContentTests {
    /// <summary>The five doc 48 § M10 asks for, by name.</summary>
    static readonly string[] Owed = ["Concrete", "Painted Metal", "Plastic", "Rusted Iron", "Worn Wood"];

    /// <summary>⚠ The shipped shelf is the folder, and the folder holds § M10's five.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Two independent sides.</b> <see cref="SmartMaterial.Shipped" /> comes from
    ///         <c>GetManifestResourceNames</c> — what the build put <em>into the assembly</em> — and
    ///         the expectation is the five names § M10 lists. A glob narrowed in the csproj takes the
    ///         first side down and leaves the second standing, which is the point.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Membership rather than equality</b>, for <c>TextureCompoundLibraryTests</c>'
    ///         reason: a sixth smart material is somebody's work, not this file's failure. Retiring
    ///         one of these five, on the other hand, is a deliberate edit here.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_shelf_this_assembly_ships_is_the_folder_and_not_a_list() {
        Assert.All(Owed, name => Assert.Contains(name, SmartMaterial.Shipped));

        // ⚠ And each one reads back, which is what makes `Shipped` a list of files rather than a list
        // of names: a manifest entry whose stream does not open is the failure `Source` returns null
        // for, and every case below would then loop over names it could not read.
        Assert.All(SmartMaterial.Shipped, name => Assert.NotNull(SmartMaterial.Source(name)));
    }

    /// <summary>Every shipped smart material reads through the stack's own reader.</summary>
    /// <remarks>
    ///     ⚠ <b>Through <c>LayerStackYaml</c> and not through a YAML parse, because that is the claim.</b>
    ///     A <c>.vxsmartmat</c> is a <c>.vxlayers</c> byte for byte — that is
    ///     <see cref="SmartMaterial" />'s central design decision — so a file this assembly ships that
    ///     needed a reader of its own would refute it here rather than in a report.
    /// </remarks>
    [Fact]
    public void Every_shipped_smart_material_reads_as_a_layer_stack() {
        Assert.NotEmpty(SmartMaterial.Shipped);

        foreach (var name in SmartMaterial.Shipped) {
            var stack = LayerStackYaml.Read(SmartMaterial.Source(name)!);

            Assert.Equal(name, stack.Name);

            // One set, because `Prepare` refuses anything else by name — a smart material is applied
            // *to* a set, so "every set" is a fact about the model it was authored on.
            var set = Assert.Single(stack.Sets);

            Assert.NotEmpty(set.Layers);
        }
    }

    /// <summary>
    ///     ⚠ Every shipped smart material keeps the three invariants: no model, no mesh, nothing
    ///     painted.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>What makes a smart material a smart material is not its syntax</b> —
    ///         <see cref="SmartMaterial" />'s own remarks say so — and these three are the whole of
    ///         it. <c>Extract</c> establishes them for a file an artist saves; nothing establishes
    ///         them for a file this assembly ships, so this is where an authored one is held to the
    ///         same bar.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The paint check walks the mask stack and the children, not just the layer kind.</b>
    ///         A <c>Fill</c> layer whose <em>mask</em> is a <c>.vxpaint</c> is exactly as unportable
    ///         as a paint layer — the strokes are texels in one model's atlas either way — and a check
    ///         that read <c>Kind</c> alone would call it clean.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_shipped_smart_material_has_no_model_no_mesh_and_nothing_painted() {
        Assert.NotEmpty(SmartMaterial.Shipped);

        var wrong = new List<string>();

        foreach (var name in SmartMaterial.Shipped) {
            var stack = LayerStackYaml.Read(SmartMaterial.Source(name)!);

            if (stack.Model.Length > 0) {
                wrong.Add($"{name}: names a model, '{stack.Model}'.");
            }

            foreach (var set in stack.Sets) {
                if (set.Mesh.Length > 0) {
                    wrong.Add($"{name}: texture set '{set.Name}' names a mesh, '{set.Mesh}'.");
                }

                Painted(name, set.Layers, wrong);
            }
        }

        Assert.Equal([], wrong.Order(StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    ///     ⚠ Every generator a shipped smart material names is a compound the library publishes.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The one edge between two assemblies' content, and it is a string.</b>
    ///         <c>MaskAsset.Generator</c> is "the published compound's node-type path", and
    ///         <c>LayerStackGraph</c> resolves it when a stack is compiled — so a compound renamed,
    ///         moved between folders or never shipped at all leaves a mask that refuses at bake time
    ///         with a sentence about a node type, on a machine that is not the one that wrote it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Against the registry the compounds are published <em>into</em>, not against the
    ///         file names.</b> A compound nests, so a path check that read the folder would be a
    ///         second opinion about what publishing does; and a generator naming an atomic node
    ///         (<c>Source/Noise</c>) is legal here for the same reason — the mask wants a node type,
    ///         and the library is what decides which ones exist.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_generator_a_shipped_smart_material_names_is_a_compound_that_ships() {
        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);
        TextureCompoundLibrary.Publish(registry, folder: null, out var problems);

        Assert.Empty(problems);
        Assert.NotEmpty(SmartMaterial.Shipped);

        var wrong = new List<string>();
        var named = 0;

        foreach (var name in SmartMaterial.Shipped) {
            var stack = LayerStackYaml.Read(SmartMaterial.Source(name)!);

            foreach (var set in stack.Sets) {
                foreach (var generator in Generators(set.Layers)) {
                    named++;

                    if (registry.Types.All(type => type.Path != generator)) {
                        wrong.Add($"{name}: a mask reads '{generator}', which nothing publishes.");
                    }
                }
            }
        }

        Assert.Equal([], wrong.Order(StringComparer.Ordinal).ToArray());

        // ⚠ The instrument. Five smart materials none of whose masks named a generator would satisfy
        // the loop above and prove nothing at all — and it is the likelier failure of the two, because
        // a constant mask is what a half-authored file has.
        Assert.True(
            named >= SmartMaterial.Shipped.Length,
            $"only {named} generator mask(s) were found across {SmartMaterial.Shipped.Length} shipped smart "
            + "material(s), so this case walked almost no edges. A smart material whose masks are all "
            + "constants is a stack of flat fills."
        );
    }

    /// <summary>Every shipped smart material prepares onto a texture set and brings layers with it.</summary>
    /// <remarks>
    ///     ⚠ <b><c>Prepare</c> is the verb, and it refuses for four separate reasons</b> — more than
    ///     one set, no layers, nothing portable, no target set. A file that reads and then prepares to
    ///     nothing is the state this catches: <see cref="SmartMaterialApplied.Layers" /> empty and a
    ///     sentence saying why, which the apply verb turns into a warning and no edit.
    /// </remarks>
    [Fact]
    public void Every_shipped_smart_material_prepares_onto_a_texture_set() {
        Assert.NotEmpty(SmartMaterial.Shipped);

        TextureSetAsset target = new() {
            Name = "Body",
            Channels = [
                new() { Usage = "baseColor" },
                new() { Usage = "roughness" },
                new() { Usage = "metalness" }
            ]
        };

        foreach (var name in SmartMaterial.Shipped) {
            var applied = SmartMaterial.Prepare(target, LayerStackYaml.Read(SmartMaterial.Source(name)!));

            Assert.False(
                applied.Layers.IsDefaultOrEmpty,
                $"'{name}' prepared to nothing: {applied.Status}"
            );
        }
    }

    /// <summary>⚠ Installing writes the shipped five onto a project's shelf, and never twice.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The reachability half, and without it the five are content nothing can select.</b>
    ///         <c>TexturingModule.ApplySmartMaterial</c> reads <c>project.Selection.Primary</c> and the
    ///         editor factory claims a file extension, so both doors take an asset with a path — there
    ///         is no selecting a manifest resource. <see cref="SmartMaterial.Install" /> is the only
    ///         thing in the tree that turns one into the other.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The second run is the assertion that matters.</b> This is called from every
    ///         <c>Activate</c>, so an <c>Install</c> that overwrote would replace an artist's edited
    ///         <c>Rusted Iron</c> — the file <a href="https://github.com/Rikarin/Vixen/issues/1070">#1070</a>
    ///         deliberately made editable — with the shipped copy the next time the editor started,
    ///         silently, and only visible as work that had gone.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Installing_fills_an_empty_shelf_and_then_leaves_it_alone() {
        var assets = Path.Combine(Path.GetTempPath(), "vixen-shelf-" + Guid.NewGuid().ToString("N")[..12]);

        try {
            Directory.CreateDirectory(assets);

            Assert.Empty(SmartMaterial.Shelf(assets));

            var written = SmartMaterial.Install(assets);

            Assert.Equal(SmartMaterial.Shipped, written);
            Assert.All(Owed, name => Assert.Contains(name, SmartMaterial.Shelf(assets)));

            // An artist edits one, which is what a shelf is for.
            var edited = Path.Combine(assets, SmartMaterial.ShelfFolder, "Rusted Iron" + SmartMaterial.Extension);

            File.WriteAllText(edited, "version: 1\nname: Rusted Iron\nbaseWidth: 8\nbaseHeight: 8\nsets: []\n");

            // ⚠ Nothing written the second time, and the edit is still there. Either half failing is a
            // different defect: a non-empty answer is a shelf that grows on every activation, and a
            // changed file is an artist's work replaced by a shipped copy.
            Assert.Empty(SmartMaterial.Install(assets));
            Assert.Contains("baseWidth: 8", File.ReadAllText(edited), StringComparison.Ordinal);
        } finally {
            try {
                if (Directory.Exists(assets)) {
                    Directory.Delete(assets, recursive: true);
                }
            } catch (IOException) {
                // A file the test wrote and the OS has not let go of. Not what is under test.
            }
        }
    }

    /// <summary>⚠ And the module installs them, so the shelf is filled without anybody running a verb.</summary>
    /// <remarks>
    ///     <b>The caller, asserted rather than described.</b> <see cref="SmartMaterial.Install" /> with
    ///     no production call site would be exactly the finished-thing-nothing-calls this workstream
    ///     keeps shipping — and a comment in <c>Activate</c> saying it is called is not evidence that
    ///     it is. This activates the real module through the real plugin host and reads the shelf.
    /// </remarks>
    [Fact]
    public void Activating_the_texturing_module_fills_the_projects_shelf() {
        using var fixture = new TexturingFixture();

        Assert.Empty(SmartMaterial.Shelf(fixture.Paths.Assets));

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());

        Assert.All(Owed, name => Assert.Contains(name, SmartMaterial.Shelf(fixture.Paths.Assets)));

        // ⚠ **The half a directory listing cannot see, and it is the half the whole design rests
        // on.** `Shelf` reads the file system; every door a `.vxsmartmat` has takes an *asset* —
        // `ApplySmartMaterial` reads `project.Selection.Primary`, `LayerStackEditorFactory` claims
        // an extension. A plugin activates after the project has been indexed, so five files
        // written and not rescanned are on the disk and unreachable until a restart, and the
        // assertion above is green either way.
        Assert.All(
            Owed,
            name => Assert.True(
                fixture.Project.Assets.TryGetByPath(
                    "Assets/" + SmartMaterial.ShelfFolder + "/" + name + SmartMaterial.Extension,
                    out _
                ),
                $"'{name}' is on the shelf and not in the asset database, so nothing can select it."
            )
        );
    }

    /// <summary>Every layer, mask and child that carries painted texels, one sentence each.</summary>
    /// <param name="name">The smart material's name, for the message.</param>
    /// <param name="layers">The layers to walk.</param>
    /// <param name="wrong">Where the sentences go.</param>
    static void Painted(string name, IEnumerable<LayerAsset> layers, List<string> wrong) {
        foreach (var layer in layers) {
            if (layer.Kind == LayerKind.Paint || layer.Paint.Length > 0) {
                wrong.Add($"{name}: layer '{layer.Id}' is painted.");
            }

            if (layer.Mask.Paint.Length > 0 || layer.Mask.Source == LayerMaskSource.Paint) {
                wrong.Add($"{name}: layer '{layer.Id}' has a painted mask.");
            }

            foreach (var entry in layer.Mask.Layers) {
                if (entry.Paint.Length > 0 || entry.Source == LayerMaskSource.Paint) {
                    wrong.Add($"{name}: layer '{layer.Id}' has a painted entry in its mask stack.");
                }
            }

            Painted(name, layer.Children, wrong);
        }
    }

    /// <summary>Every generator path a layer's masks name, at any depth.</summary>
    /// <param name="layers">The layers to walk.</param>
    /// <returns>The node-type paths, with duplicates.</returns>
    static IEnumerable<string> Generators(IEnumerable<LayerAsset> layers) {
        foreach (var layer in layers) {
            if (layer.Mask.Generator.Length > 0) {
                yield return layer.Mask.Generator;
            }

            foreach (var entry in layer.Mask.Layers) {
                if (entry.Generator.Length > 0) {
                    yield return entry.Generator;
                }
            }

            foreach (var effect in layer.Mask.Effects) {
                if (effect.Node.Length > 0) {
                    yield return effect.Node;
                }
            }

            foreach (var nested in Generators(layer.Children)) {
                yield return nested;
            }
        }
    }
}
