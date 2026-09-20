using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VcsTextureCompare.Compare;

/// <summary>两张贴图像素差：按最大画布对齐，越界像素视为最大差异。</summary>
public sealed class PixelDiffMap
{
    public int Width { get; }
    public int Height { get; }
    public int LeftWidth { get; }
    public int LeftHeight { get; }
    public int RightWidth { get; }
    public int RightHeight { get; }
    public bool SizeMismatch { get; }
    public int MaxChannelDelta { get; }
    public byte[] MaxDelta { get; }

    private PixelDiffMap(int width, int height, int lw, int lh, int rw, int rh, int maxDelta, byte[] data)
    {
        Width = width;
        Height = height;
        LeftWidth = lw;
        LeftHeight = lh;
        RightWidth = rw;
        RightHeight = rh;
        SizeMismatch = lw != rw || lh != rh;
        MaxChannelDelta = maxDelta;
        MaxDelta = data;
    }

    public static PixelDiffMap Compute(LoadedTexture left, LoadedTexture right)
    {
        var w = Math.Max(left.Width, right.Width);
        var h = Math.Max(left.Height, right.Height);
        var data = new byte[w * h];
        var maxAll = 0;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var inL = x < left.Width && y < left.Height;
                var inR = x < right.Width && y < right.Height;
                byte d;
                if (!inL || !inR)
                {
                    d = 255;
                }
                else
                {
                    var li = (y * left.Width + x) * 4;
                    var ri = (y * right.Width + x) * 4;
                    var db = Math.Abs(left.Bgra[li] - right.Bgra[ri]);
                    var dg = Math.Abs(left.Bgra[li + 1] - right.Bgra[ri + 1]);
                    var dr = Math.Abs(left.Bgra[li + 2] - right.Bgra[ri + 2]);
                    var da = Math.Abs(left.Bgra[li + 3] - right.Bgra[ri + 3]);
                    d = (byte)Math.Max(Math.Max(dr, dg), Math.Max(db, da));
                }
                data[y * w + x] = d;
                if (d > maxAll)
                    maxAll = d;
            }
        }
        return new PixelDiffMap(w, h, left.Width, left.Height, right.Width, right.Height, maxAll, data);
    }

    public DiffStats GetStats(byte threshold)
    {
        var changed = 0;
        var total = MaxDelta.Length;
        foreach (var d in MaxDelta)
        {
            if (d > threshold)
                changed++;
        }
        return new DiffStats
        {
            ChangedPixels = changed,
            TotalPixels = total,
            ChangedPercent = total == 0 ? 0 : changed * 100.0 / total,
            MaxChannelDelta = MaxChannelDelta,
            SizeMismatch = SizeMismatch,
            LeftWidth = LeftWidth,
            LeftHeight = LeftHeight,
            RightWidth = RightWidth,
            RightHeight = RightHeight
        };
    }

    /// <summary>相同像素透明；差异为黄到红。叠在原图上才能看出改动位置。</summary>
    public BitmapSource BuildHeatmap(byte threshold)
    {
        var w = Width;
        var h = Height;
        var wb = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        wb.Lock();
        try
        {
            unsafe
            {
                for (var y = 0; y < h; y++)
                {
                    var dest = (byte*)(wb.BackBuffer + y * wb.BackBufferStride);
                    for (var x = 0; x < w; x++)
                    {
                        var d = MaxDelta[y * w + x];
                        byte b, g, r, a;
                        if (d <= threshold)
                        {
                            // 相同像素全透明，露出底下的原图
                            b = g = r = a = 0;
                        }
                        else
                        {
                            var t = (d - threshold) / (float)Math.Max(1, 255 - threshold);
                            r = 255;
                            g = (byte)(255 * (1f - t));
                            b = 0;
                            a = (byte)(160 + 95 * t);
                        }
                        dest[x * 4 + 0] = b;
                        dest[x * 4 + 1] = g;
                        dest[x * 4 + 2] = r;
                        dest[x * 4 + 3] = a;
                    }
                }
            }
            wb.AddDirtyRect(new Int32Rect(0, 0, w, h));
        }
        finally
        {
            wb.Unlock();
        }
        wb.Freeze();
        return wb;
    }
}

public readonly struct DiffStats
{
    public int ChangedPixels { get; init; }
    public int TotalPixels { get; init; }
    public double ChangedPercent { get; init; }
    public int MaxChannelDelta { get; init; }
    public bool SizeMismatch { get; init; }
    public int LeftWidth { get; init; }
    public int LeftHeight { get; init; }
    public int RightWidth { get; init; }
    public int RightHeight { get; init; }
}

public enum CompareMode
{
    Wipe,
    Heatmap,
    Overlay,
    Blink
}
