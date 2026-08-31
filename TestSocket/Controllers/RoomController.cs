using Microsoft.AspNetCore.Mvc;
using System.Text;
using TestSocket.Utilities;
using TestSocket.WebSockets;
using TestSocket.WebSockets.Models;

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

        [HttpPost("{roomId}/previewCsvHeaders")]
        public async Task<IActionResult> PreviewCsvHeadersAsync(string roomId, IFormFile file)
        {
            if (!_roomManager.TryGetRoom(roomId, out _))
                return NotFound($"Stanza '{roomId}' non trovata");

            if (file == null || file.Length == 0)
                return BadRequest("File CSV mancante");

            using var reader = new StreamReader(file.OpenReadStream());
            var headerLine = await reader.ReadLineAsync();
            if (headerLine == null) return BadRequest("File CSV vuoto");

            var delimiter = CsvHelper.DetectDelimiter(headerLine);
            var headers = CsvHelper.SplitLine(headerLine, delimiter);

            var suggestion = CsvHelper.SuggestMapping(headers);

            return Ok(new
            {
                headers,
                delimiter = delimiter.ToString(),
                suggestedTitleColumn = suggestion.TitleColumn,
                suggestedPriorityColumn = suggestion.PriorityColumn,
                suggestedLinkColumn = suggestion.LinkColumn
            });
        }

        [HttpPost("{roomId}/importTasks")]
        public async Task<IActionResult> ImportTasksAsync(
            string roomId, 
            IFormFile file,
            [FromForm] string titleColumn,
            [FromForm] string? priorityColumn,
            [FromForm] string? linkColumn)
        {
            if (!_roomManager.TryGetRoom(roomId, out var room))
            {
                return NotFound($"Stanza '{roomId}' non trovata");
            }

            if (file == null || file.Length == 0)
            {
                return BadRequest("File CSV mancante");
            }

            if (string.IsNullOrWhiteSpace(titleColumn))
                return BadRequest("Colonna titolo non specificata");

            using var reader = new StreamReader(file.OpenReadStream());

            var headerLine = await reader.ReadLineAsync();
            if (headerLine == null) return BadRequest("File CSV vuoto");

            var delimiter = CsvHelper.DetectDelimiter(headerLine);
            var headers = CsvHelper.SplitLine(headerLine, delimiter);

            int titleIdx = Array.FindIndex(headers, h => h.Equals(titleColumn, StringComparison.OrdinalIgnoreCase));
            if (titleIdx < 0) return BadRequest($"Colonna titolo '{titleColumn}' non trovata nel file");

            int? priorityIdx = FindColumnIndex(headers, priorityColumn);
            int? linkIdx = FindColumnIndex(headers, linkColumn);

            var rows = new List<ImportedTaskRow>();
            string? line;

            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                var cells = CsvHelper.SplitLine(line, delimiter);
                if (titleIdx >= cells.Length) continue;

                var title = cells[titleIdx];
                if (string.IsNullOrWhiteSpace(title)) continue;

                var metadata = new Dictionary<string, string>();

                if (priorityIdx is int pIdx && pIdx < cells.Length && !string.IsNullOrWhiteSpace(cells[pIdx]))
                    metadata["priority"] = cells[pIdx];

                if (linkIdx is int lIdx && lIdx < cells.Length && !string.IsNullOrWhiteSpace(cells[lIdx]))
                    metadata["link"] = cells[lIdx];

                rows.Add(new ImportedTaskRow { Title = title, Metadata = metadata });
            }

            _roomManager.ImportTasks(room, rows);
            await _roomManager.BroadcastRoomStateAsync(room);

            return Ok();
        }

        private static int? FindColumnIndex(string[] headers, string? columnName)
        {
            if (string.IsNullOrWhiteSpace(columnName)) return null;
            var idx = Array.FindIndex(headers, h => h.Equals(columnName, StringComparison.OrdinalIgnoreCase));
            return idx >= 0 ? idx : null;
        }

        [HttpPost]
        public IActionResult CreateRoom()
        {
            var room = _roomManager.CreateRoom();
            return Ok(new { roomId = room.RoomId });
        }

        [HttpGet("{roomId}/exportTasks")]
        public IActionResult ExportTasks(string roomId)
        {
            if (!_roomManager.TryGetRoom(roomId, out var room))
                return NotFound($"Stanza '{roomId}' non trovata");

            var sb = new StringBuilder();
            sb.AppendLine("Titolo,Stima Finale,Priorità,Link");

            foreach (var task in room.Tasks)
            {
                var title = EscapeCsv(task.Title);
                var estimate = EscapeCsv(task.FinalEstimate ?? "");
                var priority = EscapeCsv(task.Metadata.GetValueOrDefault("priority", ""));
                var link = EscapeCsv(task.Metadata.GetValueOrDefault("link", ""));
                sb.AppendLine($"{title},{estimate},{priority},{link}");
            }

            var bytes = Encoding.UTF8.GetBytes(sb.ToString());
            return File(bytes, "text/csv", $"{roomId}-tasks.csv");
        }

        [HttpGet("{roomId}/exists")]
        public IActionResult RoomExists(string roomId)
        {
            return _roomManager.TryGetRoom(roomId, out _) ? Ok() : NotFound();
        }

        private static string EscapeCsv(string value)
        {
            if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            {
                return $"\"{value.Replace("\"", "\"\"")}\"";
            }
            return value;
        }
    }
}
