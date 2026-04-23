
namespace zms9110750.ReedSolomon;

#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP2_1_OR_GREATER 


/// <summary>
/// Reed-Solomon 编解码静态类
/// </summary>
public static class ReedSolomon
{
    /// <summary>
    /// 编码：只输出冗余分片（原始分片不保存）
    /// </summary>
    /// <param name="inputPath">编码文件路径</param>
    /// <param name="parityPath">冗余分片输出文件路径列表（M个）</param>
    /// <param name="dataShards">原始数据分片数量 (K)</param>
    /// <param name="segmentSize">每个分片的轮询字节数</param>
    /// <param name="matrix">编码矩阵（为 null 时自动创建 VandermondeMatrix8bit）</param>
    /// <param name="overwrite">是否允许覆写。若false且文件已存在，将抛出异常</param>
    /// <param name="cancellationToken">取消令牌</param>
    public static async Task EncodeFileAsync(
        string inputPath,
        IEnumerable<string> parityPath,
        int dataShards,
        int segmentSize = 4096,
        IMatrix<byte>? matrix = null,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(inputPath))
        {
            throw new ArgumentNullException(nameof(inputPath));
        }
        if (parityPath == null)
        {
            throw new ArgumentNullException(nameof(parityPath));
        }

        var parityList = parityPath.ToHashSet();
        if (parityList.Count == 0)
        {
            throw new ArgumentException("至少需要一个冗余分片路径", nameof(parityPath));
        }
        if (!overwrite)
        {
            foreach (var item in parityList)
            {
                if (File.Exists(item))
                {
                    throw new ArgumentException($"冗余分片路径已存在：{item}", nameof(parityPath));
                }
            }
        }

        var parityShards = parityList.Count;
        matrix ??= new VandermondeMatrix8bit(dataShards, parityShards);

        var parityStreams = parityList.Select(p => File.Create(p)).ToArray();
        await using var parity = new StreamRoundRobin(parityStreams, segmentSize);
        await using var encodeStream = new ReedSolomonEncodeStream(matrix, parity);
        await using var inputStream = File.OpenRead(inputPath);

