// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Geometry;

namespace Vixen.Editor.Blockout;

/// <summary>Doc 24's blockout verbs, as a service: what the mode's commands run and a plugin can call.</summary>
/// <remarks>
///     <para>
///         <b>Doc 50 § D2 one level down (#1307).</b> The verbs were closures inside
///         <c>BlockoutMode.Register</c>, over the mode's fields — so a plugin that wanted "subtract the
///         selection, undoably, the way the menu does" could not have it, and a test that wanted it had
///         to stand a shell up and run a command. They are methods here; the mode's commands are names
///         for them, and <c>BlockoutModule</c> publishes this so
///         <c>context.Services.Require&lt;IBlockout&gt;()</c> reaches the same behaviour.
///     </para>
///     <para>
///         ⚠ <b>Each verb acts on what the editor is editing now and does nothing without it.</b> The
///         element verbs need a mesh being edited; the rest need a scene. With neither they answer
///         <see langword="false" />, zero or <see cref="Entity.Null" /> rather than throwing, which is
///         what a command run from a palette with nothing open has always done.
///     </para>
///     <para>
///         ⚠ <b>The dials they read are the mode's</b> — the step, the bevel segments, the UV scale, the
///         copy count, the shape kind, the retopology settings — because those are what the settings
///         panel edits and what a designer set a moment ago. A verb that took its own would be a second
///         answer to "how far does Extrude go".
///     </para>
/// </remarks>
public interface IBlockout {
    // ───────────────────────────────────────────────── doc 24's P2: selection

    /// <summary>Selects the edge loop through the active edge.</summary>
    /// <returns>Whether the selection changed.</returns>
    bool SelectLoop();

    /// <summary>Selects the edge ring through it.</summary>
    /// <returns>Whether the selection changed.</returns>
    bool SelectRing();

    /// <summary>Takes in everything touching the selection.</summary>
    /// <returns>Whether the selection changed.</returns>
    bool Grow();

    /// <summary>Gives back everything on its rim.</summary>
    /// <returns>Whether the selection changed.</returns>
    bool Shrink();

    /// <summary>Selects every face in the active face's group.</summary>
    /// <returns>Whether the selection changed.</returns>
    bool SelectGroup();

    /// <summary>Selects every face coplanar with and joined to it.</summary>
    /// <returns>Whether the selection changed.</returns>
    bool SelectCoplanar();

    /// <summary>Selects every face joined to it.</summary>
    /// <returns>Whether the selection changed.</returns>
    bool SelectLinked();

    /// <summary>Selects every element of the current element mode.</summary>
    /// <returns>Whether the selection changed.</returns>
    bool SelectAll();

    /// <summary>Deselects everything.</summary>
    /// <returns>Whether the selection changed.</returns>
    bool SelectNone();

    /// <summary>Selects what is not selected.</summary>
    /// <returns>Whether the selection changed.</returns>
    bool Invert();

    // ────────────────────────────────────────────────── doc 24's P3: geometry

    /// <summary>Pulls the selected faces out along their normal by the mode's step.</summary>
    /// <param name="individually">One face at a time rather than as one region.</param>
    /// <returns>Whether the mesh changed.</returns>
    bool Extrude(bool individually = false);

    /// <summary>Shrinks the selected faces towards their own centre.</summary>
    /// <param name="individually">One face at a time rather than as one region.</param>
    /// <returns>Whether the mesh changed.</returns>
    bool Inset(bool individually = false);

    /// <summary>Cuts the corner off the selected edges.</summary>
    /// <returns>Whether the mesh changed.</returns>
    bool Bevel();

    /// <summary>Puts a loop across the ring the active edge is part of.</summary>
    /// <returns>Whether the mesh changed.</returns>
    bool LoopCut();

    /// <summary>Splits the selected faces into one face per corner.</summary>
    /// <returns>Whether the mesh changed.</returns>
    bool Subdivide();

    /// <summary>Joins two selected faces with a tube.</summary>
    /// <returns>Whether the mesh changed.</returns>
    bool Bridge();

    /// <summary>Puts a face across a hole.</summary>
    /// <returns>Whether the mesh changed.</returns>
    bool FillHole();

    /// <summary>Turns the selected faces inside out.</summary>
    /// <returns>Whether the mesh changed.</returns>
    bool Flip();

    /// <summary>Merges the selected positions into one.</summary>
    /// <returns>Whether the mesh changed.</returns>
    bool Weld();

    /// <summary>Removes the selected edges and keeps the surface.</summary>
    /// <returns>Whether the mesh changed.</returns>
    bool Dissolve();

    /// <summary>Removes the selected faces, leaving a hole.</summary>
    /// <returns>Whether the mesh changed.</returns>
    bool Delete();

