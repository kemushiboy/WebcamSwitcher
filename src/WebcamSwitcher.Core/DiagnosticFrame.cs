namespace WebcamSwitcher.Core;

/// <summary>
/// Renders the picture a role's virtual camera shows while its physical camera
/// is not delivering, e.g.
/// <code>
///   CAM-01
///   PHYSICAL CAMERA MISSING
///   STATE: RECONNECTING (ATTEMPT 3)
///   LAST FRAME: 2026-10-29 12:34:56
/// </code>
/// Straight into an NV12 buffer with a built-in 5x7 font: no GDI, no WPF, no
/// per-frame allocation, so it is safe to call from any thread at a few Hz.
/// </summary>
public sealed class DiagnosticFrame
{
    private readonly int _w, _h;
    private readonly byte[] _nv12;
    private string _lastKey = "";

    public DiagnosticFrame(int width, int height)
    {
        _w = width;
        _h = height;
        _nv12 = new byte[Protocol.Nv12Size(width, height)];
    }

    public int Width => _w;
    public int Height => _h;
    public byte[] Buffer => _nv12;

    /// <summary>
    /// Renders the lines (first one large) unless they equal the previous call's.
    /// Returns the buffer. <paramref name="accent"/> picks the stripe colour:
    /// 0 = red (missing/error), 1 = amber (connecting/no signal), 2 = grey.
    /// </summary>
    public byte[] Render(IReadOnlyList<string> lines, int accent)
    {
        string key = accent + "\u0001" + string.Join("\u0001", lines);
        if (key == _lastKey)
            return _nv12;
        _lastKey = key;

        int ySize = _w * _h;
        Array.Fill(_nv12, (byte)24, 0, ySize);                // dark grey
        Array.Fill(_nv12, (byte)128, ySize, _nv12.Length - ySize);

        // Accent stripe across the top (NV12 chroma: U,V pairs).
        (byte u, byte v) = accent switch
        {
            0 => ((byte)90, (byte)240),   // red
            1 => ((byte)40, (byte)170),   // amber
            _ => ((byte)128, (byte)128),  // grey
        };
        int stripe = Math.Max(8, _h / 40) & ~1;
        FillRect(0, 0, _w, stripe, 120, u, v);

        int y = stripe + _h / 12;
        for (int i = 0; i < lines.Count; i++)
        {
            int scale = i == 0 ? Math.Max(2, _h / 60) : Math.Max(1, _h / 140);
            string text = lines[i] ?? "";
            int maxChars = Math.Max(1, (_w - 2 * (_w / 20)) / (6 * scale));
            if (text.Length > maxChars)
                text = text[..maxChars];
            DrawText(text, _w / 20, y, scale, i == 0 ? (byte)235 : (byte)200);
            y += (i == 0 ? 10 : 9) * scale + (i == 0 ? _h / 20 : _h / 60);
        }
        return _nv12;
    }

    private void FillRect(int x0, int y0, int w, int h, byte yv, byte u, byte v)
    {
        int x1 = Math.Min(_w, x0 + w), y1 = Math.Min(_h, y0 + h);
        for (int y = Math.Max(0, y0); y < y1; y++)
            Array.Fill(_nv12, yv, y * _w + Math.Max(0, x0), x1 - Math.Max(0, x0));
        int uvBase = _w * _h;
        for (int y = Math.Max(0, y0) / 2; y < y1 / 2; y++)
            for (int x = Math.Max(0, x0) & ~1; x < x1; x += 2)
            {
                _nv12[uvBase + y * _w + x] = u;
                _nv12[uvBase + y * _w + x + 1] = v;
            }
    }

    private void DrawText(string text, int x, int y, int scale, byte luma)
    {
        foreach (char raw in text)
        {
            char c = char.ToUpperInvariant(raw);
            if (Glyphs.TryGetValue(c, out var rows))
            {
                for (int gy = 0; gy < 7; gy++)
                {
                    int bits = rows[gy];
                    for (int gx = 0; gx < 5; gx++)
                        if ((bits & (0x10 >> gx)) != 0)
                            FillLuma(x + gx * scale, y + gy * scale, scale, luma);
                }
            }
            x += 6 * scale;
            if (x >= _w)
                break;
        }
    }

    private void FillLuma(int x0, int y0, int size, byte luma)
    {
        if (x0 < 0 || y0 < 0)
            return;
        int x1 = Math.Min(_w, x0 + size), y1 = Math.Min(_h, y0 + size);
        for (int y = y0; y < y1; y++)
            Array.Fill(_nv12, luma, y * _w + x0, Math.Max(0, x1 - x0));
    }

    // 5x7 glyphs, one byte per row, bit 4 = leftmost column.
    private static readonly Dictionary<char, byte[]> Glyphs = BuildGlyphs();

