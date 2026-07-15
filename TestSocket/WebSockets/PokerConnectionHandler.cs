using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TestSocket.WebSockets.Messages;
using TestSocket.WebSockets.Models;

namespace TestSocket.WebSockets
{
    public class PokerConnectionHandler
    {
        private readonly RoomManager _roomManager;

        public PokerConnectionHandler(RoomManager roomManager)
        {
            _roomManager = roomManager;
        }

        public async Task HandleAsync(WebSocket socket, Room room)
        {
            var buffer = new byte[4096];
            try
            {
                while(socket.State == WebSocketState.Open)
                {
                    WebSocketReceiveResult result;

                    try
                    {
                        result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                    }
                    catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
                    {
                        Console.WriteLine($"[HandleAsync] Connessione chiusa per timeout/errore: {ex.GetType().Name} - {ex.Message}");
                        break;
                    }

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                        break;
                    }

                    var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    await DispatchAsync(socket, room, json);
                }
            }
            finally
            {
                _roomManager.HandleSocketClosed(room, socket);
                await _roomManager.BroadcastRoomStateAsync(room);
            }
        }

        private async Task DispatchAsync(WebSocket socket, Room room, string json)
        {
            IncomingMessage? message;
            try
            {
                message = JsonSerializer.Deserialize<IncomingMessage>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                });
            }
            catch (JsonException)
            {
                await SendErrorAsync(socket, "Messaggio JSON malformato");
                return;
            }

            if (message == null)
            {
                await SendErrorAsync(socket, "Messaggio vuoto");
                return;
            }

            switch (message.Type)
            {
                case "join":
                    if (string.IsNullOrWhiteSpace(message.UserId) || 
                        string.IsNullOrWhiteSpace(message.UserName) || 
                        string.IsNullOrWhiteSpace(message.Role))
                    {
                        await SendErrorAsync(socket, "userId, userName e role sono obbligatori per il join");
                        return;
                    }
                    if (message.Role != "voter" && message.Role != "facilitator")
                    {
                        await SendErrorAsync(socket, "role deve essere 'voter' o 'facilitator'");
                        return;
                    }
                    _roomManager.Join(room, socket, message.UserId, message.UserName, message.Role);
                    await _roomManager.BroadcastRoomStateAsync(room);
                    break;
                case "vote":
                    if (string.IsNullOrWhiteSpace(message.Value))
                    {
                        await SendErrorAsync(socket, "Valore voto mancante");
                        return;
                    }
                    if (_roomManager.TrySetVote(room, socket, message.Value))
                    {
                        await _roomManager.BroadcastRoomStateAsync(room);
                    }
                    else
                    {
                        await SendErrorAsync(socket, "Voto non valido: ruolo non ammesso o partecipante non riconosciuto");
                    }
                    break;
                case "reveal":
                    if (_roomManager.Reveal(room, socket))
                    {
                        await _roomManager.BroadcastVotesRevealedAsync(room);
                        await _roomManager.BroadcastRoomStateAsync(room);

                    } else
                    {
                        await SendErrorAsync(socket, $"Solo il facilitator può fare questa azione");
                        return;
                    }
                    break;
                case "reset":
                    if (_roomManager.Reset(room, socket))
                    {
                        await _roomManager.BroadcastRoomStateAsync(room);
                    }
                    else
                    {
                        await SendErrorAsync(socket, $"Solo il facilitator può fare questa azione");
                        return;
                    }
                    break;
                case "changePreset":
                    if (string.IsNullOrWhiteSpace(message.Preset) || !CardPresets.All.ContainsKey(message.Preset))
                    {
                        await SendErrorAsync(socket, $"Preset sconosciuto: {message.Preset}");
                        return;
                    }
                    if (!_roomManager.IsFacilitator(room, socket))
                    {
                        await SendErrorAsync(socket, "Solo il facilitator può cambiare il preset");
                        return;
                    }
                    if (_roomManager.ChangePreset(room, message.Preset, socket))
                    {
                        await _roomManager.BroadcastRoomStateAsync(room);
                    }
                    else
                    {
                        await SendErrorAsync(socket, $"Il preset non è più disponibile");
                        return;
                    }
                    
                    break;
                case "selectTask":
                    if (string.IsNullOrWhiteSpace(message.TaskId))
                    {
                        await SendErrorAsync(socket, "taskId mancante");
                        return;
                    }
                    if (!_roomManager.IsFacilitator(room, socket))
                    {
                        await SendErrorAsync(socket, "Solo il facilitator può selezionare un task");
                        return;
                    }
                    if (_roomManager.SelectTask(room, message.TaskId, socket))
                    {
                        await _roomManager.BroadcastRoomStateAsync(room);
                    }
                    else
                    {
                        await SendErrorAsync(socket, "Task non trovato");
                    }

                    break;
                case "resetTasks":
                    if (_roomManager.resetTasks(room, socket))
                    {
                        await _roomManager.BroadcastRoomStateAsync(room);
                    }
                    else
                    {
                        await SendErrorAsync(socket, $"Solo il facilitator può fare questa azione");
                        return;
                    }
                    break;
                default:
                    await SendErrorAsync(socket, $"Tipo messaggio sconosciuto: {message.Type}");
                    break;
            }
        }

        private static async Task SendErrorAsync(WebSocket socket, string message)
        {
            var payload = JsonSerializer.Serialize(new { type = "error", message });
            var bytes = Encoding.UTF8.GetBytes(payload);
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
    }
}
