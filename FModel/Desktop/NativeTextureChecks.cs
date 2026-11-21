using System.Reflection;
using System.Security.Cryptography;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse_Conversion.Textures;

namespace FModel.Desktop;

internal static class NativeTextureChecks
{
    internal static void Run(string repository)
    {
        var cache = Path.Combine(repository, ".native-deps", "texture");
        var vectors = Path.Combine(cache, "tegra_swizzle-d7ed318ce233a4d557d4a2f207ed5dc04ed813c0", "block_linear");
        if (Directory.Exists(vectors))
        {
            var source = File.ReadAllBytes(Path.Combine(vectors, "64_rgba_tiled.bin"));
            var expected = File.ReadAllBytes(Path.Combine(vectors, "64_rgba.bin"));
            var actual = PlatformDeswizzlers.GetDeswizzledData(source, new FTexture2DMipMap(64, 64, 1), PixelFormatUtils.PixelFormats[EPixelFormat.PF_R8G8B8A8]);
            if (!actual.AsSpan().SequenceEqual(expected)) throw new InvalidDataException("Managed Tegra decoder differs from the upstream RGBA vector.");
            Console.WriteLine("PASS: Switch deswizzling through the managed texture entry point");
        }
        var decoder = typeof(TextureDecoder).Assembly.GetType("CUE4Parse_Conversion.Textures.Crunch.CrunchDecoder")!;
        foreach (var (name, format, hash) in new[]
        {
            ("test-dxt1.crn", EPixelFormat.PF_DXT1, "2ACC9BD5F77D9005B95DB8AA0079E043DE51DBAF38DB02F75546E3E8D980E716"),
            ("test-dxt5.crn", EPixelFormat.PF_DXT5, "E9B4D79CAED4709238526FBECAFB692ACC6EF703BC83A2C30F65FB6412785BCD")
        })
        {
            var path = Path.Combine(cache, name);
            if (!File.Exists(path)) { Console.WriteLine("SKIP: optional Crunch sample " + name); continue; }
            var source = File.ReadAllBytes(path);
            var actual = (byte[])decoder.GetMethod("DecompressMip", BindingFlags.Public | BindingFlags.Static)!
                .Invoke(null, [source, 512, 512, 1, PixelFormatUtils.PixelFormats[format]])!;
            if (Convert.ToHexString(SHA256.HashData(actual)) != hash) throw new InvalidDataException("Managed Crunch output differs for " + name);
            var contextType = decoder.GetNestedType("CrunchContext")!;
            using var context = (IDisposable)Activator.CreateInstance(contextType, source, 0, source.Length)!;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            var begin = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(source.AsSpan(70));
            var end = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(source.AsSpan(74));
            var output = new byte[actual.Length];
            var pitch = (uint)(128 * PixelFormatUtils.PixelFormats[format].BlockBytes);
            var success = (bool)contextType.GetMethod("TryDecompressSegment")!.Invoke(context, [source, begin, (uint)(end - begin), output, pitch, 0u])!;
            if (!success || !output.AsSpan().SequenceEqual(actual)) throw new InvalidDataException("Crunch context lost its source buffer across a compacting collection.");
            Console.WriteLine("PASS: managed Crunch header, DXT output and pinned context lifetime for " + name);
        }
    }
}
