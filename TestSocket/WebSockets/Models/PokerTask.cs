namespace TestSocket.WebSockets.Models;

public enum PokerTaskStatus
{
    Pending,
    Voting,
    Voted
}

public class PokerTask
{
    public required string Id {  get; set; }
    public required string Title { get; set; }
    public PokerTaskStatus Status { get; set; } = PokerTaskStatus.Pending;
    public List <VoteResult> LastVotes { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new();
}

public class ImportedTaskRow
{
    public required string Title { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new();
}

public class VoteResult
{
    public required string UserName { get; set; }
    public required string UserId { get; set; }
    public string? Value { get; set; }
}
