using System.Collections.Immutable;
using System.Runtime.CompilerServices;

namespace zms9110750.ReedSolomon.Matrixs;
/// <summary>
/// 范德蒙德矩阵（8位）。直接生成可用的编码矩阵，调用方无需手动构建。
/// </summary>
public class VandermondeMatrix8bit : IMatrix<byte>
{
    private readonly byte[] _data;
    private readonly IGaloisField<byte> _gf;

    /// <inheritdoc/>
    public int Rows { get; }

    /// <inheritdoc/>
    public int Columns { get; }

    /// <inheritdoc/> 
    public bool IsSquare => Rows == Columns;

    /// <inheritdoc/>
    public byte PrimitivePolynomial => _gf.PrimitivePolynomial;


    /// <summary>
    /// 构造编码矩阵。内部自动完成 Vandermonde → 求逆 → 乘法的标准构建流程。
    /// </summary>
    /// <param name="dataShards">数据分片数（K）</param>
    /// <param name="parityShards">冗余分片数（M）</param>
    /// <param name="gf">可选的伽罗瓦域实现，默认使用共享实例</param>
    public VandermondeMatrix8bit(int dataShards, int parityShards, IGaloisField<byte>? gf = null)
    {
        if (dataShards <= 0)
        {
            throw new ArgumentException("数据分片数必须为正数", nameof(dataShards));
        }
        if (parityShards <= 0)
        {
            throw new ArgumentException("冗余分片数必须为正数", nameof(parityShards));
        }
        if (dataShards + parityShards > 256)
        {
            throw new ArgumentException($"总分片数不能超过 256，当前 {dataShards + parityShards}");
        }

        _gf = gf ?? GaloisField8bit.Shared;
        Rows = dataShards + parityShards;
        Columns = dataShards;

        // 1. 构建范德蒙德矩阵数据
        Span<byte> vandermondeData = stackalloc byte[Rows * Columns];
        for (int i = 0; i < Rows; i++)
        {
            int iBase = i * Columns;
            for (int j = 0; j < Columns; j++)
            {
                vandermondeData[iBase + j] = _gf.Power((byte)i, j);
            }
        }

        // 2. 提取前 K 行并求逆
        Span<byte> topInverse = stackalloc byte[dataShards * dataShards];
        InverseSubMatrix(vandermondeData, Rows, Columns, 0, 0, dataShards, topInverse);

        // 3. 范德蒙德 × 逆矩阵 = 编码矩阵
        _data = new byte[Rows * Columns];
        Multiply(vandermondeData, topInverse, dataShards, _data);

    }

    /// <summary>
    /// 私有构造器，用于内部创建矩阵（求逆等操作的结果）
    /// </summary>
    private VandermondeMatrix8bit(IGaloisField<byte> gf, byte[] data, int rows, int columns)
    {
        _gf = gf;
        _data = data;
        Rows = rows;
        Columns = columns;
    }



    /// <summary>
    /// 矩阵乘法：left × right，结果写入 result
    /// </summary>
    private void Multiply(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right, int rightCols, Span<byte> result)
    {

        for (int i = 0; i < Rows; i++)
        {
            int iBase = i * Columns;
            for (int j = 0; j < rightCols; j++)
            {
                byte sum = 0;
                for (int k = 0; k < Columns; k++)
                {
                    sum ^= _gf.Multiply(left[iBase + k], right[k * rightCols + j]);
                }
                result[i * rightCols + j] = sum;
            }
        }
    }

