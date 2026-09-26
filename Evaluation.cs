namespace ChessEngine;

/// <summary>
/// Pozisyonu sayıya çevirir. İki bileşen var:
///   1) Materyal — hangi taşlar tahtada?
///   2) Piece-square tablolar — taş hangi karede duruyor?
/// Sonuç SANTIPİYON cinsindendir (100 = bir piyon) ve daima
/// SIRASI GELEN tarafın gözünden verilir (pozitif = benim lehime).
/// </summary>
public static class Evaluation
{
    public static readonly int[] PieceValues = { 0, 100, 320, 330, 500, 900, 0 };
    //                                      boş,  P,   N,   B,   R,   Q,  K

    // Tablolar OKUNAKLI olsun diye 8. sıradan başlayarak yazıldı
    // (ekranda gördüğün tahta gibi), sonra a1-indeksli diziye çevriliyor.
    private static readonly int[] PawnTable = Flip(new[]
    {
         0,  0,  0,  0,  0,  0,  0,  0,
        50, 50, 50, 50, 50, 50, 50, 50,
        10, 10, 20, 30, 30, 20, 10, 10,
         5,  5, 10, 25, 25, 10,  5,  5,
         0,  0,  0, 20, 20,  0,  0,  0,
         5, -5,-10,  0,  0,-10, -5,  5,
         5, 10, 10,-20,-20, 10, 10,  5,
         0,  0,  0,  0,  0,  0,  0,  0
    });

    private static readonly int[] KnightTable = Flip(new[]
    {
        -50,-40,-30,-30,-30,-30,-40,-50,
        -40,-20,  0,  0,  0,  0,-20,-40,
        -30,  0, 10, 15, 15, 10,  0,-30,
        -30,  5, 15, 20, 20, 15,  5,-30,
        -30,  0, 15, 20, 20, 15,  0,-30,
        -30,  5, 10, 15, 15, 10,  5,-30,
        -40,-20,  0,  5,  5,  0,-20,-40,
        -50,-40,-30,-30,-30,-30,-40,-50
    });

    private static readonly int[] BishopTable = Flip(new[]
    {
        -20,-10,-10,-10,-10,-10,-10,-20,
        -10,  0,  0,  0,  0,  0,  0,-10,
        -10,  0,  5, 10, 10,  5,  0,-10,
        -10,  5,  5, 10, 10,  5,  5,-10,
        -10,  0, 10, 10, 10, 10,  0,-10,
        -10, 10, 10, 10, 10, 10, 10,-10,
        -10,  5,  0,  0,  0,  0,  5,-10,
        -20,-10,-10,-10,-10,-10,-10,-20
    });

    private static readonly int[] RookTable = Flip(new[]
    {
         0,  0,  0,  0,  0,  0,  0,  0,
         5, 10, 10, 10, 10, 10, 10,  5,
        -5,  0,  0,  0,  0,  0,  0, -5,
        -5,  0,  0,  0,  0,  0,  0, -5,
        -5,  0,  0,  0,  0,  0,  0, -5,
        -5,  0,  0,  0,  0,  0,  0, -5,
        -5,  0,  0,  0,  0,  0,  0, -5,
         0,  0,  0,  5,  5,  0,  0,  0
    });

    private static readonly int[] QueenTable = Flip(new[]
    {
        -20,-10,-10, -5, -5,-10,-10,-20,
        -10,  0,  0,  0,  0,  0,  0,-10,
        -10,  0,  5,  5,  5,  5,  0,-10,
         -5,  0,  5,  5,  5,  5,  0, -5,
          0,  0,  5,  5,  5,  5,  0, -5,
        -10,  5,  5,  5,  5,  5,  0,-10,
        -10,  0,  5,  0,  0,  0,  0,-10,
        -20,-10,-10, -5, -5,-10,-10,-20
    });

