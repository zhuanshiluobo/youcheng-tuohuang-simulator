using System.Runtime.InteropServices;

/// <summary>Windows 自带 GDI+ 完整解码 PNG/JPEG；无需给玩家额外安装桌面运行时。</summary>
internal static class NativeImageDecoder
{
    private const int PixelFormat32Argb = 0x26200a;
    [StructLayout(LayoutKind.Sequential)] private struct StartupInput
    { public uint Version; public IntPtr Callback; public int SuppressThread, SuppressCodecs; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect
    { public int X, Y, Width, Height; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapData
    { public uint Width, Height; public int Stride, PixelFormat; public IntPtr Scan0; public UIntPtr Reserved; }
    [DllImport("gdiplus.dll")] private static extern int GdiplusStartup(out IntPtr token, ref StartupInput input, IntPtr output);
    [DllImport("gdiplus.dll")] private static extern void GdiplusShutdown(IntPtr token);
    [DllImport("shlwapi.dll", ExactSpelling = true)] private static extern IntPtr SHCreateMemStream(byte[] bytes, uint size);
    [DllImport("gdiplus.dll")] private static extern int GdipLoadImageFromStream(IntPtr stream, out IntPtr image);
    [DllImport("gdiplus.dll")] private static extern int GdipGetImageWidth(IntPtr image, out uint width);
    [DllImport("gdiplus.dll")] private static extern int GdipGetImageHeight(IntPtr image, out uint height);
    [DllImport("gdiplus.dll")] private static extern int GdipCreateBitmapFromScan0(int width, int height, int stride, int format, IntPtr scan, out IntPtr bitmap);
    [DllImport("gdiplus.dll")] private static extern int GdipGetImageGraphicsContext(IntPtr image, out IntPtr graphics);
    [DllImport("gdiplus.dll")] private static extern int GdipDrawImageI(IntPtr graphics, IntPtr image, int x, int y);
    [DllImport("gdiplus.dll")] private static extern int GdipDeleteGraphics(IntPtr graphics);
    [DllImport("gdiplus.dll")] private static extern int GdipDisposeImage(IntPtr image);
    [DllImport("gdiplus.dll")] private static extern int GdipBitmapLockBits(IntPtr bitmap, ref Rect rect, uint flags, int format, out BitmapData data);
    [DllImport("gdiplus.dll")] private static extern int GdipBitmapUnlockBits(IntPtr bitmap, ref BitmapData data);

    public static void Decode(byte[] bytes, string path, int width, int height)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("图片完整解码需要 Windows。");
        var input = new StartupInput { Version = 1 };
        Check(GdiplusStartup(out IntPtr token, ref input, IntPtr.Zero), path);
        IntPtr stream = IntPtr.Zero, image = IntPtr.Zero, bitmap = IntPtr.Zero, graphics = IntPtr.Zero;
        try
        {
            stream = SHCreateMemStream(bytes, checked((uint)bytes.Length));
            if (stream == IntPtr.Zero) throw new InvalidDataException("无法打开图片：" + path);
            Check(GdipLoadImageFromStream(stream, out image), path);
            Check(GdipGetImageWidth(image, out uint actualWidth), path);
            Check(GdipGetImageHeight(image, out uint actualHeight), path);
            if (actualWidth != width || actualHeight != height) throw new InvalidDataException("图片解码尺寸不一致：" + path);
            Check(GdipCreateBitmapFromScan0(width, height, 0, PixelFormat32Argb, IntPtr.Zero, out bitmap), path);
            Check(GdipGetImageGraphicsContext(bitmap, out graphics), path);
            Check(GdipDrawImageI(graphics, image, 0, 0), path);
            var deleteStatus = GdipDeleteGraphics(graphics); graphics = IntPtr.Zero; Check(deleteStatus, path);
            var area = new Rect { Width = width, Height = height };
            Check(GdipBitmapLockBits(bitmap, ref area, 1, PixelFormat32Argb, out var pixels), path);
            try
            {
                int stride = Math.Abs(pixels.Stride); var row = new byte[stride];
                for (int y = 0; y < height; y++) Marshal.Copy(IntPtr.Add(pixels.Scan0, y * pixels.Stride), row, 0, stride);
            }
            finally { GdipBitmapUnlockBits(bitmap, ref pixels); }
        }
        finally
        {
            if (graphics != IntPtr.Zero) GdipDeleteGraphics(graphics);
            if (bitmap != IntPtr.Zero) GdipDisposeImage(bitmap);
            if (image != IntPtr.Zero) GdipDisposeImage(image);
            if (stream != IntPtr.Zero) Marshal.Release(stream);
            GdiplusShutdown(token);
        }
    }

    private static void Check(int status, string path)
    { if (status != 0) throw new InvalidDataException("图片解码失败（" + status + "）：" + path); }
}
