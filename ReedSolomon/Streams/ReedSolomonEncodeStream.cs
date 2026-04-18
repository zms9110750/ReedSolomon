using System;
using System.Buffers;
using System.Collections.Immutable;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace zms9110750.ReedSolomon.Streams
{
    /// <summary>
    /// Reed-Solomon 编码流
    /// </summary>
    public class ReedSolomonEncodeStream : SpanCapableStreamBase
    {
        /// <summary>当前流位置</summary>
        private long _position;

        /// <summary>是否已释放</summary>
        private bool _disposed;

        /// <summary>编码矩阵</summary>
        private IMatrix EncodingMatrix { get; }

        /// <summary>冗余分片输出流集合</summary>
        private StreamRoundRobin ParityStreams { get; }

        /// <summary>原始数据分片轮询输出流（可选，为null时只输出冗余分片）</summary>
        private Stream? OriginalStream { get; }

        /// <summary>数据分片数量（K）</summary>
        public int DataShards { get; }

        /// <summary>冗余分片数量（M）</summary>
        public int ParityShards { get; }

        /// <summary>每个分片的轮询字节数</summary>
        public int BlockSize => ParityStreams.SegmentSize;

        /// <summary>每次编码的数据块大小（DataShards * BlockSize）</summary>
        public int ChunkSize => DataShards * BlockSize;

        /// <summary>每次编码输出的冗余数据大小（ParityShards * BlockSize）</summary>
        public int OutputSize => ParityShards * BlockSize;

        /// <summary>Pipe用于缓冲输入数据</summary>
        private Pipe Pipe { get; }

        /// <summary>Pipe写入器的流包装</summary> 
        private Stream PipeStream { get; }

        /// <summary>Pipe读取器</summary>
        private PipeReader PipeReader => Pipe.Reader;

        /// <inheritdoc/>
        public override bool CanRead => false;

        /// <inheritdoc/>
        public override bool CanWrite => true;

        /// <inheritdoc/>
        public override bool CanSeek => false;

        /// <inheritdoc/>
        public override long Length => throw new NotSupportedException();

        /// <inheritdoc/>
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        /// <summary>
        /// 初始化编码流
        /// </summary>
        /// <param name="encodingMatrix">编码矩阵（非方阵）</param>
        /// <param name="parityStreams">冗余分片输出流集合，数量为 矩阵的(行-列) 数</param> 
        /// <param name="originalStream">原始数据输出流</param>
        public ReedSolomonEncodeStream(IMatrix encodingMatrix, StreamRoundRobin parityStreams, Stream? originalStream)
        {
            if (encodingMatrix == null)
            {
                throw new ArgumentNullException(nameof(encodingMatrix));
            }
            if (encodingMatrix.IsSquare)
            {
                throw new ArgumentException("编码矩阵必须是非方阵（行数 > 列数）", nameof(encodingMatrix));
            }
            if (parityStreams == null)
            {
                throw new ArgumentNullException(nameof(parityStreams));
            }

            EncodingMatrix = encodingMatrix;
            DataShards = encodingMatrix.Columns;
            ParityShards = encodingMatrix.Rows - encodingMatrix.Columns;

            if (parityStreams.StreamsCount != ParityShards)
            {
                throw new ArgumentException($"冗余流数量应为 {ParityShards}，实际 {parityStreams.StreamsCount}", nameof(parityStreams));
            }
            ParityStreams = parityStreams;
            Pipe = new Pipe(new PipeOptions(minimumSegmentSize: Math.Max(4096, Math.Min(ChunkSize, 65536))));
            PipeStream = Pipe.Writer.AsStream();
            OriginalStream = originalStream;
        }
        /// <summary>
        /// 初始化编码流
        /// </summary>
        /// <param name="encodingMatrix">编码矩阵（非方阵）</param>
        /// <param name="parityStreams">冗余分片输出流集合，数量为 矩阵的(行-列) 数</param>
        /// <param name="blockSize">每个分片的轮询字节数</param>
        /// <param name="originalStream">原始数据输出流</param>
        public ReedSolomonEncodeStream(IMatrix encodingMatrix, IReadOnlyList<Stream> parityStreams, int blockSize, Stream? originalStream)
            : this(encodingMatrix, new StreamRoundRobin(parityStreams, blockSize), originalStream)
        {
        }

        /// <summary>
        /// 初始化编码流
        /// </summary>
        /// <param name="encodingMatrix">编码矩阵</param>
        /// <param name="parityStreams">冗余分片输出流集合，数量为 矩阵的(行-列) 数</param>
        /// <param name="dataRoundRobin">原始数据分片轮询输出流</param>
        public ReedSolomonEncodeStream(IMatrix encodingMatrix, IReadOnlyList<Stream> parityStreams, StreamRoundRobin dataRoundRobin)
            : this(encodingMatrix, new StreamRoundRobin(parityStreams, dataRoundRobin?.SegmentSize ?? throw new ArgumentNullException(nameof(dataRoundRobin))), dataRoundRobin)
        {
        }
        /// <inheritdoc/>
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(ReedSolomonEncodeStream));
            }
            PipeStream.Write(buffer);
            _position += buffer.Length;
            Flush();
        }
        /// <inheritdoc/>
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(ReedSolomonEncodeStream));
            }
            await PipeStream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            _position += buffer.Length;
            await FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc/>  
        public override void Flush()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(ReedSolomonEncodeStream));
            }
            // 同步Flush不需要等待异步写入完成，直接调用同步写入
            if (!PipeReader.TryRead(out var readResult))
            {
                return;
            }
            else if (readResult.Buffer.Length < ChunkSize)
            {
                PipeReader.AdvanceTo(readResult.Buffer.Start );
                return;
            }

            ReadOnlySequence<byte> pipeBuffer = readResult.Buffer;
            var position = pipeBuffer.Start;
            byte[] poolBuffer = ArrayPool<byte>.Shared.Rent(ChunkSize + OutputSize);
            Span<byte> inputSpan = poolBuffer.AsSpan(0, ChunkSize);
            Span<byte> outputSpan = poolBuffer.AsSpan(ChunkSize, OutputSize);

            try
            {
                while (pipeBuffer.Length >= ChunkSize)
                {
                    var chunk = pipeBuffer.Slice(0, ChunkSize);
                    ReadOnlySpan<byte> chunkSpan;
                    if (chunk.IsSingleSegment)
                    {
                        chunkSpan = chunk.First.Span;
                    }
                    else
                    {
                        chunk.CopyTo(inputSpan);
                        chunkSpan = inputSpan;
                    }

                    EncodingMatrix.CodeShards(chunkSpan, outputSpan, BlockSize);

                    if (OriginalStream != null)
                    {
                        OriginalStream.Write(chunkSpan);
                    }

                    ParityStreams.Write(outputSpan);
                    position = chunk.End;
                    pipeBuffer = pipeBuffer.Slice(position);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(poolBuffer);
                PipeReader.AdvanceTo(position);
                ParityStreams.Flush();
            }
        }

        /// <inheritdoc/>
        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(ReedSolomonEncodeStream));
            }
            if (!PipeReader.TryRead(out var readResult))
            {
                return;
            }
            else if (readResult.Buffer.Length < ChunkSize)
            {
                PipeReader.AdvanceTo(readResult.Buffer.Start);
                return;
            }

            ReadOnlySequence<byte> pipeBuffer = readResult.Buffer;
            SequencePosition position = pipeBuffer.Start;
            byte[] poolBuffer = ArrayPool<byte>.Shared.Rent(ChunkSize + OutputSize);
            Memory<byte> inputMemory = poolBuffer.AsMemory(0, ChunkSize);
            Memory<byte> outputMemory = poolBuffer.AsMemory(ChunkSize, OutputSize);
            try
            {
                while (pipeBuffer.Length >= ChunkSize && !cancellationToken.IsCancellationRequested)
                {
                    var chunk = pipeBuffer.Slice(0, ChunkSize);
                    ReadOnlyMemory<byte> chunkSpan;
                    if (chunk.IsSingleSegment)
                    {
                        chunkSpan = chunk.First;
                    }
                    else
                    {
                        chunk.First.Span.CopyTo(inputMemory.Span);
                        chunkSpan = inputMemory;
                    }
                    EncodingMatrix.CodeShards(chunkSpan.Span, outputMemory.Span, BlockSize);
                    if (OriginalStream != null)
                    {
                        await OriginalStream.WriteAsync(chunkSpan, cancellationToken);
                    }
                    await ParityStreams.WriteAsync(outputMemory, cancellationToken);

                    position = chunk.End;
                    pipeBuffer = pipeBuffer.Slice(position);
                }
                await ParityStreams.FlushAsync(cancellationToken).ConfigureAwait(false); 
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(poolBuffer);
                PipeReader.AdvanceTo(position);
            }
        }


        /// <inheritdoc/> 
        protected override void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                Flush();

                // 读取Pipe中剩余的残片（不足一个Chunk）
                if (PipeReader.TryRead(out var readResult) && readResult.Buffer.Length > 0)
                {
                    var remaining = readResult.Buffer;
                    byte[] poolBuffer = ArrayPool<byte>.Shared.Rent(ChunkSize + OutputSize);
                    Span<byte> inputSpan = poolBuffer.AsSpan(0, ChunkSize);
                    Span<byte> outputSpan = poolBuffer.AsSpan(ChunkSize, OutputSize);

                    try
                    {
                        // 剩余不足一个Chunk，用零填充
                        inputSpan.Clear();

                        // 复制剩余数据到连续缓冲区
                        remaining.CopyTo(inputSpan);

                        // 编码
                        EncodingMatrix.CodeShards(inputSpan, outputSpan, BlockSize);

                        // 写入原始数据
                        if (OriginalStream != null)
                        {
                            OriginalStream.Write(inputSpan.Slice(0, (int)remaining.Length));
                        }
                        ParityStreams.Write(outputSpan);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(poolBuffer);
                    }

                    PipeReader.AdvanceTo(remaining.End);
                }

                Pipe.Writer.Complete();
                PipeReader.Complete();

                if (OriginalStream != null)
                {
                    OriginalStream.Flush();
                }
                ParityStreams.Flush();
            }
            _disposed = true;
            base.Dispose(disposing);
        }

