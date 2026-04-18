using System.Buffers;
using System.Collections.Immutable;
using System.Runtime.InteropServices;

namespace zms9110750.ReedSolomon.Streams;

/// <summary>
/// 流轮换器：循环使用多个底层流，每写入/读取指定字节数自动切换到下一个流
/// </summary>
public class StreamRoundRobin : SpanCapableStreamBase
{
    private readonly ImmutableList<Stream> _streams;
    private long _position;

    /// <summary>
    /// 底层流的数量
    /// </summary>
    public int StreamsCount => _streams.Count;

    /// <summary>
    /// 每个流操作的字节数，达到后自动切换
    /// </summary>
    public int SegmentSize { get; }

    /// <inheritdoc/>
    public override bool CanRead { get; }

    /// <inheritdoc/>
    public override bool CanWrite { get; }

    /// <inheritdoc/>
    public override bool CanSeek { get; }

    /// <inheritdoc/>
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc/>
    public override long Position
    {
        get => _position;
        set
        {
            if (!CanSeek)
            {
                throw new NotSupportedException("流不可跳转");
            }
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            if (value == _position)
            {
                return;
            }

            Span<long> stepOld = stackalloc long[_streams.Count];
            Span<long> stepNew = stackalloc long[_streams.Count];
            FillBySegments(stepOld, SegmentSize, _position);
            FillBySegments(stepNew, SegmentSize, value);

            for (int i = 0; i < _streams.Count; i++)
            {
                _streams[i].Seek(stepNew[i] - stepOld[i], SeekOrigin.Current);
            }
            _position = value;

            static void FillBySegments(Span<long> span, int segmentSize, long position)
            {
                span.Fill(Math.DivRem(position, span.Length * segmentSize, out long remaining) * segmentSize);

                foreach (ref var item in span)
                {
                    long add = Math.Min(segmentSize, remaining);
                    item += add;
                    remaining -= add;
                    if (add == 0)
                    {
                        break;
                    }
                }
            }
        }
    }

    /// <summary>
    /// 当前正在使用的流
    /// </summary>
    private Stream CurrentStream => _streams[(int)((_position / SegmentSize) % _streams.Count)];

    /// <summary>
    /// 初始化流轮换器
    /// </summary>
    /// <param name="streams">底层流集合（循环使用，不会关闭）</param>
    /// <param name="segmentSize">每个流操作的字节数，达到后自动切换</param>
    public StreamRoundRobin(IEnumerable<Stream> streams, int segmentSize)
    {
        if (streams == null)
        {
            throw new ArgumentNullException(nameof(streams));
        }
        if (segmentSize <= 0)
        {
            throw new ArgumentException("segmentSize 必须大于 0", nameof(segmentSize));
        }

        _streams = streams.OfType<Stream>().ToImmutableList();

        if (_streams.Count == 0)
        {
            throw new ArgumentException("至少需要一个有效的流", nameof(streams));
        }

        SegmentSize = segmentSize;

        CanRead = true;
        CanWrite = true;
        CanSeek = true;
        foreach (var stream in _streams)
        {
            CanRead &= stream.CanRead;
            CanWrite &= stream.CanWrite;
            CanSeek &= stream.CanSeek;
        }

        if (!CanRead && !CanWrite)
        {
            throw new InvalidOperationException("基础流即不全部可读也不全部可写。");
        }
    }

    /// <inheritdoc/>
    public override int Read(Span<byte> buffer)
    {
        if (!CanRead)
        {
            throw new NotSupportedException("流不可读");
        }

        int bytesRead = 0;
        int remaining = buffer.Length; 
        while (remaining > 0)
        {
            int toRead = Math.Min(remaining, SegmentSize - (int)(_position % SegmentSize));
            int bytesReadInThisStream = 0;

            while (bytesReadInThisStream < toRead)
            { 
                var slice = buffer.Slice(bytesRead + bytesReadInThisStream, toRead - bytesReadInThisStream);
                int read = CurrentStream.Read(slice); 
                if (read == 0)
                {
                    _position += bytesReadInThisStream;
                    return bytesRead + bytesReadInThisStream;
                }

                bytesReadInThisStream += read;
            }

            bytesRead += bytesReadInThisStream;
            remaining -= bytesReadInThisStream; 
            _position += bytesReadInThisStream;
        }

        return bytesRead;
    }

    /// <inheritdoc/>
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (!CanWrite)
        {
            throw new NotSupportedException("流不可写");
        }

        int bytesWritten = 0;
        int remaining = buffer.Length;
         
        while (remaining > 0)
        {
            int toWrite = Math.Min(remaining, SegmentSize - (int)(_position % SegmentSize)); 
            var slice = buffer.Slice(bytesWritten, toWrite);
            CurrentStream.Write(slice); 

            bytesWritten += toWrite;
            remaining -= toWrite; 
            _position += toWrite;
        }
    }

    /// <inheritdoc/>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!CanRead)
        {
            throw new NotSupportedException("流不可读");
        }

        int bytesRead = 0;
        int remaining = buffer.Length; 
        while (remaining > 0)
        {
            int toRead = Math.Min(remaining, SegmentSize - (int)(_position % SegmentSize));
            int bytesReadInThisStream = 0;

            while (bytesReadInThisStream < toRead)
            { 
                var slice = buffer.Slice(bytesRead + bytesReadInThisStream, toRead - bytesReadInThisStream);
                int read = await CurrentStream.ReadAsync(slice, cancellationToken).ConfigureAwait(false); 

                if (read == 0)
                {
                    _position += bytesReadInThisStream;
                    return bytesRead + bytesReadInThisStream;
                }

                bytesReadInThisStream += read;
            }

            bytesRead += bytesReadInThisStream;
            remaining -= bytesReadInThisStream; 
            _position += bytesReadInThisStream;
        }

        return bytesRead;
    }

    /// <inheritdoc/>
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!CanWrite)
        {
            throw new NotSupportedException("流不可写");
        }

        int bytesWritten = 0;
        int remaining = buffer.Length; 
        while (remaining > 0)
        {
            int toWrite = Math.Min(remaining, SegmentSize - (int)(_position % SegmentSize)); 
            var slice = buffer.Slice(bytesWritten, toWrite);
            await CurrentStream.WriteAsync(slice, cancellationToken).ConfigureAwait(false); 
            bytesWritten += toWrite;
            remaining -= toWrite; 
            _position += toWrite;
        }
    }

    /// <inheritdoc/>
    public override void Flush()
    {
        foreach (var stream in _streams)
        {
            stream.Flush();
        }
    }

    /// <inheritdoc/>
    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        foreach (var stream in _streams)
        {
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin)
    {
        return Position = origin switch
        {
            _ when !CanSeek => throw new NotSupportedException("流不可跳转"),
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => throw new NotSupportedException("不支持从末尾定位"),
            _ => throw new ArgumentException("无效的 SeekOrigin", nameof(origin)),
        };
    }

    /// <inheritdoc/>
    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // 不关闭任何流，由调用方管理生命周期
        }
        base.Dispose(disposing);
    }
}
