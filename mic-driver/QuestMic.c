// SPDX-License-Identifier: MIT
//
// Link para Mac: "Quest 3 Microphone" — a macOS audio device (AudioServerPlugIn, loaded by coreaudiod) whose
// input is the Quest 3 microphone, like Quest Link's "Headset Microphone" on Windows.
//
// The OXRSys runtime (inside Unity) receives the headset microphone over the Link and sends it here as UDP
// datagrams on 127.0.0.1:9950 (MicDatagram + mono PCM s16 at 48 kHz). This driver keeps a small jitter
// buffer (target 40 ms, trimmed above 150 ms for clock drift) and hands it to Core Audio as a 48 kHz mono
// float input stream. Without the headset it records silence.
//
// Written from Apple's AudioServerPlugIn documentation (the NullAudio sample's structure): one plug-in,
// one device, one input stream, no controls.

#include <CoreAudio/AudioServerPlugIn.h>
#include <CoreFoundation/CoreFoundation.h>
#include <arpa/inet.h>
#include <mach/mach_time.h>
#include <netinet/in.h>
#include <pthread.h>
#include <stdatomic.h>
#include <string.h>
#include <sys/socket.h>
#include <unistd.h>

#define kDeviceUID          "LinkParaMac_QuestMic_UID"
#define kDeviceModelUID     "LinkParaMac_QuestMic_Model"
#define kDeviceName         "Quest 3 Microphone (Link para Mac)"
#define kManufacturer       "Link para Mac"
#define kSampleRate         48000.0
#define kZeroTimeStampPeriod 16384
#define kUdpPort            9950
#define kDatagramMagic      0x4d51584fu // "OXQM", see common/CameraProtocol.h (MicDatagram)

enum
{
    kObjectID_PlugIn = kAudioObjectPlugInObject,
    kObjectID_Device = 2,
    kObjectID_Stream = 3,
};

// ─── jitter buffer (single producer: UDP thread; single consumer: IO thread) ─────────────────────────────
#define kFifoSize 131072 // power of two, ~2.7 s
static float gFifo[kFifoSize];
static _Atomic uint64_t gWrite = 0;
static _Atomic uint64_t gRead = 0;
static _Atomic int gPrimed = 0;
static const uint64_t kTargetFrames = 1920; // 40 ms
static const uint64_t kMaxFrames = 7200;    // 150 ms

typedef struct
{
    uint32_t magic;
    uint32_t sampleRate;
    uint32_t frames;
    uint32_t sequence;
} MicDatagram;

static void* UdpThread(void* unused)
{
    (void)unused;
    int fd = socket(AF_INET, SOCK_DGRAM, 0);
    if (fd < 0) return NULL;
    int reuse = 1;
    setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &reuse, sizeof(reuse));
    struct sockaddr_in addr;
    memset(&addr, 0, sizeof(addr));
    addr.sin_family = AF_INET;
    addr.sin_port = htons(kUdpPort);
    addr.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    if (bind(fd, (struct sockaddr*)&addr, sizeof(addr)) != 0) { close(fd); return NULL; }
    static uint8_t buffer[65536];
    for (;;)
    {
        ssize_t n = recv(fd, buffer, sizeof(buffer), 0);
        if (n < (ssize_t)sizeof(MicDatagram)) continue;
        MicDatagram header;
        memcpy(&header, buffer, sizeof(header));
        if (header.magic != kDatagramMagic || header.frames == 0) continue;
        if ((size_t)n < sizeof(header) + header.frames * sizeof(int16_t)) continue;
        if (header.sampleRate != (uint32_t)kSampleRate) continue; // the client captures at 48 kHz
        const int16_t* pcm = (const int16_t*)(buffer + sizeof(header));
        uint64_t w = atomic_load(&gWrite);
        const uint64_t r = atomic_load(&gRead);
        for (uint32_t i = 0; i < header.frames; ++i)
        {
            if (w - r >= kFifoSize - 1) break; // full: drop the rest (the consumer trims anyway)
            int16_t s;
            memcpy(&s, pcm + i, sizeof(s));
            gFifo[w & (kFifoSize - 1)] = s / 32768.0f;
            ++w;
        }
        atomic_store(&gWrite, w);
    }
    return NULL;
}

