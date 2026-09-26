namespace ChessEngine;

/// <summary>
/// SEE — Static Exchange Evaluation (durağan taş alışverişi değerlendirmesi).
///
/// Bir alışın, o karede yaşanacak TÜM alışveriş bittiğinde kaç santipiyon
/// kâr/zarar bıraktığını arama yapmadan hesaplar.
///
/// Neden gerekli: quiescence şu ana kadar bütün alışlara bakıyordu —
/// vezirle korunan piyonu almak gibi açıkça kötü olanlar dahil. SEE bunları
/// aramadan eleyebilir ve sıralamayı düzeltir.
///
/// Yöntem: karedeki taşı al, sonra iki taraf sırayla EN DEĞERSİZ saldıranıyla
/// karşılık versin. Her adımda "bu alışverişe devam etmek zorunda değilim"
/// kuralı uygulanarak geriye doğru en iyi durma noktası bulunur.
/// </summary>
public static class See
{

    /// <summary>
    /// Bitboard sürümü. Aynı cevabı verir (seecompare bunu sınıyor) ama
    /// 64 karelik tahta kopyalamaz ve saldıranı yön yön aramaz.
    ///
    /// Doluluk maskesinden taş çıktıkça arkasındaki uzun menzilli taş
    /// kendiliğinden devreye girer (x-ray) — saldıran maskesi her adımda
    /// güncel doluluğa göre yeniden hesaplandığı için ek koda gerek yok.
    /// </summary>
    public static int Evaluate(Board board, Move move)
    {
        int to = move.To, from = move.From;
        int attacker = board.Squares[from];
        int side = Piece.Color(attacker);

        ulong occupied = board.Occupied;

        int capturedValue;
        if (move.Flag == MoveFlag.EnPassant)
        {
            int capturedSquare = side == Piece.White ? to - 8 : to + 8;
            capturedValue = Evaluation.PieceValues[Piece.Pawn];
            occupied &= ~(1UL << capturedSquare);
        }
        else
        {
            int victim = board.Squares[to];
            capturedValue = victim == Piece.None ? 0 : Evaluation.PieceValues[Piece.Type(victim)];
        }

        // Terfi: piyon gidip yeni taş geliyor, kazanç aradaki fark kadar artar.
        int promotionGain = 0;
        int attackerType = Piece.Type(attacker);
        if (move.Flag == MoveFlag.Promotion)
        {
            promotionGain = Evaluation.PieceValues[move.PromotionType]
                          - Evaluation.PieceValues[Piece.Pawn];
            attackerType = move.PromotionType;
        }

        Span<int> gain = stackalloc int[32];
        int depth = 0;
        gain[0] = capturedValue + promotionGain;

        // İlk saldıran kareye yerleşti: kalkış karesi boşaldı, varış dolu.
        occupied &= ~(1UL << from);
        occupied |= 1UL << to;

        int onSquareValue = Evaluation.PieceValues[attackerType];
        side = Piece.Opposite(side);

        while (true)
        {
            ulong attackers = AttackersTo(board, to, occupied) & occupied;
            int nextFrom = LeastValuable(board, attackers, side, out int nextType);
            if (nextFrom < 0) break;

            depth++;
            if (depth >= 31) break;

            // Bu tarafın kazancı: karedeki taşı alır, ama kendi taşı da
            // bir sonraki adımda alınabilir. Negamax mantığı.
            gain[depth] = onSquareValue - gain[depth - 1];

            occupied &= ~(1UL << nextFrom);

            onSquareValue = Evaluation.PieceValues[nextType];
            side = Piece.Opposite(side);
        }

        // Geriye doğru: her taraf devam etmek zorunda değil, durabilir.
        while (depth > 0)
        {
            gain[depth - 1] = -Math.Max(-gain[depth - 1], gain[depth]);
            depth--;
        }

        return gain[0];
    }

    /// <summary>Verilen doluluğa göre bu kareye saldıran BÜTÜN taşlar (iki renk).</summary>
    private static ulong AttackersTo(Board board, int square, ulong occupied)
    {
        int w = Piece.ColorIndex(Piece.White), b = Piece.ColorIndex(Piece.Black);

        // Piyon simetrik değil: kareden beyaz piyon gibi vurduğum yerler,
        // oraya vuran SİYAH piyonların bulunduğu karelerdir.
        ulong attackers =
              (Bitboards.PawnAttacks[w][square] & board.PiecesOf(Piece.Black, Piece.Pawn))
            | (Bitboards.PawnAttacks[b][square] & board.PiecesOf(Piece.White, Piece.Pawn));

        attackers |= Bitboards.KnightAttacks[square]
                     & (board.PiecesOf(Piece.White, Piece.Knight) | board.PiecesOf(Piece.Black, Piece.Knight));
        attackers |= Bitboards.KingAttacks[square]
                     & (board.PiecesOf(Piece.White, Piece.King) | board.PiecesOf(Piece.Black, Piece.King));

        ulong queens = board.PiecesOf(Piece.White, Piece.Queen) | board.PiecesOf(Piece.Black, Piece.Queen);
        attackers |= Bitboards.RookAttacks(square, occupied)
                     & (board.PiecesOf(Piece.White, Piece.Rook) | board.PiecesOf(Piece.Black, Piece.Rook) | queens);
        attackers |= Bitboards.BishopAttacks(square, occupied)
                     & (board.PiecesOf(Piece.White, Piece.Bishop) | board.PiecesOf(Piece.Black, Piece.Bishop) | queens);

        return attackers;
    }