    /// <summary>Oyun ortası: şah kaleye kaçsın, merkezden uzak dursun.</summary>
    private static readonly int[] KingMiddleTable = Flip(new[]
    {
        -30,-40,-40,-50,-50,-40,-40,-30,
        -30,-40,-40,-50,-50,-40,-40,-30,
        -30,-40,-40,-50,-50,-40,-40,-30,
        -30,-40,-40,-50,-50,-40,-40,-30,
        -20,-30,-30,-40,-40,-30,-30,-20,
        -10,-20,-20,-20,-20,-20,-20,-10,
         20, 20,  0,  0,  0,  0, 20, 20,
         20, 30, 10,  0,  0, 10, 30, 20
    });

    /// <summary>Oyun sonu: şah aktif olsun, merkeze yürüsün.</summary>
    private static readonly int[] KingEndTable = Flip(new[]
    {
        -50,-40,-30,-20,-20,-30,-40,-50,
        -30,-20,-10,  0,  0,-10,-20,-30,
        -30,-10, 20, 30, 30, 20,-10,-30,
        -30,-10, 30, 40, 40, 30,-10,-30,
        -30,-10, 30, 40, 40, 30,-10,-30,
        -30,-10, 20, 30, 30, 20,-10,-30,
        -30,-30,  0,  0,  0,  0,-30,-30,
        -50,-30,-30,-30,-30,-30,-30,-50
    });

    /// <summary>8. sıradan yazılmış tabloyu a1=0 indekslemesine çevirir.</summary>
    private static int[] Flip(int[] table)
    {
        var result = new int[64];
        for (int sq = 0; sq < 64; sq++)
        {
            int file = Square.File(sq), rank = Square.Rank(sq);
            result[sq] = table[(7 - rank) * 8 + file];
        }
        return result;
    }

    private static int[] TableFor(int type, bool endgame) => type switch
    {
        Piece.Pawn => PawnTable,
        Piece.Knight => KnightTable,
        Piece.Bishop => BishopTable,
        Piece.Rook => RookTable,
        Piece.Queen => QueenTable,
        Piece.King => endgame ? KingEndTable : KingMiddleTable,
        _ => PawnTable
    };

    /// <summary>Piyon dışı materyal bu eşiğin altına inince oyun sonu sayılır.</summary>
    private const int EndgameThreshold = 1300;

    /// <summary>
    /// Çeşitlilik için puana eklenen küçük sapma (santipiyon). 0 = kapalı.
    ///
    /// Neden kökte "eşit puanlı hamleler arasından rastgele seç" değil de bu?
    /// Çünkü alpha-beta'da bir hamlenin puanı alpha'ya EŞİT çıkması "aynı
    /// derecede iyi" demek değildir — pencere yüzünden dönen değer sadece bir
    /// ÜST sınırdır, hamle gerçekte daha kötü olabilir. Onları eşit sanıp
    /// aralarından seçmek motora kötü hamle oynatır.
    ///
    /// Sapma pozisyonun Zobrist anahtarından türetiliyor: aynı pozisyon arama
    /// boyunca hep aynı sapmayı alır, yani transposition table tutarlı kalır.
    /// Tohum değişince tercihler değişir, motorun gücü ölçülebilir şekilde
    /// etkilenmez (±6 santipiyon bir piyonun %6'sı).
    /// </summary>
    public static int NoiseCentipawns = 6;
    public static int NoiseSeed;

    /// <summary>
    /// Kapatılırsa motor sadece materyal + kare tablolarıyla değerlendirir.
    /// Piyon yapısı / şah güvenliği / fil çiftinin gerçekten kazanç sağlayıp
    /// sağlamadığını maçla ölçebilmek için.
    /// </summary>
    /// Varsayilan KAPALI: terimler dogru calisiyor (isaret testleri geciyor)
    /// ama 100 oyunluk olcumde guc kazanci vermedi (50/100). Sebebi hiz:
    /// ayni derinlige %22 daha yavas iniliyor, kazanilan bilgi kaybedilen
    /// derinlikle takas oluyor. Piyon hash tablosu eklenince acilmali.
    public static bool UseAdvancedEval = false;

    // --- Piyon yapısı katsayıları (santipiyon) ---
    private const int DoubledPawnPenalty = 12;   // aynı dosyada ikinci piyon
    private const int IsolatedPawnPenalty = 15;  // komşu dosyalarda destek yok
    private const int BishopPairBonus = 30;      // iki fil birbirini tamamlar

