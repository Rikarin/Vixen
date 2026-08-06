// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Core.Mathematics;
using Vixen.Core.Serialization;
using Vixen.Core.Yaml;
using Vixen.Editor.Assets.Models;
using Vixen.Geometry;
using Vixen.Rendering;
using Xunit;

namespace Vixen.Editor.Assets.Tests;

/// <summary>docs/plan/41 § D16's importer row and docs/plan/42 § D13's, from the settings to the chunks.</summary>
/// <remarks>
///     ⚠ <b>The two failure classes this suite exists for are a setting that is read and not honoured,
///     and a setting that does not reach the content hash.</b> The second is the quieter one: two
///     imports that differ only in remesh settings and hash the same compile to one cached artefact,
///     so the second asset silently gets the first one's geometry.
/// </remarks>
public class ModelRetopologyTests {
    /// <summary>An eight-vertex cube, which is the smallest thing the remesher will accept.</summary>
    const string Cube = """
        o Cube
        v -0.5 -0.5 -0.5
        v 0.5 -0.5 -0.5
        v 0.5 0.5 -0.5
        v -0.5 0.5 -0.5
        v -0.5 -0.5 0.5
        v 0.5 -0.5 0.5
        v 0.5 0.5 0.5
        v -0.5 0.5 0.5
        f 5 6 7
        f 5 7 8
        f 1 4 3
        f 1 3 2
        f 2 3 7
        f 2 7 6
        f 1 5 8
        f 1 8 4
        f 4 8 7
        f 4 7 3
        f 1 2 6
        f 1 6 5
        """;

    /// <summary>The setting is off by default, so an ordinary import is untouched by any of this.</summary>
    [Fact]
    public async Task Retopology_is_off_unless_it_is_asked_for() {
        var (_, plain) = await Import(new() { GenerateDistanceFields = false, GenerateMeshlets = false });
        var mesh = Mesh(plain);

        Assert.Equal(8, mesh.Positions.Length);
        Assert.Equal(36, mesh.Indices.Length);
    }

    /// <summary>Turning it on replaces the geometry with quads and keeps the mesh's name.</summary>
    [Fact]
    public async Task Retopologizing_replaces_the_geometry_and_keeps_the_name() {
        var (context, result) = await Import(
            new() {
                GenerateDistanceFields = false,
                GenerateMeshlets = false,
                Retopologize = true,
                RetopologyQuads = 150
            }
        );

        var mesh = Mesh(result);

        Assert.Equal("Cube", mesh.Name);
        Assert.NotEqual(8, mesh.Positions.Length);
        Assert.Contains(context.Diagnostics, entry => entry.Message.Contains("retopologised", StringComparison.Ordinal));
    }

    /// <summary>docs/plan/42's importer row: generate when the source has none.</summary>
    [Fact]
    public async Task Unwrapping_when_missing_produces_coordinates() {
        var (_, without) = await Import(new() { GenerateDistanceFields = false, GenerateMeshlets = false });

        Assert.Empty(Mesh(without).TexCoords);

        var (_, with) = await Import(
            new() {
                GenerateDistanceFields = false,
                GenerateMeshlets = false,
                Unwrap = UnwrapMode.WhenMissing,
                UnwrapResolution = 512
            }
        );

        var mesh = Mesh(with);

        Assert.Equal(mesh.Positions.Length, mesh.TexCoords.Length);

        foreach (var coordinate in mesh.TexCoords) {
            Assert.InRange(coordinate.X, -1e-3f, 1.001f);
            Assert.InRange(coordinate.Y, -1e-3f, 1.001f);
        }
    }

    /// <summary>The symmetry axis reaches the remesher rather than being parsed and dropped.</summary>
    [Fact]
    public async Task The_symmetry_axis_changes_what_is_produced() {
        var plain = Mesh(
            (await Import(
                new() {
                    GenerateDistanceFields = false, GenerateMeshlets = false,
                    Retopologize = true, RetopologyQuads = 150
                }
            )).Result
        );

        var mirrored = Mesh(
            (await Import(
                new() {
                    GenerateDistanceFields = false, GenerateMeshlets = false,
                    Retopologize = true, RetopologyQuads = 150, RetopologySymmetry = SymmetryAxis.X
                }
            )).Result
        );

        Assert.NotEqual(plain.Positions.Length, mirrored.Positions.Length);
    }

