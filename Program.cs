using ChessEngine;

string mode = args.Length > 0 ? args[0].ToLower() : "test";

// Polyglot kitabi varsa her modda kullanilsin (web arayuzu, mac, oyun).
// uci modu kendi icinde tekrar aciyor, sorun degil.
// NNUE agini bul ve yukle. Yoksa motor elle yazilmis degerlendirmeye duser.
// MAIENGINE_NNUE en onceliklidir: olcum yaparken hangi agin yuklendigi
// tesadufe birakilamaz. Env degiskeni verildi ama yuklenemediyse
// sessizce elle yazilmis degerlendirmeye dusmek yerine hata verip cikiyoruz.
string envNet = Environment.GetEnvironmentVariable("MAIENGINE_NNUE") ?? "";
if (envNet.Length > 0)
{
    if (!NnueNetwork.TryLoadShared(envNet))
    {
        Console.Error.WriteLine($"HATA: NNUE agi yuklenemedi: {envNet}");
        return 1;
    }
    if (mode != "uci") Console.WriteLine($"NNUE agi yuklendi: {envNet}");
}
else
{
    foreach (string candidate in new[] { "nn.nnue", "docs/nn-82215d0fd0df.nnue" })
    {
        if (NnueNetwork.TryLoadShared(candidate))
        {
            if (mode != "uci") Console.WriteLine($"NNUE agi yuklendi: {candidate}");
            break;
        }
    }
}

if (mode != "uci" && mode != "polyglot" && PolyglotBook.OpenDefault())
    Console.WriteLine($"Polyglot kitabi: {PolyglotBook.Shared!.EntryCount:N0} kayit\n");

switch (mode)
{
    case "uci":
        new Uci().Run();
        break;

    case "web":
        WebServer.Run(args);
        break;

    // dotnet run -c Release matchuci /opt/homebrew/bin/stockfish 20 200 "UCI_LimitStrength=true,UCI_Elo=1800"
    case "matchuci":
        if (args.Length < 2) { Console.WriteLine("Kullanım: matchuci <motor yolu> [oyun] [ms] [\"Ayar=Deger,...\"]"); break; }
        Match.RunAgainstUci(
            args[1],
            games: args.Length > 2 && int.TryParse(args[2], out int mg) ? mg : 20,
            moveTimeMs: args.Length > 3 && int.TryParse(args[3], out int mt) ? mt : 200,
            options: args.Length > 4 ? args[4] : "",
            firstGame: args.Length > 5 && int.TryParse(args[5], out int fg) ? fg : 0);
        break;

    // Polyglot anahtar dogrulamasi + kitap denemesi
    case "polyglot":
        PolyglotCheck(args.Length > 1 ? args[1] : "Book/book.bin");
        break;

    // Yeni degerlendirme terimlerinin isaretleri dogru mu?
    // Onbellekli degerlendirme, onbelleksizle ayni sonucu veriyor mu?
    case "evalverify":
        EvalVerify();
        break;

    // SEE dogru hesapliyor mu?
    // NNUE dogrulamasi icin cesitli pozisyonlar uret
    // NNUE degerlendirmesi referansla birebir tutuyor mu?
    // Artimli accumulator, sifirdan hesapla ayni mi?
    case "accverify":
        AccVerify(args.Length > 1 ? args[1] : "docs/nn-82215d0fd0df.nnue",
                  args.Length > 2 && int.TryParse(args[2], out int ad) ? ad : 4);
        break;

    case "nnuecheck":
        NnueCheck(args.Length > 1 ? args[1] : "docs/nn-82215d0fd0df.nnue",
                  args.Length > 2 ? args[2] : "docs/nnue-ref.txt");
        break;

    case "dumpfens":
        DumpFens(args.Length > 1 && int.TryParse(args[1], out int dn) ? dn : 300);
        break;

    case "smp":
        SmpCheck(args.Length > 1 && int.TryParse(args[1], out int smpT) ? smpT : 2,
                 args.Length > 2 && int.TryParse(args[2], out int smpMs) ? smpMs : 2000);
        break;

    case "seecompare":
        SeeCompare(args.Length > 1 && int.TryParse(args[1], out int scd) ? scd : 3);
        break;

    case "bbcheck":
        BitboardCheck();
        break;

    case "attackcheck":
        AttackCheck(args.Length > 1 && int.TryParse(args[1], out int atkd) ? atkd : 3);
        break;

    case "seecheck":
        SeeCheck();
        break;

    case "evalcheck":
        EvalCheck();
        break;

    case "perft":
        Perft.RunAllTests();
        break;

    case "play":
        PlayAgainstEngine();
        break;

    case "bench":
        Bench();
        break;

    // Yeni degerlendirme vs eski degerlendirme (arama ikisinde de ayni)
    case "matcheval":
        Match.Run(new Match.Config("Yeni degerlendirme", true, AdvancedEval: true),
                  new Match.Config("Materyal + kare tablolari", true, AdvancedEval: false),
                  games: args.Length > 1 && int.TryParse(args[1], out int eg) ? eg : 30,
                  moveTimeMs: args.Length > 2 && int.TryParse(args[2], out int et) ? et : 150);
        break;

    // Yeni arama teknikleri vs onceki arama
    case "matchsearch":
        Match.Run(new Match.Config("Yeni arama (v2)", true, SearchV2: true),
                  new Match.Config("Onceki arama", true, SearchV2: false),
                  games: args.Length > 1 && int.TryParse(args[1], out int sg) ? sg : 100,
                  moveTimeMs: args.Length > 2 && int.TryParse(args[2], out int st) ? st : 120);
        break;

    // SEE acik vs kapali
    case "matchsee":
        Match.Run(new Match.Config("SEE acik", true, See: true),
                  new Match.Config("SEE kapali", true, See: false),
                  games: args.Length > 1 && int.TryParse(args[1], out int qg) ? qg : 150,
                  moveTimeMs: args.Length > 2 && int.TryParse(args[2], out int qt) ? qt : 120);
        break;

    case "match":
        Match.Run(new Match.Config("Gelişmiş arama", true),
                  new Match.Config("Sade alpha-beta", false),
                  games: args.Length > 1 && int.TryParse(args[1], out int g) ? g : 20,
                  moveTimeMs: args.Length > 2 && int.TryParse(args[2], out int t) ? t : 150);
        break;

    default:
        RunSearchTests();
        break;
}

