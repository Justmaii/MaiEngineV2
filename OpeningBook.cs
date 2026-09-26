namespace ChessEngine;

/// <summary>
/// Açılış kitabı. Satırlar UCI hamle dizisi olarak yazılır, program açılışında
/// baştan oynatılarak "pozisyon anahtarı -> hamle" sözlüğüne çevrilir.
///
/// Ağırlık = o hamlenin kaç satırda geçtiği. Böylece ana varyantlar daha sık,
/// yan varyantlar daha seyrek seçilir; her oyun aynı olmaz.
///
/// Not: PGN/Polyglot gibi hazır formatlar yerine düz metin tercih edildi —
/// dosya bağımlılığı yok, satır eklemek bir satır yazmak kadar kolay.
/// </summary>
public static class OpeningBook
{
    private readonly struct Entry
    {
        public readonly Move Move;
        public readonly int Weight;
        public Entry(Move move, int weight) { Move = move; Weight = weight; }
    }

    private static readonly Dictionary<ulong, List<Entry>> Book = new();

    /// <summary>Kitaptan çıkıldıktan sonra kaç yarım hamle daha bakılacağı.</summary>
    private const int MaxBookPly = 12;

    /// <summary>Bozuk satırlar burada birikir — testte kontrol ediliyor.</summary>
    public static readonly List<string> Errors = new();

    public static int PositionCount => Book.Count;

    private static readonly string[] Lines =
    {
        // ---------------- 1.e4 ----------------
        // İspanyol (Ruy Lopez)
        "e2e4 e7e5 g1f3 b8c6 f1b5 a7a6 b5a4 g8f6 e1g1 f8e7",
        "e2e4 e7e5 g1f3 b8c6 f1b5 a7a6 b5c6 d7c6 e1g1 f7f6",
        "e2e4 e7e5 g1f3 b8c6 f1b5 g8f6 e1g1 f6e4 d2d4 e4d6",
        // İtalyan
        "e2e4 e7e5 g1f3 b8c6 f1c4 f8c5 c2c3 g8f6 d2d3 d7d6",
        "e2e4 e7e5 g1f3 b8c6 f1c4 g8f6 d2d3 f8c5 c2c3 d7d6",
        // İskoç
        "e2e4 e7e5 g1f3 b8c6 d2d4 e5d4 f3d4 g8f6 b1c3 f8b4",
        // Petrov
        "e2e4 e7e5 g1f3 g8f6 f3e5 d7d6 e5f3 f6e4 d2d4 d6d5",
        // Viyana
        "e2e4 e7e5 b1c3 g8f6 f1c4 b8c6 d2d3 f8b4",
        // Sicilya
        "e2e4 c7c5 g1f3 d7d6 d2d4 c5d4 f3d4 g8f6 b1c3 a7a6",  // Najdorf
        "e2e4 c7c5 g1f3 d7d6 d2d4 c5d4 f3d4 g8f6 b1c3 g7g6",  // Ejderha
        "e2e4 c7c5 g1f3 b8c6 d2d4 c5d4 f3d4 g8f6 b1c3 e7e5",  // Sveshnikov
        "e2e4 c7c5 g1f3 e7e6 d2d4 c5d4 f3d4 a7a6 f1d3 g8f6",  // Kan
        "e2e4 c7c5 b1c3 b8c6 g2g3 g7g6 f1g2 f8g7",            // Kapalı Sicilya
        // Fransız
        "e2e4 e7e6 d2d4 d7d5 b1c3 g8f6 c1g5 f8e7",
        "e2e4 e7e6 d2d4 d7d5 b1d2 g8f6 e4e5 f6d7",
        "e2e4 e7e6 d2d4 d7d5 e4e5 c7c5 c2c3 b8c6",
        // Caro-Kann
        "e2e4 c7c6 d2d4 d7d5 b1c3 d5e4 c3e4 c8f5",
        "e2e4 c7c6 d2d4 d7d5 e4e5 c8f5 g1f3 e7e6",
        // İskandinav
        "e2e4 d7d5 e4d5 d8d5 b1c3 d5a5 d2d4 g8f6",
        // Alekhine
        "e2e4 g8f6 e4e5 f6d5 d2d4 d7d6 g1f3 c8g4",
        // Pirc / Modern
        "e2e4 d7d6 d2d4 g8f6 b1c3 g7g6 g1f3 f8g7",
        "e2e4 g7g6 d2d4 f8g7 b1c3 d7d6 g1f3 g8f6",

        // ---------------- 1.d4 ----------------
        // Vezir gambiti kabul edilmemiş
        "d2d4 d7d5 c2c4 e7e6 b1c3 g8f6 c1g5 f8e7 e2e3 e8g8",
        "d2d4 d7d5 c2c4 e7e6 b1c3 c7c6 g1f3 g8f6 e2e3 b8d7",
        // Slav
        "d2d4 d7d5 c2c4 c7c6 g1f3 g8f6 b1c3 d5c4 a2a4 c8f5",
        // Vezir gambiti kabul edilmiş
        "d2d4 d7d5 c2c4 d5c4 g1f3 g8f6 e2e3 e7e6 f1c4 c7c5",
        // Nimzo-Hint
        "d2d4 g8f6 c2c4 e7e6 b1c3 f8b4 e2e3 e8g8 f1d3 d7d5",
        // Vezir Hint
        "d2d4 g8f6 c2c4 e7e6 g1f3 b7b6 g2g3 c8b7 f1g2 f8e7",
        // Şah Hint
        "d2d4 g8f6 c2c4 g7g6 b1c3 f8g7 e2e4 d7d6 g1f3 e8g8",
        // Grünfeld
        "d2d4 g8f6 c2c4 g7g6 b1c3 d7d5 c4d5 f6d5 e2e4 d5c3",
        // Benoni
        "d2d4 g8f6 c2c4 c7c5 d4d5 e7e6 b1c3 e6d5 c4d5 d7d6",
        // London
        "d2d4 d7d5 c1f4 g8f6 e2e3 e7e6 g1f3 f8d6",
        // Hollanda
        "d2d4 f7f5 g2g3 g8f6 f1g2 e7e6 g1f3 f8e7",

        // ---------------- 1.Nf3 / 1.c4 ----------------
        "g1f3 d7d5 g2g3 g8f6 f1g2 e7e6 e1g1 f8e7",
        "g1f3 g8f6 c2c4 e7e6 b1c3 d7d5 d2d4 f8e7",
        "c2c4 e7e5 b1c3 g8f6 g1f3 b8c6 g2g3 d7d5",
        "c2c4 g8f6 b1c3 e7e6 g1f3 d7d5 d2d4 f8e7",
        "c2c4 c7c5 g1f3 g8f6 b1c3 b8c6 g2g3 g7g6",
    };

