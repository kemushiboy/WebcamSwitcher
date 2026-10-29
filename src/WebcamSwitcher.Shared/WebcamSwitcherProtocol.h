#pragma once
//
// WebcamSwitcher frame-transport protocol (named pipe).
//
//   Pipe name : \\.\pipe\WebcamSwitcher.Frames.v1
//   Server    : WebcamSwitcher.App (runs in the interactive user session)
//   Client    : WebcamSwitcher.Source.dll (loaded by the Windows Frame Server,
//               which runs in session 0 as LocalService/LocalSystem)
//
// The pipe is a byte stream. Every message is:
//     [WsMsgHeader][payload]
// The client must read exactly sizeof(WsMsgHeader), then `payloadLen` bytes.
//
#include <stdint.h>

#define WS_PIPE_NAME         L"\\\\.\\pipe\\WebcamSwitcher.Frames.v1"
#define WS_MSG_MAGIC         0x57564353u   // 'WCVS'
#define WS_PROTOCOL_VERSION  1

enum WsMsgType
{
    WsMsgType_Config = 1,   // payload = WsConfigMsg
    WsMsgType_Frame  = 2,   // payload = WsFrameInfo + pixel bytes
};

enum WsPixelFormat
{
    WsPixelFormat_NV12  = 1,
    WsPixelFormat_RGB32 = 2,
};

enum WsFrameFlags
{
    WsFrameFlags_None     = 0,
    WsFrameFlags_NoSignal = 1,   // black / no-signal frame
};

// Default (and currently fixed) output format, shared by the source (advertised
// media type) and the app (normalized output). The pipe ConfigMsg carries the
// same values so both sides agree without recompilation.
#define WS_DEFAULT_WIDTH    1280
#define WS_DEFAULT_HEIGHT   720
#define WS_DEFAULT_FPS_NUM  30
#define WS_DEFAULT_FPS_DEN  1

#pragma pack(push, 1)
typedef struct _WsMsgHeader
{
    uint32_t magic;       // WS_MSG_MAGIC
    uint16_t version;     // WS_PROTOCOL_VERSION
    uint16_t type;        // WsMsgType
    uint32_t payloadLen;  // bytes following this header
} WsMsgHeader;

// Sent by the server (app) once per connected client, describing the fixed
// output format the source should advertise and deliver.
typedef struct _WsConfigMsg
{
    uint32_t width;
    uint32_t height;
    uint32_t stride;      // bytes per row of the Y plane (NV12) or full row (RGB32)
    uint32_t format;      // WsPixelFormat
    uint32_t fpsNum;      // frames per second numerator
    uint32_t fpsDen;      // frames per second denominator
} WsConfigMsg;

// Frame payload layout: WsFrameInfo followed by `payloadBytes` of pixel data.
//   NV12  : payloadBytes = width * height * 3 / 2
//           (Y plane: width*height, interleaved UV: width*height/2)
//   RGB32 : payloadBytes = width * height * 4
typedef struct _WsFrameInfo
{
    uint64_t sequence;       // monotonically increasing frame counter
    uint64_t timestampQpc;   // QueryPerformanceCounter value in 100-ns units (0 if absent)
    uint32_t width;
    uint32_t height;
    uint32_t stride;
    uint32_t format;         // WsPixelFormat
    uint32_t payloadBytes;   // pixel bytes following this struct
    uint32_t flags;          // WsFrameFlags
} WsFrameInfo;
#pragma pack(pop)

// Compile-time size sanity checks.
#ifdef __cplusplus
static_assert(sizeof(WsMsgHeader) == 12, "WsMsgHeader must be 12 bytes");
static_assert(sizeof(WsConfigMsg) == 24, "WsConfigMsg must be 24 bytes");
static_assert(sizeof(WsFrameInfo) == 40, "WsFrameInfo must be 40 bytes");
#endif