    /// <summary>
    /// 子矩阵求逆：提取 [rowStart:rowStart+size, colStart:colStart+size] 并求逆，结果写入 result
    /// </summary>
    private void InverseSubMatrix(ReadOnlySpan<byte> data, int rows, int cols, int rowStart, int colStart, int size, Span<byte> result)
    {
        // 构建增广矩阵 [A | I]
        int augSize = size * size * 2;
        Span<byte> augData = stackalloc byte[augSize];

        for (int i = 0; i < size; i++)
        {
            int srcOffset = (rowStart + i) * cols + colStart;
            int iAugBase = i * size * 2;
            data.Slice(srcOffset, size).CopyTo(augData.Slice(iAugBase, size));
            augData[iAugBase + size + i] = 1;
        }

        Span<byte> temp = stackalloc byte[size * 2];

        // 高斯消元
        for (int col = 0; col < size; col++)
        {
            // 找主元
            int pivot = col;
            while (pivot < size && augData[pivot * size * 2 + col] == 0)
            {
                pivot++;
            }

            if (pivot == size)
            {
                throw new InvalidOperationException("矩阵不可逆");
            }

            // 交换行
            if (pivot != col)
            {
                int curRowStart = col * size * 2;
                int pivotRowStart = pivot * size * 2;
                augData.Slice(curRowStart, size * 2).CopyTo(temp);
                augData.Slice(pivotRowStart, size * 2).CopyTo(augData.Slice(curRowStart, size * 2));
                temp.CopyTo(augData.Slice(pivotRowStart, size * 2));
            }

            // 归一化当前行
            int curRowBase = col * size * 2;
            byte inv = _gf.Inverse(augData[curRowBase + col]);
            for (int j = 0; j < size * 2; j++)
            {
                augData[curRowBase + j] = _gf.Multiply(augData[curRowBase + j], inv);
            }

            // 消去其他行
            for (int row = 0; row < size; row++)
            {
                if (row != col)
                {
                    int rowBase = row * size * 2;
                    byte factor = augData[rowBase + col];
                    if (factor != 0)
                    {
                        for (int j = 0; j < size * 2; j++)
                        {
                            augData[rowBase + j] ^= _gf.Multiply(factor, augData[curRowBase + j]);
                        }
                    }
                }
            }
        }

        // 提取逆矩阵
        for (int i = 0; i < size; i++)
        {
            int iBase = i * size;
            int iAugBase = i * size * 2;
            augData.Slice(iAugBase + size, size).CopyTo(result.Slice(iBase, size));
        }
    }

    /// <inheritdoc/>
    public IMatrix InverseRows(ReadOnlySpan<int> rowIndices)
    {
        int size = rowIndices.Length;
        // 如果是方阵，不允许使用此方法
        if (IsSquare)
        {
            throw new InvalidOperationException("当前矩阵已是解码矩阵，不允许使用 InverseRows 方法。");
        }

        // 提取子矩阵
        Span<byte> subData = stackalloc byte[size * size];
        for (int i = 0; i < size; i++)
        {
            int row = rowIndices[i];
            int srcOffset = row * Columns;
            int dstOffset = i * size;
            _data.AsSpan(srcOffset, size).CopyTo(subData.Slice(dstOffset, size));
        }

        // 求逆
        Span<byte> inverseData = stackalloc byte[size * size];
        InverseSubMatrix(subData, size, size, 0, 0, size, inverseData);

        return new VandermondeMatrix8bit(_gf, inverseData.ToArray(), size, size);
    }

    /// <inheritdoc/> 
    public void CodeShards(ReadOnlySpan<byte> inputs, Span<byte> outputs, int blockSize)
    {
        // 验证 blockSize 必须大于 0
        if (blockSize <= 0)
        {
            throw new ArgumentException("blockSize 必须大于 0", nameof(blockSize));
        }

        // 验证 inputs 长度：应为 Columns × blockSize
        int expectedInputLength = Columns * blockSize;
        if (inputs.Length != expectedInputLength)
        {
            throw new ArgumentException($"inputs 长度应为 {expectedInputLength}，实际 {inputs.Length}", nameof(inputs));
        }

        // 根据是否为方阵决定输出长度和起始行
        int startRow;
        int rowCount;

        if (IsSquare)
        {
            // 方阵（逆矩阵）：输出全部 Rows 行数据分片
            startRow = 0;
            rowCount = Rows;
        }
        else
        {
            // 非方阵（编码矩阵）：只输出后 M 行冗余分片
            startRow = Columns;
            rowCount = Rows - Columns;

        }
        int expectedOutputLength = rowCount * blockSize;
        if (outputs.Length != expectedOutputLength)
        {
            throw new ArgumentException($"outputs 长度应为 {expectedOutputLength}，实际 {outputs.Length}", nameof(outputs));
        }

        // 遍历输出分片
        for (int row = 0; row < rowCount; row++)
        {
            int outputOffset = row * blockSize;
            int matrixRow = startRow + row;
            int rowBase = matrixRow * Columns;

            for (int i = 0; i < blockSize; i++)
            {
                byte sum = 0;
                for (int col = 0; col < Columns; col++)
                {
                    int inputOffset = col * blockSize + i;
                    sum ^= _gf.Multiply(_data[rowBase + col], inputs[inputOffset]);
                }
                outputs[outputOffset + i] = sum;
            }
        }
    }

