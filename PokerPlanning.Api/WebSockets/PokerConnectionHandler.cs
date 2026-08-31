using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using PokerPlanning.Api.WebSockets.Messages;
using PokerPlanning.Api.WebSockets.Models;

namespace PokerPlanning.Api.WebSockets
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
                        try
                        {
                            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                            {
                                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                            }
                        }
                        catch (Exception ex) when (ex is WebSocketException or InvalidOperationException)
                        {
                            // il socket è già stato chiuso altrove (es. da un kick concorrente): nessun problema
                        }
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
                    if (await RejectIfTooLongAsync(socket, message.UserId, FieldLimits.UserId, "userId")) return;
                    if (await RejectIfTooLongAsync(socket, message.UserName, FieldLimits.UserName, "Il nome")) return;
                    if (!_roomManager.CanUserJoin(room, message.UserId))
                    {
                        var reason = room.KickedUserIds.ContainsKey(message.UserId) ? "kicked" : "locked";
                        var payload = JsonSerializer.Serialize(new { type = "joinRejected", reason });
                        var bytes = Encoding.UTF8.GetBytes(payload);
                        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
                        await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, reason, CancellationToken.None);
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
                    if (await RejectIfTooLongAsync(socket, message.Value, FieldLimits.VoteValue, "Il voto")) return;
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
                    if (await RejectIfTooLongAsync(socket, message.TaskId, FieldLimits.TaskId, "taskId")) return;
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
                    if (_roomManager.ResetTasks(room, socket))
                    {
                        await _roomManager.BroadcastRoomStateAsync(room);
                    }
                    else
                    {
                        await SendErrorAsync(socket, $"Solo il facilitator può fare questa azione");
                        return;
                    }
                    break;
                case "throwEmoji":
                    if (string.IsNullOrWhiteSpace(message.TargetUserId) || string.IsNullOrWhiteSpace(message.Emoji))
                    {
                        await SendErrorAsync(socket, "targetUserId ed emoji sono obbligatori");
                        return;
                    }
                    if (await RejectIfTooLongAsync(socket, message.TargetUserId, FieldLimits.UserId, "targetUserId")) return;
                    if (await RejectIfTooLongAsync(socket, message.Emoji, FieldLimits.Emoji, "L'emoji")) return;
                    await _roomManager.ThrowEmoji(room, socket, message.TargetUserId, message.Emoji);
                    break;
                case "confirmEstimate":
                    if (string.IsNullOrWhiteSpace(message.TaskId) || string.IsNullOrWhiteSpace(message.FinalEstimate))
                    {
                        await SendErrorAsync(socket, "taskId e finalEstimate sono obbligatori");
                        return;
                    }
                    if (await RejectIfTooLongAsync(socket, message.TaskId, FieldLimits.TaskId, "taskId")) return;
                    if (await RejectIfTooLongAsync(socket, message.FinalEstimate, FieldLimits.FinalEstimate, "La stima finale")) return;
                    if (_roomManager.ConfirmEstimate(room, socket, message.TaskId, message.FinalEstimate))
                    {
                        await _roomManager.BroadcastRoomStateAsync(room);
                    }
                    else
                    {
                        await SendErrorAsync(socket, "Solo il facilitator può confermare la stima");
                    }
                    break;
                case "kickParticipant":
                    if (string.IsNullOrWhiteSpace(message.TargetUserId))
                    {
                        await SendErrorAsync(socket, "targetUserId mancante");
                        return;
                    }
                    if (await RejectIfTooLongAsync(socket, message.TargetUserId, FieldLimits.UserId, "targetUserId")) return;
                    if (await _roomManager.KickParticipant(room, socket, message.TargetUserId))
                    {
                        await _roomManager.BroadcastRoomStateAsync(room);
                    }
                    else
                    {
                        await SendErrorAsync(socket, "Impossibile rimuovere questo partecipante");
                    }
                    break;
                case "setRoomLocked":
                    if (_roomManager.SetRoomLocked(room, socket, message.Locked ?? false))
                    {
                        await _roomManager.BroadcastRoomStateAsync(room);
                    }
                    else
                    {
                        await SendErrorAsync(socket, "Solo il facilitator può bloccare/sbloccare la stanza");
                    }
                    break;
                case "promoteToFacilitator":
                    if (string.IsNullOrWhiteSpace(message.TargetUserId))
                    {
                        await SendErrorAsync(socket, "targetUserId mancante");
                        return;
                    }
                    if (await RejectIfTooLongAsync(socket, message.TargetUserId, FieldLimits.UserId, "targetUserId")) return;
                    if (_roomManager.PromoteToFacilitator(room, socket, message.TargetUserId))
                    {
                        await _roomManager.BroadcastRoomStateAsync(room);
                    }
                    else
                    {
                        await SendErrorAsync(socket, "Impossibile promuovere questo partecipante");
                    }
                    break;
                case "changeUserName":
                    if (string.IsNullOrWhiteSpace(message.UserName))
                    {
                        await SendErrorAsync(socket, "userName mancante");
                        return;
                    }
                    if (await RejectIfTooLongAsync(socket, message.UserName, FieldLimits.UserName, "Il nome")) return;
                    if (_roomManager.ChangeUserName(room, socket, message.UserName))
                    {
                        await _roomManager.BroadcastRoomStateAsync(room);
                    }
                    else
                    {
                        await SendErrorAsync(socket, "Impossibile cambiare nome");
                    }
                    break;
                case "addTask":
                    if (string.IsNullOrWhiteSpace(message.TaskTitle))
                    {
                        await SendErrorAsync(socket, "Titolo task mancante");
                        return;
                    }
                    if (await RejectIfTooLongAsync(socket, message.TaskTitle, FieldLimits.TaskTitle, "Il titolo del task")) return;
                    if (_roomManager.AddTask(room, socket, message.TaskTitle))
                    {
                        await _roomManager.BroadcastRoomStateAsync(room);
                    }
                    else
                    {
                        await SendErrorAsync(socket, "Solo il facilitator può aggiungere task");
                    }
                    break;
                case "deleteTask":
                    if (string.IsNullOrWhiteSpace(message.TaskId))
                    {
                        await SendErrorAsync(socket, "taskId mancante");
                        return;
                    }
                    if (await RejectIfTooLongAsync(socket, message.TaskId, FieldLimits.TaskId, "taskId")) return;
                    if (_roomManager.DeleteTask(room, socket, message.TaskId))
                    {
                        await _roomManager.BroadcastRoomStateAsync(room);
                    }
                    else
                    {
                        await SendErrorAsync(socket, "Impossibile eliminare questo task");
                    }
                    break;
                case "startBreak":
                    if (message.BreakMinutes is not int minutes)
                    {
                        await SendErrorAsync(socket, "Durata pausa mancante");
                        return;
                    }
                    if (_roomManager.StartBreak(room, socket, minutes))
                    {
                        await _roomManager.BroadcastRoomStateAsync(room);
                    }
                    else
                    {
                        await SendErrorAsync(socket, "Impossibile avviare la pausa");
                    }
                    break;
                case "cancelBreak":
                    if (_roomManager.CancelBreak(room, socket))
                    {
                        await _roomManager.BroadcastRoomStateAsync(room);
                    }
                    else
                    {
                        await SendErrorAsync(socket, "Impossibile annullare la pausa");
                    }
                    break;
                default:
                    await SendErrorAsync(socket, $"Tipo messaggio sconosciuto: {message.Type}");
                    break;
            }
        }

        /// <summary>
        /// Restituisce true (e ha già informato il client) se il campo sfora il suo tetto.
        /// Rifiuta invece di troncare: sui messaggi interattivi un valore tagliato in
        /// silenzio è più confondente di un errore esplicito.
        /// </summary>
        private static async Task<bool> RejectIfTooLongAsync(WebSocket socket, string value, int max, string fieldName)
        {
            if (value.Length <= max) return false;
            await SendErrorAsync(socket, $"{fieldName} supera il limite di {max} caratteri");
            return true;
        }

        private static async Task SendErrorAsync(WebSocket socket, string message)
        {
            var payload = JsonSerializer.Serialize(new { type = "error", message });
            var bytes = Encoding.UTF8.GetBytes(payload);
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
    }
}
