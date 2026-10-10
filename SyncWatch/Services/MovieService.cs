using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SyncWatch.Data;
using SyncWatch.Dtos;
using SyncWatch.Models;
using SyncWatch.Services.Exception;
using System.Text.RegularExpressions;

namespace SyncWatch.Services
{
    public class MovieService : IMovieService
    {
        private const int COPY_BUFFER_BYTES = 1024 * 1024;
        private const string Mp4ContentType = "video/mp4";
        private const int SniffHeaderBytes = 64;

        private readonly DatabaseContext _db;
        private readonly IAmazonS3 _s3;
        private readonly string _bucketName;
        private readonly long _maxRoomStorageBytes;
        private readonly long _maxFileBytes;
        private readonly TimeSpan _ticketLifetime;
        private readonly ILogger<MovieService> _logger;

        public MovieService(DatabaseContext db, IAmazonS3 s3, IConfiguration configuration, ILogger<MovieService> logger)
        {
            _db = db;
            _s3 = s3;


            _bucketName = configuration["R2:BucketName"]!;

            _maxRoomStorageBytes = configuration.GetValue<long>("StorageSettings:MaxRoomBytes");

            // R2 accepts a single PUT up to 5 GB. Anything above this would fail
            // mid-upload on a presigned URL with no way to tell the client why.
            _maxFileBytes = configuration.GetValue("UploadSettings:MaxFileBytes", 5L * 1024 * 1024 * 1024);

            _ticketLifetime = TimeSpan.FromMinutes(
                configuration.GetValue("UploadSettings:TicketLifetimeMinutes", 30));

            _logger = logger;
        }

        public async Task<Movie> UploadMovieAsync(Guid roomId, [FromForm] IFormFile file)
        {
            _logger.LogInformation("Received request to upload a new movie file.");

            if (file == null || file.Length == 0)
            {
                _logger.LogWarning("Upload failed: Request contained an empty file.");
                throw new EmptyFileException();
            }

            await ValidateUploadAsync(roomId, file.FileName, file.Length);

            int episodeNumber = await ResolveEpisodeNumberAsync(roomId, file.FileName);

            var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
            var objectKey = $"{roomId}/{Guid.NewGuid()}{extension}";
            var tempFilePath = Path.Combine(Path.GetTempPath(), objectKey.Replace('/', '_'));

            using (var stream = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None, COPY_BUFFER_BYTES, useAsync: true))
            {
                await file.CopyToAsync(stream, CancellationToken.None);
            }

            var durationSeconds = 0d;
            if (!Mp4DurationReader.TryReadDuration(tempFilePath, out durationSeconds))
            {
                _logger.LogWarning(
                    "Could not read a duration from {FileName}; it will show as unknown until re-uploaded.",
                    file.FileName);
            }

            try
            {
                using (var uploadStream = new FileStream(
                    tempFilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    COPY_BUFFER_BYTES, useAsync: true))
                {
                    var putRequest = new PutObjectRequest
                    {
                        BucketName = _bucketName,
                        Key = objectKey,
                        InputStream = uploadStream,
                        ContentType = file.ContentType,
                        DisablePayloadSigning = true
                    };

                    await _s3.PutObjectAsync(putRequest);
                }
            }
            finally
            {
                File.Delete(tempFilePath);
            }

            var movie = new Movie
            {
                RoomId = roomId,
                FileName = file.FileName,
                EpisodeNumber = episodeNumber,
                Title = Path.GetFileNameWithoutExtension(file.FileName),
                FileSize = file.Length,
                ContentType = file.ContentType,
                StorageLocation = objectKey,
                Duration = durationSeconds,
                Status = MovieUploadStatus.Uploaded,
            };

            return await RegisterMovieAsync(movie);
        }

