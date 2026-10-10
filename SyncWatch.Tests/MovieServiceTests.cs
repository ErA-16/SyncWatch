using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using SyncWatch.Data;
using SyncWatch.Dtos;
using SyncWatch.Models;
using SyncWatch.Services;
using SyncWatch.Services.Exception;
using System;
using System.Collections.Generic;
using System.Text;

namespace SyncWatch.Tests
{
    public class MovieServiceTests
    {
        private MovieService CreateService(out DatabaseContext db)
        {
            return CreateService(out db, out _);
        }

        private MovieService CreateService(out DatabaseContext db, out Mock<IAmazonS3> s3)
        {
            db = TestHelper.CreateDbContext();

            var configData = new Dictionary<string, string?>
            {
                { "R2:BucketName", "test-bucket" },
                { "StorageSettings:MaxRoomBytes", "524288000" }   // 500 MB
            };
            var config = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();

            var logger = Mock.Of<ILogger<MovieService>>();

            s3 = new Mock<IAmazonS3>();
            s3.Setup(x => x.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PutObjectResponse());
            s3.Setup(x => x.GetPreSignedURL(It.IsAny<GetPreSignedUrlRequest>()))
                .Returns((GetPreSignedUrlRequest r) => $"https://r2.example.com/test-bucket/{r.Key}?sig=x");

            return new MovieService(db, s3.Object, config, logger);
        }

        // Minimal but genuine MP4 header: an ftyp box at offset 0, padded out to
        // the 64-byte range read the service sniffs.
        private static byte[] CreateMp4Header()
        {
            var header = new byte[64];
            WriteUInt32(header, 0, 24);
            Encoding.ASCII.GetBytes("ftyp").CopyTo(header, 4);
            Encoding.ASCII.GetBytes("isom").CopyTo(header, 8);
            return header;
        }

        private static byte[] CreateNonMp4Header()
        {
            var header = new byte[64];
            Encoding.ASCII.GetBytes("MZ").CopyTo(header, 0);          // a PE executable
            Encoding.ASCII.GetBytes("This program cannot be run").CopyTo(header, 2);
            return header;
        }

        private static void WriteUInt32(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8);
            buffer[offset + 3] = (byte)value;
        }

