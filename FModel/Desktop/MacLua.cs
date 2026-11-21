using CUE4Parse.UE4.Lua.unluac;
using System.Buffers.Binary;
using System.Text;

namespace FModel.Desktop;

internal static class MacLua
{
    private static readonly Lazy<Unluac> Decompiler = new(() => new Unluac(Path.Combine(AppContext.BaseDirectory, "libunluac.dylib")));

    internal static string Read(byte[] data, EUnluacFlags flags, string? opcodeMap, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (data.Length < 4 || BinaryPrimitives.ReadUInt32LittleEndian(data) != UnluacHelper.LuaMagic)
            return Encoding.UTF8.GetString(data);
        var map = string.IsNullOrWhiteSpace(opcodeMap) ? [] : File.ReadAllBytes(opcodeMap);
        if (map.Length > 0) flags |= EUnluacFlags.OpCodeMap;
        var result = Decompiler.Value.Decompile(data, map, (uint)flags, out var output, out var log);
        token.ThrowIfCancellationRequested();
        var message = Encoding.UTF8.GetString(log);
        if (result is not (EUnluacErrorCode.Ok or EUnluacErrorCode.PartialDecompile))
            throw new InvalidDataException("Lua decompilation failed. " + message);
        if (message.Length > 0) Serilog.Log.Warning("Lua decompiler reported {Message}", message);
        return Encoding.UTF8.GetString(output);
    }
}