    /// <summary>The mapper produces the settings the fields name, including the plane.</summary>
    [Fact]
    public void The_mapper_carries_every_field() {
        var settings = new ModelImportSettings {
            RetopologyQuads = 321,
            RetopologyAdaptivity = 0.75f,
            RetopologyFeatureAngle = 22f,
            RetopologyKeepUvSeams = true,
            RetopologySymmetry = SymmetryAxis.Z
        };

        var remesh = settings.ToRemeshSettings();

        Assert.Equal(321, remesh.TargetQuads);
        Assert.Equal(0.75f, remesh.Adaptivity);
        Assert.Equal(22f, remesh.FeatureAngle);
        Assert.True(remesh.KeepUvSeams);
        Assert.Equal(new Plane(Vector3.UnitZ, 0f), remesh.Symmetry);

        var packing = settings with { UnwrapResolution = 2048, UnwrapMargin = 6, UnwrapTexelDensity = 512f };

        Assert.Equal(2048, packing.ToPackSettings().Resolution);
        Assert.Equal(6, packing.ToPackSettings().Margin);
        Assert.Equal(512f, packing.ToPackSettings().TexelDensity);
    }

    /// <summary>
    ///     <b>Two imports differing only in remesh settings must not hit the same cache entry.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The hash is taken over the <c>.meta</c>'s <c>importer</c> mapping rather than over
    ///         the settings object</b> — <c>ImportPipeline</c> serializes the authored YAML and hashes
    ///         that, skipping only <c>version</c> and <c>sourceHash</c>. So a new setting reaches the
    ///         key by being written into the meta at all, which is what <c>GenerateDistanceFields</c>
    ///         relies on and what this asserts still holds for the new ones.
    ///     </para>
    ///     <para>
    ///         It is asserted here at the level the pipeline computes it — the same
    ///         <see cref="ArtifactKey.HashOf(string)" /> over the same writer — rather than by running
    ///         two whole imports, because what can break is the serializer dropping a field and not the
    ///         hash function.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("retopologize: true", "retopologize: false")]
    [InlineData("retopologyQuads: 5000", "retopologyQuads: 900")]
    [InlineData("retopologySymmetry: None", "retopologySymmetry: X")]
    [InlineData("unwrap: Never", "unwrap: Always")]
    [InlineData("unwrapMargin: 4", "unwrapMargin: 12")]
    public void Two_settings_that_differ_hash_differently(string left, string right) {
        Assert.NotEqual(Hash(left), Hash(right));
    }

    /// <summary>And the same settings hash the same, or nothing would ever come out of the cache.</summary>
    [Fact]
    public void The_same_settings_hash_the_same() =>
        Assert.Equal(Hash("retopologyQuads: 900"), Hash("retopologyQuads: 900"));

    /// <summary>Every setting the record declares survives a round trip through the meta's YAML.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the failure that makes the hash test above pass and the setting still not
    ///     work.</b> A property the serializer skips is one the meta never carries, so it is neither
    ///     hashed nor read — <c>SplineAsset.Points</c> is the repository's own instance of exactly that,
    ///     recorded in its doc comment.
    /// </remarks>
    [Fact]
    public void The_settings_survive_the_meta() {
        var settings = new ModelImportSettings {
            Retopologize = true,
            RetopologyQuads = 777,
            RetopologyAdaptivity = 0.25f,
            RetopologySymmetry = SymmetryAxis.Y,
            RetopologyKeepUvSeams = true,
            RetopologyGuides = [new() { Spline = "Curves/spine.vxspline", Strength = 0.5f }],
            Unwrap = UnwrapMode.Always,
            UnwrapResolution = 2048,
            UnwrapMargin = 6,
            UnwrapTexelDensity = 256f
        };

        var written = YamlWriter.Write(YamlSerializer.Serialize(settings));
        var read = YamlSerializer.Deserialize<ModelImportSettings>(YamlReader.Read(written));

        Assert.True(read.Retopologize);
        Assert.Equal(777, read.RetopologyQuads);
        Assert.Equal(0.25f, read.RetopologyAdaptivity);
        Assert.Equal(SymmetryAxis.Y, read.RetopologySymmetry);
        Assert.True(read.RetopologyKeepUvSeams);
        Assert.Equal(UnwrapMode.Always, read.Unwrap);
        Assert.Equal(2048, read.UnwrapResolution);
        Assert.Equal(6, read.UnwrapMargin);
        Assert.Equal(256f, read.UnwrapTexelDensity);

        var guide = Assert.Single(read.RetopologyGuides);

        Assert.Equal("Curves/spine.vxspline", guide.Spline);
        Assert.Equal(0.5f, guide.Strength);
    }

