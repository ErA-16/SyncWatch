using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SyncWatch.Data;
using SyncWatch.Models;
using SyncWatch.Services.Exception;
using System.Text.RegularExpressions;

namespace SyncWatch.Services
{
    public class MovieService : IMovieService
    {
        private const int COPY_BUFFER_BYTES = 1024 * 1024;

        private readonly DatabaseContext _db;
        private readonly IAmazonS3 _s3;
        private readonly string _bucketName;
        private readonly long _maxRoomStorageBytes;
        private readonly ILogger<MovieService> _logger;

        public MovieService(DatabaseContext db, IAmazonS3 s3, IConfiguration configuration, ILogger<MovieService> logger)
        {
            _db = db;
            _s3 = s3;


            _bucketName = configuration["R2:BucketName"]!;

            _maxRoomStorageBytes = configuration.GetValue<long>("StorageSettings:MaxRoomBytes");

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

            var allowedExtensions = new[] { ".mp4" };
            var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (!allowedExtensions.Contains(extension))
            {
                _logger.LogWarning("Upload rejected: File extension {Extension} is not allowed.", extension);
                throw new InvalidFileTypeException();
            }

            var currentRoomStorage = await _db.Movies.Where(m => m.RoomId == roomId).SumAsync(s => s.FileSize);
            if (currentRoomStorage + file.Length > _maxRoomStorageBytes)
            {
                _logger.LogWarning("Upload rejected: Room {RoomId} storage limit exceeded.", roomId);
                throw new StorageLimitException();
            }

            int episodeNumber = await ResolveEpisodeNumberAsync(roomId, file.FileName);

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

            _db.Add(movie);
            await _db.SaveChangesAsync();

            var room = await _db.Rooms.FindAsync(roomId);
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
