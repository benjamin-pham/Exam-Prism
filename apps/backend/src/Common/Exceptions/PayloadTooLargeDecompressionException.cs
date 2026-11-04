namespace Common.Exceptions;

using System;

public sealed class PayloadTooLargeDecompressionException : Exception
{
    public long MaxAllowedBytes { get; }

    public PayloadTooLargeDecompressionException(long maxAllowedBytes)
        : base($"Decompressed request payload exceeded the maximum allowed limit of {maxAllowedBytes} bytes.")
    {
        MaxAllowedBytes = maxAllowedBytes;
    }
}