static void ReadFifo(float* out, UInt32 frames)
{
    const uint64_t w = atomic_load(&gWrite);
    uint64_t r = atomic_load(&gRead);
    uint64_t available = w - r;
    if (available > kMaxFrames) // clock drift / a burst after a stall: back to the target latency
    {
        r = w - kTargetFrames;
        available = kTargetFrames;
    }
    if (!atomic_load(&gPrimed))
    {
        if (available < kTargetFrames)
        {
            memset(out, 0, frames * sizeof(float));
            atomic_store(&gRead, r);
            return;
        }
        atomic_store(&gPrimed, 1);
    }
    UInt32 take = available < frames ? (UInt32)available : frames;
    for (UInt32 i = 0; i < take; ++i) out[i] = gFifo[(r + i) & (kFifoSize - 1)];
    if (take < frames)
    {
        memset(out + take, 0, (frames - take) * sizeof(float));
        atomic_store(&gPrimed, 0); // refill before resuming
    }
    atomic_store(&gRead, r + take);
}

// ─── state ───────────────────────────────────────────────────────────────────────────────────────────────
static pthread_mutex_t gStateMutex = PTHREAD_MUTEX_INITIALIZER;
static AudioServerPlugInHostRef gHost = NULL;
static UInt32 gRefCount = 0;
static UInt32 gIOCount = 0;
static Float64 gHostTicksPerFrame = 0.0;
static UInt64 gAnchorHostTime = 0;
static UInt64 gNumberTimeStamps = 0;
static Boolean gStreamActive = true;

static void FillFormat(AudioStreamBasicDescription* f)
{
    f->mSampleRate = kSampleRate;
    f->mFormatID = kAudioFormatLinearPCM;
    f->mFormatFlags = kAudioFormatFlagsNativeFloatPacked;
    f->mBytesPerPacket = 4;
    f->mFramesPerPacket = 1;
    f->mBytesPerFrame = 4;
    f->mChannelsPerFrame = 1;
    f->mBitsPerChannel = 32;
    f->mReserved = 0;
}

// ─── COM plumbing ────────────────────────────────────────────────────────────────────────────────────────
static HRESULT QM_QueryInterface(void* inDriver, REFIID inUUID, LPVOID* outInterface);
static ULONG QM_AddRef(void* inDriver);
static ULONG QM_Release(void* inDriver);
static OSStatus QM_Initialize(AudioServerPlugInDriverRef inDriver, AudioServerPlugInHostRef inHost);
static OSStatus QM_CreateDevice(AudioServerPlugInDriverRef, CFDictionaryRef, const AudioServerPlugInClientInfo*, AudioObjectID*);
static OSStatus QM_DestroyDevice(AudioServerPlugInDriverRef, AudioObjectID);
static OSStatus QM_AddDeviceClient(AudioServerPlugInDriverRef, AudioObjectID, const AudioServerPlugInClientInfo*);
static OSStatus QM_RemoveDeviceClient(AudioServerPlugInDriverRef, AudioObjectID, const AudioServerPlugInClientInfo*);
static OSStatus QM_PerformDeviceConfigurationChange(AudioServerPlugInDriverRef, AudioObjectID, UInt64, void*);
static OSStatus QM_AbortDeviceConfigurationChange(AudioServerPlugInDriverRef, AudioObjectID, UInt64, void*);
static Boolean QM_HasProperty(AudioServerPlugInDriverRef, AudioObjectID, pid_t, const AudioObjectPropertyAddress*);
static OSStatus QM_IsPropertySettable(AudioServerPlugInDriverRef, AudioObjectID, pid_t, const AudioObjectPropertyAddress*, Boolean*);
static OSStatus QM_GetPropertyDataSize(AudioServerPlugInDriverRef, AudioObjectID, pid_t, const AudioObjectPropertyAddress*, UInt32, const void*, UInt32*);
static OSStatus QM_GetPropertyData(AudioServerPlugInDriverRef, AudioObjectID, pid_t, const AudioObjectPropertyAddress*, UInt32, const void*, UInt32, UInt32*, void*);
static OSStatus QM_SetPropertyData(AudioServerPlugInDriverRef, AudioObjectID, pid_t, const AudioObjectPropertyAddress*, UInt32, const void*, UInt32, const void*);
static OSStatus QM_StartIO(AudioServerPlugInDriverRef, AudioObjectID, UInt32);
static OSStatus QM_StopIO(AudioServerPlugInDriverRef, AudioObjectID, UInt32);
static OSStatus QM_GetZeroTimeStamp(AudioServerPlugInDriverRef, AudioObjectID, UInt32, Float64*, UInt64*, UInt64*);
static OSStatus QM_WillDoIOOperation(AudioServerPlugInDriverRef, AudioObjectID, UInt32, UInt32, Boolean*, Boolean*);
static OSStatus QM_BeginIOOperation(AudioServerPlugInDriverRef, AudioObjectID, UInt32, UInt32, UInt32, const AudioServerPlugInIOCycleInfo*);
static OSStatus QM_DoIOOperation(AudioServerPlugInDriverRef, AudioObjectID, AudioObjectID, UInt32, UInt32, UInt32, const AudioServerPlugInIOCycleInfo*, void*, void*);
static OSStatus QM_EndIOOperation(AudioServerPlugInDriverRef, AudioObjectID, UInt32, UInt32, UInt32, const AudioServerPlugInIOCycleInfo*);

