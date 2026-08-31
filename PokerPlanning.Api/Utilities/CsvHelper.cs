namespace PokerPlanning.Api.Utilities
{
    public static class CsvHelper
    {
        private static readonly string[] TitleAliases = { "task", "title", "titolo", "nome" };
        private static readonly string[] PriorityAliases = { "priority", "priorita", "priorità", "prio" };
        private static readonly string[] LinkAliases = { "link", "url", "ticket", "collegamento" };

        public static char DetectDelimiter(string headerLine)
        {
            var commaCount = headerLine.Count(c => c == ',');
            var semicolonCount = headerLine.Count(c => c == ';');
            return semicolonCount > commaCount ? ';' : ',';
        }

        public static string[] SplitLine(string line, char delimiter)
        {
            return line.Split(delimiter).Select(c => c.Trim().Trim('"')).ToArray();
        }

        public static (string? TitleColumn, string? PriorityColumn, string? LinkColumn) SuggestMapping(string[] headers)
        {
            return (
                FindByAlias(headers, TitleAliases) ?? headers.FirstOrDefault(),
                FindByAlias(headers, PriorityAliases),
                FindByAlias(headers, LinkAliases)
            );
        }

        private static string? FindByAlias(string[] headers, string[] aliases)
        {
            foreach (var alias in aliases)
            {
                var match = headers.FirstOrDefault(h => h.Equals(alias, StringComparison.OrdinalIgnoreCase));
                if (match != null) return match;
            }
            return null;
        }
    }
}
