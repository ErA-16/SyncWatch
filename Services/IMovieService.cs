using Microsoft.AspNetCore.Mvc;
using SyncWatch.Models;

namespace SyncWatch.Services
{
    public interface IMovieService
    {
        Task<Movie> UploadMovieAsync(Guid roomId, IFormFile file);
        Task<string> StreamMovieAsync(Guid movieId);
    }
}
