using Microsoft.AspNetCore.Mvc;
using SyncWatch.Dtos;
using SyncWatch.Services;
using SyncWatch.Services.Exception;

namespace SyncWatch.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class PlaybackController : ControllerBase
    {
        private readonly IPlaybackService _playbackService;
        private readonly ILogger<PlaybackController> _logger;

        public PlaybackController(IPlaybackService playbackService, ILogger<PlaybackController> logger)
        {
            _playbackService = playbackService;
            _logger = logger;
        }

        [HttpPost("play/{roomId:guid}")]
        public async Task<IActionResult> Play(Guid roomId, [FromBody] PlaybackCommandRequest request)
        {
            var state = await _playbackService.PlayAsync(roomId, request);
            return Ok(state);
        }

        [HttpPost("pause/{roomId:guid}")]
        public async Task<IActionResult> Pause(Guid roomId, [FromBody] PlaybackCommandRequest request)
        {
            try
            {
                var state = await _playbackService.PauseAsync(roomId, request);
                return Ok(state);
            }
            catch (PlaybackStateNotFoundException ex)
            {
                return NotFound(ex.Message);
            }
        }

        [HttpPost("seek/{roomId:guid}")]
        public async Task<IActionResult> Seek(Guid roomId, [FromBody] PlaybackCommandRequest request)
        {
            try
            {
                var state = await _playbackService.SeekAsync(roomId, request);
                return Ok(state);
            }
            catch (PlaybackStateNotFoundException ex)
            {
                return NotFound(ex.Message);
            }
        }

        [HttpPost("switch-episode/{roomId:guid}")]
        public async Task<IActionResult> SwitchEpisode(Guid roomId, [FromBody] PlaybackCommandRequest request)
        {
            try
            {
                var state = await _playbackService.SwitchEpisodeAsync(roomId, request);
                return Ok(state);
            }
            catch (PlaybackStateNotFoundException ex)
            {
                return NotFound(ex.Message);
            }
            catch (MovieNotFoundException ex)
            {
                return NotFound(ex.Message);
            }
        }
    }
}