// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Styling;

namespace Vixen.Ui.Tests;

/// <summary>Elements that exist to be generated against.</summary>
/// <remarks>
///     The generator is tested by using it, which is the same bet <c>Vixen.Core.Reflection.Tests</c>
///     makes. A test that inspects emitted source asserts what the generator wrote; a test that
///     calls the emitted members asserts what it <i>means</i>, and only the second notices when the
///     source compiles and behaves differently from what was intended.
/// </remarks>
public partial class Panel : UiElement {
    /// <summary>How many times a change callback ran on this element.</summary>
    public int RadiusChanges { get; private set; }

    /// <summary>The last change this element was told about, by property.</summary>
    public UiPropertyKey? LastChanged { get; private set; }

    /// <summary>A plain value property with a default that is not <c>default(T)</c>.</summary>
    [UiProperty(Inherits = true, Default = 4f, Changed = nameof(OnRadiusChanged), Coerce = nameof(CoerceRadius))]
    public partial float Radius { get; set; }

    /// <summary>Inherited, the way <c>color</c> is.</summary>
    [UiProperty(Inherits = true, Default = "black")]
    public partial string Tint { get; set; }

    /// <summary>An enum default, which is the case a naive literal writer gets wrong.</summary>
    [UiProperty(Default = ElementState.Hover)]
    public partial ElementState PreferredState { get; set; }

    /// <summary>A reference type with no default at all.</summary>
    [UiProperty]
    public partial string? Label { get; set; }

    /// <inheritdoc />
    protected internal override void OnPropertyChanged(UiPropertyKey key) => LastChanged = key;

    void OnRadiusChanged(float previous, float current) => RadiusChanges++;

    static float CoerceRadius(float value) => Math.Clamp(value, 0f, 100f);
}

/// <summary>A subclass, to check that a derived type sees its base's properties.</summary>
public partial class Card : Panel {
    /// <summary>Its own property, so the registry has to report both.</summary>
    [UiProperty(Default = 2)]
    public partial int Elevation { get; set; }
}

/// <summary>A sibling with a same-named property, which inheritance must not confuse with Panel's.</summary>
public partial class Overlay : UiElement {
    /// <summary>Same name, different declaring type.</summary>
    [UiProperty(Inherits = true, Default = "none")]
    public partial string Tint { get; set; }
}
