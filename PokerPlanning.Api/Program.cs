using System.Text.Json;
using PokerPlanning.Api.WebSockets;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers()
     .AddJsonOptions(options =>
     {
         options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
     });

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Le origini consentite cambiano tra sviluppo e produzione: stanno in appsettings
// (Cors:AllowedOrigins) così un nuovo ambiente non richiede una ricompilazione.
var allowedOrigins = builder.Configuration
    .GetSection("Cors:AllowedOrigins")
    .Get<string[]>() ?? Array.Empty<string>();

builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy =>
    {
        policy.WithOrigins(allowedOrigins)
            .AllowAnyMethod()
            .AllowAnyHeader();
    });
});

// Lo stato vive in memoria nel processo: una sola istanza condivisa da tutte le connessioni.
builder.Services.AddSingleton<RoomManager>();
builder.Services.AddSingleton<PokerConnectionHandler>();
builder.Services.AddHostedService<RoomCleanupService>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("Frontend");

app.UseHttpsRedirection();

app.MapControllers();

app.UseWebSockets();

// Unico endpoint realtime: da qui in poi la sessione parla solo per messaggi JSON.
app.Map("ws/poker/{roomId}", async (HttpContext context, string roomId, PokerConnectionHandler handler, RoomManager roomManager) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    // Rifiutiamo prima dell'handshake: aprire il socket e chiuderlo subito dopo
    // farebbe scattare il retry con backoff del client su una stanza che non esiste.
    if (!roomManager.TryGetRoom(roomId, out var room))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        await context.Response.WriteAsync("Stanza non trovata");
        return;
    }

    var options = new WebSocketAcceptContext
    {
        KeepAliveInterval = TimeSpan.FromSeconds(15),
        KeepAliveTimeout = TimeSpan.FromSeconds(10)
    };

    using var socket = await context.WebSockets.AcceptWebSocketAsync(options);
    await handler.HandleAsync(socket, room);
});

app.Run();
