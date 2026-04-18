using zms9110750.ReedSolomon.Galos;

namespace ReedSolomon.NET.Tests.GaloisField
{
    /// <summary>
    /// 测试 GaloisField8bit 类自己的特性
    /// </summary>
    public class GF8bitTests
    {
        private readonly Random _random = new Random(42);

        /// <summary>
        /// 测试构造器：合法的枚举值能正常构造，非法的抛出异常
        /// </summary>
        [Fact]
        public void Constructor_WithValidPolynomial_ShouldSucceed()
        {
            var validValues = Enum.GetValues<GaloisField8Poly>().ToHashSet();
            for (int value = 0; value < 256; value++)
            {
                if (validValues.Contains((GaloisField8Poly)value))
                {
                    var gf = new GaloisField8bit((GaloisField8Poly)value);
                    Assert.Equal(value, gf.PrimitivePolynomial);
                    Assert.Equal(8, gf.Bits);
                }
                else
                {
                    Assert.Throws<ArgumentOutOfRangeException>(() => new GaloisField8bit((GaloisField8Poly)value));
                }
            }
        }

        /// <summary>
        /// 测试默认构造器使用 P29
        /// </summary>
        [Fact]
        public void Constructor_Default_ShouldUseP29()
        {
            var gf = new GaloisField8bit();
            Assert.Equal(29, gf.PrimitivePolynomial);
        }

        /// <summary>
        /// 测试不同多项式的乘法结果不同
        /// </summary>
        [Fact]
        public void DifferentPolynomials_ShouldHaveDifferentMultiplicationResults()
        {
            var polynomials = Enum.GetValues(typeof(GaloisField8Poly)).Cast<GaloisField8Poly>().ToArray();
            var fields = polynomials.Select(p => new GaloisField8bit(p)).ToArray();

            for (int i = 0; i < fields.Length; i++)
            {
                for (int j = i + 1; j < fields.Length; j++)
                {
                    bool foundDifference = false;
                    for (int sample = 0; sample < 256; sample++)
                    {
                        byte a = (byte)_random.Next(256);
                        byte b = (byte)_random.Next(256);
                        if (fields[i].Multiply(a, b) != fields[j].Multiply(a, b))
                        {
                            foundDifference = true;
                            break;
                        }
                    }
                    Assert.True(foundDifference,
                        $"多项式 {polynomials[i]} 和 {polynomials[j]} 的乘法结果完全相同");
                }
            }
        }

        /// <summary>
        /// 测试不同多项式的幂运算结果不同
        /// </summary>
        [Fact]
        public void DifferentPolynomials_ShouldHaveDifferentPowerResults()
        {
            var gf29 = new GaloisField8bit(GaloisField8Poly.P29);
            var gf43 = new GaloisField8bit(GaloisField8Poly.P43);

            bool foundDifference = false;
            for (byte a = 1; a < 255 && !foundDifference; a++)
            {
                for (int exp = 1; exp <= 10; exp++)
                {
                    if (gf29.Power(a, exp) != gf43.Power(a, exp))
                    {
                        foundDifference = true;
                        break;
                    }
                }
            }
            Assert.True(foundDifference, "不同多项式的幂运算结果应该不同");
        }

        /// <summary>
        /// 测试不同多项式的逆元结果不同
        /// </summary>
        [Fact]
        public void DifferentPolynomials_ShouldHaveDifferentInverseResults()
        {
            var gf29 = new GaloisField8bit(GaloisField8Poly.P29);
            var gf43 = new GaloisField8bit(GaloisField8Poly.P43);

            bool foundDifference = false;
            for (byte a = 1; a < 255 && !foundDifference; a++)
            {
                if (gf29.Inverse(a) != gf43.Inverse(a))
                {
                    foundDifference = true;
                    break;
                }
            }
            Assert.True(foundDifference, "不同多项式的逆元结果应该不同");
        }

        /// <summary>
        /// 测试不同多项式的对数表和指数表互逆
        /// </summary>
        [Fact]
        public void AllPolynomials_LogAndExp_ShouldBeInverse()
        {
            foreach (GaloisField8Poly poly in Enum.GetValues(typeof(GaloisField8Poly)))
            {
                var gf = new GaloisField8bit(poly);
                var expField = typeof(GaloisField8bit).GetField("_exp", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var logField = typeof(GaloisField8bit).GetField("_log", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var exp = expField.GetValue(gf) as byte[];
                var log = logField.GetValue(gf) as byte[];

                for (byte x = 1; x < 255; x++)
                {
                    byte logX = log[x];
                    byte expLogX = exp[logX];
                    Assert.Equal(x, expLogX);
                }
            }
        }
    }
}
