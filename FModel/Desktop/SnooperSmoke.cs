using CUE4Parse_Conversion;
using Editor;
using OpenTK.Graphics.OpenGL4;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Snooper.Hosting;
using Snooper.Rendering.Cache;
using Snooper.Rendering.Components.Mesh;
using System.Numerics;
using System.Text.Json;

namespace FModel.Desktop;

internal static class SnooperSmoke
{
    public static int Run(string[] args)
    {
        if (args.Length != 3) throw new ArgumentException("Usage: --snooper-smoke-test profile.json asset-name output-directory");
        var profile = GameProfiles.Load(args[0]);
        var output = Path.GetFullPath(args[2]);
        Directory.CreateDirectory(output);
        var settings = new DesktopSettings { NaniteFormat = CUE4Parse_Conversion.Options.ENaniteMeshFormat.NoNanite };
        using var workspace = new ArchiveWorkspace { Log = Console.WriteLine };
        workspace.Open(profile, settings, CancellationToken.None);
        Bridge.Host = new SmokeHost(workspace.Exports, output);
        var file = workspace.Files.Single(x => x.Name == args[1]);
        var asset = workspace.Provider!.LoadPackage(file.File).GetExports().First(SnooperScene.CanView);
        var actor = SnooperScene.Create(asset, settings, profile, CancellationToken.None);
        using var editor = new EditorWindow(30, 1200, 800, workspace.Provider);
        SnooperScene.Load(editor, actor);
        var frames = 0;
        var phase = 0;
        byte[]? baseline = null;
        var checks = new List<string>();
        var skeletal = actor.Components.OfType<SkinnedMeshComponent>().FirstOrDefault();
        var playback = (skeletal as SkeletalMeshComponent)?.Playback;
        var initialTime = playback?.Time;
        var bone = skeletal is null ? -1 : Math.Min(2, skeletal.Pose.BoneCount - 1);
        var nextFrame = 12;
        editor.RenderFrame += _ =>
        {
            if (++frames < nextFrame || TextureCache.IsLoading) return;
            nextFrame = frames + 3;
            if (phase == 0)
            {
                Capture(editor, Path.Combine(output, "snooper.png"));
                baseline = ReadScene(editor);
                checks.Add("Original editor, textured scene, lighting, shadows, selection and Retina framebuffer");
                if (playback is not null)
                {
                    if (playback.Time == initialTime) throw new InvalidOperationException("Animation playback did not advance.");
                    checks.Add($"Animation timeline advanced from {initialTime:0.000} to {playback.Time:0.000}");
                    playback.IsPlaying = false;
                }
                editor.Manager.Wireframe.Enabled = editor.Manager.Wireframe.Overlay = true;
                phase++; return;
            }
            if (phase == 1)
            {
                if (Difference(baseline!, ReadScene(editor)) < 100) throw new InvalidOperationException("Wireframe did not affect the rendered scene.");
                Capture(editor, Path.Combine(output, "wireframe.png"));
                checks.Add("Wireframe overlay changes the rendered geometry without the NVIDIA extension");
                editor.Manager.Wireframe.Enabled = false;
                if (bone >= 0 && skeletal is not null)
                {
                    var edited = skeletal.Pose.BoneMatrices[bone];
                    edited.Translation += new Vector3(0.3f, 0.15f, 0);
                    skeletal.MoveBone(bone, edited);
                    if (!skeletal.Pose.IsBoneEdited(bone)) throw new InvalidOperationException("Bone edit was not applied.");
                }
                phase++; return;
            }
            if (phase == 2 && bone >= 0 && skeletal is not null)
            {
                if (Difference(baseline!, ReadScene(editor)) < 100) throw new InvalidOperationException("Bone editing did not deform the rendered mesh.");
                Capture(editor, Path.Combine(output, "bone-edit.png"));
                skeletal.ResetBones();
                if (skeletal.Pose.IsBoneEdited(bone)) throw new InvalidOperationException("Bone reset failed.");
                checks.Add($"Bone editing, GPU skinning and reset ({skeletal.Pose.Skeleton.GetBoneName(bone)})");
                phase++; return;
            }
            if (phase == 3 && skeletal is { MorphWeights.Length: > 0 })
            {
                baseline = ReadScene(editor);
                Array.Fill(skeletal.MorphWeights, 1f);
                MarkMorphDirty();
                phase++; return;
            }
            if (phase == 4 && skeletal is not null)
            {
                if (Difference(baseline!, ReadScene(editor)) < 100) throw new InvalidOperationException("Morph weights did not deform the rendered mesh.");
                Capture(editor, Path.Combine(output, "morph-targets.png"));
                Array.Clear(skeletal.MorphWeights);
                MarkMorphDirty();
                checks.Add($"Morph deformation changes rendered geometry ({skeletal.MorphWeights.Length} targets) and resets weights");
                phase++; return;
            }
            actor.Export(workspace.Exports);
            if (workspace.Exports.TotalQueued > 0) checks.Add($"Original scene export queued {workspace.Exports.TotalQueued} items");
            File.WriteAllText(Path.Combine(output, "checks.json"), JsonSerializer.Serialize(new
            {
                Asset = asset.GetPathName(), Renderer = GL.GetString(StringName.Renderer), Checks = checks,
                MorphTargets = skeletal?.MorphWeights.Length ?? 0,
                ActorCount = editor.Manager.ActorCount, Textures = TextureCache.LoadedTextureCount
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"PASS: original Snooper editor rendered {editor.Manager.ActorCount} actors, {TextureCache.LoadedTextureCount} resident textures on {GL.GetString(StringName.Renderer)}.");
            foreach (var check in checks) Console.WriteLine("PASS: " + check);
            editor.Close();
        };
        editor.Run();
        return 0;

        void MarkMorphDirty()
        {
            // Exercise the same internal dirty flag used by the original editor widget.
            typeof(Snooper.Rendering.Components.ActorComponent).GetMethod("MarkDirty", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(skeletal, [Snooper.Rendering.Components.DirtyFlags.Morph]);
        }
    }

    private static byte[] ReadScene(EditorWindow editor)
    {
        var texture = editor.Manager.Pipeline.GetFinalTexture();
        var pixels = new byte[checked(texture.Width * texture.Height * 4)];
        GL.GetTextureSubImage((uint)texture, 0, 0, 0, 0, texture.Width, texture.Height, 1, PixelFormat.Rgba, PixelType.UnsignedByte, pixels.Length, pixels);
        return pixels;
    }

    private static int Difference(byte[] first, byte[] second)
    {
        if (first.Length != second.Length) throw new InvalidOperationException("Unexpected scene size change during validation.");
        var changed = 0;
        for (var i = 0; i < first.Length; i++) if (Math.Abs(first[i] - second[i]) > 10) changed++;
        return changed;
    }

    internal static void Capture(EditorWindow editor, string path)
    {
        var width = editor.FramebufferSize.X; var height = editor.FramebufferSize.Y;
        var pixels = new byte[checked(width * height * 4)];
        GL.ReadPixels(0, 0, width, height, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
        if (pixels.Where((_, i) => i % 4 != 3).Max() - pixels.Where((_, i) => i % 4 != 3).Min() < 30)
            throw new InvalidOperationException("The original Snooper framebuffer was blank.");
        using var image = Image.LoadPixelData<Rgba32>(pixels, width, height);
        image.Mutate(x => x.Flip(FlipMode.Vertical));
        image.SaveAsPng(path);
    }

    private sealed class SmokeHost(ExportSession session, string directory) : IBridgeHost
    {
        public string Name => "FModel";
        public string ImGuiIniPath => Path.Combine(directory, "snooper.ini");
#if DEBUG
        public bool SuppressViewerLogs { get; set; }
#endif
        public ExportSession Session => session;
        public void ShowExportSession() { }
    }
}
