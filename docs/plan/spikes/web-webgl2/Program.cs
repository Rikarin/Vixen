using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.JavaScript;
using Silk.NET.Core.Contexts;
using Silk.NET.OpenGLES;

public static partial class Program
{
    [DllImport("*", EntryPoint = "emscripten_GetProcAddress")]
    private static extern nint GetProcAddress([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport("*", EntryPoint = "emscripten_webgl_init_context_attributes")]
    private static extern void InitAttrs(ref Attrs a);
    [DllImport("*", EntryPoint = "emscripten_webgl_create_context")]
    private static extern nint CreateContext([MarshalAs(UnmanagedType.LPUTF8Str)] string t, ref Attrs a);
    [DllImport("*", EntryPoint = "emscripten_webgl_make_context_current")]
    private static extern int MakeCurrent(nint ctx);
    // A *statically declared* GL P/Invoke — trampoline generated at build time.
    [DllImport("*", EntryPoint = "glClearColor")]
    private static extern void GlClearColorStatic(float r, float g, float b, float a);

    [StructLayout(LayoutKind.Sequential)]
    private struct Attrs { public int A,D,S,Aa,Pm,Pd,Pp,Fi,Maj,Min,Ee,Es,Pc,Ro; }

    private static void Log(System.Text.StringBuilder sb, string s) { sb.Append(s).Append(" | "); JsLog(s); }

    [JSImport("log", "main.js")]
    internal static partial void JsLog(string s);

    [JSExport]
    public static string Run()
    {
        var sb = new System.Text.StringBuilder();
        try
        {
            Log(sb, "step1:enter");
            var p = GetProcAddress("glClear");
            Log(sb, $"step2:DllImport GetProcAddress('glClear')=0x{p:X}");

            var attrs = new Attrs();
            InitAttrs(ref attrs);
            attrs.Maj = 2; attrs.Min = 0; attrs.D = 1;
            Log(sb, "step3:InitAttrs ok");

            var ctx = CreateContext("#canvas", ref attrs);
            Log(sb, $"step4:CreateContext=0x{ctx:X}");
            if (ctx == 0) return sb.Append("FAIL: no context").ToString();

            var mc = MakeCurrent(ctx);
            Log(sb, $"step5:MakeCurrent={mc}");

            GlClearColorStatic(0.2f, 0.4f, 0.1f, 1f);
            Log(sb, "step6:STATIC DllImport glClearColor ok");

            var gl = new GL(new LamdaNativeContext(GetProcAddress));
            Log(sb, "step7:Silk GL object constructed");

            gl.ClearColor(0.1f, 0.2f, 0.3f, 1f);          // <-- Silk.NET dynamic fn-ptr call
            Log(sb, "step8:SILK gl.ClearColor ok");

            var ver = gl.GetStringS(StringName.Version);
            Log(sb, $"step9:SILK version={ver}");

            Log(sb, "step10:begin triangle");
            uint v = gl.CreateShader(ShaderType.VertexShader);
            Log(sb, $"step11:CreateShader={v}");
            gl.ShaderSource(v, "#version 300 es\nin vec2 p; void main(){ gl_Position=vec4(p,0,1); }");
            Log(sb, "step12:ShaderSource(string) ok");
            gl.CompileShader(v);
            Log(sb, "step13:CompileShader ok");
            gl.GetShader(v, ShaderParameterName.CompileStatus, out int okv);
            Log(sb, $"step14:GetShader(out int)={okv}");
            var slog = gl.GetShaderInfoLog(v);
            Log(sb, $"step15:GetShaderInfoLog len={slog?.Length}");
            uint f2 = gl.CreateShader(ShaderType.FragmentShader);
            gl.ShaderSource(f2, "#version 300 es\nprecision mediump float; out vec4 o; void main(){ o=vec4(1,0.6,0.1,1); }");
            gl.CompileShader(f2);
            gl.GetShader(f2, ShaderParameterName.CompileStatus, out int okf);
            Log(sb, $"step16:fs compiled={okf}");
            uint prog = gl.CreateProgram();
            gl.AttachShader(prog, v); gl.AttachShader(prog, f2); gl.LinkProgram(prog);
            gl.GetProgram(prog, ProgramPropertyARB.LinkStatus, out int okp);
            Log(sb, $"step17:LinkProgram={okp}");
            uint vao = gl.GenVertexArray();
            gl.BindVertexArray(vao);
            Log(sb, $"step18:VAO={vao}");
            uint vbo = gl.GenBuffer();
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
            Log(sb, $"step19:VBO={vbo}");
            float[] tri = new float[]{ 0f,0.8f, -0.8f,-0.8f, 0.8f,-0.8f };
            unsafe { fixed (float* pv = tri) gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(tri.Length*sizeof(float)), pv, BufferUsageARB.StaticDraw); }
            Log(sb, "step20:BufferData(void*) ok");
            gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 0, 0);
            gl.EnableVertexAttribArray(0);
            Log(sb, "step21:VertexAttribPointer ok");
            gl.Viewport(0, 0, 320, 240);
            gl.Clear((uint)ClearBufferMask.ColorBufferBit);
            gl.UseProgram(prog);
            gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
            Log(sb, $"step22:DrawArrays ok, glGetError={gl.GetError()}");
            return sb.Append("ALL OK").ToString();
        }
        catch (Exception ex) { return sb.Append("EX ").Append(ex.GetType().Name).Append(": ").Append(ex.Message).ToString(); }
    }
    public static void Main() { }
}
