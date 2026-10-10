using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SyncWatch.Data;
using SyncWatch.Dtos;
using SyncWatch.Models;
using Microsoft.EntityFrameworkCore;
using SyncWatch.Services;
using SyncWatch.Services.Exception;

namespace SyncWatch.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class RoomController : ControllerBase
    {
        private readonly IRoomService _roomService;
        private readonly ILogger<RoomController> _logger;

        public RoomController(IRoomService roomService, ILogger<RoomController> logger)
        {
            _roomService = roomService;
            _logger = logger;
        }

        [HttpPost("create-room")]
        public async Task<IActionResult> CreateRoom([FromBody]CreateRoomRequest request)
        {
            try
            {
                var room = await _roomService.CreateRoomAsync(request);
                return Ok(room);
            }
            catch (HostNameRequiredException ex)
            {
                return BadRequest(ex.Message);
            }
        }

        [HttpPost("join-room")]
        public async Task<IActionResult> JoinRoom(JoinRoomRequest request)
        {
            try
            {
                var room = await _roomService.JoinRoomAsync(request);
                return Ok(room);

            }
            catch (RoomNotFoundException ex)
            {
                return NotFound(ex.Message);
            }
            catch (MaximumRoomCapacityReachedException ex)
            {
                return BadRequest(ex.Message);
            }
        }

        [HttpDelete("close-room/{id:guid}")]
        public async Task<IActionResult> CloseRoom(Guid id, [FromHeader] string hostToken)
        {
            try
            {
                var result = await _roomService.CloseRoomAsync(id, hostToken);
                return NoContent();
            }
            catch (RoomNotFoundException ex)
            {
                return NotFound(ex.Message);
            }
            catch (ForbiddenException ex)
            {
                return Forbid(ex.Message);
            }
        }

        [HttpPatch("leave")]
        public async Task<IActionResult> LeaveRoom([FromHeader] string token)
        {
            try
            {
                var result = await _roomService.LeaveRoomAsync(token);
                return Ok(result);
            }
            catch (ParticipantNotFoundException ex)
            {
                return NotFound(ex.Message);
            }
        }

        [HttpPatch("reconnect")]
        public async Task<IActionResult> ReconnectRoom([FromHeader] string token)
        {
            try
            {
                var result = await _roomService.ReconnectRoomAsync(token);
                return Ok(result);
            }
            catch (ParticipantNotFoundException ex)
            {
                return NotFound(ex.Message);
            }
        }

        [HttpGet("{code}")]
        public async Task<IActionResult> GetRoom(string code)
        {
            try
            {
                var room = await _roomService.GetRoomAsync(code);
                return Ok(room);
            }
            catch (RoomNotFoundException ex)
            {
                return NotFound(ex.Message);
            }
        }
    }
}
