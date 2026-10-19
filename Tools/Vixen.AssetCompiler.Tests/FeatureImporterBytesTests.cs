// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Editor.Assets;
using Xunit;

namespace Vixen.AssetCompiler.Tests;

/// <summary>
///     Every feature importer leaving <c>BuiltInImporters</c> for its feature writes the bytes it always
///     did, in this process and in a worker (#1300's remainder).
/// </summary>
/// <remarks>
///     <para>
///         <b><see cref="LinkedImporterTests" />' shape, one asset per importer.</b> An importer that moves
///         between assemblies keeps its <c>DataContract</c> alias, so every <c>.meta</c> still names it —
///         and it must keep every byte it writes, or a move is a content change a project ships without
///         anybody deciding to. The digests were captured on <c>c40c2ea93</c>, before any of these left
///         <c>Vixen.Editor.Assets</c>.
///     </para>
///     <para>
///         ⚠ <b>The worker half is the one a move can break on its own.</b> A worker is a process that
///         links what <c>WorkerHost</c> links and nothing a test linked here; a format whose host link was
///         forgotten fails there with "this build has no importer called …", which is the red this
///         theory is for.
///     </para>
///     <para>
///         ⚠ <b>Seven of the fourteen have moved</b> — the terrain, water and AI importers. The seven
///         animation ones are still the pipeline's, waiting on a decision about their home (#1499), and
///         are pinned here so that whichever home is chosen is held to these bytes.
///     </para>
///     <para>
///         The sources are the samples' own files where one is committed and a small literal otherwise,
///         each the smallest file the importer's own suite shows compiling cleanly.
///     </para>
/// </remarks>
public sealed class FeatureImporterBytesTests : IDisposable {
    /// <summary>Importer, file under <c>Assets/</c>, and the SHA-256 of every chunk it writes, in order.</summary>
    static readonly (string Importer, string File, string Digest)[] Pinned = [
        ("TerrainAssetImporter", "Meadow.vxgrass", "GrassType:9C6CB8D8CD0E3E8BB0D64EECD36558C628E0A9EAAB74A286C30A0E9E668A6372"),
        ("HeightmapImporter", "Ridge.r16", "Heightmap:B78FBEBFEED89E0E6919ACA8884E199BFCB8553807D1D19E38971469D8D31871"),
        ("WaterWavesImporter", "Lake.vxwaves", "WaterWavesAsset:A00073FF382B05C91710D731BC3703464EB6633CB4115BDEF18B9FB327FED5A9"),
        ("BehaviorTreeImporter", "Guard.vxbt", "BehaviorTreeContent:C624589B979F4629E08968FE4DED70F234A68AEFDF8CD74BE8DF4BBBD44337A3"),
        ("UtilitySetImporter", "Guard.vxutility", "UtilitySetContent:D26B490FBCC33186E523A53847EACF48DFAC6267C8A026C68EB4192FE60747E4"),
        ("QueryImporter", "Cover.vxquery", "QueryContent:C7D44D062583F48202E76256BB61CAD56C5D7DF211B588FE4994277A2A75B64F"),
        ("GoapDomainImporter", "Guard.vxgoap", "GoapDomainContent:CE740D6D13E0443CCF7590153595CFC2F6E113C708DCA8DB8508E8729E6CB284"),
        ("AnimationClipImporter", "idle.vxanim", "AnimationClipContent:D49D271241E22757E0908E0C1C7177BD1AE78CFE6A5B132329A3887B09A8D010"),
        ("ShapeVocabularyImporter", "humanoid.vxshapevocab", "ShapeVocabularyContent:C59BDCF87F25941F8E2FFDDF9D2DAA93A6D240B8E9A432A654549D420106ADC7"),
        ("ProxyShapeSetImporter", "Body.vxproxyshapes", "ProxyShapeSetContent:A9F734DDAE25BC3F9BF73EF9BCE5D17BFBBDB7DBD578AE33CF27852BAB511488"),
        ("PriorityLadderImporter", "swimming.vxpriorities", "PriorityLadderContent:1A9A975BF21F17AB646A5D30C7DE0E18ED5ED2BD21AB85E27186768AF3DAF556"),
        ("ConstraintTemplateImporter", "seated.vxconstraints", "ConstraintTemplateContent:270623B8595D4BD542D120CB804B87E97165AD7689F81973F8CC21DFE575042C"),
        ("HarnessPlanImporter", "rail.vxharness", "HarnessPlanContent:3F626342430E3EC0F308F5ABC129072AAB598909E6143BFB380060F95B920239"),
        ("MoveSetImporter", "locomotion.vxmoveset", "MoveSetContent:04BFC758C67CE1C477CEA88F62782D1D17AA5EBD4DE7514C42F94083ED33A94B")
    ];

