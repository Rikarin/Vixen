// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven.IR;

namespace Vixen.Raven.Artefacts;

/// <summary>
///     Turns lowered IR into the <see cref="LibraryIr" /> a <c>.rvnlib</c> carries.
/// </summary>
/// <remarks>
///     <para>
///         The encoder and <see cref="LibraryIrDecoder" /> live in one file on purpose: they are the
///         two directions of a single mapping, and a case added to one without the other is a
///         silent round-trip loss. Every statement shape appears in both switches, and the encoder
///         refuses an unknown one rather than dropping it.
///     </para>
///     <para>
///         Names, not objects, are what cross the boundary — and specifically the name the
///         <em>artefact</em> uses. An entity linked in from another library may have been renamed to
///         keep the producing module's namespace unambiguous, so the encoder is given the naming
///         function rather than reading <c>IrFunction.Name</c>: what it writes has to be the name a
///         consumer will look up.
///     </para>
/// </remarks>
internal sealed class LibraryIrEncoder {
    readonly Func<IrFunction, string> functionName;
    readonly Func<IrStructType, string> structName;

    LibraryIrEncoder(Func<IrStructType, string> structName, Func<IrFunction, string> functionName) {
        this.structName = structName;
        this.functionName = functionName;
    }

    /// <summary>
    ///     Encodes the given structs and functions.
    /// </summary>
    /// <param name="structs">The structs to export.</param>
    /// <param name="functions">The functions to export.</param>
    /// <param name="structName">
    ///     The artefact name for a struct — its own, or the name the library it was linked from
    ///     gave it.
    /// </param>
    /// <param name="functionName">The artefact name for a function, on the same terms.</param>
    /// <remarks>
    ///     Takes the entities to export rather than a whole module, because what a library exports
    ///     is the writer's decision: a shader's functions come along (a protocol implementation is
    ///     one), its bindings and entry points do not.
    /// </remarks>
    public static LibraryIr Encode(
        IEnumerable<IrStructType> structs,
        IEnumerable<IrFunction> functions,
        Func<IrStructType, string> structName,
        Func<IrFunction, string> functionName
    ) {
        var encoder = new LibraryIrEncoder(structName, functionName);

        return new() {
            Structs = [.. structs.Select(encoder.EncodeStruct)],
            Functions = [.. functions.Select(encoder.EncodeFunction)]
        };
    }

    LibraryIrStruct EncodeStruct(IrStructType structType) =>
        new() {
            Name = structName(structType),
            Fields = [.. structType.Fields.Select(f => new LibraryIrField(f.Name, EncodeType(f.Type)))]
        };

    /// <summary>Encodes a type. A struct travels as its name, which is its identity.</summary>
    LibraryIrTypeReference EncodeType(IrType type) =>
        type switch {
            IrVectorType vector => new() {
                Kind = IrTypeKind.Vector, Component = vector.Component.Kind, Size = vector.Size
            },
            IrMatrixType matrix => new() {
                Kind = IrTypeKind.Matrix,
                Component = matrix.Component.Kind,
                Rows = matrix.Rows,
                Columns = matrix.Columns
            },
            IrArrayType array => new() {
                Kind = IrTypeKind.Array, Element = EncodeType(array.Element), Length = array.Length
            },
            IrStructType aggregate => new() { Kind = IrTypeKind.Struct, Struct = structName(aggregate) },
            IrTextureType texture => new() {
                Kind = IrTypeKind.Texture,
                Dimension = texture.Dimension,
                Sampled = EncodeType(texture.SampledType)
            },
            IrStorageImageType image => new() {
                Kind = IrTypeKind.StorageImage,
                Dimension = image.Dimension,
                Sampled = EncodeType(image.TexelType),
                Format = image.Format
            },
            IrSamplerType => new() { Kind = IrTypeKind.Sampler },
            // Scalars, void included: the kind is the whole identity.
            _ => new() { Kind = type.Kind }
        };

