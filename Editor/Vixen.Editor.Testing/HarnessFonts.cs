// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Ui;
using Vixen.Ui.Text;

namespace Vixen.Editor.Testing;

/// <summary>The fixed-pitch face an editor under test draws its code in.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Four declarations in three of this tree's stylesheets write
///         <c>font-family: monospace</c></b> — <c>AdvancedTheme.vcss</c> on <c>code-editor</c>,
///         <c>EditorTheme.vcss</c> on the errors panel and the revisions patch,
///         <c>AssetEditorTheme.vcss</c> on the asset editors' code views — and
///         <c>FontRegistry</c> has no generic families, so each of them is an ordinary
///         lookup that falls through to <c>Default</c> when nothing claims the name. #1259 gave the
///         product an answer (<c>SystemFonts.InstallMonospace</c>, called by <c>UiApplication</c>
///         and <c>EditorHost</c>) and left the harness without one, so every editor test measuring
///         one of those four controls went on measuring it in Open Sans — the state
///         <c>AdvancedTheme.vcss</c>'s own comment forbids, and one a suite cannot notice, because
///         a <c>CodeEditor</c> expectation is computed the way the control computes it.
///     </para>
///     <para>
///         ⚠ <b>The synthetic face and not the machine's, which is the one thing this must not
///         borrow.</b> <c>Vixen.Editor.App</c>'s <c>Fonts</c> embeds Open Sans precisely so that
///         three platforms measure, wrap and photograph identically;
///         <c>SystemFonts.InstallMonospace</c> would hand this harness Consolas on Windows, Menlo
///         on macOS and whatever a container has on Linux, and the golden images would follow.
///         <c>TestMono.ttf</c> is Vixen's own — ninety-five hollow boxes, every advance 1229 at
///         2048 per em — so the cell width is the same number everywhere. Its README sits beside
///         it in <c>Core/Vixen.Ui.Controls.Advanced.Tests/Fonts</c>, and it is linked from there
///         rather than copied so that one file cannot become two that disagree.
///     </para>
///     <para>
///         A real fixed-pitch face for the <i>product</i> is a separate decision — a licence and a
///         look — and is #1315. This is the harness, where boxes are enough: what a test asks of
///         monospace is that a column is a cell wide and every cell is the same, which a box
///         answers exactly.
///     </para>
/// </remarks>
static class HarnessFonts {
    /// <summary>The family name those four declarations write.</summary>
    /// <remarks>
    ///     The same string <c>SystemFonts.MonospaceFamily</c> holds, spelled out rather than
    ///     referenced, because this assembly registers its own face and the two would be equal by
    ///     coincidence of CSS rather than by dependency.
    /// </remarks>
    public const string MonospaceFamily = "monospace";

    /// <summary>Registers the harness's fixed-pitch face on a document.</summary>
    /// <param name="document">The document.</param>
    /// <returns>Whether the face was registered.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Never the default.</b> <c>FontRegistry.Register</c> claims <c>Default</c> for
    ///         the first face a bare document sees, so a document that has not been given the
    ///         editor's face yet would draw every label in hollow boxes; the default is put back to
    ///         whatever it was.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the order this is called in is <i>not</i> what keeps that from happening,
    ///         which a sabotage said and a reading of <c>Fonts.Install</c> did not.</b> Moving the
    ///         call ahead of <c>Fonts.Install</c> in <c>EditorSession</c> leaves
    ///         <c>HarnessMonospaceTests</c> green, because that method assigns
    ///         <c>document.Fonts.Default</c> outright rather than relying on being first. So the
    ///         two lines around the <c>Register</c> here are what guards a bare document, and the
    ///         ordering is house style rather than a mechanism — worth knowing before someone
    ///         "simplifies" one of the two away on the strength of the other.
    ///     </para>
    /// </remarks>
    public static bool InstallMonospace(UiDocument document) {
        ArgumentNullException.ThrowIfNull(document);

        if (Embedded("TestMono.ttf") is not { } face) {
            return false;
        }

        var standby = document.Fonts.Default;
        document.Fonts.Register(MonospaceFamily, face);
        document.Fonts.Default = standby;

        return true;
    }

    static FontFace? Embedded(string file) {
        var assembly = typeof(HarnessFonts).Assembly;

        using var stream = assembly.GetManifestResourceStream(assembly.GetName().Name + ".Fonts." + file);

        if (stream is null) {
            return null;
        }

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);

        return FontFace.Load(buffer.ToArray(), name: Path.GetFileNameWithoutExtension(file));
    }
}
