using System.Runtime.InteropServices;

namespace CUE4Parse_Conversion.Textures;

// Used only by the macOS build of CUE4Parse-Conversion. The upstream helper
// extracts Windows PE DLLs into the working directory, which is both unsuitable
// for macOS and invalid when launched from a read-only application bundle.
internal static class TextureNativeLibrary
{
    public static void Prepare(string dllName)
    {
        var name = Path.GetFileNameWithoutExtension(dllName);
        var path = Path.Combine(AppContext.BaseDirectory, "lib" + name + ".dylib");
        if (!NativeLibrary.TryLoad(path, out var handle))
            throw new DllNotFoundException($"Cannot load the bundled texture decoder at {path}.");
        NativeLibrary.Free(handle);
    }
}
