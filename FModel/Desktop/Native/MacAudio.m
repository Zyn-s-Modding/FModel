#import <AVFoundation/AVFoundation.h>
#import <AppKit/AppKit.h>
#import <AudioToolbox/AudioToolbox.h>
#import <CoreAudio/CoreAudio.h>
#import <Accelerate/Accelerate.h>

@interface FModelAudio : NSObject {
@public
    AudioFileID _file;
    ExtAudioFileRef _decoder;
    AudioStreamBasicDescription _format;
}
@property(nonatomic, strong) AVAudioPlayer *player;
@property(nonatomic, strong) NSData *data;
@property(nonatomic) AudioFileID file;
@property(nonatomic) ExtAudioFileRef decoder;
@property(nonatomic) AudioStreamBasicDescription format;
@property(nonatomic) FFTSetup fft;
@end

@implementation FModelAudio
@synthesize file = _file, decoder = _decoder, format = _format;
- (void)dealloc {
    [_player stop];
    if (_decoder) ExtAudioFileDispose(_decoder);
    if (_file) AudioFileClose(_file);
    if (_fft) vDSP_destroy_fftsetup(_fft);
}
@end

static OSStatus ReadAudioBytes(void *context, SInt64 position, UInt32 requested, void *buffer, UInt32 *actual) {
    NSData *data = ((__bridge FModelAudio *)context).data;
    if (position < 0) return kAudioFilePositionError;
    UInt64 available = (UInt64)position < data.length ? data.length - (UInt64)position : 0;
    *actual = (UInt32)MIN((UInt64)requested, available);
    if (*actual) memcpy(buffer, (const char *)data.bytes + position, *actual);
    return noErr;
}
static SInt64 AudioByteCount(void *context) { return ((__bridge FModelAudio *)context).data.length; }
static FModelAudio *Audio(void *handle) { return (__bridge FModelAudio *)handle; }

