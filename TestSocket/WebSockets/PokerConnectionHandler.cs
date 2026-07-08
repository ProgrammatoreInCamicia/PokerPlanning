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

        public async Task HandleAsync(WebSocket socket, string roomId)
        {
            var room = _roomManager.GetOrCreateRoom(roomId);
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
                    catch (WebSocketException)
                    {
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
                    _roomManager.Reveal(room);
                    await _roomManager.BroadcastVotesRevealedAsync(room);
                    await _roomManager.BroadcastRoomStateAsync(room);
                    break;
                case "reset":
                    _roomManager.Reset(room);
                    await _roomManager.BroadcastRoomStateAsync(room);
                    break;
                case "changePreset":
                    if (string.IsNullOrWhiteSpace(message.Preset) || !CardPresets.All.ContainsKey(message.Preset))
                    {
                        await SendErrorAsync(socket, $"Preset sconosciuto: {message.Preset}");
                        return;
                    }
                    _roomManager.ChangePreset(room, message.Preset);
                    await _roomManager.BroadcastRoomStateAsync(room);
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
