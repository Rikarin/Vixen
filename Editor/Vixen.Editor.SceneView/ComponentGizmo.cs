// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.Core;
using Vixen.Engine.Transforms;
using Vixen.Rendering;

namespace Vixen.Editor.SceneView;

/// <summary>Where the entity carrying a component is, in world space.</summary>
/// <param name="Position">Its origin.</param>
/// <param name="Right">Its local X, normalised.</param>
/// <param name="Up">Its local Y, normalised.</param>
/// <param name="Forward">Its local Z, normalised.</param>
/// <param name="Scale">Its lossy world scale.</param>
/// <remarks>
///     ⚠ <b>Five vectors rather than the <c>Transform</c> they came from, and the reason is that
///     <c>Transform</c> is a <c>ref struct</c>.</b> One cannot be a field, boxed, or captured, which
///     rules out handing it to a drawer that wants to keep it for a moment — and it needs a
///     <c>World</c> and an <c>Entity</c> to exist at all, so a test for a gizmo would have to build a
///     world to call one. This is what a gizmo actually reads, in a shape a test can write a literal
///     for.
/// </remarks>
public readonly record struct GizmoPlacement(
    Vector3 Position,
    Vector3 Right,
    Vector3 Up,
    Vector3 Forward,
    Vector3 Scale
) {
    /// <summary>Reads an entity's placement.</summary>
    /// <param name="transform">The entity's transform.</param>
    /// <returns>Where it is.</returns>
    public static GizmoPlacement Of(in Transform transform) =>
        new(transform.Position, transform.Right, transform.Up, transform.Forward, transform.LossyScale);
}

/// <summary>Where a gizmo puts its lines.</summary>
/// <remarks>
///     <para>
///         <b>A writer over the viewport's depth-tested line list.</b> Everything here ends up in the
///         same buffer and the same draw call as the grid, the markers and the light shapes — a gizmo
///         is not a rendering path, it is more vertices in the one that exists.
///     </para>
///     <para>
///         ⚠ <b>Segments and not shapes, because the renderer has no shapes.</b> A ring is a fan of
///         short lines and a sphere is three rings; there is no circle primitive underneath and
///         pretending otherwise would mean a gizmo author discovering the tessellation themselves. The
///         helpers here are the ones <c>SceneLines</c> already wrote for the light shapes, made
///         public — a contributed gizmo and a built-in one draw with the same vocabulary or the
///         built-ins are a privileged tier.
///     </para>
/// </remarks>
public sealed class GizmoDraw {
    /// <summary>How many segments a full turn of a ring is drawn with.</summary>
    /// <remarks>
    ///     Thirty-two, which is <c>SceneLines</c>'s own number for a light's cone rings: smooth at the
    ///     distance somebody edits from, and cheap enough that a scene full of them is thousands of
    ///     lines rather than tens of thousands.
    /// </remarks>
    public const int Segments = 32;

    readonly List<LineVertex> lines;

    /// <summary>How many vertices have been written.</summary>
    /// <remarks>Two per segment. Read by tests, which is the only thing that should care.</remarks>
    public int Count => lines.Count;

    /// <summary>Writes into a list of line vertices.</summary>
    /// <param name="lines">The list, which is the viewport's own.</param>
    public GizmoDraw(List<LineVertex> lines) {
        ArgumentNullException.ThrowIfNull(lines);
        this.lines = lines;
    }

    /// <summary>One segment.</summary>
    /// <param name="from">One end.</param>
    /// <param name="to">The other.</param>
    /// <param name="colour">Its colour, at both ends.</param>
    public void Line(Vector3 from, Vector3 to, Color4 colour) {
        lines.Add(new(from, colour));
        lines.Add(new(to, colour));
    }

    /// <summary>A circle, in the plane two directions span.</summary>
    /// <param name="centre">Its centre.</param>
    /// <param name="first">One axis of its plane, normalised.</param>
    /// <param name="second">The other, normalised and perpendicular to the first.</param>
    /// <param name="radius">Its radius. Nothing is drawn for a radius of zero or less.</param>
    /// <param name="colour">Its colour.</param>
    public void Ring(Vector3 centre, Vector3 first, Vector3 second, float radius, Color4 colour) {
        if (radius <= 0f) {
            return;
        }

        var previous = centre + (first * radius);

        for (var i = 1; i <= Segments; i++) {
            var turn = i / (float) Segments;
            var next = centre + (((first * MathF.Cos(turn * MathF.Tau)) + (second * MathF.Sin(turn * MathF.Tau))) * radius);

            Line(previous, next, colour);
            previous = next;
        }
    }

