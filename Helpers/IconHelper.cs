using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AdbExplorer.Helpers
{
    public static class IconHelper
    {
        public static bool TryCreateIcoFromPng(string pngPath, string icoPath)
        {
            try
            {
                if (!File.Exists(pngPath))
                    return false;

                var decoder = new PngBitmapDecoder(new Uri(pngPath), BitmapCreateOptions.IgnoreImageCache, BitmapCacheOption.OnLoad);
                var source = decoder.Frames.Count > 0 ? decoder.Frames[0] : null;
                if (source == null || source.PixelWidth == 0 || source.PixelHeight == 0)
                    return false;

                int[] sizes = new[] { 256, 128, 64, 48, 32, 16 };
                var pngImages = new List<(int size, byte[] data)>();

                foreach (int size in sizes)
                {
                    byte[]? pngData = RenderPngBytes(source, size);
                    if (pngData != null && pngData.Length > 0)
                        pngImages.Add((size, pngData));
                }

                if (pngImages.Count == 0)
                    return false;

                Directory.CreateDirectory(Path.GetDirectoryName(icoPath) ?? ".");
                using var fs = new FileStream(icoPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                using var writer = new BinaryWriter(fs);

                // ICONDIR header
                writer.Write((ushort)0); // reserved
                writer.Write((ushort)1); // type = icon
                writer.Write((ushort)pngImages.Count);

                int offset = 6 + (16 * pngImages.Count);
                foreach (var item in pngImages)
                {
                    byte width = item.size == 256 ? (byte)0 : (byte)item.size;
                    byte height = item.size == 256 ? (byte)0 : (byte)item.size;

                    writer.Write(width);    // width
                    writer.Write(height);   // height
                    writer.Write((byte)0);  // color count
                    writer.Write((byte)0);  // reserved
                    writer.Write((ushort)1); // planes
                    writer.Write((ushort)32); // bit count
                    writer.Write(item.data.Length); // bytes in resource
                    writer.Write(offset); // image offset

                    offset += item.data.Length;
                }

                // Write PNG images
                foreach (var item in pngImages)
                    writer.Write(item.data);

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static byte[]? RenderPngBytes(BitmapSource source, int size)
        {
            try
            {
                double scaleX = (double)size / source.PixelWidth;
                double scaleY = (double)size / source.PixelHeight;
                var scaled = new TransformedBitmap(source, new ScaleTransform(scaleX, scaleY));
                scaled.Freeze();

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(scaled));
                using var ms = new MemoryStream();
                encoder.Save(ms);
                return ms.ToArray();
            }
            catch
            {
                return null;
            }
        }
    }
}