static AudioServerPlugInDriverInterface gInterface = {
    NULL,
    QM_QueryInterface, QM_AddRef, QM_Release,
    QM_Initialize, QM_CreateDevice, QM_DestroyDevice,
    QM_AddDeviceClient, QM_RemoveDeviceClient,
    QM_PerformDeviceConfigurationChange, QM_AbortDeviceConfigurationChange,
    QM_HasProperty, QM_IsPropertySettable, QM_GetPropertyDataSize, QM_GetPropertyData, QM_SetPropertyData,
    QM_StartIO, QM_StopIO, QM_GetZeroTimeStamp,
    QM_WillDoIOOperation, QM_BeginIOOperation, QM_DoIOOperation, QM_EndIOOperation,
};
static AudioServerPlugInDriverInterface* gInterfacePtr = &gInterface;
static AudioServerPlugInDriverRef gDriverRef = &gInterfacePtr;

void* QuestMic_Create(CFAllocatorRef inAllocator, CFUUIDRef inRequestedTypeUUID)
{
    (void)inAllocator;
    if (CFEqual(inRequestedTypeUUID, kAudioServerPlugInTypeUUID)) return gDriverRef;
    return NULL;
}

static HRESULT QM_QueryInterface(void* inDriver, REFIID inUUID, LPVOID* outInterface)
{
    if (inDriver != gDriverRef || outInterface == NULL) return kAudioHardwareBadObjectError;
    CFUUIDRef requested = CFUUIDCreateFromUUIDBytes(NULL, inUUID);
    HRESULT result = E_NOINTERFACE;
    if (CFEqual(requested, IUnknownUUID) || CFEqual(requested, kAudioServerPlugInDriverInterfaceUUID))
    {
        pthread_mutex_lock(&gStateMutex);
        ++gRefCount;
        pthread_mutex_unlock(&gStateMutex);
        *outInterface = gDriverRef;
        result = S_OK;
    }
    CFRelease(requested);
    return result;
}

static ULONG QM_AddRef(void* inDriver)
{
    if (inDriver != gDriverRef) return 0;
    pthread_mutex_lock(&gStateMutex);
    ULONG count = ++gRefCount;
    pthread_mutex_unlock(&gStateMutex);
    return count;
}

static ULONG QM_Release(void* inDriver)
{
    if (inDriver != gDriverRef) return 0;
    pthread_mutex_lock(&gStateMutex);
    if (gRefCount > 0) --gRefCount;
    ULONG count = gRefCount;
    pthread_mutex_unlock(&gStateMutex);
    return count;
}

static OSStatus QM_Initialize(AudioServerPlugInDriverRef inDriver, AudioServerPlugInHostRef inHost)
{
    if (inDriver != gDriverRef) return kAudioHardwareBadObjectError;
    gHost = inHost;
    struct mach_timebase_info timebase;
    mach_timebase_info(&timebase);
    Float64 hostClockFrequency = (Float64)timebase.denom / (Float64)timebase.numer * 1000000000.0;
    gHostTicksPerFrame = hostClockFrequency / kSampleRate;
    pthread_t thread;
    if (pthread_create(&thread, NULL, UdpThread, NULL) == 0) pthread_detach(thread);
    return noErr;
}

static OSStatus QM_CreateDevice(AudioServerPlugInDriverRef d, CFDictionaryRef desc, const AudioServerPlugInClientInfo* info, AudioObjectID* out)
{
    (void)d; (void)desc; (void)info; (void)out;
    return kAudioHardwareUnsupportedOperationError;
}

static OSStatus QM_DestroyDevice(AudioServerPlugInDriverRef d, AudioObjectID id)
{
    (void)d; (void)id;
    return kAudioHardwareUnsupportedOperationError;
}

static OSStatus QM_AddDeviceClient(AudioServerPlugInDriverRef d, AudioObjectID id, const AudioServerPlugInClientInfo* info)
{
    (void)info;
    return d == gDriverRef && id == kObjectID_Device ? noErr : kAudioHardwareBadObjectError;
}

