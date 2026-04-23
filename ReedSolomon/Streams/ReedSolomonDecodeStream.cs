
using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;

namespace zms9110750.ReedSolomon.Streams;
/// <summary>
/// Reed-Solomon 解码流
/// </summary>
public class ReedSolomonDecodeStream : SpanCapableStreamBase
{
    /// <summary>当前流位置</summary>
    private long _position;

    /// <summary>是否已释放</summary>
    private bool _disposed;

    /// <summary>释放对象后，true 将流对象保持打开状态;false将一起释放底层流</summary>
    private bool _leaveOpen;

    /// <summary>恢复矩阵</summary>
    private IMatrix RecoveryMatrix { get; }

    /// <summary>所有分片流（轮询读取）</summary>
    private StreamRoundRobin AllShardStreams { get; }

    /// <summary>数据分片数量（K）</summary>
    public int DataShards { get; }

    /// <summary>每个分片的轮询字节数</summary>
    public int BlockSize { get; }

    /// <summary>每次解码的数据块大小（DataShards * BlockSize）</summary>
    private int ChunkSize => DataShards * BlockSize;
    /// <summary>缓存已解码的数据的Pipe</summary>
    private Pipe Pipe { get; }

    /// <summary>缓存已解码的数据的Pipe的写入器流包装</summary> 
    private Stream PipeStream { get; }

    /// <summary>缓存已解码的数据的Pipe的读取器</summary>
    private PipeReader PipeReader => Pipe.Reader;

    /// <summary>所有分片流的Pipe读取器</summary>
    private PipeReader AllShardReader { get; }

    /// <inheritdoc/>
    public override bool CanRead => true;

    /// <inheritdoc/>
    public override bool CanWrite => false;

    /// <inheritdoc/>
    public override bool CanSeek => false;

    /// <inheritdoc/>
    public override long Length { get; }

    /// <summary>剩余长度 ( Length - Position)</summary>
    public long RemainLength => Length - Position;

    /// <inheritdoc/>
    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    /// <summary>
    /// 初始化解码流
    /// </summary>
    /// <param name="recoveryMatrix">恢复矩阵（方阵，大小应为 dataShards × dataShards）</param>
    /// <param name="allShardStreams">所有分片流（顺序：前K个数据分片，后M个冗余分片）</param>
    /// <param name="blockSize">每个分片的轮询字节数</param>
    /// <param name="length">流的总长度</param>
    /// <param name="leaveOpen">释放对象后，true 将流对象保持打开状态;false将一起释放底层流</param>
    public ReedSolomonDecodeStream(IMatrix recoveryMatrix, IReadOnlyList<Stream> allShardStreams, int blockSize, long length, bool leaveOpen = false)
        : this(recoveryMatrix, new StreamRoundRobin(allShardStreams, blockSize), length, leaveOpen)
    {
    }

    /// <summary>
    /// 初始化解码流
    /// </summary>
    /// <param name="recoveryMatrix">恢复矩阵（方阵，大小应为 dataShards × dataShards）</param>
    /// <param name="allShardStreams">所有分片流（顺序：前K个数据分片，后M个冗余分片）</param>
    /// <param name="length">流的总长度</param>
    /// <param name="leaveOpen">释放对象后，true 将流对象保持打开状态;false将一起释放底层流</param>
    public ReedSolomonDecodeStream(IMatrix recoveryMatrix, StreamRoundRobin allShardStreams, long length, bool leaveOpen = false)
    {
        if (recoveryMatrix == null)
        {
            throw new ArgumentNullException(nameof(recoveryMatrix));
        }
        if (!recoveryMatrix.IsSquare)
        {
            throw new ArgumentException("恢复矩阵必须是方阵", nameof(recoveryMatrix));
        }
        if (allShardStreams == null)
        {
            throw new ArgumentNullException(nameof(allShardStreams));
        }
        if (allShardStreams.StreamsCount != recoveryMatrix.Rows)
        {
            throw new ArgumentException($"分片流数量应为 {recoveryMatrix.Rows}，实际 {allShardStreams.StreamsCount}", nameof(allShardStreams));
        }
        _leaveOpen = leaveOpen;
        DataShards = recoveryMatrix.Rows;
        BlockSize = allShardStreams.SegmentSize;
        RecoveryMatrix = recoveryMatrix;
        AllShardStreams = allShardStreams;
        AllShardReader = PipeReader.Create(AllShardStreams);
        Length = length;
        Pipe = new Pipe(new PipeOptions(minimumSegmentSize: Math.Min(Math.Max(ChunkSize, 4096), 65536), pauseWriterThreshold: 0));
        PipeStream = Pipe.Writer.AsStream();
    }