// ----------------------------------------------------------------------
//  Arama testleri: motor bilinen taktikleri bulabiliyor mu?
// ----------------------------------------------------------------------
/// <summary>
/// Polyglot anahtar hesabini standart test degerleriyle karsilastirir.
/// Bu degerler formatin kendi belgesinden gelir; tutmazsa kitap sessizce
/// yanlis hamle verir, o yuzden once bunlar gecmeli.
/// </summary>
void PolyglotCheck(string bookPath)
{
    var vectors = new (string Moves, ulong Key)[]
    {
        ("",                                          0x463b96181691fc9cUL),
        ("e2e4",                                      0x823c9b50fd114196UL),
        ("e2e4 d7d5",                                 0x0756b94461c50fb0UL),
        ("e2e4 d7d5 e4e5",                            0x662fafb965db29d4UL),
        ("e2e4 d7d5 e4e5 f7f5",                       0x22a48b5a8e47ff78UL),
        ("e2e4 d7d5 e4e5 f7f5 e1e2",                  0x652a607ca3f242c1UL),
        ("e2e4 d7d5 e4e5 f7f5 e1e2 e8f7",             0x00fdd303c946bdd9UL),
        ("a2a4 b7b5 h2h4 b5b4 c2c4",                  0x3c8123ea7b067637UL),
        ("a2a4 b7b5 h2h4 b5b4 c2c4 b4c3 a1a3",        0x5c3f9b829b279560UL),
    };

    Console.WriteLine("=== Polyglot anahtar testleri ===");
    bool allOk = true;

    foreach (var (moves, expected) in vectors)
    {
        var board = new Board();
        foreach (string token in moves.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var move = Uci.ParseMove(board, token);
            if (move.IsNull) { Console.WriteLine($"  gecersiz hamle: {token}"); break; }
            board.MakeMove(move);
        }

        ulong actual = PolyglotBook.ComputeKey(board);
        bool ok = actual == expected;
        if (!ok) allOk = false;

        string label = moves.Length == 0 ? "baslangic" : moves;
        Console.WriteLine($"  {(ok ? "OK  " : "HATA")} {label,-42} {actual:x16} / {expected:x16}");
    }

    Console.WriteLine(allOk ? "\nTum anahtarlar dogru." : "\nAnahtar hesabi HATALI.");
    if (!allOk) return;

    Console.WriteLine($"\n=== Kitap: {bookPath} ===");
    using var book = new PolyglotBook(bookPath);
    if (!book.IsOpen) { Console.WriteLine("  dosya bulunamadi"); return; }

    Console.WriteLine($"  {book.EntryCount:N0} kayit");

    var testBoard = new Board();
    var rng = new Random(1);
    for (int ply = 0; ply < 12; ply++)
    {
        var move = book.Probe(testBoard, rng);
        if (move.IsNull) { Console.WriteLine($"  {ply + 1}. yari hamlede kitap bitti"); break; }
        Console.Write($"{move} ");
        testBoard.MakeMove(move);
    }
    Console.WriteLine();
}

