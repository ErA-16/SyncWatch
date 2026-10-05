using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.EntityFrameworkCore;
using SyncWatch.Data;

namespace SyncWatch.Services
{
    public class RoomExpirationService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IAmazonS3 _s3;
        private readonly string _bucketName;

        public RoomExpirationService(IServiceProvider serviceProvider, IAmazonS3 s3, IConfiguration configuration)
        {
            _serviceProvider = serviceProvider;
            _s3 = s3;
            _bucketName = configuration["R2:BucketName"]!;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                using (var scope = _serviceProvider.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();

                    var expiredRooms = await db.Rooms.Include(r => r.Movies)
                        .Where(r => r.ExpiresAt < DateTime.UtcNow)
                        .ToListAsync();

                    foreach (var room in expiredRooms)
                    {
                        foreach (var movie in room.Movies)
                        {
                            await _s3.DeleteObjectAsync(new DeleteObjectRequest
                            {
                                BucketName = _bucketName,
                                Key = movie.StorageLocation
                            });
                        }

                        if (expiredRooms.Count > 0)
                        {
                            await db.SaveChangesAsync();
                        }

                        db.Remove(room);
                    }
                }

                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            }
        }
    }
}
