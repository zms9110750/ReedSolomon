using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ReedSolomon.NET.Tests.Matrixs;

public class VandermondeMatrix8bitTests
{
    [Theory]
    [InlineData(4, 2)]
    [InlineData(12, 4)]
    [InlineData(3, 7)]
    [InlineData(1, 1)]
    public void Encode_Then_Decode_ShouldRecoverOriginalData(int dataShards, int parityShards)
    {
        // 创建编码矩阵
        var matrix = new VandermondeMatrix8bit(dataShards, parityShards);
        int totalShards = dataShards + parityShards;
        const int blockSize = 1024; // 每个分片 1KB

        byte[] datas = new byte[totalShards * blockSize];
        Random random = new Random(42);


        random.NextBytes(datas.AsSpan(0, dataShards * blockSize));

        matrix.CodeShards(datas.AsSpan(0, dataShards * blockSize), datas.AsSpan(dataShards * blockSize, parityShards * blockSize), blockSize);

        byte[] hold = new byte[blockSize * dataShards];

        var 分片序数 = Enumerable.Range(0, dataShards + parityShards).ToArray();
        random.Shuffle(分片序数);


        for (int i = 0; i < dataShards; i++)
        {
            datas.AsSpan(分片序数[i] * blockSize, blockSize).CopyTo(hold.AsSpan(i * blockSize, blockSize));
        }
        byte[] recoveredDataShards = new byte[blockSize * dataShards];
        matrix.RecoverDataShards(hold, recoveredDataShards, 分片序数.AsSpan(0, dataShards), blockSize);
         
        Assert.Equal(datas.AsSpan(0, dataShards * blockSize).ToArray(), recoveredDataShards);
    }
}