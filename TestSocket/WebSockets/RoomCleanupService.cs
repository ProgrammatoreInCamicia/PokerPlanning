namespace TestSocket.WebSockets
{
    public class RoomCleanupService : BackgroundService
    {
        private readonly RoomManager _roomManager;
        private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

        public RoomCleanupService(RoomManager roomManager)
        {
            _roomManager = roomManager;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(Interval, stoppingToken);

                var affectedRooms = _roomManager.CleanupStaleParticipants();
                foreach (var room in affectedRooms)
                {
                    await _roomManager.BroadcastRoomStateAsync(room);
                }
            }
        }
    }
}
