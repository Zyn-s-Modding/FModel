using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using OpenTK.Graphics.OpenGL4;
using SharpGLTF.Schema2;
using System.Numerics;
using SkiaSharp;

namespace FModel.Desktop;

// The macOS viewport deliberately uses the OpenGL 3.2 core feature set. Apple
// cannot create the 4.6 bindless context used by the upstream Snooper renderer.
internal sealed class ModelViewport : OpenGlControlBase
{
    private sealed class Bindings(GlInterface gl) : OpenTK.IBindingsContext
    {
        public nint GetProcAddress(string name) => gl.GetProcAddress(name);
    }

    private sealed class Primitive
    {
        public required float[] Vertices;
        public required uint[] Indices;
        public required string Name;
        public required Vector4 Color;
        public byte[]? Texture;
        public int Vao, Vbo, Ebo, TextureId;
    }

    private readonly List<Primitive> _primitives = [];
    private int _program;
    private int _depth, _depthWidth, _depthHeight;
    private bool _initialized;
    private Vector3 _center;
    private float _radius = 1, _distance = 3, _yaw = 0.6f, _pitch = 0.3f;
    private Point? _pointer;
    private bool _wireframe;
    public bool Wireframe { get => _wireframe; set { _wireframe = value; RequestNextFrameRendering(); } }
    public string? SelectedNode { get; set; }
    public Action<string>? Error { get; set; }
    public string? Renderer { get; private set; }
    public bool Rendered { get; private set; }
    public string? CapturePath { get; set; }
    public bool CapturedGeometry { get; private set; }
    public IReadOnlyList<string> Nodes => _primitives.Select(x => x.Name).Distinct().ToArray();

    public ModelViewport(string glbPath)
    {
        var model = ModelRoot.Load(glbPath);
        var minimum = new Vector3(float.MaxValue); var maximum = new Vector3(float.MinValue);
        foreach (var node in model.LogicalNodes.Where(x => x.Mesh is not null))
        {
            foreach (var source in node.Mesh.Primitives)
            {
                var positions = source.GetVertexAccessor("POSITION").AsVector3Array();
                var normals = source.GetVertexAccessor("NORMAL")?.AsVector3Array();
                var uv = source.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array();
                var vertices = new float[positions.Count * 8];
                Matrix4x4.Invert(node.WorldMatrix, out var inverse);
                var normalMatrix = Matrix4x4.Transpose(inverse);
                for (var i = 0; i < positions.Count; i++)
                {
                    var p = Vector3.Transform(positions[i], node.WorldMatrix);
                    minimum = Vector3.Min(minimum, p); maximum = Vector3.Max(maximum, p);
                    var n = normals is not null ? Vector3.Normalize(Vector3.TransformNormal(normals[i], normalMatrix)) : Vector3.UnitY;
                    var t = uv?[i] ?? Vector2.Zero;
                    var offset = i * 8;
                    vertices[offset] = p.X; vertices[offset + 1] = p.Y; vertices[offset + 2] = p.Z;
                    vertices[offset + 3] = n.X; vertices[offset + 4] = n.Y; vertices[offset + 5] = n.Z;
                    vertices[offset + 6] = t.X; vertices[offset + 7] = t.Y;
                }
                var triangles = source.GetTriangleIndices().SelectMany(x => new[] { (uint)x.A, (uint)x.B, (uint)x.C }).ToArray();
                var color = source.Material?.FindChannel("BaseColor");
                var baseColor = color?.Parameters.FirstOrDefault(x => x.ValueType == typeof(Vector4))?.Value is Vector4 rgba ? rgba : Vector4.One;
                _primitives.Add(new Primitive { Name = node.Name ?? node.Mesh.Name ?? "Mesh", Vertices = vertices, Indices = triangles,
                    Color = baseColor, Texture = color?.Texture?.PrimaryImage?.Content.Content.ToArray() });
            }
        }
        if (_primitives.Count == 0) throw new InvalidDataException("The asset contains no triangle geometry.");
        _center = (minimum + maximum) / 2;
        _radius = Math.Max(0.001f, (maximum - minimum).Length() / 2);
        ResetCamera();
        Focusable = true;
        PointerPressed += (_, e) => { _pointer = e.GetPosition(this); e.Pointer.Capture(this); Focus(); };
        PointerReleased += (_, e) => { _pointer = null; e.Pointer.Capture(null); };
        PointerMoved += (_, e) =>
        {
            if (_pointer is not { } previous) return;
            var current = e.GetPosition(this); var delta = current - previous; _pointer = current;
            if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
            {
                var forward = Vector3.Normalize(_center - Eye()); var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
                _center += (right * (float)-delta.X + Vector3.UnitY * (float)delta.Y) * (_distance / Math.Max(100, (float)Bounds.Height));
            }
            else { _yaw += (float)delta.X * 0.01f; _pitch = Math.Clamp(_pitch + (float)delta.Y * 0.01f, -1.5f, 1.5f); }
            RequestNextFrameRendering();
        };
        PointerWheelChanged += (_, e) => { _distance = Math.Clamp(_distance * MathF.Exp((float)-e.Delta.Y * 0.1f), _radius * 0.01f, _radius * 1000); RequestNextFrameRendering(); e.Handled = true; };
    }