    /// <inheritdoc/>
    /// <remarks>同步方法使用了异步无await的等待。可能死锁。尽可能使用异步方法。</remarks>
    public override int Read(Span<byte> buffer)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ReedSolomonDecodeStream));
        }
        if (buffer.Length > RemainLength)
        {
            buffer = buffer.Slice(0, (int)RemainLength);
        }
        if (buffer.Length == 0)
        {
            return 0;
        }

        byte[] poolBuffer = ArrayPool<byte>.Shared.Rent(ChunkSize << 1);

        try
        {
            int bytesRead = 0;
            Memory<byte> outputMemory = poolBuffer.AsMemory(0, ChunkSize);
            Memory<byte> inputBufferMemory = poolBuffer.AsMemory(ChunkSize, ChunkSize);
            while (true)
            {
                if (PipeReader.TryRead(out var readResult))
                {
                    if (readResult.Buffer.Length >= buffer.Length)
                    {
                        var data = readResult.Buffer.Slice(0, buffer.Length);
                        data.CopyTo(buffer);
                        PipeReader.AdvanceTo(data.End);
                        return buffer.Length;
                    }
                    else
                    {
                        PipeReader.AdvanceTo(readResult.Buffer.Start, readResult.Buffer.End);
                    }
                }
                var shardBuffer = (AllShardReader.ReadAtLeastAsync(ChunkSize).Result).Buffer;
                if (shardBuffer.Length > ChunkSize)
                {
                    shardBuffer = shardBuffer.Slice(0, ChunkSize);
                }
                ReadOnlyMemory<byte> chunkMemory;
                switch ((integrity: shardBuffer.Length == ChunkSize, shardBuffer.IsSingleSegment))
                {
                    case { integrity: true, IsSingleSegment: true }:
                        chunkMemory = shardBuffer.First;
                        break;
                    case { integrity: false } when shardBuffer.Length < RemainLength:
                        throw new EndOfStreamException("分片流过早结束，无法读取足够的数据进行解码");
                    case { integrity: false }:
                        inputBufferMemory.Span.Clear();
                        goto default;
                    default:
                        shardBuffer.CopyTo(inputBufferMemory.Span);
                        chunkMemory = inputBufferMemory;
                        break;
                }
                RecoveryMatrix.CodeShards(chunkMemory.Span, outputMemory.Span, BlockSize);
                PipeStream.Write(outputMemory.Span);
                AllShardReader.AdvanceTo(shardBuffer.End);
                _position += shardBuffer.Length;
                bytesRead += (int)shardBuffer.Length;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(poolBuffer);
        }
    }

    /// <inheritdoc/>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ReedSolomonDecodeStream));
        }
        if (buffer.Length > RemainLength)
        {
            buffer = buffer.Slice(0, (int)RemainLength);
        }
        if (buffer.Length == 0)
        {
            return 0;
        }

        byte[] poolBuffer = ArrayPool<byte>.Shared.Rent(ChunkSize << 1);
        var remainLength = RemainLength;
        try
        {

            Memory<byte> outputMemory = poolBuffer.AsMemory(0, ChunkSize);
            Memory<byte> inputBufferMemory = poolBuffer.AsMemory(ChunkSize, ChunkSize);
            while (true)
            {

                if (PipeReader.TryRead(out var readResult))
                {
                    if (readResult.Buffer.Length >= buffer.Length)
                    {
                        var data = readResult.Buffer.Slice(0, buffer.Length);
                        try
                        {
                            data.CopyTo(buffer.Span);
                            _position += data.Length;
                            return buffer.Length;
                        }
                        finally
                        {
                            PipeReader.AdvanceTo(data.End);
                        }
                    }
                    else
                    {
                        PipeReader.AdvanceTo(readResult.Buffer.Start, readResult.Buffer.End);
                    }
                }
                var shardBuffer = (await AllShardReader.ReadAtLeastAsync(ChunkSize, cancellationToken).ConfigureAwait(false)).Buffer;
                if (shardBuffer.Length > ChunkSize)
                {
                    shardBuffer = shardBuffer.Slice(0, ChunkSize);
                }
                ReadOnlyMemory<byte> chunkMemory;

                switch ((integrity: shardBuffer.Length == ChunkSize, shardBuffer.IsSingleSegment))
                {
                    case { integrity: true, IsSingleSegment: true }:
                        chunkMemory = shardBuffer.First;
                        break;
                    case { integrity: false } when shardBuffer.Length < remainLength:
                        throw new EndOfStreamException("分片流过早结束，无法读取足够的数据进行解码");
                    case { integrity: false }:
                        inputBufferMemory.Span.Clear();
                        goto default;
                    default:
                        shardBuffer.CopyTo(inputBufferMemory.Span);
                        chunkMemory = inputBufferMemory;
                        break;
                }

                RecoveryMatrix.CodeShards(chunkMemory.Span, outputMemory.Span, BlockSize);
                await PipeStream.WriteAsync(outputMemory, cancellationToken).ConfigureAwait(false);

                remainLength -= (int)Math.Min(remainLength, shardBuffer.Length);
                AllShardReader.AdvanceTo(shardBuffer.End);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(poolBuffer);
        }
    }

    /// <inheritdoc/>
    public override void Flush()
    {
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing && !_leaveOpen)
        {
            AllShardStreams.Dispose();
            Pipe.Writer.Complete();
            PipeReader.Complete(); 
        }
        _disposed = true;
        base.Dispose(disposing);
    }

    /// <inheritdoc/>
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        throw new NotSupportedException();
    }
    /// <inheritdoc/> 
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }
    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    /// <inheritdoc/>
    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }
}