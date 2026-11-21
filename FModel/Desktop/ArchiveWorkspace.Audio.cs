using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Criware;
using CUE4Parse.UE4.Assets.Exports.Fmod;
using CUE4Parse.UE4.Assets.Exports.Sound;
using CUE4Parse.UE4.Assets.Exports.Sound.Node;
using CUE4Parse.UE4.Assets.Exports.Wwise;
using CUE4Parse.UE4.Criware;
using CUE4Parse.UE4.Criware.Readers;
using CUE4Parse.UE4.FMod;
using CUE4Parse.UE4.Wwise;
using CUE4Parse_Conversion.Sounds;
using Newtonsoft.Json;
using CUE4Parse.GameTypes.Borderlands3.Assets.Exports;
using CUE4Parse.GameTypes.Borderlands4.Assets.Exports;
using CUE4Parse.GameTypes.Borderlands4.Wwise;
using CUE4Parse.GameTypes.LegoBatman.Assets;
using CUE4Parse.GameTypes.SMG.UE4.Assets.Exports.Wwise;
using CUE4Parse.GameTypes.SquareEnix.UE4.Assets.Exports;
using CUE4Parse.GameTypes.WarnerBros.GothamKnights.Assets.Exports.Wwise;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Versions;

namespace FModel.Desktop;

public sealed partial class ArchiveWorkspace
{
    private WwiseProvider? _wwise;
    private FModProvider? _fmod;
    private CriWareProvider? _criware;
    private WwiseProvider Wwise => _wwise ??= new WwiseProvider(Provider!, Profile!.Directory);
    private FModProvider Fmod => _fmod ??= new FModProvider(Provider!, Profile!.Directory);
    private CriWareProvider Criware => _criware ??= new CriWareProvider(Provider!, Profile!.Directory);

    private List<PreviewSound> ExtractSounds(UObject export, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var directory = Path.GetDirectoryName(Provider!.FixPath(export.Owner?.Name ?? ""))?.Replace('\\', '/') ?? "";
        List<PreviewSound> WwiseSounds(IEnumerable<WwiseExtractedSound> sounds) => sounds.Select(sound =>
        {
            token.ThrowIfCancellationRequested();
            return new PreviewSound(sound.OutputPath, sound.Extension, sound.GetData());
        }).Where(sound => sound.Data.Length > 0).ToList();
        List<PreviewSound> FmodSounds(IEnumerable<FModExtractedSound> sounds) => sounds.Select(sound =>
            new PreviewSound(directory + "/" + sound.Name, sound.Extension, sound.Data)).ToList();
        List<PreviewSound> CriwareSounds(IEnumerable<CriWareExtractedSound> sounds) => sounds.Select(sound =>
            new PreviewSound(directory + "/" + sound.Name, sound.Extension, sound.Data)).ToList();

        switch (export)
        {
            case USQEXSEADSound sound:
                return AudioConversion.ExtractBankAsync(sound.SQEXSoundData?.ReadDataOnce() ?? [], "sab", directory, token).GetAwaiter().GetResult();
            case USoundWave or USoundNodeWave or UAkMediaAssetData:
                if (export is UAkMediaAssetData mediaData && mediaData.Outer?.Object?.Value is UAkMediaAsset) return [];
                export.Decode(_settings!.CompressedAudioMode == ECompressedAudio.PlayDecompressed, out var format, out var data);
                return data is { Length: > 0 } ? [new(directory.TrimStart('/') + "/" + export.Name, format, data)] : [];
            case UExternalSource source:
                var wem = source.Data?.WemFile?.GetData();
                return wem is { Length: > 0 } ? [new(directory + "/" + Path.GetFileNameWithoutExtension((source.ExternalSourcePath ?? source.Name).Replace('\\', '/')), "wem", wem)] : [];
            case UAkMediaAsset media: return MediaSounds(media);
            case UAkAudioEventData eventData:
                return eventData.MediaList.Select(index => index?.Object?.Value).OfType<UAkMediaAsset>()
                    .SelectMany(media => MediaSounds(media, $"{eventData.Outer?.Name ?? eventData.Name} ({media.ID})")).ToList();
            case UAkAudioBank bank: return WwiseSounds(Wwise.ExtractBankSounds(bank));
            case UAkAudioEvent audioEvent: return WwiseSounds(Wwise.ExtractAudioEventSounds(audioEvent));
            case UDialogPerformanceData dialogue: return WwiseSounds(Wwise.ExtractDialogBorderlands3(dialogue));
            case UFaceFXAnimSet face when Provider.Versions.Game == EGame.GAME_Borderlands4:
                return WwiseSounds(face.FaceFXAnimDataList.SelectMany(item => Wwise.ExtractAudioEventBorderlands4(Wwise.GetOwnerDirectory(face), item.ID.Name, false)));
            case UGbxGraphAsset graph:
                return WwiseSounds(GbxAudioUtil.GetAndClearEvents().SelectMany(item => Wwise.ExtractAudioEventBorderlands4(Wwise.GetOwnerDirectory(graph), item.Key, item.Value)));
            case UWubAudioEvent audioEvent: return WwiseSounds(Wwise.ExtractWubAudioEventSounds(audioEvent));
            case UWubDialogueEvent dialogue:
                return dialogue.Wems.SelectMany(wem => Provider.Files.Values.Where(file => file.Path.EndsWith(wem.Text + ".wem", StringComparison.OrdinalIgnoreCase)))
                    .Select(file => new PreviewSound(file.PathWithoutExtension, file.Extension, file.Read())).ToList();
            case UOrpheusBank { SoundBank: not null } bank: return WwiseSounds(Wwise.ExtractBankSounds(bank.SoundBank));
            case UOrpheusEvent audioEvent: return WwiseSounds(Wwise.ExtractGothamKnightsAudioEventSounds(audioEvent));
            case UFMODBank bank: return FmodSounds(Fmod.ExtractBankSounds(bank));
            case UFMODEvent audioEvent: return FmodSounds(Fmod.ExtractEventSounds(audioEvent));
            case USoundAtomCueSheet sheet: return CriwareSounds(Criware.ExtractCriWareSounds(sheet));
            case UAtomCueSheet sheet: return CriwareSounds(Criware.ExtractCriWareSounds(sheet));
            case USoundAtomCue cue: return CriwareSounds(Criware.ExtractCriWareSounds(cue));
            case UAtomWaveBank bank: return CriwareSounds(Criware.ExtractCriWareSounds(bank));
            case USQEXSEADSoundBank bank:
                return AudioConversion.ExtractBankAsync(bank.SQEXSoundBankData?.ReadDataOnce() ?? [], "sab", directory, token).GetAwaiter().GetResult();
            default: return [];
        }

        List<PreviewSound> MediaSounds(UAkMediaAsset media, string? fallback = null)
        {
            var path = directory + "/" + (media.MediaName ?? fallback ?? media.Name);
            var sounds = new List<PreviewSound>();
            if (media.CurrentMediaAssetData?.ResolvedObject?.Object?.Value is UAkMediaAssetData mediaData)
            {
                mediaData.Decode(_settings!.CompressedAudioMode == ECompressedAudio.PlayDecompressed, out var format, out var data);
                if (data is { Length: > 0 }) sounds.Add(new(path, format, data));
            }
            if (Provider.Versions.Game == EGame.GAME_GearsofWarEDay && media.CustomGameData is FByteBulkData bulk && bulk.Data is { Length: > 0 } bytes)
                sounds.Add(new(path, "wem", bytes));
            return sounds;
        }
    }

