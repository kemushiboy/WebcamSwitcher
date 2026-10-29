using System.Runtime.InteropServices;

namespace WebcamSwitcher.Core;

// C# mirror of src/WebcamSwitcher.Shared/WebcamSwitcherProtocol.h
public static class Protocol
{
    public const string PipeName = @"\\.\pipe\WebcamSwitcher.Frames.v1";

    /// <summary>Pipe for the continuous passthrough feed of camera slot <paramref name="slot"/>.</summary>
    public static string PassthroughPipeName(int slot) => PipeName + ".p" + slot;

    /// <summary>
    /// Pipe of one Camera Role, named after its persistent virtual camera ID so
    /// that it is tied to the role, never to a list position.
    /// </summary>
    public static string RolePipeName(string virtualCameraId) => PipeName + ".r." + virtualCameraId;

    public const uint Magic = 0x57564353u;
    public const ushort Version = 1;

    public const ushort TypeConfig = 1;
    public const ushort TypeFrame = 2;

    public const uint FormatNv12 = 1;
    public const uint FormatRgb32 = 2;

    public const uint FlagNone = 0;
    public const uint FlagNoSignal = 1;

    public const int DefaultWidth = 1280;
    public const int DefaultHeight = 720;
    public const int DefaultFps = 30;

    // CLSID of WebcamSwitcher.Source (matches dllmain.cpp)
    public const string SourceClsid = "{7B1C4D2E-8F3A-4B5C-9D6E-1F2A3B4C5D6E}";

    public static int Nv12Size(int width, int height) => width * height * 3 / 2;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct WsMsgHeader
{
    public uint Magic;
    public ushort Version;
    public ushort Type;
    public uint PayloadLen;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct WsConfigMsg
{
    public uint Width;
    public uint Height;
    public uint Stride;
    public uint Format;
    public uint FpsNum;
    public uint FpsDen;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct WsFrameInfo
{
    public ulong Sequence;
    public ulong TimestampQpc;
    public uint Width;
    public uint Height;
    public uint Stride;
    public uint Format;
    public uint PayloadBytes;
    public uint Flags;
}
