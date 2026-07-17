using System.Net.WebSockets;

namespace TestSocket.WebSockets.Models
{
    public class Participant
    {
        public required string UserId { get; set; }
        public required string UserName { get; set; }
        public required string Role { get; set; } // Voter | Facilitator
        public string? Vote { get; set; }
        public DateTime JoinedAt { get; init; } = DateTime.UtcNow;

        public WebSocket? Socket { get; set; }
        public DateTime? DisconnectedAt { get; set; }

        public bool HasVoted => Vote != null;
        public bool IsConnected => Socket != null && Socket.State == WebSocketState.Open;
    }
}
