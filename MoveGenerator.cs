namespace ChessEngine;

/// <summary>
/// Hamle üretimi. İki tasarım kararı burada önemli:
///
/// 1. Hamleler çağıranın verdiği tampona (Span) yazılır, liste ayrılmaz.
///    Arama saniyede yüz binlerce düğüm geziyor; her düğümde yeni liste
///    ayırmak çöp toplayıcıyı sürekli çalıştırır ve ölçülebilir zaman yer.
///
/// 2. Sadece alışları ya da sadece sessiz hamleleri isteyebilirsin. Quiescence bütün
///    hamleleri üretip alışları ayıklamak yerine doğrudan alışları ister —
///    aramanın düğümlerinin çoğu orada geçtiği için bu tek başına kazanç.
/// </summary>
/// <summary>Hangi hamleler üretilsin?</summary>
public enum GenType
{
    /// <summary>Hepsi.</summary>
    All,
    /// <summary>Alışlar ve terfiler — quiescence'ın istediği.</summary>
    Captures,
    /// <summary>Alış olmayanlar: sessiz taş hamleleri, piyon itişleri, rok.</summary>
    Quiets
}

public static class MoveGenerator
{
    /// <summary>Bir pozisyonda mümkün olabilecek en fazla hamle sayısının
    /// güvenli üst sınırı (bilinen rekor 218).</summary>
    public const int MaxMoves = 256;

    private static readonly int[] PromotionTypes = { Piece.Queen, Piece.Rook, Piece.Bishop, Piece.Knight };

    // ------------------------------------------------------------------
    //  Tamponlu sürümler — aramanın kullandığı yol
    // ------------------------------------------------------------------

    /// <summary>Legal hamleleri tampona yazar, sayısını döner.</summary>
    public static int GenerateLegal(Board board, Span<Move> moves, GenType type = GenType.All)
    {
        // Önce aynı tampona pseudo-legal hamleleri yaz, sonra YERİNDE ele.
        // Ayrı bir ara tampon açmıyoruz: legal sayacı her zaman okunan
        // indeksin gerisinde kaldığı için üzerine yazma riski yok.
        int count = GeneratePseudoLegal(board, moves, type);

        int legal = 0;
        for (int i = 0; i < count; i++)
            if (board.IsMoveLegal(moves[i]))
                moves[legal++] = moves[i];

        return legal;
    }

    /// <summary>Şah açma kontrolü yapılmamış hamleler. Rok hamleleri burada
    /// zaten tam kontrol edilir, çünkü geçilen kareler de güvenli olmalı.</summary>
    public static int GeneratePseudoLegal(Board board, Span<Move> moves, GenType type = GenType.All)
    {
        int count = 0;
        int color = board.SideToMove;
        int colorIndex = Piece.ColorIndex(color);
        ulong own = board.ColorBB[colorIndex];
        ulong enemy = board.ColorBB[1 - colorIndex];
        ulong occupied = board.Occupied;

        // Alış isteniyorsa hedef rakip taşlar; sessiz isteniyorsa boş kareler;
        // hepsi isteniyorsa "kendi taşım olmayan her yer".
        ulong targets = type switch
        {
            GenType.Captures => enemy,
            GenType.Quiets => ~occupied,
            _ => ~own
        };

        GeneratePawnMoves(board, color, enemy, occupied, type, moves, ref count);

        // Üretim sırası taş değerine göre: at, fil, kale, vezir, şah.
        // Sebebi sıralama: eşit puanlı sessiz hamleler arasında üretim sırası
        // belirleyici oluyor, ve şah hamlelerinin ortaoyununda genelde kötü
        // olması yüzünden onları en sona bırakmak arama ağacını küçültüyor.
        ulong knights = board.PiecesOf(color, Piece.Knight);
        while (knights != 0)
        {
            int from = Bitboards.PopLsb(ref knights);
            AddMoves(from, Bitboards.KnightAttacks[from] & targets, moves, ref count);
        }

        ulong bishops = board.PiecesOf(color, Piece.Bishop);
        while (bishops != 0)
        {
            int from = Bitboards.PopLsb(ref bishops);
            AddMoves(from, Bitboards.BishopAttacks(from, occupied) & targets, moves, ref count);
        }

        ulong rooks = board.PiecesOf(color, Piece.Rook);
        while (rooks != 0)
        {
            int from = Bitboards.PopLsb(ref rooks);
            AddMoves(from, Bitboards.RookAttacks(from, occupied) & targets, moves, ref count);
        }

        ulong queens = board.PiecesOf(color, Piece.Queen);
        while (queens != 0)
        {
            int from = Bitboards.PopLsb(ref queens);
            AddMoves(from, Bitboards.QueenAttacks(from, occupied) & targets, moves, ref count);
        }

        ulong kings = board.PiecesOf(color, Piece.King);
        while (kings != 0)
        {
            int from = Bitboards.PopLsb(ref kings);
            AddMoves(from, Bitboards.KingAttacks[from] & targets, moves, ref count);
        }

        // Rok bir alış değildir, quiescence'ta üretilmez.
        if (type != GenType.Captures) board.CastlingMovesFor(color, moves, ref count);

        return count;
    }