    private PreviewResult? PreviewAudioBank(AssetEntry entry, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var archive = entry.File.CreateReader();
        var directory = entry.File.Directory.TrimEnd('/');
        switch (entry.Type.ToLowerInvariant())
        {
            case "bnk" or "pck":
                var bank = new WwiseReader(new FWwiseArchive(archive), new WwiseGameFileSource(entry.File));
                var media = Wwise.ExtractBankSounds(bank);
                var sounds = media.Select(sound => new PreviewSound(sound.OutputPath, sound.Extension, sound.GetData())).ToList();
                return new(JsonConvert.SerializeObject(bank, Formatting.Indented), [], [], Sounds: sounds);
            case "bank" or "fsb":
                if (!Fmod.TryLoadBank(archive, entry.File.NameWithoutExtension, out var fmod))
                    throw new InvalidDataException("The FMOD bank could not be parsed.");
                var samples = Fmod.ExtractBankSounds(fmod).Select(sound => new PreviewSound(directory + "/" + sound.Name, sound.Extension, sound.Data)).ToList();
                return new(JsonConvert.SerializeObject(fmod, Formatting.Indented, new CUE4Parse.FmodSoundBankConverter(), new Newtonsoft.Json.Converters.StringEnumConverter()), [], [], Sounds: samples);
            case "awb":
                var awb = new AwbReader(archive);
                var waves = Criware.ExtractCriWareSounds(awb, archive.Name).Select(sound => new PreviewSound(directory + "/" + sound.Name, sound.Extension, sound.Data)).ToList();
                return new(JsonConvert.SerializeObject(awb, Formatting.Indented), [], [], Sounds: waves);
            case "acb":
                var acb = new AcbReader(archive);
                var cues = Criware.ExtractCriWareSounds(acb, archive.Name).Select(sound => new PreviewSound(directory + "/" + sound.Name, sound.Extension, sound.Data)).ToList();
                return new(JsonConvert.SerializeObject(acb, Formatting.Indented), [], [], Sounds: cues);
            default: return null;
        }
    }
}

public sealed record PreviewSound(string Path, string Format, byte[] Data);