/// <summary>Terimleri tek tek izole eden pozisyonlar. Her satirda sadece
/// test edilen ozellik farkli; puan beklenen yone gitmeli.</summary>
/// <summary>
/// Piyon onbellegi dogru mu? Agacta her dugumde degerlendirmeyi iki kez
/// hesaplar: bir kez onbellek bos, bir kez dolu. Ikisi ayni cikmali.
/// Yanlis onbellek sessizce yanlis oynatir, o yuzden bu test sart.
/// </summary>
/// <summary>SEE testleri. Ilk ikisi satranc programlama literaturunun
/// standart ornekleri; gerisi elle dogrulanabilir basit vakalar.</summary>
/// <summary>Rastgele oynanan oyunlardan cesitli pozisyonlar toplar.
/// NNUE implementasyonunu dogrulamak icin referans motora verilecek.</summary>
/// <summary>Kendi NNUE kodumuzu referans motorun degerleriyle karsilastirir.
/// Nicemlenmis aritmetikte sessiz hata cok kolay olur; tek guvenceli yol bu.</summary>
/// <summary>
/// Agacta her dugumde artimli accumulator ile sifirdan hesabi karsilastirir.
/// Zobrist ve piyon anahtarinda yaptigimizin aynisi: artimli guncellemede
/// hata sessizdir, tek guvence bu testtir.
/// </summary>
void AccVerify(string netPath, int depth)
{
    if (!NnueNetwork.TryLoadShared(netPath)) { Console.WriteLine($"Ag yuklenemedi: {netPath}"); return; }
    var net = NnueNetwork.Shared!;

    long nodes = 0;
    bool ok = true;

    bool Walk(Board board, int remaining)
    {
        // Artimli deger
        int incremental = net.EvaluateAccumulated(board.Nnue!.Current, board.SideToMove);

        // Sifirdan deger (ayni pozisyon, temiz hesap)
        int fromScratch = net.Evaluate(board);
        nodes++;

        if (incremental != fromScratch)
        {
            Console.WriteLine($"  UYUSMAZLIK artimli={incremental} sifirdan={fromScratch}");
            Console.WriteLine($"  {board.ToFen()}");
            return false;
        }
        if (remaining == 0) return true;

        foreach (var move in MoveGenerator.GenerateLegalMoves(board))
        {
            var undo = board.MakeMove(move);
            bool good = Walk(board, remaining - 1);
            board.UnmakeMove(undo);
            if (!good) { Console.WriteLine($"    hamle: {move}"); return false; }
        }
        return true;
    }

    Console.WriteLine($"=== Artimli accumulator dogrulamasi (derinlik {depth}) ===");
    foreach (var test in Perft.StandardTests)
    {
        bool good = Walk(new Board(test.Fen), depth);
        Console.WriteLine($"  {(good ? "OK  " : "HATA")} {test.Name}");
        if (!good) ok = false;
    }
    Console.WriteLine($"\n{nodes:N0} dugum kontrol edildi. {(ok ? "Hepsi tutuyor." : "HATA VAR.")}");
}

