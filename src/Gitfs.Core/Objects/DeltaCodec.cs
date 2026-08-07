namespace Gitfs.Core.Objects;

/// <summary>Разворот git-дельты (тело OBJ_OFS_DELTA / OBJ_REF_DELTA):
/// два 7-битных varint размеров, затем команды copy/insert.
/// Спека §6.4: размер результата — только здесь, не в заголовке пакета.</summary>
public static class DeltaCodec
{
    public static (long SourceSize, long TargetSize, int HeaderLength) ReadSizes(ReadOnlySpan<byte> delta)
    {
        var pos = 0;
        var source = ReadVarint(delta, ref pos);
        var target = ReadVarint(delta, ref pos);
        return (source, target, pos);
    }

    public static byte[] Apply(ReadOnlySpan<byte> baseData, ReadOnlySpan<byte> delta,
        long maxTargetBytes = long.MaxValue)
    {
        var (sourceSize, targetSize, pos) = ReadSizes(delta);
        if (sourceSize != baseData.Length)
            throw new InvalidDataException($"delta expects base of {sourceSize} bytes, got {baseData.Length}");
        // Потолок ДО аллокации: дельта в несколько байт может заявить результат
        // в гигабайты — из ревью, находка про DoS через targetSize.
        if (targetSize > maxTargetBytes)
            throw new InvalidDataException($"delta target {targetSize} bytes exceeds limit {maxTargetBytes}");
        if (targetSize > int.MaxValue)
            throw new InvalidDataException($"delta target {targetSize} bytes not supported");

        var result = new byte[targetSize];
        var written = 0;
        while (pos < delta.Length)
        {
            var cmd = delta[pos++];
            if ((cmd & 0x80) != 0)
            {
                long offset = 0, size = 0;
                if ((cmd & 0x01) != 0) offset |= (long)Next(delta, ref pos);
                if ((cmd & 0x02) != 0) offset |= (long)Next(delta, ref pos) << 8;
                if ((cmd & 0x04) != 0) offset |= (long)Next(delta, ref pos) << 16;
                if ((cmd & 0x08) != 0) offset |= (long)Next(delta, ref pos) << 24;
                if ((cmd & 0x10) != 0) size |= (long)Next(delta, ref pos);
                if ((cmd & 0x20) != 0) size |= (long)Next(delta, ref pos) << 8;
                if ((cmd & 0x40) != 0) size |= (long)Next(delta, ref pos) << 16;
                if (size == 0) size = 0x10000;
                if (offset + size > baseData.Length || written + size > result.Length)
                    throw new InvalidDataException("delta copy out of range");
                baseData.Slice((int)offset, (int)size).CopyTo(result.AsSpan(written));
                written += (int)size;
            }
            else
            {
                if (cmd == 0) throw new InvalidDataException("delta: reserved zero command");
                if (pos + cmd > delta.Length || written + cmd > result.Length)
                    throw new InvalidDataException("delta insert out of range");
                delta.Slice(pos, cmd).CopyTo(result.AsSpan(written));
                pos += cmd;
                written += cmd;
            }
        }
        if (written != result.Length)
            throw new InvalidDataException($"delta produced {written} bytes, target declared {result.Length}");
        return result;
    }

    private static byte Next(ReadOnlySpan<byte> delta, ref int pos) =>
        pos < delta.Length ? delta[pos++] : throw new InvalidDataException("truncated delta");

    private static long ReadVarint(ReadOnlySpan<byte> data, ref int pos)
    {
        long value = 0;
        var shift = 0;
        while (true)
        {
            var b = Next(data, ref pos);
            value |= (long)(b & 0x7f) << shift;
            if ((b & 0x80) == 0) return value;
            shift += 7;
            if (shift > 56) throw new InvalidDataException("varint too long");
        }
    }
}