static OSStatus QM_RemoveDeviceClient(AudioServerPlugInDriverRef d, AudioObjectID id, const AudioServerPlugInClientInfo* info)
{
    (void)info;
    return d == gDriverRef && id == kObjectID_Device ? noErr : kAudioHardwareBadObjectError;
}

static OSStatus QM_PerformDeviceConfigurationChange(AudioServerPlugInDriverRef d, AudioObjectID id, UInt64 action, void* info)
{
    (void)d; (void)id; (void)action; (void)info;
    return noErr;
}

static OSStatus QM_AbortDeviceConfigurationChange(AudioServerPlugInDriverRef d, AudioObjectID id, UInt64 action, void* info)
{
    (void)d; (void)id; (void)action; (void)info;
    return noErr;
}

// ─── properties ──────────────────────────────────────────────────────────────────────────────────────────
static Boolean PlugInHas(AudioObjectPropertySelector s)
{
    switch (s)
    {
        case kAudioObjectPropertyBaseClass: case kAudioObjectPropertyClass: case kAudioObjectPropertyOwner:
        case kAudioObjectPropertyManufacturer: case kAudioObjectPropertyOwnedObjects:
        case kAudioPlugInPropertyDeviceList: case kAudioPlugInPropertyTranslateUIDToDevice:
        case kAudioPlugInPropertyResourceBundle:
            return true;
    }
    return false;
}

static Boolean DeviceHas(AudioObjectPropertySelector s)
{
    switch (s)
    {
        case kAudioObjectPropertyBaseClass: case kAudioObjectPropertyClass: case kAudioObjectPropertyOwner:
        case kAudioObjectPropertyName: case kAudioObjectPropertyManufacturer: case kAudioObjectPropertyOwnedObjects:
        case kAudioObjectPropertyControlList:
        case kAudioDevicePropertyDeviceUID: case kAudioDevicePropertyModelUID: case kAudioDevicePropertyTransportType:
        case kAudioDevicePropertyRelatedDevices: case kAudioDevicePropertyClockDomain: case kAudioDevicePropertyDeviceIsAlive:
        case kAudioDevicePropertyDeviceIsRunning: case kAudioDevicePropertyDeviceCanBeDefaultDevice:
        case kAudioDevicePropertyDeviceCanBeDefaultSystemDevice: case kAudioDevicePropertyLatency:
        case kAudioDevicePropertyStreams: case kAudioDevicePropertySafetyOffset: case kAudioDevicePropertyNominalSampleRate:
        case kAudioDevicePropertyAvailableNominalSampleRates: case kAudioDevicePropertyIsHidden:
        case kAudioDevicePropertyPreferredChannelsForStereo: case kAudioDevicePropertyZeroTimeStampPeriod:
            return true;
    }
    return false;
}

static Boolean StreamHas(AudioObjectPropertySelector s)
{
    switch (s)
    {
        case kAudioObjectPropertyBaseClass: case kAudioObjectPropertyClass: case kAudioObjectPropertyOwner:
        case kAudioObjectPropertyName: case kAudioObjectPropertyOwnedObjects:
        case kAudioStreamPropertyIsActive: case kAudioStreamPropertyDirection: case kAudioStreamPropertyTerminalType:
        case kAudioStreamPropertyStartingChannel: case kAudioStreamPropertyLatency:
        case kAudioStreamPropertyVirtualFormat: case kAudioStreamPropertyPhysicalFormat:
        case kAudioStreamPropertyAvailableVirtualFormats: case kAudioStreamPropertyAvailablePhysicalFormats:
            return true;
    }
    return false;
}

static Boolean QM_HasProperty(AudioServerPlugInDriverRef d, AudioObjectID id, pid_t pid, const AudioObjectPropertyAddress* a)
{
    (void)pid;
    if (d != gDriverRef || a == NULL) return false;
    if (id == kObjectID_PlugIn) return PlugInHas(a->mSelector);
    if (id == kObjectID_Device) return DeviceHas(a->mSelector);
    if (id == kObjectID_Stream) return StreamHas(a->mSelector);
    return false;
}

static OSStatus QM_IsPropertySettable(AudioServerPlugInDriverRef d, AudioObjectID id, pid_t pid, const AudioObjectPropertyAddress* a, Boolean* out)
{
    if (!QM_HasProperty(d, id, pid, a) || out == NULL) return kAudioHardwareUnknownPropertyError;
    *out = false;
    if (id == kObjectID_Device && a->mSelector == kAudioDevicePropertyNominalSampleRate) *out = true;
    if (id == kObjectID_Stream && (a->mSelector == kAudioStreamPropertyIsActive || a->mSelector == kAudioStreamPropertyVirtualFormat ||
                                  a->mSelector == kAudioStreamPropertyPhysicalFormat))
        *out = true;
    return noErr;
}

