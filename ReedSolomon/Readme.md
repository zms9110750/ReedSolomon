# ReedSolomon（RS冗余编码校验库）

基于 Reed-Solomon 纠删码的 .NET 实现，支持流式编码/解码，适用于数据分片存储和恢复。

## 特性

- 支持 8 位伽罗瓦域（GF(256)）
- 流式编码/解码，支持任意长度数据
- 多框架支持：netstandard2.0、netstandard2.1、net8.0
- 内置 `StreamRoundRobin` 多流轮询器

## API 参考

| 类 | 说明 |
|---|---|
| `VandermondeMatrix8bit` | 范德蒙德编码矩阵，自动生成编码/解码矩阵 |
| `ReedSolomonEncodeStream` | 编码流，写入原始数据，自动生成冗余分片 |
| `ReedSolomonDecodeStream` | 解码流，从可用分片恢复原始数据 |
| `StreamRoundRobin` | 多流轮询器，将多个流包装为一个连续流 |
| `IMatrix` | 矩阵接口，提供编解码核心方法 |

## 快速开始

### 快捷使用(仅限netstandard2.1 以上)
```csharp
string path = "X:\\共享目录\\英雄联盟原画\\lux_splash_uncentered_7.jpg";
string outputPath = path.Replace("\\共享目录", ""); 
int paritycount = 6;
int originalCount = 3;

// ==================== 快捷编码 ====================
await ReedSolomon.EncodeFileAsync(
    path,
    Enumerable.Range(originalCount, paritycount).Select(s => $"{outputPath}.shard_{s}"),
    dataShards: originalCount,
    overwrite: true);

// ==================== 快捷解码 ====================
await ReedSolomon.DecodeFileAsync(
    Enumerable.Range(3, paritycount).Select(s => KeyValuePair.Create(s, $"{outputPath}.shard_{s}")).ToDictionary(),
    outputPath + ".recovered.jpg",
    dataShards: originalCount,
    parityShards: paritycount,
    originalFileSize: new FileInfo(path).Length
    );
```
 
### 数组编解码

```csharp
VandermondeMatrix8bit matrix8Bit = new VandermondeMatrix8bit(3, 6);
byte[][] original = new byte[3][]
{
    new byte[] { 1, 2, 3, 4, 5 },
    new byte[] { 6, 7, 8, 9, 10 },
    new byte[] { 11, 12, 13, 14, 15 }
};
byte[][] parity = new byte[6][];
for (int i = 0; i < parity.Length; i++)
{
    parity[i] = new byte[original[0].Length];
}

// ==================== 数组编码 ====================
{
    matrix8Bit.CodeShards(original, parity, 0, original[0].Length);
}

// ==================== 随机选取部分分片 ============
int[] availableIndex;
byte[][] availableShards;
{
    Random rand = new Random(42);
    int[] all = Enumerable.Range(0, original.Length + parity.Length).ToArray();
    var available = original.Concat(parity).Zip(all).OrderBy(_ => rand.Next()).Take(original.Length).ToDictionary();
    availableIndex = available.Values.ToArray();
    availableShards = available.Keys.ToArray();
}

// ==================== 数组解码 ====================
{
    var inverse = matrix8Bit.InverseRows(availableIndex);
    byte[][] recovery = new byte[original.Length][];
    for (int i = 0; i < recovery.Length; i++)
    {
        recovery[i] = new byte[original[0].Length];
    }

    inverse.CodeShards(availableShards, recovery, 0, original[0].Length); 
    foreach (var item in recovery)
    {
        foreach (var item2 in item)
        {
            Console.WriteLine(item2);
        }
    }
}
``` 

### 流编解码

```csharp

string path = "X:\\共享目录\\英雄联盟原画\\lux_splash_uncentered_8.jpg";
string outputPath = path.Replace("\\共享目录", "");
int segmen = 1;
int paritycount = 6;
int originalCount = 3;

VandermondeMatrix8bit matrix8Bit = new VandermondeMatrix8bit(originalCount, paritycount);
// ==================== 流式编码 ====================

{

    var output = Enumerable.Range(0, originalCount + paritycount).Select(s => File.Create($"{outputPath}.shard_{s}")).ToArray();

    await using var parity = new StreamRoundRobin(output.Skip(originalCount), segmen);
    await using var original = new StreamRoundRobin(output.Take(originalCount), segmen);
    await using var encodeStream = new ReedSolomonEncodeStream(matrix8Bit, parity, original);

    using var inputStream = File.OpenRead(path);
    await inputStream.CopyToAsync(encodeStream);
}

// ==================== 流式解码 ====================

{
    int[]? availableIndices = Enumerable.Range(0, originalCount + paritycount).ToArray();
    Random.Shared.Shuffle(availableIndices);

    var recoveryMatrix = matrix8Bit.InverseRows(availableIndices.AsSpan().Slice(0, originalCount));
    var availableStreams = availableIndices.Take(originalCount).Select(s => File.Open($"{outputPath}.shard_{s}", FileMode.Open)).ToArray();
    using var availableRoundRobin = new StreamRoundRobin(availableStreams, segmen);
    using var outputStream = File.Create(outputPath);
    using var decodeStream = new ReedSolomonDecodeStream(recoveryMatrix, availableRoundRobin, new FileInfo(path).Length);
    await decodeStream.CopyToAsync(outputStream);
    Console.WriteLine(decodeStream.Position);
    Console.WriteLine(outputStream.Position);
}
```

## 参数说明

### 数据分片数（K）和冗余分片数（M）

- 总分片数 N = K + M <= 256（8bit 域限制）
- 可容忍任意 M 个分片丢失
- 存储成本 = 原始数据 / K × (K + M)

### 步长（blockSize）

- 每个分片每次写入/读取的字节数
- **太小**：频繁切换流，影响性能
- **太大**：冗余块大小总是步长的整倍数

建议步长为(原始数据长 / 原始数据分片) 的因式分解。

### 本原多项式（Primitive Polynomial）

- 类似于密码，编码和解码必须使用相同的本原多项式
- 默认使用标准本原多项式（8bit: 0x11D）

### 文件长度（length）

- 编码时最后一个块不足步长会用 0 填充
- 解码时必须提供原始文件长度
- 若不提供，解码会出现尾随0。
- 如果提供参数过大，会在基础流结束时报错。

### 可用分片索引（availableIndices）

- 解码时需要自行维护可用分片的索引列表
- 索引数量必须等于数据分片数（K）
- 索引顺序必须与分片流传入顺序一致
- 可用分片可以是原始数据分片或冗余分片的任意组合

## 注意事项

- 各同步方法都可能死锁，应当使用异步方法。
- 流生命周期由调用方管理，编解码流不会自动关闭底层流
- 解码时可用分片数量必须等于数据分片数（K）