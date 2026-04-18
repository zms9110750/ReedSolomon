using System;
using System.Buffers;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using zms9110750.ReedSolomon.Galos;
using zms9110750.ReedSolomon.Matrixs;
using zms9110750.ReedSolomon.Streams;

namespace zms9110750.ReedSolomon.Tests
{
    public class ReedSolomonDecodeStreamTests
    {
        private readonly ITestOutputHelper _output;

        public ReedSolomonDecodeStreamTests(ITestOutputHelper output)
        {
            _output = output;
        }

        /// <summary>
        /// 测试构造器参数验证
        /// </summary>
        [Fact]
        public void Constructor_Should_Validate_Parameters()
        {
            var encodingMatrix = new VandermondeMatrix8bit(3, 2);
            var recoveryMatrix = encodingMatrix.InverseRows(new[] { 0, 1, 2 });
            var streams = new MemoryStream[] { new MemoryStream(), new MemoryStream(), new MemoryStream() };
            var roundRobin = new StreamRoundRobin(streams, 64);

            // 测试 recoveryMatrix 为 null
            Assert.Throws<ArgumentNullException>(() => new ReedSolomonDecodeStream(null!, roundRobin, 100));

            // 测试 recoveryMatrix 不是方阵
            var nonSquareMatrix = new VandermondeMatrix8bit(3, 2); // 5x3 不是方阵
            Assert.Throws<ArgumentException>(() => new ReedSolomonDecodeStream(nonSquareMatrix, roundRobin, 100));

            // 测试 allShardStreams 为 null
            Assert.Throws<ArgumentNullException>(() => new ReedSolomonDecodeStream(recoveryMatrix, (StreamRoundRobin)null!, 100));

            // 测试分片流数量不匹配
            var wrongCountStreams = new MemoryStream[] { new MemoryStream(), new MemoryStream() };
            var wrongRoundRobin = new StreamRoundRobin(wrongCountStreams, 64);
            Assert.Throws<ArgumentException>(() => new ReedSolomonDecodeStream(recoveryMatrix, wrongRoundRobin, 100));
        }

        /// <summary>
        /// 测试解码流能否正确恢复原始数据
        /// </summary>
        [Theory]
        [InlineData(3, 2, 64, 3 * 64 * 2 + 50, "0")]
        [InlineData(3, 2, 64, 3 * 64 * 2 + 50, "1|2")]
        [InlineData(5, 3, 32, 480, "0|3|4")]
        [InlineData(2, 4, 256, 1, "0")]
        [InlineData(2, 4, 256, 1, "1")]
        [InlineData(1, 1, 128, 100, "0")]
        [InlineData(1, 2, 512, 512 + 128, "1|2")]
        [InlineData(1, 2, 512, 512 + 128, "0|2")]
        [InlineData(1, 2, 512, 512 + 128, "0|1")]
        public async Task DecodeStream_Should_Recover_OriginalData(
            int dataShards, int parityShards, int blockSize, int dataLength, string lostShardsStr)
        {
            _output.WriteLine($"=== 解码测试开始 ===");
            _output.WriteLine($"参数: dataShards={dataShards}, parityShards={parityShards}, blockSize={blockSize}, dataLength={dataLength}");
            _output.WriteLine($"丢失分片: {lostShardsStr}");

            byte[] originalData = new byte[dataLength];
            new Random(42).NextBytes(originalData);

            var encodingMatrix = new VandermondeMatrix8bit(dataShards, parityShards);
            var allShards = EncodeToShards(encodingMatrix, originalData, dataShards, parityShards, blockSize);

            _output.WriteLine($"编码完成，总分片数: {allShards.Length}");

            var lostIndices = lostShardsStr.Split('|').Select(int.Parse).ToArray();
            var availableIndices = Enumerable.Range(0, allShards.Length)
                .Where(i => !lostIndices.Contains(i))
                .Take(dataShards)
                .ToArray();

            _output.WriteLine($"可用分片索引: [{string.Join(", ", availableIndices)}]");

            // 只创建可用分片的流
            var availableStreams = availableIndices.Select(i => new MemoryStream(allShards[i])).ToArray();
            var availableRoundRobin = new StreamRoundRobin(availableStreams, blockSize);

            var recoveryMatrix = encodingMatrix.InverseRows(availableIndices);
            var decodeStream = new ReedSolomonDecodeStream(recoveryMatrix, availableRoundRobin, dataLength);

            byte[] recoveredData = new byte[dataLength];

            int totalBytesRead = 0;
            while (totalBytesRead < dataLength)
            {
                int bytesRead = await decodeStream.ReadAsync(recoveredData, totalBytesRead, dataLength - totalBytesRead);
                if (bytesRead == 0)
                    break;
                totalBytesRead += bytesRead;
            }

            _output.WriteLine($"解码完成，读取字节数: {totalBytesRead}");

            Assert.Equal(dataLength, totalBytesRead);
            Assert.Equal(originalData, recoveredData);

            _output.WriteLine($"=== 解码测试通过 ===");

            foreach (var s in availableStreams)
                s.Dispose();
            decodeStream.Dispose();
        }

