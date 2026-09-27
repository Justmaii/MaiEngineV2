using System.Diagnostics;

namespace ChessEngine;

/// <summary>
/// Negamax + alpha-beta budama + iterative deepening.
///
/// Negamax: "benim en iyi hamlem = rakibin en kötü cevabını veren hamle".
/// Minimax'ın tek fonksiyonla yazılmış hali, çünkü -Evaluate(rakip) = Evaluate(ben).
///
/// Alpha-beta: bir dalın kesinlikle daha kötü olduğu anlaşıldığı anda
/// geri kalanına bakmayı bırakır. Sonucu değiştirmez, sadece hızlandırır.
/// </summary>
public class Search
{
    public const int MateScore = 100_000;
    public const int Infinity = 1_000_000;

    public long NodesSearched { get; private set; }
    public int LastScore { get; private set; }
    public int DepthReached { get; private set; }

    public readonly TranspositionTable Table;

    /// <summary>Her iterasyon bittiğinde çağrılır (derinlik, puan, düğüm, ms, en iyi hamle).
    /// Konsol ve UCI çıktısı bunun üzerinden üretilir.</summary>
    public Action<int, int, long, long, Move>? OnIteration;

    /// <summary>Kitap seçimi ve değerlendirme gürültüsü için. Sabit tohum
    /// verirsen oyunlar tekrarlanabilir olur (hata ayıklarken işe yarar).</summary>
    public Random Random = new();

    private const int MaxPly = 64;

    /// <summary>Kapatılırsa motor sade alpha-beta'ya döner: null-move, LMR,
    /// PVS, killer/history ve şah uzatması devre dışı. Karşılaştırma için.</summary>
    public bool UseAdvancedSearch = true;

    /// <summary>Killer hamleler: alış OLMADIĞI halde beta kesmesi yapan hamleler.
    /// Aynı derinlikteki kardeş dallarda da genelde iyidirler, o yüzden
    /// sıralamada alışlardan hemen sonra denenirler.</summary>
    private readonly Move[,] _killers = new Move[MaxPly, 2];

    /// <summary>History: hangi (kalkış, varış) ikilisi geçmişte kaç kez kesme
    /// yaptı. Killer'lardan daha genel, daha zayıf bir sinyal.</summary>
    private readonly int[,] _history = new int[64, 64];

    /// <summary>
    /// Her derinlik için ayrılmış hamle ve puan tamponları.
    ///
    /// Arama saniyede yüz binlerce düğüm geziyor ve her düğümde hamle üretiyor.
    /// Bunun için her seferinde yeni dizi ayırmak çöp toplayıcıyı sürekli
    /// çalıştırır. Bir kez ayrılıp tekrar tekrar kullanılan tampon bunu bitirir.
    /// Her derinliğin kendi tamponu var çünkü arama özyinelemeli: alt düğüm
    /// çalışırken üst düğümün hamle listesi hâlâ duruyor olmalı.
    /// </summary>
    private readonly Move[][] _moveBuffers;
    private readonly int[][] _scoreBuffers;

    /// <summary>
    /// Üçgen PV tablosu: motorun düşündüğü varyantın tamamı.
    ///
    /// Arama zaten "en iyi hamle, sonra rakibin en iyi cevabı, sonra..."
    /// zincirini biliyor ama sonunda sadece ilk hamleyi döndürüyordu.
    /// Burada her derinlik kendi en iyi zincirini tutuyor ve alpha yükselince
    /// bir alt derinliğinkini kendi hamlesinin arkasına ekliyor.
    ///
    /// Oyun gücüne etkisi yok; izlerken ve hata ararken motorun NEDEN o hamleyi
    /// oynadığını görmeyi sağlıyor.
    /// </summary>
    private readonly Move[,] _pv = new Move[MaxPly, MaxPly];
    private readonly int[] _pvLength = new int[MaxPly];

    /// <summary>Arama sırasında ulaşılan en derin nokta (quiescence dahil).</summary>
    public int SelDepth { get; private set; }

    /// <summary>Son aramanın ana varyantı.</summary>
    public IReadOnlyList<Move> PrincipalVariation
    {
        get
        {
            var line = new List<Move>();
            for (int i = 0; i < _pvLength[0] && i < MaxPly; i++) line.Add(_pv[0, i]);
            return line;
        }
    }

    /// <summary>
    /// Yeni bir iterasyona başlamak için son an (ms). 0 = kapalı, sadece
    /// sert sınır kullanılır. Sert sınır (timeLimitMs) iterasyonu ORTASINDA
    /// keser ve o iterasyonun sonucu atılır; yumuşak sınır o israfı önler.
    /// </summary>
    public int SoftLimitMs;

    /// <summary>Kaç iş parçacığı? 1 = klasik tek çekirdekli arama.</summary>
    public int Threads = 1;

    /// <summary>Yardımcı iş parçacıklarına "dur" demenin tek yolu.
    /// Ana iş parçacığı süresi dolunca burayı işaretler.</summary>
    internal sealed class SearchControl { public volatile bool Stop; }
    internal SearchControl Control = new();

    /// <summary>Yardımcı iş parçacıkları için: tablo paylaşılır, geri kalan
    /// durum (killer, history, düğüm sayacı) her iş parçacığına özeldir.</summary>
    private Search(TranspositionTable sharedTable, SearchControl control)
    {
        (_moveBuffers, _scoreBuffers) = CreateBuffers();
        Table = sharedTable;
        Control = control;
        UseOpeningBook = false;
    }

    public Search(int tableSizeMb = 64)
    {
        Table = new TranspositionTable(tableSizeMb);
        (_moveBuffers, _scoreBuffers) = CreateBuffers();

        // Her motor örneği kendi gürültü tohumunu alır: aynı pozisyonda
        // hep aynı hamle yerine oyundan oyuna değişen tercihler.
        Evaluation.NoiseSeed = Random.Next();

        // Ölçüm kolaylığı: maç betikleri iş parçacığı sayısını ortam
        // değişkeniyle verebilsin. UCI'dan gelen "setoption Threads" bunu ezer.
        if (int.TryParse(Environment.GetEnvironmentVariable("MAIENGINE_THREADS"), out int envThreads)
            && envThreads > 0)
            Threads = envThreads;

        string? envSee = Environment.GetEnvironmentVariable("MAIENGINE_SEE");
        if (envSee is "1" or "true") UseSee = true;
        else if (envSee is "0" or "false") UseSee = false;
    }

