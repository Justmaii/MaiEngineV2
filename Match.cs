namespace ChessEngine;

/// <summary>
/// İki motor ayarını birbirine karşı oynatır. Derinlik ve düğüm sayısı
/// güzel görünebilir ama asıl soru "daha çok kazanıyor mu?" — bunu
/// ancak maç cevaplar.
/// </summary>
public static class Match
{
    public record Config(string Name, bool Advanced, bool AdvancedEval = false, bool SearchV2 = true, bool See = false);

    public static void Run(Config a, Config b, int games = 20, int moveTimeMs = 150, int maxPlies = 160)
    {
        int winsA = 0, winsB = 0, draws = 0;

        Console.WriteLine($"{a.Name}  vs  {b.Name}");
        Console.WriteLine($"{games} oyun, hamle başına {moveTimeMs} ms\n");

        for (int game = 0; game < games; game++)
        {
            // Renkler her oyunda değişsin — beyaz avantajı sonucu bozmasın.
            bool aIsWhite = game % 2 == 0;
            var result = PlayGame(a, b, aIsWhite, moveTimeMs, maxPlies, seed: game);

            if (result == 0) draws++;
            else if ((result > 0) == aIsWhite) winsA++;
            else winsB++;

            string symbol = result == 0 ? "=" : ((result > 0) == aIsWhite ? "1" : "0");
            Console.Write(symbol);
            if ((game + 1) % 10 == 0) Console.Write(" ");
        }

        double points = winsA + draws / 2.0;
        double percent = points / games * 100;

        Console.WriteLine($"\n\n{a.Name}: {winsA} galibiyet, {draws} beraberlik, {winsB} yenilgi");
        Console.WriteLine($"Skor: {points:0.#}/{games}  ({percent:0.#}%)");
        Console.WriteLine($"Yaklaşık elo farkı: {EloDifference(percent):+0;-0;0}");
        Console.WriteLine("(az oyunda hata payı büyüktür — yön göstergesi olarak oku)");
    }

    /// <returns>+1 beyaz kazandı, -1 siyah kazandı, 0 berabere</returns>
    private static int PlayGame(Config a, Config b, bool aIsWhite, int moveTimeMs, int maxPlies, int seed)
    {
        var board = new Board();

        var searchA = new Search(16) { UseAdvancedSearch = a.Advanced, UseAdvancedEval = a.AdvancedEval, UseSearchV2 = a.SearchV2, UseSee = a.See, Random = new Random(seed * 2) };
        var searchB = new Search(16) { UseAdvancedSearch = b.Advanced, UseAdvancedEval = b.AdvancedEval, UseSearchV2 = b.SearchV2, UseSee = b.See, Random = new Random(seed * 2 + 1) };

        for (int ply = 0; ply < maxPlies; ply++)
        {
            var legal = MoveGenerator.GenerateLegalMoves(board);
            if (legal.Count == 0)
            {
                if (!board.IsInCheck(board.SideToMove)) return 0;             // pat
                return board.SideToMove == Piece.White ? -1 : 1;              // mat
            }
            if (board.HalfmoveClock >= 100 || board.IsRepetition()) return 0;

            bool whiteToMove = board.SideToMove == Piece.White;
            var search = (whiteToMove == aIsWhite) ? searchA : searchB;

            var move = search.FindBestMove(board, maxDepth: 30, timeLimitMs: moveTimeMs, verbose: false);
            if (move.IsNull) return 0;
            board.MakeMove(move);
        }

        return 0; // hamle sınırına gelindi, berabere say
    }

    /// <summary>
    /// Kendi motorumuzu dışarıdaki bir UCI motoruna karşı oynatır.
    /// options: "UCI_LimitStrength=true,UCI_Elo=1800" gibi.
    /// </summary>
    public static void RunAgainstUci(string enginePath, int games, int moveTimeMs,
                                     string options = "", int maxPlies = 200)
    {
        using var opponent = new ExternalEngine(enginePath);

        foreach (string option in options.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] pair = option.Split('=', 2);
            if (pair.Length == 2) opponent.SetOption(pair[0].Trim(), pair[1].Trim());
        }
        opponent.IsReady();

        int wins = 0, losses = 0, draws = 0;
        Console.WriteLine($"MaiEngine  vs  {opponent.Name}" +
                          (options.Length > 0 ? $"  [{options}]" : ""));
        Console.WriteLine($"{games} oyun, hamle başına {moveTimeMs} ms\n");

        for (int game = 0; game < games; game++)
        {
            bool weAreWhite = game % 2 == 0;
            int result = PlayAgainstUci(opponent, weAreWhite, moveTimeMs, maxPlies, seed: game);

            if (result == 0) draws++;
            else if ((result > 0) == weAreWhite) wins++;
            else losses++;

            Console.Write(result == 0 ? "=" : ((result > 0) == weAreWhite ? "1" : "0"));
            if ((game + 1) % 10 == 0) Console.Write(" ");
        }

        double points = wins + draws / 2.0;
        double percent = points / games * 100;
        Console.WriteLine($"\n\nMaiEngine: {wins} galibiyet, {draws} beraberlik, {losses} yenilgi");
        Console.WriteLine($"Skor: {points:0.#}/{games}  ({percent:0.#}%)");
        Console.WriteLine($"Rakibe göre elo farkı: {EloDifference(percent):+0;-0;0}");
    }

    private static int PlayAgainstUci(ExternalEngine opponent, bool weAreWhite,
                                      int moveTimeMs, int maxPlies, int seed)
    {
        var board = new Board();
        var ours = new Search(32) { Random = new Random(seed) };
        var playedMoves = new List<string>();
        opponent.NewGame();

        for (int ply = 0; ply < maxPlies; ply++)
        {
            var legal = MoveGenerator.GenerateLegalMoves(board);
            if (legal.Count == 0)
                return board.IsInCheck(board.SideToMove) ? (board.SideToMove == Piece.White ? -1 : 1) : 0;
            if (board.HalfmoveClock >= 100 || board.IsRepetition()) return 0;

            bool ourTurn = (board.SideToMove == Piece.White) == weAreWhite;

            Move move;
            if (ourTurn)
            {
                move = ours.FindBestMove(board, maxDepth: 30, timeLimitMs: moveTimeMs, verbose: false);
            }
            else
            {
                string? text = opponent.Think(playedMoves, moveTimeMs);
                if (text == null) return 0;
                move = Uci.ParseMove(board, text);

                // Rakip legal olmayan bir şey gönderirse oyunu ona kaybettir.
                if (move.IsNull)
                {
                    Console.Write($"[{text}?]");
                    return weAreWhite ? 1 : -1;
                }
            }

            if (move.IsNull) return 0;
            playedMoves.Add(move.ToString());
            board.MakeMove(move);
        }

        return 0;
    }

    /// <summary>Skor yüzdesinden elo farkı (standart logistic formül).</summary>
    private static double EloDifference(double percent)
    {
        double p = Math.Clamp(percent / 100.0, 0.01, 0.99);
        return -400 * Math.Log10(1 / p - 1);
    }
}
