using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TestSocket.WebSockets.Messages;
using TestSocket.WebSockets.Models;

namespace TestSocket.WebSockets
{
    public class RoomManager
    {

        private static readonly TimeSpan GracePeriod = TimeSpan.FromMinutes(20);
        private static readonly TimeSpan RoomAbandonTimeout = TimeSpan.FromHours(5);
        private static readonly TimeSpan FacilitatorAbsenceTimeout = TimeSpan.FromMinutes(2);

        // private static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(15);

        private static readonly string[] Adjectives =
        {
            "stellare", "subacqueo", "volante", "silenzioso", "ardente", "baffuto",
            "glaciale", "curioso", "audace", "ribelle", "elegante", "splendente"
        };

        private static readonly string[] Animals =
        {
            "procione", "scimmia", "gufo", "tasso", "lince",
            "alpaca", "airone", "riccio", "orsetto", "falco"
        };

        private readonly ConcurrentDictionary<string, Room> _rooms = new();

        /// <summary>
        /// Crea una nuova stanza con nome generato automaticamente, garantendo unicità.
        /// </summary>
        public Room CreateRoom()
        {
            string roomId;
            do
            {
                var animal = Animals[Random.Shared.Next(Animals.Length)];
                var adjective = Adjectives[Random.Shared.Next(Adjectives.Length)];
                roomId = $"{animal}-{adjective}";
            }
            while (_rooms.ContainsKey(roomId));

            var room = new Room { RoomId = roomId };
            _rooms[roomId] = room;
            return room;
        }

        /// <summary>
        /// Recupera una stanza esistente. Non ne crea una nuova se non trovata.
        /// </summary>
        public bool TryGetRoom(string roomId, out Room room)
        {
            return _rooms.TryGetValue(roomId, out room!);
        }

        public bool CanUserJoin(Room room, string userId)
        {
            if (room.KickedUserIds.ContainsKey(userId)) return false;
            if (room.IsLocked && !room.KnownUserIds.ContainsKey(userId)) return false;
            return true;
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
            room.KnownUserIds[userId] = 0;
            room.EmptySince = null;
            return true;
        }

        public bool SetRoomLocked(Room room, WebSocket socket, bool locked)
        {
            if (!IsFacilitator(room, socket)) return false;
            room.IsLocked = locked;
            return true;
        }

