using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.EntityFrameworkCore;
using SyncWatch.Data;
using SyncWatch.Models;

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
                try
                {
                    await ExpireRoomsAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (System.Exception)
                {
                }

                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            }
        }

        private async Task ExpireRoomsAsync(CancellationToken stoppingToken)
        {
            using (var scope = _serviceProvider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();

                var expiredRooms = await db.Rooms.Include(r => r.Movies)
                    .Where(r => r.ExpiresAt < DateTime.UtcNow)
                    .ToListAsync(stoppingToken);

                foreach (var room in expiredRooms)
                {
                    await ExpireRoomAsync(db, room);
                }
            }
        }

        private async Task ExpireRoomAsync(DatabaseContext db, Room room)
        {
            using var transaction = await db.Database.BeginTransactionAsync();

            try
            {
                foreach (var movie in room.Movies)
                {
                    await _s3.DeleteObjectAsync(new DeleteObjectRequest
                    {
                        BucketName = _bucketName,
                        Key = movie.StorageLocation
                    });
                }

                db.Remove(room);
                await db.SaveChangesAsync();
                await transaction.CommitAsync();
            }
            catch (System.Exception)
            {
                db.ChangeTracker.Clear();
                await transaction.RollbackAsync();
            }
        }
    }
}