    /// <summary>Saldıranlar arasından bu tarafın en değersiz taşı.
    /// Sıra piyon → at → fil → kale → vezir → şah.</summary>
    private static int LeastValuable(Board board, ulong attackers, int side, out int type)
    {
        ulong mine = attackers & board.ColorBB[Piece.ColorIndex(side)];

        for (int t = Piece.Pawn; t <= Piece.King; t++)
        {
            ulong set = mine & board.PiecesOf(side, t);
            if (set != 0) { type = t; return Bitboards.Lsb(set); }
        }

        type = Piece.None;
        return -1;
    }

    /// <summary>Alışveriş sonucunda kalan net kazanç (santipiyon).
    /// Negatifse alış zararlı demektir.</summary>
    public static int EvaluateSlow(Board board, Move move)
    {
        int to = move.To;
        int from = move.From;

        // Tahtanın çalışma kopyası: taşlar alışverişte tükendikçe boşaltılır.
        // Bir taş kalkınca arkasındaki uzun menzilli taş kendiliğinden
        // devreye girer (x-ray), ayrıca kod yazmaya gerek kalmaz.
        Span<int> squares = stackalloc int[64];
        for (int i = 0; i < 64; i++) squares[i] = board.Squares[i];

        int attacker = squares[from];
        int side = Piece.Color(attacker);

        int capturedValue;
        if (move.Flag == MoveFlag.EnPassant)
        {
            int capturedSquare = side == Piece.White ? to - 8 : to + 8;
            capturedValue = Evaluation.PieceValues[Piece.Pawn];
            squares[capturedSquare] = Piece.None;
        }
        else
        {
            int victim = squares[to];
            capturedValue = victim == Piece.None ? 0 : Evaluation.PieceValues[Piece.Type(victim)];
        }

        // Terfi: piyon gidip yeni taş geliyor, kazanç aradaki fark kadar artar.
        int promotionGain = 0;
        int attackerType = Piece.Type(attacker);
        if (move.Flag == MoveFlag.Promotion)
        {
            promotionGain = Evaluation.PieceValues[move.PromotionType]
                          - Evaluation.PieceValues[Piece.Pawn];
            attackerType = move.PromotionType;
        }

        Span<int> gain = stackalloc int[32];
        int depth = 0;
        gain[0] = capturedValue + promotionGain;

        squares[from] = Piece.None;
        squares[to] = attackerType | side;

        int onSquareValue = Evaluation.PieceValues[attackerType];
        side = Piece.Opposite(side);

        while (true)
        {
            int nextFrom = LeastValuableAttacker(squares, to, side);
            if (nextFrom < 0) break;

            depth++;
            if (depth >= 31) break;

            // Bu tarafın kazancı: karedeki taşı alır, ama kendi taşı da
            // bir sonraki adımda alınabilir. Negamax mantığı.
            gain[depth] = onSquareValue - gain[depth - 1];

            int nextAttacker = squares[nextFrom];
            int nextType = Piece.Type(nextAttacker);

            squares[nextFrom] = Piece.None;
            squares[to] = nextAttacker;

            onSquareValue = Evaluation.PieceValues[nextType];
            side = Piece.Opposite(side);
        }

        // Geriye doğru: her taraf devam etmek zorunda değil, durabilir.
        while (depth > 0)
        {
            gain[depth - 1] = -Math.Max(-gain[depth - 1], gain[depth]);
            depth--;
        }

        return gain[0];
    }

    /// <summary>Bu kareye saldıran en değersiz taşın karesi, yoksa -1.
    /// Sıra piyon → at → fil → kale → vezir → şah.</summary>
    private static int LeastValuableAttacker(Span<int> squares, int target, int side)
    {
        int file = Square.File(target), rank = Square.Rank(target);

        // Piyon: beyaz saldırıyorsa bir alt sıradan, siyah bir üst sıradan.
        int pawnRank = side == Piece.White ? rank - 1 : rank + 1;
        for (int df = -1; df <= 1; df += 2)
        {
            if (!Square.IsValid(file + df, pawnRank)) continue;
            int sq = Square.FromFileRank(file + df, pawnRank);
            if (squares[sq] == (Piece.Pawn | side)) return sq;
        }

        foreach (var (df, dr) in Board.KnightDirs)
        {
            if (!Square.IsValid(file + df, rank + dr)) continue;
            int sq = Square.FromFileRank(file + df, rank + dr);
            if (squares[sq] == (Piece.Knight | side)) return sq;
        }

        int found = FindSlider(squares, file, rank, Board.BishopDirs, Piece.Bishop, side);
        if (found >= 0) return found;

        found = FindSlider(squares, file, rank, Board.RookDirs, Piece.Rook, side);
        if (found >= 0) return found;

        found = FindSlider(squares, file, rank, Board.QueenDirs, Piece.Queen, side);
        if (found >= 0) return found;

        foreach (var (df, dr) in Board.QueenDirs)
        {
            if (!Square.IsValid(file + df, rank + dr)) continue;
            int sq = Square.FromFileRank(file + df, rank + dr);
            if (squares[sq] == (Piece.King | side)) return sq;
        }

        return -1;
    }

    /// <summary>Verilen yönlerde ilk rastlanan taş aranan türdense karesini döner.
    /// Araya giren taş varsa o yön kapalıdır.</summary>
    private static int FindSlider(Span<int> squares, int file, int rank,
                                  (int df, int dr)[] dirs, int pieceType, int side)
    {
        foreach (var (df, dr) in dirs)
        {
            int f = file + df, r = rank + dr;
            while (Square.IsValid(f, r))
            {
                int sq = Square.FromFileRank(f, r);
                int piece = squares[sq];
                if (piece != Piece.None)
                {
                    if (piece == (pieceType | side)) return sq;
                    break;
                }
                f += df; r += dr;
            }
        }
        return -1;
    }
}
