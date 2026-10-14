using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using SyncWatch.Data;
using SyncWatch.Hubs;
using SyncWatch.Models;

namespace SyncWatch.Services
{
    public class PlaybackSyncService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IHubContext<PlaybackHub> _hubContext;
        private readonly TimeSpan _pollingInterval;
        private readonly ILogger<PlaybackSyncService> _logger;

        public PlaybackSyncService(IServiceProvider serviceProvider, IHubContext<PlaybackHub> hubContext, IConfiguration configuration, ILogger<PlaybackSyncService> logger)
        {
            _serviceProvider = serviceProvider;
            _hubContext = hubContext;
            _logger = logger;

            var pollingIntervalSeconds = configuration.GetValue<int>("PlaybackSyncSettings:PollingIntervalSeconds", 5);
            _pollingInterval = pollingIntervalSeconds > 0 ? TimeSpan.FromSeconds(pollingIntervalSeconds) : TimeSpan.FromSeconds(5);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using (var scope = _serviceProvider.CreateScope())
                    {
                        var db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();

                        var playingStates = await db.PlaybackStates
                            .AsNoTracking()
                            .Where(s => s.Status == PlaybackStatus.Playing)
                            .Select(s => new
                            {
                                s.RoomId,
                                s.CurrentMovieId,
                                s.Status,
                                s.Position,
                                s.UpdatedAt,
                                s.Version
                            })
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
                                break;
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Error sending playback update for Room {RoomId}.", state.RoomId);
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Playback sync loop encountered an error.");
                }

                try
                {
                    await Task.Delay(_pollingInterval, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }
}