    LibraryIrFunction EncodeFunction(IrFunction function) {
        // Roots are addressed by position in parameters-then-locals, which is the order the
        // decoder rebuilds them in.
        var roots = new Dictionary<IrVariable, int>();
        foreach (var variable in function.Parameters.Concat(function.Locals)) {
            roots[variable] = roots.Count;
        }

        var values = new SortedDictionary<int, LibraryIrTypeReference>();
        CollectValues(function.Body, values);

        return new() {
            Name = functionName(function),
            ReturnType = EncodeType(function.ReturnType),
            Parameters = [.. function.Parameters.Select(EncodeVariable)],
            Locals = [.. function.Locals.Select(EncodeVariable)],
            Values = [.. values.Select(entry => new LibraryIrValue(entry.Key, entry.Value))],
            ValueCount = function.ValueCount,
            Body = EncodeBlock(function.Body, roots)
        };
    }

    LibraryIrVariable EncodeVariable(IrVariable variable) =>
        new(variable.Name, EncodeType(variable.Type), variable.IsByReference);

    /// <summary>
    ///     Records the type of every value the body mentions, whether it defines it or reads it.
    /// </summary>
    /// <remarks>
    ///     Both, rather than only the definitions: reading operands too means a value the encoder
    ///     somehow failed to see defined still gets a type rather than becoming an id nothing
    ///     describes.
    /// </remarks>
    void CollectValues(IrStatement statement, SortedDictionary<int, LibraryIrTypeReference> values) {
        void Note(IrValue? value) {
            if (value is not null) {
                values[value.Id] = EncodeType(value.Type);
            }
        }

        switch (statement) {
            case IrBlock block:
                foreach (var nested in block.Statements) {
                    CollectValues(nested, values);
                }

                break;

            case IrInstruction instruction:
                Note(instruction.Result);
                foreach (var operand in instruction.Operands) {
                    Note(operand);
                }

                break;

            case IrIfStatement conditional:
                Note(conditional.Condition);
                CollectValues(conditional.Then, values);

                if (conditional.Else is { } otherwise) {
                    CollectValues(otherwise, values);
                }

                break;

            case IrLoopStatement loop:
                CollectValues(loop.Condition, values);
                Note(loop.ConditionValue);
                CollectValues(loop.Body, values);

                if (loop.Continue is { } step) {
                    CollectValues(step, values);
                }

                break;

            case IrReturnStatement { Value: { } returned }:
                Note(returned);
                break;
        }
    }

    LibraryIrBlock EncodeBlock(IrBlock block, Dictionary<IrVariable, int> roots) =>
        new() { Statements = [.. block.Statements.Select(s => EncodeStatement(s, roots))] };

