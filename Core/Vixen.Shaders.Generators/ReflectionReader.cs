// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Shaders.Generators;

/// <summary>Reads a <c>.reflect.json</c> document into the model this generator emits from.</summary>
/// <remarks>
///     Every field is read with a fallback, so a document missing something produces bindings with
///     less in them rather than an exception. The one thing worth failing on is a document that is
///     not this schema at all, which shows up as a shader with no parameters and no permutations —
///     reported by <see cref="ShaderBindingsGenerator" /> as malformed rather than silently emitting
///     an empty class.
/// </remarks>
static class ReflectionReader {
    public static ShaderReflection Read(string json) {
        var root = JsonValue.Parse(json);

        if (root["Sets"].IsNull && root["Parameters"].IsNull && root["Permutations"].IsNull) {
            throw new InvalidOperationException(
                "no 'Sets', 'Parameters' or 'Permutations' member — is this a Raven reflection document?"
            );
        }

        var reflection = new ShaderReflection();

        foreach (var set in root["Sets"].Items) {
            var descriptorSet = new DescriptorSet { Set = set["Set"].AsInt() };

            foreach (var binding in set["Bindings"].Items) {
                var described = new Binding {
                    Index = binding["Binding"].AsInt(),
                    Name = binding["Name"].AsString(string.Empty),
                    Type = binding["Type"].AsString(string.Empty),
                    Size = binding["Size"].AsInt(),
                    IsWritable = binding["IsWritable"].AsBool(false)
                };

                foreach (var member in binding["Members"].Items) {
                    described.Members.Add(
                        new Member {
                            Name = member["Name"].AsString(string.Empty),
                            Type = ReadType(member["Type"]),
                            Offset = member["Offset"].AsInt(),
                            Size = member["Size"].AsInt(),
                            ArrayStride = member["ArrayStride"].AsInt(),
                            MatrixStride = member["MatrixStride"].AsInt()
                        }
                    );
                }

                descriptorSet.Bindings.Add(described);
            }

            reflection.Sets.Add(descriptorSet);
        }

        foreach (var parameter in root["Parameters"].Items) {
            reflection.Parameters.Add(
                new Parameter {
                    Name = parameter["Name"].AsString(string.Empty),
                    Type = ReadType(parameter["Type"]),
                    Set = parameter["Set"].AsInt(),
                    Binding = parameter["Binding"].AsInt(),
                    Offset = parameter["Offset"].AsInt(),
                    Size = parameter["Size"].AsInt(),
                    ArrayStride = parameter["ArrayStride"].AsInt(),
                    MatrixStride = parameter["MatrixStride"].AsInt(),
                    DefaultValue = parameter["DefaultValue"].AsString(string.Empty)
                }
            );
        }

        foreach (var permutation in root["Permutations"].Items) {
            reflection.Permutations.Add(
                new Permutation {
                    Name = permutation["Name"].AsString(string.Empty),
                    Type = ReadType(permutation["Type"]),
                    DefaultValue = permutation["DefaultValue"].AsString(string.Empty)
                }
            );
        }

        foreach (var stage in root["Stages"].Items) {
            reflection.Stages.Add(stage.AsString(string.Empty));
        }

        foreach (var key in root["UsedPermutationKeys"].Items) {
            reflection.UsedPermutationKeys.Add(key.AsString(string.Empty));
        }

        return reflection;
    }

    static DataType ReadType(JsonValue value) =>
        new() {
            Scalar = value["Scalar"].AsString("Float"),
            Rows = value["Rows"].AsInt(1),
            Columns = value["Columns"].AsInt(1),
            ArrayLength = value["ArrayLength"].IsNull ? null : value["ArrayLength"].AsInt(),
            StructName = value["StructName"].AsString(),
            IsMatrix = value["IsMatrix"].AsBool(false),
            IsArray = value["IsArray"].AsBool(false),
            IsStruct = value["IsStruct"].AsBool(false)
        };
}