        // Puts an object in R2 that CompleteUploadAsync will find and accept.
        private static void SetupStoredObject(Mock<IAmazonS3> s3, string key, long size, byte[] header)
        {
            s3.Setup(x => x.GetObjectMetadataAsync("test-bucket", key, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GetObjectMetadataResponse { ContentLength = size });

            s3.Setup(x => x.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GetObjectResponse
                {
                    ContentLength = header.Length,
                    ResponseStream = new MemoryStream(header)
                });
        }

        // Builds a fake uploaded file � Moq stands in for IFormFile, CopyToAsync writes real bytes so StreamMovieAsync can find the file on disk
        private IFormFile CreateFakeFile(string fileName, long length, string contentType = "video/mp4")
        {
            var mock = new Mock<IFormFile>();
            mock.Setup(f => f.FileName).Returns(fileName);
            mock.Setup(f => f.Length).Returns(length);
            mock.Setup(f => f.ContentType).Returns(contentType);
            mock.Setup(f => f.CopyToAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
                .Returns<Stream, CancellationToken>((stream, _) =>
                {
                    var bytes = new byte[length];
                    return stream.WriteAsync(bytes, 0, bytes.Length);
                });
            return mock.Object;
        }

        [Fact]
        public async Task UploadMovieAsync_EmptyFile_ThrowsEmptyFileException()
        {
            var service = CreateService(out _);
            var emptyFile = CreateFakeFile("movie.mp4", 0);

            await Assert.ThrowsAsync<EmptyFileException>(
                () => service.UploadMovieAsync(Guid.NewGuid(), emptyFile));
        }

        [Fact]
        public async Task UploadMovieAsync_InvalidExtension_ThrowsInvalidFileTypeException()
        {
            var service = CreateService(out _);
            var file = CreateFakeFile("movie.mkv", 1000);

            await Assert.ThrowsAsync<InvalidFileTypeException>(
                () => service.UploadMovieAsync(Guid.NewGuid(), file));
        }

        [Fact]
        public async Task UploadMovieAsync_ExceedsRoomStorageCap_ThrowsStorageLimitException()
        {
            var service = CreateService(out var db);
            var roomId = Guid.NewGuid();

            // Pre-fill the room with 499 MB already used
            db.Movies.Add(new Movie { RoomId = roomId, FileSize = 499 * 1024L * 1024L, FileName = "a.mp4", Title = "a", StorageLocation = "x" });
            await db.SaveChangesAsync();

            var file = CreateFakeFile("movie.mp4", 2 * 1024L * 1024L);   // pushes total past 500MB

            await Assert.ThrowsAsync<StorageLimitException>(
                () => service.UploadMovieAsync(roomId, file));
        }

        [Fact]
        public async Task UploadMovieAsync_ParsesEpisodeNumberFromFileName()
        {
            var service = CreateService(out _);
            var roomId = Guid.NewGuid();
            var file = CreateFakeFile("Show.E03.mp4", 1000);

            var movie = await service.UploadMovieAsync(roomId, file);

            Assert.Equal(3, movie.EpisodeNumber);
        }

        [Fact]
        public async Task UploadMovieAsync_FirstUpload_TransitionsRoomToReady()
        {
            var service = CreateService(out var db);
            var room = new Room { Status = RoomStatus.Waiting, Code = "ABCDE" };
            db.Rooms.Add(room);
            await db.SaveChangesAsync();

            var file = CreateFakeFile("movie.mp4", 1000);
            await service.UploadMovieAsync(room.Id, file);

            var updatedRoom = await db.Rooms.FindAsync(room.Id);
            Assert.Equal(RoomStatus.Ready, updatedRoom!.Status);
        }

        [Fact]
        public async Task CreateUploadTicketAsync_IssuesUrlAndKeyForTheRoom()
        {
            var service = CreateService(out _);
            var roomId = Guid.NewGuid();

            var ticket = await service.CreateUploadTicketAsync(roomId,
                new UploadTicketRequest { FileName = "Show.E03.mp4", FileSize = 300 * 1024L * 1024L });

            Assert.Contains($"{roomId}/", ticket.ObjectKey);
            Assert.EndsWith(".mp4", ticket.ObjectKey);
            Assert.Equal("video/mp4", ticket.ContentType);
            Assert.StartsWith("https://", ticket.UploadUrl);
        }

        [Fact]
        public async Task CreateUploadTicketAsync_NonMp4_ThrowsInvalidFileTypeException()
        {
            var service = CreateService(out _);

            await Assert.ThrowsAsync<InvalidFileTypeException>(
                () => service.CreateUploadTicketAsync(Guid.NewGuid(),
                    new UploadTicketRequest { FileName = "setup.exe", FileSize = 1000 }));
        }

        [Fact]
        public async Task CreateUploadTicketAsync_ExceedsRoomStorageCap_ThrowsStorageLimitException()
        {
            var service = CreateService(out var db);
            var roomId = Guid.NewGuid();

            db.Movies.Add(new Movie { RoomId = roomId, FileSize = 499 * 1024L * 1024L, FileName = "a.mp4", Title = "a", StorageLocation = "x" });
            await db.SaveChangesAsync();

            await Assert.ThrowsAsync<StorageLimitException>(
                () => service.CreateUploadTicketAsync(roomId,
                    new UploadTicketRequest { FileName = "movie.mp4", FileSize = 2 * 1024L * 1024L }));
        }

        [Fact]
        public async Task CompleteUploadAsync_ValidMp4_RegistersMovieAndReadiesRoom()
        {
            var service = CreateService(out var db, out var s3);
            var room = new Room { Status = RoomStatus.Waiting, Code = "ABCDE" };
            db.Rooms.Add(room);
            await db.SaveChangesAsync();

            var key = $"{room.Id}/{Guid.NewGuid()}.mp4";
            SetupStoredObject(s3, key, 4096, CreateMp4Header());

            var movie = await service.CompleteUploadAsync(room.Id, new CompleteUploadRequest
            {
                ObjectKey = key,
                FileName = "Show.E03.mp4",
                FileSize = 4096,
                DurationSeconds = 1420
            });

            Assert.Equal(3, movie.EpisodeNumber);
            Assert.Equal(key, movie.StorageLocation);
            Assert.Equal(1420, movie.Duration);
            Assert.Equal(4096, movie.FileSize);
            Assert.Equal(MovieUploadStatus.Uploaded, movie.Status);
            Assert.Equal(RoomStatus.Ready, (await db.Rooms.FindAsync(room.Id))!.Status);
        }

        [Fact]
        public async Task CompleteUploadAsync_ExecutableRenamedToMp4_IsRejectedAndDeleted()
        {
            var service = CreateService(out var db, out var s3);
            var key = $"{Guid.NewGuid()}/{Guid.NewGuid()}.mp4";
            SetupStoredObject(s3, key, 4096, CreateNonMp4Header());

            await Assert.ThrowsAsync<InvalidContentException>(
                () => service.CompleteUploadAsync(
                    Guid.Parse(key.Split('/')[0]),
                    new CompleteUploadRequest { ObjectKey = key, FileName = "innocent.mp4", FileSize = 4096 }));

            Assert.Empty(db.Movies);
            s3.Verify(x => x.DeleteObjectAsync("test-bucket", key, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task CompleteUploadAsync_KeyFromAnotherRoom_IsRejected()
        {
            var service = CreateService(out var db, out var s3);
            var otherRoomId = Guid.NewGuid();
            var key = $"{otherRoomId}/{Guid.NewGuid()}.mp4";
            SetupStoredObject(s3, key, 4096, CreateMp4Header());

            await Assert.ThrowsAsync<MovieNotFoundException>(
                () => service.CompleteUploadAsync(Guid.NewGuid(),
                    new CompleteUploadRequest { ObjectKey = key, FileName = "movie.mp4", FileSize = 4096 }));

            Assert.Empty(db.Movies);
        }

        [Fact]
        public async Task CompleteUploadAsync_SizeMismatch_IsRejectedAsIncomplete()
        {
            var service = CreateService(out var db, out var s3);
            var roomId = Guid.NewGuid();
            var key = $"{roomId}/{Guid.NewGuid()}.mp4";
            SetupStoredObject(s3, key, 4096, CreateMp4Header());

            await Assert.ThrowsAsync<IncompleteUploadException>(
                () => service.CompleteUploadAsync(roomId, new CompleteUploadRequest
                {
                    ObjectKey = key,
                    FileName = "movie.mp4",
                    FileSize = 9999
                }));

            Assert.Empty(db.Movies);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public void LooksLikeMp4_AcceptsFtypAtOffsetFour(bool hasFtyp, bool padded)
        {
            var header = new byte[64];
            WriteUInt32(header, 0, 8);                       // tiny leading box
            if (padded)
            {
                Encoding.ASCII.GetBytes("free").CopyTo(header, 4);
                WriteUInt32(header, 8, 16);
                Encoding.ASCII.GetBytes("ftyp").CopyTo(header, 12);
            }
            else if (hasFtyp)
            {
                Encoding.ASCII.GetBytes("ftyp").CopyTo(header, 4);
            }

            Assert.Equal(hasFtyp || padded, Mp4Signature.LooksLikeMp4(header));
        }

        [Fact]
        public void LooksLikeMp4_RejectsNonMp4()
        {
            Assert.False(Mp4Signature.LooksLikeMp4(CreateNonMp4Header()));
            Assert.False(Mp4Signature.LooksLikeMp4(new byte[] { 1, 2, 3 }));
        }

        [Fact]
        public async Task StreamMovieAsync_MovieNotInDb_ThrowsMovieNotFoundException()
        {
            var service = CreateService(out _);

            await Assert.ThrowsAsync<MovieNotFoundException>(
                () => service.StreamMovieAsync(Guid.NewGuid()));
        }

        [Fact]
        public async Task StreamMovieAsync_FileMissingOnDisk_ThrowsMovieNotFoundOnDiskException()
        {
            var service = CreateService(out var db);
            var movie = new Movie { RoomId = Guid.NewGuid(), Title = "x", FileName = "x.mp4", StorageLocation = "nonexistent-path.mp4" };

            await Assert.ThrowsAsync<MovieNotFoundException>(
                () => service.StreamMovieAsync(movie.Id));
        }
    }
}