    /// <summary>Quiescence kendi derinliğine iner, o yüzden tampon sayısı
    /// arama derinliğinden fazla olmalı.</summary>
    private const int MaxBufferPly = MaxPly * 2 + 8;

    /// <summary>
    /// Geç hamle azaltması tablosu: [derinlik][kaçıncı hamle] -> kaç ply azalt.
    ///
    /// Sabit "4. hamleden sonra 1, 8. hamleden sonra 2" yerine logaritmik bir
    /// eğri: derinlik arttıkça ve hamle listede geriye gittikçe azaltma artıyor.
    /// Sıralamaya güveniyoruz — listenin sonundaki sessiz hamlelerin iyi çıkma
    /// ihtimali düşük, yanılırsak zaten tam derinlikte tekrar arıyoruz.
    /// </summary>
    private static readonly int[,] LmrTable = BuildLmrTable();

    private static int[,] BuildLmrTable()
    {
        var table = new int[MaxPly + 1, MoveGenerator.MaxMoves];
        for (int depth = 1; depth <= MaxPly; depth++)
            for (int moveNumber = 1; moveNumber < MoveGenerator.MaxMoves; moveNumber++)
                table[depth, moveNumber] =
                    (int)(0.75 + Math.Log(depth) * Math.Log(moveNumber) / 2.25);
        return table;
    }

    private static (Move[][], int[][]) CreateBuffers()
    {
        var moves = new Move[MaxBufferPly][];
        var scores = new int[MaxBufferPly][];
        for (int i = 0; i < MaxBufferPly; i++)
        {
            moves[i] = new Move[MoveGenerator.MaxMoves];
            scores[i] = new int[MoveGenerator.MaxMoves];
        }
        return (moves, scores);
    }

    private readonly Stopwatch _timer = new();
    private int _timeLimitMs;
    private bool _stopped;
    private Move _bestMoveThisIteration;
    private Move _bestMoveOverall;

    /// <summary>Kök seviyedeki hamle sıralamasını iyileştirmek için:
    /// önceki iterasyonun en iyi hamlesi ilk denenir.</summary>
    private Move _previousBest;

    /// <summary>Açık: açılışta kitaptan hamle oynanır, arama yapılmaz.</summary>
    public bool UseOpeningBook = true;

    /// <summary>Piyon yapısı / şah güvenliği / fil çifti terimleri kullanılsın mı?
    /// Karşılaştırma maçlarında kapatılabilsin diye örnek başına tutuluyor.</summary>
    public bool UseAdvancedEval = false;

    /// <summary>Yeni arama teknikleri: aspiration windows, ters futility,
    /// futility budamasi ve quiescence'ta delta budamasi. Olculebilsin diye
    /// tek bayrak altinda.</summary>
    public bool UseSearchV2 = true;

    /// <summary>SEE ile kötü alışları budama ve sıralama.
    /// İlk ölçümde (mailbox SEE) 150 oyunda %49 vermişti — hesap doğruydu
    /// ama pahalıydı: her çağrıda 64 kare kopyalanıp saldıran yön yön
    /// aranıyordu. Bitboard sürümüyle 176 oyunda %56,5 = +46 elo
    /// (%95 güven +13..+79), o yüzden artık varsayılan olarak açık.</summary>
    public bool UseSee = true;

    // --- Ölçülüp REDDEDİLEN arama teknikleri ---
    //
    // Üçü de büyük motorlarda standart, üçü de burada kazanç vermedi.
    // Tek tek 16'şar oyunda %50 çıktılar; üçü birden açıkken 32 oyunda
    // %37,5 (-89 elo) verdiler, yani birlikte zarar ediyorlar: arama ağacı
    // fazla budanıyor ve derinlik kağıt üstünde artarken (16 -> 23 ply)
    // oyun gücü düşüyor.
    //
    // Kod duruyor ve ortam değişkeniyle açılabiliyor, çünkü doğru ayarlarla
    // (farklı eşikler, "improving" bayrağı, counter-move sıralaması) tekrar
    // denenmeye değerler. Ama ölçülmeden açılmazlar.
    public bool LateMovePruning = Environment.GetEnvironmentVariable("MAIENGINE_LMP") == "1";
    public bool InternalReduction = Environment.GetEnvironmentVariable("MAIENGINE_IIR") == "1";
    public bool LmrCurve = Environment.GetEnvironmentVariable("MAIENGINE_LMRCURVE") == "1";

    /// <summary>Son hamle kitaptan mı geldi?</summary>
    public bool LastMoveFromBook { get; private set; }

    public Move FindBestMove(Board board, int maxDepth = 6, int timeLimitMs = 3000, bool verbose = true)
    {
        LastMoveFromBook = false;

        // Değerlendirme bayrağı statik; arama tek iş parçacıklı ve sıralı
        // olduğu için her aramanın başında kendi ayarını yazması yeterli.
        Evaluation.UseAdvancedEval = UseAdvancedEval;

        if (UseOpeningBook)
        {
            // Once disaridan verilen Polyglot kitabi, yoksa gomulu kitap.
            var bookMove = PolyglotBook.Shared?.Probe(board, Random) ?? default;
            if (bookMove.IsNull) bookMove = OpeningBook.Probe(board, Random);
            if (!bookMove.IsNull)
            {
                LastMoveFromBook = true;
                LastScore = 0;
                DepthReached = 0;
                NodesSearched = 0;
                return bookMove;
            }
        }

        NodesSearched = 0;
        SelDepth = 0;
        Array.Clear(_pvLength);
        _stopped = false;
        _timeLimitMs = timeLimitMs;
        _previousBest = default;
        _bestMoveOverall = default;
        _timer.Restart();

        var legal = MoveGenerator.GenerateLegalMoves(board);
        if (legal.Count == 0) return default;
        _bestMoveOverall = legal[0];

        // Killer'lar derinliğe bağlı olduğu için yeni aramada geçersiz; sıfırla.
        // History daha genel bir sinyal, tamamen atmak yerine yarıya indiriyoruz.
        Array.Clear(_killers);

        Control.Stop = false;

        // --- Lazy SMP ---
        // Yardımcı iş parçacıkları aynı pozisyonu bağımsız arar; aralarındaki
        // tek bağ paylaşılan transposition table. Biri bir dalı çözdüğünde
        // sonucu tabloya yazar, diğerleri o dalı ucuza geçer. Kimse kimseye
        // iş dağıtmaz — "lazy" adı buradan geliyor: basit, ve ölçülebilir.
        var helpers = new List<Task>();
        var helperEngines = new List<Search>();

        if (Threads > 1)
        {
            string fen = board.ToFen();
            var history = board.PositionHistory.ToArray();

            for (int i = 1; i < Threads; i++)
            {
                // Her yardımcının KENDİ tahtası olmalı: MakeMove tahtayı değiştirir,
                // ve NNUE accumulator'ı tahtaya bağlı.
                var copy = new Board(fen);
                copy.PositionHistory.Clear();
                copy.PositionHistory.AddRange(history);

                var helper = new Search(Table, Control)
                {
                    UseAdvancedSearch = UseAdvancedSearch,
                    UseAdvancedEval = UseAdvancedEval,
                    UseSearchV2 = UseSearchV2,
                    UseSee = UseSee,
                    Random = new Random(Random.Next())
                };
                helperEngines.Add(helper);
                helpers.Add(Task.Run(() => helper.RunIterativeDeepening(copy, maxDepth, verbose: false)));
            }
        }

        RunIterativeDeepening(board, maxDepth, verbose);

        // Ana iş parçacığı bitti: yardımcılar da dursun ve toplansın.
        Control.Stop = true;
        if (helpers.Count > 0)
        {
            Task.WaitAll(helpers.ToArray());
            foreach (var helper in helperEngines) NodesSearched += helper.NodesSearched;
        }

        return _bestMoveOverall;
    }

