using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Aprillz.MewUI;
using Aprillz.MewUI.Rendering;
using Aprillz.MewUI.Resources;

namespace GirlsMadeInfinitePudding;

public class UnityColorImageSource(byte[] data, int width, int height) : IImageSource
{
    public IImage CreateImage(IGraphicsFactory factory)
    {
        var bgraData = ConvertRgbaToBgra(data);
        var bufferSource = new StaticPixelBufferSource(bgraData, width, height);
        return factory.CreateImageFromPixelSource(bufferSource);
    }

    private static byte[] ConvertRgbaToBgra(byte[] data)
    {
        if (data.Length % 4 != 0)
            throw new ArgumentException("Data length must be a multiple of 4 (RGBA32 format)", nameof(data));

        var bgraData = new byte[data.Length];

        // 获取安全的内存视图
        ReadOnlySpan<byte> srcSpan = data;
        Span<byte> dstSpan = bgraData;

        int processedBytes = 0;

        if (Vector128.IsHardwareAccelerated)
        {
            var shuffleMask = Vector128.Create(
                2, 1, 0, 3,
                6, 5, 4, 7,
                10, 9, 8, 11,
                14, 13, 12, (byte)15);

            ReadOnlySpan<Vector128<byte>> srcVecSpan = MemoryMarshal.Cast<byte, Vector128<byte>>(srcSpan);
            Span<Vector128<byte>> dstVecSpan = MemoryMarshal.Cast<byte, Vector128<byte>>(dstSpan);

            for (int i = 0; i < srcVecSpan.Length; i++)
            {
                dstVecSpan[i] = Vector128.Shuffle(srcVecSpan[i], shuffleMask);
            }

            // Cast 方法会自动计算能被 16 整除的长度，这里算出已经处理了多少 byte
            processedBytes = srcVecSpan.Length * 16;
        }

        // 处理不足 16 字节的剩余数据
        for (int i = processedBytes; i < srcSpan.Length; i += 4)
        {
            dstSpan[i] = srcSpan[i + 2];
            dstSpan[i + 1] = srcSpan[i + 1];
            dstSpan[i + 2] = srcSpan[i];
            dstSpan[i + 3] = srcSpan[i + 3];
        }

        return bgraData;
    }

    private class StaticPixelBufferSource(byte[] data, int width, int height) : IPixelBufferSource
    {
        [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
        private static extern PixelBufferLock CreatePixelBufferLock(byte[] buffer,
            int pixelWidth,
            int pixelHeight,
            int strideBytes,
            BitmapPixelFormat pixelFormat,
            int version,
            PixelRegion? dirtyRegion,
            Action? release);
        
        public PixelBufferLock Lock()
            => CreatePixelBufferLock(data, PixelWidth, PixelHeight, StrideBytes, PixelFormat, Version, null, null);

        public int PixelWidth => width;

        public int PixelHeight => height;

        public int StrideBytes => PixelWidth * 4;

        public BitmapPixelFormat PixelFormat => BitmapPixelFormat.Bgra32;

        public int Version => 0;
    }
}