    LibraryIrStatement EncodeStatement(IrStatement statement, Dictionary<IrVariable, int> roots) =>
        statement switch {
            IrBlock block => EncodeBlock(block, roots),
            IrConstantInstruction constant => new LibraryIrConstant(
                constant.Result.Id,
                LibraryValue.From(constant.Value)
            ),
            IrLoadInstruction load => new LibraryIrLoad(load.Result.Id, EncodePlace(load.Place, roots)),
            IrStoreInstruction store => new LibraryIrStore(EncodePlace(store.Place, roots), store.Value.Id),
            IrUnaryInstruction unary => new LibraryIrUnary(unary.Result.Id, unary.Op, unary.Operand.Id),
            IrBinaryInstruction binary => new LibraryIrBinary(
                binary.Result.Id,
                binary.Op,
                binary.Left.Id,
                binary.Right.Id
            ),
            IrConvertInstruction convert => new LibraryIrConvert(
                convert.Result.Id,
                convert.ConversionKind,
                convert.Operand.Id
            ),
            IrIntrinsicInstruction intrinsic => new LibraryIrIntrinsic(
                intrinsic.Result?.Id,
                intrinsic.Intrinsic,
                [.. intrinsic.Arguments.Select(a => a.Id)]
            ),
            IrCallInstruction call => new LibraryIrCall(
                call.Result?.Id,
                functionName(call.Function),
                [.. call.Arguments.Select(a => EncodeArgument(a, roots))]
            ),
            IrConstructInstruction construct => new LibraryIrConstruct(
                construct.Result.Id,
                [.. construct.Arguments.Select(a => a.Id)]
            ),
            IrExtractInstruction extract => new LibraryIrExtract(
                extract.Result.Id,
                extract.Source.Id,
                [.. extract.Chain.Select(EncodeAccess)]
            ),
            IrSelectInstruction select => new LibraryIrSelect(
                select.Result.Id,
                select.Condition.Id,
                select.WhenTrue.Id,
                select.WhenFalse.Id
            ),
            IrIfStatement conditional => new LibraryIrIf(
                conditional.Condition.Id,
                EncodeBlock(conditional.Then, roots),
                conditional.Else is { } otherwise ? EncodeBlock(otherwise, roots) : null
            ),
            IrLoopStatement loop => new LibraryIrLoop(
                EncodeBlock(loop.Condition, roots),
                loop.ConditionValue.Id,
                EncodeBlock(loop.Body, roots),
                loop.Continue is { } step ? EncodeBlock(step, roots) : null,
                loop.TestBeforeBody
            ),
            IrReturnStatement @return => new LibraryIrReturn(@return.Value?.Id),
            IrBreakStatement => new LibraryIrBreak(),
            IrContinueStatement => new LibraryIrContinue(),
            IrDiscardStatement => new LibraryIrDiscard(),
            _ => throw new InvalidOperationException(
                $"Cannot export IR statement '{statement.GetType().Name}': the library encoder has no case for it."
            )
        };

    /// <summary>
    ///     Encodes one call argument: a value id, or the root index of the storage handed over.
    /// </summary>
    static LibraryIrArgument EncodeArgument(IrArgument argument, Dictionary<IrVariable, int> roots) {
        if (argument.Value is { } value) {
            return new(value.Id, null);
        }

        if (!roots.TryGetValue(argument.Reference!, out var root)) {
            // The lowerer only ever passes a function-scoped temp, so a reference that is not a
            // root of this function means the IR was built wrongly rather than exported wrongly.
            throw new InvalidOperationException(
                $"Cannot export a by-reference argument naming '{argument.Reference!.Name}': "
                + "it is not a parameter or local of the function."
            );
        }

        return new(null, root);
    }

    static LibraryIrPlace EncodePlace(IrPlace place, Dictionary<IrVariable, int> roots) {
        if (!roots.TryGetValue(place.Root, out var root)) {
            // A global root means the body reads a shader binding, which the writer refuses before
            // it gets here (RVN5001). Reaching this is a bug in that check, not bad input.
            throw new InvalidOperationException(
                $"Cannot export a place rooted at '{place.Root.Name}': it is not a parameter or local of the function."
            );
        }

        return new(root, [.. place.Chain.Select(EncodeAccess)]);
    }

    static LibraryIrAccess EncodeAccess(IrAccess access) =>
        access switch {
            IrFieldAccess field => new LibraryIrFieldAccess(field.Index),
            IrIndexAccess index => new LibraryIrIndexAccess(index.Index.Id),
            IrSwizzleAccess swizzle => new LibraryIrSwizzleAccess([.. swizzle.Components]),
            _ => throw new InvalidOperationException(
                $"Cannot export access '{access.GetType().Name}': the library encoder has no case for it."
            )
        };
}