    /// <summary>
    /// Iterative deepening döngüsü. Hem ana iş parçacığı hem yardımcılar
    /// aynı döngüyü çalıştırır; yardımcılar sadece ekrana bir şey yazmaz
    /// ve sonuçları Control.Stop ile kesilir.
    /// </summary>
    private void RunIterativeDeepening(Board board, int maxDepth, bool verbose)
    {
        _stopped = false;
        _timer.Restart();
        if (_timeLimitMs == 0) _timeLimitMs = int.MaxValue;   // yardımcıyı süre değil, ana iş parçacığı durdurur

        var legal = MoveGenerator.GenerateLegalMoves(board);
        if (legal.Count == 0) return;
        if (_bestMoveOverall.IsNull) _bestMoveOverall = legal[0];

        // Iterative deepening: önce 1 derinlik, sonra 2, 3...
        // Kulağa israf gibi gelir ama sığ aramanın sonucu derin aramanın
        // hamle sıralamasını iyileştirir ve net kazanç sağlar.
        for (int depth = 1; depth <= maxDepth; depth++)
        {
            _bestMoveThisIteration = default;

            // --- Aspiration windows ---
            // Bir sonraki derinliğin puanı genelde öncekine yakın çıkar. O yüzden
            // tüm pencereyi aramak yerine dar bir aralıkla başlarız; tutarsa arama
            // çok daha ucuz olur. Tutmazsa (puan aralığın dışına düşerse)
            // pencereyi genişletip tekrar ararız.
            int score;
            if (!UseSearchV2 || depth < 4)
            {
                score = Negamax(board, depth, 0, -Infinity, Infinity);
            }
            else
            {
                int window = 30;
                while (true)
                {
                    int alpha = Math.Max(-Infinity, LastScore - window);
                    int beta = Math.Min(Infinity, LastScore + window);

                    score = Negamax(board, depth, 0, alpha, beta);
                    if (_stopped) break;

                    // Sınıra dayandıysa gerçek puan dışarıda; pencereyi aç.
                    if (score <= alpha || score >= beta)
                    {
                        window *= 4;
                        if (window > 1200) window = Infinity;
                        continue;
                    }
                    break;
                }
            }

            if (_stopped) break; // yarım kalan iterasyonun sonucuna güvenilmez

            // Dar pencere kökte kesme yaparsa bu iterasyonda en iyi hamle
            // atanmamış olabilir; o durumda önceki derinliğinkini koruyoruz.
            bool moveChanged = !_bestMoveThisIteration.IsNull
                               && !_previousBest.IsNull
                               && _bestMoveThisIteration.ToString() != _previousBest.ToString();
            int previousScore = LastScore;

            if (!_bestMoveThisIteration.IsNull) _bestMoveOverall = _bestMoveThisIteration;
            _previousBest = _bestMoveOverall;
            LastScore = score;
            DepthReached = depth;

            OnIteration?.Invoke(depth, score, NodesSearched, _timer.ElapsedMilliseconds, _bestMoveOverall);

            if (verbose)
                Console.WriteLine($"  derinlik {depth,2}  puan {FormatScore(score),8}  " +
                                  $"hamle {_bestMoveOverall}  düğüm {NodesSearched,9}  " +
                                  $"tt {Table.Hits,8}  {_timer.ElapsedMilliseconds} ms");

            // Mat bulunduysa daha derine bakmanın anlamı yok.
            if (Math.Abs(score) > MateScore - 100) break;

            // --- Yumuşak sınır ---
            // Bir sonraki iterasyon kabaca bunun 2-3 katı sürer. Yumuşak sınırı
            // çoktan geçmişsek başlamanın anlamı yok: yarım kalacak ve sonucu
            // atılacak. Ama pozisyon oynaksa — en iyi hamle değiştiyse ya da
            // puan düştüyse — biraz daha düşünmeye değer, çünkü tam da böyle
            // anlarda yanlış hamle oynanır.
            if (SoftLimitMs > 0)
            {
                long elapsed = _timer.ElapsedMilliseconds;
                bool unstable = moveChanged || score < previousScore - 30;
                long budget = unstable ? SoftLimitMs * 5L / 4 : SoftLimitMs;
                if (elapsed >= budget) break;
            }
        }

    }