        public async Task<UploadTicketResponse> CreateUploadTicketAsync(
            Guid roomId, UploadTicketRequest request)
        {
            await ValidateUploadAsync(roomId, request.FileName, request.FileSize);

            var objectKey = $"{roomId}/{Guid.NewGuid()}.mp4";

            // Content type is signed into the URL, so the browser has to send
            // exactly this value or R2 rejects the PUT with SignatureDoesNotMatch.
            var presignRequest = new GetPreSignedUrlRequest
            {
                BucketName = _bucketName,
                Key = objectKey,
                Verb = HttpVerb.PUT,
                ContentType = Mp4ContentType,
                Expires = DateTime.UtcNow.Add(_ticketLifetime)
            };

            var uploadUrl = _s3.GetPreSignedURL(presignRequest);

            _logger.LogInformation(
                "Issued upload ticket for room {RoomId} ({FileName}, {FileSize} bytes).",
                roomId, request.FileName, request.FileSize);

            return new UploadTicketResponse
            {
                UploadUrl = uploadUrl,
                ObjectKey = objectKey,
                ContentType = Mp4ContentType,
                ExpiresInSeconds = (int)_ticketLifetime.TotalSeconds
            };
        }

        public async Task<Movie> CompleteUploadAsync(Guid roomId, CompleteUploadRequest request)
        {
            if (!IsObjectKeyForRoom(roomId, request.ObjectKey))
            {
                // Without this a client could hand us any key in the bucket and
                // attach someone else's object to their own room.
                _logger.LogWarning(
                    "Upload completion rejected: {ObjectKey} does not belong to room {RoomId}.",
                    request.ObjectKey, roomId);
                throw new MovieNotFoundException("Unknown upload.");
            }

            try
            {
                await VerifyUploadedObjectAsync(roomId, request);

                var episodeNumber = await ResolveEpisodeNumberAsync(roomId, request.FileName);

                var movie = new Movie
                {
                    RoomId = roomId,
                    FileName = request.FileName,
                    EpisodeNumber = episodeNumber,
                    Title = Path.GetFileNameWithoutExtension(request.FileName),
                    FileSize = request.FileSize,
                    ContentType = Mp4ContentType,
                    StorageLocation = request.ObjectKey,
                    Duration = request.DurationSeconds.GetValueOrDefault(),
                    Status = MovieUploadStatus.Uploaded,
                };

                return await RegisterMovieAsync(movie);
            }
            catch
            {
                // Never leave an unverified or orphaned object sitting in the bucket
                // burning storage against the room's cap.
                await TryDeleteObjectAsync(request.ObjectKey);
                throw;
            }
        }

        private async Task ValidateUploadAsync(Guid roomId, string fileName, long fileSize)
        {
            if (string.IsNullOrWhiteSpace(fileName) || fileSize <= 0)
            {
                _logger.LogWarning("Upload rejected: Request contained an empty file.");
                throw new EmptyFileException();
            }

            var extension = Path.GetExtension(fileName).ToLowerInvariant();
            if (extension != ".mp4")
            {
                _logger.LogWarning("Upload rejected: File extension {Extension} is not allowed.", extension);
                throw new InvalidFileTypeException();
            }

            if (fileSize > _maxFileBytes)
            {
                _logger.LogWarning("Upload rejected: {FileSize} bytes exceeds the per-file limit.", fileSize);
                throw new StorageLimitException("That file is too large.");
            }

            var currentRoomStorage = await _db.Movies
                .Where(m => m.RoomId == roomId)
                .SumAsync(s => s.FileSize);

            if (currentRoomStorage + fileSize > _maxRoomStorageBytes)
            {
                _logger.LogWarning("Upload rejected: Room {RoomId} storage limit exceeded.", roomId);
                throw new StorageLimitException();
            }
        }