void NnueCheck(string netPath, string refPath)
{
    if (!File.Exists(netPath)) { Console.WriteLine($"Ag dosyasi yok: {netPath}"); return; }
    if (!File.Exists(refPath)) { Console.WriteLine($"Referans yok: {refPath}"); return; }

    var net = NnueNetwork.Load(netPath);
    Console.WriteLine($"Ag yuklendi: {net.Description.Trim()}");

    int total = 0, exact = 0, worst = 0;
    string worstFen = "";

    foreach (string line in File.ReadLines(refPath))
    {
        int split = line.LastIndexOf(';');
        if (split < 0) continue;

        string fen = line[..split];
        if (!int.TryParse(line[(split + 1)..], out int expected)) continue;

        int actual = net.Evaluate(new Board(fen));
        total++;
        if (actual == expected) exact++;

        int diff = Math.Abs(actual - expected);
        if (diff > worst) { worst = diff; worstFen = fen; }
    }

    // Hiz: naif surum (her degerlendirmede bastan hesap) arama icin yeterli mi?
    var boards = File.ReadLines(refPath)
        .Select(l => new Board(l[..l.LastIndexOf(';')])).ToList();

    var timer = System.Diagnostics.Stopwatch.StartNew();
    long sink = 0;
    int rounds = 20;
    for (int r = 0; r < rounds; r++)
        foreach (var b in boards) sink += net.Evaluate(b);
    timer.Stop();

    long evals = (long)rounds * boards.Count;
    Console.WriteLine($"Hiz: {evals} degerlendirme / {timer.ElapsedMilliseconds} ms " +
                      $"= {evals * 1000 / Math.Max(1, timer.ElapsedMilliseconds):N0}/sn  (sink {sink})");

    Console.WriteLine($"\n{exact}/{total} pozisyon BIREBIR ayni.");
    if (exact != total)
    {
        Console.WriteLine($"En buyuk fark: {worst}");
        Console.WriteLine($"  {worstFen}");
    }
}

void DumpFens(int count)
{
    var rng = new Random(12345);
    var seen = new HashSet<string>();
    var lines = new List<string>();

    while (lines.Count < count)
    {
        var board = new Board();
        int plies = 0;

        while (plies < 120 && lines.Count < count)
        {
            var moves = MoveGenerator.GenerateLegalMoves(board);
            if (moves.Count == 0) break;

            // Cogunlukla rastgele oyna ki pozisyonlar cesitlensin.
            board.MakeMove(moves[rng.Next(moves.Count)]);
            plies++;

            // Acilisi atla, sonra her birkac hamlede bir kaydet.
            // Sahtaki pozisyonlari atla: referans motor onlar icin
            // degerlendirme basmiyor, hizalama bozulur.
            if (plies >= 6 && plies % 4 == 0 && !board.IsInCheck(board.SideToMove))
            {
                string fen = board.ToFen();
                if (seen.Add(fen)) lines.Add(fen);
            }
        }
    }

    File.WriteAllLines("/mnt/user-data/outputs/nnue-fens.txt", lines);
    Console.WriteLine($"{lines.Count} pozisyon yazildi: nnue-fens.txt");
}

void SeeCheck()
{
    var cases = new (string Name, string Fen, string Move, string Expect)[]
    {
        ("Rxe5 - bedava piyon",
         "1k1r4/1pp4p/p7/4p3/8/P5P1/1PP4P/2K1R3 w - - 0 1", "e1e5", "pozitif"),

        ("Nxe5 - korunan piyon, zararli",
         "1k1r3q/1ppn3p/p4b2/4p3/8/P2N2P1/1PP1R1BP/2K1Q3 w - - 0 1", "d3e5", "negatif"),

        ("Vezirle korunmasiz kale almak",
         "4k3/8/8/3r4/8/8/8/3QK3 w - - 0 1", "d1d5", "pozitif"),

        ("Vezirle korunan piyonu almak",
         "4k3/3p4/2p5/8/8/8/8/3QK3 w - - 0 1", "d1d7", "negatif"),

        ("Piyonla korunan piyonu piyonla almak",
         "4k3/8/8/8/2p5/3p4/2P5/4K3 w - - 0 1", "c2d3", "sifir"),

        ("Piyonla korunan ati almak",
         "4k3/8/8/8/2p5/3n4/2P5/4K3 w - - 0 1", "c2d3", "pozitif"),
    };

    Console.WriteLine("=== SEE testleri ===");
    bool allOk = true;

    foreach (var (name, fen, moveText, expect) in cases)
    {
        var board = new Board(fen);
        var move = Uci.ParseMove(board, moveText);
        if (move.IsNull)
        {
            Console.WriteLine($"  HATA {name,-34} gecersiz hamle: {moveText}");
            allOk = false;
            continue;
        }

        int see = See.Evaluate(board, move);
        bool ok = expect switch
        {
            "pozitif" => see > 0,
            "negatif" => see < 0,
            _ => see == 0,
        };
        if (!ok) allOk = false;

        Console.WriteLine($"  {(ok ? "OK  " : "HATA")} {name,-34} SEE {see,+5}  (beklenen {expect})");
    }

    Console.WriteLine(allOk ? "\nSEE dogru." : "\nSEE HATALI.");
}

