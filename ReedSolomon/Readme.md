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
 
### 数组编解码

```csharp
// ==================== 数组编码 ====================
int dataShards = 3;
int parityShards = 2;
int blockSize = 4096;

byte[] original = File.ReadAllBytes("input.bin");
var matrix = new VandermondeMatrix8bit(dataShards, parityShards);

int chunkSize = dataShards * blockSize;
int totalChunks = (original.Length + chunkSize - 1) / chunkSize;
byte[][] parityShardsData = new byte[parityShards][];
for (int i = 0; i < parityShards; i++)
    parityShardsData[i] = new byte[totalChunks * blockSize];

for (int chunk = 0; chunk < totalChunks; chunk++)
{
    byte[] input = new byte[chunkSize];
    int offset = chunk * chunkSize;
    int len = Math.Min(chunkSize, original.Length - offset);
    Array.Copy(original, offset, input, 0, len);

    byte[] output = new byte[parityShards * blockSize];
    matrix.CodeShards(input, output, blockSize);

    for (int i = 0; i < parityShards; i++)
        Array.Copy(output, i * blockSize, parityShardsData[i], chunk * blockSize, blockSize);
}

for (int i = 0; i < parityShards; i++)
    File.WriteAllBytes($"parity_{i}.bin", parityShardsData[i]);

// ==================== 数组解码 ====================
int[] availableIndices = { 1, 2 };
var recoveryMatrix = matrix.InverseRows(availableIndices);

byte[][] availableShards = availableIndices.Select(i => File.ReadAllBytes($"shard_{i}.bin")).ToArray();
int shardLength = availableShards[0].Length;
int chunks = shardLength / blockSize;
byte[] recovered = new byte[chunks * chunkSize];

for (int chunk = 0; chunk < chunks; chunk++)
{
    byte[] input = new byte[dataShards * blockSize];
    for (int i = 0; i < availableShards.Length; i++)
        Array.Copy(availableShards[i], chunk * blockSize, input, i * blockSize, blockSize);

    byte[] output = new byte[dataShards * blockSize];
    recoveryMatrix.CodeShards(input, output, blockSize);
    Array.Copy(output, 0, recovered, chunk * chunkSize, chunkSize);
}

File.WriteAllBytes("recovered.bin", recovered.Take(original.Length).ToArray());
``` 


### 流编解码

```csharp
// ==================== 流式编码 ====================
var dataStreams = Enumerable.Range(0, dataShards).Select(i => File.Create($"data_{i}.bin")).ToArray();
var parityStreams = Enumerable.Range(0, parityShards).Select(i => File.Create($"parity_{i}.bin")).ToArray();
var dataRoundRobin = new StreamRoundRobin(dataStreams, blockSize);

using var encodeStream = new ReedSolomonEncodeStream(matrix, parityStreams, dataRoundRobin);
using var inputStream = File.OpenRead("input.bin");
await inputStream.CopyToAsync(encodeStream);

// ==================== 流式解码 ====================
var availableStreams = availableIndices.Select(i => File.OpenRead($"shard_{i}.bin")).ToArray();
var availableRoundRobin = new StreamRoundRobin(availableStreams, blockSize);

using var decodeStream = new ReedSolomonDecodeStream(recoveryMatrix, availableRoundRobin, original.Length);
using var outputStream = File.Create("recovered.bin");
await decodeStream.CopyToAsync(outputStream); 
```

### StreamRoundRobin

```csharp
// 将多个流包装为一个连续流
var streams = new Stream[] { stream0, stream1, stream2 };
var roundRobin = new StreamRoundRobin(streams, blockSize);

// 写入：轮流写入每个流（每个流写 blockSize 字节）
roundRobin.Write(buffer);

// 读取：轮流从每个流读取
roundRobin.Read(buffer);
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