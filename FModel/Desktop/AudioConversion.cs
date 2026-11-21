using System.Diagnostics;

namespace FModel.Desktop;

internal static class AudioConversion
{
    public static async Task<List<PreviewSound>> ExtractBankAsync(byte[] data, string format, string outputDirectory, CancellationToken token)
    {
        if (data.Length == 0) return [];
        var decoder = Environment.GetEnvironmentVariable("FMODEL_VGMSTREAM") ?? Path.Combine(AppContext.BaseDirectory, "vgmstream-cli");
        var directory = Path.Combine(Path.GetTempPath(), "FModel-bank-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var input = Path.Combine(directory, "input." + format);
            await File.WriteAllBytesAsync(input, data, token);
            var options = new ProcessStartInfo(decoder) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var argument in new[] { "-S", "0", "-o", Path.Combine(directory, "?n_?s.wav"), input }) options.ArgumentList.Add(argument);
            using var process = Process.Start(options) ?? throw new IOException("The bank decoder could not start.");
            var standardOutput = process.StandardOutput.ReadToEndAsync(token);
            var standardError = process.StandardError.ReadToEndAsync(token);
            try { await process.WaitForExitAsync(token); }
            catch (OperationCanceledException) { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); throw; }
            var error = await standardError;
            await standardOutput;
            if (process.ExitCode != 0) throw new InvalidDataException($"The bank decoder failed: {error[..Math.Min(error.Length, 2000)]}");
            var sounds = new List<PreviewSound>();
            foreach (var path in Directory.EnumerateFiles(directory, "*.wav").Order(StringComparer.Ordinal))
                sounds.Add(new(outputDirectory + "/" + Path.GetFileNameWithoutExtension(path), "wav", await File.ReadAllBytesAsync(path, token)));
            return sounds;
        }
        finally { Directory.Delete(directory, true); }
    }

    public static async Task<byte[]> ForPlaybackAsync(byte[] data, string format, CancellationToken token = default, ulong criwareKey = 0)
    {
        var extension = format.ToLowerInvariant();
        if (extension == "ogg") return await Task.Run(() => AudioWindow.DecodeVorbis(data), token);
        if (extension == "hca") return await Task.Run(() => CUE4Parse.UE4.Criware.Decoders.HCA.HcaWaveStream.ConvertHcaToWav(data, criwareKey), token);
        if (extension == "adx") return await Task.Run(() => CUE4Parse.UE4.Criware.Decoders.ADX.AdxDecoder.ConvertAdxToWav(data, criwareKey), token);
        if (extension is "wav" or "wave") return data;
        if (extension is "mp3" or "aif" or "aiff" or "aac" or "m4a" or "flac")
            return await Task.Run(() => { using var audio = new MacAudio(data); return audio.DecodePcm(); }, token);
        if (extension.Length is < 1 or > 16 || extension.Any(x => !char.IsAsciiLetterOrDigit(x)))
            throw new NotSupportedException($"Unsupported audio format: {format}");
        var decoder = Environment.GetEnvironmentVariable("FMODEL_VGMSTREAM") ?? Path.Combine(AppContext.BaseDirectory, "vgmstream-cli");
        if (!File.Exists(decoder)) throw new NotSupportedException($"{format} playback requires the bundled vgmstream decoder. Rebuild the macOS application with its native audio helper.");
        var directory = Path.Combine(Path.GetTempPath(), "FModel-audio-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var input = Path.Combine(directory, "input." + extension);
            var output = Path.Combine(directory, "output.wav");
            await File.WriteAllBytesAsync(input, data, token);
            var options = new ProcessStartInfo(decoder) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var argument in new[] { "-i", "-o", output, input }) options.ArgumentList.Add(argument);
            using var process = Process.Start(options) ?? throw new IOException("The audio decoder could not start.");
            var standardOutput = process.StandardOutput.ReadToEndAsync(token);
            var standardError = process.StandardError.ReadToEndAsync(token);
            try { await process.WaitForExitAsync(token); }
            catch (OperationCanceledException) { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); throw; }
            var error = await standardError;
            await standardOutput;
            if (process.ExitCode != 0 || !File.Exists(output))
                throw new NotSupportedException($"The audio decoder could not decode {format}: {error[..Math.Min(error.Length, 2000)]}");
            return await File.ReadAllBytesAsync(output, token);
        }
        finally { Directory.Delete(directory, true); }
    }
}