void EvalVerify()
{
    Evaluation.UseAdvancedEval = true;
    Evaluation.NoiseCentipawns = 0;

    int checkedNodes = 0;
    bool ok = true;

    bool Walk(Board board, int depth)
    {
        Evaluation.ClearPawnCache();
        int fresh = Evaluation.Evaluate(board);
        int cached = Evaluation.Evaluate(board);   // ayni pozisyon, artik onbellekten
        checkedNodes++;

        if (fresh != cached)
        {
            Console.WriteLine($"  UYUSMAZLIK {fresh} != {cached}  {board.ToFen()}");
            return false;
        }
        if (depth == 0) return true;

        foreach (var move in MoveGenerator.GenerateLegalMoves(board))
        {
            var undo = board.MakeMove(move);
            bool good = Walk(board, depth - 1);
            board.UnmakeMove(undo);
            if (!good) return false;
        }
        return true;
    }

    Console.WriteLine("=== Piyon onbellegi dogrulamasi ===");
    foreach (var test in Perft.StandardTests)
    {
        bool good = Walk(new Board(test.Fen), 3);
        Console.WriteLine($"  {(good ? "OK  " : "HATA")} {test.Name}");
        if (!good) ok = false;
    }
    Console.WriteLine($"\n{checkedNodes:N0} pozisyon kontrol edildi. {(ok ? "Hepsi tutuyor." : "HATA VAR.")}");
}

void EvalCheck()
{
    var cases = new (string Name, string Fen, string Expect)[]
    {
        ("ikiz piyon (beyazda)",   "4k3/pppppppp/8/8/8/2P5/PPP1PPPP/4K3 w - - 0 1", "negatif"),
        ("izole piyon (beyazda)",  "4k3/pppppppp/8/8/8/8/P3P2P/4K3 w - - 0 1",      "negatif"),
        ("gecer piyon (beyazda)",  "4k3/8/8/4P3/8/8/8/4K3 w - - 0 1",               "pozitif"),
        ("fil cifti (beyazda)",    "4k3/8/8/8/8/8/8/2B1KB2 w - - 0 1",              "pozitif"),
        ("sah kalkani yok (beyaz)","4k3/pppppppp/8/8/8/5PPP/8/5RK1 w - - 0 1",      "negatif"),
    };

    Console.WriteLine("=== Degerlendirme terim testleri ===");
    Console.WriteLine("(fark = yeni terimlerin katkisi, beyazin gozunden)\n");

    foreach (var (name, fen, expect) in cases)
    {
        var board = new Board(fen);

        Evaluation.NoiseCentipawns = 0;

        Evaluation.UseAdvancedEval = false;
        int plain = Evaluation.Evaluate(board);

        Evaluation.UseAdvancedEval = true;
        int rich = Evaluation.Evaluate(board);

        // Puan sirasi gelen tarafin gozunden; beyaza cevir.
        if (board.SideToMove != Piece.White) { plain = -plain; rich = -rich; }
        int diff = rich - plain;

        bool ok = expect == "pozitif" ? diff > 0 : diff < 0;
        Console.WriteLine($"  {(ok ? "OK  " : "HATA")} {name,-26} fark {diff,+5}  (beklenen {expect})");
    }
}