    /// <summary>A guide that is not there is a warning and the import still produces a mesh.</summary>
    /// <remarks>
    ///     ⚠ <b>Unlike <c>vixen remesh --guide</c>, which refuses.</b> An import runs unattended over a
    ///     whole project and a curve that was renamed must not fail every model that ever mentioned
    ///     it; a person typing a path at a prompt has said what they want and would rather be told.
    /// </remarks>
    [Fact]
    public async Task A_guide_that_is_missing_is_a_warning_rather_than_a_refusal() {
        var (context, result) = await Import(
            new() {
                GenerateDistanceFields = false,
                GenerateMeshlets = false,
                Retopologize = true,
                RetopologyQuads = 150,
                RetopologyGuides = [new() { Spline = "Curves/nothing.vxspline" }]
            }
        );

        Assert.Contains(
            context.Diagnostics,
            entry => entry.Severity == ImportSeverity.Warning
                && entry.Message.Contains("nothing.vxspline", StringComparison.Ordinal)
        );

        Assert.NotEmpty(Mesh(result).Positions);
    }

    /// <summary>A guide asset is read, sampled and declared as a file dependency.</summary>
    /// <remarks>
    ///     <b>docs/plan/41 § D10's "an asset, not a paint session", made a fact about the cache.</b>
    ///     Without the declaration, editing the curve would leave every model that follows it importing
    ///     from cache — which is the staleness that looks exactly like the setting doing nothing.
    /// </remarks>
    [Fact]
    public async Task A_guide_asset_is_read_and_becomes_a_file_dependency() {
        var curve = new VirtualPath("/Curves/spine.vxspline");

        var (context, result) = await Import(
            new() {
                GenerateDistanceFields = false,
                GenerateMeshlets = false,
                Retopologize = true,
                RetopologyQuads = 150,
                RetopologyGuides = [new() { Spline = "Curves/spine.vxspline" }]
            },
            files => files.Seed(
                curve,
                Encoding.UTF8.GetBytes(
                    YamlWriter.Write(
                        YamlSerializer.Serialize(
                            SplineAsset.Through(
                                "spine",
                                [new Vector3(-0.5f, 0f, -0.5f), new Vector3(0.5f, 0f, -0.5f)]
                            )
                        )
                    )
                )
            )
        );

        Assert.Contains(curve, context.FileDependencies);
        Assert.DoesNotContain(context.Diagnostics, entry => entry.Severity == ImportSeverity.Warning);
        Assert.NotEmpty(Mesh(result).Positions);
    }

    /// <summary>A spline as a guide is sampled evenly along its length rather than by parameter.</summary>
    [Fact]
    public void A_spline_becomes_a_guide_sampled_by_distance() {
        var spline = SplineAsset
            .Through("s", [Vector3.Zero, new Vector3(1f, 0f, 0f), new Vector3(4f, 0f, 0f)])
            .Build();

        var guide = ModelRetopology.ToGuide(spline, 0.5f, 5);

        Assert.Equal(5, guide.Points.Count);
        Assert.Equal(0.5f, guide.Strength);

        var first = (guide.Points[1] - guide.Points[0]).Length();
        var last = (guide.Points[4] - guide.Points[3]).Length();

        // Even by distance: the two end segments are the same length even though the control points
        // are one unit apart at one end and three at the other.
        Assert.Equal(first, last, 2);
    }

    static MeshData Mesh(ImportResult result) =>
        Serializer.Read<MeshData>(
            Assert.Single(result.Artifacts, artifact => artifact.Type == ModelImporter.MeshType).Content.Span.ToArray()
        );

    static ObjectId Hash(string setting) =>
        ArtifactKey.HashOf(YamlWriter.Write(YamlReader.Read("!ModelImporter\n" + setting + "\n")));

    static async Task<(ImportContext Context, ImportResult Result)> Import(
        ModelImportSettings settings,
        Action<MemoryFileProvider>? seed = null
    ) {
        var path = new VirtualPath("/Assets/cube.obj");
        var files = new MemoryFileProvider();

        files.Seed(path, Encoding.UTF8.GetBytes(Cube));
        seed?.Invoke(files);

        var importer = new ModelImporter();

        // ⚠ The declared-reads check is off, because a guide's path is declared by the importer
        // during the run and the provider this test seeds has no project behind it.
        var context = new ImportContext(
            AssetId.New(),
            path,
            settings,
            files,
            importer.Name,
            "Windows",
            enforceDeclaredReads: false
        );

        return (context, await importer.ImportAsync(context, TestContext.Current.CancellationToken));
    }
}