    /// <summary>Three rings in the three axis planes, which reads as a sphere.</summary>
    /// <param name="centre">Its centre.</param>
    /// <param name="radius">Its radius.</param>
    /// <param name="colour">Its colour.</param>
    public void Sphere(Vector3 centre, float radius, Color4 colour) {
        Ring(centre, Vector3.UnitX, Vector3.UnitY, radius, colour);
        Ring(centre, Vector3.UnitY, Vector3.UnitZ, radius, colour);
        Ring(centre, Vector3.UnitZ, Vector3.UnitX, radius, colour);
    }

    /// <summary>A wireframe box in an entity's own axes.</summary>
    /// <param name="centre">Its centre.</param>
    /// <param name="right">Its local X, normalised.</param>
    /// <param name="up">Its local Y, normalised.</param>
    /// <param name="forward">Its local Z, normalised.</param>
    /// <param name="extents">Half its size along each of those, so a unit cube is <c>0.5</c> each.</param>
    /// <param name="colour">Its colour.</param>
    public void Box(Vector3 centre, Vector3 right, Vector3 up, Vector3 forward, Vector3 extents, Color4 colour) {
        var x = right * extents.X;
        var y = up * extents.Y;
        var z = forward * extents.Z;

        Span<Vector3> corners = [
            centre - x - y - z, centre + x - y - z, centre + x + y - z, centre - x + y - z,
            centre - x - y + z, centre + x - y + z, centre + x + y + z, centre - x + y + z
        ];

        for (var i = 0; i < 4; i++) {
            var next = (i + 1) % 4;

            Line(corners[i], corners[next], colour);
            Line(corners[i + 4], corners[next + 4], colour);
            Line(corners[i], corners[i + 4], colour);
        }
    }

    /// <summary>A three-axis cross, which is what an entity with no shape is drawn as.</summary>
    /// <param name="origin">Its centre.</param>
    /// <param name="arm">How far each of the six arms reaches.</param>
    /// <param name="colour">Its colour.</param>
    public void Cross(Vector3 origin, float arm, Color4 colour) {
        Line(origin - (Vector3.UnitX * arm), origin + (Vector3.UnitX * arm), colour);
        Line(origin - (Vector3.UnitY * arm), origin + (Vector3.UnitY * arm), colour);
        Line(origin - (Vector3.UnitZ * arm), origin + (Vector3.UnitZ * arm), colour);
    }
}

/// <summary>Draws the lines for one component on one entity.</summary>
/// <param name="draw">Where the lines go.</param>
/// <param name="component">The component's value, boxed.</param>
/// <param name="placement">Where the entity carrying it is.</param>
/// <param name="selected">Whether that entity is in the selection.</param>
public delegate void GizmoDrawer(GizmoDraw draw, object component, GizmoPlacement placement, bool selected);

/// <summary>Lines drawn in the viewport for every entity carrying a component type.</summary>
/// <param name="Target">The component or behaviour type. Every entity with one gets the lines.</param>
/// <param name="Draw">What draws them.</param>
/// <param name="SelectedOnly">Whether to draw only for entities in the selection.</param>
/// <param name="Order">Which of two gizmos draws first, low first.</param>
/// <remarks>
///     <para>
///         <b>Doc 36 § D4's <c>AddGizmo</c>, whose "Replaces the hardcoding at" column said
///         <i>nothing</i>.</b> That was wrong and it is the useful part of building this:
///         <c>SceneLines.LightShapes</c> is a walk over the scene testing for one component type and
///         switching on its kind, which is exactly this mechanism written once, in the application's
///         own assembly, for the one component the application happens to know about. A plugin's
///         component had no way to be drawn at all.
///     </para>
///     <para>
///         ⚠ <b><see cref="SelectedOnly" /> is Unity's <c>GizmoType.Selected</c> and it earns its
///         place.</b> A trigger volume drawn for every entity in a level is a scene nobody can see
///         through; the same volume drawn for the one you clicked is the reason you clicked it. A
///         gizmo that is cheap and always relevant — a light's reach — wants the default.
///     </para>
///     <para>
///         ⚠ <b>The component arrives boxed, which is the tooling path's price and not a mistake.</b>
///         One allocation per entity per frame per gizmo is what a runtime <see cref="Type" /> costs;
///         <c>Vixen.Core.Reflection</c>'s own remarks name the same trade for the inspector. A gizmo
///         that needs to be free is a built-in that can be generic.
///     </para>
/// </remarks>
public sealed record ComponentGizmo(Type Target, GizmoDrawer Draw, bool SelectedOnly = false, int Order = 0);