void *fmodel_audio_create(const void *bytes, int length) {
    @autoreleasepool {
        if (!bytes || length <= 0) return NULL;
        FModelAudio *audio = [FModelAudio new];
        audio.data = [NSData dataWithBytes:bytes length:(NSUInteger)length];
        NSError *error = nil;
        audio.player = [[AVAudioPlayer alloc] initWithData:audio.data error:&error];
        if (!audio.player) return NULL;
        [audio.player prepareToPlay];
        if (AudioFileOpenWithCallbacks((__bridge void *)audio, ReadAudioBytes, NULL, AudioByteCount, NULL, 0, &audio->_file) == noErr) {
            UInt32 size = sizeof(AudioStreamBasicDescription);
            AudioFileGetProperty(audio.file, kAudioFilePropertyDataFormat, &size, &audio->_format);
            if (ExtAudioFileWrapAudioFileID(audio.file, false, &audio->_decoder) == noErr) {
                AudioStreamBasicDescription output = {0};
                output.mSampleRate = audio.format.mSampleRate;
                output.mFormatID = kAudioFormatLinearPCM;
                output.mFormatFlags = kAudioFormatFlagIsFloat | kAudioFormatFlagIsPacked;
                output.mBytesPerPacket = output.mBytesPerFrame = sizeof(float);
                output.mFramesPerPacket = 1;
                output.mChannelsPerFrame = 1;
                output.mBitsPerChannel = 32;
                if (ExtAudioFileSetProperty(audio.decoder, kExtAudioFileProperty_ClientDataFormat, sizeof(output), &output) != noErr) {
                    ExtAudioFileDispose(audio.decoder); audio.decoder = NULL;
                }
            }
        }
        audio.fft = vDSP_create_fftsetup(12, kFFTRadix2);
        return (__bridge_retained void *)audio;
    }
}
void fmodel_audio_destroy(void *handle) { if (handle) { FModelAudio *audio = (__bridge_transfer FModelAudio *)handle; [audio.player stop]; } }
int fmodel_audio_play(void *handle) { return [Audio(handle).player play]; }
void fmodel_audio_pause(void *handle) { [Audio(handle).player pause]; }
void fmodel_audio_stop(void *handle) { [Audio(handle).player stop]; Audio(handle).player.currentTime = 0; }
int fmodel_audio_playing(void *handle) { return Audio(handle).player.isPlaying; }
double fmodel_audio_duration(void *handle) { return Audio(handle).player.duration; }
double fmodel_audio_position(void *handle) { return Audio(handle).player.currentTime; }
void fmodel_audio_seek(void *handle, double seconds) { Audio(handle).player.currentTime = seconds; }
void fmodel_audio_volume(void *handle, float volume) { Audio(handle).player.volume = volume; }
void fmodel_audio_loop(void *handle, int loop) { Audio(handle).player.numberOfLoops = loop ? -1 : 0; }
double fmodel_audio_sample_rate(void *handle) { return Audio(handle).format.mSampleRate; }
int fmodel_audio_channels(void *handle) { return Audio(handle).player.numberOfChannels; }
int fmodel_audio_byte_rate(void *handle) {
    AudioStreamBasicDescription format = Audio(handle).format;
    return format.mFramesPerPacket ? (int)(format.mSampleRate * format.mBytesPerPacket / format.mFramesPerPacket) : 0;
}
int fmodel_audio_device(void *handle, const char *uid) {
    @autoreleasepool {
        NSString *device = uid && *uid ? [NSString stringWithUTF8String:uid] : nil;
        if (!device) {
            AudioDeviceID defaultDevice = kAudioObjectUnknown;
            UInt32 size = sizeof(defaultDevice);
            AudioObjectPropertyAddress address = {kAudioHardwarePropertyDefaultOutputDevice, kAudioObjectPropertyScopeGlobal, kAudioObjectPropertyElementMain};
            if (AudioObjectGetPropertyData(kAudioObjectSystemObject, &address, 0, NULL, &size, &defaultDevice) != noErr) return 0;
            CFStringRef deviceUid = NULL;
            size = sizeof(deviceUid);
            AudioObjectPropertyAddress uidAddress = {kAudioDevicePropertyDeviceUID, kAudioObjectPropertyScopeGlobal, kAudioObjectPropertyElementMain};
            if (AudioObjectGetPropertyData(defaultDevice, &uidAddress, 0, NULL, &size, &deviceUid) != noErr || !deviceUid) return 0;
            device = CFBridgingRelease(deviceUid);
        }
        Audio(handle).player.currentDevice = device;
        [Audio(handle).player prepareToPlay];
        return 1;
    }
}
char *fmodel_audio_devices(void) {
    @autoreleasepool {
        NSMutableArray *devices = [NSMutableArray array];
        AudioObjectPropertyAddress address = {kAudioHardwarePropertyDevices, kAudioObjectPropertyScopeGlobal, kAudioObjectPropertyElementMain};
        UInt32 size = 0;
        if (AudioObjectGetPropertyDataSize(kAudioObjectSystemObject, &address, 0, NULL, &size) == noErr) {
            AudioDeviceID *ids = malloc(size);
            if (AudioObjectGetPropertyData(kAudioObjectSystemObject, &address, 0, NULL, &size, ids) == noErr) {
                for (UInt32 index = 0; index < size / sizeof(AudioDeviceID); index++) {
                    AudioObjectPropertyAddress streams = {kAudioDevicePropertyStreamConfiguration, kAudioDevicePropertyScopeOutput, kAudioObjectPropertyElementMain};
                    UInt32 streamSize = 0;
                    if (AudioObjectGetPropertyDataSize(ids[index], &streams, 0, NULL, &streamSize) != noErr || streamSize == 0) continue;
                    AudioBufferList *buffers = malloc(streamSize);
                    UInt32 channels = 0;
                    if (AudioObjectGetPropertyData(ids[index], &streams, 0, NULL, &streamSize, buffers) == noErr)
                        for (UInt32 b = 0; b < buffers->mNumberBuffers; b++) channels += buffers->mBuffers[b].mNumberChannels;
                    free(buffers);
                    if (!channels) continue;
                    CFStringRef name = NULL, uid = NULL;
                    UInt32 stringSize = sizeof(CFStringRef);
                    AudioObjectPropertyAddress nameAddress = {kAudioObjectPropertyName, kAudioObjectPropertyScopeGlobal, kAudioObjectPropertyElementMain};
                    AudioObjectPropertyAddress uidAddress = {kAudioDevicePropertyDeviceUID, kAudioObjectPropertyScopeGlobal, kAudioObjectPropertyElementMain};
                    AudioObjectGetPropertyData(ids[index], &nameAddress, 0, NULL, &stringSize, &name);
                    stringSize = sizeof(CFStringRef);
                    AudioObjectGetPropertyData(ids[index], &uidAddress, 0, NULL, &stringSize, &uid);
                    if (name && uid) [devices addObject:@{@"Name": (__bridge NSString *)name, @"Id": (__bridge NSString *)uid}];
                    if (name) CFRelease(name);
                    if (uid) CFRelease(uid);
                }
            }
            free(ids);
        }
        NSData *json = [NSJSONSerialization dataWithJSONObject:devices options:0 error:nil];
        NSString *text = [[NSString alloc] initWithData:json encoding:NSUTF8StringEncoding];
        return strdup((text ?: @"[]").UTF8String);
    }
}
void fmodel_audio_free(void *pointer) { free(pointer); }
void *fmodel_audio_pcm_wav(void *handle, int *length) {
    @autoreleasepool {
        *length = 0;
        FModelAudio *audio = Audio(handle);
        UInt32 channels = audio.player.numberOfChannels;
        if (!audio.decoder || channels == 0 || channels > 32 || audio.format.mSampleRate <= 0) return NULL;
        AudioStreamBasicDescription format = {0};
        format.mSampleRate = audio.format.mSampleRate;
        format.mFormatID = kAudioFormatLinearPCM;
        format.mFormatFlags = kAudioFormatFlagIsFloat | kAudioFormatFlagIsPacked;
        format.mBytesPerPacket = format.mBytesPerFrame = sizeof(float) * channels;
        format.mFramesPerPacket = 1; format.mChannelsPerFrame = channels; format.mBitsPerChannel = 32;
        if (ExtAudioFileSetProperty(audio.decoder, kExtAudioFileProperty_ClientDataFormat, sizeof(format), &format) != noErr) return NULL;
        ExtAudioFileSeek(audio.decoder, 0);
        NSMutableData *data = [NSMutableData dataWithLength:44];
        float *samples = malloc(4096 * channels * sizeof(float));
        int16_t *pcm = malloc(4096 * channels * sizeof(int16_t));
        OSStatus status = samples && pcm ? noErr : kAudioFileUnspecifiedError;
        while (status == noErr) {
            AudioBufferList buffer = {1, {{channels, 4096 * channels * sizeof(float), samples}}};
            UInt32 frames = 4096;
            status = ExtAudioFileRead(audio.decoder, &frames, &buffer);
            if (status != noErr || frames == 0) break;
            if (data.length + frames * channels * sizeof(int16_t) > INT_MAX) { status = kAudioFileInvalidFileError; break; }
            for (UInt32 index = 0; index < frames * channels; index++) pcm[index] = (int16_t)lrintf(MAX(-1, MIN(1, samples[index])) * 32767);
            [data appendBytes:pcm length:frames * channels * sizeof(int16_t)];
        }
        free(samples); free(pcm);
        // Restore the mono client format used by spectrum analysis.
        format.mChannelsPerFrame = 1; format.mBytesPerFrame = format.mBytesPerPacket = sizeof(float);
        ExtAudioFileSetProperty(audio.decoder, kExtAudioFileProperty_ClientDataFormat, sizeof(format), &format);
        if (status != noErr) return NULL;
        uint8_t *header = data.mutableBytes;
        memcpy(header, "RIFF", 4); memcpy(header + 8, "WAVEfmt ", 8); memcpy(header + 36, "data", 4);
        uint32_t riffSize = (uint32_t)data.length - 8, fmtSize = 16, rate = (uint32_t)format.mSampleRate;
        uint32_t byteRate = rate * channels * 2, dataSize = (uint32_t)data.length - 44;
        uint16_t encoding = 1, channelCount = (uint16_t)channels, alignment = channelCount * 2, bits = 16;
        memcpy(header + 4, &riffSize, 4); memcpy(header + 16, &fmtSize, 4); memcpy(header + 20, &encoding, 2);
        memcpy(header + 22, &channelCount, 2); memcpy(header + 24, &rate, 4); memcpy(header + 28, &byteRate, 4);
        memcpy(header + 32, &alignment, 2); memcpy(header + 34, &bits, 2); memcpy(header + 40, &dataSize, 4);
        void *result = malloc(data.length);
        if (!result) return NULL;
        memcpy(result, data.bytes, data.length); *length = (int)data.length;
        return result;
    }
}
int fmodel_audio_spectrum(void *handle, float *bands, int count) {
    FModelAudio *audio = Audio(handle);
    if (!bands || count <= 0) return 0;
    memset(bands, 0, (size_t)count * sizeof(float));
    if (!audio.decoder || !audio.fft || audio.format.mSampleRate <= 0) return 0;
    if (ExtAudioFileSeek(audio.decoder, (SInt64)(audio.player.currentTime * audio.format.mSampleRate)) != noErr) return 0;
    float samples[4096] = {0}, window[4096], real[2048], imaginary[2048], power[2048];
    AudioBufferList buffer = {1, {{1, sizeof(samples), samples}}};
    UInt32 frames = 4096;
    if (ExtAudioFileRead(audio.decoder, &frames, &buffer) != noErr || !frames) return 0;
    vDSP_hann_window(window, 4096, vDSP_HANN_NORM);
    vDSP_vmul(samples, 1, window, 1, samples, 1, 4096);
    DSPSplitComplex split = {real, imaginary};
    vDSP_ctoz((DSPComplex *)samples, 2, &split, 1, 2048);
    vDSP_fft_zrip(audio.fft, &split, 1, 12, FFT_FORWARD);
    imaginary[0] = 0;
    vDSP_zvmags(&split, 1, power, 1, 2048);
    for (int band = 0; band < count; band++) {
        int start = MAX(1, (int)pow(2048.0, (double)band / count));
        int end = MIN(2048, MAX(start + 1, (int)pow(2048.0, (double)(band + 1) / count)));
        float peak = 0;
        for (int bin = start; bin < end; bin++) peak = MAX(peak, power[bin]);
        double db = 10 * log10(MAX(peak / (4096.0 * 4096.0), 1e-10));
        bands[band] = (float)MAX(0, MIN(1, (db + 80) / 80));
    }
    return 1;
}

int fmodel_copy_png(const void *bytes, int length) {
    @autoreleasepool {
        if (!bytes || length <= 0) return 0;
        NSData *data = [NSData dataWithBytes:bytes length:(NSUInteger)length];
        NSPasteboard *pasteboard = [NSPasteboard generalPasteboard];
        [pasteboard clearContents];
        return [pasteboard setData:data forType:NSPasteboardTypePNG];
    }
}
