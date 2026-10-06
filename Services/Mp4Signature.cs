namespace SyncWatch.Services
{
    public static class Mp4Signature
    {
        private const int BoxHeaderSize = 8;

        // Checks the leading bytes of a file for an ISO base media file format
        // ("ftyp") box. The extension is attacker-controlled, so this is what
        // actually stops an executable renamed to .mp4 from getting in.
        public static bool LooksLikeMp4(byte[] header)
        {
            if (header == null || header.Length < 12)
            {
                return false;
            }

            if (IsAscii(header, 4, "ftyp"))
            {
                return true;
            }

            // Some muxers emit free/skip/wide before ftyp, so walk the leading box chain.
            var offset = 0;
            while (offset + BoxHeaderSize <= header.Length)
            {
                var size = ReadUInt32(header, offset);
                if (size < BoxHeaderSize || offset + size > header.Length)
                {
                    return false;
                }

                if (IsAscii(header, offset + 4, "ftyp"))
                {
                    return true;
                }

                offset += (int)size;
            }

            return false;
        }

        private static bool IsAscii(byte[] buffer, int offset, string value)
        {
            if (offset + value.Length > buffer.Length)
            {
                return false;
            }

            for (var i = 0; i < value.Length; i++)
            {
                if (buffer[offset + i] != (byte)value[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static uint ReadUInt32(byte[] buffer, int offset)
        {
            return ((uint)buffer[offset] << 24)
                 | ((uint)buffer[offset + 1] << 16)
                 | ((uint)buffer[offset + 2] << 8)
                 | buffer[offset + 3];
        }
    }
}
