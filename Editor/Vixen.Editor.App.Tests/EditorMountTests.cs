// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Editor.Assets.Content;
using Vixen.Editor.Core;
using Vixen.Editor.Testing;
using Vixen.Engine.Renderer;
using Vixen.Graphics.Null;
using Vixen.Rendering.Materials;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>The viewport's material source, which for the life of the editor there was not one of.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The blocker was never a descriptor set and never the compositor.</b>
///         <c>EditorWorldRenderer</c> owns a real <c>WorldRenderer</c>, which builds the bindless table
///         and pairs <c>MaterialRenderFeature.TextureIndices</c> in its own constructor. What was
///         missing is one call: <c>WorldRenderer.Mount</c> is the only thing in the engine that builds
///         an <c>IMaterialSource</c>, an <c>AssetTextureSource</c>, a vfx source and the terrain seams,
///         it takes an <c>AssetManager</c>, and nothing in the editor made one — <see cref="EditorContent" />
///         itself had no caller outside its own tests.
///     </para>
///     <para>
///         So every drawable in every scene was painted with <c>CompileFallback</c>'s grey
///         metal-roughness surface whatever material it named, and
///         <c>EditorWorldRenderer.Degraded</c> said so into a log nobody read.
///     </para>
/// </remarks>
public sealed class EditorMountTests : IDisposable {
    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-mount-" + Guid.NewGuid().ToString("N")[..12]);
    readonly NullDevice device = new();
    readonly List<IDisposable> owned = [];

    public void Dispose() {
        for (var index = owned.Count - 1; index >= 0; index--) {
            owned[index].Dispose();
        }

        device.Dispose();

        try {
            if (Directory.Exists(root)) {
                Directory.Delete(root, recursive: true);
            }
        } catch (IOException) {
            // A store the test wrote and the OS has not let go of. Not what is under test.
        }
    }

    /// <summary>An editor opened on an imported project has a material source, and keeps its geometry.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Both halves, because doing one without the other is the failure this arrangement was
    ///         designed around.</b> <c>WorldRenderer.Mount</c> replaces <c>Source</c> with an
    ///         <c>AssetMeshSource</c> over the catalog, and a catalog resolves <em>less</em> than the
    ///         import cache does — an excluded asset gets no address at all. So a mount that forgot to
    ///         put <c>ProjectMeshSource</c> back would take a subset of the project's geometry off
    ///         screen, silently, in exchange for the materials.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the extraction, which is the one <c>Mount</c> has never heard of.</b> The
    ///         editor's <c>MeshExtractionSystem</c> is hand-assembled because <c>Register</c> takes an
    ///         <c>EngineLoop</c> and an editing frame runs none — so <c>Mount</c> fills in the
    ///         renderer's own <c>Extraction</c>, which in the editor is null, and the source would
    ///         reach nothing. That is the same "two renderers and both must be wired" shape that left
    ///         morphing out of the editor.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task An_imported_project_gives_the_viewport_a_material_source() {
        await Cataloged();

        var editor = Running().Application;
        var frame = editor.Frame!;

        // The whole of the fix, in the order the failure was reported: a painter, the project's own
        // geometry still underneath it, and the extraction actually told about the painter.
        Assert.NotNull(frame.Renderer.Painter);
        Assert.Same(editor.SceneGeometry, frame.Renderer.Source);
        Assert.NotNull(frame.Meshes.Materials);
        Assert.Equal(0, frame.Unresolved);

        // ⚠ And the texture half, which is what the row asks for and is the one that can be missing
        // while everything above is present. `WorldRenderer.Mount` builds an `AssetTextureSource`
        // *only* where there is a bindless table to index it — "a source with nothing indexing its
        // views would upload every texture in the level and hand the slots to nobody" — so a device
        // reporting no bindless mounts materials that compile and sample the table's fallback for
        // ever, which is a picture rather than a failure.
        var painter = Assert.IsType<AssetMaterialSource>(frame.Renderer.Painter);

        Assert.NotNull(frame.Renderer.Table);
        Assert.NotNull(painter.Textures);

