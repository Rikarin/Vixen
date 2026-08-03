// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Core;

/// <summary>Marks a static method as a line in Create ▸ and the starter file it writes.</summary>
/// <remarks>
///     <para>
///         <b>Doc 36 § D3, and F3's literal array read from the other end.</b> The method takes
///         nothing and returns the file's contents; the attribute names the line and the extension.
///         Together they are a <see cref="NewAssetKind" />, which is what the Create menu is built
///         from.
///     </para>
///     <code language="csharp">
///         [CreateAssetMenu("Dialogue Table", ".dialogue")]
///         public static string NewTable() => "entries: []\n";
///     </code>
///     <para>
///         ⚠ <b>A method returning the contents, unlike Unity's attribute on a
///         <c>ScriptableObject</c> class.</b> Unity can put it on a type because a new asset there is
///         a default instance serialised by a serializer that already knows the type. Ours is a file
///         with an extension that an importer claims, and <see cref="NewAssetKind.Contents" />'s own
///         remark is about the trap: an <i>empty</i> file is right for a kind whose editor opens one
///         as a blank document, and wrong for a kind read by an importer, which deserialises it and
///         puts a warning beside it instead of an asset. Making the author write the return value is
///         what stops that being a default they never saw.
///     </para>
///     <para>
///         An author who wants the empty file writes <c>return "";</c>, and one who wants a serialised
///         default calls the serializer in the body — which is a line of their code rather than a
///         mechanism in ours.
///     </para>
///     <para>
///         ⚠ <b>Read by a scan of the assembly that declared it, and only for a plugin or a project
///         script</b> — see <c>CustomInspectorAttribute</c> for why that is bounded rather than the
///         assembly scanning ADR-002 refuses. In-tree code registers a <see cref="NewAssetKind" />
///         directly, which is what the application's own kinds do.
///     </para>
/// </remarks>
/// <param name="title">What the menu line says.</param>
/// <param name="extension">What the new file is called after the dot, including it.</param>
[AttributeUsage(AttributeTargets.Method)]
public sealed class CreateAssetMenuAttribute(string title, string extension) : Attribute {
    /// <summary>What the menu line says.</summary>
    public string Title { get; } = title;

    /// <summary>What the new file is called after the dot, including it.</summary>
    public string Extension { get; } = extension;

    /// <summary>What it is called before the dot, or empty to use <see cref="Title" />.</summary>
    /// <remarks>
    ///     A number is appended when the name is taken, so this is the stem rather than the whole
    ///     name — "New Dialogue" becomes <c>New Dialogue 2.dialogue</c> beside an existing one.
    /// </remarks>
    public string DefaultName { get; init; } = string.Empty;

    /// <summary>Whether to open it after creating it.</summary>
    /// <remarks>
    ///     ⚠ <b>Needs an editor claiming the extension, and is <see langword="true" /> because most
    ///     kinds have one.</b> A kind whose file nothing opens should say <see langword="false" />:
    ///     creating one and having a pane fail to appear reads as the command not working.
    /// </remarks>
    public bool Opens { get; init; } = true;

    /// <summary>The command id, or empty to derive one from the title.</summary>
    /// <remarks>
    ///     ⚠ <b>Worth setting for anything a key should be bound to.</b> A derived id changes when the
    ///     title does, so renaming the menu line drops the user's binding for it.
    /// </remarks>
    public string Id { get; init; } = string.Empty;

    /// <summary>Where among the lines, low first.</summary>
    public int Order { get; init; }
}