    readonly string project = Path.Combine(Path.GetTempPath(), $"vixen-feature-importers-{Environment.ProcessId}-{Guid.NewGuid():N}");

    public FeatureImporterBytesTests() {
        var assets = Path.Combine(project, "Assets");

        Directory.CreateDirectory(assets);

        File.Copy(Path.Combine(Root(), "Samples", "03-PbrShowcase", "Assets", "Terrain", "Meadow.vxgrass"), Path.Combine(assets, "Meadow.vxgrass"));
        File.Copy(Path.Combine(Root(), "Samples", "13-ThirdPersonShooter", "Assets", "Water", "Lake.vxwaves"), Path.Combine(assets, "Lake.vxwaves"));
        File.Copy(Path.Combine(Root(), "Samples", "13-ThirdPersonShooter", "Assets", "Animation", "idle.vxanim"), Path.Combine(assets, "idle.vxanim"));

        // A 4 × 4 ramp of 16-bit samples, which is a square and so needs no dimensions in its settings.
        File.WriteAllBytes(Path.Combine(assets, "Ridge.r16"), [.. Enumerable.Range(0, 16).SelectMany(sample => BitConverter.GetBytes((ushort)(sample * 4000)))]);

        foreach (var (file, text) in Sources) {
            File.WriteAllText(Path.Combine(assets, file), text, Encoding.UTF8);
        }
    }

    public void Dispose() => PluginImporterTests.Discard(() => Directory.Delete(project, recursive: true));

    /// <summary>Each moved importer, two processes, the bytes captured before the move.</summary>
    [Fact]
    public async Task Every_moved_feature_importer_writes_the_same_bytes_here_and_in_a_worker() {
        var linked = new ImporterContributions();

        WorkerHost.LinkImporters(linked);

        var here = new InProcessImportExecutor(
            BuiltInImporters.Create(linked, new ImporterContributions()),
            new PhysicalFileProvider(project, isReadOnly: true)
        );

        using var pool = new CompilerPool(project, workers: 1, contributed: new ImporterContributions());

        List<string> wrong = [];

        foreach (var (importer, file, digest) in Pinned) {
            var job = new ImportJob(AssetId.New(), importer, new VirtualPath("/Assets/" + file), string.Empty, "Windows", EnforceDeclaredReads: true);
            var local = await here.ExecuteAsync(job, Cancellation);
            var remote = await pool.ExecuteAsync(job, Cancellation);

            Assert.True(local.Succeeded, $"{file} in this process:\n{Why(local)}");
            Assert.True(remote.Succeeded, $"{file} in a worker:\n{Why(remote)}");
            Assert.NotEmpty(local.Artifacts);

            var mine = Digest(local);

            if (mine != digest) {
                wrong.Add($"(\"{importer}\", \"{file}\", \"{mine}\") in this process, pinned \"{digest}\"");
            }

            if (Digest(remote) != digest) {
                wrong.Add($"{file} in a worker: {Digest(remote)}");
            }
        }

        Assert.True(
            wrong.Count == 0,
            "A feature importer wrote bytes other than the ones captured before it moved — a move changed what a "
            + "project ships:\n" + string.Join("\n", wrong)
        );
    }

