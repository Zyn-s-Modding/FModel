using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Media;
using Snooper.Core;

namespace FModel.Desktop;

internal static class DesktopFonts
{
    public static FontFamily Interface { get; private set; } = new("avares://FModel/Resources/Fonts#Selawik");
    public static FontFamily Code { get; private set; } = new("Consolas,Menlo,monospace");

    public static void Initialize()
    {
        if (OperatingSystem.IsMacOS())
        {
            foreach (var file in new[] { "segoeui.ttf", "segoeuib.ttf", "seguisb.ttf", "segoeuii.ttf", "consola.ttf", "consolab.ttf", "consolai.ttf" })
            {
                var path = UiFonts.Resolve(file, "");
                if (File.Exists(path)) RegisterFont(path);
            }
        }
        Interface = FontManager.Current.SystemFonts.FirstOrDefault(x => x.Name == "Segoe UI") ?? Interface;
        Code = FontManager.Current.SystemFonts.FirstOrDefault(x => x.Name == "Consolas") ?? Code;
        Serilog.Log.Information("Interface font {InterfaceFont}, document font {DocumentFont}", Interface, Code);
    }

    // Registration lasts for this process and lets CoreText and Skia use fonts
    // supplied beside the application without installing them on the Mac.
    private static void RegisterFont(string path)
    {
        var bytes = Encoding.UTF8.GetBytes(path);
        var url = CFURLCreateFromFileSystemRepresentation(0, bytes, bytes.Length, false);
        if (url == 0) return;
        try
        {
            var registered = CTFontManagerRegisterFontsForURL(url, 1, out var error);
            if (error != 0) CFRelease(error);
            Serilog.Log.Debug("Registered local font {FontPath}: {Registered}", path, registered);
        }
        finally { CFRelease(url); }
    }

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern nint CFURLCreateFromFileSystemRepresentation(nint allocator, byte[] path, nint length, [MarshalAs(UnmanagedType.I1)] bool isDirectory);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRelease(nint value);
    [DllImport("/System/Library/Frameworks/CoreText.framework/CoreText")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CTFontManagerRegisterFontsForURL(nint url, uint scope, out nint error);
}
