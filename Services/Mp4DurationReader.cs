using System.Text;

namespace SyncWatch.Services
{
    public static class Mp4DurationReader
    {
        private const int BoxHeaderSize = 8;

        public static bool TryReadDuration(string filePath, out double durationSeconds)
        {
            durationSeconds = 0;

            try
            {
                using var stream = new FileStream(
                    filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

                return TryReadDuration(stream, out durationSeconds);
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        public static bool TryReadDuration(Stream stream, out double durationSeconds)
        {
            durationSeconds = 0;

            if (!stream.CanSeek)
            {
                return false;
            }

            if (!TryFindBox(stream, 0, stream.Length, "moov", out var moovStart, out var moovEnd))
            {
                return false;
            }

            return TryReadMovieHeader(stream, moovStart, moovEnd, out durationSeconds);
        }

        private static bool TryFindBox(
            Stream stream, long start, long end, string type,
            out long payloadStart, out long payloadEnd)
        {
            payloadStart = 0;
            payloadEnd = 0;

            var position = start;
            var header = new byte[BoxHeaderSize];

            while (position + BoxHeaderSize <= end)
            {
                stream.Seek(position, SeekOrigin.Begin);

                if (ReadExactly(stream, header, BoxHeaderSize) != BoxHeaderSize)
                {
                    return false;
                }

                long size = ReadUInt32(header, 0);
                var boxType = Encoding.ASCII.GetString(header, 4, 4);
                var headerSize = (long)BoxHeaderSize;

                if (size == 1)
                {
                    var extended = new byte[8];
                    if (ReadExactly(stream, extended, 8) != 8)
                    {
                        return false;
                    }

                    size = (long)ReadUInt64(extended, 0);
                    headerSize = 16;
                }
                else if (size == 0)
                {
                    size = end - position;
                }

                if (size < headerSize || position + size > end)
                {
                    return false;
                }

                if (boxType == type)
                {
                    payloadStart = position + headerSize;
                    payloadEnd = position + size;
                    return true;
                }

                position += size;
            }

            return false;
        }

        private static bool TryReadMovieHeader(
            Stream stream, long moovStart, long moovEnd, out double durationSeconds)
        {
            durationSeconds = 0;

            if (!TryFindBox(stream, moovStart, moovEnd, "mvhd", out var payloadStart, out _))
            {
                return false;
            }

            var versionAndFlags = new byte[4];
            stream.Seek(payloadStart, SeekOrigin.Begin);

            if (ReadExactly(stream, versionAndFlags, versionAndFlags.Length) != versionAndFlags.Length)
            {
                return false;
            }

            var version = versionAndFlags[0];
            long timescale;
            long duration;

            if (version == 1)
            {
                var wide = new byte[28];
                if (ReadExactly(stream, wide, wide.Length) != wide.Length)
                {
                    return false;
                }

                timescale = ReadUInt32(wide, 16);
                duration = (long)ReadUInt64(wide, 20);
            }
            else
            {
                var narrow = new byte[16];
                if (ReadExactly(stream, narrow, narrow.Length) != narrow.Length)
                {
                    return false;
                }

                timescale = ReadUInt32(narrow, 8);
                duration = ReadUInt32(narrow, 12);
            }

            if (timescale <= 0 || duration <= 0)
            {
                return false;
            }

            durationSeconds = (double)duration / timescale;
            return true;
        }

        private static int ReadExactly(Stream stream, byte[] buffer, int count)
        {
            var total = 0;

            while (total < count)
            {
                var read = stream.Read(buffer, total, count - total);
                if (read <= 0) break;
                total += read;
            }

            return total;
        }

        private static uint ReadUInt32(byte[] buffer, int offset)
        {
            return ((uint)buffer[offset] << 24)
                 | ((uint)buffer[offset + 1] << 16)
                 | ((uint)buffer[offset + 2] << 8)
                 | buffer[offset + 3];
        }

        private static ulong ReadUInt64(byte[] buffer, int offset)
        {
            ulong value = 0;
            for (var i = 0; i < 8; i++)
            {
                value = (value << 8) | buffer[offset + i];
            }
            return value;
        }
    }
}