        /// <summary>
        /// 测试同步读取
        /// </summary>
        [Theory]
        [InlineData(3, 2, 64, 3 * 64 * 2 + 50, "0")]
        [InlineData(1, 1, 128, 100, "0")]
        public void DecodeStream_SyncRead_Should_Recover_OriginalData(
            int dataShards, int parityShards, int blockSize, int dataLength, string lostShardsStr)
        {
            _output.WriteLine($"=== 同步解码测试开始 ===");

            byte[] originalData = new byte[dataLength];
            new Random(42).NextBytes(originalData);

            var encodingMatrix = new VandermondeMatrix8bit(dataShards, parityShards);
            var allShards = EncodeToShards(encodingMatrix, originalData, dataShards, parityShards, blockSize);

            var lostIndices = lostShardsStr.Split('|').Select(int.Parse).ToArray();
            var availableIndices = Enumerable.Range(0, allShards.Length)
                .Where(i => !lostIndices.Contains(i))
                .Take(dataShards)
                .ToArray();

            var availableStreams = availableIndices.Select(i => new MemoryStream(allShards[i])).ToArray();
            var availableRoundRobin = new StreamRoundRobin(availableStreams, blockSize);

            var recoveryMatrix = encodingMatrix.InverseRows(availableIndices);
            using var decodeStream = new ReedSolomonDecodeStream(recoveryMatrix, availableRoundRobin, dataLength);

            byte[] recoveredData = new byte[dataLength];
            int totalBytesRead = 0;

            while (totalBytesRead < dataLength)
            {
                int bytesRead = decodeStream.Read(recoveredData, totalBytesRead, dataLength - totalBytesRead);
                if (bytesRead == 0)
                    break;
                totalBytesRead += bytesRead;
            }

            Assert.Equal(dataLength, totalBytesRead);
            Assert.Equal(originalData, recoveredData);

            foreach (var s in availableStreams)
                s.Dispose();
        }

        /// <summary>
        /// 测试 CopyTo 方法
        /// </summary>
        [Fact]
        public async Task DecodeStream_CopyTo_Should_Work()
        {
            int dataShards = 3, parityShards = 2, blockSize = 64, dataLength = 434;
            string lostShardsStr = "0";

            byte[] originalData = new byte[dataLength];
            new Random(42).NextBytes(originalData);

            var encodingMatrix = new VandermondeMatrix8bit(dataShards, parityShards);
            var allShards = EncodeToShards(encodingMatrix, originalData, dataShards, parityShards, blockSize);

            var lostIndices = lostShardsStr.Split('|').Select(int.Parse).ToArray();
            var availableIndices = Enumerable.Range(0, allShards.Length)
                .Where(i => !lostIndices.Contains(i))
                .Take(dataShards)
                .ToArray();

            var availableStreams = availableIndices.Select(i => new MemoryStream(allShards[i])).ToArray();
            var availableRoundRobin = new StreamRoundRobin(availableStreams, blockSize);

            var recoveryMatrix = encodingMatrix.InverseRows(availableIndices);
            using var decodeStream = new ReedSolomonDecodeStream(recoveryMatrix, availableRoundRobin, dataLength);
            using var outputStream = new MemoryStream();

            await decodeStream.CopyToAsync(outputStream, TestContext.Current.CancellationToken);

            Assert.Equal(originalData.Length, outputStream.Length);
            Assert.Equal(originalData, outputStream.ToArray());

            foreach (var s in availableStreams)
                s.Dispose();
        }

