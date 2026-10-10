using Microsoft.AspNetCore.SignalR;
using SyncWatch.Data;
using SyncWatch.Hubs;
using SyncWatch.Models;
using Microsoft.EntityFrameworkCore;

namespace SyncWatch.Services
{
    public class PlaybackSyncService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IHubContext<PlaybackHub> _hubContext;

        public PlaybackSyncService(IServiceProvider serviceProvider, IHubContext<PlaybackHub> hubContext)
        {
            _serviceProvider = serviceProvider;
            _hubContext = hubContext;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                using (var scope = _serviceProvider.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();

                    var playingStates = await db.PlaybackStates
                        .Where(s => s.Status == PlaybackStatus.Playing)
                        .ToListAsync();

                    foreach (var state in playingStates)
                    {
                        var elapsed = (DateTime.UtcNow - state.UpdatedAt).TotalSeconds;
                        var currentPosition = state.Position + elapsed;

                        await _hubContext.Clients.Group(state.RoomId.ToString())
                            .SendAsync("PlaybackUpdated", new
                            {
                                state.RoomId,
                                state.CurrentMovieId,
                                state.Status,
                                Position = currentPosition,
                                state.Version
                            }, stoppingToken);
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}
