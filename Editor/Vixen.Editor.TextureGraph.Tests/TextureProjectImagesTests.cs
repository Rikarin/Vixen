// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Core.Imaging;
using Vixen.Editor.Core;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Xunit;

namespace Vixen.Editor.TextureGraph.Tests;

/// <summary>
///     The resolver that turns a plan's external image into a project's own picture, and every way it
///     answers when it cannot.
/// </summary>
/// <remarks>
///     <para>
///         <b><a href="https://github.com/Rikarin/Vixen/issues/1087">#1087</a>.</b> This was 313
///         <see langword="internal" /> lines in <c>Vixen.Editor.Texturing</c>, so
///         <c>vixen texture bake --graph</c> refused every graph reading an imported PNG. The
///         end-to-end proof is <c>TextureCommandTests</c>, on a device, through the command line;
///         these are the answers a picture could never show.
///     </para>
///     <para>
///         ⚠ <b>No decoder anywhere in this file, which is the design rather than a shortcut.</b>
///         <c>ImageDecoders</c> lives in <c>Vixen.Editor.Assets</c>, whose closure adds twenty-five
///         assemblies to this one's, so the read is a callback the host supplies — and a test
///         handing back a fabricated <see cref="TextureData" /> is exercising exactly the seam a
///         host uses rather than a stand-in for one.
///     </para>
///     <para>
///         ⚠ <b><c>NullDevice</c> is enough and the assertions say why.</b> Nothing here reads a
///         texel back: what is asserted is which file the resolver asked for, which sentence it
///         returned, and whether <see cref="TextureUploads" /> ended up holding the image. A null
///         device answers all three honestly and would answer a pixel comparison with two black
///         images, which is why the pixel lives in the CLI's device test instead.
///     </para>
/// </remarks>
public sealed class TextureProjectImagesTests : IDisposable {
    const string Reference = "Assets/Rust.png";

    readonly string root = Path.Combine(
        Path.GetTempPath(),
        "vixen-project-images",
        Guid.NewGuid().ToString("N")[..12]
    );

    readonly NullDevice device = new(new());

    public TextureProjectImagesTests() => Directory.CreateDirectory(Path.Combine(root, "Assets"));

    public void Dispose() {
        device.Dispose();

        try {
            if (Directory.Exists(root)) {
                Directory.Delete(root, recursive: true);
            }
        } catch (IOException) {
            // A temporary directory that would not go is not a test failure.
        }
    }

    /// <summary>An imported picture is asked for by its own absolute path and uploaded.</summary>
    /// <remarks>
    ///     ⚠ <b>The path the callback is handed is asserted, not only that one arrived.</b> A
    ///     resolver that passed the reference through unresolved would still upload — the host would
    ///     open the wrong file or none — and every other assertion here would be identical.
    /// </remarks>
    [Fact]
    public void An_imported_picture_is_read_from_the_projects_own_file_and_uploaded() {
        var project = Project();
        var plan = Plan();

        using TextureUploads uploads = new(device);

        var asked = new List<string>();

        var why = TextureProjectImages.Resolve(
            project,
            uploads,
            plan,
            new(0, default, Reference, 8, 8, []),
            file => {
                asked.Add(file);

                return (Picture(PixelFormat.Rgba8UNorm, 8, 8), null);
            }
        );

        Assert.Null(why);
        Assert.Equal(Path.Combine(root, "Assets", "Rust.png"), Assert.Single(asked));
        Assert.True(uploads.Externals.ContainsKey(0), "the plan's external image was not filled");
    }

    /// <summary>A reference nothing has imported is named, and the project is named as the reason.</summary>
    [Fact]
    public void A_reference_no_asset_matches_is_refused_without_touching_the_disk() {
        var project = Project(import: false);
        var plan = Plan();

        using TextureUploads uploads = new(device);

        var asked = 0;

        var why = TextureProjectImages.Resolve(
            project,
            uploads,
            plan,
            new(0, default, Reference, 8, 8, []),
            _ => {
                asked++;

                return (Picture(PixelFormat.Rgba8UNorm, 8, 8), null);
            }
        );

        Assert.NotNull(why);
        Assert.Contains("is not in this project's assets", why, StringComparison.Ordinal);
        Assert.Equal(0, asked);
        Assert.Empty(uploads.Externals);
    }

    /// <summary>⚠ A reference carrying a scheme is refused rather than looked up as a path.</summary>
    /// <remarks>
    ///     <b>What keeps the split honest.</b> <c>meshmap:</c> and <c>vxpaint:</c> are the texturing
    ///     plugin's, because both name something a live editor session supplies; a host that cannot
    ///     fill one has to say so rather than report a missing file called
    ///     <c>meshmap:curvature</c>. The theory names both of the schemes that exist and one that
    ///     does not, because the rule is about the shape and not about a list somebody maintains.
    /// </remarks>
    [Theory]
    [InlineData("meshmap:curvature")]
    [InlineData("vxpaint:Wall.vxpaint#baseColor")]
    [InlineData("clipboard:latest")]
    public void A_reference_naming_a_session_is_refused_as_one(string reference) {
        var project = Project();
        var plan = Plan();

        using TextureUploads uploads = new(device);

        var why = TextureProjectImages.Resolve(
            project,
            uploads,
            plan,
            new(0, default, reference, 8, 8, []),
            _ => (Picture(PixelFormat.Rgba8UNorm, 8, 8), null)
        );

        Assert.NotNull(why);
        Assert.Contains(reference, why, StringComparison.Ordinal);
        Assert.DoesNotContain("is not in this project's assets", why, StringComparison.Ordinal);
    }

