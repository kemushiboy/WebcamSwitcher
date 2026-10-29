using System.Diagnostics;

namespace WebcamSwitcher.Core;

/// <summary>
/// Measurement aid for the added latency of this app's path (capture callback
/// -> pipe -> Frame Server -> consumer). When enabled, the arrival time of each
/// camera frame (<see cref="Stopwatch.GetTimestamp"/>, i.e. QPC) is written as
/// a 64-bit barcode into the top rows of the published frame; a consumer on the
/// same machine decodes it and subtracts it from its own QPC on receipt
/// (tools/LatencyTest). Never enabled in normal operation.
/// </summary>
public static class LatencyProbe
{
    public const int Bits = 64;
    public const int CellWidth = 16;
    public const int CellHeight = 16;
    private const byte One = 235, Zero = 16;

    public static void Stamp(byte[] nv12, int width, int height, long timestamp)
    {
        if (width < Bits * CellWidth || height < CellHeight)
            return;
        ulong v = unchecked((ulong)timestamp);
        for (int bit = 0; bit < Bits; bit++)
        {
            byte luma = ((v >> (Bits - 1 - bit)) & 1) != 0 ? One : Zero;
            int x0 = bit * CellWidth;
            for (int y = 0; y < CellHeight; y++)
                Array.Fill(nv12, luma, y * width + x0, CellWidth);
        }
        // Neutral chroma under the barcode so colour conversion cannot shift luma.
        int uv = width * height;
        for (int y = 0; y < CellHeight / 2; y++)
            Array.Fill(nv12, (byte)128, uv + y * width, Bits * CellWidth);
    }

    /// <summary>Reads the stamp back from an 8-bit luma plane (NV12/I420 Y or Gray8).</summary>
    public static long Read(ReadOnlySpan<byte> luma, int stride)
    {
        ulong v = 0;
        int y = CellHeight / 2;
        for (int bit = 0; bit < Bits; bit++)
        {
            int x = bit * CellWidth + CellWidth / 2;
            v = (v << 1) | (luma[y * stride + x] >= 128 ? 1UL : 0UL);
        }
        return unchecked((long)v);
    }
}