void RunSearchTests()
{
    Console.WriteLine($"Açılış kitabı: {OpeningBook.PositionCount} pozisyon");
    if (OpeningBook.Errors.Count > 0)
    {
        Console.WriteLine($"  KİTAPTA {OpeningBook.Errors.Count} BOZUK SATIR:");
        foreach (string error in OpeningBook.Errors) Console.WriteLine("  " + error);
    }
    else Console.WriteLine("  tüm satırlar legal  OK");

    var tests = new (string Name, string Fen, string Expected)[]
    {
        // Sırt sıra mat (back rank): vezir a8'e iner, mat.
        ("Mat 1 hamlede", "6k1/5ppp/8/8/8/8/8/R5K1 w - - 0 1", "a1a8"),
        // Serbest vezir alışı.
        ("Bedava vezir",  "4k3/8/8/3q4/4P3/8/8/4K3 w - - 0 1", "e4d5"),
        // Legal'in matı benzeri: at f7'ye, şah kaçamaz.
        ("Smothered mat", "6rk/6pp/8/6N1/8/8/8/6K1 w - - 0 1", "g5f7"),
    };

    foreach (var test in tests)
    {
        Console.WriteLine($"\n=== {test.Name} ===");
        var board = new Board(test.Fen);
        Console.WriteLine(board);

        var search = new Search();
        var best = search.FindBestMove(board, maxDepth: 6, timeLimitMs: 5000);

        bool ok = best.ToString() == test.Expected;
        Console.WriteLine($"  -> seçilen: {best}  beklenen: {test.Expected}  {(ok ? "OK" : "FARKLI")}");
    }

    Console.WriteLine("\n\n=== Motor kendine karşı (12 yarım hamle) ===");
    SelfPlay(12);
}

/// <summary>Sabit sürede hangi derinliğe inilebildiğini ölçer.
/// Motoru geliştirdikçe buradaki derinlik ve düğüm sayısını karşılaştır.</summary>
void Bench()
{
    var positions = new (string Name, string Fen)[]
    {
        ("Başlangıç", Board.StartFen),
        ("Kiwipete",  "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1"),
        ("Oyun sonu", "8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1"),
    };

    // Ölçüm tekrarlanabilir olsun diye çeşitlilik gürültüsü kapalı.
    Evaluation.NoiseCentipawns = 0;

    // "bench plain" -> yeni degerlendirme terimleri olmadan olc.
    bool advancedEval = !(args.Length > 1 && args[1].ToLower() == "plain");
    Console.WriteLine(advancedEval ? "(yeni degerlendirme ACIK)" : "(yeni degerlendirme KAPALI)");

    foreach (var (name, fen) in positions)
    {
        Console.WriteLine($"\n=== {name} (3 sn) ===");
        var board = new Board(fen);
        var search = new Search { UseAdvancedEval = advancedEval, UseOpeningBook = false };
        Evaluation.ClearPawnCache();
        search.FindBestMove(board, maxDepth: 30, timeLimitMs: 3000);
        if (advancedEval && Evaluation.PawnCacheProbes > 0)
            Console.WriteLine($"  piyon onbellegi: %{100.0 * Evaluation.PawnCacheHits / Evaluation.PawnCacheProbes:0.0} isabet");
    }
}

void SelfPlay(int plies)
{
    var board = new Board();
    var search = new Search();

    for (int i = 0; i < plies; i++)
    {
        var move = search.FindBestMove(board, maxDepth: 5, timeLimitMs: 1000, verbose: false);
        if (move.IsNull) { Console.WriteLine("Oyun bitti."); break; }

        Console.WriteLine($"{i / 2 + 1}{(i % 2 == 0 ? "." : "...")} {move}   " +
                          $"(puan {Search.FormatScore(search.LastScore)}, " +
                          $"derinlik {search.DepthReached}, {search.NodesSearched} düğüm)");
        board.MakeMove(move);
    }

    Console.WriteLine();
    Console.WriteLine(board);
}

// ----------------------------------------------------------------------
//  Konsoldan motora karşı oynama
// ----------------------------------------------------------------------
void PlayAgainstEngine()
{
    var board = new Board();
    var search = new Search();

    Console.Write("Hangi renkle oynuyorsun? (b = beyaz, s = siyah): ");
    bool playerIsWhite = (Console.ReadLine() ?? "b").Trim().ToLower().StartsWith("b");
    int playerColor = playerIsWhite ? Piece.White : Piece.Black;

    while (true)
    {
        Console.WriteLine(board);

        var legal = MoveGenerator.GenerateLegalMoves(board);
        if (legal.Count == 0)
        {
            Console.WriteLine(board.IsInCheck(board.SideToMove)
                ? (board.SideToMove == playerColor ? "Mat oldun." : "Mat! Kazandın.")
                : "Pat — berabere.");
            break;
        }
        if (board.HalfmoveClock >= 100) { Console.WriteLine("50 hamle kuralı — berabere."); break; }

        if (board.SideToMove == playerColor)
        {
            Console.Write("Hamlen (örn e2e4, terfi için e7e8q; çıkmak için q): ");
            string input = (Console.ReadLine() ?? "").Trim().ToLower();
            if (input == "q") break;

            var move = legal.FirstOrDefault(m => m.ToString() == input);
            if (move.IsNull && input != "") { Console.WriteLine("Geçersiz hamle, tekrar dene."); continue; }
            board.MakeMove(move);
        }
        else
        {
            Console.WriteLine("Motor düşünüyor...");
            var move = search.FindBestMove(board, maxDepth: 8, timeLimitMs: 3000);
            Console.WriteLine($"Motor: {move}  (puan {Search.FormatScore(search.LastScore)})");
            board.MakeMove(move);
        }
    }
}