    private static Dictionary<char, byte[]> BuildGlyphs()
    {
        var g = new Dictionary<char, byte[]>();
        void Add(char c, params byte[] rows) => g[c] = rows;
        Add(' ', 0, 0, 0, 0, 0, 0, 0);
        Add('A', 0x0E, 0x11, 0x11, 0x1F, 0x11, 0x11, 0x11);
        Add('B', 0x1E, 0x11, 0x11, 0x1E, 0x11, 0x11, 0x1E);
        Add('C', 0x0E, 0x11, 0x10, 0x10, 0x10, 0x11, 0x0E);
        Add('D', 0x1E, 0x11, 0x11, 0x11, 0x11, 0x11, 0x1E);
        Add('E', 0x1F, 0x10, 0x10, 0x1E, 0x10, 0x10, 0x1F);
        Add('F', 0x1F, 0x10, 0x10, 0x1E, 0x10, 0x10, 0x10);
        Add('G', 0x0E, 0x11, 0x10, 0x17, 0x11, 0x11, 0x0F);
        Add('H', 0x11, 0x11, 0x11, 0x1F, 0x11, 0x11, 0x11);
        Add('I', 0x0E, 0x04, 0x04, 0x04, 0x04, 0x04, 0x0E);
        Add('J', 0x07, 0x02, 0x02, 0x02, 0x02, 0x12, 0x0C);
        Add('K', 0x11, 0x12, 0x14, 0x18, 0x14, 0x12, 0x11);
        Add('L', 0x10, 0x10, 0x10, 0x10, 0x10, 0x10, 0x1F);
        Add('M', 0x11, 0x1B, 0x15, 0x15, 0x11, 0x11, 0x11);
        Add('N', 0x11, 0x11, 0x19, 0x15, 0x13, 0x11, 0x11);
        Add('O', 0x0E, 0x11, 0x11, 0x11, 0x11, 0x11, 0x0E);
        Add('P', 0x1E, 0x11, 0x11, 0x1E, 0x10, 0x10, 0x10);
        Add('Q', 0x0E, 0x11, 0x11, 0x11, 0x15, 0x12, 0x0D);
        Add('R', 0x1E, 0x11, 0x11, 0x1E, 0x14, 0x12, 0x11);
        Add('S', 0x0F, 0x10, 0x10, 0x0E, 0x01, 0x01, 0x1E);
        Add('T', 0x1F, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04);
        Add('U', 0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x0E);
        Add('V', 0x11, 0x11, 0x11, 0x11, 0x11, 0x0A, 0x04);
        Add('W', 0x11, 0x11, 0x11, 0x15, 0x15, 0x15, 0x0A);
        Add('X', 0x11, 0x11, 0x0A, 0x04, 0x0A, 0x11, 0x11);
        Add('Y', 0x11, 0x11, 0x11, 0x0A, 0x04, 0x04, 0x04);
        Add('Z', 0x1F, 0x01, 0x02, 0x04, 0x08, 0x10, 0x1F);
        Add('0', 0x0E, 0x11, 0x13, 0x15, 0x19, 0x11, 0x0E);
        Add('1', 0x04, 0x0C, 0x04, 0x04, 0x04, 0x04, 0x0E);
        Add('2', 0x0E, 0x11, 0x01, 0x02, 0x04, 0x08, 0x1F);
        Add('3', 0x1F, 0x02, 0x04, 0x02, 0x01, 0x11, 0x0E);
        Add('4', 0x02, 0x06, 0x0A, 0x12, 0x1F, 0x02, 0x02);
        Add('5', 0x1F, 0x10, 0x1E, 0x01, 0x01, 0x11, 0x0E);
        Add('6', 0x06, 0x08, 0x10, 0x1E, 0x11, 0x11, 0x0E);
        Add('7', 0x1F, 0x01, 0x02, 0x04, 0x08, 0x08, 0x08);
        Add('8', 0x0E, 0x11, 0x11, 0x0E, 0x11, 0x11, 0x0E);
        Add('9', 0x0E, 0x11, 0x11, 0x0F, 0x01, 0x02, 0x0C);
        Add('-', 0, 0, 0, 0x1F, 0, 0, 0);
        Add('_', 0, 0, 0, 0, 0, 0, 0x1F);
        Add(':', 0, 0x0C, 0x0C, 0, 0x0C, 0x0C, 0);
        Add('.', 0, 0, 0, 0, 0, 0x0C, 0x0C);
        Add(',', 0, 0, 0, 0, 0x0C, 0x04, 0x08);
        Add('/', 0x01, 0x01, 0x02, 0x04, 0x08, 0x10, 0x10);
        Add('(', 0x02, 0x04, 0x08, 0x08, 0x08, 0x04, 0x02);
        Add(')', 0x08, 0x04, 0x02, 0x02, 0x02, 0x04, 0x08);
        Add('#', 0x0A, 0x0A, 0x1F, 0x0A, 0x1F, 0x0A, 0x0A);
        Add('%', 0x18, 0x19, 0x02, 0x04, 0x08, 0x13, 0x03);
        Add('&', 0x0C, 0x12, 0x14, 0x08, 0x15, 0x12, 0x0D);
        Add('+', 0, 0x04, 0x04, 0x1F, 0x04, 0x04, 0);
        Add('=', 0, 0, 0x1F, 0, 0x1F, 0, 0);
        Add('>', 0x08, 0x04, 0x02, 0x01, 0x02, 0x04, 0x08);
        Add('<', 0x02, 0x04, 0x08, 0x10, 0x08, 0x04, 0x02);
        Add('?', 0x0E, 0x11, 0x01, 0x02, 0x04, 0, 0x04);
        Add('!', 0x04, 0x04, 0x04, 0x04, 0x04, 0, 0x04);
        Add('\'', 0x04, 0x04, 0x08, 0, 0, 0, 0);
        Add('"', 0x0A, 0x0A, 0, 0, 0, 0, 0);
        Add('@', 0x0E, 0x11, 0x17, 0x15, 0x17, 0x10, 0x0E);
        Add('*', 0, 0x04, 0x15, 0x0E, 0x15, 0x04, 0);
        Add('[', 0x0E, 0x08, 0x08, 0x08, 0x08, 0x08, 0x0E);
        Add(']', 0x0E, 0x02, 0x02, 0x02, 0x02, 0x02, 0x0E);
        Add('…', 0, 0, 0, 0, 0, 0, 0x15);
        return g;
    }
}
