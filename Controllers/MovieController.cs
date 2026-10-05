using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SyncWatch.Data;
using SyncWatch.Models;
using SyncWatch.Services;
using SyncWatch.Services.Exception;
using System.Text.RegularExpressions;

namespace SyncWatch.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class MovieController : ControllerBase
    {
        private readonly ILogger<MovieController> _logger;
        private readonly IMovieService _movieService;

        public MovieController(IMovieService movieService, ILogger<MovieController> logger)
        {
            _logger = logger;
            _movieService = movieService;
        }


        [HttpPost("upload/{roomId:guid}")]
        [DisableRequestSizeLimit]
        public async Task<IActionResult> UploadMovie(Guid roomId, [FromForm] IFormFile file)
        {
            try
            {
                var movie = await _movieService.UploadMovieAsync(roomId, file);
                return Ok(new { movie.Id, movie.Title, movie.EpisodeNumber });

            }
            catch (Exception ex) when (ex is EmptyFileException || ex is InvalidFileTypeException || ex is StorageLimitException)
            {
                _logger.LogWarning("Upload controller caught handled exception: {Message}", ex.Message);
                return BadRequest(ex.Message);
            }
        }

        [HttpGet("stream/{movieId:guid}")]
        public async Task<IActionResult> StreamMovie(Guid movieId)
        {
            try
            {
                var url = await _movieService.StreamMovieAsync(movieId);
                return Redirect(url);
            }
            catch (MovieNotFoundException ex)
            {
                return NotFound(ex.Message);
            }
        }
    }
}
