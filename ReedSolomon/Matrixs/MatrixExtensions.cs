using System.Buffers;
using System.Collections.Immutable;

namespace zms9110750.ReedSolomon.Matrixs;

/// <summary>
/// 矩阵扩展方法，提供解码（分片恢复）功能
/// </summary>
public static class MatrixExtensions
{
    /// <summary>
    /// 使用连续内存布局执行解码：从任意 K 个可用分片恢复全部 K 个数据分片
    /// </summary>
    /// <param name="encodingMatrix">编码矩阵（原始矩阵）</param>
    /// <param name="availableShards">K 个可用分片连续拼接，长度为 Columns × blockSize</param>
    /// <param name="recoveredDataShards">K 个数据分片连续拼接，长度为 Columns × blockSize，会被写入</param>
    /// <param name="availableRowIndices">可用分片对应的矩阵行索引，长度为 Columns</param>
    /// <param name="blockSize">每个分片的字节数</param>
    /// <exception cref="ArgumentException">当 availableRowIndices 长度不等于 Columns 时抛出</exception>
    /// <exception cref="ArgumentException">当 availableShards 或 recoveredDataShards 长度不等于 Columns × blockSize 时抛出</exception>
    /// <remarks>多次使用，应该自行求逆矩阵并缓存，避免重复计算。
    /// <code>
    /// var inverse = encodingMatrix.InverseRows(availableRowIndices, dataShardCount);
    /// inverse.CodeShards(availableShards, recoveredDataShards, blockSize);
    /// </code>
    /// </remarks>
    public static void RecoverDataShards(
        this IMatrix<byte> encodingMatrix,
        ReadOnlySpan<byte> availableShards,
        Span<byte> recoveredDataShards,
        ReadOnlySpan<int> availableRowIndices,
        int blockSize)
    {
        // 验证 blockSize 必须大于 0
        if (blockSize <= 0)
        {
            throw new ArgumentException("blockSize 必须大于 0", nameof(blockSize));
        }
        int dataShardCount = encodingMatrix.Columns;

        // 验证 availableRowIndices 长度必须等于数据分片数
        if (availableRowIndices.Length != dataShardCount)
        {
            throw new ArgumentException($"availableRowIndices 长度应为 {dataShardCount}，实际 {availableRowIndices.Length}", nameof(availableRowIndices));
        }

        // 验证 availableShards 长度
        int expectedInputLength = dataShardCount * blockSize;
        if (availableShards.Length != expectedInputLength)
        {
            throw new ArgumentException($"availableShards 长度应为 {expectedInputLength}，实际 {availableShards.Length}", nameof(availableShards));
        }

        // 验证 recoveredDataShards 长度
        int expectedOutputLength = dataShardCount * blockSize;
        if (recoveredDataShards.Length != expectedOutputLength)
        {
            throw new ArgumentException($"recoveredDataShards 长度应为 {expectedOutputLength}，实际 {recoveredDataShards.Length}", nameof(recoveredDataShards));
        }

        // 求逆矩阵并执行解码
        var inverse = encodingMatrix.InverseRows(availableRowIndices);
        inverse.CodeShards(availableShards, recoveredDataShards, blockSize);
    }

    /// <summary>
    /// 使用分片集合执行解码：恢复缺失的分片
    /// </summary>
    /// <param name="encodingMatrix">编码矩阵（原始矩阵）</param>
    /// <param name="availableShards">可用的输入分片，数量为 K</param>
    /// <param name="recoveredDataShards">恢复出的数据分片，数量为 K。会被写入</param>
    /// <param name="availableRowIndices">可用分片对应的矩阵行索引，长度为 K</param>
    /// <remarks>多次使用，应该自行求逆矩阵并缓存，避免重复计算。
    /// <code>
    /// var inverse = encodingMatrix.InverseRows(availableRowIndices, dataShardCount);
    /// inverse.CodeShards(availableShards, recoveredDataShards);
    /// </code>
    /// </remarks>
    /// <exception cref="ArgumentNullException">当参数为 null 时抛出</exception>
    /// <exception cref="ArgumentException">当分片数量或长度不正确时抛出</exception>
    public static void RecoverDataShards(
        this IMatrix<byte> encodingMatrix,
        ReadOnlyMemory<ReadOnlyMemory<byte>> availableShards,
        ReadOnlyMemory<Memory<byte>> recoveredDataShards,
        ReadOnlySpan<int> availableRowIndices)
    {
        int dataShardCount = encodingMatrix.Columns;

        // 验证参数非空
        if (availableShards.IsEmpty)
        {
            throw new ArgumentNullException(nameof(availableShards));
        }
        if (recoveredDataShards.IsEmpty)
        {
            throw new ArgumentNullException(nameof(recoveredDataShards));
        }
        if (availableRowIndices.IsEmpty)
        {
            throw new ArgumentNullException(nameof(availableRowIndices));
        }

        // 验证 availableRowIndices 长度必须等于数据分片数
        if (availableRowIndices.Length != dataShardCount)
        {
            throw new ArgumentException($"availableRowIndices 长度应为 {dataShardCount}，实际 {availableRowIndices.Length}", nameof(availableRowIndices));
        }

        // 验证可用分片数量
        if (availableShards.Length != dataShardCount)
        {
            throw new ArgumentException($"availableShards 数量应为 {dataShardCount}，实际 {availableShards.Length}", nameof(availableShards));
        }

        // 验证恢复分片数量
        if (recoveredDataShards.Length != dataShardCount)
        {
            throw new ArgumentException($"recoveredDataShards 数量应为 {dataShardCount}，实际 {recoveredDataShards.Length}", nameof(recoveredDataShards));
        }

        // 获取第一个分片的长度作为基准
        int length = availableShards.Span[0].Length;

        // 验证所有可用分片长度一致
        for (int i = 1; i < dataShardCount; i++)
        {
            if (availableShards.Span[i].Length != length)
            {
                throw new ArgumentException($"可用分片长度不一致：分片0长度为 {length}，分片{i}长度为 {availableShards.Span[i].Length}");
            }
        }

        // 验证所有恢复分片长度一致
        for (int i = 0; i < dataShardCount; i++)
        {
            if (recoveredDataShards.Span[i].Length != length)
            {
                throw new ArgumentException($"恢复分片{i}长度应为 {length}，实际 {recoveredDataShards.Span[i].Length}");
            }
        }

        // 求逆矩阵并执行编码
        var inverse = encodingMatrix.InverseRows(availableRowIndices);
        inverse.CodeShards(availableShards, recoveredDataShards);
    }

