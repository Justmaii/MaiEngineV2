namespace ChessEngine;

/// <summary>
/// UCI (Universal Chess Interface) — motorların arayüzlerle konuştuğu
/// standart metin protokolü. Arena, CuteChess, BanksiaGUI gibi programlar
/// bunu konuşur; arkadaşının motoruyla maç yapmanın yolu da budur.
///
/// Protokol basit: stdin'den satır okur, stdout'a satır yazarsın.
/// Kritik kural — her cevaptan sonra çıktıyı FLUSH et, yoksa arayüz
/// motorun donduğunu sanır.
/// </summary>
public class Uci
{
    public const string EngineName = "MaiEngine v2 (NNUE) 0.1";
    public const string EngineAuthor = "Mai";

    private int _hashMb = 64;
    private bool _ownBook = true;
    private int _threads = 1;
    private string _bookFile = "Book/book.bin";

    private Board _board = new();
    private Search _search;

    public Uci()
    {
        _search = new Search(_hashMb) { UseOpeningBook = _ownBook };
    }

    public void Run()
    {
        // Otomatik flush: her WriteLine'dan sonra elle flush etmeye gerek kalmasın.
        var output = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        Console.SetOut(output);

        PolyglotBook.OpenDefault();

        string? line;
        while ((line = Console.ReadLine()) != null)
        {
            string[] tokens = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) continue;

            switch (tokens[0])
            {
                case "uci":
                    Console.WriteLine($"id name {EngineName}");
                    Console.WriteLine($"id author {EngineAuthor}");
                    // Arayüzün gördüğü ayarlar. Bunları ilan etmezsek turnuva
                    // programı "bu motorun Hash'i yok" diye varsayar ve rakibe
                    // verdiği belleği bize vermez — koşullar eşitsiz kalır.
                    Console.WriteLine($"option name Hash type spin default {_hashMb} min 1 max 4096");
                    Console.WriteLine($"option name OwnBook type check default {(_ownBook ? "true" : "false")}");
                    Console.WriteLine($"option name Threads type spin default {_threads} min 1 max 64");
                    Console.WriteLine("option name Clear Hash type button");
                    Console.WriteLine($"option name BookFile type string default {_bookFile}");
                    Console.WriteLine("uciok");
                    break;

                case "setoption":
                    HandleSetOption(tokens);
                    break;

                case "isready":
                    Console.WriteLine("readyok");
                    break;

                case "ucinewgame":
                    _board = new Board();
                    _search = new Search(_hashMb) { UseOpeningBook = _ownBook };
                    break;

                case "position":
                    HandlePosition(tokens);
                    break;

                case "go":
                    HandleGo(tokens);
                    break;

                case "d": // standart değil ama hata ayıklarken çok işe yarar
                    Console.WriteLine(_board);
                    break;

                case "quit":
                    return;
            }
        }
    }

    /// <summary>
    /// "setoption name &lt;ad&gt; value &lt;deger&gt;" — ad birden fazla kelime
    /// olabilir ("Clear Hash"), o yüzden name ile value arasındaki her şeyi
    /// ad sayıyoruz.
    /// </summary>
    private void HandleSetOption(string[] tokens)
    {
        int nameStart = Array.IndexOf(tokens, "name");
        if (nameStart < 0) return;

        int valueIndex = Array.IndexOf(tokens, "value");
        int nameEnd = valueIndex < 0 ? tokens.Length : valueIndex;
        if (nameStart + 1 >= nameEnd) return;

        string name = string.Join(' ', tokens[(nameStart + 1)..nameEnd]);
        string value = valueIndex >= 0 && valueIndex + 1 < tokens.Length
            ? string.Join(' ', tokens[(valueIndex + 1)..])
            : "";

        switch (name.ToLowerInvariant())
        {
            case "hash":
                if (int.TryParse(value, out int mb))
                {
                    _hashMb = Math.Clamp(mb, 1, 4096);
                    _search.Table.Resize(_hashMb);
                }
                break;

            case "ownbook":
                _ownBook = value.Trim().ToLowerInvariant() is "true" or "1";
                _search.UseOpeningBook = _ownBook;
                break;

            case "clear hash":
                _search.Table.Clear();
                break;

            case "bookfile":
                _bookFile = value.Trim();
                bool opened = PolyglotBook.OpenDefault(_bookFile);
                Console.WriteLine(opened
                    ? $"info string kitap acildi: {PolyglotBook.Shared!.EntryCount} kayit"
                    : $"info string kitap acilamadi: {_bookFile}");
                break;

            case "threads":
                // Lazy SMP: yardımcı iş parçacıkları aynı pozisyonu arar ve
                // paylaşılan transposition table üzerinden birbirine yardım eder.
                if (int.TryParse(value, out int threads))
                {
                    _threads = Math.Clamp(threads, 1, 64);
                    _search.Threads = _threads;
                }
                break;
        }
    }

    private void HandlePosition(string[] tokens)
    {
        int index = 1;

        if (tokens[index] == "startpos")
        {
            _board = new Board();
            index++;
        }
        else if (tokens[index] == "fen")
        {
            index++;
            // FEN 6 parçadan oluşur ama arayüzler bazen son ikisini göndermez.
            var fenParts = new List<string>();
            while (index < tokens.Length && tokens[index] != "moves")
                fenParts.Add(tokens[index++]);
            _board = new Board(string.Join(' ', fenParts));
        }

        if (index < tokens.Length && tokens[index] == "moves")
        {
            index++;
            for (; index < tokens.Length; index++)
            {
                var move = ParseMove(_board, tokens[index]);
                if (move.IsNull) break; // tanınmayan hamle: sessizce dur
                _board.MakeMove(move);
            }
        }
    }

    private void HandleGo(string[] tokens)
    {
        int moveTime = 0, depth = 30;
        int wtime = 0, btime = 0, winc = 0, binc = 0, movesToGo = 0;

        for (int i = 1; i < tokens.Length - 1; i++)
        {
            if (!int.TryParse(tokens[i + 1], out int value)) continue;
            switch (tokens[i])
            {
                case "movetime": moveTime = value; break;
                case "depth": depth = value; break;
                case "wtime": wtime = value; break;
                case "btime": btime = value; break;
                case "winc": winc = value; break;
                case "binc": binc = value; break;
                case "movestogo": movesToGo = value; break;
            }
        }

        int softLimit = 0;
        if (moveTime == 0 && Environment.GetEnvironmentVariable("MAIENGINE_OLDTIME") == "1")
        {
            // Karsilastirma icin eski kural: kalanin 1/25'i + eklemenin 3/4'u,
            // yumusak sinir yok.
            int left = _board.SideToMove == Piece.White ? wtime : btime;
            int inc = _board.SideToMove == Piece.White ? winc : binc;
            int div = movesToGo > 0 ? Math.Min(movesToGo, 30) : 25;
            moveTime = left <= 0 ? 1000 : Math.Max(50, Math.Min(left / div + inc * 3 / 4, left / 2));
        }
        else if (moveTime == 0)
        {
            (softLimit, moveTime) = TimeManager.Allocate(
                _board.SideToMove == Piece.White ? wtime : btime,
                _board.SideToMove == Piece.White ? winc : binc,
                movesToGo);
        }
        _search.SoftLimitMs = softLimit;

        _search.OnIteration = (d, score, nodes, ms, best) =>
        {
            string scoreText = Math.Abs(score) > Search.MateScore - 1000
                ? $"mate {(score > 0 ? 1 : -1) * ((Search.MateScore - Math.Abs(score) + 1) / 2)}"
                : $"cp {score}";

            long nps = ms > 0 ? nodes * 1000 / ms : 0;
            Console.WriteLine($"info depth {d} score {scoreText} nodes {nodes} " +
                              $"nps {nps} time {ms} pv {best}");
        };

        var move = _search.FindBestMove(_board, depth, moveTime, verbose: false);
        _search.OnIteration = null;

        Console.WriteLine($"bestmove {(move.IsNull ? "0000" : move.ToString())}");
    }

    /// <summary>"e2e4" / "e7e8q" metnini, pozisyondaki legal hamlelerden biriyle eşleştirir.
    /// Metinden doğrudan Move üretmek yerine eşleştirmek, bayrakların (rok,
    /// geçerken alma, çift adım) doğru gelmesini garantiler.</summary>
    public static Move ParseMove(Board board, string text)
    {
        foreach (var move in MoveGenerator.GenerateLegalMoves(board))
            if (move.ToString() == text) return move;
        return default;
    }
}