#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP2_1_OR_GREATER
        /// <inheritdoc/>
        public override async ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true;

                await FlushAsync().ConfigureAwait(false);

                // 读取Pipe中剩余的残片（不足一个Chunk）
                var readResult = await PipeReader.ReadAsync().ConfigureAwait(false);
                var remaining = readResult.Buffer;

                if (remaining.Length > 0)
                {
                    byte[] poolBuffer = ArrayPool<byte>.Shared.Rent(ChunkSize + OutputSize);
                    Memory<byte> inputMemory = poolBuffer.AsMemory(0, ChunkSize);
                    Memory<byte> outputMemory = poolBuffer.AsMemory(ChunkSize, OutputSize);

                    try
                    {
                        //填充0
                        inputMemory.Span.Clear();

                        // 复制剩余数据到连续内存
                        remaining.CopyTo(inputMemory.Span);

                        // 编码
                        EncodingMatrix.CodeShards(inputMemory.Span, outputMemory.Span, BlockSize);

                        // 写入原始数据
                        if (OriginalStream != null)
                        {
                            await OriginalStream.WriteAsync(inputMemory.Slice(0, (int)remaining.Length)).ConfigureAwait(false);
                        }

                        // 写入冗余分片 
                        await ParityStreams.WriteAsync(outputMemory).ConfigureAwait(false);
                        PipeReader.AdvanceTo(remaining.End);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(poolBuffer);
                    }

                }

                Pipe.Writer.Complete();
                PipeReader.Complete();

                if (OriginalStream != null)
                {
                    await OriginalStream.FlushAsync().ConfigureAwait(false);
                }
                await ParityStreams.FlushAsync().ConfigureAwait(false);
            }
            await base.DisposeAsync().ConfigureAwait(false);
        }
#endif
        /// <inheritdoc/>
        public override int Read(Span<byte> buffer)
        {
            throw new NotImplementedException();
        }

        /// <inheritdoc/>
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
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
}