using Microsoft.AspNetCore.Mvc;
using TestSocket.WebSockets;

namespace TestSocket.Controllers
{
    [ApiController]
    //[Route("[controller]")]
    [Route("api/rooms")]
    public class RoomController : ControllerBase
    {
        private readonly RoomManager _roomManager;
        
        public RoomController(RoomManager roomManager)
        {
            _roomManager = roomManager;
        }

        [HttpPost("{roomId}/importTasks")]
        public async Task<IActionResult> ImportTasksAsync(string roomId, IFormFile file)
        {
            if (file == null || file.Length == 0)
            {
                return BadRequest("File CSV mancante");
            }

            var titles = new List<string>();
            using var reader = new StreamReader(file.OpenReadStream());
            string? line;
            var isFirstLine = true;

            while((line = await reader.ReadLineAsync()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                var title = line.Split(',')[0].Trim().Trim('"');

                if (isFirstLine && title.Equals("task", StringComparison.OrdinalIgnoreCase))
                {
                    isFirstLine = false;
                    continue;
                }
                isFirstLine = false;

                titles.Add(title);
            }

            var room = _roomManager.GetOrCreateRoom(roomId);
            _roomManager.ImportTasks(room, titles);
            await _roomManager.BroadcastRoomStateAsync(room);

            return Ok();
        }
    }
}