        public async Task<bool> KickParticipant(Room room, WebSocket socket, string targetUserId)
        {
            if (!IsFacilitator(room, socket)) return false;
            if (!room.ParticipantsByUserId.TryGetValue(targetUserId, out var target)) return false;
            if (target.Role == "facilitator") return false;

            room.KickedUserIds[targetUserId] = 0;

            if (target.Socket is { State: WebSocketState.Open } socketToKick)
            {
                var payload = JsonSerializer.Serialize(new { type = "kicked" }, JsonOptions);
                var bytes = Encoding.UTF8.GetBytes(payload);

                try
                {
                    await socketToKick.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
                }
                catch (WebSocketException)
                {
                    // il client si è già disconnesso da solo prima ancora di ricevere l'avviso
                }

                try
                {
                    if (socketToKick.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    {
                        await socketToKick.CloseAsync(WebSocketCloseStatus.NormalClosure, "kicked", CancellationToken.None);
                    }
                }
                catch (Exception ex) when (ex is WebSocketException or InvalidOperationException)
                {
                    // già chiuso dal client nel frattempo: nessun problema
                }

                room.UserIdBySocket.TryRemove(socketToKick, out _);
            }

            room.ParticipantsByUserId.TryRemove(targetUserId, out _);
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
            var now = DateTime.UtcNow;

            foreach (var room in _rooms.Values)
            {
                var roomChanged = false;

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
                    roomChanged = true;
                }

                if (AutoPromoteFacilitatorIfNeeded(room, now))
                {
                    roomChanged = true;
                }

                if (roomChanged)
                    affectedRooms.Add(room);

                if (room.ParticipantsByUserId.IsEmpty)
                {
                    room.EmptySince ??= now;

                    if (now - room.EmptySince.Value > RoomAbandonTimeout)
                    {
                        _rooms.TryRemove(room.RoomId, out _);
                    }
                }
                else
                {
                    room.EmptySince = null; // c'è ancora qualcuno, annulla il conto alla rovescia
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

        public bool Reveal(Room room, WebSocket socket) 
        {
            if (!IsFacilitator(room, socket)) return false;

            room.CardsRevealed = true;

            var activeTask = room.Tasks.FirstOrDefault(t => t.Id == room.ActiveTaskId);
            if (activeTask != null)
            {
                activeTask.Status = PokerTaskStatus.Voted;
                activeTask.LastVotes = room.ParticipantsByUserId.Values
                    .Where(p => p.Role != "facilitator")
                    .Select(p => new VoteResult { 
                        UserName = p.UserName, 
                        Value = p.Vote,
                        UserId = p.UserId
                    })
                    .ToList();
            }
            return true;
        } 

        public bool Reset(Room room, WebSocket socket)
        {
            if (!IsFacilitator(room, socket)) return false;

            room.CardsRevealed = false;
            foreach(var participant in room.ParticipantsByUserId.Values)
            {
                participant.Vote = null;
            }

            return true;
        }

        public bool resetTasks(Room room, WebSocket socket)
        {
            if (!IsFacilitator(room, socket)) return false;

            room.ActiveTaskId = null;
            room.Tasks.Clear();

            return true;
        }

        public bool ChangePreset(Room room, string preset, WebSocket socket)
        {
            if (!IsFacilitator(room, socket)) return false;
            if (!CardPresets.All.ContainsKey(preset)) return false;

            room.ActivePreset = preset;
            return true;
        }

        public async Task BroadcastRoomStateAsync(Room room)
        {
            var payload = new
            {
                type = "roomState",
                preset = room.ActivePreset,
                revealed = room.CardsRevealed,
                activeTaskId = room.ActiveTaskId,
                locked = room.IsLocked,
                tasks = room.Tasks.Select(t => new
                {
                    id = t.Id,
                    title = t.Title,
                    status = t.Status.ToString(),
                    lastVotes = t.LastVotes,
                    metadata = t.Metadata,
                    finalEstimate = t.FinalEstimate
                }),
                participants = room.ParticipantsByUserId.Values.Select(p => new
                {
                    userName = p.UserName,
                    role = p.Role,
                    hasVoted = p.HasVoted,
                    connected = p.IsConnected,
                    userId = p.UserId,
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

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private async Task BroadcastAsync(Room room, object payload)
        {
            var json = JsonSerializer.Serialize(payload, JsonOptions);
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
                catch (WebSocketException ex)
                {
                    Console.WriteLine($"[BroadcastAsync] Invio fallito verso un socket: {ex.Message}");
                }
            }
        }

        public void ImportTasks(Room room, List<ImportedTaskRow> rows)
        {
            var newTasks = rows.Select(r => new PokerTask
            {
                Id = Guid.NewGuid().ToString(),
                Title = r.Title,
                Metadata = r.Metadata
            }).ToList();

            var ordered = newTasks
                .Select((t, idx) => (task: t, idx, priority: TryParsePriority(t)))
                .OrderByDescending(x => x.priority.HasValue)
                .ThenByDescending(x => x.priority ?? 0)
                .ThenBy(x => x.idx)
                .Select(x => x.task)
                .ToList();

            room.Tasks.AddRange(ordered);
        }

        private static double? TryParsePriority(PokerTask t)
        {
            if (t.Metadata.TryGetValue("priority", out var raw) &&
                double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var val))
            {
                return val;
            }
            return null;
        }

        public bool SelectTask(Room room, string taskId, WebSocket socket)
        {
            if (!IsFacilitator(room, socket)) return false;

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

        public bool IsFacilitator(Room room, WebSocket socket)
        {
            if (!room.UserIdBySocket.TryGetValue(socket, out var userId)) return false;
            if (!room.ParticipantsByUserId.TryGetValue(userId, out var participant)) return false;
            return participant.Role == "facilitator";
        }

        public async Task<bool> ThrowEmoji(Room room, WebSocket socket, string targetUserId, string emoji)
        {
            if (!room.UserIdBySocket.TryGetValue(socket, out var fromUserId)) return false;
            if (!room.ParticipantsByUserId.ContainsKey(fromUserId)) return false;
            if (!room.ParticipantsByUserId.ContainsKey(targetUserId)) return false;

            var payload = new
            {
                type = "emojiThrown",
                id = Guid.NewGuid().ToString(),
                fromUserId,
                targetUserId,
                emoji
            };
            await BroadcastAsync(room, payload);
            return true;
        }

        public bool ConfirmEstimate(Room room, WebSocket socket, string taskId, string finalEstimate)
        {
            if (!IsFacilitator(room, socket)) return false;

            var task = room.Tasks.FirstOrDefault(t => t.Id == taskId);
            if (task == null) return false;

            task.FinalEstimate = finalEstimate;
            return true;
        }

        public bool PromoteToFacilitator(Room room, WebSocket socket, string targetUserId)
        {
            if (!IsFacilitator(room, socket)) return false;
            if (!room.UserIdBySocket.TryGetValue(socket, out var currentUserId)) return false;
            if (!room.ParticipantsByUserId.TryGetValue(currentUserId, out var currentFacilitator)) return false;
            if (!room.ParticipantsByUserId.TryGetValue(targetUserId, out var target)) return false;
            if (target.Role != "voter" || !target.IsConnected) return false;

            currentFacilitator.Role = "voter";
            target.Role = "facilitator";
            target.Vote = null; // i facilitator non votano

            return true;
        }

        public bool ChangeUserName(Room room, WebSocket socket, string newUserName)
        {
            if (!room.UserIdBySocket.TryGetValue(socket, out var userId)) return false;
            if (!room.ParticipantsByUserId.TryGetValue(userId, out var participant)) return false;

            participant.UserName = newUserName;
            return true;
        }

        private bool AutoPromoteFacilitatorIfNeeded(Room room, DateTime now)
        {
            var currentFacilitator = room.ParticipantsByUserId.Values
                .FirstOrDefault(p => p.Role == "facilitator");

            if (currentFacilitator == null) return false;
            if (!currentFacilitator.DisconnectedAt.HasValue) return false; // è connesso, nulla da fare
            if (now - currentFacilitator.DisconnectedAt.Value <= FacilitatorAbsenceTimeout) return false;

            var candidate = room.ParticipantsByUserId.Values
                .Where(p => p.Role == "voter" && p.IsConnected)
                .OrderBy(p => p.JoinedAt) // il più "anziano" in stanza
                .FirstOrDefault();

            if (candidate == null) return false; // nessuno disponibile, si riprova al prossimo giro

            currentFacilitator.Role = "voter";
            candidate.Role = "facilitator";
            candidate.Vote = null;

            return true;
        }
    }
}
