using System.Buffers.Binary;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace BlindsDown.Ui.Tray;

/// <summary>
/// Turns a 32-bpp ARGB bitmap into an <see cref="Icon"/> by writing a one-image .ico file in memory.
/// Why not Bitmap.GetHicon + Icon.FromHandle: that Icon does not own the HICON, so every re-render would
/// leak a GDI handle unless DestroyIcon is called at exactly the right moment. An Icon loaded from a
/// stream owns its handle and destroys it on Dispose. The image is passed to the Icon constructor at its
/// exact size; otherwise Windows would stretch it to SM_CXICON (32 px at 100%).
/// </summary>
internal static class IconBuilder
{
    private const int IconDirSize = 6;
    private const int IconDirEntrySize = 16;
    private const int BitmapInfoHeaderSize = 40;

    public static Icon FromBitmap(Bitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        int width = bitmap.Width;
        int height = bitmap.Height;
        if (width is < 1 or > 256 || height is < 1 or > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(bitmap), "Icons must be 1-256 pixels in each direction.");
        }

        int xorStride = width * 4;
        int andStride = ((width + 31) / 32) * 4; // 1 bpp rows padded to 32 bits.
        int imageBytes = BitmapInfoHeaderSize + (xorStride * height) + (andStride * height);
        int imageOffset = IconDirSize + IconDirEntrySize;
        byte[] ico = new byte[imageOffset + imageBytes];
        Span<byte> span = ico;

        // ICONDIR
        BinaryPrimitives.WriteUInt16LittleEndian(span[2..], 1); // type: icon
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], 1); // one image

        // ICONDIRENTRY (0 means 256)
        span[6] = (byte)(width == 256 ? 0 : width);
        span[7] = (byte)(height == 256 ? 0 : height);
        BinaryPrimitives.WriteUInt16LittleEndian(span[10..], 1); // planes
        BinaryPrimitives.WriteUInt16LittleEndian(span[12..], 32); // bits per pixel
        BinaryPrimitives.WriteInt32LittleEndian(span[14..], imageBytes);
        BinaryPrimitives.WriteInt32LittleEndian(span[18..], imageOffset);

        // BITMAPINFOHEADER: the height covers the colour image plus the AND mask below it.
        Span<byte> header = span[imageOffset..];
        BinaryPrimitives.WriteInt32LittleEndian(header, BitmapInfoHeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], width);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], height * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(header[12..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header[14..], 32);
        BinaryPrimitives.WriteInt32LittleEndian(header[20..], imageBytes - BitmapInfoHeaderSize);

        Span<byte> xor = span.Slice(imageOffset + BitmapInfoHeaderSize, xorStride * height);
        Span<byte> and = span.Slice(imageOffset + BitmapInfoHeaderSize + (xorStride * height), andStride * height);
        CopyPixels(bitmap, xor, and, andStride);

        // Icon copies the bytes, so the stream can go away immediately.
        using var stream = new MemoryStream(ico, writable: false);
        return new Icon(stream, width, height);
    }

    /// <summary>Copies straight (non-premultiplied) BGRA rows bottom-up, as icon DIBs expect, and sets the
    /// legacy AND mask bit for fully transparent pixels.</summary>
    private static void CopyPixels(Bitmap bitmap, Span<byte> xor, Span<byte> and, int andStride)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;
        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte[] row = new byte[width * 4];
            for (int y = 0; y < height; y++)
            {
                Marshal.Copy(data.Scan0 + (y * data.Stride), row, 0, row.Length);
                int targetRow = height - 1 - y;
                row.CopyTo(xor.Slice(targetRow * width * 4, row.Length));
                for (int x = 0; x < width; x++)
                {
                    if (row[(x * 4) + 3] == 0)
                    {
                        and[(targetRow * andStride) + (x / 8)] |= (byte)(0x80 >> (x % 8));
                    }
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}
