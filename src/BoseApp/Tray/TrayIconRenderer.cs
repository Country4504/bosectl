using System.Runtime.InteropServices;

namespace BoseApp.Tray;

/// <summary>
/// 动态绘制托盘图标：两根竖向电池条分别代表左耳 (L) 和右耳 (R)。
/// 不依赖 System.Drawing，直接在 32 位 ARGB 缓冲区里画像素，再通过 CreateIconIndirect 生成 HICON。
/// </summary>
public static class TrayIconRenderer
{
    public readonly record struct State(int? Left, int? Right, bool Connected, bool LightTaskbar);

    private const uint Red = 0xFFE5484D;

    public static IntPtr Render(State s, int size)
    {
        size = Math.Clamp(size, 16, 64);
        var px = new uint[size * size];

        uint fg = s.LightTaskbar ? 0xFF1F1F1F : 0xFFFFFFFF;
        uint dim = s.LightTaskbar ? 0x801F1F1F : 0x80FFFFFF;

        double u = size / 16.0;                // 以 16px 为基准的缩放单位
        int stroke = Math.Max(1, (int)Math.Round(u));
        int barW = (int)Math.Round(6 * u);
        int gap = size - 2 * barW - 2 * (int)Math.Round(1 * u);
        int x0 = (int)Math.Round(1 * u);
        int x1 = x0 + barW + gap;
        int capH = Math.Max(1, (int)Math.Round(1.5 * u));
        int top = capH;
        int bottom = size - 1 - (int)Math.Round(0.5 * u);

        DrawBar(px, size, x0, top, barW, bottom, capH, stroke, s.Connected ? s.Left : null, s.Connected ? fg : dim);
        DrawBar(px, size, x1, top, barW, bottom, capH, stroke, s.Connected ? s.Right : null, s.Connected ? fg : dim);

        if (!s.Connected)
        {
            // 断开：在中间画一条斜线
            for (int i = 0; i < size; i++)
            for (int t = 0; t < stroke + 1; t++)
                Set(px, size, i, size - 1 - i + t, Red);
        }

        return CreateIcon(px, size);
    }

    private static void DrawBar(uint[] px, int size, int x, int top, int w, int bottom, int capH, int stroke, int? level, uint color)
    {
        // 电池正极帽
        int capW = Math.Max(2, w / 2);
        int capX = x + (w - capW) / 2;
        FillRect(px, size, capX, top - capH, capW, capH, color);

        // 外框
        int h = bottom - top + 1;
        FillRect(px, size, x, top, w, stroke, color);
        FillRect(px, size, x, bottom - stroke + 1, w, stroke, color);
        FillRect(px, size, x, top, stroke, h, color);
        FillRect(px, size, x + w - stroke, top, stroke, h, color);

        if (level is not int lv) return;

        // 内部填充（框内留 1px 间隙，太小的图标则不留）
        int pad = size >= 24 ? stroke : 0;
        int innerX = x + stroke + pad;
        int innerW = w - 2 * (stroke + pad);
        int innerTop = top + stroke + pad;
        int innerBottom = bottom - stroke - pad;
        int innerH = innerBottom - innerTop + 1;
        if (innerW <= 0 || innerH <= 0) return;

        int fillH = (int)Math.Round(innerH * Math.Clamp(lv, 0, 100) / 100.0);
        if (lv > 0) fillH = Math.Max(1, fillH);
        uint fill = lv <= 20 ? Red : color;
        FillRect(px, size, innerX, innerBottom - fillH + 1, innerW, fillH, fill);
    }

    private static void FillRect(uint[] px, int size, int x, int y, int w, int h, uint c)
    {
        for (int yy = y; yy < y + h; yy++)
        for (int xx = x; xx < x + w; xx++)
            Set(px, size, xx, yy, c);
    }

    private static void Set(uint[] px, int size, int x, int y, uint c)
    {
        if ((uint)x < (uint)size && (uint)y < (uint)size) px[y * size + x] = c;
    }

    // ================================================================ HICON 生成

    private static IntPtr CreateIcon(uint[] argb, int size)
    {
        var bi = new BITMAPV5HEADER
        {
            bV5Size = (uint)Marshal.SizeOf<BITMAPV5HEADER>(),
            bV5Width = size,
            bV5Height = -size, // 自上而下
            bV5Planes = 1,
            bV5BitCount = 32,
            bV5Compression = 3, // BI_BITFIELDS
            bV5RedMask = 0x00FF0000,
            bV5GreenMask = 0x0000FF00,
            bV5BlueMask = 0x000000FF,
            bV5AlphaMask = 0xFF000000,
        };

        IntPtr hdc = GetDC(IntPtr.Zero);
        IntPtr color = CreateDIBSection(hdc, ref bi, 0, out var bits, IntPtr.Zero, 0);
        ReleaseDC(IntPtr.Zero, hdc);
        if (color == IntPtr.Zero) return IntPtr.Zero;

        // 预乘 alpha（Windows 图标的 32 位位图要求预乘）
        var buf = new int[argb.Length];
        for (int i = 0; i < argb.Length; i++)
        {
            uint p = argb[i];
            uint a = p >> 24;
            uint r = ((p >> 16) & 0xFF) * a / 255;
            uint g = ((p >> 8) & 0xFF) * a / 255;
            uint b = (p & 0xFF) * a / 255;
            buf[i] = unchecked((int)((a << 24) | (r << 16) | (g << 8) | b));
        }
        Marshal.Copy(buf, 0, bits, buf.Length);

        IntPtr mask = CreateBitmap(size, size, 1, 1, IntPtr.Zero);
        var info = new ICONINFO { fIcon = true, hbmColor = color, hbmMask = mask };
        IntPtr hIcon = CreateIconIndirect(ref info);
        DeleteObject(color);
        DeleteObject(mask);
        return hIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPV5HEADER
    {
        public uint bV5Size;
        public int bV5Width;
        public int bV5Height;
        public ushort bV5Planes;
        public ushort bV5BitCount;
        public uint bV5Compression;
        public uint bV5SizeImage;
        public int bV5XPelsPerMeter;
        public int bV5YPelsPerMeter;
        public uint bV5ClrUsed;
        public uint bV5ClrImportant;
        public uint bV5RedMask;
        public uint bV5GreenMask;
        public uint bV5BlueMask;
        public uint bV5AlphaMask;
        public uint bV5CSType;
        public int ex, ey, ez, rx, ry, rz, gx, gy, gz; // CIEXYZTRIPLE
        public uint bV5GammaRed;
        public uint bV5GammaGreen;
        public uint bV5GammaBlue;
        public uint bV5Intent;
        public uint bV5ProfileData;
        public uint bV5ProfileSize;
        public uint bV5Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPV5HEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateBitmap(int w, int h, uint planes, uint bpp, IntPtr bits);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll")] private static extern IntPtr CreateIconIndirect(ref ICONINFO info);
}
