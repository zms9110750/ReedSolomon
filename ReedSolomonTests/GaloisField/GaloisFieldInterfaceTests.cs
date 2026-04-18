using zms9110750.ReedSolomon.Galos;

namespace ReedSolomon.NET.Tests.GaloisField
{
    /// <summary>
    /// 测试 IGaloisField&lt;byte&gt; 接口的运算方法
    /// </summary>
    public class GaloisFieldInterfaceTests
    {
        private readonly Random _random = new Random(42);

        public static TheoryData<GaloisField8Poly> AllPolynomials()
        {
            var data = new TheoryData<GaloisField8Poly>();
            foreach (GaloisField8Poly poly in Enum.GetValues(typeof(GaloisField8Poly)))
            {
                data.Add(poly);
            }
            return data;
        }

        /// <summary>
        /// 测试所有多项式下，加法等于 XOR
        /// </summary>
        [Theory]
        [MemberData(nameof(AllPolynomials))]
        public void Add_ShouldBeXor(GaloisField8Poly poly)
        {
            IGaloisField<byte> gf = new GaloisField8bit(poly);
            for (int sample = 0; sample < 1000; sample++)
            {
                byte a = (byte)_random.Next(256);
                byte b = (byte)_random.Next(256);
                Assert.Equal((byte)(a ^ b), gf.Add(a, b));
            }
        }
         

        /// <summary>
        /// 测试所有多项式下，0 乘任何数等于 0
        /// </summary>
        [Theory]
        [MemberData(nameof(AllPolynomials))]
        public void Multiply_Zero_ShouldBeZero(GaloisField8Poly poly)
        {
            IGaloisField<byte> gf = new GaloisField8bit(poly);
            for (byte x = 0; x < 255; x++)
            {
                Assert.Equal(0, gf.Multiply(0, x));
                Assert.Equal(0, gf.Multiply(x, 0));
            }
        }

        /// <summary>
        /// 测试所有多项式下，1 乘任何数等于自身
        /// </summary>
        [Theory]
        [MemberData(nameof(AllPolynomials))]
        public void Multiply_One_ShouldBeIdentity(GaloisField8Poly poly)
        {
            IGaloisField<byte> gf = new GaloisField8bit(poly);
            for (byte x = 0; x < 255; x++)
            {
                Assert.Equal(x, gf.Multiply(1, x));
                Assert.Equal(x, gf.Multiply(x, 1));
            }
        }

        /// <summary>
        /// 测试所有多项式下，乘法交换律
        /// </summary>
        [Theory]
        [MemberData(nameof(AllPolynomials))]
        public void Multiply_Commutative(GaloisField8Poly poly)
        {
            IGaloisField<byte> gf = new GaloisField8bit(poly);
            for (int sample = 0; sample < 1000; sample++)
            {
                byte a = (byte)_random.Next(256);
                byte b = (byte)_random.Next(256);
                Assert.Equal(gf.Multiply(a, b), gf.Multiply(b, a));
            }
        } 
        /// <summary>
        /// 测试所有多项式下，非零元素都有逆元
        /// </summary>
        [Theory]
        [MemberData(nameof(AllPolynomials))]
        public void Inverse_NonZero_ShouldExist(GaloisField8Poly poly)
        {
            IGaloisField<byte> gf = new GaloisField8bit(poly);
            for (byte a = 1; a < 255; a++)
            {
                byte inv = gf.Inverse(a);
                byte product = gf.Multiply(a, inv);
                Assert.Equal(1, product);
            }
        }

        /// <summary>
        /// 测试所有多项式下，0 没有逆元
        /// </summary>
        [Theory]
        [MemberData(nameof(AllPolynomials))]
        public void Inverse_Zero_ShouldThrow(GaloisField8Poly poly)
        {
            IGaloisField<byte> gf = new GaloisField8bit(poly);
            Assert.Throws<DivideByZeroException>(() => gf.Inverse(0));
        }

        /// <summary>
        /// 测试所有多项式下，幂运算正确
        /// </summary>
        [Theory]
        [MemberData(nameof(AllPolynomials))]
        public void Power_ShouldBeCorrect(GaloisField8Poly poly)
        {
            IGaloisField<byte> gf = new GaloisField8bit(poly);
            for (byte a = 0; a < 255; a++)
            {
                Assert.Equal(1, gf.Power(a, 0));

                if (a == 0)
                {
                    Assert.Equal(0, gf.Power(a, 1));
                    Assert.Equal(0, gf.Power(a, 100));
                }
                else
                {
                    Assert.Equal(a, gf.Power(a, 1));
                    Assert.Equal(1, gf.Power(a, 255));

                    byte expected = a;
                    for (int n = 2; n <= 10; n++)
                    {
                        expected = gf.Multiply(expected, a);
                        Assert.Equal(expected, gf.Power(a, n));
                    }
                }
            }
        }

        /// <summary>
        /// 测试所有多项式下，分配律成立
        /// </summary>
        [Theory]
        [MemberData(nameof(AllPolynomials))]
        public void DistributiveLaw_ShouldHold(GaloisField8Poly poly)
        {
            IGaloisField<byte> gf = new GaloisField8bit(poly);
            for (int sample = 0; sample < 1000; sample++)
            {
                byte a = (byte)_random.Next(256);
                byte b = (byte)_random.Next(256);
                byte c = (byte)_random.Next(256);
                byte left = gf.Multiply(a, gf.Add(b, c));
                byte right = gf.Add(gf.Multiply(a, b), gf.Multiply(a, c));
                Assert.Equal(left, right);
            }
        }

        /// <summary>
        /// 测试所有多项式下，结合律成立
        /// </summary>
        [Theory]
        [MemberData(nameof(AllPolynomials))]
        public void AssociativeLaw_ShouldHold(GaloisField8Poly poly)
        {
            IGaloisField<byte> gf = new GaloisField8bit(poly);
            for (int sample = 0; sample < 1000; sample++)
            {
                byte a = (byte)_random.Next(256);
                byte b = (byte)_random.Next(256);
                byte c = (byte)_random.Next(256);

                Assert.Equal(gf.Add(gf.Add(a, b), c), gf.Add(a, gf.Add(b, c)));
                Assert.Equal(gf.Multiply(gf.Multiply(a, b), c), gf.Multiply(a, gf.Multiply(b, c)));
            }
        }

        /// <summary>
        /// 测试所有多项式下，Bits 返回 8
        /// </summary>
        [Theory]
        [MemberData(nameof(AllPolynomials))]
        public void Bits_ShouldBe8(GaloisField8Poly poly)
        {
            IGaloisField<byte> gf = new GaloisField8bit(poly);
            Assert.Equal(8, gf.Bits);
        }
         
    }
}

namespace ReedSolomon.NET.Tests.GaloisField
{
}