/// <summary>
///     Rebuilds lowered IR from one or more <see cref="LibraryIr" />, so referenced libraries'
///     functions can be linked into the module being compiled.
/// </summary>
/// <remarks>
///     <para>
///         The output is ordinary IR — the same classes lowering produces — which is the point: the
///         backends never learn that a function came from a library, so there is one code path and
///         one set of golden tests.
///     </para>
///     <para>
///         <strong>One decoder for every library, not one each.</strong> A module has a single IR
///         namespace, so a name resolves to one entity regardless of which library mentions it, and
///         that is what lets <c>Brdf.rvnlib</c> call a function it does not itself contain out of
///         <c>Math.rvnlib</c> — and reach the <em>same</em> struct object the consumer's own
///         variables are typed by, rather than a private copy that would fail the verifier. The
///         cost is that two libraries exporting the same IR name collapse to the first, which is
///         the rule the duplicate-reference warning already states.
///     </para>
///     <para>
///         Loading is phased because the graph has cycles: every struct shell, then their fields,
///         then every signature, then the bodies.
///     </para>
/// </remarks>
internal sealed class LibraryIrDecoder {
    readonly Dictionary<string, IrFunction> functions = new(StringComparer.Ordinal);
    readonly List<LibraryIr> loaded = [];
    readonly Func<string, string> nameFor;
    readonly Dictionary<string, IrStructType> structs = new(StringComparer.Ordinal);

    /// <summary>Structs keyed by the name their artefact gave them.</summary>
    /// <remarks>
    ///     Keyed by the artefact name, while the object may carry a different one: a linked module
    ///     has one namespace, so a library entity whose name a consumer already uses gives way.
    ///     Cross-references inside an artefact are by the original name, so that is the key.
    /// </remarks>
    public IReadOnlyDictionary<string, IrStructType> Structs => structs;

    /// <summary>Functions keyed by the name their artefact gave them.</summary>
    public IReadOnlyDictionary<string, IrFunction> Functions => functions;

    /// <param name="nameFor">
    ///     Maps an artefact name to one that is free in the module being built. Called once per
    ///     struct and function in artefact order, so renaming is deterministic.
    /// </param>
    public LibraryIrDecoder(Func<string, string> nameFor) {
        this.nameFor = nameFor;
    }

    /// <summary>
    ///     Declares a library's structs and resolves their fields.
    /// </summary>
    /// <remarks>
    ///     Separate from <see cref="DecodeFunctions" /> because the two are needed at different
    ///     points: a consumer's own signature may mention a library struct, so the structs must
    ///     exist before any source signature is built — while the functions wait until the
    ///     compilation's own names are taken, which is what keeps a library from claiming a name
    ///     source was about to use.
    /// </remarks>
    public void DecodeStructs(LibraryIr ir) {
        loaded.Add(ir);

        foreach (var structType in ir.Structs) {
            // First declaration of a name wins, so a struct two libraries both mention has one
            // identity across the linked module.
            if (!structs.ContainsKey(structType.Name)) {
                structs[structType.Name] = new(nameFor(structType.Name));
            }
        }

        foreach (var structType in ir.Structs) {
            var target = structs[structType.Name];

            // Only fill a shell once: a re-declaration of the same name is the loser above.
            if (target.Fields.Count == 0) {
                target.SetFields([.. structType.Fields.Select(f => new IrField(f.Name, DecodeType(f.Type)))]);
            }
        }
    }

    /// <summary>
    ///     Declares every loaded library's functions and decodes their bodies. Signatures first,
    ///     because a call may name a function declared after it.
    /// </summary>
    public void DecodeFunctions() {
        foreach (var ir in loaded) {
            foreach (var function in ir.Functions) {
                if (!functions.ContainsKey(function.Name)) {
                    functions[function.Name] = new(nameFor(function.Name), DecodeType(function.ReturnType));
                }
            }
        }

        HashSet<string> filled = new(StringComparer.Ordinal);

        foreach (var ir in loaded) {
            foreach (var function in ir.Functions) {
                if (filled.Add(function.Name)) {
                    DecodeBody(function);
                }
            }
        }
    }

