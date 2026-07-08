using System.Net.WebSockets;
using System.Text;
using TestSocket.WebSockets;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

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

app.UseHttpsRedirection();

app.UseAuthorization();


app.MapControllers();

app.UseWebSockets();
app.Map("ws/poker/{roomId}", async (HttpContext context, string roomId, PokerConnectionHandler handler) =>
//app.Map("ws/echo", async (HttpContext context) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        return;
    }

    var options = new WebSocketAcceptContext
    {
        KeepAliveInterval = TimeSpan.FromSeconds(15),
    };

    using var socket = await context.WebSockets.AcceptWebSocketAsync(options);
    await handler.HandleAsync(socket, roomId);
});

app.Run();