static Boolean OutputScope(const AudioObjectPropertyAddress* a)
{
    return a->mScope == kAudioObjectPropertyScopeOutput;
}

static OSStatus QM_GetPropertyDataSize(AudioServerPlugInDriverRef d, AudioObjectID id, pid_t pid, const AudioObjectPropertyAddress* a,
                                       UInt32 qs, const void* q, UInt32* out)
{
    (void)qs; (void)q;
    if (!QM_HasProperty(d, id, pid, a) || out == NULL) return kAudioHardwareUnknownPropertyError;
    switch (a->mSelector)
    {
        case kAudioObjectPropertyBaseClass: case kAudioObjectPropertyClass: *out = sizeof(AudioClassID); return noErr;
        case kAudioObjectPropertyOwner: *out = sizeof(AudioObjectID); return noErr;
        case kAudioObjectPropertyName: case kAudioObjectPropertyManufacturer: case kAudioDevicePropertyDeviceUID:
        case kAudioDevicePropertyModelUID: case kAudioPlugInPropertyResourceBundle:
            *out = sizeof(CFStringRef); return noErr;
        case kAudioObjectPropertyOwnedObjects:
            if (id == kObjectID_PlugIn) *out = sizeof(AudioObjectID);
            else if (id == kObjectID_Device) *out = OutputScope(a) ? 0 : sizeof(AudioObjectID);
            else *out = 0;
            return noErr;
        case kAudioPlugInPropertyDeviceList: *out = sizeof(AudioObjectID); return noErr;
        case kAudioPlugInPropertyTranslateUIDToDevice: *out = sizeof(AudioObjectID); return noErr;
        case kAudioObjectPropertyControlList: *out = 0; return noErr;
        case kAudioDevicePropertyRelatedDevices: *out = sizeof(AudioObjectID); return noErr;
        case kAudioDevicePropertyStreams: *out = OutputScope(a) ? 0 : sizeof(AudioObjectID); return noErr;
        case kAudioDevicePropertyNominalSampleRate: *out = sizeof(Float64); return noErr;
        case kAudioDevicePropertyAvailableNominalSampleRates: *out = sizeof(AudioValueRange); return noErr;
        case kAudioDevicePropertyPreferredChannelsForStereo: *out = 2 * sizeof(UInt32); return noErr;
        case kAudioStreamPropertyVirtualFormat: case kAudioStreamPropertyPhysicalFormat:
            *out = sizeof(AudioStreamBasicDescription); return noErr;
        case kAudioStreamPropertyAvailableVirtualFormats: case kAudioStreamPropertyAvailablePhysicalFormats:
            *out = sizeof(AudioStreamRangedDescription); return noErr;
        default: *out = sizeof(UInt32); return noErr;
    }
}

#define REQUIRE_SIZE(type) do { if (inDataSize < sizeof(type)) return kAudioHardwareBadPropertySizeError; *outDataSize = sizeof(type); } while (0)