    /// <summary>A colon after a separator is not a scheme, whatever a reference happens to carry.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The other half of the scheme rule, and the half a one-sided test would miss.</b>
    ///         A rule that called everything a scheme would pass every assertion above and refuse
    ///         every real graph — the failure mode is silent because the sentence looks deliberate.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Asserted on the reference and not on a folder, because the first version of this
    ///         case made a directory called <c>Set:A</c> — which Windows refuses, so it threw in its
    ///         first statement on one of CI's three legs.</b> The property is decided before any
    ///         file system is involved: <c>SchemeOf</c> stops at the first separator, so a colon
    ///         after one cannot name a scheme. Going to disk to prove that tested the platform
    ///         rather than the rule.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("Assets/Set A/Rust:1.png")]
    [InlineData("Assets/Set A/12:34.png")]
    public void A_colon_after_a_separator_is_still_a_path(string reference) {
        var project = new EditorProject(new(root));

        project.Assets.Scan();

        using TextureUploads uploads = new(device);

        var why = TextureProjectImages.Resolve(
            project,
            uploads,
            Plan(),
            new(0, default, reference, 8, 8, []),
            _ => (Picture(PixelFormat.Rgba8UNorm, 8, 8), null)
        );

        // ⚠ Not `Assert.Null`: the file is not in the project, so a refusal is correct. What this
        // case is about is *which* refusal — the scheme sentence would mean the rule had claimed a
        // colon it should have walked past.
        Assert.NotNull(why);
        Assert.Contains("is not in this project's assets", why, StringComparison.Ordinal);
    }

    /// <summary>A picture in a format the plan's slot cannot hold is refused by name.</summary>
    /// <remarks>
    ///     ⚠ <b>Before the upload, so the message is about the file rather than about a byte
    ///     count.</b> A block-compressed asset has the wrong length for the Rgba8 image it would
    ///     fill, and <c>TextureUploads.Add</c> would refuse it with arithmetic nobody can act on.
    /// </remarks>
    [Fact]
    public void A_picture_that_is_not_rgba8_is_named_rather_than_counted_in_bytes() {
        var project = Project();
        var plan = Plan();

        using TextureUploads uploads = new(device);

        var why = TextureProjectImages.Resolve(
            project,
            uploads,
            plan,
            new(0, default, Reference, 8, 8, []),
            _ => (Picture(PixelFormat.Rgba16Float, 8, 8), null)
        );

        Assert.NotNull(why);
        Assert.Contains(Reference, why, StringComparison.Ordinal);
        Assert.Contains("Rgba8", why, StringComparison.Ordinal);
        Assert.Empty(uploads.Externals);
    }

    /// <summary>Whatever the host said about a file it could not read is carried through.</summary>
    [Fact]
    public void A_host_that_could_not_read_the_file_has_its_own_reason_repeated() {
        var project = Project();
        var plan = Plan();

        using TextureUploads uploads = new(device);

        var why = TextureProjectImages.Resolve(
            project,
            uploads,
            plan,
            new(0, default, Reference, 8, 8, []),
            _ => (null, "nothing here decodes '.png'.")
        );

        Assert.NotNull(why);
        Assert.Contains("nothing here decodes '.png'.", why, StringComparison.Ordinal);
    }

    /// <summary>⚠ Every entry is answered, not only the first.</summary>
    /// <remarks>
    ///     <b>A host that stopped at the first would send an author round the loop once per missing
    ///     picture</b>, which for a stack moved between projects is once per layer. The two entries
    ///     fail for different reasons on purpose: a loop that reported the same sentence twice would
    ///     satisfy a count.
    /// </remarks>
    [Fact]
    public void Every_unfilled_external_gets_its_own_sentence() {
        var project = Project();
        var plan = Plan(2);

        using TextureUploads uploads = new(device);

        var unresolved = TextureProjectImages.Fill(
            project,
            uploads,
            plan,
            [new(0, default, "meshmap:curvature", 8, 8, []), new(1, default, "Assets/Missing.png", 8, 8, [])],
            _ => (Picture(PixelFormat.Rgba8UNorm, 8, 8), null)
        );

        Assert.Equal(2, unresolved.Count);
        Assert.Contains("meshmap:curvature", unresolved[0], StringComparison.Ordinal);
        Assert.Contains("Assets/Missing.png", unresolved[1], StringComparison.Ordinal);
    }

    /// <summary>A project on disk, scanned, with the imported picture in it unless asked otherwise.</summary>
    EditorProject Project(bool import = true) {
        if (import) {
            // The bytes never matter: the decode is the host's callback and this file is only ever
            // opened by one. What has to be true is that the database indexes it.
            File.WriteAllBytes(Path.Combine(root, "Assets", "Rust.png"), [1, 2, 3, 4]);
        }

        var project = new EditorProject(new(root));

        project.Assets.Scan();

        return project;
    }

    /// <summary>A plan whose images are all external and all 8×8 Rgba8.</summary>
    static TexturePlan Plan(int images = 1) =>
        new() {
            BaseWidth = 8,
            BaseHeight = 8,
            Images = [.. Enumerable.Repeat(new TextureImage(TextureFormat.Rgba8, External: true), images)],

            // Nothing here evaluates: an upload is filed against the image table alone, and a plan
            // with no op is one `Validate` would refuse for a reason none of these tests is about.
            Ops = []
        };

    /// <summary>A picture of a given format, with whatever texels the format implies.</summary>
    static TextureData Picture(PixelFormat format, int width, int height) => new(format, width, height);
}