        await inputStream.CopyToAsync(encodeStream, cancellationToken);
    }

    /// <summary>
    /// 编码：同时输出原始分片和冗余分片
    /// </summary>
    /// <param name="inputPath">编码文件路径</param>
    /// <param name="parityPath">冗余分片输出文件路径列表（M个）</param>
    /// <param name="dataShardsPath">原始数据分片路径列表（K个）</param>
    /// <param name="segmentSize">每个分片的轮询字节数</param>
    /// <param name="matrix">编码矩阵（为 null 时自动创建 VandermondeMatrix8bit）</param>
    /// <param name="overwrite">是否允许覆写。若false且文件已存在，将抛出异常</param> 
    /// <param name="cancellationToken">取消令牌</param>
    public static async Task EncodeFileAsync(
        string inputPath,
        IEnumerable<string> parityPath,
        IEnumerable<string> dataShardsPath,
        int segmentSize = 4096,
        IMatrix<byte>? matrix = null,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(inputPath))
        {
            throw new ArgumentNullException(nameof(inputPath));
        }
        if (parityPath == null)
        {
            throw new ArgumentNullException(nameof(parityPath));
        }
        if (dataShardsPath == null)
        {
            throw new ArgumentNullException(nameof(dataShardsPath));
        }

        var parityList = parityPath.ToHashSet();
        var dataList = dataShardsPath.ToHashSet();

        if (dataList.Count == 0)
        {
            throw new ArgumentException("至少需要一个原始分片路径", nameof(dataShardsPath));
        }
        if (parityList.Count == 0)
        {
            throw new ArgumentException("至少需要一个冗余分片路径", nameof(parityPath));
        }
        if (!overwrite)
        {
            foreach (var item in parityList)
            {
                if (File.Exists(item))
                {
                    throw new ArgumentException($"冗余分片路径已存在：{item}", nameof(parityPath));
                }
            }
        }
        var dataShards = dataList.Count;
        var parityShards = parityList.Count;

        matrix ??= new VandermondeMatrix8bit(dataShards, parityShards);

        var dataStreams = dataList.Select(d => File.Create(d)).ToArray();
        var parityStreams = parityList.Select(p => File.Create(p)).ToArray();

        await using var parity = new StreamRoundRobin(parityStreams, segmentSize);
        await using var original = new StreamRoundRobin(dataStreams, segmentSize);
        await using var encodeStream = new ReedSolomonEncodeStream(matrix, parity, original);
        await using var inputStream = File.OpenRead(inputPath);

        await inputStream.CopyToAsync(encodeStream, cancellationToken);
    }

    /// <summary>
    /// 解码：使用指定的矩阵恢复原始文件
    /// </summary>
    /// <param name="availablePaths">可用分片字典（键=分片索引，值=文件路径）</param>
    /// <param name="outputPath">输出文件路径</param>
    /// <param name="originalFileSize">原始文件大小</param>
    /// <param name="segmentSize">每个分片的轮询字节数</param>
    /// <param name="matrix">编码矩阵</param>
    /// <param name="overwrite">是否覆盖已存在的输出文件</param>
    /// <param name="cancellationToken">取消令牌</param>
    public static async Task DecodeFileAsync(
        IDictionary<int, string> availablePaths,
        string outputPath,
        long originalFileSize,
        IMatrix<byte> matrix,
        int segmentSize = 4096,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        if (availablePaths == null)
        {
            throw new ArgumentNullException(nameof(availablePaths));
        }
        if (availablePaths.Count == 0)
        {
            throw new ArgumentException("至少需要一个可用分片", nameof(availablePaths));
        }
        if (string.IsNullOrEmpty(outputPath))
        {
            throw new ArgumentNullException(nameof(outputPath));
        }
        if (originalFileSize <= 0)
        {
            throw new ArgumentException("原始文件大小必须大于0", nameof(originalFileSize));
        }
        if (matrix == null)
        {
            throw new ArgumentNullException(nameof(matrix));
        }
        if (matrix.IsSquare)
        {
            throw new ArgumentException("方法内根据传入参数构造解码矩阵。已是方阵的矩阵无法构造新的解码矩阵。", nameof(matrix));
        }
        if (File.Exists(outputPath) && !overwrite)
        {
            throw new IOException($"文件已存在且未设置 overwrite: {outputPath}");
        }

        var dataShards = matrix.Columns;

        if (availablePaths.Count < dataShards)
        {
            throw new ArgumentException($"可用分片不足，需要至少 {dataShards} 个，实际 {availablePaths.Count} 个", nameof(availablePaths));
        }

        var indices = availablePaths.Select(kv => kv.Key).Take(dataShards).ToArray();
        var paths = availablePaths.Select(kv => kv.Value).Take(dataShards).ToArray();

        // 打开所有可用流
        var streams = paths.Select(File.OpenRead).ToArray();

        try
        {
            // 取前 dataShards 个用于解码
            var usedStreams = streams.ToArray();
            var recoveryMatrix = matrix.InverseRows(indices);

            using var roundRobin = new StreamRoundRobin(usedStreams, segmentSize);
            await using var output = File.Create(outputPath);
            using var decodeStream = new ReedSolomonDecodeStream(recoveryMatrix, roundRobin, originalFileSize);

            await decodeStream.CopyToAsync(output, cancellationToken);
        }
        finally
        {
            foreach (var stream in streams)
            {
                await stream.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// 解码：自动创建范德蒙德矩阵恢复原始文件
    /// </summary>
    /// <param name="availablePaths">可用分片字典（键=分片索引，值=文件路径）</param>
    /// <param name="outputPath">输出文件路径</param>
    /// <param name="dataShards">原始数据分片数量 (K)</param>
    /// <param name="parityShards">冗余分片数量 (M)</param>
    /// <param name="originalFileSize">原始文件大小</param>
    /// <param name="segmentSize">每个分片的轮询字节数</param>
    /// <param name="overwrite">是否覆盖已存在的输出文件</param>
    /// <param name="cancellationToken">取消令牌</param>
    public static async Task DecodeFileAsync(
        IDictionary<int, string> availablePaths,
        string outputPath,
        int dataShards,
        int parityShards,
        long originalFileSize,
        int segmentSize = 4096,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        if (dataShards <= 0)
        {
            throw new ArgumentException("数据分片数必须大于0", nameof(dataShards));
        }
        if (parityShards <= 0)
        {
            throw new ArgumentException("冗余分片数必须大于0", nameof(parityShards));
        }

        var matrix = new VandermondeMatrix8bit(dataShards, parityShards);

        await DecodeFileAsync(
            availablePaths: availablePaths,
            outputPath: outputPath,
            originalFileSize: originalFileSize,
            segmentSize: segmentSize,
            matrix: matrix,
            overwrite: overwrite,
            cancellationToken: cancellationToken);
    }
}
#endif