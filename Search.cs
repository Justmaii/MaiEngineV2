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
        Table = sharedTable;
        Control = control;
        UseOpeningBook = false;
    }

    public Search(int tableSizeMb = 64)
    {
        Table = new TranspositionTable(tableSizeMb);

        // Her motor örneği kendi gürültü tohumunu alır: aynı pozisyonda
        // hep aynı hamle yerine oyundan oyuna değişen tercihler.
        Evaluation.NoiseSeed = Random.Next();

        // Ölçüm kolaylığı: maç betikleri iş parçacığı sayısını ortam
        // değişkeniyle verebilsin. UCI'dan gelen "setoption Threads" bunu ezer.
        if (int.TryParse(Environment.GetEnvironmentVariable("MAIENGINE_THREADS"), out int envThreads)
            && envThreads > 0)
            Threads = envThreads;
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
    /// Varsayilan KAPALI: hesap doğru (seecheck 6/6) ama 150 oyunda kazanç
    /// vermedi (%49). Muhtemel sebep maliyet — mailbox tahtada SEE her
    /// çağrıda 64 kareyi kopyalayıp saldıran arıyor. Bitboard'a geçilirse
    /// çok ucuzlar ve tekrar ölçülmeli.</summary>
    public bool UseSee = false;

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
        for (int from = 0; from < 64; from++)
            for (int to = 0; to < 64; to++)
                _history[from, to] /= 2;

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
        }

    }

    private int Negamax(Board board, int depth, int ply, int alpha, int beta)
    {
        if (_stopped) return 0;
        if ((NodesSearched & 2047) == 0
            && (Control.Stop || _timer.ElapsedMilliseconds > _timeLimitMs))
        {
            _stopped = true;
            return 0;
        }
        NodesSearched++;

        // Beraberlikler: kökte değilse hemen 0 dön, aramaya gerek yok.
        if (ply > 0 && (board.IsRepetition() || board.HalfmoveClock >= 100)) return 0;

        // Tabloda bu pozisyon var mı?
        int alphaOriginal = alpha;
        Move ttMove = default;
        if (ply > 0 && Table.TryProbe(board.ZobristKey, depth, ply, alpha, beta,
                                      out int ttScore, out ttMove))
        {
            return ttScore;
        }

        bool inCheck = board.IsInCheck(board.SideToMove);
        bool isPvNode = beta - alpha > 1;

        // Şah uzatması: şahtayken durmak tehlikelidir, pozisyon "sessiz" değildir.
        // Bir derinlik daha bakarak şahın nasıl çözüldüğünü görürüz.
        //
        // ply sınırı şart: uzatma derinliği azaltmadığı için sürekli şah veren
        // bir varyant (ebedî şah) aramayı sonsuza kadar derinleştirebilir.
        if (inCheck && UseAdvancedSearch && ply < MaxPly - 8) depth++;

        if (depth <= 0) return Quiescence(board, alpha, beta);

        // --- Ters futility (static null move) ---
        // Durağan değerlendirme beta'nın bu kadar üstündeyse, rakip bu düğüme
        // izin vermeyecek kadar kötü durumda demektir; aramaya gerek yok.
        // Sadece sığ derinlikte ve PV dışı düğümlerde güvenli.
        int staticEval = 0;
        bool haveStaticEval = false;

        if (UseSearchV2 && !inCheck && !isPvNode && depth <= 6
            && Math.Abs(beta) < MateScore - 1000)
        {
            staticEval = Evaluation.Evaluate(board);
            haveStaticEval = true;
            if (staticEval - 85 * depth >= beta) return beta;
        }

        // --- Null-move budaması ---
        // Fikir: "rakibe bedava bir hamle versem bile pozisyonum hâlâ beta'nın
        // üstündeyse, gerçek hamlemle kesin üstündedir" — o zaman bu dalı
        // ucuza (azaltılmış derinlikte) kesebiliriz.
        // Şartlar: şahta olmamak, PV düğümü olmamak, ve TAŞ SAHİBİ OLMAK —
        // sadece piyonu kalan tarafta zugzwang olur, yani hamle yapmak zarardır
        // ve varsayım ters teper.
        if (UseAdvancedSearch && !inCheck && depth >= 3 && ply > 0 && beta - alpha == 1
            && board.HasNonPawnMaterial(board.SideToMove))
        {
            int reduction = 2 + depth / 6;
            var nullUndo = board.MakeNullMove();
            int nullScore = -Negamax(board, depth - 1 - reduction, ply + 1, -beta, -beta + 1);
            board.UnmakeNullMove(nullUndo);

            if (_stopped) return 0;
            if (nullScore >= beta) return beta;
        }

        var moves = MoveGenerator.GenerateLegalMoves(board);

        if (moves.Count == 0)
        {
            // Şahtaysak mat, değilsek pat.
            // Mat puanına ply eklenir ki motor "erken mat"ı tercih etsin.
            return inCheck ? -MateScore + ply : 0;
        }

        // Sıralama önceliği: kökte önceki iterasyonun en iyisi,
        // derinlerde tablodaki hamle.
        OrderMoves(board, moves, ply == 0 ? _previousBest : ttMove, ply);

        Move bestMove = moves[0];
        int movesSearched = 0;

        // --- Futility budaması ---
        // Sığ derinlikte, durağan puan alpha'nın epey altındaysa sessiz hamleler
        // pozisyonu kurtarmaya yetmez; sadece alış ve terfilere bakarız.
        bool futilityPrune = false;
        if (UseSearchV2 && !inCheck && !isPvNode && depth <= 2
            && Math.Abs(alpha) < MateScore - 1000)
        {
            if (!haveStaticEval) { staticEval = Evaluation.Evaluate(board); haveStaticEval = true; }
            futilityPrune = staticEval + 120 * depth <= alpha;
        }

        foreach (var move in moves)
        {
            bool isQuiet = !IsCapture(board, move) && move.Flag != MoveFlag.Promotion;

            // Budanan sessiz hamle; en az bir hamle mutlaka aranmalı.
            if (futilityPrune && isQuiet && movesSearched > 0) continue;

            var undo = board.MakeMove(move);

            int score;
            if (movesSearched == 0 || !UseAdvancedSearch)
            {
                // İlk hamle (sıralamaya göre en umutlusu) tam pencereyle aranır.
                score = -Negamax(board, depth - 1, ply + 1, -beta, -alpha);
            }
            else
            {
                // --- Geç hamle azaltması (LMR) ---
                // Sıralama işe yarıyorsa listenin sonundaki sessiz hamleler
                // muhtemelen kötüdür. Onları önce SIĞ arayıp eleriz; yanılırsak
                // (puan alpha'yı aşarsa) tam derinlikte tekrar ararız.
                int reduction = 0;
                if (isQuiet && depth >= 3 && movesSearched >= 4 && !inCheck)
                    reduction = movesSearched >= 8 ? 2 : 1;

                // --- Ana varyant araması (PVS) ---
                // İlk hamle en iyiyse gerisi sadece "daha iyi değil" diye
                // kanıtlanmalı. Bunu en dar pencereyle (alpha, alpha+1) yapmak
                // çok daha ucuzdur; kanıt tutmazsa tam pencereyle tekrarlarız.
                score = -Negamax(board, depth - 1 - reduction, ply + 1, -alpha - 1, -alpha);

                if (score > alpha && reduction > 0)
                    score = -Negamax(board, depth - 1, ply + 1, -alpha - 1, -alpha);

                if (score > alpha && score < beta)
                    score = -Negamax(board, depth - 1, ply + 1, -beta, -alpha);
            }

            board.UnmakeMove(undo);
            movesSearched++;

            if (_stopped) return 0;

            if (score >= beta)
            {
                // Alış olmayan bir hamle kesme yaptıysa killer ve history'ye yaz.
                if (isQuiet && UseAdvancedSearch)
                {
                    if (ply < MaxPly)
                    {
                        _killers[ply, 1] = _killers[ply, 0];
                        _killers[ply, 0] = move;
                    }
                    _history[move.From, move.To] += depth * depth;
                }

                // Rakip buraya izin vermez, dalı kes. Puan bir ALT sınır.
                Table.Store(board.ZobristKey, depth, ply, beta, NodeType.LowerBound, move);
                return beta;
            }

            if (score > alpha)
            {
                alpha = score;
                bestMove = move;
                if (ply == 0) _bestMoveThisIteration = move;
            }
        }

        // alpha hiç aşılmadıysa puan kesin değil, ÜST sınırdır.
        var type = alpha > alphaOriginal ? NodeType.Exact : NodeType.UpperBound;
        Table.Store(board.ZobristKey, depth, ply, alpha, type, bestMove);

        return alpha;
    }

    /// <summary>
    /// Sessizlik araması. Derinlik bittiğinde pozisyonu olduğu gibi puanlamak
    /// tehlikelidir: tam vezir alınmış, karşılığı bir sonraki hamlede geliyorsa
    /// motor kendini vezir önde sanır. Bu yüzden derinlik bitse bile ALIŞLAR
    /// bitene kadar bakmaya devam ederiz.
    /// </summary>
    private int Quiescence(Board board, int alpha, int beta)
    {
        NodesSearched++;

        int standPat = Evaluation.Evaluate(board);
        if (standPat >= beta) return beta;
        if (standPat > alpha) alpha = standPat;

        var captures = MoveGenerator.GenerateLegalMoves(board)
                                    .Where(m => IsCapture(board, m))
                                    .ToList();
        OrderMoves(board, captures, default);

        foreach (var move in captures)
        {
            // --- Delta budaması ---
            // Alınan taşı bedavaya alsak bile alpha'ya yaklaşamıyorsak bu alışa
            // bakmanın anlamı yok. 200 santipiyon pay, pozisyonel sürprizler için.
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
            // Alışveriş sonunda zarar eden alışa bakmanın anlamı yok.
            // Şahtayken uygulanmaz: orada her hamle zorunlu olabilir.
            if (UseSee && !board.IsInCheck(board.SideToMove) && See.Evaluate(board, move) < 0)
                continue;

            var undo = board.MakeMove(move);
            int score = -Quiescence(board, -beta, -alpha);
            board.UnmakeMove(undo);

            if (score >= beta) return beta;
            if (score > alpha) alpha = score;
        }

        return alpha;
    }

    private static bool SameMove(Move a, Move b) =>
        a.From == b.From && a.To == b.To && a.PromotionType == b.PromotionType;

    public static bool IsCapture(Board board, Move move) =>
        board.Squares[move.To] != Piece.None || move.Flag == MoveFlag.EnPassant;

    /// <summary>
    /// Hamle sıralaması alpha-beta'nın en önemli parçasıdır: iyi hamleler
    /// önce denenirse çok daha fazla dal kesilir. Aynı arama, 5-10 kat hızlı.
    /// </summary>
    private void OrderMoves(Board board, List<Move> moves, Move priority, int ply = -1)
    {
        var scores = new int[moves.Count];

        for (int i = 0; i < moves.Count; i++)
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

            if (UseAdvancedSearch && victim == Piece.None && move.Flag == MoveFlag.Normal
                && ply >= 0 && ply < MaxPly)
            {
                // Killer'lar alışların hemen altında, history onların da altında.
                if (SameMove(move, _killers[ply, 0])) score += 8_000;
                else if (SameMove(move, _killers[ply, 1])) score += 7_000;
                else score += Math.Min(_history[move.From, move.To], 6_000);
            }

            scores[i] = score;
        }

        var arr = moves.ToArray();
        Array.Sort(scores, arr);
        Array.Reverse(arr); // Array.Sort artan sıralar, biz azalan istiyoruz
        moves.Clear();
        moves.AddRange(arr);
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
