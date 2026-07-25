namespace Vixen.Raven.IR;

/// <summary>The closed set of shapes a Raven IR type can take.</summary>
public enum IrTypeKind {
    Void,
    Bool,
    Int,
    UInt,
    Float,
    Double,
    Vector,
    Matrix,
    Array,
    Struct,
    Texture,
    Sampler
}

/// <summary>How many coordinates a texture is sampled with.</summary>
public enum IrTextureDimension {
    Texture2D,
    Texture3D,
    Cube
}

/// <summary>
/// A type in the target-independent IR. Deliberately much smaller than
/// <see cref="Symbols.TypeSymbol"/>: it holds only what every GPU target can
/// represent, so a backend can switch on <see cref="Kind"/> exhaustively and
/// anything unrepresentable is rejected during lowering rather than in the
/// emitter.
/// </summary>
public abstract class IrType {
    public abstract IrTypeKind Kind { get; }

    /// <summary>Stable, target-neutral spelling used by the IR dump.</summary>
    public abstract string Name { get; }

    public bool IsVoid => Kind == IrTypeKind.Void;

    public bool IsScalar => Kind is IrTypeKind.Bool or IrTypeKind.Int or IrTypeKind.UInt
        or IrTypeKind.Float or IrTypeKind.Double;

    /// <summary>Scalar, vector or matrix — the types arithmetic applies to.</summary>
    public bool IsNumeric => IsScalar && Kind != IrTypeKind.Bool
        || Kind is IrTypeKind.Vector or IrTypeKind.Matrix;

    /// <summary>The scalar each lane holds; the type itself when it is scalar.</summary>
    public IrScalarType ComponentType => this switch {
        IrScalarType scalar => scalar,
        IrVectorType vector => vector.Component,
        IrMatrixType matrix => matrix.Component,
        _ => IrScalarType.Void
    };

    public override string ToString() => Name;
}

/// <summary>A scalar type. Instances are interned, so reference equality is type identity.</summary>
public sealed class IrScalarType : IrType {
    public static readonly IrScalarType Void = new(IrTypeKind.Void, "void");
    public static readonly IrScalarType Bool = new(IrTypeKind.Bool, "bool");
    public static readonly IrScalarType Int = new(IrTypeKind.Int, "i32");
    public static readonly IrScalarType UInt = new(IrTypeKind.UInt, "u32");
    public static readonly IrScalarType Float = new(IrTypeKind.Float, "f32");
    public static readonly IrScalarType Double = new(IrTypeKind.Double, "f64");

    IrScalarType(IrTypeKind kind, string name) {
        Kind = kind;
        Name = name;
    }

    public override IrTypeKind Kind { get; }
    public override string Name { get; }
}

/// <summary>A vector of 2–4 lanes.</summary>
public sealed class IrVectorType : IrType, IEquatable<IrVectorType> {
    public IrVectorType(IrScalarType component, int size) {
        Component = component;
        Size = size;
    }

    public IrScalarType Component { get; }
    public int Size { get; }

    public override IrTypeKind Kind => IrTypeKind.Vector;
    public override string Name => $"vec<{Component.Name},{Size}>";

    public bool Equals(IrVectorType? other) =>
        other is not null && ReferenceEquals(Component, other.Component) && Size == other.Size;

    public override bool Equals(object? obj) => Equals(obj as IrVectorType);
    public override int GetHashCode() => HashCode.Combine(Component, Size);
}

/// <summary>A matrix, stored row-major in the IR; a backend may transpose on emit.</summary>
public sealed class IrMatrixType : IrType, IEquatable<IrMatrixType> {
    public IrMatrixType(IrScalarType component, int rows, int columns) {
        Component = component;
        Rows = rows;
        Columns = columns;
    }

    public IrScalarType Component { get; }
    public int Rows { get; }
    public int Columns { get; }

    public override IrTypeKind Kind => IrTypeKind.Matrix;
    public override string Name => $"mat<{Component.Name},{Rows},{Columns}>";

    /// <summary>The vector type of one row.</summary>
    public IrVectorType RowType => new(Component, Columns);

    public bool Equals(IrMatrixType? other) =>
        other is not null
        && ReferenceEquals(Component, other.Component)
        && Rows == other.Rows
        && Columns == other.Columns;

    public override bool Equals(object? obj) => Equals(obj as IrMatrixType);
    public override int GetHashCode() => HashCode.Combine(Component, Rows, Columns);
}

/// <summary>An array. <see cref="Length"/> is null for an unsized array.</summary>
public sealed class IrArrayType : IrType, IEquatable<IrArrayType> {
    public IrArrayType(IrType element, int? length = null) {
        Element = element;
        Length = length;
    }

    public IrType Element { get; }
    public int? Length { get; }

    public override IrTypeKind Kind => IrTypeKind.Array;
    public override string Name => Length is { } n ? $"array<{Element.Name},{n}>" : $"array<{Element.Name}>";

    public bool Equals(IrArrayType? other) =>
        other is not null && Element.Equals(other.Element) && Length == other.Length;

    public override bool Equals(object? obj) => Equals(obj as IrArrayType);
    public override int GetHashCode() => HashCode.Combine(Element, Length);
}

/// <summary>One field of an <see cref="IrStructType"/>.</summary>
public sealed record IrField(string Name, IrType Type);

/// <summary>
/// A user-declared aggregate. Struct identity is nominal — two structurally
/// identical structs are different types — so equality is reference equality.
/// </summary>
public sealed class IrStructType : IrType {
    IrField[] fields = [];

    public IrStructType(string name) => Name = name;

    public override IrTypeKind Kind => IrTypeKind.Struct;
    public override string Name { get; }

    public IReadOnlyList<IrField> Fields => fields;

    /// <summary>Fields are filled in after construction so structs may refer to each other.</summary>
    internal void SetFields(IrField[] value) => fields = value;

    public int IndexOf(string fieldName) {
        for (var i = 0; i < fields.Length; i++) {
            if (fields[i].Name == fieldName) {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>An opaque sampled texture.</summary>
public sealed class IrTextureType : IrType, IEquatable<IrTextureType> {
    public IrTextureType(IrTextureDimension dimension, IrType sampledType) {
        Dimension = dimension;
        SampledType = sampledType;
    }

    public IrTextureDimension Dimension { get; }

    /// <summary>What a sample returns; <c>vec&lt;f32,4&gt;</c> for now.</summary>
    public IrType SampledType { get; }

    public override IrTypeKind Kind => IrTypeKind.Texture;

    public override string Name => Dimension switch {
        IrTextureDimension.Texture2D => "texture2d",
        IrTextureDimension.Texture3D => "texture3d",
        _ => "texturecube"
    };

    public bool Equals(IrTextureType? other) =>
        other is not null && Dimension == other.Dimension && SampledType.Equals(other.SampledType);

    public override bool Equals(object? obj) => Equals(obj as IrTextureType);
    public override int GetHashCode() => HashCode.Combine(Dimension, SampledType);
}

/// <summary>An opaque sampler state.</summary>
public sealed class IrSamplerType : IrType {
    public static readonly IrSamplerType Instance = new();

    IrSamplerType() { }

    public override IrTypeKind Kind => IrTypeKind.Sampler;
    public override string Name => "sampler";
}