    IrType DecodeType(LibraryIrTypeReference type) =>
        type.Kind switch {
            IrTypeKind.Vector => new IrVectorType(Scalar(type.Component), type.Size),
            IrTypeKind.Matrix => new IrMatrixType(Scalar(type.Component), type.Rows, type.Columns),
            IrTypeKind.Array => new IrArrayType(
                type.Element is { } element ? DecodeType(element) : IrScalarType.Void,
                type.Length
            ),
            IrTypeKind.Struct => type.Struct is { } name
                ? structs.GetValueOrDefault(name) ?? Missing(name)
                : IrScalarType.Void,
            IrTypeKind.Texture => new IrTextureType(
                type.Dimension,
                type.Sampled is { } sampled ? DecodeType(sampled) : IrScalarType.Void
            ),
            IrTypeKind.StorageImage => new IrStorageImageType(
                type.Dimension,
                type.Sampled is { } texel ? DecodeType(texel) : IrScalarType.Void,
                type.Format ?? string.Empty
            ),
            IrTypeKind.Sampler => IrSamplerType.Instance,
            _ => Scalar(type.Kind)
        };

    /// <summary>
    ///     A struct an artefact names but no loaded library declares: an empty aggregate of that
    ///     name.
    /// </summary>
    /// <remarks>
    ///     Reached when a library was built against another that this compilation did not
    ///     reference. The symbol layer reports that as <c>RVN5004</c> at the type it could not
    ///     resolve; here the placeholder keeps the IR shape intact so the failure is one diagnostic
    ///     rather than a crash.
    /// </remarks>
    IrStructType Missing(string name) {
        var placeholder = new IrStructType(nameFor(name));
        structs[name] = placeholder;
        return placeholder;
    }

    static IrScalarType Scalar(IrTypeKind kind) =>
        kind switch {
            IrTypeKind.Bool => IrScalarType.Bool,
            IrTypeKind.Int => IrScalarType.Int,
            IrTypeKind.UInt => IrScalarType.UInt,
            IrTypeKind.Float => IrScalarType.Float,
            IrTypeKind.Double => IrScalarType.Double,
            _ => IrScalarType.Void
        };

    void DecodeBody(LibraryIrFunction source) {
        var function = functions[source.Name];

        List<IrVariable> roots = [];
        foreach (var parameter in source.Parameters) {
            roots.Add(function.AddParameter(parameter.Name, DecodeType(parameter.Type), parameter.ByReference));
        }

        foreach (var local in source.Locals) {
            roots.Add(function.AddLocal(local.Name, DecodeType(local.Type)));
        }

        // One IrValue object per id, so the verifier's define-once check and the backends' value
        // maps see the identity the lowerer would have produced.
        Dictionary<int, IrValue> values = [];
        foreach (var value in source.Values) {
            values[value.Id] = new(value.Id, DecodeType(value.Type));
        }

        function.ReserveValues(source.ValueCount);

        new BodyDecoder(this, roots, values).Fill(source.Body, function.Body);
    }

    /// <summary>Rebuilds one function's body against its variables and value table.</summary>
    sealed class BodyDecoder(LibraryIrDecoder decoder, List<IrVariable> roots, Dictionary<int, IrValue> values) {
        public void Fill(LibraryIrBlock source, IrBlock target) {
            foreach (var statement in source.Statements) {
                if (Decode(statement) is { } decoded) {
                    target.Add(decoded);
                }
            }
        }

        IrBlock Block(LibraryIrBlock source) {
            var block = new IrBlock();
            Fill(source, block);
            return block;
        }

