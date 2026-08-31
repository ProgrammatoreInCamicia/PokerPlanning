using Microsoft.AspNetCore.Mvc;
using System.Text;
using PokerPlanning.Api.Utilities;
using PokerPlanning.Api.WebSockets;
using PokerPlanning.Api.WebSockets.Models;

namespace PokerPlanning.Api.Controllers
{
    /// <summary>
    /// Endpoint HTTP di contorno al canale WebSocket: creazione stanza, verifica esistenza
    /// e import/export CSV del backlog. Tutto il resto della sessione viaggia sul socket.
    /// </summary>
    [ApiController]
    [Route("api/rooms")]
    public class RoomController : ControllerBase
    {
        private readonly RoomManager _roomManager;

        public RoomController(RoomManager roomManager)
        {
            _roomManager = roomManager;
        }

        /// <summary>
        /// Legge solo l'intestazione del CSV e propone un mapping delle colonne, così il
        /// client può far confermare l'abbinamento all'utente prima di importare davvero.
        /// </summary>
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

            foreach (var task in RoomManager.GetTasksSnapshot(room))
            {
                var title = EscapeCsv(task.Title);
                var estimate = EscapeCsv(task.FinalEstimate ?? "");
                var priority = EscapeCsv(task.Metadata.GetValueOrDefault("priority", ""));
                var link = EscapeCsv(task.Metadata.GetValueOrDefault("link", ""));
                sb.AppendLine($"{title},{estimate},{priority},{link}");
            }

            // BOM esplicito: senza, Excel apre il CSV in ANSI e sfascia gli accenti
            var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
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