static OSStatus QM_GetPropertyData(AudioServerPlugInDriverRef d, AudioObjectID id, pid_t pid, const AudioObjectPropertyAddress* a,
                                   UInt32 qs, const void* q, UInt32 inDataSize, UInt32* outDataSize, void* outData)
{
    if (!QM_HasProperty(d, id, pid, a) || outDataSize == NULL || outData == NULL) return kAudioHardwareUnknownPropertyError;
    const AudioObjectPropertySelector s = a->mSelector;

    if (id == kObjectID_PlugIn)
    {
        switch (s)
        {
            case kAudioObjectPropertyBaseClass: REQUIRE_SIZE(AudioClassID); *(AudioClassID*)outData = kAudioObjectClassID; return noErr;
            case kAudioObjectPropertyClass: REQUIRE_SIZE(AudioClassID); *(AudioClassID*)outData = kAudioPlugInClassID; return noErr;
            case kAudioObjectPropertyOwner: REQUIRE_SIZE(AudioObjectID); *(AudioObjectID*)outData = kAudioObjectUnknown; return noErr;
            case kAudioObjectPropertyManufacturer: REQUIRE_SIZE(CFStringRef); *(CFStringRef*)outData = CFSTR(kManufacturer); return noErr;
            case kAudioObjectPropertyOwnedObjects: case kAudioPlugInPropertyDeviceList:
                *outDataSize = inDataSize >= sizeof(AudioObjectID) ? sizeof(AudioObjectID) : 0;
                if (*outDataSize) *(AudioObjectID*)outData = kObjectID_Device;
                return noErr;
            case kAudioPlugInPropertyTranslateUIDToDevice:
            {
                REQUIRE_SIZE(AudioObjectID);
                AudioObjectID result = kAudioObjectUnknown;
                if (qs == sizeof(CFStringRef) && q != NULL)
                {
                    CFStringRef uid = *(const CFStringRef*)q;
                    if (uid != NULL && CFStringCompare(uid, CFSTR(kDeviceUID), 0) == kCFCompareEqualTo) result = kObjectID_Device;
                }
                *(AudioObjectID*)outData = result;
                return noErr;
            }
            case kAudioPlugInPropertyResourceBundle: REQUIRE_SIZE(CFStringRef); *(CFStringRef*)outData = CFSTR(""); return noErr;
        }
        return kAudioHardwareUnknownPropertyError;
    }

    if (id == kObjectID_Device)
    {
        switch (s)
        {
            case kAudioObjectPropertyBaseClass: REQUIRE_SIZE(AudioClassID); *(AudioClassID*)outData = kAudioObjectClassID; return noErr;
            case kAudioObjectPropertyClass: REQUIRE_SIZE(AudioClassID); *(AudioClassID*)outData = kAudioDeviceClassID; return noErr;
            case kAudioObjectPropertyOwner: REQUIRE_SIZE(AudioObjectID); *(AudioObjectID*)outData = kObjectID_PlugIn; return noErr;
            case kAudioObjectPropertyName: REQUIRE_SIZE(CFStringRef); *(CFStringRef*)outData = CFSTR(kDeviceName); return noErr;
            case kAudioObjectPropertyManufacturer: REQUIRE_SIZE(CFStringRef); *(CFStringRef*)outData = CFSTR(kManufacturer); return noErr;
            case kAudioDevicePropertyDeviceUID: REQUIRE_SIZE(CFStringRef); *(CFStringRef*)outData = CFSTR(kDeviceUID); return noErr;
            case kAudioDevicePropertyModelUID: REQUIRE_SIZE(CFStringRef); *(CFStringRef*)outData = CFSTR(kDeviceModelUID); return noErr;
            case kAudioObjectPropertyOwnedObjects: case kAudioDevicePropertyStreams:
                if (OutputScope(a) || inDataSize < sizeof(AudioObjectID)) { *outDataSize = 0; return noErr; }
                *outDataSize = sizeof(AudioObjectID);
                *(AudioObjectID*)outData = kObjectID_Stream;
                return noErr;
            case kAudioObjectPropertyControlList: *outDataSize = 0; return noErr;
            case kAudioDevicePropertyTransportType: REQUIRE_SIZE(UInt32); *(UInt32*)outData = kAudioDeviceTransportTypeVirtual; return noErr;
            case kAudioDevicePropertyRelatedDevices: REQUIRE_SIZE(AudioObjectID); *(AudioObjectID*)outData = kObjectID_Device; return noErr;
            case kAudioDevicePropertyClockDomain: REQUIRE_SIZE(UInt32); *(UInt32*)outData = 0; return noErr;
            case kAudioDevicePropertyDeviceIsAlive: REQUIRE_SIZE(UInt32); *(UInt32*)outData = 1; return noErr;
            case kAudioDevicePropertyDeviceIsRunning:
                REQUIRE_SIZE(UInt32);
                pthread_mutex_lock(&gStateMutex);
                *(UInt32*)outData = gIOCount > 0 ? 1 : 0;
                pthread_mutex_unlock(&gStateMutex);
                return noErr;
            case kAudioDevicePropertyDeviceCanBeDefaultDevice:
                REQUIRE_SIZE(UInt32); *(UInt32*)outData = OutputScope(a) ? 0 : 1; return noErr;
            case kAudioDevicePropertyDeviceCanBeDefaultSystemDevice: REQUIRE_SIZE(UInt32); *(UInt32*)outData = 0; return noErr;
            case kAudioDevicePropertyLatency: case kAudioDevicePropertySafetyOffset:
                REQUIRE_SIZE(UInt32); *(UInt32*)outData = 0; return noErr;
            case kAudioDevicePropertyNominalSampleRate: REQUIRE_SIZE(Float64); *(Float64*)outData = kSampleRate; return noErr;
            case kAudioDevicePropertyAvailableNominalSampleRates:
            {
                REQUIRE_SIZE(AudioValueRange);
                AudioValueRange range = {kSampleRate, kSampleRate};
                *(AudioValueRange*)outData = range;
                return noErr;
            }
            case kAudioDevicePropertyIsHidden: REQUIRE_SIZE(UInt32); *(UInt32*)outData = 0; return noErr;
            case kAudioDevicePropertyPreferredChannelsForStereo:
                if (inDataSize < 2 * sizeof(UInt32)) return kAudioHardwareBadPropertySizeError;
                ((UInt32*)outData)[0] = 1; ((UInt32*)outData)[1] = 1; *outDataSize = 2 * sizeof(UInt32);
                return noErr;
            case kAudioDevicePropertyZeroTimeStampPeriod: REQUIRE_SIZE(UInt32); *(UInt32*)outData = kZeroTimeStampPeriod; return noErr;
        }
        return kAudioHardwareUnknownPropertyError;
    }

    if (id == kObjectID_Stream)
    {
        switch (s)
        {
            case kAudioObjectPropertyBaseClass: REQUIRE_SIZE(AudioClassID); *(AudioClassID*)outData = kAudioObjectClassID; return noErr;
            case kAudioObjectPropertyClass: REQUIRE_SIZE(AudioClassID); *(AudioClassID*)outData = kAudioStreamClassID; return noErr;
            case kAudioObjectPropertyOwner: REQUIRE_SIZE(AudioObjectID); *(AudioObjectID*)outData = kObjectID_Device; return noErr;
            case kAudioObjectPropertyName: REQUIRE_SIZE(CFStringRef); *(CFStringRef*)outData = CFSTR("Quest 3 Microphone"); return noErr;
            case kAudioObjectPropertyOwnedObjects: *outDataSize = 0; return noErr;
            case kAudioStreamPropertyIsActive:
                REQUIRE_SIZE(UInt32);
                pthread_mutex_lock(&gStateMutex);
                *(UInt32*)outData = gStreamActive ? 1 : 0;
                pthread_mutex_unlock(&gStateMutex);
                return noErr;
            case kAudioStreamPropertyDirection: REQUIRE_SIZE(UInt32); *(UInt32*)outData = 1; return noErr; // input
            case kAudioStreamPropertyTerminalType: REQUIRE_SIZE(UInt32); *(UInt32*)outData = kAudioStreamTerminalTypeMicrophone; return noErr;
            case kAudioStreamPropertyStartingChannel: REQUIRE_SIZE(UInt32); *(UInt32*)outData = 1; return noErr;
            case kAudioStreamPropertyLatency: REQUIRE_SIZE(UInt32); *(UInt32*)outData = 0; return noErr;
            case kAudioStreamPropertyVirtualFormat: case kAudioStreamPropertyPhysicalFormat:
                REQUIRE_SIZE(AudioStreamBasicDescription);
                FillFormat((AudioStreamBasicDescription*)outData);
                return noErr;
            case kAudioStreamPropertyAvailableVirtualFormats: case kAudioStreamPropertyAvailablePhysicalFormats:
            {
                if (inDataSize < sizeof(AudioStreamRangedDescription)) { *outDataSize = 0; return noErr; }
                AudioStreamRangedDescription* r = (AudioStreamRangedDescription*)outData;
                FillFormat(&r->mFormat);
                r->mSampleRateRange.mMinimum = kSampleRate;
                r->mSampleRateRange.mMaximum = kSampleRate;
                *outDataSize = sizeof(AudioStreamRangedDescription);
                return noErr;
            }
        }
        return kAudioHardwareUnknownPropertyError;
    }
    return kAudioHardwareBadObjectError;
}