    /// <summary>Geçer piyonun bulunduğu sıraya göre değeri. 7. sıradaki piyon
    /// neredeyse bir kale kadar tehlikelidir.</summary>
    private static readonly int[] PassedPawnBonus = { 0, 5, 12, 25, 45, 75, 120, 0 };

    /// <summary>Şahın önündeki her eksik piyon için ceza (oyun ortasında).</summary>
    private const int MissingShieldPenalty = 18;
    private const int OpenFileNearKingPenalty = 14;

    /// <summary>
    /// Piyon yapısı + şah güvenliği + fil çifti. Beyazın gözünden döner.
    ///
    /// Bu terimler materyal ve kare tablolarının göremediği şeyi görür:
    /// aynı taşlarla aynı karelerde duran iki pozisyondan biri kazanılmış,
    /// diğeri kaybedilmiş olabilir — farkı piyon iskeleti ve şahın durumu yaratır.
    /// </summary>
    /// <summary>
    /// Piyon yapısı önbelleği. Piyon iskeleti sadece piyon hamlelerinde değişir,
    /// yani arama boyunca çoğu düğümde aynıdır — bir kez hesaplayıp saklamak
    /// değerlendirmeyi belirgin hızlandırır.
    /// </summary>
    // Piyon yapısı önbelleği. Piyon iskeleti sadece piyon hamlelerinde değişir,
    // yani arama boyunca çoğu düğümde aynıdır — bir kez hesaplayıp saklamak
    // değerlendirmeyi belirgin hızlandırır.
    //
    // Paralel diziler kullanılıyor: anahtar, geçerlilik, puan ve dosya başına
    // piyon sayıları. Sayılar da saklanıyor çünkü şah kalkanı onlara bakıyor.
    private const int PawnCacheSize = 1 << 16;

    [ThreadStatic] private static ulong[]? _pawnKeys;
    [ThreadStatic] private static bool[]? _pawnValid;
    [ThreadStatic] private static int[]? _pawnScores;
    [ThreadStatic] private static byte[]? _pawnCounts;   // 16 bayt/kayıt: 0-7 beyaz, 8-15 siyah

    public static long PawnCacheHits;
    public static long PawnCacheProbes;

    /// <summary>Önbelleği boşaltır. Doğrulama testinde kullanılıyor.</summary>
    public static void ClearPawnCache()
    {
        if (_pawnValid != null) Array.Clear(_pawnValid);
        PawnCacheHits = 0;
        PawnCacheProbes = 0;
    }

    /// <summary>Piyon yapısı hesabı — önbellekten gelir ya da hesaplanıp saklanır.
    /// Dosya sayıları da döner, çünkü şah kalkanı onlara ihtiyaç duyuyor.</summary>
    private static int PawnStructure(Board board, out int countBase)
    {
        Span<int> whiteCount = stackalloc int[8];
        Span<int> blackCount = stackalloc int[8];

        _pawnKeys ??= new ulong[PawnCacheSize];
        _pawnValid ??= new bool[PawnCacheSize];
        _pawnScores ??= new int[PawnCacheSize];
        _pawnCounts ??= new byte[PawnCacheSize * 16];

        PawnCacheProbes++;

        ulong key = board.PawnKey;
        int index = (int)(key & (PawnCacheSize - 1));
        countBase = index * 16;

        // İsabet durumunda hiçbir şey kopyalanmıyor: şah kalkanı sayıları
        // doğrudan önbellek dizisinden okuyor.
        if (_pawnValid[index] && _pawnKeys[index] == key)
        {
            PawnCacheHits++;
            return _pawnScores[index];
        }

        Span<int> whiteMaxRank = stackalloc int[8];   // beyazın en ileri piyonu
        Span<int> blackMinRank = stackalloc int[8];   // siyahın en ileri piyonu
        for (int f = 0; f < 8; f++)
        {
            whiteCount[f] = 0; blackCount[f] = 0;
            whiteMaxRank[f] = -1; blackMinRank[f] = 8;
        }

        for (int sq = 0; sq < 64; sq++)
        {
            int piece = board.Squares[sq];
            if (Piece.Type(piece) != Piece.Pawn) continue;

            int file = Square.File(sq), rank = Square.Rank(sq);
            if (Piece.IsColor(piece, Piece.White))
            {
                whiteCount[file]++;
                if (rank > whiteMaxRank[file]) whiteMaxRank[file] = rank;
            }
            else
            {
                blackCount[file]++;
                if (rank < blackMinRank[file]) blackMinRank[file] = rank;
            }
        }

        int score = PawnTerms(board, whiteCount, blackCount, whiteMaxRank, blackMinRank);

        _pawnKeys[index] = key;
        _pawnValid[index] = true;
        _pawnScores[index] = score;
        for (int f = 0; f < 8; f++)
        {
            _pawnCounts[countBase + f] = (byte)whiteCount[f];
            _pawnCounts[countBase + 8 + f] = (byte)blackCount[f];
        }
        return score;
    }

