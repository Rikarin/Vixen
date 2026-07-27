// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Core.Yaml.Tests;

/// <summary>
///     The corpus test [08](../../docs/plan/08-asset-pipeline-and-addressables.md) asks for: every
///     fixture is read, written back, and compared byte for byte.
/// </summary>
/// <remarks>
///     This is the property that makes <c>.meta</c> files safe to rewrite. A migration touches every
///     file in a project; if reading and writing were not the identity, the resulting diff would be a
///     hundred thousand files of reformatting with the real change buried in it, and nobody would
///     review it. Everything the model carries — flow style, quoting, comments, key order — is
///     carried because of this test.
/// </remarks>
public sealed class RoundTripTests {
    public static TheoryData<string> Corpus {
        get {
            var data = new TheoryData<string>();

            foreach (var file in Directory.EnumerateFiles(CorpusDirectory, "*.yaml").Order(StringComparer.Ordinal)) {
                data.Add(Path.GetFileName(file));
            }

            return data;
        }
    }

    static string CorpusDirectory => Path.Combine(AppContext.BaseDirectory, "Corpus");

    [Theory]
    [MemberData(nameof(Corpus))]
    public void ReadingAndWritingAFixtureIsTheIdentity(string name) {
        var path = Path.Combine(CorpusDirectory, name);

        // Read as bytes and normalise line endings rather than trusting the checkout: .gitattributes
        // marks these LF, but a Windows clone with a misconfigured autocrlf would otherwise fail
        // this test for a reason that has nothing to do with the emitter.
        var expected = File.ReadAllText(path).ReplaceLineEndings("\n");
        var actual = YamlWriter.Write(YamlReader.Read(expected));

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void TheCorpusIsNotEmpty() => Assert.NotEmpty(Corpus);

    /// <summary>Reading what was just written gives the same tree, however the tree was built.</summary>
    [Fact]
    public void WritingAndReadingIsTheIdentityForATreeNobodyParsed() {
        var root = new YamlMapping()
            .Set("guid", new YamlScalar("9e8a44c9930c64e388ca034c5fe4c426"))
            .Set("metaVersion", new YamlScalar("1", YamlScalarStyle.Plain))
            .Set(
                "importer",
                new YamlMapping { Tag = "TextureImporter" }
                    .Set("version", new YamlScalar("3", YamlScalarStyle.Plain))
                    .Set("wrap", new YamlMapping().Set("u", new YamlScalar("Repeat")).Set("v", new YamlScalar("Repeat")))
            )
            .Set("labels", new YamlSequence().Add(new YamlScalar("ui")).Add(new YamlScalar("hd")));

        var text = YamlWriter.Write(root);

        Assert.Equal(
            """
            guid: 9e8a44c9930c64e388ca034c5fe4c426
            metaVersion: 1
            importer: !TextureImporter
              version: 3
              wrap: { u: Repeat, v: Repeat }
            labels: [ui, hd]

            """.ReplaceLineEndings("\n"),
            text
        );

        Assert.Equal(text, YamlWriter.Write(YamlReader.Read(text)));
    }
}
