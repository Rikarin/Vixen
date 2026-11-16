// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Vixen.Interchange.Tests;

/// <summary>
///     <see cref="ModelReader.Revision" /> is raised whenever what the reader makes of a file's bytes changes, as far as
///     a test can see that: the post-processing it asks Assimp for, and what ZBrush polypaint is written as (Vixen
///     #1649).
/// </summary>
/// <remarks>
///     ⚠ <b>A host keys what it made from a model on the file's bytes and this revision</b>, a bake above all, and the
///     bytes do not change when the reader does. A reader that reads otherwise under the same revision hands back a
///     cached bake of the old reading, which nothing on screen gives away.
/// </remarks>
public sealed class ModelReaderRevisionTests {
    /// <summary>
    ///     Every digest the reader has had, with the revision it was read under. A change to what the digest covers adds
    ///     a line here with a new <see cref="ModelReader.Revision" />, never edits a line or reuses a revision.
    /// </summary>
    static readonly Dictionary<string, string> Known = new(StringComparer.Ordinal) {
        ["af3c2aeeab7290e9ba3753c2750023c5c9ad053e4b08cfe65fe8d57592908c7b"] = "2026-11-16 polypaint"
    };

    /// <summary>
    ///     An OBJ with polypaint, spaced and unspaced groups, a vertex that names its own colour and a group that is no
    ///     hex, so every way <c>ZBrushPolypaint</c> reads a line counts in the digest.
    /// </summary>
    const string Painted = "v 0 0 0\r\nv 1 0 0 0.5 0.5 0.5\nv 1 1 0\nv 0 1 0\n"
        + "#MRGB ffff00008000ff00 000000ff\n#MRGB ff123456zz000000\nvt 0 0\nf 1 2 3\nf 1 3 4\n";

    /// <summary>The current reader's digest belongs to the current revision, and no revision has two digests.</summary>
    [Fact]
    public void The_revision_is_raised_with_every_change_to_what_the_reader_makes_of_a_file() {
        var digest = Digest();

        Assert.True(
            Known.TryGetValue(digest, out var revision) && revision == ModelReader.Revision,
            $"What the model reader makes of a file changed (digest {digest}) and ModelReader.Revision did not, or it "
            + "is not in Known. Raise ModelReader.Revision to the date of the change, then add the digest to Known "
            + "with it, so a host's cache keyed on the revision reads files again."
        );
        Assert.Equal(Known.Count, Known.Values.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>The post-processing flags for every setting that changes them, then what polypaint is written as.</summary>
    static string Digest() {
        var said = new StringBuilder();

        foreach (var normals in new[] { false, true }) {
            foreach (var tangents in new[] { false, true }) {
                var options = new ModelReadOptions { GenerateNormals = normals, GenerateTangents = tangents };

                said.Append(ModelReader.Flags(options).ToString("x8", CultureInfo.InvariantCulture)).Append('\n');
            }
        }

        said.Append(Encoding.UTF8.GetString(ZBrushPolypaint.Carry(Encoding.ASCII.GetBytes(Painted)) ?? []));

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(said.ToString())));
    }
}
