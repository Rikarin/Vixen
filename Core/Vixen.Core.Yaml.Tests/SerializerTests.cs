// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Reflection;
using Xunit;

namespace Vixen.Core.Yaml.Tests;

public sealed class SerializerTests {
    /// <summary>
    ///     The headline: <c>!TextureImporter</c> is a tag on a node declared as an interface, and the
    ///     generated registry says which type that is. No <c>Type.GetType</c>, no assembly scan —
    ///     which is what makes it work after trimming.
    /// </summary>
    [Fact]
    public void ATagChoosesTheTypeThroughTheGeneratedRegistry() {
        const string yaml = """
            guid: 9e8a44c9930c64e388ca034c5fe4c426
            metaVersion: 1
            importer: !TextureImporter
              version: 3
              colorSpace: Srgb
              maxSize: 1024
            address: ui/textures/hero
            labels: [ui, hd]
            """;

        var meta = YamlSerializer.Parse<AssetMetaFixture>(yaml);

        Assert.Equal(Guid.Parse("9e8a44c9930c64e388ca034c5fe4c426"), meta.Guid);
        var importer = Assert.IsType<TextureImportSettings>(meta.Importer);
        Assert.Equal(1024, importer.MaxSize);
        Assert.Equal(ColorSpace.Srgb, importer.ColorSpace);
        Assert.Equal(["ui", "hd"], meta.Labels);

        // And the defaults the record declares survive a document that does not mention them, which
        // is what keeps a .meta file short.
        Assert.Equal(TextureCompression.Bc7, importer.Compression);
        Assert.Equal(0.85f, importer.Quality);
    }