    /// <summary>The literal sources, for the formats no sample commits.</summary>
    static readonly (string File, string Text)[] Sources = [
        ("Guard.vxbt", """
            name: Guard
            root:
              name: Root
              type: Sequence
              children:
                - name: Wait
                  type: Wait
            """),
        ("Guard.vxutility", """
            name: Guard
            actions:
              - name: Idle
                task: Wait
            """),
        ("Cover.vxquery", """
            name: Cover
            generators:
              - extent: 12
            """),
        ("Guard.vxgoap", """
            name: Guard
            keys:
              - name: armed
                source: Constant
            actions:
              - name: Arm
                task: Wait
                effects:
                  - key: armed
            goals:
              - name: BeArmed
                conditions:
                  - key: armed
                    comparison: GreaterOrEqual
                    value: 1
            """),
        ("humanoid.vxshapevocab", """
            name: humanoid
            shapes:
              - name: belly
                meaning: The front of the torso, between the ribs and the hips.
              - name: right-palm
                meaning: The gripping face of the right hand.
            tags:
              - tag: affords=grip-surface
                meaning: A hand may close on it.
            classes:
              - name: humanoid
                members:
                  - name: belly
                    kind: Sphere
                    extents: 0.2 0.2 0.2
                    required: true
                  - name: right-palm
                    kind: Box
                    tags: [affords=grip-surface]
                    extents: 0.04 0.02 0.08
                    required: true
            """),
        ("Body.vxproxyshapes", """
            name: Body
            vocabulary: /Assets/humanoid.vxshapevocab
            class: humanoid
            shapes:
              - name: belly
                kind: Sphere
                joint: Spine
                position: 0 0.1 0.05
                extents: 0.22 0.22 0.22
              - name: right-palm
                kind: Box
                joint: Wrist
                extents: 0.04 0.02 0.08
                tags: [affords=grip-surface]
                coarse: true
            """),
        ("swimming.vxpriorities", """
            name: swimming
            step: 100
            rungs:
              - name: flourish
                value: 0
                meaning: A secondary motion.
              - name: contact
                value: 400
                meaning: A hand on the ladder rung.
            """),
        ("seated.vxconstraints", """
            name: seated
            revision: 2
            meaning: A character sitting.
            tags:
              - name: right hand
                kind: Position
                effector: hand_r
                begin: 0.0
                end: 0.5
              - name: hips
                kind: Position
                effector: pelvis
                begin: 0.0
                end: 1.0
            """),
        ("rail.vxharness", """
            name: reach the rail
            clip: Assets/Anim/Reach.vxanim
            rig: Assets/Bodies/Hero.gltf
            shapes: Assets/Bodies/Hero.vxproxyshapes
            samples: 16
            bodies: [0.8, 1.0, 1.25]
            thresholds:
              residual: 0.02
              penetration: 0.01
              reach: true
            """),
        ("locomotion.vxmoveset", """
            name: locomotion
            entries:
              - name: walk
                clip: Assets/Anim/Walk.vxanim
                speed: 1.4
                facets:
                  - key: role
                    value: loop
              - name: idle
                clip: Assets/Anim/Idle.vxanim
                facets:
                  - key: role
                    value: idle
            """)
    ];

    static string Digest(ExecutedImport import) =>
        string.Join("+", import.Artifacts.Select(chunk => chunk.Type + ":" + Convert.ToHexString(SHA256.HashData(chunk.Content.Span))));

    static string Root() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent) {
            if (File.Exists(Path.Combine(directory.FullName, "Vixen.slnx"))) {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The repository root, which holds Vixen.slnx, is above no directory this test runs in.");
    }

    static string Why(ExecutedImport import) =>
        string.Join(
            "\n",
            import.Diagnostics.Select(diagnostic =>
                string.Create(CultureInfo.InvariantCulture, $"{diagnostic.Severity}: {diagnostic.Message}")
            )
        );

    static CancellationToken Cancellation => TestContext.Current.CancellationToken;
}