    static OpeningBook()
    {
        foreach (string line in Lines)
        {
            var board = new Board();
            int ply = 0;

            foreach (string token in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (ply >= MaxBookPly) break;

                var move = Uci.ParseMove(board, token);
                if (move.IsNull)
                {
                    // Satırda yazım hatası var — sessizce yutma, kaydet.
                    Errors.Add($"{line}  ->  geçersiz hamle: {token} ({board.ToFen()})");
                    break;
                }

                Add(board.ZobristKey, move);
                board.MakeMove(move);
                ply++;
            }
        }
    }

    private static void Add(ulong key, Move move)
    {
        if (!Book.TryGetValue(key, out var entries))
            Book[key] = entries = new List<Entry>();

        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].Move.From == move.From && entries[i].Move.To == move.To)
            {
                entries[i] = new Entry(move, entries[i].Weight + 1);
                return;
            }
        }
        entries.Add(new Entry(move, 1));
    }

    /// <summary>Bu pozisyon kitapta varsa ağırlığa göre rastgele bir hamle döndürür,
    /// yoksa boş hamle. Boş hamle gelirse normal arama yapılır.</summary>
    public static Move Probe(Board board, Random random)
    {
        if (!Book.TryGetValue(board.ZobristKey, out var entries) || entries.Count == 0)
            return default;

        int total = entries.Sum(e => e.Weight);
        int pick = random.Next(total);

        foreach (var entry in entries)
        {
            pick -= entry.Weight;
            if (pick < 0) return entry.Move;
        }
        return entries[0].Move;
    }
}