    /// <summary>And the tag is written back, because the declared type could not have implied it.</summary>
    [Fact]
    public void ATagIsWrittenWhenTheDeclaredTypeDoesNotSayWhichOneItIs() {
        var meta = new AssetMetaFixture {
            Guid = Guid.Parse("9e8a44c9930c64e388ca034c5fe4c426"),
            Importer = new TextureImportSettings { Version = 3, MaxSize = 1024 },
            Labels = ["ui"]
        };

        var yaml = YamlSerializer.ToYaml(meta);

        Assert.Contains("importer: !TextureImporter\n", yaml, StringComparison.Ordinal);
        Assert.Contains("guid: 9e8a44c9930c64e388ca034c5fe4c426\n", yaml, StringComparison.Ordinal);

        // The nested record's own members carry no tag: `wrap` is declared WrapModes and is one.
        Assert.Contains("wrap: { u: Repeat, v: Repeat }\n", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("!WrapModes", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void AValueSurvivesBeingWrittenAndReadBack() {
        var original = new TextureImportSettings {
            Version = 4,
            SourceHash = "8f3a2c91d04e5b76a1c8e5f2b73d9048",
            ColorSpace = ColorSpace.Linear,
            Usage = TextureUsage.Normal,
            GenerateMips = false,
            Wrap = new() { U = WrapMode.Clamp, V = WrapMode.Mirror },
            Anisotropy = 4,
            MaxSize = 512,
            Compression = TextureCompression.Astc6X6,
            Quality = 0.5f,
            Streaming = false,
            Overrides = [
                new() { Target = "Android", Compression = TextureCompression.Astc6X6, MaxSize = 1024 },
                new() { Target = "iOS", Compression = TextureCompression.Astc6X6 }
            ]
        };

        // Compared as documents rather than as records: a record's generated Equals compares an
        // array member by reference, so two settings with identical overrides are never equal to it.
        // The document is what has to survive anyway.
        var once = YamlSerializer.ToYaml(original);

        Assert.Equal(once, YamlSerializer.ToYaml(YamlSerializer.Parse<TextureImportSettings>(once)));
    }

    /// <summary>
    ///     A dictionary and a list, which is where the AOT problem lives: neither can be built with
    ///     <c>Array.CreateInstance</c> or <c>MakeGenericType</c> on a phone, so both come from
    ///     constructors the reflection generator wrote.
    /// </summary>
    [Fact]
    public void ADictionaryAndAListSurviveTheRoundTrip() {
        var original = new ModelImportSettings {
            Scale = 0.01f,
            MaterialMapping = new() {
                ["Body"] = "vx:9e8a44c9930c64e388ca034c5fe4c426",
                ["Cloth"] = "vx:c1d2e3f4a5b60718c1d2e3f4a5b60718"
            },
            Lods = ["high", "medium", "low"]
        };

        var yaml = YamlSerializer.ToYaml(original);
        var read = YamlSerializer.Parse<ModelImportSettings>(yaml);

        Assert.Equal(original.MaterialMapping, read.MaterialMapping);
        Assert.Equal(original.Lods, read.Lods);
        Assert.Equal(0.01f, read.Scale);
    }

    /// <summary>
    ///     The constructors are registered by the generator that saw the member, so every collection
    ///     type reachable from a described type is there before any code runs.
    /// </summary>
    [Fact]
    public void EveryCollectionTypeAMemberDeclaresHasAConstructor() {
        Assert.True(CollectionFactory.TryCreate(typeof(TargetOverride[]), 3, out var overrides));
        Assert.Equal(3, ((TargetOverride[])overrides).Length);

        Assert.True(CollectionFactory.TryCreate(typeof(List<string>), 4, out var lods));
        Assert.Empty((List<string>)lods);

        Assert.True(CollectionFactory.TryCreate(typeof(Dictionary<string, string>), 2, out var mapping));
        Assert.Empty((Dictionary<string, string>)mapping);

        Assert.True(CollectionFactory.TryCreate(typeof(string[]), 2, out _));
    }

    /// <summary>
    ///     camelCase on write, and lenient on read: these files are hand-edited, and someone who
    ///     typed <c>MaxSize</c> meant <c>maxSize</c>. The next write puts it back in the canonical
    ///     form.
    /// </summary>
    [Fact]
    public void KeysAreCamelCaseOnWriteAndCaseInsensitiveOnRead() {
        var read = YamlSerializer.Parse<TextureImportSettings>("MaxSize: 256\nCOLORSPACE: Linear\n");

        Assert.Equal(256, read.MaxSize);
        Assert.Equal(ColorSpace.Linear, read.ColorSpace);
        Assert.Contains("maxSize: 256\n", YamlSerializer.ToYaml(read), StringComparison.Ordinal);
    }

    /// <summary>
    ///     An unknown key is ignored, because a project opened in an older editor after someone added
    ///     an import setting must still load. Dropping it silently is the other failure, so the caller
    ///     is told.
    /// </summary>
    [Fact]
    public void AnUnknownKeyIsIgnoredAndReported() {
        var unknown = new List<string>();
        var options = YamlSerializerOptions.Default with { OnUnknownKey = unknown.Add };

        var read = YamlSerializer.Parse<AssetMetaFixture>(
            "metaVersion: 1\nimporter: !TextureImporter\n  maxSize: 64\n  futureSetting: 12\nquantumFlux: true\n",
            options
        );

        Assert.Equal(64, Assert.IsType<TextureImportSettings>(read.Importer).MaxSize);
        Assert.Equal(["importer.futureSetting", "quantumFlux"], unknown);
    }

    /// <summary>
    ///     Omitting defaults is available and off. A file listing only what differs would change shape
    ///     whenever a default changed, which is a diff on files nobody touched.
    /// </summary>
    [Fact]
    public void OmittingDefaultsIsAChoiceAndNotTheDefault() {
        var settings = new TextureImportSettings { MaxSize = 512 };

        Assert.Contains("compression: Bc7\n", YamlSerializer.ToYaml(settings), StringComparison.Ordinal);

        var sparse = YamlSerializer.ToYaml(settings, YamlSerializerOptions.Default with { OmitDefaults = true });

        Assert.Equal("maxSize: 512\n", sparse);
    }

    [Fact]
    public void NullIsWrittenAndReadAsNull() {
        var meta = new AssetMetaFixture { MetaVersion = 2 };
        var yaml = YamlSerializer.ToYaml(meta);

        Assert.Contains("importer: null\n", yaml, StringComparison.Ordinal);
        Assert.Null(YamlSerializer.Parse<AssetMetaFixture>(yaml).Importer);
    }

    [Fact]
    public void ATagThatNamesNothingSaysSoRatherThanFailingObscurely() {
        var failure = Assert.Throws<YamlBindingException>(
            () => YamlSerializer.Parse<AssetMetaFixture>("importer: !GoneImporter\n  version: 1\n")
        );

        Assert.Equal("importer", failure.Path);
        Assert.Contains("GoneImporter", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAbstractMemberWithNoTagSaysWhatIsMissing() {
        var failure = Assert.Throws<YamlBindingException>(
            () => YamlSerializer.Parse<AssetMetaFixture>("importer:\n  version: 1\n")
        );

        Assert.Contains("needs a type tag", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ATagNamingTheWrongKindOfTypeIsRefused() {
        var failure = Assert.Throws<YamlBindingException>(
            () => YamlSerializer.Parse<AssetMetaFixture>("importer: !WrapModes\n  u: Clamp\n")
        );

        Assert.Contains("not a IImportSettings", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AValueThatIsNotTheRightShapeNamesWhereItWent() {
        var failure = Assert.Throws<YamlBindingException>(
            () => YamlSerializer.Parse<TextureImportSettings>("maxSize: enormous\n")
        );

        Assert.Equal("maxSize", failure.Path);
        Assert.Contains("'enormous' is not a Int32", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     <see cref="System.Collections.Immutable.ImmutableArray{T}" /> is refused by name, because
    ///     building one for a runtime-known element type needs <c>MakeGenericMethod</c> — which is
    ///     exactly what NativeAOT does not have. Failing here with an explanation beats binding on a
    ///     desktop and throwing on a phone.
    /// </summary>
    [Fact]
    public void AnImmutableArrayIsRefusedWithTheReason() {
        var failure = Assert.Throws<YamlBindingException>(
            () => YamlSerializer.Deserialize<System.Collections.Immutable.ImmutableArray<string>>(
                new YamlSequence().Add(new YamlScalar("a"))
            )
        );

        Assert.Contains("NativeAOT", failure.Message, StringComparison.Ordinal);
        Assert.Contains("T[]", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The whole of doc 08's worked texture example, bound and written back, so that the schema
    ///     the document specifies is the one this actually reads.
    /// </summary>
    [Fact]
    public void TheWorkedExampleFromTheDesignBindsAndComesBack() {
        const string yaml = """
            guid: 9e8a44c9930c64e388ca034c5fe4c426
            metaVersion: 1
            importer: !TextureImporter
              version: 3
              sourceHash: 8f3a2c91d04e5b76a1c8e5f2b73d9048
              colorSpace: Srgb
              usage: Albedo
              generateMips: true
              wrap: { u: Repeat, v: Repeat }
              anisotropy: 8
              maxSize: 2048
              compression: Bc7
              quality: 0.85
              streaming: true
              overrides:
                - target: Android
                  compression: Astc6X6
                  maxSize: 1024
                - target: iOS
                  compression: Astc6X6
            address: ui/textures/hero
            labels: [ui, hd]
            """;

        var meta = YamlSerializer.Parse<AssetMetaFixture>(yaml);
        var importer = Assert.IsType<TextureImportSettings>(meta.Importer);

        Assert.Equal(2, importer.Overrides.Length);
        Assert.Equal("Android", importer.Overrides[0].Target);
        Assert.Equal(1024, importer.Overrides[0].MaxSize);
        Assert.Null(importer.Overrides[1].MaxSize);

        var written = YamlSerializer.ToYaml(meta);

        Assert.Equal(written, YamlSerializer.ToYaml(YamlSerializer.Parse<AssetMetaFixture>(written)));
    }
}