/// <summary>Marks a static method as the gizmo for a component type.</summary>
/// <remarks>
///     <para>
///         <b>Doc 36 § D3, and Unity's <c>[DrawGizmo]</c>.</b> The method is what
///         <see cref="ComponentGizmo.Draw" /> takes.
///     </para>
///     <code language="csharp">
///         [DrawGizmo(typeof(SpawnPoint), SelectedOnly = true)]
///         public static void Draw(GizmoDraw draw, object component, GizmoPlacement placement, bool selected) {
///             draw.Sphere(placement.Position, ((SpawnPoint) component).Radius, new(0.2f, 0.9f, 0.4f, 1f));
///         }
///     </code>
///     <para>
///         ⚠ <b>The component is <see cref="object" /> rather than the target type, and casting it is
///         the author's line.</b> A typed delegate would need the attribute to be generic, which an
///         attribute cannot be over a type argument the scanner discovers at run time. The cast is
///         checked — the drawer is only ever called for an entity carrying <see cref="Target" />.
///     </para>
///     <para>
///         ⚠ <b>Read by a scan of the assembly that declared it, and only for a plugin or a project
///         script</b> — see <c>CustomInspectorAttribute</c> for why that is bounded rather than the
///         assembly scanning ADR-002 refuses. In-tree code registers a <see cref="ComponentGizmo" />
///         directly.
///     </para>
/// </remarks>
/// <param name="target">The component or behaviour type this draws for.</param>
[AttributeUsage(AttributeTargets.Method)]
public sealed class DrawGizmoAttribute(Type target) : Attribute {
    /// <summary>The type it draws for.</summary>
    public Type Target { get; } = target;

    /// <summary>Whether to draw only for entities in the selection.</summary>
    public bool SelectedOnly { get; init; }

    /// <summary>Which of two gizmos draws first, low first.</summary>
    public int Order { get; init; }
}

/// <summary>The pass that runs every contributed gizmo over a scene.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Here rather than in <see cref="SceneLines" />, because the bridges are not here.</b>
///         Resolving a <see cref="Type" /> to "is it on this entity, and what is its value" is
///         <c>IComponentBridge</c>'s job, and the list of bridges is assembled by the application —
///         it needs the behaviour store, which needs the scene. <see cref="SceneLines" /> holds one of
///         these and calls it; whoever has both halves builds it.
///     </para>
///     <para>
///         ⚠ <b>The bridge map is rebuilt when the bridge list changes length.</b> Behaviour bridges
///         appear when a project's game assembly loads, which is after the first frame is drawn — a
///         map built once in a constructor would miss every behaviour gizmo in the session that
///         loaded them.
///     </para>
/// </remarks>
public sealed class ComponentGizmos {
    readonly Dictionary<Type, IComponentBridge> map = [];
    readonly IReadOnlyList<IComponentBridge> bridges;
    readonly IEditorRegistry extensions;
    int mapped = -1;

    /// <summary>Runs the gizmos a registry holds, over the bridges an application assembled.</summary>
    /// <param name="bridges">What answers "is this component on this entity".</param>
    /// <param name="extensions">Where the <see cref="ComponentGizmo" /> contributions are.</param>
    public ComponentGizmos(IReadOnlyList<IComponentBridge> bridges, IEditorRegistry extensions) {
        ArgumentNullException.ThrowIfNull(bridges);
        ArgumentNullException.ThrowIfNull(extensions);

        this.bridges = bridges;
        this.extensions = extensions;
    }

    /// <summary>Draws every registered gizmo for every entity that has its component.</summary>
    /// <param name="document">The scene.</param>
    /// <param name="draw">Where the lines go.</param>
    public void Build(SceneDocument document, GizmoDraw draw) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(draw);

        var gizmos = extensions.All<ComponentGizmo>();

        if (gizmos.Count == 0) {
            return;
        }

        Remap();

        // ⚠ Gizmos outside the entity loop and entities inside it, not the other way round. A scene
        // has thousands of entities and a session has a handful of gizmos, so the bridge lookup and
        // the order sort happen once per gizmo rather than once per entity.
        foreach (var gizmo in gizmos.OrderBy(entry => entry.Order)) {
            if (!map.TryGetValue(gizmo.Target, out var bridge)) {
                continue;
            }

            foreach (var entity in document.Entities) {
                var selected = document.Selection.Contains(entity);

                if ((gizmo.SelectedOnly && !selected)
                    || !document.World.Has<WorldTransform>(entity)
                    || !bridge.Has(document.World, entity)) {
                    continue;
                }

                var placement = GizmoPlacement.Of(new Transform(document.World, entity));

                gizmo.Draw(draw, bridge.Read(document.World, entity), placement, selected);
            }
        }
    }

    void Remap() {
        if (mapped == bridges.Count) {
            return;
        }

        map.Clear();

        foreach (var bridge in bridges) {
            map[bridge.ComponentType] = bridge;
        }

        mapped = bridges.Count;
    }
}
