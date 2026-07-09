using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TestSocket.WebSockets.Messages;
using TestSocket.WebSockets.Models;

namespace TestSocket.WebSockets
{
    public class RoomManager
    {

        private static readonly TimeSpan GracePeriod = TimeSpan.FromMinutes(5);
        // private static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(15);

        private readonly ConcurrentDictionary<string, Room> _rooms = new();

        public Room GetOrCreateRoom(string roomId)
        {
            var room = _rooms.GetOrAdd(roomId, id => new Room { RoomId = id });

            return room;
        }

        /// <summary>
        /// Join o riconnessione. Se lo userId esiste già nella stanza (era disconnesso),
        /// lo stato precedente (voto incluso) viene ripristinato sul nuovo socket.
        /// </summary>
        public bool Join(Room room, WebSocket socket, string userId, string userName, string role) {
            if (room.ParticipantsByUserId.TryGetValue(userId, out var existing))
            {
                existing.Socket = socket;
                existing.DisconnectedAt = null;
                existing.UserName = userName;
            } 
            else
            {
                room.ParticipantsByUserId[userId] = new Participant { 
                    UserId = userId,
                    UserName = userName, 
                    Role = role,
                    Socket = socket
                };
            }

            room.UserIdBySocket[socket] = userId;
            return true;
        }

        /// <summary>
        /// Chiamato quando un socket si chiude (close pulito o eccezione).
        /// Non elimina subito il partecipante: lo marca come disconnesso e lascia il grace period
        /// fare il suo corso tramite CleanupStaleParticipants.
        /// </summary>
        public void HandleSocketClosed(Room room, WebSocket socket)
        {
            Console.WriteLine("[HandleSocketClosed] chiamato");

            if (!room.UserIdBySocket.TryRemove(socket, out var userId))
            {
                Console.WriteLine("[HandleSocketClosed] socket non trovato in UserIdBySocket");
                return;
            }

            Console.WriteLine($"[HandleSocketClosed] userId={userId}");

            if (room.ParticipantsByUserId.TryGetValue(userId, out var participant))
            {
                // solo se il socket che si è chiuso è ancora quello corrente del partecipante:
                // evita race in cui una riconnessione rapidissima già sostituisce il socket
                if (participant.Socket == socket)
                {
                    participant.Socket = null;
                    participant.DisconnectedAt = DateTime.UtcNow;
                    Console.WriteLine($"[HandleSocketClosed] userId={userId} marcato disconnesso");
                }
            }
        }

        /// <summary>
        /// Da chiamare periodicamente (background task): rimuove definitivamente
        /// i partecipanti disconnessi da più del grace period.
        /// </summary>
        public IEnumerable<Room> CleanupStaleParticipants()
        {
            var affectedRooms = new List<Room>();

            foreach (var room in _rooms.Values)
            {
                var staleUserIds = room.ParticipantsByUserId.Values
                    .Where(p => p.DisconnectedAt.HasValue && DateTime.UtcNow - p.DisconnectedAt.Value > GracePeriod)
                    .Select(p => p.UserId)
                    .ToArray();

                foreach (var userId in staleUserIds)
                {
                    room.ParticipantsByUserId.TryRemove(userId, out _);
                }

                if (staleUserIds.Length > 0)
                {
                    affectedRooms.Add(room);
                }

                if (room.ParticipantsByUserId.IsEmpty)
                {
                    _rooms.TryRemove(room.RoomId, out _);
                }
            }

            return affectedRooms;
        }

        public bool TrySetVote(Room room, WebSocket socket, string value)
        {
            if (!room.UserIdBySocket.TryGetValue(socket, out var userId)) return false;
            if (!room.ParticipantsByUserId.TryGetValue(userId, out var participant)) return false;

            // Il facilitator non vota
            if (participant.Role == "facilitator")
                return false;

            participant.Vote = value;
            return true;
        }

        public void Reveal(Room room) 
        {
            room.CardsRevealed = true;

            var activeTask = room.Tasks.FirstOrDefault(t => t.Id == room.ActiveTaskId);
            if (activeTask != null)
            {
                activeTask.Status = PokerTaskStatus.Voted;
                activeTask.LastVotes = room.ParticipantsByUserId.Values
                    .Where(p => p.Role != "facilitator")
                    .Select(p => new VoteResult { UserName = p.UserName, Value = p.Vote })
                    .ToList();
            }
        } 

        public void Reset(Room room)
        {
            room.CardsRevealed = false;
            foreach(var partecipan in room.ParticipantsByUserId.Values)
            {
                partecipan.Vote = null;
            }
        }

        public void ChangePreset(Room room, string preset)
        {
            if (CardPresets.All.ContainsKey(preset))
            {
                room.ActivePreset = preset;
            }
        }

        public async Task BroadcastRoomStateAsync(Room room)
        {
            var payload = new
            {
                type = "roomState",
                preset = room.ActivePreset,
                revealed = room.CardsRevealed,
                activeTaskId = room.ActiveTaskId,
                tasks = room.Tasks.Select(t => new
                {
                    t.Id,
                    t.Title,
                    status = t.Status.ToString(),
                    lastVotes = t.LastVotes
                }),
                participants = room.ParticipantsByUserId.Values.Select(p => new
                {
                    p.UserName,
                    p.Role,
                    hasVoted = p.HasVoted,
                    connected = p.IsConnected
                })
            };

            await BroadcastAsync(room, payload);
        }

        public async Task BroadcastVotesRevealedAsync(Room room)
        {
            var payload = new
            {
                type = "votesRevealed",
                votes = room.ParticipantsByUserId.Values
                    .Where(p => p.Role != "facilitator")
                    .Select(p => new { p.UserName, value = p.Vote })
            };

            await BroadcastAsync(room, payload);
        }

        public async Task BroadcastAsync(Room room, object payload)
        {
            var json = JsonSerializer.Serialize(payload);
            var bytes = Encoding.UTF8.GetBytes(json);

            var sockets = room.ParticipantsByUserId.Values
                .Where(p => p.IsConnected)
                .Select(p => p.Socket!)
                .ToArray();

            foreach (var socket in sockets)
            {
                if (socket.State != WebSocketState.Open) continue;

                try
                {
                    await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
                }
                catch (WebSocketException)
                {

                }
            }
        }

        public void ImportTasks(Room room, IEnumerable<string> titles)
        {
            foreach(var title in titles)
            {
                room.Tasks.Add(new PokerTask
                {
                    Id = Guid.NewGuid().ToString(),
                    Title = title,
                });
            }
        }

        public bool SelectTask(Room room, string taskId)
        {
            var task = room.Tasks.FirstOrDefault(t => t.Id == taskId);
            if (task == null) return false;

            room.ActiveTaskId = taskId;
            task.Status = PokerTaskStatus.Voting;
            room.CardsRevealed = false;

            // nuovo task si rivota 
            foreach(var participant in room.ParticipantsByUserId.Values)
            {
                participant.Vote = null;
            }

            return true;
        }
    }
}