return 0;

// Kayan tas saldirilarinin hizli yolu, yavas referansla birebir ayni mi?
// Her kare icin rastgele doluluk ornekleri + sinir durumlari deneniyor.
static void BitboardCheck()
{
    Console.WriteLine("=== Bitboard kayan tas dogrulamasi ===");
    var rng = new Random(20260926);
    long checks = 0;
    int bad = 0;

    for (int sq = 0; sq < 64 && bad == 0; sq++)
    {
        // Sinir durumlari: bos tahta, tamamen dolu tahta, sadece kendi karesi.
        var occupancies = new List<ulong> { 0UL, ulong.MaxValue, 1UL << sq };
        for (int i = 0; i < 4000; i++)
        {
            // Seyrek ve yogun doluluklar ayri ayri denenmeli.
            ulong occ = (ulong)rng.NextInt64();
            if (i % 3 == 0) occ &= (ulong)rng.NextInt64();
            if (i % 3 == 1) occ |= (ulong)rng.NextInt64() & (ulong)rng.NextInt64();
            occupancies.Add(occ);
        }

        foreach (ulong occ in occupancies)
        {
            ulong fastRook = Bitboards.RookAttacks(sq, occ);
            ulong slowRook = Bitboards.SlowSliderAttacks(sq, occ, diagonal: false);
            ulong fastBishop = Bitboards.BishopAttacks(sq, occ);
            ulong slowBishop = Bitboards.SlowSliderAttacks(sq, occ, diagonal: true);
            checks += 2;

            if (fastRook != slowRook || fastBishop != slowBishop)
            {
                Console.WriteLine($"  HATA kare {Square.Name(sq)} doluluk {occ:X16}");
                Console.WriteLine($"    kale hizli {fastRook:X16} yavas {slowRook:X16}");
                Console.WriteLine($"    fil  hizli {fastBishop:X16} yavas {slowBishop:X16}");
                bad++;
                break;
            }
        }
    }

    Console.WriteLine(bad == 0
        ? $"\n{checks:N0} saldiri maskesi kontrol edildi. Hepsi birebir ayni."
        : "\nFARK VAR - hizli yol kullanilamaz.");
}

// Bitboard saldiri sorgusu, eski mailbox surumuyle her pozisyonda ve
// her karede ayni cevabi veriyor mu? Perft agacini gezip 64 kare x 2 renk
// icin ikisini karsilastirir.
static void AttackCheck(int depth)
{
    Console.WriteLine($"=== Saldiri sorgusu dogrulamasi (derinlik {depth}) ===");
    long positions = 0, comparisons = 0;
    bool ok = true;

    foreach (var test in Perft.StandardTests)
    {
        var board = new Board(test.Fen);
        bool good = Walk(board, depth, ref positions, ref comparisons);
        Console.WriteLine($"  {(good ? "OK  " : "HATA")} {test.Name}");
        if (!good) { ok = false; break; }
    }

    Console.WriteLine(ok
        ? $"\n{positions:N0} pozisyon x 128 sorgu = {comparisons:N0} karsilastirma. Hepsi ayni."
        : "\nFARK VAR.");

    static bool Walk(Board board, int depth, ref long positions, ref long comparisons)
    {
        positions++;
        for (int sq = 0; sq < 64; sq++)
        {
            foreach (int color in new[] { Piece.White, Piece.Black })
            {
                comparisons++;
                if (board.IsSquareAttacked(sq, color) != board.IsSquareAttackedSlow(sq, color))
                {
                    Console.WriteLine($"    {Square.Name(sq)} / {(color == Piece.White ? "beyaz" : "siyah")}: {board.ToFen()}");
                    return false;
                }
            }
        }

        if (depth == 0) return true;

        foreach (var move in MoveGenerator.GenerateLegalMoves(board))
        {
            var undo = board.MakeMove(move);
            bool good = Walk(board, depth - 1, ref positions, ref comparisons);
            board.UnmakeMove(undo);
            if (!good) return false;
        }
        return true;
    }
}

