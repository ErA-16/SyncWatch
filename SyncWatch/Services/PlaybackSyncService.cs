using Microsoft.AspNetCore.SignalR;
using SyncWatch.Data;
using SyncWatch.Hubs;
using SyncWatch.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace SyncWatch.Services
{
    public class PlaybackSyncService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IHubContext<PlaybackHub> _hubContext;
        private readonly ILogger<PlaybackSyncService> _logger;

        public PlaybackSyncService(IServiceProvider serviceProvider, IHubContext<PlaybackHub> hubContext, ILogger<PlaybackSyncService> logger)
        {
            _serviceProvider = serviceProvider;
            _hubContext = hubContext;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Consider making the interval configurable via IConfiguration
            var delay = TimeSpan.FromSeconds(5);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using (var scope = _serviceProvider.CreateScope())
                    {
                        var db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();

                        // Use AsNoTracking and project into a lightweight anonymous type so EF Core doesn't track entities
                        var playingStates = await db.PlaybackStates
                            .AsNoTracking()
                            .Where(s => s.Status == PlaybackStatus.Playing)
                            .Select(s => new { s.RoomId, s.CurrentMovieId, s.Status, s.Position, s.UpdatedAt, s.Version })
                            .ToListAsync(stoppingToken);

                        foreach (var state in playingStates)
                        {
                            try
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
                            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                            {
                                // Shutdown requested, break out
                                break;
                            }
                            catch (Exception ex)
                            {
                                // Log and continue with other states
                                _logger.LogError(ex, "Error sending playback update for Room {RoomId}", state.RoomId);
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // Expected during shutdown
                    break;
                }
                catch (Exception ex)
                {
                    // Querying the database or creating the scope failed; log and continue after delay
                    _logger.LogError(ex, "PlaybackSyncService loop encountered an error. Will retry after delay.");
                }

                try
                {
                    await Task.Delay(delay, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // Shutdown requested, exit loop
                    break;
                }
            }
        }
    }
}
