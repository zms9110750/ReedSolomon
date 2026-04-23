using System.Collections;
using System.Collections.Immutable;

namespace zms9110750.ReedSolomon.Streams;
/// <summary>
/// 流轮换器：循环使用多个底层流，每写入/读取指定字节数自动切换到下一个流
/// </summary>
public class StreamRoundRobin : SpanCapableStreamBase
{
    private readonly ImmutableList<Stream> _streams;
    private long _position;
    /// <summary>释放对象后，true 将流对象保持打开状态;false将一起释放底层流</summary>
    private bool _leaveOpen;

    /// <summary>标识已经结束的流</summary>
    private BitArray _streamEndedFlags;
    /// <summary>
    /// 获取当前Segment中剩余的字节数（从当前位置到当前Segment末尾的距离）
    /// </summary>
    private int RemainingInCurrentSegment => SegmentSize - (int)(_position % SegmentSize);
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
    private Stream CurrentStream => _streams[CurrentStreamIndex];

    /// <summary>当前正在使用的流索引</summary>
    private int CurrentStreamIndex => (int)((_position / SegmentSize) % _streams.Count);

    /// <summary>
    /// 初始化流轮换器
    /// </summary>
    /// <param name="streams">底层流集合（循环使用，不会关闭）</param>
    /// <param name="segmentSize">每个流操作的字节数，达到后自动切换</param>
    /// <param name="leaveOpen">释放对象后，true 将流对象保持打开状态;false将一起释放底层流</param>
    /// <param name="model">在部分基础流结束时的读取模式。</param>
    public StreamRoundRobin(IEnumerable<Stream> streams, int segmentSize, bool leaveOpen = false)
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
        foreach (var stream in _streams)
        {
            CanRead &= stream.CanRead;
            CanWrite &= stream.CanWrite;
        }

        if (!CanRead && !CanWrite)
        {
            throw new InvalidOperationException("基础流即不全部可读也不全部可写。");
        }

        _leaveOpen = leaveOpen;
        _streamEndedFlags = new BitArray(_streams.Count);
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
        // 检查流是否可读，不可读则抛出异常
        if (!CanRead)
        {
            throw new NotSupportedException("流不可读");
        }

        // 已成功读取的总字节数
        int bytesRead = 0;
        // 剩余需要读取的字节数（缓冲区剩余空间）
        int remaining = buffer.Length;

        // 当还有剩余空间时继续读取
        while (remaining > 0)
        {
            // 本次在当前块中最多可读取的字节数
            // 限制条件：不超过剩余需求，且不超过当前SegmentSize块的边界
            // SegmentSize - (position % SegmentSize) 计算到当前块末尾的距离
            int toRead = Math.Min(remaining, RemainingInCurrentSegment);

            // 本次循环中从当前流实际读取的字节数
            int bytesReadInThisStream = 0;

            // 持续从当前流读取，直到达到本次计划的读取量(toRead)
            while (bytesReadInThisStream < toRead)
            {
                // 计算当前需要读取的缓冲区切片
                // bytesRead: 之前已读取的总字节数
                // bytesReadInThisStream: 本次流中已读取的字节数
                // 切片范围从总偏移开始，长度为剩余未读部分
                var slice = buffer.Slice(bytesRead, toRead).Slice(bytesReadInThisStream);

                // 从当前流异步读取数据到切片中
                int read = await CurrentStream.ReadAsync(slice, cancellationToken).ConfigureAwait(false);

                // 如果读到0字节，表示当前流已到达末尾
                if (read == 0)
                {
#if NET8_0_OR_GREATER
                    if (_streamEndedFlags.HasAllSet())
#else
                    if (_streamEndedFlags.Cast<bool>().All(flag => flag))
#endif
                    {
                        // 更新总位置偏移（只加上本次已读取的部分）
                        _position += bytesReadInThisStream;
                        // 返回目前已读取的总字节数（包括之前循环读取的）
                        return bytesRead + bytesReadInThisStream;
                    }
                    else
                    {
                        _streamEndedFlags.Set(CurrentStreamIndex, true);
                        slice.Span.Clear();
                        read = slice.Length;
                    }
                }
                // 累加本次从当前流读取的字节数
                bytesReadInThisStream += read;
            }

            // 累加总读取字节数
            bytesRead += bytesReadInThisStream;
            // 减少剩余需要读取的字节数
            remaining -= bytesReadInThisStream;
            // 更新全局位置偏移（累积已读取的总字节数）
            _position += bytesReadInThisStream;
        }

        // 成功读取完所有请求的字节数，返回总读取量
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
            if (!_leaveOpen)
            {
                foreach (var stream in _streams)
                {
                    stream.Dispose();
                }
            }
        }
        base.Dispose(disposing);
    }
#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP2_1_OR_GREATER 
    /// <inheritdoc/>
    public override async ValueTask DisposeAsync()
    {
        if (!_leaveOpen)
        {
            foreach (var stream in _streams)
            {
                await stream.DisposeAsync();
            }
        }
        await base.DisposeAsync();
    }
#endif

}
