using SyncWatch.Services;
using System.Text;
using Xunit;

namespace SyncWatch.Tests
{
    public class Mp4DurationReaderTests
    {
        [Fact]
        public void TryReadDuration_NoMoovBox_ReturnsFalse()
        {
            using var stream = new MemoryStream(BuildFtypOnly());

            Assert.False(Mp4DurationReader.TryReadDuration(stream, out var seconds));
            Assert.Equal(0, seconds);
        }

        [Fact]
        public void TryReadDuration_Version0Mvhd_ReturnsDurationInSeconds()
        {
            using var stream = new MemoryStream(BuildMp4(timescale: 600, duration: 15000, version: 0));

            Assert.True(Mp4DurationReader.TryReadDuration(stream, out var seconds));
            Assert.Equal(25d, seconds, precision: 3);
        }

        [Fact]
        public void TryReadDuration_Version1Mvhd_UsesWideFields()
        {
            using var stream = new MemoryStream(BuildMp4(timescale: 1000, duration: 3_723_000, version: 1));

            Assert.True(Mp4DurationReader.TryReadDuration(stream, out var seconds));
            Assert.Equal(3723d, seconds, precision: 3);
        }

        [Fact]
        public void TryReadDuration_ZeroTimescale_ReturnsFalse()
        {
            using var stream = new MemoryStream(BuildMp4(timescale: 0, duration: 1000, version: 0));

            Assert.False(Mp4DurationReader.TryReadDuration(stream, out var seconds));
            Assert.Equal(0, seconds);
        }

        [Fact]
        public void TryReadDuration_MoovAfterMdat_StillFound()
        {
            var bytes = new List<byte>();
            bytes.AddRange(BuildFtypOnly());
            bytes.AddRange(BuildBox("mdat", new byte[64]));
            bytes.AddRange(BuildBox("moov", BuildMvhdBox(600, 7200, version: 0)));

            using var stream = new MemoryStream(bytes.ToArray());

            Assert.True(Mp4DurationReader.TryReadDuration(stream, out var seconds));
            Assert.Equal(12d, seconds, precision: 3);
        }

        [Fact]
        public void TryReadDuration_TruncatedFile_ReturnsFalseInsteadOfThrowing()
        {
            var full = BuildMp4(timescale: 600, duration: 15000, version: 0);
            using var stream = new MemoryStream(full, 0, 12);   // header only

            Assert.False(Mp4DurationReader.TryReadDuration(stream, out _));
        }

        [Fact]
        public void TryReadDuration_MissingFile_ReturnsFalse()
        {
            var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.mp4");

            Assert.False(Mp4DurationReader.TryReadDuration(path, out var seconds));
            Assert.Equal(0, seconds);
        }

        private static byte[] BuildFtypOnly()
        {
            return BuildBox("ftyp", Encoding.ASCII.GetBytes("isom"));
        }

        private static byte[] BuildMp4(uint timescale, ulong duration, byte version)
        {
            var bytes = new List<byte>();
            bytes.AddRange(BuildFtypOnly());
            bytes.AddRange(BuildBox("mdat", new byte[32]));
            bytes.AddRange(BuildBox("moov", BuildMvhdBox(timescale, duration, version)));
            return bytes.ToArray();
        }

        private static byte[] BuildMvhdBox(uint timescale, ulong duration, byte version)
        {
            var payload = new List<byte>();
            payload.Add(version);
            payload.AddRange(new byte[3]);          // flags

            if (version == 1)
            {
                payload.AddRange(BigEndian(8UL));  // creation_time
                payload.AddRange(BigEndian(8UL));  // modification_time
                payload.AddRange(BigEndian(timescale));
                payload.AddRange(BigEndian(duration));
            }
            else
            {
                payload.AddRange(BigEndian((uint)0));   // creation_time
                payload.AddRange(BigEndian((uint)0));   // modification_time
                payload.AddRange(BigEndian(timescale));
                payload.AddRange(BigEndian((uint)Math.Min(duration, uint.MaxValue)));
            }

            return BuildBox("mvhd", payload.ToArray());
        }

        private static byte[] BuildBox(string type, byte[] payload)
        {
            var box = new List<byte>();
            box.AddRange(BigEndian((uint)(payload.Length + 8)));
            box.AddRange(Encoding.ASCII.GetBytes(type));
            box.AddRange(payload);
            return box.ToArray();
        }

        private static byte[] BigEndian(uint value)
        {
            return new[]
            {
                (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value
            };
        }

        private static byte[] BigEndian(ulong value)
        {
            var bytes = new byte[8];
            for (var i = 0; i < 8; i++)
            {
                bytes[i] = (byte)(value >> (56 - (8 * i)));
            }
            return bytes;
        }
    }
}