        IrStatement? Decode(LibraryIrStatement statement) =>
            statement switch {
                LibraryIrBlock block => Block(block),
                LibraryIrConstant constant => new IrConstantInstruction(
                    Value(constant.Result),
                    constant.Value?.ToObject()
                ),
                LibraryIrLoad load => new IrLoadInstruction(Value(load.Result), Place(load.Place)),
                LibraryIrStore store => new IrStoreInstruction(Place(store.Place), Value(store.Value)),
                LibraryIrUnary unary => new IrUnaryInstruction(Value(unary.Result), unary.Op, Value(unary.Operand)),
                LibraryIrBinary binary => new IrBinaryInstruction(
                    Value(binary.Result),
                    binary.Op,
                    Value(binary.Left),
                    Value(binary.Right)
                ),
                LibraryIrConvert convert => new IrConvertInstruction(
                    Value(convert.Result),
                    convert.ConversionKind,
                    Value(convert.Operand)
                ),
                LibraryIrIntrinsic intrinsic => new IrIntrinsicInstruction(
                    intrinsic.Result is { } result ? Value(result) : null,
                    intrinsic.Intrinsic,
                    [.. intrinsic.Arguments.Select(Value)]
                ),
                LibraryIrCall call => Call(call),
                LibraryIrConstruct construct => new IrConstructInstruction(
                    Value(construct.Result),
                    [.. construct.Arguments.Select(Value)]
                ),
                LibraryIrExtract extract => new IrExtractInstruction(
                    Value(extract.Result),
                    Value(extract.Source),
                    [.. extract.Chain.Select(Access)]
                ),
                LibraryIrSelect select => new IrSelectInstruction(
                    Value(select.Result),
                    Value(select.Condition),
                    Value(select.WhenTrue),
                    Value(select.WhenFalse)
                ),
                LibraryIrIf conditional => new IrIfStatement(
                    Value(conditional.Condition),
                    Block(conditional.Then),
                    conditional.Else is { } otherwise ? Block(otherwise) : null
                ),
                LibraryIrLoop loop => new IrLoopStatement(
                    Block(loop.Condition),
                    Value(loop.ConditionValue),
                    Block(loop.Body),
                    loop.Continue is { } step ? Block(step) : null,
                    loop.TestBeforeBody
                ),
                LibraryIrReturn @return => new IrReturnStatement(
                    @return.Value is { } returned ? Value(returned) : null
                ),
                LibraryIrBreak => new IrBreakStatement(),
                LibraryIrContinue => new IrContinueStatement(),
                LibraryIrDiscard => new IrDiscardStatement(),
                _ => null
            };

        /// <summary>A call whose callee no loaded library declares is dropped rather than crashing.</summary>
        /// <remarks>
        ///     Reached when a library was built against another this compilation did not reference —
        ///     which the symbol layer reports as <c>RVN5004</c> — or from a hand-edited artefact.
        ///     Dropping the statement leaves its result undefined, which <c>IrVerifier</c> reports
        ///     with a name attached, rather than throwing out of the decoder.
        /// </remarks>
        IrStatement? Call(LibraryIrCall call) =>
            decoder.functions.GetValueOrDefault(call.Function) is { } callee
                ? new IrCallInstruction(
                    call.Result is { } result ? Value(result) : null,
                    callee,
                    [.. call.Arguments.Select(Argument)]
                )
                : null;

        IrArgument Argument(LibraryIrArgument argument) =>
            argument.Reference is { } root
                ? IrArgument.ByReference(roots[root])
                : IrArgument.Of(Value(argument.Value!.Value));

        IrPlace Place(LibraryIrPlace place) => new(roots[place.Root], [.. place.Chain.Select(Access)]);

        IrAccess Access(LibraryIrAccess access) =>
            access switch {
                LibraryIrFieldAccess field => new IrFieldAccess(field.Index),
                LibraryIrIndexAccess index => new IrIndexAccess(Value(index.Index)),
                LibraryIrSwizzleAccess swizzle => new IrSwizzleAccess([.. swizzle.Components]),
                _ => new IrFieldAccess(0)
            };

        /// <summary>
        ///     The one object for a value id. A value the table omits is created as void, so the
        ///     verifier reports a type mismatch rather than the decoder throwing.
        /// </summary>
        IrValue Value(int id) {
            if (!values.TryGetValue(id, out var value)) {
                values[id] = value = new(id, IrScalarType.Void);
            }

            return value;
        }
    }
}
