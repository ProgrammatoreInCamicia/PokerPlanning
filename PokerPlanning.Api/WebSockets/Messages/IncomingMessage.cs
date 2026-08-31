namespace PokerPlanning.Api.WebSockets.Messages
{
    public class IncomingMessage
    {
        public required string Type { get; set; } // "join" | "vote" | "reveal" | "reset" | "changePreset"
        public string? UserId { get; set; }
        public string? UserName { get; set; }
        public string? Role { get; set; }
        public string? Value { get; set; }
        public string? Preset { get; set; }
        public string? TaskId { get; set; }
        public string? TargetUserId { get; set; }
        public string? Emoji { get; set; }
        public string? FinalEstimate { get; set; }
        public bool? Locked { get; set; }
        public string? TaskTitle { get; set; }
        public int? BreakMinutes { get; set; }
    }
}
