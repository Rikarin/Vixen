// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;

namespace Vixen.Editor.Blockout;

/// <summary>Every word the blockout toolset shows, declared once.</summary>
/// <remarks>
///     <para>
///         <b>One family for sixty-nine commands that three local helpers declared between them.</b>
///         <c>BlockoutMode.Register</c>'s <c>Make</c>, <c>Verb</c> and <c>Declare</c> each took a
///         label and built <c>new StringId("editor.command." + id, label)</c> out of it, so the
///         census saw three constructions and the tree had sixty-nine undeclared words — which is
///         the shape a helper hides best.
///     </para>
///     <para>
///         ⚠ <b>The labels are here rather than at the registration, and the ordering is the
///         registration's.</b> A key the family does not carry throws while the mode registers,
///         which is deterministic and is what <c>BlockoutModeTests</c> exercises on every run.
///     </para>
///     <para>
///         Two of the three groups are computed: one command per <see cref="ShapeKind" /> in
///         <c>BlockoutMode.Kinds</c> and one per <c>BlockoutElement</c>, both built from the same
///         lists the registration walks — so a shape added to <c>Kinds</c> gets a declared label
///         rather than an undeclared one.
///     </para>
/// </remarks>
public static class BlockoutStrings {
    /// <summary>Every command the mode registers, by its command id.</summary>
    /// <remarks>
    ///     The prefix is the convention the helpers were spelling out: a command's label is
    ///     <c>editor.command.</c> followed by the command's own id.
    /// </remarks>
    public static StringFamily Commands { get; } = new(
        "editor.command.",
        [
            new(BlockoutMode.SelectLoopCommand, "Select Loop"),
            new(BlockoutMode.SelectRingCommand, "Select Ring"),
            new(BlockoutMode.GrowCommand, "Grow Selection"),
            new(BlockoutMode.ShrinkCommand, "Shrink Selection"),
            new(BlockoutMode.SelectGroupCommand, "Select Group"),
            new(BlockoutMode.SelectCoplanarCommand, "Select Coplanar"),
            new(BlockoutMode.SelectLinkedCommand, "Select Linked"),
            new(BlockoutMode.SelectAllCommand, "Select All Elements"),
            new(BlockoutMode.SelectNoneCommand, "Deselect Elements"),
            new(BlockoutMode.InvertCommand, "Invert Element Selection"),
            new(BlockoutMode.ExtrudeCommand, "Extrude"),
            new(BlockoutMode.ExtrudeIndividualCommand, "Extrude Individual"),
            new(BlockoutMode.InsetCommand, "Inset"),
            new(BlockoutMode.InsetIndividualCommand, "Inset Individual"),
            new(BlockoutMode.BevelCommand, "Bevel"),
            new(BlockoutMode.LoopCutCommand, "Loop Cut"),
            new(BlockoutMode.SubdivideCommand, "Subdivide"),
            new(BlockoutMode.BridgeCommand, "Bridge"),
            new(BlockoutMode.FillCommand, "Fill Hole"),
            new(BlockoutMode.FlipCommand, "Flip Normals"),
            new(BlockoutMode.WeldCommand, "Weld to Centre"),
            new(BlockoutMode.DissolveCommand, "Dissolve Edges"),
            new(BlockoutMode.DeleteCommand, "Delete Faces"),
            new(BlockoutMode.DetachCommand, "Detach Faces"),
            new(BlockoutMode.KnifeCommand, "Knife"),
            new(BlockoutMode.ProjectWorldCommand, "Project UVs (World)"),
            new(BlockoutMode.ProjectBoxCommand, "Project UVs (Object)"),
            new(BlockoutMode.FitUvCommand, "Fit UVs"),
            new(BlockoutMode.SmoothCommand, "Smooth Faces"),
            new(BlockoutMode.HardenCommand, "Harden Faces"),
            new(BlockoutMode.AutoSmoothCommand, "Auto Smooth"),
            new(BlockoutMode.NewGroupCommand, "New Face Group"),
            new(BlockoutMode.ShapeToolCommand, "Shape Tool"),
            new(BlockoutMode.CreateShapeCommand, "Create Shape"),
            new(BlockoutMode.CubeGridCommand, "Cube Grid Box"),
            new(BlockoutMode.PushOutCommand, "Push Cells Out"),
            new(BlockoutMode.PushInCommand, "Pull Cells In"),
            new(BlockoutMode.DuplicateCommand, "Duplicate"),
            new(BlockoutMode.MirrorCommand, "Mirror"),
            new(BlockoutMode.ArrayCommand, "Array"),
            new(BlockoutMode.RadialCommand, "Radial Array"),
            new(BlockoutMode.UnionCommand, "Union"),
            new(BlockoutMode.SubtractCommand, "Subtract"),
            new(BlockoutMode.IntersectCommand, "Intersect"),
            new(BlockoutMode.ApplyBooleanCommand, "Apply Boolean"),
            new(BlockoutMode.PlaneCutCommand, "Plane Cut"),
            new(BlockoutMode.TrimCommand, "Trim"),
            new(BlockoutMode.RetopologizeCommand, "Retopologize"),
            new(BlockoutMode.RemeshDebugCommand, "Retopology Debug Overlays"),
            new(BlockoutMode.BakeCommand, "Bake To Mesh Asset"),
            new(BlockoutMode.EditableCommand, "Make Mesh Editable"),
            new(BlockoutMode.ExportObjCommand, "Export OBJ…"),
            new(BlockoutMode.ExportGltfCommand, "Export glTF…"),
            .. BlockoutMode.Kinds.Select(
                kind => new KeyValuePair<string, string>(BlockoutMode.KindCommand(kind), "Shape: " + kind)
            ),
            new(BlockoutMode.ElementCommand(BlockoutElement.Object), "Object Mode"),
            new(BlockoutMode.ElementCommand(BlockoutElement.Vertex), "Vertex Mode"),
            new(BlockoutMode.ElementCommand(BlockoutElement.Edge), "Edge Mode"),
            new(BlockoutMode.ElementCommand(BlockoutElement.Face), "Face Mode")
        ]
    );

    /// <summary>What a translator's template for this toolset holds.</summary>
    /// <remarks>
    ///     Spread from the family, which is what puts every member of it in the template. A family
    ///     left out of this list would hide sixty-nine strings rather than one, which is why
    ///     <c>VXS0310</c> counts a <see cref="StringFamily" /> property as a declaration.
    /// </remarks>
    public static IReadOnlyList<StringId> All { get; } = [.. Commands.All];
}