    public void ResetCamera() { _distance = _radius * 2.8f; RequestNextFrameRendering(); }
    private Vector3 Eye() => _center + new Vector3(MathF.Cos(_pitch) * MathF.Sin(_yaw), MathF.Sin(_pitch), MathF.Cos(_pitch) * MathF.Cos(_yaw)) * _distance;

    protected override void OnOpenGlInit(GlInterface gl)
    {
        try
        {
            GL.LoadBindings(new Bindings(gl));
            Renderer = GL.GetString(StringName.Renderer);
            var vertex = Compile(ShaderType.VertexShader, """
                #version 150
                in vec3 position; in vec3 normal; in vec2 uv;
                uniform mat4 transform;
                out vec3 vNormal; out vec2 vUv;
                void main() { gl_Position = transform * vec4(position,1.0); vNormal = normal; vUv = uv; }
                """);
            var fragment = Compile(ShaderType.FragmentShader, """
                #version 150
                in vec3 vNormal; in vec2 vUv;
                uniform vec4 color; uniform sampler2D diffuse; uniform int hasTexture; uniform int selected;
                out vec4 outputColor;
                void main() {
                    vec4 base = color * (hasTexture != 0 ? texture(diffuse,vUv) : vec4(1.0));
                    if (base.a < 0.1) discard;
                    float light = 0.3 + 0.7 * abs(dot(normalize(vNormal),normalize(vec3(0.4,0.8,0.6))));
                    vec3 lit = base.rgb * light;
                    if (selected != 0) lit = mix(lit,vec3(0.2,0.5,1.0),0.3);
                    outputColor = vec4(lit,base.a);
                }
                """);
            _program = GL.CreateProgram(); GL.AttachShader(_program, vertex); GL.AttachShader(_program, fragment);
            GL.BindAttribLocation(_program, 0, "position"); GL.BindAttribLocation(_program, 1, "normal"); GL.BindAttribLocation(_program, 2, "uv");
            GL.LinkProgram(_program); GL.GetProgram(_program, GetProgramParameterName.LinkStatus, out var success);
            if (success == 0) throw new Exception(GL.GetProgramInfoLog(_program));
            GL.DeleteShader(vertex); GL.DeleteShader(fragment);
            foreach (var primitive in _primitives)
            {
                primitive.Vao = GL.GenVertexArray(); GL.BindVertexArray(primitive.Vao);
                primitive.Vbo = GL.GenBuffer(); GL.BindBuffer(BufferTarget.ArrayBuffer, primitive.Vbo);
                GL.BufferData(BufferTarget.ArrayBuffer, primitive.Vertices.Length * sizeof(float), primitive.Vertices, BufferUsageHint.StaticDraw);
                primitive.Ebo = GL.GenBuffer(); GL.BindBuffer(BufferTarget.ElementArrayBuffer, primitive.Ebo);
                GL.BufferData(BufferTarget.ElementArrayBuffer, primitive.Indices.Length * sizeof(uint), primitive.Indices, BufferUsageHint.StaticDraw);
                for (var attribute = 0; attribute < 3; attribute++)
                {
                    GL.EnableVertexAttribArray(attribute);
                    GL.VertexAttribPointer(attribute, attribute == 2 ? 2 : 3, VertexAttribPointerType.Float, false, 8 * sizeof(float), (attribute == 0 ? 0 : attribute == 1 ? 3 : 6) * sizeof(float));
                }
                {
                    using var bitmap = primitive.Texture is not null ? SKBitmap.Decode(primitive.Texture) : null;
                    primitive.TextureId = GL.GenTexture(); GL.BindTexture(TextureTarget.Texture2D, primitive.TextureId);
                    if (bitmap is not null)
                        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, bitmap.Width, bitmap.Height, 0,
                            bitmap.ColorType == SKColorType.Bgra8888 ? OpenTK.Graphics.OpenGL4.PixelFormat.Bgra : OpenTK.Graphics.OpenGL4.PixelFormat.Rgba, PixelType.UnsignedByte, bitmap.GetPixels());
                    else
                        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, 1, 1, 0, OpenTK.Graphics.OpenGL4.PixelFormat.Rgba, PixelType.UnsignedByte, new byte[] { 255, 255, 255, 255 });
                    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
                    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
                }
            }
            GL.BindVertexArray(0);
            _initialized = true;
        }
        catch (Exception e) { Error?.Invoke(e.Message); }
    }

    protected override unsafe void OnOpenGlRender(GlInterface gl, int framebuffer)
    {
        if (!_initialized) return;
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var width = Math.Max(1, (int)(Bounds.Width * scale)); var height = Math.Max(1, (int)(Bounds.Height * scale));
        GL.Viewport(0, 0, width, height);
        if (_depth == 0) _depth = GL.GenRenderbuffer();
        if (_depthWidth != width || _depthHeight != height)
        {
            GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _depth);
            GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.DepthComponent24, width, height);
            _depthWidth = width; _depthHeight = height;
        }
        GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, _depth);
        GL.Disable(EnableCap.ScissorTest); GL.Disable(EnableCap.Blend);
        GL.ColorMask(true, true, true, true); GL.DepthMask(true); GL.ClearDepth(1);
        GL.ClearColor(0.08f, 0.09f, 0.11f, 1); GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        GL.Enable(EnableCap.DepthTest); GL.Disable(EnableCap.CullFace); GL.DepthFunc(DepthFunction.Less); GL.DepthMask(true);
        GL.PolygonMode(TriangleFace.FrontAndBack, Wireframe ? PolygonMode.Line : PolygonMode.Fill);
        GL.UseProgram(_program);
        var view = Matrix4x4.CreateLookAt(Eye(), _center, Vector3.UnitY);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 4, Math.Max(0.01f, (float)(Bounds.Width / Math.Max(1, Bounds.Height))), _radius * 0.001f, _radius * 2000);
        var transform = view * projection;
        GL.UniformMatrix4(GL.GetUniformLocation(_program, "transform"), 1, false, (float*)&transform);
        GL.Uniform1(GL.GetUniformLocation(_program, "diffuse"), 0);
        GL.ActiveTexture(TextureUnit.Texture0);
        foreach (var primitive in _primitives)
        {
            GL.Uniform4(GL.GetUniformLocation(_program, "color"), primitive.Color.X, primitive.Color.Y, primitive.Color.Z, primitive.Color.W);
            GL.Uniform1(GL.GetUniformLocation(_program, "hasTexture"), primitive.TextureId == 0 ? 0 : 1);
            GL.Uniform1(GL.GetUniformLocation(_program, "selected"), SelectedNode == primitive.Name ? 1 : 0);
            GL.BindTexture(TextureTarget.Texture2D, primitive.TextureId); GL.BindVertexArray(primitive.Vao);
            GL.DrawElements(OpenTK.Graphics.OpenGL4.PrimitiveType.Triangles, primitive.Indices.Length, DrawElementsType.UnsignedInt, 0);
        }
        GL.BindVertexArray(0); GL.UseProgram(0); GL.Disable(EnableCap.DepthTest); GL.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill);
        Rendered = true;
        if (CapturePath is { } capture)
        {
            var pixels = new byte[width * height * 4];
            GL.ReadPixels(0, 0, width, height, OpenTK.Graphics.OpenGL4.PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
            var reference = pixels.AsSpan(0, 3).ToArray();
            for (var i = 0; i < pixels.Length; i += 4)
                if (Math.Abs(pixels[i] - reference[0]) + Math.Abs(pixels[i + 1] - reference[1]) + Math.Abs(pixels[i + 2] - reference[2]) > 30) { CapturedGeometry = true; break; }
            using var raw = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, raw.GetPixels(), pixels.Length);
            using var upright = new SKBitmap(width, height); using var canvas = new SKCanvas(upright);
            canvas.Translate(0, height); canvas.Scale(1, -1); canvas.DrawBitmap(raw, 0, 0);
            using var encoded = upright.Encode(SKEncodedImageFormat.Png, 100); using var output = File.Create(capture); encoded.SaveTo(output);
            CapturePath = null;
        }
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        foreach (var p in _primitives) { if (p.Vao != 0) GL.DeleteVertexArray(p.Vao); if (p.Vbo != 0) GL.DeleteBuffer(p.Vbo); if (p.Ebo != 0) GL.DeleteBuffer(p.Ebo); if (p.TextureId != 0) GL.DeleteTexture(p.TextureId); }
        if (_program != 0) GL.DeleteProgram(_program);
        if (_depth != 0) GL.DeleteRenderbuffer(_depth);
        _initialized = false;
        _program = 0;
    }

    private static int Compile(ShaderType type, string source)
    {
        var shader = GL.CreateShader(type); GL.ShaderSource(shader, source); GL.CompileShader(shader);
        GL.GetShader(shader, ShaderParameter.CompileStatus, out var success);
        if (success == 0) { var error = GL.GetShaderInfoLog(shader); GL.DeleteShader(shader); throw new Exception(error); }
        return shader;
    }
}
