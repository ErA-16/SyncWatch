using Microsoft.AspNetCore.Mvc;
using SyncWatch.Dtos;
using SyncWatch.Models;

namespace SyncWatch.Services
{
    public interface IMovieService
    {
        Task<Movie> UploadMovieAsync(Guid roomId, IFormFile file);
        Task<UploadTicketResponse> CreateUploadTicketAsync(Guid roomId, UploadTicketRequest request);
        Task<Movie> CompleteUploadAsync(Guid roomId, CompleteUploadRequest request);
        Task<string> StreamMovieAsync(Guid movieId);
    }
}
