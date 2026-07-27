// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Text.Outlines;

/// <summary>The Type 2 charstring machine: a small stack language that draws one glyph.</summary>
internal sealed class Charstrings(
    OutlineBuilder builder,
    float[] matrix,
    CffRange[] globalSubrs,
    CffRange[] localSubrs
) {
    /// <summary>The interpreter's operand stack. The specification caps it at 48.</summary>
    readonly float[] stack = new float[48];

    readonly int globalBias = CffOutlines.Bias(globalSubrs.Length);
    readonly int localBias = CffOutlines.Bias(localSubrs.Length);

    int count;
    float x;
    float y;
    int stems;
    bool widthTaken;
    bool open;

    public void Finish() {
        if (open) {
            builder.Close();
            open = false;
        }
    }

    public void Run(byte[] data, int start, int end, int depth) {
        if (depth > 10) {
            return;
        }

        var reader = new SfntReader(data) { Position = start };

        while (reader.Position < end && reader.Has(1)) {
            var b0 = reader.U8();

            if (b0 >= 32 || b0 == 28) {
                Push(ref reader, b0);
                continue;
            }

            switch (b0) {
                case 1 or 3 or 18 or 23:                      // hstem vstem hstemhm vstemhm
                    CountStems();
                    break;

                case 19 or 20: {                              // hintmask cntrmask
                    CountStems();

                    // The mask is one bit per stem, rounded up to whole bytes — which is why
                    // miscounting the stems desynchronises everything after it.
                    reader.Position += (stems + 7) / 8;
                    break;
                }

                case 21: {                                    // rmoveto
                    var i = Width(2);
                    x += At(i);
                    y += At(i + 1);
                    MoveTo();
                    break;
                }

                case 22: {                                    // hmoveto
                    x += At(Width(1));
                    MoveTo();
                    break;
                }

                case 4: {                                     // vmoveto
                    y += At(Width(1));
                    MoveTo();
                    break;
                }

                case 5:                                       // rlineto
                    for (var i = 0; i + 1 < count; i += 2) {
                        x += stack[i];
                        y += stack[i + 1];
                        LineTo();
                    }

                    count = 0;
                    break;

                case 6 or 7: {                                // hlineto vlineto
                    var horizontal = b0 == 6;
                    for (var i = 0; i < count; i++) {
                        if (horizontal) {
                            x += stack[i];
                        } else {
                            y += stack[i];
                        }

                        LineTo();
                        horizontal = !horizontal;
                    }

                    count = 0;
                    break;
                }

                case 8:                                       // rrcurveto
                    for (var i = 0; i + 5 < count; i += 6) {
                        Curve(stack[i], stack[i + 1], stack[i + 2], stack[i + 3], stack[i + 4], stack[i + 5]);
                    }

                    count = 0;
                    break;

                case 24: {                                    // rcurveline
                    var i = 0;
                    for (; i + 5 < count - 2; i += 6) {
                        Curve(stack[i], stack[i + 1], stack[i + 2], stack[i + 3], stack[i + 4], stack[i + 5]);
                    }

                    if (i + 1 < count) {
                        x += stack[i];
                        y += stack[i + 1];
                        LineTo();
                    }

                    count = 0;
                    break;
                }

                case 25: {                                    // rlinecurve
                    var i = 0;
                    for (; i + 1 < count - 6; i += 2) {
                        x += stack[i];
                        y += stack[i + 1];
                        LineTo();
                    }

                    if (i + 5 < count) {
                        Curve(stack[i], stack[i + 1], stack[i + 2], stack[i + 3], stack[i + 4], stack[i + 5]);
                    }

                    count = 0;
                    break;
                }

                case 26: {                                    // vvcurveto
                    var i = 0;
                    float dx = 0;
                    if ((count & 1) != 0) {
                        dx = stack[0];
                        i = 1;
                    }

                    for (; i + 3 < count; i += 4) {
                        Curve(dx, stack[i], stack[i + 1], stack[i + 2], 0, stack[i + 3]);
                        dx = 0;
                    }

                    count = 0;
                    break;
                }

                case 27: {                                    // hhcurveto
                    var i = 0;
                    float dy = 0;
                    if ((count & 1) != 0) {
                        dy = stack[0];
                        i = 1;
                    }

                    for (; i + 3 < count; i += 4) {
                        Curve(stack[i], dy, stack[i + 1], stack[i + 2], stack[i + 3], 0);
                        dy = 0;
                    }

                    count = 0;
                    break;
                }

                case 30 or 31: {                              // vhcurveto hvcurveto
                    var horizontal = b0 == 31;
                    for (var i = 0; i + 3 < count; i += 4) {
                        // The last group may carry a fifth argument, which is the other axis of the
                        // final point rather than another curve.
                        var extra = i + 8 > count && count - i == 5 ? stack[i + 4] : 0;

                        if (horizontal) {
                            Curve(stack[i], 0, stack[i + 1], stack[i + 2], extra, stack[i + 3]);
                        } else {
                            Curve(0, stack[i], stack[i + 1], stack[i + 2], stack[i + 3], extra);
                        }

                        horizontal = !horizontal;
                    }

                    count = 0;
                    break;
                }

                case 10 or 29: {                              // callsubr callgsubr
                    var subrs = b0 == 10 ? localSubrs : globalSubrs;
                    var bias = b0 == 10 ? localBias : globalBias;

                    if (count == 0) {
                        break;
                    }

                    var index = (int)stack[--count] + bias;
                    if (index >= 0 && index < subrs.Length) {
                        Run(data, subrs[index].Start, subrs[index].End, depth + 1);
                    }

                    break;
                }

                case 11:                                      // return
                    return;

                case 14:                                      // endchar
                    _ = Width(count >= 4 ? 4 : 0);
                    Finish();
                    return;

                case 12:
                    if (reader.Has(1)) {
                        Flex(reader.U8());
                    }

                    break;

                default:
                    count = 0;
                    break;
            }
        }
    }

    void Push(ref SfntReader reader, byte b0) {
        float value;

        if (b0 == 28) {
            value = reader.Has(2) ? reader.S16() : 0;
        } else if (b0 <= 246) {
            value = b0 - 139;
        } else if (b0 <= 250) {
            value = reader.Has(1) ? ((b0 - 247) * 256) + reader.U8() + 108 : 0;
        } else if (b0 <= 254) {
            value = reader.Has(1) ? (-(b0 - 251) * 256) - reader.U8() - 108 : 0;
        } else {
            value = reader.Has(4) ? (int)reader.U32() / 65536f : 0;   // 16.16 fixed
        }

        if (count < stack.Length) {
            stack[count++] = value;
        }
    }

    float At(int index) => index >= 0 && index < count ? stack[index] : 0;

    void CountStems() {
        stems += (count - Width(-1)) / 2;
        count = 0;
    }

    /// <summary>How many leading operands are a width rather than a coordinate: one, or none.</summary>
    /// <param name="expected">
    ///     How many operands the operator takes, or −1 for the stem operators, which take pairs.
    /// </param>
    /// <remarks>
    ///     ⚠ <b>Only the first stack-clearing operator may carry a width</b>, and a stem operator
    ///     signals it by an <i>odd</i> count because its own arguments come in pairs. The first
    ///     version of this tested the parity the other way round: that miscounts the stems, so
    ///     <c>hintmask</c> skips the wrong number of bytes, so everything after it is read as
    ///     garbage. It produced a wrong shape rather than an error, and only in fonts hinted heavily
    ///     enough to have a <c>hintmask</c> at all — which is why it cost the STIX maths faces
    ///     several hundred units a glyph and nothing else a thing.
    /// </remarks>
    int Width(int expected) {
        if (widthTaken) {
            return 0;
        }

        widthTaken = true;
        return expected < 0 ? count % 2 : count > expected ? 1 : 0;
    }

    (float X, float Y) Transform(float px, float py) =>
        ((matrix[0] * px) + (matrix[2] * py) + matrix[4], (matrix[1] * px) + (matrix[3] * py) + matrix[5]);

    void MoveTo() {
        if (open) {
            builder.Close();
        }

        var point = Transform(x, y);
        builder.Move(point.X, point.Y);
        open = true;
        count = 0;
    }

    void LineTo() {
        var point = Transform(x, y);
        builder.Line(point.X, point.Y);
    }

    /// <summary>One cubic, from the three relative deltas a charstring stores it as.</summary>
    void Curve(float dxa, float dya, float dxb, float dyb, float dxc, float dyc) {
        var ax = x + dxa;
        var ay = y + dya;
        var bx = ax + dxb;
        var by = ay + dyb;
        x = bx + dxc;
        y = by + dyc;

        var a = Transform(ax, ay);
        var b = Transform(bx, by);
        var end = Transform(x, y);
        builder.Cubic(a.X, a.Y, b.X, b.Y, end.X, end.Y);
    }

    /// <summary>The four flex operators: two curves written as one, for a nearly-flat join.</summary>
    void Flex(byte op) {
        switch (op) {
            case 35:                                          // flex
                Curve(At(0), At(1), At(2), At(3), At(4), At(5));
                Curve(At(6), At(7), At(8), At(9), At(10), At(11));
                break;

            case 34: {                                        // hflex
                var start = y;
                Curve(At(0), 0, At(1), At(2), At(3), 0);
                Curve(At(4), 0, At(5), start - (y + At(2)), At(6), 0);
                y = start;
                break;
            }

            case 36: {                                        // hflex1
                var start = y;
                Curve(At(0), At(1), At(2), At(3), At(4), 0);
                Curve(At(5), 0, At(6), At(7), At(8), start - y - At(1) - At(3) - At(7));
                break;
            }

            case 37: {                                        // flex1
                var startX = x;
                var startY = y;
                float dx = 0, dy = 0;
                for (var i = 0; i < 10; i += 2) {
                    dx += At(i);
                    dy += At(i + 1);
                }

                Curve(At(0), At(1), At(2), At(3), At(4), At(5));
                Curve(At(6), At(7), At(8), At(9), startX + dx + At(10) - x, startY + dy - y);
                break;
            }

            default:
                break;
        }

        count = 0;
    }
}