    private static int PawnTerms(Board board, Span<int> whiteCount, Span<int> blackCount,
                                 Span<int> whiteMaxRank, Span<int> blackMinRank)
    {
        int score = 0;

        for (int file = 0; file < 8; file++)
        {
            // İkiz piyonlar: aynı dosyadaki her fazladan piyon.
            if (whiteCount[file] > 1) score -= (whiteCount[file] - 1) * DoubledPawnPenalty;
            if (blackCount[file] > 1) score += (blackCount[file] - 1) * DoubledPawnPenalty;

            int left = file - 1, right = file + 1;
            bool whiteNeighbour = (left >= 0 && whiteCount[left] > 0) || (right < 8 && whiteCount[right] > 0);
            bool blackNeighbour = (left >= 0 && blackCount[left] > 0) || (right < 8 && blackCount[right] > 0);

            // İzole piyon: komşu dosyalarda onu koruyabilecek piyon yok.
            if (whiteCount[file] > 0 && !whiteNeighbour) score -= IsolatedPawnPenalty * whiteCount[file];
            if (blackCount[file] > 0 && !blackNeighbour) score += IsolatedPawnPenalty * blackCount[file];
        }

        // Geçer piyonlar: önünde ve yan dosyalarda rakip piyon kalmamış olanlar.
        for (int sq = 0; sq < 64; sq++)
        {
            int piece = board.Squares[sq];
            if (Piece.Type(piece) != Piece.Pawn) continue;

            int file = Square.File(sq), rank = Square.Rank(sq);

            if (Piece.IsColor(piece, Piece.White))
            {
                bool blocked = false;
                for (int f = Math.Max(0, file - 1); f <= Math.Min(7, file + 1); f++)
                    if (blackMinRank[f] > rank && blackMinRank[f] < 8) { blocked = true; break; }
                if (!blocked) score += PassedPawnBonus[rank];
            }
            else
            {
                bool blocked = false;
                for (int f = Math.Max(0, file - 1); f <= Math.Min(7, file + 1); f++)
                    if (whiteMaxRank[f] < rank && whiteMaxRank[f] >= 0) { blocked = true; break; }
                if (!blocked) score -= PassedPawnBonus[7 - rank];
            }
        }

        return score;
    }

    /// <summary>Piyon yapısı (önbellekli) + şah güvenliği + fil çifti.
    /// Beyazın gözünden döner.</summary>
    private static int PawnsAndKing(Board board, bool endgame, int whiteBishops, int blackBishops)
    {
        int score = PawnStructure(board, out int countBase);

        if (whiteBishops >= 2) score += BishopPairBonus;
        if (blackBishops >= 2) score -= BishopPairBonus;

        // Şah güvenliği sadece oyun ortasında anlamlı; oyun sonunda şah zaten
        // öne çıkmalı ve piyon kalkanı diye bir şey kalmaz.
        if (!endgame)
        {
            score += KingShield(board, Piece.White, countBase, countBase + 8);
            score -= KingShield(board, Piece.Black, countBase + 8, countBase);
        }

        return score;
    }

