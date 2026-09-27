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
    /// <param name="firstGame">Tohum numarasi kaydan baslasin. Uzun maclari
    /// parca parca calistirirken ayni oyunlari tekrar oynamamak icin.</param>
    public static void RunAgainstUci(string enginePath, int games, int moveTimeMs,
                                     string options = "", int maxPlies = 200, int firstGame = 0)
    {
        using var opponent = new ExternalEngine(enginePath);

        foreach (string option in options.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] pair = option.Split('=', 2);
            if (pair.Length == 2) opponent.SetOption(pair[0].Trim(), pair[1].Trim());
        }
        opponent.IsReady();

        // Acilis seti: her pozisyon iki kez oynanir (bir kez her renkle), boylece
        // bir acilisin bir tarafa yaramasi sonucu bozmaz.
        string? openingPath = Environment.GetEnvironmentVariable("MAIENGINE_OPENINGS");
        string[] openings = openingPath != null && File.Exists(openingPath)
            ? File.ReadAllLines(openingPath).Where(l => l.Trim().Length > 0).ToArray()
            : Array.Empty<string>();

        int wins = 0, losses = 0, draws = 0;
        Console.WriteLine($"MaiEngine  vs  {opponent.Name}" +
                          (options.Length > 0 ? $"  [{options}]" : ""));
        Console.WriteLine($"{games} oyun, hamle başına {moveTimeMs} ms\n");

        for (int index = 0; index < games; index++)
        {
            int game = firstGame + index;
            bool weAreWhite = game % 2 == 0;
            string? startFen = openings.Length > 0 ? openings[(game / 2) % openings.Length].Trim() : null;
            int result = PlayAgainstUci(opponent, weAreWhite, moveTimeMs, maxPlies, seed: game, startFen);

            if (result == 0) draws++;
            else if ((result > 0) == weAreWhite) wins++;
            else losses++;

            Console.Write(result == 0 ? "=" : ((result > 0) == weAreWhite ? "1" : "0"));
            if ((index + 1) % 10 == 0) Console.Write(" ");
            Console.Out.Flush();
        }

        double points = wins + draws / 2.0;
        double percent = points / games * 100;
        Console.WriteLine($"\n\nMaiEngine: {wins} galibiyet, {draws} beraberlik, {losses} yenilgi");
        Console.WriteLine($"Skor: {points:0.#}/{games}  ({percent:0.#}%)");
        Console.WriteLine($"Rakibe göre elo farkı: {EloDifference(percent):+0;-0;0}");
    }

    private static int PlayAgainstUci(ExternalEngine opponent, bool weAreWhite,
                                      int moveTimeMs, int maxPlies, int seed, string? startFen = null)
    {
        var board = startFen == null ? new Board() : new Board(startFen);

        // Kitap KAPALI: rakip de kitapsiz oynuyor (OwnBook=false). Bu satır eskiden
        // eksikti ve test edilen taraf ilk hamlelerde gömülü kitaptan oynuyordu.
        var ours = new Search(32) { Random = new Random(seed), UseOpeningBook = false };
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
                string? text = opponent.Think(playedMoves, moveTimeMs, startFen);
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

    /// <summary>
    /// SAATLİ maç: her tarafın toplam süresi ve hamle eklemesi var, payını
    /// kendi ayırır. Sabit hamle süresiyle ölçülemeyen tek şey budur —
    /// zaman yönetimi ancak saat varken bir anlam ifade eder.
    /// Süresi biten taraf kaybeder.
    /// </summary>
    public static void RunAgainstUciClock(string enginePath, int games, int baseMs, int incMs,
                                          int firstGame = 0, int maxPlies = 300)
    {
        using var opponent = new ExternalEngine(enginePath);
        opponent.SetOption("OwnBook", "false");
        opponent.IsReady();

        int wins = 0, losses = 0, draws = 0, flags = 0;
        Console.WriteLine($"MaiEngine  vs  {opponent.Name}");
        Console.WriteLine($"{games} oyun, {baseMs / 1000.0:0.#} sn + {incMs / 1000.0:0.##} sn ekleme\n");

        for (int index = 0; index < games; index++)
        {
            int game = firstGame + index;
            bool weAreWhite = game % 2 == 0;
            int result = PlayClockGame(opponent, weAreWhite, baseMs, incMs, maxPlies, seed: game, ref flags);

            if (result == 0) draws++;
            else if ((result > 0) == weAreWhite) wins++;
            else losses++;

            Console.Write(result == 0 ? "=" : ((result > 0) == weAreWhite ? "1" : "0"));
            if ((index + 1) % 10 == 0) Console.Write(" ");
            Console.Out.Flush();
        }

        double points = wins + draws / 2.0;
        double percent = points / games * 100;
        Console.WriteLine($"\n\nMaiEngine: {wins} galibiyet, {draws} beraberlik, {losses} yenilgi");
        Console.WriteLine($"Skor: {points:0.#}/{games}  ({percent:0.#}%)");
        Console.WriteLine($"Rakibe göre elo farkı: {EloDifference(percent):+0;-0;0}");
        if (flags > 0) Console.WriteLine($"Süre aşımı: {flags}");
    }

    private static int PlayClockGame(ExternalEngine opponent, bool weAreWhite, int baseMs, int incMs,
                                     int maxPlies, int seed, ref int flags)
    {
        var board = new Board();
        var ours = new Search(32) { Random = new Random(seed), UseOpeningBook = false };
        var playedMoves = new List<string>();
        opponent.NewGame();

        int whiteClock = baseMs, blackClock = baseMs;
        var stopwatch = new System.Diagnostics.Stopwatch();

        for (int ply = 0; ply < maxPlies; ply++)
        {
            var legal = MoveGenerator.GenerateLegalMoves(board);
            if (legal.Count == 0)
                return board.IsInCheck(board.SideToMove) ? (board.SideToMove == Piece.White ? -1 : 1) : 0;
            if (board.HalfmoveClock >= 100 || board.IsRepetition()) return 0;

            bool whiteToMove = board.SideToMove == Piece.White;
            bool ourTurn = whiteToMove == weAreWhite;
            int clock = whiteToMove ? whiteClock : blackClock;
            if (clock <= 0) { flags++; return whiteToMove ? -1 : 1; }

            Move move;
            stopwatch.Restart();

            if (ourTurn)
            {
                (int soft, int hard) = TimeManager.Allocate(clock, incMs, 0);
                ours.SoftLimitMs = soft;
                move = ours.FindBestMove(board, maxDepth: 40, timeLimitMs: hard, verbose: false);
            }
            else
            {
                string? text = opponent.ThinkWithClock(playedMoves, whiteClock, blackClock, incMs, incMs);
                if (text == null) return 0;
                move = Uci.ParseMove(board, text);
                if (move.IsNull) { Console.Write($"[{text}?]"); return weAreWhite ? 1 : -1; }
            }

            int spent = (int)stopwatch.ElapsedMilliseconds;
            if (whiteToMove) whiteClock = whiteClock - spent + incMs;
            else blackClock = blackClock - spent + incMs;

            // Süresi biten taraf kaybeder — turnuvada da böyle.
            if ((whiteToMove ? whiteClock : blackClock) < 0) { flags++; return whiteToMove ? -1 : 1; }

            if (move.IsNull) return 0;
            playedMoves.Add(move.ToString());
            board.MakeMove(move);
        }

        return 0;
    }

}