static OSStatus QM_SetPropertyData(AudioServerPlugInDriverRef d, AudioObjectID id, pid_t pid, const AudioObjectPropertyAddress* a,
                                   UInt32 qs, const void* q, UInt32 inDataSize, const void* inData)
{
    (void)qs; (void)q;
    if (!QM_HasProperty(d, id, pid, a) || inData == NULL) return kAudioHardwareUnknownPropertyError;
    if (id == kObjectID_Device && a->mSelector == kAudioDevicePropertyNominalSampleRate)
    {
        if (inDataSize < sizeof(Float64)) return kAudioHardwareBadPropertySizeError;
        return *(const Float64*)inData == kSampleRate ? noErr : kAudioHardwareIllegalOperationError;
    }
    if (id == kObjectID_Stream && a->mSelector == kAudioStreamPropertyIsActive)
    {
        if (inDataSize < sizeof(UInt32)) return kAudioHardwareBadPropertySizeError;
        pthread_mutex_lock(&gStateMutex);
        gStreamActive = *(const UInt32*)inData != 0;
        pthread_mutex_unlock(&gStateMutex);
        return noErr;
    }
    if (id == kObjectID_Stream && (a->mSelector == kAudioStreamPropertyVirtualFormat || a->mSelector == kAudioStreamPropertyPhysicalFormat))
    {
        if (inDataSize < sizeof(AudioStreamBasicDescription)) return kAudioHardwareBadPropertySizeError;
        const AudioStreamBasicDescription* f = (const AudioStreamBasicDescription*)inData;
        return f->mSampleRate == kSampleRate && f->mChannelsPerFrame == 1 && f->mFormatID == kAudioFormatLinearPCM
                   ? noErr : kAudioDeviceUnsupportedFormatError;
    }
    return kAudioHardwareUnsupportedOperationError;
}

