namespace PokerPlanning.Api.WebSockets.Messages
{
    /// <summary>
    /// Tetti di lunghezza sui campi testuali in arrivo. Il client li rispetta già con i
    /// suoi maxlength, ma un WebSocket aperto è a tutti gli effetti un'API pubblica:
    /// senza questi limiti chiunque può infilare qualche KB di testo in un campo e farlo
    /// ritrasmettere dal server a tutta la stanza, a ogni broadcast successivo.
    /// </summary>
    public static class FieldLimits
    {
        public const int UserId = 64;
        public const int UserName = 40;
        public const int TaskId = 64;
        public const int TaskTitle = 200;
        public const int FinalEstimate = 16;
        public const int VoteValue = 16;

        // le sequenze emoji con ZWJ (famiglie, bandiere, modificatori) arrivano
        // tranquillamente a una decina di code unit UTF-16: 32 lascia margine
        public const int Emoji = 32;

        // valori dei metadati importati da CSV (priorità, link al ticket)
        public const int MetadataValue = 500;

        /// <summary>
        /// Tronca preservando le coppie surrogate: tagliare a metà un carattere non-BMP
        /// (un emoji, per dire) produrrebbe una stringa non serializzabile in JSON.
        /// </summary>
        public static string Truncate(string value, int max)
        {
            if (value.Length <= max) return value;
            if (char.IsHighSurrogate(value[max - 1])) max--;
            return value[..max];
        }
    }
}
