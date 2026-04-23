using System;

namespace zms9110750.ReedSolomon.Galos;
/// <summary>
/// GF(2⁸) 伽罗瓦域实现
/// </summary>
public class GaloisField8bit : IGaloisField<byte>
{
    /// <summary>
    /// 共享的单例实例。使用标准本原多项式29( 0x11d)
    /// </summary>
    public static GaloisField8bit Shared { get; } = new GaloisField8bit();

    /// <summary>
    /// 指数表。大小为 512，前 255 个是 2^0 到 2^254，后 255 个是重复，用于避免乘法时取模。
    /// </summary>
    private readonly byte[] _exp;

    /// <summary>
    /// 对数表。大小为 256，_log[x] 返回 x 的对数。索引 0 处为 0（不会被使用）。
    /// </summary>
    private readonly byte[] _log;

    /// <summary>
    /// 乘法表。二维连续数组，[a, b] 的值为 a × b。
    /// </summary>
    private readonly byte[,] _mul;

    /// <inheritdoc/>
    public int Bits => 8;

    /// <inheritdoc/>
    public byte PrimitivePolynomial { get; }

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="primitivePolynomial">本原多项式，默认为 29 (x⁸ + x⁴ + x³ + x² + 1)</param>
    public GaloisField8bit(GaloisField8Poly primitivePolynomial = GaloisField8Poly.P29)
    {
        switch (primitivePolynomial)
        {
            case GaloisField8Poly.P29:
            case GaloisField8Poly.P43:
            case GaloisField8Poly.P45:
            case GaloisField8Poly.P77:
            case GaloisField8Poly.P95:
            case GaloisField8Poly.P99:
            case GaloisField8Poly.P101:
            case GaloisField8Poly.P105:
            case GaloisField8Poly.P113:
            case GaloisField8Poly.P135:
            case GaloisField8Poly.P141:
            case GaloisField8Poly.P169:
            case GaloisField8Poly.P195:
            case GaloisField8Poly.P207:
            case GaloisField8Poly.P231:
            case GaloisField8Poly.P245:
                PrimitivePolynomial = (byte)primitivePolynomial;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(primitivePolynomial));
        }

        _exp = new byte[512];
        _log = new byte[256];
        _mul = new byte[256, 256];

        // 生成指数表
        _exp[0] = 1;
        for (int i = 1; i < 255; i++)
        {
            int v = _exp[i - 1] << 1;
            if (v >= 256)
            {
                v ^= PrimitivePolynomial;
            }
            _exp[i] = (byte)v;
        }

        // 重复一遍，避免乘法时取模
        Array.Copy(_exp, 0, _exp, 255, 255);

        // 生成对数表
        for (int i = 0; i < 255; i++)
        {
            _log[_exp[i]] = (byte)i;
        }

        // 生成乘法表
        for (int a = 0; a < 256; a++)
        {
            for (int b = 0; b < 256; b++)
            {
                _mul[a, b] = a == 0 || b == 0 ? (byte)0 : _exp[_log[a] + _log[b]];
            }
        }
    }

    /// <inheritdoc/>
    public byte Add(byte a, byte b)
    {
        return (byte)(a ^ b);
    }
    /// <inheritdoc/>
    public byte Multiply(byte a, byte b)
    {
        return _mul[a, b];
    }

    /// <inheritdoc/>
    public byte Power(byte a, int exponent)
    {
        if (exponent == 0)
        {
            return 1;
        }
        if (a == 0)
        {
            return 0;
        }

        int logResult = (_log[a] * exponent) % 255;
        return _exp[logResult];
    }

    /// <inheritdoc/>
    public byte Inverse(byte a)
    {
        if (a == 0)
        {
            throw new DivideByZeroException("0 没有逆元");
        }
        return _exp[255 - _log[a]];
    }

}
