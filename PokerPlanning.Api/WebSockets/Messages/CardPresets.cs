namespace PokerPlanning.Api.WebSockets.Messages
{
    public static class CardPresets
    {
        public static readonly Dictionary<string, string[]> All = new()
        {
            ["fibonacci"] = new[] { "0", "1", "2", "3", "5", "8", "13", "21", "?", "☕" },
            ["tshirt"] = new[] { "XS", "S", "M", "L", "XL", "XXL", "?", "☕" }
        };
    }
}