// ─── IO ──────────────────────────────────────────────────────────────────────────────────────────────────
static OSStatus QM_StartIO(AudioServerPlugInDriverRef d, AudioObjectID id, UInt32 client)
{
    (void)client;
    if (d != gDriverRef || id != kObjectID_Device) return kAudioHardwareBadObjectError;
    pthread_mutex_lock(&gStateMutex);
    if (gIOCount++ == 0)
    {
        gNumberTimeStamps = 0;
        gAnchorHostTime = mach_absolute_time();
        atomic_store(&gPrimed, 0);
        atomic_store(&gRead, atomic_load(&gWrite)); // start from fresh audio
    }
    pthread_mutex_unlock(&gStateMutex);
    return noErr;
}

static OSStatus QM_StopIO(AudioServerPlugInDriverRef d, AudioObjectID id, UInt32 client)
{
    (void)client;
    if (d != gDriverRef || id != kObjectID_Device) return kAudioHardwareBadObjectError;
    pthread_mutex_lock(&gStateMutex);
    if (gIOCount > 0) --gIOCount;
    pthread_mutex_unlock(&gStateMutex);
    return noErr;
}

static OSStatus QM_GetZeroTimeStamp(AudioServerPlugInDriverRef d, AudioObjectID id, UInt32 client, Float64* outSampleTime,
                                    UInt64* outHostTime, UInt64* outSeed)
{
    (void)client;
    if (d != gDriverRef || id != kObjectID_Device) return kAudioHardwareBadObjectError;
    const UInt64 now = mach_absolute_time();
    const Float64 ticksPerPeriod = gHostTicksPerFrame * kZeroTimeStampPeriod;
    const UInt64 next = gAnchorHostTime + (UInt64)((gNumberTimeStamps + 1) * ticksPerPeriod);
    if (next <= now) ++gNumberTimeStamps;
    *outSampleTime = (Float64)(gNumberTimeStamps * kZeroTimeStampPeriod);
    *outHostTime = gAnchorHostTime + (UInt64)(gNumberTimeStamps * ticksPerPeriod);
    *outSeed = 1;
    return noErr;
}

static OSStatus QM_WillDoIOOperation(AudioServerPlugInDriverRef d, AudioObjectID id, UInt32 client, UInt32 op, Boolean* willDo, Boolean* inPlace)
{
    (void)client;
    if (d != gDriverRef || id != kObjectID_Device) return kAudioHardwareBadObjectError;
    const Boolean read = op == kAudioServerPlugInIOOperationReadInput;
    if (willDo) *willDo = read;
    if (inPlace) *inPlace = true;
    return noErr;
}

static OSStatus QM_BeginIOOperation(AudioServerPlugInDriverRef d, AudioObjectID id, UInt32 client, UInt32 op, UInt32 frames,
                                    const AudioServerPlugInIOCycleInfo* info)
{
    (void)client; (void)op; (void)frames; (void)info;
    return d == gDriverRef && id == kObjectID_Device ? noErr : kAudioHardwareBadObjectError;
}

static OSStatus QM_DoIOOperation(AudioServerPlugInDriverRef d, AudioObjectID id, AudioObjectID stream, UInt32 client, UInt32 op,
                                 UInt32 frames, const AudioServerPlugInIOCycleInfo* info, void* mainBuffer, void* secondary)
{
    (void)client; (void)info; (void)secondary;
    if (d != gDriverRef || id != kObjectID_Device || stream != kObjectID_Stream) return kAudioHardwareBadObjectError;
    if (op == kAudioServerPlugInIOOperationReadInput && mainBuffer != NULL) ReadFifo((float*)mainBuffer, frames);
    return noErr;
}

static OSStatus QM_EndIOOperation(AudioServerPlugInDriverRef d, AudioObjectID id, UInt32 client, UInt32 op, UInt32 frames,
                                  const AudioServerPlugInIOCycleInfo* info)
{
    (void)client; (void)op; (void)frames; (void)info;
    return d == gDriverRef && id == kObjectID_Device ? noErr : kAudioHardwareBadObjectError;
}
