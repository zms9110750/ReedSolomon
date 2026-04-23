using System;
using System.Buffers;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace zms9110750.ReedSolomon.Streams;
/// <summary>
/// Stream 扩展方法，为低版本提供 Span/Memory 支持
/// </summary>
internal static class StreamExtensions
{
    private static AsyncLocal<long> _totalRequested = new();
    private static readonly AsyncLocal<byte[]> _asyncLocalBuffer = new();
    private static byte[] GetBuffer(int minSize)
    {
        byte[]? buffer = _asyncLocalBuffer.Value;
        if (buffer != null && buffer.Length >= minSize && _totalRequested.Value > 0)
        {
            if ((_totalRequested.Value >> 10) > minSize)
            {
                _totalRequested.Value = 0;
            }
            return buffer;
        }
        if (buffer != null)
        {
            _totalRequested.Value += buffer.Length;
            ArrayPool<byte>.Shared.Return(buffer);
        }
        buffer = ArrayPool<byte>.Shared.Rent(minSize);
        _asyncLocalBuffer.Value = buffer;
        return buffer;
    }

    /// <summary>
    /// 读取数据到 Span 缓冲区
    /// </summary>
    public static int Read(this Stream stream, Span<byte> buffer)
    {
        byte[] cached = GetBuffer(buffer.Length);
        int bytesRead = stream.Read(cached, 0, buffer.Length);
        cached.AsSpan(0, bytesRead).CopyTo(buffer);
        return bytesRead;
    }

    /// <summary>
    /// 将 Span 数据写入流
    /// </summary>
    public static void Write(this Stream stream, ReadOnlySpan<byte> buffer)
    {
        byte[] cached = GetBuffer(buffer.Length);
        buffer.CopyTo(cached.AsSpan());
        stream.Write(cached, 0, buffer.Length);
    }

    /// <summary>
    /// 异步读取数据到 Memory 缓冲区
    /// </summary>
    public static async ValueTask<int> ReadAsync(this Stream stream, Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (MemoryMarshal.TryGetArray(buffer, out ArraySegment<byte> segment))
        {
            return await stream.ReadAsync(segment.Array!, segment.Offset, segment.Count, cancellationToken);
        }
        byte[] cached = GetBuffer(buffer.Length);
        int bytesRead = await stream.ReadAsync(cached, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
        cached.AsSpan(0, bytesRead).CopyTo(buffer.Span);
        return bytesRead;
    }

    /// <summary>
    /// 异步将 Memory 数据写入流
    /// </summary>
    public static async ValueTask WriteAsync(this Stream stream, ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (MemoryMarshal.TryGetArray(buffer, out ArraySegment<byte> segment))
        {
            await stream.WriteAsync(segment.Array!, segment.Offset, segment.Count, cancellationToken);
        }
        byte[] cached = GetBuffer(buffer.Length);
        buffer.Span.CopyTo(cached.AsSpan());
        await stream.WriteAsync(cached, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
    }
}