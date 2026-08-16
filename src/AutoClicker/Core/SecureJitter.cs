using System.Security.Cryptography;

namespace AutoClicker.Core;

/// <summary>
/// Uniform doubles in [0,1) drawn from the OS cryptographic RNG, buffered so the per-click
/// cost stays negligible even at very high rates.
///
/// A plain PRNG repeats a deterministic sequence from its seed, so a long enough record of
/// click timings can in principle be used to predict the next one. This cannot, which is
/// the whole point of the variance setting.
///
/// Not thread-safe by design - only the engine thread uses it.
/// </summary>
internal sealed class SecureJitter
{
    private readonly byte[] _buffer = new byte[512];
    private int _offset;

    public SecureJitter() => Refill();

    public double NextDouble()
    {
        if (_offset + sizeof(ulong) > _buffer.Length) Refill();

        var bits = BitConverter.ToUInt64(_buffer, _offset);
        _offset += sizeof(ulong);

        // Top 53 bits -> the exactly representable range of a double.
        return (bits >> 11) * (1.0 / (1UL << 53));
    }

    private void Refill()
    {
        RandomNumberGenerator.Fill(_buffer);
        _offset = 0;
    }
}
