using SyncWatch.Data;
using Microsoft.EntityFrameworkCore;

namespace SyncWatch.Services
{
    public class RoomExpirationService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;

        public RoomExpirationService(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
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
                            if (System.IO.File.Exists(movie.StorageLocation))
                            {
                                System.IO.File.Delete(movie.StorageLocation);
                            }
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
