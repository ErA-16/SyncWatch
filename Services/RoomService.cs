using Microsoft.AspNetCore.Mvc;
using SyncWatch.Data;
using SyncWatch.Dtos;
using SyncWatch.Models;
using SyncWatch.Services.Exception;
using Microsoft.EntityFrameworkCore;

namespace SyncWatch.Services
{
    public class RoomService : IRoomService
    {
        private readonly DatabaseContext _db;
        private readonly int _maxParticipants;

        public RoomService(DatabaseContext db, IConfiguration configuration)
        {
            _db = db;
            _maxParticipants = configuration.GetValue<int>("ParticipantsSettings:MaxParticipants");
        }

        private async Task<string> GenerateUniqueRoomCode()
        {
            const string chars = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
            var random = new Random();
            string code;

            do
            {
                code = new string(Enumerable.Range(0, 5)
                    .Select(_ => chars[random.Next(chars.Length)])
                    .ToArray());
            }
            while (await _db.Rooms.AnyAsync(r => r.Code == code));

            return code;
        }

        public async Task<CreateRoomResponse> CreateRoomAsync([FromBody] CreateRoomRequest request)
        {
            if (string.IsNullOrEmpty(request.HostName))
            {
                throw new HostNameRequiredException();
            }
            var room = new Room
            {
                Code = await GenerateUniqueRoomCode()
            };

            var host = new Participant
            {
                RoomId = room.Id,
                Token = Guid.NewGuid().ToString("N"),
                DisplayName = request.HostName,
                IsHost = true,
                Status = ParticipantStatus.Offline
            };

            room.Participants.Add(host);
            _db.Rooms.Add(room);
            await _db.SaveChangesAsync();

            return new CreateRoomResponse { RoomId = room.Id, RoomCode = room.Code, ParticipantId = host.Id, Token = host.Token };
        }

        public async Task<JoinRoomResponse> JoinRoomAsync(JoinRoomRequest request)
        {
            var room = await _db.Rooms
                        .Include(r => r.Participants)
                        .FirstOrDefaultAsync(r => r.Code == request.RoomCode);

            if (room == null)
            {
                throw new RoomNotFoundException();
            }

            if (room.Participants.Count >= _maxParticipants)
            {
                throw new MaximumRoomCapacityReachedException();
            }

            var participant = new Participant
            {
                RoomId = room.Id,
                Token = Guid.NewGuid().ToString("N"),
                DisplayName = request.DisplayName,
                IsHost = false,
                Status = ParticipantStatus.Offline
            };

            _db.Participants.Add(participant);
            await _db.SaveChangesAsync();

            return new JoinRoomResponse { RoomId = room.Id, ParticipantId = participant.Id, Token = participant.Token };
        }

        public async Task<bool> CloseRoomAsync(Guid id, string hostToken)
        {
            var selectedRoom = await _db.Rooms
                .Include(r => r.Participants)
                .FirstOrDefaultAsync(r => r.Id == id);

            if (selectedRoom == null)
            {
                throw new RoomNotFoundException();
            }

            var isHost = selectedRoom.Participants
                .Any(p => p.Token == hostToken && p.IsHost);

            if (!isHost)
            {
                throw new ForbiddenException();
            }

            _db.Rooms.Remove(selectedRoom);
            await _db.SaveChangesAsync();

            return true;
        }

        public async Task<bool> SetParticipantStatusAsync(string token, ParticipantStatus status)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            var participant = await _db.Participants
                .FirstOrDefaultAsync(p => p.Token == token);

            if (participant == null)
            {
                return false;
            }

            if (participant.Status == status)
            {
                return true;
            }

            participant.Status = status;
            await _db.SaveChangesAsync();

            return true;
        }

        public async Task<bool> LeaveRoomAsync(string token)
        {
            if (!await SetParticipantStatusAsync(token, ParticipantStatus.Offline))
            {
                throw new ParticipantNotFoundException();
            }

            return true;
        }

        public async Task<bool> ReconnectRoomAsync(string token)
        {
            if (!await SetParticipantStatusAsync(token, ParticipantStatus.Online))
            {
                throw new ParticipantNotFoundException();
            }

            return true;
        }

        public async Task<RoomResponse> GetRoomAsync(string code)
        {
            var room = await _db.Rooms
                .Include(r => r.Participants)
                .Include(r => r.Movies)
                .FirstOrDefaultAsync(r => r.Code == code);

            if (room == null)
            {
                throw new RoomNotFoundException();
            }

            await BackfillMissingDurationsAsync(room.Movies);

            if (NormalizeDuplicateEpisodeNumbers(room.Movies))
            {
                await _db.SaveChangesAsync();
            }

            return new RoomResponse
            {
                RoomId = room.Id,
                Code = room.Code,
                Status = room.Status,
                Participants = room.Participants.Select(p => new ParticipantSummary
                {
                    DisplayName = p.DisplayName,
                    IsHost = p.IsHost,
                    Status = p.Status
                }).ToList(),
                Movies = room.Movies
                    .OrderBy(m => m.EpisodeNumber)
                    .Select(m => new MovieSummary
                    {
                        Id = m.Id,
                        Title = m.Title,
                        EpisodeNumber = m.EpisodeNumber,
                        Duration = m.Duration
                    }).ToList()
            };
        }

        private async Task BackfillMissingDurationsAsync(List<Movie> movies)
        {
            var missing = movies
                .Where(m => m.Duration <= 0 && !string.IsNullOrEmpty(m.StorageLocation))
                .ToList();

            if (missing.Count == 0)
            {
                return;
            }

            var changed = false;

            foreach (var movie in missing)
            {
                if (!File.Exists(movie.StorageLocation))
                {
                    continue;
                }

                if (Mp4DurationReader.TryReadDuration(movie.StorageLocation, out var seconds))
                {
                    movie.Duration = seconds;
                    changed = true;
                }
            }

            if (changed)
            {
                await _db.SaveChangesAsync();
            }
        }

        private static bool NormalizeDuplicateEpisodeNumbers(List<Movie> movies)
        {

            var duplicates = movies
                .GroupBy(m => m.EpisodeNumber)
                .Where(g => g.Count() > 1)
                .ToList();

            if (duplicates.Count == 0)
            {
                return false;
            }

            var reserved = new HashSet<int>(
                movies
                    .GroupBy(m => m.EpisodeNumber)
                    .Where(g => g.Count() == 1)
                    .Select(g => g.Key));

            var changed = false;
            var next = 1;

            foreach (var group in duplicates.OrderBy(g => g.Key))
            {
                foreach (var movie in group.OrderBy(m => m.Id))
                {
                    while (reserved.Contains(next))
                    {
                        next++;
                    }

                    if (movie.EpisodeNumber != next)
                    {
                        movie.EpisodeNumber = next;
                        changed = true;
                    }

                    reserved.Add(next);
                    next++;
                }
            }

            return changed;
        }

    }
}