        /// <summary>
        /// 测试读取超出剩余长度的情况
        /// </summary>
        [Fact]
        public async Task ReadAsync_Should_Not_Read_Beyond_RemainLength()
        {
            int dataShards = 3, parityShards = 2, blockSize = 64, dataLength = 100;
            string lostShardsStr = "0";

            byte[] originalData = new byte[dataLength];
            new Random(42).NextBytes(originalData);

            var encodingMatrix = new VandermondeMatrix8bit(dataShards, parityShards);
            var allShards = EncodeToShards(encodingMatrix, originalData, dataShards, parityShards, blockSize);

            var lostIndices = lostShardsStr.Split('|').Select(int.Parse).ToArray();
            var availableIndices = Enumerable.Range(0, allShards.Length)
                .Where(i => !lostIndices.Contains(i))
                .Take(dataShards)
                .ToArray();

            var availableStreams = availableIndices.Select(i => new MemoryStream(allShards[i])).ToArray();
            var availableRoundRobin = new StreamRoundRobin(availableStreams, blockSize);

            var recoveryMatrix = encodingMatrix.InverseRows(availableIndices);
            using var decodeStream = new ReedSolomonDecodeStream(recoveryMatrix, availableRoundRobin, dataLength);

            byte[] buffer = new byte[200];
            int bytesRead = await decodeStream.ReadAsync(buffer, 0, 200);

            Assert.Equal(dataLength, bytesRead);

            foreach (var s in availableStreams)
                s.Dispose();
        }

        /// <summary>
        /// 编码生成所有分片（数据分片 + 冗余分片）
        /// </summary>
        private byte[][] EncodeToShards(IMatrix encodingMatrix, byte[] originalData, int dataShards, int parityShards, int blockSize)
        {
            int chunkSize = dataShards * blockSize;
            int fullChunks = originalData.Length / chunkSize;
            int lastChunkLen = originalData.Length % chunkSize;
            int totalChunks = fullChunks + (lastChunkLen > 0 ? 1 : 0);
            int shardLength = totalChunks * blockSize;

            byte[][] shards = new byte[dataShards + parityShards][];
            for (int i = 0; i < shards.Length; i++)
            {
                shards[i] = new byte[shardLength];
            }

            for (int chunk = 0; chunk < fullChunks; chunk++)
            {
                int chunkOffset = chunk * chunkSize;
                byte[] input = new byte[chunkSize];
                Array.Copy(originalData, chunkOffset, input, 0, chunkSize);

                byte[] output = new byte[parityShards * blockSize];
                encodingMatrix.CodeShards(input, output, blockSize);

                for (int i = 0; i < dataShards; i++)
                {
                    Array.Copy(input, i * blockSize, shards[i], chunk * blockSize, blockSize);
                }
                for (int i = 0; i < parityShards; i++)
                {
                    Array.Copy(output, i * blockSize, shards[dataShards + i], chunk * blockSize, blockSize);
                }
            }

            if (lastChunkLen > 0)
            {
                byte[] input = new byte[chunkSize];
                Array.Copy(originalData, fullChunks * chunkSize, input, 0, lastChunkLen);

                byte[] output = new byte[parityShards * blockSize];
                encodingMatrix.CodeShards(input, output, blockSize);

                for (int i = 0; i < dataShards; i++)
                {
                    int copyLen = Math.Min(blockSize, lastChunkLen - i * blockSize);
                    if (copyLen > 0)
                    {
                        Array.Copy(input, i * blockSize, shards[i], fullChunks * blockSize, copyLen);
                    }
                }
                for (int i = 0; i < parityShards; i++)
                {
                    Array.Copy(output, i * blockSize, shards[dataShards + i], fullChunks * blockSize, blockSize);
                }
            }

            return shards;
        }
    }
}