    // ================================================================
    //  Arama v3 — modern budama ve uzatmalar
    // ================================================================
    //
    // Her parça ortam değişkeniyle kapatılabilir (MAIENGINE_OFF=nmp,rfp,...),
    // böylece bir özelliğin katkısı tek tek ölçülebiliyor.
    private static readonly HashSet<string> Off = new(
        (Environment.GetEnvironmentVariable("MAIENGINE_OFF") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    private static readonly bool UseRfp = !Off.Contains("rfp");
    private static readonly bool UseRazor = !Off.Contains("razor");
    private static readonly bool UseNmp = !Off.Contains("nmp");
    private static readonly bool UseProbCut = !Off.Contains("probcut");
    private static readonly bool UseIir = !Off.Contains("iir");
    private static readonly bool UseSingular = !Off.Contains("se");
    private static readonly bool UseLmp = !Off.Contains("lmp");
    private static readonly bool UseFutility = !Off.Contains("fut");
    private static readonly bool UseHistPrune = !Off.Contains("hp");
    private static readonly bool UseSeePrune = !Off.Contains("seep");
    private static readonly bool UseCont = !Off.Contains("cont");
    private static readonly bool UseLmrV3 = !Off.Contains("lmr");
    private static readonly bool UseQsTt = !Off.Contains("qstt");
    private static readonly bool OldNmp = Off.Contains("nmpnew");
    private static readonly bool OldRfp = Off.Contains("rfpnew");
    private static readonly bool UseQsEvasion = !Off.Contains("qsevade");
    private static readonly bool PvTtCut = Off.Contains("pvttguard");
    private static readonly bool UseTtEval = !Off.Contains("tteval");
    private static readonly bool UseMdp = !Off.Contains("mdp");

    private const int NoEval = int.MinValue / 4;
    private const int HistMax = 16384;
    private const int PieceSlots = 12 * 64;

    // Arama yığını: her ply için yapılan hamle, oynayan taş, durağan puan.
    private readonly int[] _evalStack = new int[MaxBufferPly + 4];
    private readonly Move[] _moveStack = new Move[MaxBufferPly + 4];
    private readonly int[] _pieceStack = new int[MaxBufferPly + 4];
    private readonly Move[] _excluded = new Move[MaxBufferPly + 4];

    // History artık tarafa göre ayrı: beyazın g1-f3'ü ile siyahınki aynı şey değil.
    private readonly int[] _mainHist = new int[2 * 64 * 64];
    // Devam history'si: "bir önceki hamle X iken Y iyi miydi?" [önceki taş,kare][taş,kare]
    private readonly int[] _contHist = new int[PieceSlots * PieceSlots];
    // Karşı hamle: rakibin son hamlesine en son hangi cevap kesme yaptı.
    private readonly Move[] _counterMoves = new Move[PieceSlots];

    private int _timeCheck = 1024;

    /// <summary>Sıralama kalitesi ölçüsü: beta kesmelerinin kaçı ilk hamlede geldi.</summary>
    public static long FailHigh, FailHighFirst;

    /// <summary>Hamle şah çekiyor mu? Tahtada oynamadan, bitboard ile.
    /// Budamalar şah çeken hamleleri atlamamalı: mat çoğu zaman sessiz bir
    /// şahla gelir ve "kötü görünen" o hamle budanırsa mat görünmez.</summary>
    internal static bool GivesCheck(Board board, Move move)
    {
        int us = board.SideToMove, them = Piece.Opposite(us);
        int ksq = board.KingSquare[Piece.ColorIndex(them)];
        if (move.Flag == MoveFlag.Castle) return true;   // nadir; temkinli ol
        int piece = board.Squares[move.From];
        int type = move.Flag == MoveFlag.Promotion ? move.PromotionType : Piece.Type(piece);
        ulong occ = (board.Occupied & ~(1UL << move.From)) | (1UL << move.To);
        if (move.Flag == MoveFlag.EnPassant)
            occ &= ~(1UL << (us == Piece.White ? move.To - 8 : move.To + 8));
        ulong kbit = 1UL << ksq;
        int to = move.To;
        bool direct = type switch
        {
            Piece.Pawn => (Bitboards.PawnAttacks[Piece.ColorIndex(us)][to] & kbit) != 0,
            Piece.Knight => (Bitboards.KnightAttacks[to] & kbit) != 0,
            Piece.Bishop => (Bitboards.BishopAttacks(to, occ) & kbit) != 0,
            Piece.Rook => (Bitboards.RookAttacks(to, occ) & kbit) != 0,
            Piece.Queen => (Bitboards.QueenAttacks(to, occ) & kbit) != 0,
            _ => false
        };
        if (direct) return true;
        // Açarak şah: kalkış karesi boşalınca arkadaki kale/fil/vezir şaha değiyor mu?
        ulong fromMask = ~(1UL << move.From);
        ulong bq = (board.PiecesOf(us, Piece.Bishop) | board.PiecesOf(us, Piece.Queen)) & fromMask;
        ulong rq = (board.PiecesOf(us, Piece.Rook) | board.PiecesOf(us, Piece.Queen)) & fromMask;
        return (Bitboards.BishopAttacks(ksq, occ) & bq) != 0 || (Bitboards.RookAttacks(ksq, occ) & rq) != 0;
    }

    private static int PieceIndex(int piece) =>
        (Piece.Color(piece) == Piece.White ? 0 : 6) + Piece.Type(piece) - 1;

    private static void Gravity(ref int entry, int bonus)
    {
        bonus = Math.Clamp(bonus, -HistMax, HistMax);
        entry += bonus - entry * Math.Abs(bonus) / HistMax;
    }

    private static int HistoryBonus(int depth) => Math.Min(170 * depth - 100, 1600);

    private int QuietHistory(Board board, Move move, int ply)
    {
        int color = board.SideToMove == Piece.White ? 0 : 1;
        int h = _mainHist[(color * 64 + move.From) * 64 + move.To];
        if (!UseCont || ply < 1) return h;
        int cur = PieceIndex(board.Squares[move.From]) * 64 + move.To;
        if (_pieceStack[ply - 1] >= 0)
            h += _contHist[(_pieceStack[ply - 1] * 64 + _moveStack[ply - 1].To) * PieceSlots + cur];
        if (ply >= 2 && _pieceStack[ply - 2] >= 0)
            h += _contHist[(_pieceStack[ply - 2] * 64 + _moveStack[ply - 2].To) * PieceSlots + cur];
        return h;
    }

    private void UpdateQuietHistory(Board board, Move move, int ply, int bonus)
    {
        int color = board.SideToMove == Piece.White ? 0 : 1;
        Gravity(ref _mainHist[(color * 64 + move.From) * 64 + move.To], bonus);
        if (!UseCont || ply < 1) return;
        int cur = PieceIndex(board.Squares[move.From]) * 64 + move.To;
        if (_pieceStack[ply - 1] >= 0)
            Gravity(ref _contHist[(_pieceStack[ply - 1] * 64 + _moveStack[ply - 1].To) * PieceSlots + cur], bonus);
        if (ply >= 2 && _pieceStack[ply - 2] >= 0)
            Gravity(ref _contHist[(_pieceStack[ply - 2] * 64 + _moveStack[ply - 2].To) * PieceSlots + cur], bonus);
    }

    private Move CounterMoveAt(int ply) =>
        ply >= 1 && _pieceStack[ply - 1] >= 0
            ? _counterMoves[_pieceStack[ply - 1] * 64 + _moveStack[ply - 1].To]
            : default;

    private int Negamax(Board board, int depth, int ply, int alpha, int beta, bool cutNode = false)
    {
        if (_stopped) return 0;
        if (--_timeCheck <= 0)
        {
            _timeCheck = 1024;
            if (Control.Stop || _timer.ElapsedMilliseconds > _timeLimitMs)
            {
                _stopped = true;
                return 0;
            }
        }

        bool isPvNode = beta - alpha > 1;
        bool root = ply == 0;
        if (ply < MaxPly) _pvLength[ply] = ply;

        if (!root && (board.IsRepetition() || board.HalfmoveClock >= 100)) return 0;

        bool inCheck = board.IsInCheck(board.SideToMove);

        // Şah uzatması (derinlik bitmeden önce: şahtayken yaprağa inilmez).
        if (inCheck && UseAdvancedSearch && ply < MaxPly - 8) depth++;
        if (depth <= 0) return Quiescence(board, alpha, beta, ply);

        NodesSearched++;
        if (ply > SelDepth) SelDepth = ply;

        if (!root)
        {
            if (ply >= MaxPly - 2) return inCheck ? 0 : Evaluation.Evaluate(board);

            // Mat mesafesi budaması: daha kısa bir mat zaten bulunduysa
            // bu daldan daha iyisi çıkamaz.
            if (UseMdp)
            {
                alpha = Math.Max(alpha, -MateScore + ply);
                beta = Math.Min(beta, MateScore - ply - 1);
                if (alpha >= beta) return alpha;
            }
        }

        int alphaOriginal = alpha;
        Move excluded = _excluded[ply];
        bool hasExcluded = !excluded.IsNull;
        ulong key = board.ZobristKey;

        bool ttHit = false;
        int ttScore = 0, ttDepth = -100;
        NodeType ttType = NodeType.UpperBound;
        Move ttMove = default;
        int ttEval = short.MinValue;
        if (!hasExcluded)
            ttHit = Table.Probe(key, ply, out ttScore, out ttDepth, out ttType, out ttMove, out ttEval);

        // PV dışı düğümde yeterince derin kayıt varsa sonucu doğrudan kullan.
        if ((!isPvNode || (PvTtCut && !root)) && ttHit && ttDepth >= depth
            && (ttType == NodeType.Exact
                || (ttType == NodeType.LowerBound && ttScore >= beta)
                || (ttType == NodeType.UpperBound && ttScore <= alpha)))
        {
            return ttType == NodeType.LowerBound ? beta
                 : ttType == NodeType.UpperBound ? alpha
                 : Math.Clamp(ttScore, alpha, beta);
        }
        if (root && !_previousBest.IsNull) ttMove = _previousBest;

        // --- Durağan değerlendirme ve "improving" ---
        int staticEval, eval;
        if (inCheck)
        {
            staticEval = eval = NoEval;
            _evalStack[ply] = NoEval;
        }
        else
        {
            staticEval = hasExcluded ? _evalStack[ply]
                       : UseTtEval && ttHit && ttEval != short.MinValue ? ttEval
                       : Evaluation.Evaluate(board);
            eval = staticEval;
            // Tablodaki puan sınır olarak durağan puandan daha bilgili.
            if (ttHit && Math.Abs(ttScore) < MateScore - 1000
                && (ttType == NodeType.Exact
                    || (ttType == NodeType.LowerBound && ttScore > eval)
                    || (ttType == NodeType.UpperBound && ttScore < eval)))
                eval = ttScore;
            _evalStack[ply] = staticEval;
        }

        // improving: iki ply önceki (aynı taraf) durumumuzdan iyi miyiz?
        // İyileşiyorsak budamalar daha temkinli, kötüleşiyorsak daha cesur.
        bool improving = false;
        if (!inCheck)
        {
            if (ply >= 2 && _evalStack[ply - 2] != NoEval) improving = staticEval > _evalStack[ply - 2];
            else if (ply >= 4 && _evalStack[ply - 4] != NoEval) improving = staticEval > _evalStack[ply - 4];
            else improving = true;
        }

        if (!isPvNode && !inCheck && !hasExcluded && UseAdvancedSearch)
        {
            // --- Ters futility ---
            if (OldRfp)
            {
                if (depth <= 6 && Math.Abs(beta) < MateScore - 1000 && staticEval - 85 * depth >= beta)
                    return beta;
            }
            else if (UseRfp && depth <= 8 && Math.Abs(beta) < MateScore - 1000
                && eval - 80 * (depth - (improving ? 1 : 0)) >= beta)
                return beta;

            // --- Razoring: umutsuz görünen düğümü sessizlik aramasıyla doğrula ---
            if (UseRazor && depth <= 3 && eval + 250 * depth + 100 < alpha)
            {
                int razor = Quiescence(board, alpha, alpha + 1, ply);
                if (razor <= alpha) return alpha;
            }

            // --- Null move ---
            if (UseNmp && depth >= 3 && (OldNmp || eval >= beta) && !root
                && _pieceStack[ply - 1] >= 0            // üst üste iki null yok
                && board.HasNonPawnMaterial(board.SideToMove))
            {
                int r = OldNmp ? 3 + depth / 6 : 3 + depth / 3 + Math.Min((eval - beta) / 200, 3);
                _moveStack[ply] = default;
                _pieceStack[ply] = -1;
                var nullUndo = board.MakeNullMove();
                int nullScore = -Negamax(board, depth - r, ply + 1, -beta, -beta + 1, !cutNode);
                board.UnmakeNullMove(nullUndo);
                if (_stopped) return 0;
                if (nullScore >= beta) return beta;
            }

            // --- ProbCut ---
            // İyi bir alış, sığ aramada beta'nın epey üstünü veriyorsa tam
            // derinlikte de büyük ihtimalle beta'yı geçer: dalı kes.
            int probBeta = beta + 200;
            if (UseProbCut && depth >= 5 && Math.Abs(beta) < MateScore - 1000
                && !(ttHit && ttDepth >= depth - 3 && ttScore < probBeta))
            {
                int pSlot = Math.Min(ply, MaxBufferPly - 1);
                var pPicker = new MovePicker(this, board, -1, ttMove,
                                             _moveBuffers[pSlot], _scoreBuffers[pSlot], capturesOnly: true);
                while (pPicker.Next(out Move pm))
                {
                    if (staticEval + See.Evaluate(board, pm) < probBeta) continue;
                    _moveStack[ply] = pm;
                    _pieceStack[ply] = PieceIndex(board.Squares[pm.From]);
                    var pu = board.MakeMove(pm);
                    int v = -Quiescence(board, -probBeta, -probBeta + 1, ply + 1);
                    if (v >= probBeta)
                        v = -Negamax(board, depth - 4, ply + 1, -probBeta, -probBeta + 1, !cutNode);
                    board.UnmakeMove(pu);
                    if (_stopped) return 0;
                    if (v >= probBeta)
                    {
                        Table.Store(key, depth - 3, ply, v, NodeType.LowerBound, pm, staticEval);
                        return beta;
                    }
                }
            }
        }

        // --- IIR: tabloda hamle yoksa bir ply sığ ara ---
        if (UseIir && depth >= 4 && ttMove.IsNull && !hasExcluded && (isPvNode || cutNode)) depth--;

        int slot = Math.Min(ply, MaxBufferPly - 1);
        var picker = new MovePicker(this, board, ply, ttMove,
                                    _moveBuffers[slot], _scoreBuffers[slot], capturesOnly: false);

        Span<Move> quietsTried = stackalloc Move[64];
        int quietCount = 0;
        bool skipQuiets = false;
        Move bestMove = default;
        int movesSearched = 0;
        Move killer1 = ply < MaxPly ? _killers[ply, 0] : default;
        Move killer2 = ply < MaxPly ? _killers[ply, 1] : default;

        while (picker.Next(out Move move))
        {
            if (hasExcluded && SameMove(move, excluded)) continue;

            bool isCapture = IsCapture(board, move);
            bool isQuiet = !isCapture && move.Flag != MoveFlag.Promotion;
            if (skipQuiets && isQuiet && !GivesCheck(board, move)) continue;

            int hist = isQuiet ? QuietHistory(board, move, ply) : 0;

            // --- Sığ derinlikte hamle budamaları ---
            if (!root && !inCheck && movesSearched > 0 && UseAdvancedSearch
                && alpha > -MateScore + 1000 && !GivesCheck(board, move))
            {
                if (isQuiet)
                {
                    int lmrDepth = Math.Max(depth - 1 - LmrTable[Math.Min(depth, MaxPly),
                                            Math.Min(movesSearched + 1, MoveGenerator.MaxMoves - 1)], 0);

                    if (UseLmp && depth <= 8
                        && movesSearched >= (3 + depth * depth) / (improving ? 1 : 2))
                    {
                        skipQuiets = true;
                        continue;
                    }
                    if (UseFutility && lmrDepth <= 8 && staticEval + 120 + 110 * lmrDepth <= alpha)
                        continue;
                    if (UseHistPrune && lmrDepth <= 3 && hist < -3500 * depth)
                        continue;
                    if (UseSeePrune && lmrDepth <= 8 && See.Evaluate(board, move) < -25 * lmrDepth * lmrDepth)
                        continue;
                }
                else if (UseSeePrune && depth <= 8 && See.Evaluate(board, move) < -100 * depth)
                {
                    continue;
                }
            }

            // --- Singular extension ---
            // Tablodaki hamle, diğer bütün hamlelerden belirgin biçimde iyiyse
            // ("tek hamle"), onu bir ply daha derin ara.
            int extension = 0;
            if (UseSingular && !root && depth >= 8 && !hasExcluded && ttHit
                && SameMove(move, ttMove) && ttType != NodeType.UpperBound
                && ttDepth >= depth - 3 && Math.Abs(ttScore) < MateScore - 1000
                && ply < MaxPly - 12)
            {
                int sBeta = ttScore - 2 * depth;
                _excluded[ply] = move;
                int v = Negamax(board, (depth - 1) / 2, ply, sBeta - 1, sBeta, cutNode);
                _excluded[ply] = default;
                if (_stopped) return 0;

                if (v < sBeta) extension = 1;
                else if (sBeta >= beta) return beta;           // çoklu kesme
                else if (ttScore >= beta) extension = -1;       // ters uzatma
            }

            int newDepth = depth - 1 + extension;

            _moveStack[ply] = move;
            _pieceStack[ply] = PieceIndex(board.Squares[move.From]);
            var undo = board.MakeMove(move);
            bool givesCheck = board.IsInCheck(board.SideToMove);

            int score = 0;
            if (!UseAdvancedSearch)
            {
                score = -Negamax(board, depth - 1, ply + 1, -beta, -alpha);
            }
            else
            {
                bool doFull;
                if (depth >= 2 && movesSearched >= 1 && isQuiet)
                {
                    int r;
                    if (UseLmrV3)
                    {
                        r = LmrTable[Math.Min(depth, MaxPly), Math.Min(movesSearched + 1, MoveGenerator.MaxMoves - 1)];
                        if (isPvNode) r--;
                        if (cutNode) r++;
                        if (!improving) r++;
                        if (givesCheck) r--;
                        if (SameMove(move, killer1) || SameMove(move, killer2)) r--;
                        r -= hist / 8000;
                    }
                    else
                    {
                        r = depth >= 3 && movesSearched >= 4 && !inCheck ? (movesSearched >= 8 ? 2 : 1) : 0;
                    }
                    int d = Math.Clamp(newDepth - r, 1, Math.Max(newDepth, 1));
                    score = -Negamax(board, d, ply + 1, -alpha - 1, -alpha, true);
                    doFull = score > alpha && d < newDepth;
                }
                else
                {
                    doFull = !isPvNode || movesSearched > 0;
                }

                if (doFull)
                    score = -Negamax(board, newDepth, ply + 1, -alpha - 1, -alpha, !cutNode);

                if (isPvNode && (movesSearched == 0 || (score > alpha && score < beta)))
                    score = -Negamax(board, newDepth, ply + 1, -beta, -alpha, false);
            }

            board.UnmakeMove(undo);
            movesSearched++;

            if (_stopped) return 0;

            if (score >= beta)
            {
                FailHigh++;
                if (movesSearched == 1) FailHighFirst++;
                if (isQuiet && UseAdvancedSearch)
                {
                    if (ply < MaxPly && !SameMove(move, _killers[ply, 0]))
                    {
                        _killers[ply, 1] = _killers[ply, 0];
                        _killers[ply, 0] = move;
                    }
                    int bonus = HistoryBonus(depth);
                    UpdateQuietHistory(board, move, ply, bonus);
                    for (int i = 0; i < quietCount; i++)
                        UpdateQuietHistory(board, quietsTried[i], ply, -bonus);
                    if (ply >= 1 && _pieceStack[ply - 1] >= 0)
                        _counterMoves[_pieceStack[ply - 1] * 64 + _moveStack[ply - 1].To] = move;
                }

                if (!hasExcluded)
                    Table.Store(key, depth, ply, beta, NodeType.LowerBound, move, inCheck ? short.MinValue : staticEval);
                return beta;
            }

            if (isQuiet && quietCount < 64) quietsTried[quietCount++] = move;

            if (score > alpha)
            {
                alpha = score;
                bestMove = move;
                if (root) _bestMoveThisIteration = move;

                if (ply < MaxPly)
                {
                    _pv[ply, ply] = move;
                    int childLength = ply + 1 < MaxPly ? _pvLength[ply + 1] : ply + 1;
                    for (int next = ply + 1; next < childLength && next < MaxPly; next++)
                        _pv[ply, next] = _pv[ply + 1, next];
                    _pvLength[ply] = childLength;
                }
            }
        }

        if (movesSearched == 0)
        {
            if (hasExcluded) return alpha;
            return inCheck ? -MateScore + ply : 0;
        }

        if (!hasExcluded)
        {
            var type = alpha > alphaOriginal ? NodeType.Exact : NodeType.UpperBound;
            Table.Store(key, depth, ply, alpha, type, bestMove, inCheck ? short.MinValue : staticEval);
        }
        return alpha;
    }

    /// <summary>
    /// Sessizlik araması. Derinlik bittiğinde pozisyonu olduğu gibi puanlamak
    /// tehlikelidir: tam vezir alınmış, karşılığı bir sonraki hamlede geliyorsa
    /// motor kendini vezir önde sanır. Bu yüzden derinlik bitse bile ALIŞLAR
    /// bitene kadar bakmaya devam ederiz. Şahtaysak bütün kaçışlara bakılır:
    /// "olduğu gibi kalmak" (stand pat) şahtayken bir seçenek değil.
    /// </summary>
    private int Quiescence(Board board, int alpha, int beta, int ply)
    {
        if (_stopped) return 0;
        if (--_timeCheck <= 0)
        {
            _timeCheck = 1024;
            if (Control.Stop || _timer.ElapsedMilliseconds > _timeLimitMs) { _stopped = true; return 0; }
        }
        if (ply > SelDepth) SelDepth = ply;
        NodesSearched++;

        bool inCheck = UseQsEvasion && board.IsInCheck(board.SideToMove);
        if (ply >= MaxBufferPly - 2) return inCheck ? 0 : Evaluation.Evaluate(board);

        bool isPvNode = beta - alpha > 1;
        bool qHit = Table.Probe(board.ZobristKey, ply, out int ttScore, out _, out NodeType ttType, out _, out int qEval);
        if (UseQsTt && !isPvNode && qHit
            && (ttType == NodeType.Exact
                || (ttType == NodeType.LowerBound && ttScore >= beta)
                || (ttType == NodeType.UpperBound && ttScore <= alpha)))
        {
            return ttType == NodeType.LowerBound ? beta
                 : ttType == NodeType.UpperBound ? alpha
                 : Math.Clamp(ttScore, alpha, beta);
        }

        int standPat = 0;
        if (!inCheck)
        {
            standPat = UseTtEval && qHit && qEval != short.MinValue ? qEval : Evaluation.Evaluate(board);
            if (standPat >= beta) return beta;
            if (standPat > alpha) alpha = standPat;
        }

        int qSlot = Math.Min(ply, MaxBufferPly - 1);
        var qPicker = new MovePicker(this, board, -1, default,
                                     _moveBuffers[qSlot], _scoreBuffers[qSlot], capturesOnly: !inCheck);

        int searched = 0;
        while (qPicker.Next(out Move move))
        {
            if (!inCheck)
            {
                // --- Delta budaması ---
                if (UseSearchV2)
                {
                    int victim = board.Squares[move.To];
                    int gain = victim == Piece.None
                        ? Evaluation.PieceValues[Piece.Pawn]
                        : Evaluation.PieceValues[Piece.Type(victim)];
                    if (move.Flag == MoveFlag.Promotion)
                        gain += Evaluation.PieceValues[move.PromotionType];

                    if (standPat + gain + 200 < alpha) continue;
                }

                // --- SEE budaması ---
                if (UseSee && See.Evaluate(board, move) < 0) continue;
            }

            _moveStack[ply] = move;
            _pieceStack[ply] = PieceIndex(board.Squares[move.From]);
            var undo = board.MakeMove(move);
            int score = -Quiescence(board, -beta, -alpha, ply + 1);
            board.UnmakeMove(undo);
            searched++;

            if (score >= beta) return beta;
            if (score > alpha) alpha = score;
        }

        if (inCheck && searched == 0) return -MateScore + ply;
        return alpha;
    }

    private static bool SameMove(Move a, Move b) =>
        a.From == b.From && a.To == b.To && a.PromotionType == b.PromotionType;

    public static bool IsCapture(Board board, Move move) =>
        board.Squares[move.To] != Piece.None || move.Flag == MoveFlag.EnPassant;

    /// <summary>
    /// Hamleleri AŞAMA AŞAMA verir, hepsini birden üretmez.
    ///
    /// Sebebi basit bir gözlem: alpha-beta düğümlerinin çoğunda ilk birkaç
    /// hamleden biri kesme yapıyor ve geri kalanına hiç bakılmıyor. Hepsini
    /// üretip sıralamak, o düğümlerde tamamen boşa harcanan iş demek.
    ///
    /// Sıra: tablodaki hamle → alışlar ve terfiler → killer hamleler →
    /// sessiz hamleler. Sessiz hamleler ancak sıra onlara gelirse üretilir.
    ///
    /// Tablodaki hamle üretilmeden denendiği için önce doğrulanması gerekir
    /// (IsPseudoLegal): tablo çakışabilir ve başka pozisyonun hamlesini
    /// verebilir.
    /// </summary>
    private ref struct MovePicker
    {
        private const int StageTt = 0, StageGenCaptures = 1, StageCaptures = 2;
        private const int StageKiller1 = 3, StageKiller2 = 4;
        private const int StageGenQuiets = 5, StageQuiets = 6, StageDone = 7;

        private readonly Search _search;
        private readonly Board _board;
        private readonly int _ply;
        private readonly bool _capturesOnly;

        private readonly Move _ttMove;
        private readonly Move _killer1, _killer2;

        private Span<Move> _moves;
        private Span<int> _scores;
        private int _count, _index, _stage;

        public MovePicker(Search search, Board board, int ply, Move ttMove,
                          Span<Move> moves, Span<int> scores, bool capturesOnly)
        {
            _search = search;
            _board = board;
            _ply = ply;
            _capturesOnly = capturesOnly;
            _ttMove = ttMove;
            _moves = moves;
            _scores = scores;
            _count = 0;
            _index = 0;
            _stage = StageTt;

            bool useKillers = search.UseAdvancedSearch && !capturesOnly && ply >= 0 && ply < MaxPly;
            _killer1 = useKillers ? search._killers[ply, 0] : default;
            _killer2 = useKillers ? search._killers[ply, 1] : default;
        }

        /// <summary>Sıradaki hamle. false dönerse hamle kalmadı.</summary>
        public bool Next(out Move move)
        {
            while (true)
            {
                switch (_stage)
                {
                    case StageTt:
                        _stage = StageGenCaptures;
                        if (!_ttMove.IsNull && _board.IsPseudoLegal(_ttMove)
                            && (!_capturesOnly || IsNoisy(_ttMove))
                            && _board.IsMoveLegal(_ttMove))
                        {
                            move = _ttMove;
                            return true;
                        }
                        continue;

                    case StageGenCaptures:
                        _count = MoveGenerator.GenerateLegal(_board, _moves, GenType.Captures);
                        _search.ScoreMoves(_board, _moves, _scores, _count, default, -1);
                        _index = 0;
                        _stage = StageCaptures;
                        continue;

                    case StageCaptures:
                        while (_index < _count)
                        {
                            PickBest(_moves, _scores, _index, _count);
                            var candidate = _moves[_index++];
                            if (SameMove(candidate, _ttMove)) continue;
                            move = candidate;
                            return true;
                        }
                        _stage = _capturesOnly ? StageDone : StageKiller1;
                        continue;

                    case StageKiller1:
                        _stage = StageKiller2;
                        if (TryKiller(_killer1, out move)) return true;
                        continue;

                    case StageKiller2:
                        _stage = StageGenQuiets;
                        if (TryKiller(_killer2, out move)) return true;
                        continue;

                    case StageGenQuiets:
                        _count = MoveGenerator.GenerateLegal(_board, _moves, GenType.Quiets);
                        _search.ScoreMoves(_board, _moves, _scores, _count, default, _ply);
                        _index = 0;
                        _stage = StageQuiets;
                        continue;

                    case StageQuiets:
                        while (_index < _count)
                        {
                            PickBest(_moves, _scores, _index, _count);
                            var candidate = _moves[_index++];
                            if (SameMove(candidate, _ttMove)) continue;
                            if (SameMove(candidate, _killer1) || SameMove(candidate, _killer2)) continue;
                            move = candidate;
                            return true;
                        }
                        _stage = StageDone;
                        continue;

                    default:
                        move = default;
                        return false;
                }
            }
        }

        private bool TryKiller(Move killer, out Move move)
        {
            move = default;
            if (killer.IsNull || SameMove(killer, _ttMove)) return false;
            if (!_board.IsPseudoLegal(killer)) return false;
            if (IsNoisy(killer)) return false;           // alış aşaması zaten verdi
            if (!_board.IsMoveLegal(killer)) return false;

            move = killer;
            return true;
        }

        private bool IsNoisy(Move move) =>
            _board.Squares[move.To] != Piece.None
            || move.Flag == MoveFlag.EnPassant
            || move.Flag == MoveFlag.Promotion;
    }

    /// <summary>
    /// Hamle sıralaması alpha-beta'nın en önemli parçasıdır: iyi hamleler
    /// önce denenirse çok daha fazla dal kesilir. Aynı arama, 5-10 kat hızlı.
    /// </summary>
    /// <summary>
    /// Hamle sıralaması alpha-beta'nın en önemli parçasıdır: iyi hamleler
    /// önce denenirse çok daha fazla dal kesilir. Aynı arama, 5-10 kat hızlı.
    ///
    /// Burada sadece PUANLAMA yapılıyor; sıralama PickBest ile adım adım.
    /// </summary>
    private void ScoreMoves(Board board, Span<Move> moves, Span<int> scores, int count,
                            Move priority, int ply)
    {
        for (int i = 0; i < count; i++)
        {
            var move = moves[i];
            int score = 0;

            if (!priority.IsNull && SameMove(move, priority)) score += 1_000_000;

            int victim = board.Squares[move.To];
            if (victim != Piece.None)
            {
                // MVV-LVA: değerli taşı, değersiz taşla al (vezir al > piyonla, +).
                int attacker = board.Squares[move.From];
                int captureScore = Evaluation.PieceValues[Piece.Type(victim)] * 10
                                 - Evaluation.PieceValues[Piece.Type(attacker)];

                // SEE zararlı diyorsa alışı killer'ların da altına indir:
                // MVV-LVA "vezirle piyon al" der, SEE "ama piyon korunuyor" der.
                if (UseSee && See.Evaluate(board, move) < 0) score += 1_000 + captureScore;
                else score += 10_000 + captureScore;
            }
            else if (move.Flag == MoveFlag.EnPassant)
            {
                score += 10_000 + Evaluation.PieceValues[Piece.Pawn] * 10;
            }

            if (move.Flag == MoveFlag.Promotion)
                score += 9_000 + Evaluation.PieceValues[move.PromotionType];

            if (UseAdvancedSearch && victim == Piece.None && move.Flag != MoveFlag.Promotion
                && move.Flag != MoveFlag.EnPassant && ply >= 0 && ply < MaxBufferPly)
            {
                // Sessiz hamleler: killer'lar zaten ayrı aşamada verildi.
                // Karşı hamle en üstte, sonra history + devam history'si.
                score = QuietHistory(board, move, ply);
                if (SameMove(move, CounterMoveAt(ply))) score += 50_000;
            }

            scores[i] = score;
        }
    }

    /// <summary>Kalanların en yüksek puanlısını index konumuna taşır.
    /// Tam sıralama yerine bu: kesme olursa geri kalan hiç sıralanmaz.</summary>
    private static void PickBest(Span<Move> moves, Span<int> scores, int index, int count)
    {
        int best = index;
        for (int i = index + 1; i < count; i++)
            if (scores[i] > scores[best]) best = i;

        if (best == index) return;

        (moves[index], moves[best]) = (moves[best], moves[index]);
        (scores[index], scores[best]) = (scores[best], scores[index]);
    }

    public static string FormatScore(int score)
    {
        if (Math.Abs(score) > MateScore - 100)
        {
            int movesToMate = (MateScore - Math.Abs(score) + 1) / 2;
            return (score > 0 ? "M" : "-M") + movesToMate;
        }
        return score.ToString();
    }
}
