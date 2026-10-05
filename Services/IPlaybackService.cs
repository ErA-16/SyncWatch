using SyncWatch.Dtos;
using SyncWatch.Models;

namespace SyncWatch.Services
{
    public interface IPlaybackService
    {
        Task<PlaybackState> PlayAsync(Guid roomId, PlaybackCommandRequest request);
        Task<PlaybackState> PauseAsync(Guid roomId, PlaybackCommandRequest request);
        Task<PlaybackState> SeekAsync(Guid roomId, PlaybackCommandRequest request);
        Task<PlaybackState> SwitchEpisodeAsync(Guid roomId, PlaybackCommandRequest request);

        Task<PlaybackState> AdvanceAsync(Guid roomId, PlaybackCommandRequest request);

        Task<PlaybackState?> GetCurrentStateAsync(Guid roomId);
    }
}