    /// <summary>
    /// 使用分片集合执行解码（兼容版本）
    /// </summary>
    /// <param name="encodingMatrix">编码矩阵（原始矩阵）</param>
    /// <param name="availableShards">可用的输入分片，数量为 K</param>
    /// <param name="recoveredDataShards">恢复出的数据分片，数量为 K。会被写入</param>
    /// <param name="availableRowIndices">可用分片对应的矩阵行索引，长度为 K</param>
    /// <param name="offset">每个分片的起始字节索引</param>
    /// <param name="count">每个分片要处理的字节数</param>
    /// <remarks>多次使用，应该自行求逆矩阵并缓存，避免重复计算。
    /// <code>
    /// var inverse = encodingMatrix.InverseRows(availableRowIndices, dataShardCount);
    /// inverse.CodeShards(availableShards, recoveredDataShards, offset, count);
    /// </code>
    /// </remarks>
    /// <exception cref="ArgumentNullException">当参数为 null 时抛出</exception>
    /// <exception cref="ArgumentException">当分片数量不足或长度不足时抛出</exception>
    public static void RecoverDataShards(
        this IMatrix<byte> encodingMatrix,
        IEnumerable<IReadOnlyList<byte>> availableShards,
        IEnumerable<IList<byte>> recoveredDataShards,
        ReadOnlySpan<int> availableRowIndices,
        int offset,
        int count)
    { 

        // 验证 offset 和 count
        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "offset 不能为负数");
        }
        if (count <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "count 必须大于 0");
        }

        int dataShardCount = encodingMatrix.Columns;

        // 验证 availableRowIndices 长度必须等于数据分片数
        if (availableRowIndices.Length != dataShardCount)
        {
            throw new ArgumentException($"availableRowIndices 长度应为 {dataShardCount}，实际 {availableRowIndices.Length}", nameof(availableRowIndices));
        }

        // 将可用分片转为列表并验证数量
        var availableList = availableShards as IReadOnlyList<IReadOnlyList<byte>> ?? availableShards?.ToImmutableList() ?? throw new ArgumentNullException(nameof(availableShards));
        if (availableList.Count != dataShardCount)
        {
            throw new ArgumentException($"availableShards 数量应为 {dataShardCount}，实际 {availableList.Count}", nameof(availableShards));
        }

        // 将缺失分片转为列表并验证数量
        var missingList = recoveredDataShards as IReadOnlyList<IList<byte>> ?? recoveredDataShards?.ToImmutableList()?? throw new ArgumentNullException(nameof(recoveredDataShards));
        if (missingList.Count != dataShardCount)
        {
            throw new ArgumentException(
                $"recoveredDataShards 数量应为 {dataShardCount}，实际 {missingList.Count}",
                nameof(recoveredDataShards));
        }

        // 验证所有可用分片长度足够
        for (int i = 0; i < dataShardCount; i++)
        {
            if (availableList[i].Count < offset + count)
            {
                throw new ArgumentException($"可用分片 {i} 长度不足，需要 {offset + count}，实际 {availableList[i].Count}");
            }
        }

        // 验证所有缺失分片长度足够
        for (int i = 0; i < dataShardCount; i++)
        {
            if (missingList[i].Count < offset + count)
            {
                throw new ArgumentException($"缺失分片 {i} 长度不足，需要 {offset + count}，实际 {missingList[i].Count}");
            }
        }

        var inverse = encodingMatrix.InverseRows(availableRowIndices);
        inverse.CodeShards(availableShards, recoveredDataShards, offset, count);
    }
}