        private async Task VerifyUploadedObjectAsync(Guid roomId, CompleteUploadRequest request)
        {
            var metadata = await _s3.GetObjectMetadataAsync(
                _bucketName, request.ObjectKey);

            var actualSize = metadata.ContentLength;
            if (actualSize <= 0)
            {
                _logger.LogWarning("Upload rejected: R2 object {ObjectKey} is empty.", request.ObjectKey);
                throw new IncompleteUploadException();
            }

            if (actualSize != request.FileSize)
            {
                _logger.LogWarning(
                    "Upload rejected: client reported {Reported} bytes but R2 stored {Stored}.",
                    request.FileSize, actualSize);
                throw new IncompleteUploadException();
            }

            var currentRoomStorage = await _db.Movies
                .Where(m => m.RoomId == roomId)
                .SumAsync(s => s.FileSize);

            if (currentRoomStorage + actualSize > _maxRoomStorageBytes)
            {
                _logger.LogWarning("Upload rejected: Room {RoomId} storage limit exceeded.", roomId);
                throw new StorageLimitException();
            }

            // The extension check above only stops an honest typo. Sniffing the first
            // box is what actually stops someone renaming malware to .mp4.
            if (!await HasMp4SignatureAsync(request.ObjectKey))
            {
                _logger.LogWarning(
                    "Upload rejected: {ObjectKey} has no MP4 signature at its start.", request.ObjectKey);
                throw new InvalidContentException();
            }

            request.FileSize = actualSize;
        }

        private async Task<bool> HasMp4SignatureAsync(string objectKey)
        {
            using var response = await _s3.GetObjectAsync(new GetObjectRequest
            {
                BucketName = _bucketName,
                Key = objectKey,
                ByteRange = new ByteRange(0, SniffHeaderBytes - 1)
            });

            using var buffer = new MemoryStream();
            await response.ResponseStream.CopyToAsync(buffer);

            return Mp4Signature.LooksLikeMp4(buffer.ToArray());
        }

        private static bool IsObjectKeyForRoom(Guid roomId, string objectKey)
        {
            if (string.IsNullOrWhiteSpace(objectKey))
            {
                return false;
            }

            var prefix = $"{roomId}/";
            if (!objectKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var name = objectKey[prefix.Length..];
            if (name.Contains('/') || !name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return Guid.TryParseExact(name[..^".mp4".Length], "D", out _);
        }

        private async Task TryDeleteObjectAsync(string objectKey)
        {
            try
            {
                await _s3.DeleteObjectAsync(_bucketName, objectKey);
            }
            catch (System.Exception ex)
            {
                // Best effort only — a leftover object costs storage, it must not
                // turn a rejected upload into a 500.
                _logger.LogWarning(ex, "Could not clean up rejected upload {ObjectKey}.", objectKey);
            }
        }

        private async Task<Movie> RegisterMovieAsync(Movie movie)
        {
            _db.Add(movie);
            await _db.SaveChangesAsync();

            var room = await _db.Rooms.FindAsync(movie.RoomId);
            if (room != null && room.Status == RoomStatus.Waiting)
            {
                room.Status = RoomStatus.Ready;
                await _db.SaveChangesAsync();
            }

            return movie;
        }

        private async Task<int> ResolveEpisodeNumberAsync(Guid roomId, string fileName)

        {
            var taken = (await _db.Movies
                    .Where(m => m.RoomId == roomId)
                    .Select(m => m.EpisodeNumber)
                    .ToListAsync())
                .Where(n => n > 0)
                .ToHashSet();

            var match = Regex.Match(fileName, @"\bE(\d+)\b|\bEpisode\s*(\d+)\b", RegexOptions.IgnoreCase);

            if (match.Success)
            {
                var digits = !string.IsNullOrEmpty(match.Groups[1].Value)
                    ? match.Groups[1].Value
                    : match.Groups[2].Value;

                if (int.TryParse(digits, out var explicitNumber) && explicitNumber > 0 && !taken.Contains(explicitNumber))
                {
                    return explicitNumber;
                }

                _logger.LogInformation(
                    "Filename suggested episode {Number} but that slot is taken; falling back to the next free number.",
                    explicitNumber);
            }

            var candidate = 1;
            while (taken.Contains(candidate))
            {
                candidate++;
            }

            return candidate;
        }

        public async Task<string> StreamMovieAsync(Guid movieId)
        {
            var movie = await _db.Movies.FindAsync(movieId);

            if (movie == null)
            {
                throw new MovieNotFoundException();
            }

            var request = new GetPreSignedUrlRequest
            {
                BucketName = _bucketName,
                Key = movie.StorageLocation,
                Expires = DateTime.UtcNow.AddMinutes(10),
                Verb = HttpVerb.GET
            };

            return _s3.GetPreSignedURL(request);
        }
    }
}