    /// <inheritdoc/> 
    public void CodeShards(ReadOnlyMemory<ReadOnlyMemory<byte>> inputs, ReadOnlyMemory<Memory<byte>> outputs)
    {
        // 验证输入
        if (inputs.IsEmpty)
        {
            throw new ArgumentNullException(nameof(inputs));
        }
        if (outputs.IsEmpty)
        {
            throw new ArgumentNullException(nameof(outputs));
        }

        // 验证输入分片数量
        if (inputs.Length != Columns)
        {
            throw new ArgumentException($"输入分片数量应为 {Columns}，实际 {inputs.Length}", nameof(inputs));
        }

        // 根据是否为方阵决定输出数量
        int expectedOutputCount = IsSquare ? Rows : (Rows - Columns);
        if (outputs.Length != expectedOutputCount)
        {
            throw new ArgumentException($"输出分片数量应为 {expectedOutputCount}，实际 {outputs.Length}", nameof(outputs));
        }

        // 获取第一个输入分片的长度作为基准
        int length = inputs.Span[0].Length;

        // 验证所有输入分片长度一致
        for (int i = 1; i < Columns; i++)
        {
            if (inputs.Span[i].Length != length)
            {
                throw new ArgumentException($"输入分片长度不一致：分片0长度为 {length}，分片{i}长度为 {inputs.Span[i].Length}");
            }
        }

        // 验证所有输出分片长度一致
        for (int i = 0; i < expectedOutputCount; i++)
        {
            if (outputs.Span[i].Length != length)
            {
                throw new ArgumentException($"输出分片{i}长度应为 {length}，实际 {outputs.Span[i].Length}");
            }
        }

        // 确定起始行
        int startRow = IsSquare ? 0 : Columns;

        // 执行矩阵乘法
        for (int row = 0; row < expectedOutputCount; row++)
        {
            var outputSpan = outputs.Span[row].Span;
            int matrixRow = startRow + row;
            int rowBase = matrixRow * Columns;

            // 对每个字节位置进行运算
            for (int i = 0; i < length; i++)
            {
                byte sum = 0;
                for (int col = 0; col < Columns; col++)
                {
                    sum ^= _gf.Multiply(_data[rowBase + col], inputs.Span[col].Span[i]);
                }
                outputSpan[i] = sum;
            }
        }
    }

    /// <inheritdoc/> 
    public void CodeShards(IEnumerable<IList<byte>> inputs, IEnumerable<IList<byte>> outputs, int offset, int count)
    {
        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "offset 不能为负数");
        }
        if (count <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "count 必须大于 0");
        }

        var inputList = inputs as IList<IList<byte>> ?? inputs?.ToImmutableList() ?? throw new ArgumentNullException(nameof(inputs));
        var outputList = outputs as IList<IList<byte>> ?? outputs?.ToImmutableList() ?? throw new ArgumentNullException(nameof(outputs));

        // 验证输入分片数量
        if (inputList.Count != Columns)
        {
            throw new ArgumentException($"输入分片数量应为 {Columns}，实际 {inputList.Count}", nameof(inputs));
        }

        // 根据是否为方阵决定输出数量
        int expectedOutputCount = IsSquare ? Rows : (Rows - Columns);
        if (outputList.Count != expectedOutputCount)
        {
            throw new ArgumentException($"输出分片数量应为 {expectedOutputCount}，实际 {outputList.Count}", nameof(outputs));
        }

        // 验证所有输入分片长度足够
        for (int col = 0; col < Columns; col++)
        {
            if (inputList[col].Count < offset + count)
            {
                throw new ArgumentException($"输入分片 {col} 长度不足，需要 {offset + count}，实际 {inputList[col].Count}");
            }
        }

        // 验证所有输出分片长度足够
        for (int row = 0; row < expectedOutputCount; row++)
        {
            if (outputList[row].Count < offset + count)
            {
                throw new ArgumentException($"输出分片 {row} 长度不足，需要 {offset + count}，实际 {outputList[row].Count}");
            }
        }

        // 确定起始行
        int startRow = IsSquare ? 0 : Columns;

        for (int row = 0; row < expectedOutputCount; row++)
        {
            var output = outputList[row];
            int matrixRow = startRow + row;
            int rowBase = matrixRow * Columns;

            for (int i = 0; i < count; i++)
            {
                byte sum = 0;
                for (int col = 0; col < Columns; col++)
                {
                    sum ^= _gf.Multiply(_data[rowBase + col], inputList[col][offset + i]);
                }
                output[offset + i] = sum;
            }
        }
    }
}
