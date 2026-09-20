using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VcsTextureCompare.Compare;

/// <summary>
/// 磁盘贴图解码结果。Bgra 按行主序、每像素 4 字节，供像素差计算；Bitmap 已 Freeze。
/// </summary>
public sealed class LoadedTexture
{
    public required string Path { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required byte[] Bgra { get; init; }
    public required BitmapSource Bitmap { get; init; }
    public required long FileSize { get; init; }
    public required string Format { get; init; }
}

/// <summary>
/// 贴图解码：WIC 处理 PNG/JPG/BMP/GIF/WEBP，TGA 走自研读取（Unity 贴图常用）。
/// </summary>
public static class ImageLoader
{
    public static readonly string[] Extensions = { ".png", ".jpg", ".jpeg", ".tga", ".bmp", ".webp", ".gif" };

    public static bool IsSupported(string path)
    {
        var ext = System.IO.Path.GetExtension(path);
        return Extensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase));
    }

    public static LoadedTexture Load(string path)
    {
        var ext = System.IO.Path.GetExtension(path);
        BitmapSource bitmap;
        string format;
        if (string.Equals(ext, ".tga", StringComparison.OrdinalIgnoreCase))
        {
            bitmap = TgaDecoder.Load(path);
            format = "TGA";
        }
        else
        {
            bitmap = LoadViaWic(path);
            format = string.IsNullOrEmpty(ext) ? "IMG" : ext.TrimStart('.').ToUpperInvariant();
        }

        var bgra = ToBgra32(bitmap, out var converted);
        converted.Freeze();
        return new LoadedTexture
        {
            Path = path,
            Width = converted.PixelWidth,
            Height = converted.PixelHeight,
            Bgra = bgra,
            Bitmap = converted,
            FileSize = new FileInfo(path).Length,
            Format = format
        };
    }

    public static async Task<LoadedTexture> LoadAsync(string path, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            return Load(path);
        }, ct);
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";
        if (bytes < 1024 * 1024)
            return $"{bytes / 1024.0:0.#} KB";
        return $"{bytes / (1024.0 * 1024.0):0.##} MB";
    }

    private static BitmapSource LoadViaWic(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"无法解码图片：{System.IO.Path.GetFileName(path)}。{ex.Message}");
        }
    }

    private static byte[] ToBgra32(BitmapSource source, out BitmapSource bgraSource)
    {
        BitmapSource converted = source;
        if (source.Format != PixelFormats.Bgra32)
        {
            var conv = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            conv.Freeze();
            converted = conv;
        }
        var w = converted.PixelWidth;
        var h = converted.PixelHeight;
        var stride = w * 4;
        var pixels = new byte[stride * h];
        converted.CopyPixels(pixels, stride, 0);
        bgraSource = converted;
        return pixels;
    }
}

/// <summary>读取未压缩 / RLE 的 24、32 位 TGA（含底原点翻转）。</summary>
internal static class TgaDecoder
{
    public static BitmapSource Load(string path)
    {
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);
        if (fs.Length < 18)
            throw new InvalidOperationException("TGA 文件过短。");

        var idLength = br.ReadByte();
        var colorMapType = br.ReadByte();
        var imageType = br.ReadByte();
        br.ReadBytes(5); // color map spec
        br.ReadInt16(); // x origin
        br.ReadInt16(); // y origin
        var width = br.ReadUInt16();
        var height = br.ReadUInt16();
        var bpp = br.ReadByte();
        var descriptor = br.ReadByte();
        if (width == 0 || height == 0)
            throw new InvalidOperationException("TGA 尺寸无效。");
        if (idLength > 0)
            br.ReadBytes(idLength);
        if (colorMapType == 1)
        {
            // 跳过调色板，真彩贴图通常没有；索引色暂不支持
            throw new InvalidOperationException("暂不支持带调色板的 TGA。");
        }

        var compressed = imageType is 10 or 11;
        var trueColor = imageType is 2 or 10;
        var grey = imageType is 3 or 11;
        if (!trueColor && !grey)
            throw new InvalidOperationException($"不支持的 TGA 类型 {imageType}。");
        if (trueColor && bpp is not 24 and not 32)
            throw new InvalidOperationException($"不支持的 TGA 位深 {bpp}。");
        if (grey && bpp is not 8 and not 16)
            throw new InvalidOperationException($"不支持的 TGA 灰度位深 {bpp}。");

        var bytesPerPixel = bpp / 8;
        var pixelCount = width * height;
        var raw = ReadPixels(br, pixelCount, bytesPerPixel, compressed);
        var topOrigin = (descriptor & 0x20) != 0;
        var wb = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        wb.Lock();
        try
        {
            unsafe
            {
                for (var y = 0; y < height; y++)
                {
                    var srcY = topOrigin ? y : height - 1 - y;
                    var dest = (byte*)(wb.BackBuffer + y * wb.BackBufferStride);
                    for (var x = 0; x < width; x++)
                    {
                        var src = ((srcY * width) + x) * bytesPerPixel;
                        byte b, g, r, a;
                        if (grey)
                        {
                            var v = raw[src];
                            b = g = r = v;
                            a = bytesPerPixel == 2 ? raw[src + 1] : (byte)255;
                        }
                        else
                        {
                            b = raw[src];
                            g = raw[src + 1];
                            r = raw[src + 2];
                            a = bytesPerPixel == 4 ? raw[src + 3] : (byte)255;
                        }
                        dest[x * 4 + 0] = b;
                        dest[x * 4 + 1] = g;
                        dest[x * 4 + 2] = r;
                        dest[x * 4 + 3] = a;
                    }
                }
            }
            wb.AddDirtyRect(new Int32Rect(0, 0, width, height));
        }
        finally
        {
            wb.Unlock();
        }
        wb.Freeze();
        return wb;
    }

    private static byte[] ReadPixels(BinaryReader br, int pixelCount, int bytesPerPixel, bool compressed)
    {
        var raw = new byte[pixelCount * bytesPerPixel];
        if (!compressed)
        {
            var n = br.Read(raw, 0, raw.Length);
            if (n < raw.Length)
                throw new InvalidOperationException("TGA 像素数据不完整。");
            return raw;
        }

        var dst = 0;
        while (dst < raw.Length)
        {
            var header = br.ReadByte();
            var count = (header & 0x7F) + 1;
            if ((header & 0x80) != 0)
            {
                var pixel = br.ReadBytes(bytesPerPixel);
                if (pixel.Length < bytesPerPixel)
                    throw new InvalidOperationException("TGA RLE 数据不完整。");
                for (var i = 0; i < count; i++)
                {
                    Buffer.BlockCopy(pixel, 0, raw, dst, bytesPerPixel);
                    dst += bytesPerPixel;
                }
            }
            else
            {
                var bytes = count * bytesPerPixel;
                var n = br.Read(raw, dst, bytes);
                if (n < bytes)
                    throw new InvalidOperationException("TGA RLE 数据不完整。");
                dst += bytes;
            }
        }
        return raw;
    }
}
