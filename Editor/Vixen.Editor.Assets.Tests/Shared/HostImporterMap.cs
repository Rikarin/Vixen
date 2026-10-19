// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Assets;
using Xunit;

namespace Vixen.Testing.Importers;

/// <summary>
///     The extension → importer map every host's registry must have, written out by hand: the editor's,
///     <c>vixen</c>'s and the compiler worker's (#1300).
/// </summary>
/// <remarks>
///     <para>
///         <b>Why a literal list, and why one file linked into three test projects.</b> Since #1300 an
///         importer that lives beside its documents rather than in <c>Vixen.Editor.Assets</c> reaches a
///         registry because each host <i>links</i> it at start-up (<c>ImporterContributions.Linked</c>).
///         That is three registration calls in three processes, and dropping any one of them is the
///         defect this repository has shipped seven times in another shape: the format falls through
///         to <c>RawImporter</c>, imports as a byte blob, and nothing says so.
///         <c>EveryAttributedImporterTests</c> could not see it, because it scans the pipeline's own
///         assembly and an importer that has left it is invisible there. A list derived from the tree
///         would agree with whatever the tree does; this one was captured from the tree before
///         anything moved, so a move that changes any host's answer turns it red.
///     </para>
///     <para>
///         ⚠ <b>Type names, not full names</b>, so moving an importer between assemblies is not a change
///         to this list — the extension and the importer that claims it are what a project depends on,
///         and the <c>.meta</c> names the importer, not its namespace.
///     </para>
///     <para>
///         ⚠ <b>A host's map is built over a set its link method filled, never over the process-wide
///         one.</b> Three hosts link into one static in a test process, so a map read off it would be
///         green for a host that linked nothing as long as another host had.
///     </para>
/// </remarks>
static class HostImporterMap {
    /// <summary>The map, as <c>extension TypeName</c> lines; <c>-</c> for an importer chosen by name, <c>*</c> for the fallback.</summary>
    /// <remarks>Captured on master at 5f03c9b80, before any importer moved.</remarks>
    public static readonly string[] Expected = [
        "* RawImporter",
        "- FolderImporter",
        ".3ds ModelImporter",
        ".blend ModelImporter",
        ".bmp TextureImporter",
        ".cube CubeLutImporter",
        ".dae ModelImporter",
        ".dds TextureImporter",
        ".fbx ModelImporter",
        ".flac AudioImporter",
        ".gif TextureImporter",
        ".glb ModelImporter",
        ".gltf ModelImporter",
        ".hdr TextureImporter",
        ".hmpng HeightmapImporter",
        ".jpeg TextureImporter",
        ".jpg TextureImporter",
        ".ktx2 TextureImporter",
        ".m4v VideoImporter",
        ".mkv VideoImporter",
        ".mov VideoImporter",
        ".mp3 AudioImporter",
        ".mp4 VideoImporter",
        ".obj ModelImporter",
        ".ogg AudioImporter",
        ".ply ModelImporter",
        ".png TextureImporter",
        ".psd TextureImporter",
        ".r16 HeightmapImporter",
        ".stl ModelImporter",
        ".tga TextureImporter",
        ".vxanim AnimationClipImporter",
        ".vxbt BehaviorTreeImporter",
        ".vxcompositor CompositorImporter",
        ".vxconstraints ConstraintTemplateImporter",
        ".vxdef DefinitionImporter",
        ".vxeffect DefinitionImporter",
        ".vxfol TerrainAssetImporter",
        ".vxfoliage TerrainAssetImporter",
        ".vxgoap GoapDomainImporter",
        ".vxgrass TerrainAssetImporter",
        ".vxgroup NativeFormatImporter",
        ".vxharness HarnessPlanImporter",
        ".vxinput NativeFormatImporter",
        ".vxitem DefinitionImporter",
        ".vxlayer TerrainAssetImporter",
        ".vxloot DefinitionImporter",
        ".vxmat MaterialImporter",
        ".vxmixer MixerImporter",
        ".vxmoveset MoveSetImporter",
        ".vxnavmesh NavMeshImporter",
        ".vxnetrules NetworkRulesImporter",
        ".vxprefab SceneImporter",
        ".vxpriorities PriorityLadderImporter",
        ".vxproxyshapes ProxyShapeSetImporter",
        ".vxquery QueryImporter",
        ".vxquest DefinitionImporter",
        ".vxrecipe DefinitionImporter",
        ".vxscene SceneImporter",
        ".vxshadergraph ShaderGraphImporter",
        ".vxshapevocab ShapeVocabularyImporter",
        ".vxspline TerrainAssetImporter",
        ".vxutility UtilitySetImporter",
        ".vxvfx VfxImporter",
        ".vxwaves WaterWavesImporter",
        ".wav AudioImporter",
        ".wave AudioImporter",
        ".webm VideoImporter"
    ];

    /// <summary>The rows of <see cref="Expected" /> only a host's link provides; the pipeline's own registry has the rest.</summary>
    public static readonly string[] LinkedOnly = [
        ".hmpng HeightmapImporter",
        ".r16 HeightmapImporter",
        ".vxbt BehaviorTreeImporter",
        ".vxfol TerrainAssetImporter",
        ".vxfoliage TerrainAssetImporter",
        ".vxgoap GoapDomainImporter",
        ".vxgrass TerrainAssetImporter",
        ".vxlayer TerrainAssetImporter",
        ".vxquery QueryImporter",
        ".vxshadergraph ShaderGraphImporter",
        ".vxspline TerrainAssetImporter",
        ".vxutility UtilitySetImporter",
        ".vxvfx VfxImporter",
        ".vxwaves WaterWavesImporter"
    ];

    /// <summary>A registry's map, in <see cref="Expected" />'s form and order.</summary>
    /// <param name="registry">The registry.</param>
    /// <returns>The lines.</returns>
    public static string[] Of(ImporterRegistry registry) {
        ArgumentNullException.ThrowIfNull(registry);

        List<string> lines = [];

        foreach (var importer in registry.Importers) {
            var name = importer.GetType().Name;

            if (ReferenceEquals(importer, registry.Fallback)) {
                lines.Add("* " + name);
            } else if (importer.Extensions.Count == 0) {
                lines.Add("- " + name);
            } else {
                lines.AddRange(importer.Extensions.Select(extension => extension + " " + name));
            }
        }

        return [.. lines.Order(StringComparer.Ordinal)];
    }

    /// <summary>Asserts a host's registry has exactly <see cref="Expected" />, naming every difference.</summary>
    /// <param name="host">Which host, for the message.</param>
    /// <param name="link">The host's link method, handed a fresh set.</param>
    public static void AssertHost(string host, Action<ImporterContributions> link) {
        ArgumentNullException.ThrowIfNull(link);

        var linked = new ImporterContributions();

        link(linked);

        var actual = Of(BuiltInImporters.Create(linked, new ImporterContributions()));
        var missing = Expected.Except(actual, StringComparer.Ordinal).ToList();
        var extra = actual.Except(Expected, StringComparer.Ordinal).ToList();

        Assert.True(
            missing.Count == 0 && extra.Count == 0,
            $"The {host}'s importer registry does not have the map every host must have.\n"
            + $"Missing: {string.Join(", ", missing)}\nUnexpected: {string.Join(", ", extra)}\n"
            + "A missing row is a format this host imports as a byte blob: link the importer's assembly in "
            + "the host's LinkImporters (#1300). Full map:\n" + string.Join("\n", actual)
        );
    }
}