// Cok is parcacikli arama: cokmuyor mu, legal hamle donuyor mu, ve
// tek is parcacigina gore ne kadar derine iniyor?
static void SmpCheck(int threads, int ms)
{
    Console.WriteLine($"=== Lazy SMP kontrolu ({threads} is parcacigi, {ms} ms) ===");
    Console.WriteLine($"Makinede {Environment.ProcessorCount} mantiksal cekirdek var.\n");

    Console.WriteLine($"{"Pozisyon",-14} {"tek iş parçacığı",26}   |  {"çok iş parçacığı",26}");
    Console.WriteLine($"{"",-14} {"derinlik",9} {"düğüm",12}      |  {"derinlik",9} {"düğüm",12}");

    foreach (var test in Perft.StandardTests)
    {
        var results = new (int depth, long nodes, Move move)[2];

        for (int pass = 0; pass < 2; pass++)
        {
            int useThreads = pass == 0 ? 1 : threads;
            var board = new Board(test.Fen);
            var search = new Search(64) { UseOpeningBook = false, Threads = useThreads };
            var move = search.FindBestMove(board, maxDepth: 40, timeLimitMs: ms, verbose: false);

            // Donen hamle gercekten legal mi? Yaris kosulu bozuk hamle uretirse
            // en hizli burada yakalanir.
            bool legal = MoveGenerator.GenerateLegalMoves(board).Any(m => m.ToString() == move.ToString());
            if (!legal)
            {
                Console.WriteLine($"  HATA: legal olmayan hamle {move} — {test.Fen}");
                return;
            }
            results[pass] = (search.DepthReached, search.NodesSearched, move);
        }

        Console.WriteLine($"{test.Name,-14} {results[0].depth,9} {results[0].nodes,12}   |  " +
                          $"{results[1].depth,9} {results[1].nodes,12}   " +
                          $"x{(double)results[1].nodes / Math.Max(1, results[0].nodes):0.00} dugum");
    }

    Console.WriteLine("\nButun hamleler legal. (Derinlik farki kazanci gosterir, dugum carpani is hacmini.)");
}

// Bitboard SEE, eski mailbox SEE ile ayni cevabi veriyor mu?
// Perft agacini gezip her alista ikisini karsilastirir.
static void SeeCompare(int depth)
{
    Console.WriteLine($"=== SEE karsilastirmasi (derinlik {depth}) ===");
    long captures = 0;
    bool ok = true;

    foreach (var test in Perft.StandardTests)
    {
        var board = new Board(test.Fen);
        bool good = Walk(board, depth, ref captures);
        Console.WriteLine($"  {(good ? "OK  " : "HATA")} {test.Name}");
        if (!good) { ok = false; break; }
    }

    Console.WriteLine(ok
        ? $"\n{captures:N0} alis karsilastirildi. Hepsi ayni."
        : "\nFARK VAR.");

    static bool Walk(Board board, int depth, ref long captures)
    {
        foreach (var move in MoveGenerator.GeneratePseudoLegalMoves(board))
        {
            bool isCapture = board.Squares[move.To] != Piece.None
                             || move.Flag == MoveFlag.EnPassant
                             || move.Flag == MoveFlag.Promotion;
            if (!isCapture) continue;

            captures++;
            int fast = See.Evaluate(board, move);
            int slow = See.EvaluateSlow(board, move);
            if (fast != slow)
            {
                Console.WriteLine($"    {move}: bitboard {fast} vs mailbox {slow} — {board.ToFen()}");
                return false;
            }
        }

        if (depth == 0) return true;

        foreach (var move in MoveGenerator.GenerateLegalMoves(board))
        {
            var undo = board.MakeMove(move);
            bool good = Walk(board, depth - 1, ref captures);
            board.UnmakeMove(undo);
            if (!good) return false;
        }
        return true;
    }
}
