using Microsoft.VisualStudio.TestPlatform.Utilities;
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
    public class ReedSolomonEncodeStreamTests(ITestOutputHelper _output)
    {
        /// <summary>
        /// 测试流编码与直接矩阵编码结果是否一致
        /// </summary>
        /// <param name="dataShards">数据分片数</param>
        /// <param name="parityShards">冗余分片数</param>
        /// <param name="blockSize">块大小</param>
        /// <param name="dataLength">原始数据长度</param>
        [Theory]
        [InlineData(3, 2, 64, 3 * 64 * 2 + 50)]
        [InlineData(1, 1, 128, 100)]
        [InlineData(5, 3, 32, 5 * 32 * 3)]
        [InlineData(2, 4, 256, 1)]
        [InlineData(2, 1, 7, 11)]           // 质数组合
        [InlineData(3, 2, 13, 17)]          // 质数组合
        [InlineData(5, 3, 19, 23)]          // 质数组合
        [InlineData(7, 5, 29, 31)]          // 质数组合
        public void EncodeStream_Should_Produce_Same_Result_As_MatrixDirect(
            int dataShards, int parityShards, int blockSize, int dataLength)
        {
            _output.WriteLine($"=== 测试开始 ===");
            _output.WriteLine($"参数: dataShards={dataShards}, parityShards={parityShards}, blockSize={blockSize}, dataLength={dataLength}");
            _output.WriteLine($"ChunkSize = {dataShards * blockSize}");
            _output.WriteLine($"完整块数 = {dataLength / (dataShards * blockSize)}");
            _output.WriteLine($"最后一块字节数 = {dataLength % (dataShards * blockSize)}");

            // 生成随机测试数据
            byte[] originalData = new byte[dataLength];
            new Random(42).NextBytes(originalData);

            // 创建编码矩阵
            var encodingMatrix = new VandermondeMatrix8bit(dataShards, parityShards);
            _output.WriteLine($"编码矩阵: Rows={encodingMatrix.Rows}, Columns={encodingMatrix.Columns}");

            // 方法1：直接用矩阵编码
            _output.WriteLine("");
            _output.WriteLine("--- 直接矩阵编码 ---");
            byte[][] directResult = DirectEncodeWithMatrix(encodingMatrix, originalData, dataShards, blockSize);

            // 方法2：用流编码
            _output.WriteLine("");
            _output.WriteLine("--- 流编码 ---");
            byte[][] streamResult = EncodeWithStream(encodingMatrix, originalData, dataShards, parityShards, blockSize);

            // 验证结果一致
            _output.WriteLine("");
            _output.WriteLine("--- 验证结果 ---");
            Assert.Equal(directResult.Length, streamResult.Length);
            _output.WriteLine($"冗余分片数量: {directResult.Length}");

            for (int i = 0; i < directResult.Length; i++)
            {
                _output.WriteLine($"分片 {i}: 期望长度={directResult[i].Length}, 实际长度={streamResult[i].Length}");
                Assert.Equal(directResult[i], streamResult[i]);
            }

            _output.WriteLine("=== 测试通过 ===");
        }

        /// <summary>
        /// 直接使用矩阵编码
        /// </summary>
        private byte[][] DirectEncodeWithMatrix(IMatrix encodingMatrix, byte[] originalData, int dataShards, int blockSize)
        {
            int chunkSize = dataShards * blockSize;
            int fullChunks = originalData.Length / chunkSize;
            int lastChunkLen = originalData.Length % chunkSize;
            int parityShards = encodingMatrix.Rows - encodingMatrix.Columns;

            _output.WriteLine($"ChunkSize={chunkSize}, fullChunks={fullChunks}, lastChunkLen={lastChunkLen}");

            // 输出长度按 blockSize 对齐
            int alignedLength = ((originalData.Length + chunkSize - 1) / chunkSize) * blockSize;
            _output.WriteLine($"对齐后每个冗余分片长度: {alignedLength}");

            byte[][] result = new byte[parityShards][];
            for (int i = 0; i < parityShards; i++)
            {
                result[i] = new byte[alignedLength];
            }

            // 处理完整的 Chunk
            for (int chunk = 0; chunk < fullChunks; chunk++)
            {
                int chunkOffset = chunk * chunkSize;
                byte[] input = new byte[chunkSize];
                Array.Copy(originalData, chunkOffset, input, 0, chunkSize);

                byte[] output = new byte[parityShards * blockSize];
                encodingMatrix.CodeShards(input, output, blockSize);

                _output.WriteLine($"完整块 {chunk}: 输入长度={input.Length}, 输出长度={output.Length}");

                for (int i = 0; i < parityShards; i++)
                {
                    int offset = i * blockSize;
                    int destOffset = chunk * blockSize;
                    Array.Copy(output, offset, result[i], destOffset, blockSize);
                }
            }

            // 处理最后一个不完整的 Chunk
            if (lastChunkLen > 0)
            {
                int chunkOffset = fullChunks * chunkSize;
                byte[] input = new byte[chunkSize];
                Array.Copy(originalData, chunkOffset, input, 0, lastChunkLen);
                // 剩余部分已经是0

                byte[] output = new byte[parityShards * blockSize];
                encodingMatrix.CodeShards(input, output, blockSize);

                _output.WriteLine($"最后一个不完整块: 实际输入长度={lastChunkLen}, 填充后输入长度={input.Length}, 输出长度={output.Length}");

                for (int i = 0; i < parityShards; i++)
                {
                    int offset = i * blockSize;
                    int destOffset = fullChunks * blockSize;
                    Array.Copy(output, offset, result[i], destOffset, blockSize);
                }
            }

            _output.WriteLine($"直接矩阵编码完成，每个冗余分片长度: {result[0].Length}");
            return result;
        }

        /// <summary>
        /// 使用流编码
        /// </summary>
        private byte[][] EncodeWithStream(IMatrix encodingMatrix, byte[] originalData, int dataShards, int parityShards, int blockSize)
        {
            int chunkSize = dataShards * blockSize;
            _output.WriteLine($"ChunkSize={chunkSize}");
            _output.WriteLine($"原始数据长度={originalData.Length}");

            var parityStreams = new MemoryStream[parityShards];
            for (int i = 0; i < parityShards; i++)
            {
                parityStreams[i] = new MemoryStream();
            }

            var dataStreams = new MemoryStream[dataShards];
            for (int i = 0; i < dataShards; i++)
            {
                dataStreams[i] = new MemoryStream();
            }
            var dataRoundRobin = new StreamRoundRobin(dataStreams, blockSize);
            _output.WriteLine($"StreamRoundRobin创建: SegmentSize={dataRoundRobin.SegmentSize}");

            using (var encodeStream = new ReedSolomonEncodeStream(encodingMatrix, parityStreams, dataRoundRobin))
            {
                _output.WriteLine($"编码流创建完成，开始写入 {originalData.Length} 字节");
                encodeStream.Write(originalData, 0, originalData.Length);
                _output.WriteLine($"写入完成，调用Flush");
                encodeStream.Flush();
            }

            byte[][] result = new byte[parityShards][];
            for (int i = 0; i < parityShards; i++)
            {
                result[i] = parityStreams[i].ToArray();
                _output.WriteLine($"冗余分片 {i}: 流长度={result[i].Length}");
            }

            foreach (var s in parityStreams)
                s.Dispose();
            foreach (var s in dataStreams)
                s.Dispose();

            _output.WriteLine($"流编码完成");
            return result;
        }
    }
}