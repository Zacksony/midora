using System.Reflection;

namespace Midora.Compiler.Tests;

public sealed class SourceFingerprintPrimitiveTests
{
    private delegate void AddLong(ref ulong hash, long value);

    [Fact]
    public void ZeroSuffixOptimizationMatchesAllEightOriginalBytesForSignedValuesAndInitialStates()
    {
        AddLong append = typeof(SourceFingerprint).GetMethod("Add", BindingFlags.NonPublic | BindingFlags.Static,
            [typeof(ulong).MakeByRefType(), typeof(long)])!.CreateDelegate<AddLong>();
        Random random = new(20260910);
        long[] boundaries = [0, 1, 255, 256, 65535, 65536, int.MinValue, int.MaxValue,
            uint.MaxValue, long.MinValue, long.MaxValue, -1];
        foreach (long value in boundaries) Check(value);
        for (int i = 0; i < 100000; i++)
            Check((i % 4) switch { 0 => 0, 1 => random.NextInt64(1 << 24),
                2 => random.NextInt64(), _ => -random.NextInt64() });
        void Check(long value)
        {
            ulong initial = unchecked((ulong)random.NextInt64() * 391371UL);
            ulong expected = initial, actual = initial, bits = unchecked((ulong)value);
            for (int i = 0; i < 8; i++)
                expected = unchecked((expected ^ (byte)(bits >> (i * 8))) * 1099511628211UL);
            append(ref actual, value);
            Assert.Equal(expected, actual);
        }
    }
}