    private static void AddMoves(int from, ulong targets, Span<Move> moves, ref int count)
    {
        while (targets != 0)
            moves[count++] = new Move(from, Bitboards.PopLsb(ref targets));
    }

    /// <summary>
    /// Piyonlar tek tek değil, hepsi birden kaydırılarak üretilir: bütün
    /// piyonları bir sıra ileri kaydır, dolu kareleri çıkar — kalan her bit
    /// bir hamle.
    ///
    /// Alış modunda bile terfiler üretilir: terfi materyal kazancıdır ve
    /// quiescence'ın görmesi gerekir, yoksa "sessiz" sandığı bir hamleyle
    /// vezir çıkar ve arama bunu kaçırır.
    /// </summary>
    private static void GeneratePawnMoves(Board board, int color, ulong enemy, ulong occupied,
                                          GenType type, Span<Move> moves, ref int count)
    {
        ulong pawns = board.PiecesOf(color, Piece.Pawn);
        if (pawns == 0) return;

        bool white = color == Piece.White;
        int push = white ? 8 : -8;
        ulong empty = ~occupied;
        const ulong NotFileA = 0xFEFEFEFEFEFEFEFEUL;
        const ulong NotFileH = 0x7F7F7F7F7F7F7F7FUL;
        ulong promoRank = white ? 0xFF00000000000000UL : 0x00000000000000FFUL;
        ulong doubleRank = white ? 0x00000000FF000000UL : 0x000000FF00000000UL;

        ulong single = (white ? pawns << 8 : pawns >> 8) & empty;

        // Terfiler "alış" sayılır: materyal kazancıdır ve quiescence'ın
        // görmesi gerekir, yoksa sessiz sandığı bir hamleyle vezir çıkar.
        if (type != GenType.Quiets)
        {
            ulong promo = single & promoRank;
            while (promo != 0)
            {
                int to = Bitboards.PopLsb(ref promo);
                AddPromotions(moves, ref count, to - push, to);
            }
        }

        if (type != GenType.Captures)
        {
            ulong quiet = single & ~promoRank;
            while (quiet != 0)
            {
                int to = Bitboards.PopLsb(ref quiet);
                moves[count++] = new Move(to - push, to);
            }

            ulong doubles = (white ? single << 8 : single >> 8) & empty & doubleRank;
            while (doubles != 0)
            {
                int to = Bitboards.PopLsb(ref doubles);
                moves[count++] = new Move(to - 2 * push, to, MoveFlag.DoublePawnPush);
            }
        }

        if (type == GenType.Quiets) return;

        // Çapraz alışlar: kaydırırken taşma olmasın diye ilgili kenar
        // dosyası maskeyle çıkarılıyor.
        ulong epMask = board.EnPassantSquare >= 0 ? 1UL << board.EnPassantSquare : 0;

        for (int side = 0; side < 2; side++)
        {
            int shift = side == 0 ? push - 1 : push + 1;
            ulong source = side == 0 ? pawns & NotFileA : pawns & NotFileH;
            ulong attacks = shift > 0 ? source << shift : source >> -shift;

            ulong captures = attacks & enemy;

            ulong capturePromo = captures & promoRank;
            while (capturePromo != 0)
            {
                int to = Bitboards.PopLsb(ref capturePromo);
                AddPromotions(moves, ref count, to - shift, to);
            }

            ulong plain = captures & ~promoRank;
            while (plain != 0)
            {
                int to = Bitboards.PopLsb(ref plain);
                moves[count++] = new Move(to - shift, to);
            }

            ulong ep = attacks & epMask;
            while (ep != 0)
            {
                int to = Bitboards.PopLsb(ref ep);
                moves[count++] = new Move(to - shift, to, MoveFlag.EnPassant);
            }
        }
    }

    private static void AddPromotions(Span<Move> moves, ref int count, int from, int to)
    {
        foreach (int type in PromotionTypes)
            moves[count++] = new Move(from, to, MoveFlag.Promotion, type);
    }

    // ------------------------------------------------------------------
    //  Liste döndüren sürümler — arayüz, testler, perft
    //  (Arama bunları KULLANMAZ; sıcak yolda tahsisat istemiyoruz.)
    // ------------------------------------------------------------------

    public static List<Move> GenerateLegalMoves(Board board)
    {
        Span<Move> buffer = stackalloc Move[MaxMoves];
        int count = GenerateLegal(board, buffer);

        var list = new List<Move>(count);
        for (int i = 0; i < count; i++) list.Add(buffer[i]);
        return list;
    }

    public static List<Move> GeneratePseudoLegalMoves(Board board)
    {
        Span<Move> buffer = stackalloc Move[MaxMoves];
        int count = GeneratePseudoLegal(board, buffer);

        var list = new List<Move>(count);
        for (int i = 0; i < count; i++) list.Add(buffer[i]);
        return list;
    }
}