        // And the sentence that existed to say none of the above was true.
        Assert.Null(frame.Degraded);
    }

    /// <summary>A project with nothing imported is not mounted, and says why rather than throwing.</summary>
    /// <remarks>
    ///     ⚠ <b>It is the state every new project is in.</b> An editor that refused to open one is an
    ///     editor nobody can start a project with, so the viewport stays on the fallback and
    ///     <c>Degraded</c> is the string a panel reads. This is also what keeps every other suite in
    ///     this project — none of which imports anything — asserting what it always did.
    /// </remarks>
    [Fact]
    public void A_project_with_no_content_keeps_the_fallback_and_says_so() {
        var frame = Running().Application.Frame!;

        Assert.Null(frame.Renderer.Painter);
        Assert.Null(frame.Meshes.Materials);
        Assert.NotNull(frame.Degraded);
        Assert.Contains("fallback", frame.Degraded, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A material the catalog has not got is painted with the fallback, not waited on for ever.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Without the wrapper the mesh does not draw at all, and nothing anywhere says so.</b>
    ///         <c>IMaterialSource.TryGet</c> is two-valued and its false means "not yet" —
    ///         <c>MeshExtractionSystem.Painted</c> reads it that way and leaves the entity unsettled —
    ///         so a reference <c>AssetMaterialSource</c> has refused for good is one the extraction
    ///         asks about every frame, for ever, while the object is never added. Every counter in the
    ///         frame stays healthy; the geometry is simply absent.
    ///     </para>
    ///     <para>
    ///         The editor meets this on purpose rather than by accident: <c>BuildPlanner.AddressOf</c>
    ///         gives an excluded asset no address, and exclusion is the designed case — "a reference
    ///         FBX kept beside the one that ships". Its geometry is drawn from the import cache and its
    ///         material has to come from somewhere.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task A_material_outside_the_catalog_falls_back_rather_than_vanishing() {
        var content = await Cataloged();

        using var painter = new AssetMaterialSource(content.Assets!);

        var missing = new AssetReference(new AssetId(Guid.NewGuid()), SubAssetId.Main);
        var grey = Grey();
        var source = new EditorMaterialSource(painter, grey);

        // The unwrapped answer, which is the defect: false, for ever, for a reference that will never
        // arrive — indistinguishable to the extraction from one still on its way.
        Assert.False(painter.TryGet(missing, out _));
        Assert.True(painter.Refused(missing));

        Assert.True(source.TryGet(missing, out var painted));
        Assert.Same(grey, painted);
        Assert.Equal(1, source.FellBack);

        // Counted per reference rather than per ask, so a scene of a thousand crates wearing one
        // absent material reports one.
        Assert.True(source.TryGet(missing, out _));
        Assert.Equal(1, source.FellBack);
    }

    /// <summary>With nothing to fall back to, the wrapper is exactly the source it wraps.</summary>
    /// <remarks>
    ///     A host that would compile no material at all draws nothing whatever this did — see
    ///     <c>EditorWorldRenderer.Fallback</c>, whose null is "a frame in which nothing is drawn" — so
    ///     substituting null here would be a null material reaching a feature that dereferences it.
    /// </remarks>
    [Fact]
    public async Task With_no_fallback_the_wrapper_refuses_exactly_as_the_source_does() {
        var content = await Cataloged();

        using var painter = new AssetMaterialSource(content.Assets!);
        var source = new EditorMaterialSource(painter, fallback: null);

        Assert.False(source.TryGet(new(new AssetId(Guid.NewGuid()), SubAssetId.Main), out _));
        Assert.Equal(0, source.FellBack);
    }

    /// <summary>An editor over this test's project, one frame in, with a device.</summary>
    EditorSession Running() {
        var session = EditorSession.Start(new EditorSessionOptions { ProjectRoot = root });

        owned.Add(session);

        session.Application.GraphicsDevice = device;
        session.Frame();

        return session;
    }

    /// <summary>Imports one asset into this test's project and writes the catalog over it.</summary>
    /// <remarks>
    ///     ⚠ <b>The catalog is written here rather than by the editor, and that is the honest shape of
    ///     what ships.</b> <c>EditorContent</c> opens whatever the last write left; the editor rewrites
    ///     it whenever a content task succeeds — <c>ContentTasks.Cataloged</c>, on the pool — and does
    ///     not plan the whole project on the frame thread every time somebody opens it. So a project
    ///     imported through the editor is mounted from the next start onwards, which is the sequence
    ///     this reproduces.
    /// </remarks>
    async Task<EditorContent> Cataloged() {
        Directory.CreateDirectory(Path.Combine(root, "Assets"));

        var project = new EditorProject(new ProjectPaths(root));
        var file = Path.Combine(project.Paths.Assets, "Textures", "crate.txt");

        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, "the crate", Encoding.UTF8, TestContext.Current.CancellationToken);

        var workspace = new ProjectWorkspace(project.Paths);

        await ContentPipeline.ImportAsync(
            workspace,
            ProjectWorkspace.HostTarget,
            _ => { },
            cancellationToken: TestContext.Current.CancellationToken
        );

        var content = new EditorContent(project);

        owned.Add(content);

        Assert.True(content.Rebuild(), content.Refusal);
        Assert.NotNull(content.Assets);

        return content;
    }

    /// <summary>The same fallback the editor compiles, so the identity assertion means something.</summary>
    static Rendering.Material Grey() {
        var compilation = MaterialCompiler.Compile(
            new() {
                ShaderName = "ForwardPlus",
                Features = [
                    new MetalRoughnessFeature {
                        BaseColor = new Vector3(0.62f, 0.63f, 0.66f),
                        Metalness = 0f,
                        Roughness = 0.7f
                    }
                ]
            }
        );

        Assert.False(compilation.Failed, string.Join("; ", compilation.Diagnostics.Select(one => one.Message)));

        return compilation.Material!;
    }
}
