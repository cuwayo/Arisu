namespace Arisu.Utilities;

internal static class BoundedStreamReader
{
    public static async Task<byte[]> ReadAsync(
        Stream source,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);

        if (!source.CanRead)
        {
            throw new ArgumentException("The source stream must be readable.", nameof(source));
        }

        if (source.CanSeek && source.Length - source.Position > maximumBytes)
        {
            throw new InvalidDataException(
                $"The stream exceeds the maximum allowed size of {maximumBytes} bytes.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        var content = new byte[Math.Min(81_920, maximumBytes)];
        var buffer = new byte[Math.Min(81_920, maximumBytes)];
        var totalBytes = 0;

        while (true)
        {
            var bytesRead = await source.ReadAsync(buffer, cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }

            if (bytesRead > maximumBytes - totalBytes)
            {
                throw new InvalidDataException(
                    $"The stream exceeds the maximum allowed size of {maximumBytes} bytes.");
            }

            var requiredCapacity = totalBytes + bytesRead;
            if (requiredCapacity > content.Length)
            {
                var newCapacity = (int)Math.Min(
                    maximumBytes,
                    Math.Max(requiredCapacity, (long)content.Length * 2));
                Array.Resize(ref content, newCapacity);
            }

            buffer.AsSpan(0, bytesRead).CopyTo(content.AsSpan(totalBytes));
            totalBytes = requiredCapacity;
        }

        if (totalBytes != content.Length)
        {
            Array.Resize(ref content, totalBytes);
        }

        return content;
    }
}
