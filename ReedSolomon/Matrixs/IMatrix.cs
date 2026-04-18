using System;
using zms9110750.ReedSolomon.Galos;

namespace zms9110750.ReedSolomon.Matrixs
{
    /// <summary>
    /// 矩阵接口（非泛型），提供给不关心域类型和本原多项式的调用者使用。
    /// 只包含编解码所需的核心方法，不包含矩阵变换操作。
    /// </summary>
    public interface IMatrix
    {
        /// <summary>
        /// 获取矩阵的行数
        /// </summary>
        /// <remarks>
        /// 对于编码矩阵：行数 = 数据分片数(K) + 冗余分片数(M) = 总分片数(N)<br/>
        /// 对于逆矩阵：行数 = 数据分片数(K)
        /// </remarks>
        int Rows { get; }

        /// <summary>
        /// 获取矩阵的列数
        /// </summary>
        /// <remarks>
        /// 无论编解码矩阵：列数 = 数据分片数(K)
        /// </remarks>
        int Columns { get; }

        /// <summary>
        /// 判断矩阵是否为方阵
        /// </summary>
        /// <remarks>
        /// 编码矩阵不是方阵，行数大于列数（Rows > Columns），差额部分用于生成冗余分片。<br/>
        /// 方阵没有冗余分片，通常用于解码。<br/>
        /// 编码矩阵的编码方法的输出分片数量等于行数减去列数（Rows - Columns）。<br/>
        /// 解码矩阵的编码方法的输出分片数量等于列数（Columns）。<br/>
        /// </remarks>
        bool IsSquare { get; }

        /// <summary>
        /// 使用连续内存布局执行矩阵乘法
        /// </summary>
        /// <param name="inputs">数据分片连续拼接，总长度为 Columns × blockSize</param>
        /// <param name="outputs">输出分片，会被写入。若<see cref="IsSquare"/>为: 
        /// <list type="bullet">
        /// <item>false:输出冗余分片，总长度为 (Rows - Columns) × blockSize</item>
        /// <item>true:输出复原数据分片，长度为 Columns × blockSize</item>
        /// </list></param>
        /// <param name="blockSize">每个分片的字节数</param>
        /// <exception cref="ArgumentException"/>
        void CodeShards(ReadOnlySpan<byte> inputs, Span<byte> outputs, int blockSize);

        /// <summary>
        /// 使用分片集合执行矩阵乘法
        /// </summary>
        /// <param name="inputs">Columns 个数据分片。所有分片长度必须相等。</param>
        /// <param name="outputs">输出分片，会被写入。若<see cref="IsSquare"/>为: 
        /// <list type="bullet">
        /// <item>false:输出冗余分片，数量为 Rows - Columns</item>
        /// <item>true:输出复原数据分片，数量为 Columns</item>
        /// </list></param> 
        /// <exception cref="ArgumentNullException"/> 
        /// <exception cref="ArgumentException"/> 
        void CodeShards(ReadOnlyMemory<ReadOnlyMemory<byte>> inputs, ReadOnlyMemory<Memory<byte>> outputs);

        /// <summary>
        /// 使用分片集合执行矩阵乘法
        /// </summary>
        /// <param name="inputs">Columns 个数据分片</param>
        /// <param name="outputs">输出分片，会被写入。若<see cref="IsSquare"/>为: 
        /// <list type="bullet">
        /// <item>false:输出冗余分片，数量为 Rows - Columns</item>
        /// <item>true:输出复原数据分片，数量为 Columns</item>
        /// </list>
        /// 所有分片长度必须至少为 offset + count。
        /// </param>
        /// <param name="offset">每个分片的起始字节索引</param>
        /// <param name="count">每个分片要处理的字节数</param> 
        /// <exception cref="ArgumentNullException"/> 
        /// <exception cref="ArgumentException"/> 
        void CodeShards(IEnumerable<IReadOnlyList<byte>> inputs, IEnumerable<IList<byte>> outputs, int offset, int count);

        /// <summary>
        /// 根据指定的行索引构建子矩阵并求逆。
        /// 用于解码时，根据可用分片的行索引构造恢复矩阵。
        /// </summary>
        /// <param name="rowIndices">要提取的行索引，长度等于留存数据分片数</param>
        /// <returns>子方阵的逆矩阵</returns>
        /// <remarks>方阵时，不能调用此方法。</remarks>
        /// <exception cref="InvalidOperationException"/>
        IMatrix InverseRows(ReadOnlySpan<int> rowIndices );
    }

    /// <summary>
    /// 伽罗瓦域上的矩阵接口（泛型），提供给需要矩阵变换的调用者使用。
    /// 包含本原多项式信息和矩阵运算方法，用于构建编码矩阵和解码矩阵。
    /// </summary>
    /// <typeparam name="T">域元素类型（byte, ushort, uint 等），决定了伽罗瓦域的位数</typeparam>
    public interface IMatrix<T> : IMatrix where T : unmanaged
    {
        /// <summary>
        /// 本原多项式。与域元素类型 T 共同唯一确定一个伽罗瓦域。
        /// </summary>
        T PrimitivePolynomial { get; }
    }
}