    /// <summary>Şahın bulunduğu ve komşu dosyalarda kendi piyonu var mı?
    /// Yoksa o dosya rakip kale/vezir için açık bir yoldur.</summary>
    private static int KingShield(Board board, int color, int ownBase, int enemyBase)
    {
        byte[] counts = _pawnCounts!;
        int kingSquare = board.KingSquare[Piece.ColorIndex(color)];
        if (kingSquare < 0) return 0;

        int kingFile = Square.File(kingSquare);
        int penalty = 0;

        for (int f = Math.Max(0, kingFile - 1); f <= Math.Min(7, kingFile + 1); f++)
        {
            if (counts[ownBase + f] == 0)
            {
                penalty += MissingShieldPenalty;
                // Rakibin de piyonu yoksa dosya tamamen açık — daha tehlikeli.
                if (counts[enemyBase + f] == 0) penalty += OpenFileNearKingPenalty;
            }
        }

        return -penalty;
    }

    private static int Noise(ulong key)
    {
        if (NoiseCentipawns <= 0) return 0;

        // Karıştırma (splitmix64): benzer anahtarlar benzer sapma almasın.
        ulong h = key ^ ((ulong)(uint)NoiseSeed * 0x9E3779B97F4A7C15UL);
        h ^= h >> 30; h *= 0xBF58476D1CE4E5B9UL;
        h ^= h >> 27; h *= 0x94D049BB133111EBUL;
        h ^= h >> 31;

        int range = NoiseCentipawns * 2 + 1;
        return (int)(h % (ulong)range) - NoiseCentipawns;
    }

    /// <summary>
    /// Stockfish'in iç birimi ile santipiyon arasındaki dönüşüm.
    /// SF12'de bir piyonun oyun sonu değeri 208 birimdir; bizim motorda 100.
    /// Bu çevrim olmadan mat puanları ve budama eşikleri yanlış ölçekte kalır.
    /// </summary>
    private const int SfPawnValue = 208;

    public static int Evaluate(Board board)
    {
        // Ağ yüklüyse değerlendirme tamamen ona devredilir.
        var net = NnueNetwork.Shared;
        if (net != null && board.Nnue != null)
        {
            int raw = net.EvaluateAccumulated(board.Nnue.Current, board.SideToMove);
            return raw * 100 / SfPawnValue + Noise(board.ZobristKey);
        }

        int whiteNonPawn = 0, blackNonPawn = 0;
        int whiteBishops = 0, blackBishops = 0;
        for (int sq = 0; sq < 64; sq++)
        {
            int piece = board.Squares[sq];
            if (piece == Piece.None) continue;
            int type = Piece.Type(piece);
            if (type == Piece.Pawn || type == Piece.King) continue;

            bool white = Piece.IsColor(piece, Piece.White);
            if (white) whiteNonPawn += PieceValues[type];
            else blackNonPawn += PieceValues[type];

            // Fil çifti sayımı: bu döngü zaten her kareye bakıyor, bedava.
            if (type == Piece.Bishop) { if (white) whiteBishops++; else blackBishops++; }
        }
        bool endgame = whiteNonPawn <= EndgameThreshold && blackNonPawn <= EndgameThreshold;

        int score = 0; // beyazın gözünden
        for (int sq = 0; sq < 64; sq++)
        {
            int piece = board.Squares[sq];
            if (piece == Piece.None) continue;

            int type = Piece.Type(piece);
            bool isWhite = Piece.IsColor(piece, Piece.White);

            // Siyah taş için tabloyu dikeyde aynala (a1 <-> a8).
            int tableIndex = isWhite ? sq : sq ^ 56;
            int value = PieceValues[type] + TableFor(type, endgame)[tableIndex];

            score += isWhite ? value : -value;
        }

        if (UseAdvancedEval) score += PawnsAndKing(board, endgame, whiteBishops, blackBishops);

        score += Noise(board.ZobristKey);

        // Negamax için: puan daima sırası gelen tarafın gözünden.
        return board.SideToMove == Piece.White ? score : -score;
    }
}
