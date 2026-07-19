using Microsoft.AspNetCore.Identity;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TestSocket.WebSockets;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers()
     .AddJsonOptions(options =>
     {
         options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
     });
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontendDev", policy =>
    {
        policy.WithOrigins(
                "http://localhost:4200",
                "http://localhost:5200",
                "https://poker.programmatoreincamicia.dev"
            )
            .AllowAnyMethod()
            .AllowAnyHeader();
    });
});

builder.Services.AddSingleton<RoomManager>();
builder.Services.AddSingleton<PokerConnectionHandler>();
builder.Services.AddHostedService<RoomCleanupService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("AllowFrontendDev");

app.UseHttpsRedirection();

app.UseAuthorization();


app.MapControllers();

app.UseWebSockets();
app.Map("ws/poker/{roomId}", async (HttpContext context, string roomId, PokerConnectionHandler handler, RoomManager roomManager) =>
//app.Map("ws/echo", async (HttpContext context) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        return;
    }

    if (!roomManager.TryGetRoom(roomId, out var room))
    {
        context.Response.StatusCode = 404;
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
