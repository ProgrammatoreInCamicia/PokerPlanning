using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace TestSocket.WebSockets.Models
{
    public class Room
    {
        public required string RoomId { get; init; }
        public string ActivePreset { get; set; } = "fibonacci";
        public bool CardsRevealed { get; set; }

        public List<PokerTask> Tasks { get; } = new();
        public string? ActiveTaskId { get; set; }
        public bool IsLocked { get; set; }

        // chiave = connessione WebSocket, così alla disconnessione sappiamo subito chi rimuovere
        public ConcurrentDictionary<string, Participant> ParticipantsByUserId { get; } = new();
        public ConcurrentDictionary<WebSocket, string> UserIdBySocket{ get; } = new();
        public ConcurrentDictionary<string, byte> KnownUserIds { get; } = new();  // mai ripulito
        public ConcurrentDictionary<string, byte> KickedUserIds { get; } = new(); // banditi esplicitamente

        public DateTime? EmptySince { get; set; }
        public DateTime? BreakEndsAt { get; set; }
    }
}