    /// <summary>Takes the selected faces out into an entity of their own.</summary>
    /// <returns>Whether anything was detached.</returns>
    bool Detach();

    // ────────────────────────────────────────────────── doc 24's P5: surfaces

    /// <summary>Projects the selected faces' texture coordinates, at the mode's UV scale.</summary>
    /// <param name="projection">In world space, or in the object's own.</param>
    /// <returns>Whether anything was projected.</returns>
    bool Project(UvProjection projection);

    /// <summary>Stretches each selected face's coordinates to cover one repeat.</summary>
    /// <returns>Whether anything changed.</returns>
    bool FitUvs();

    /// <summary>Puts the selected faces in a smoothing group, or takes them out of one.</summary>
    /// <param name="smooth">Smooth, or harden.</param>
    /// <returns>Whether anything changed.</returns>
    bool Smooth(bool smooth = true);

    /// <summary>Groups the whole mesh's faces by how sharply they meet.</summary>
    /// <returns>Whether anything changed.</returns>
    bool AutoSmooth();

    /// <summary>Puts the selected faces in a face group of their own.</summary>
    /// <returns>Whether anything changed.</returns>
    bool NewGroup();

    // ────────────────────────────────────────────────── doc 24's P4: creation

    /// <summary>Makes one of the mode's current shape at its default size, at the work plane's origin, and selects it.</summary>
    /// <returns>The new entity, or <see cref="Entity.Null" />.</returns>
    Entity CreateShape();

    /// <summary>Puts a box on the lattice, and selects it.</summary>
    /// <param name="at">Which cells, or null for the cell at the work plane's origin.</param>
    /// <returns>The new entity, or <see cref="Entity.Null" />.</returns>
    Entity CubeGrid(GridBox? at = null);

    /// <summary>Pushes the selected boxes' far side along the work plane's second axis.</summary>
    /// <param name="cells">How many cells; negative pulls in.</param>
    /// <returns>Whether any box changed.</returns>
    bool Push(int cells);

    /// <summary>Copies what is selected.</summary>
    /// <returns>How many entities were made.</returns>
    int Duplicate();

    /// <summary>Reflects a copy of what is selected across the work plane.</summary>
    /// <returns>How many entities were made.</returns>
    int Mirror();

    /// <summary>Repeats the first selected entity along a line, the mode's step apart.</summary>
    /// <param name="radial">Round a circle about the work plane's normal instead.</param>
    /// <returns>How many copies were made.</returns>
    int Repeat(bool radial);

    // ────────────────────────────────────────────── doc 24's P6: the booleans

    /// <summary>Everything in either of the selected solids, as a derived result.</summary>
    /// <returns>The result, or <see cref="Entity.Null" />.</returns>
    Entity Union();

    /// <summary>The first selected solid, less the rest.</summary>
    /// <returns>The result, or <see cref="Entity.Null" />.</returns>
    Entity Subtract();

    /// <summary>Only what all the selected solids share.</summary>
    /// <returns>The result, or <see cref="Entity.Null" />.</returns>
    Entity Intersect();

    /// <summary>Collapses the selected derived results into plain meshes and deletes their operands.</summary>
    /// <returns>Whether anything was collapsed.</returns>
    bool ApplyBoolean();

    /// <summary>Cuts the selection with the work plane and keeps the near half.</summary>
    /// <returns>How many solids were cut.</returns>
    int PlaneCut();

    /// <summary>Cuts the first selected solid by the second's surface.</summary>
    /// <returns>Whether anything was cut.</returns>
    bool Trim();

    // ─────────────────────────────────────── doc 24's P7 and doc 41 § D16: handoff

    /// <summary>Replaces the selected solids with all-quad meshes of the same shape, at the mode's settings.</summary>
    /// <returns>How many were replaced.</returns>
    int Retopologize();

    /// <summary>Captures doc 41 § D1's per-stage artefacts for the selected solid, or clears a capture.</summary>
    /// <returns>Whether there is a capture now.</returns>
    bool ToggleRemeshDebug();

    /// <summary>Writes the selection into mesh assets and points the entities at them.</summary>
    /// <returns>How many were baked; zero while the host can bake nothing.</returns>
    int Bake();

    /// <summary>Gives the selected mesh references their geometry back as editable meshes.</summary>
    /// <returns>How many were made editable; zero while the host can read no mesh.</returns>
    int MakeEditable();

    /// <summary>Exports the selection and hands the text to the mode's export callback.</summary>
    /// <param name="extension"><c>.obj</c> or <c>.gltf</c>.</param>
    /// <returns>Whether anything was written.</returns>
    bool Export(string extension);
}
