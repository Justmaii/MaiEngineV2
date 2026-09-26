namespace ChessEngine;

/// <summary>
/// Zobrist hash: her pozisyonu 64 bitlik tek bir sayıya indirger.
///
/// Fikir şu: her (taş, kare) ikilisine rastgele bir sayı atanır. Pozisyonun
/// anahtarı, tahtadaki tüm ikililerin XOR'udur. XOR'un güzelliği kendi tersi
/// olması: bir taşı kaldırmak da koymak da aynı işlem. Böylece her hamlede
/// tahtayı baştan taramak yerine anahtarı birkaç XOR ile günceller,
/// hamleyi geri alırken de aynı XOR'ları tekrarlarsın.
/// </summary>
public static class Zobrist
{
    // [taş değeri][kare] — taş değeri en fazla King|Black = 22
    public static readonly ulong[,] PieceKeys = new ulong[24, 64];
    public static readonly ulong[] CastlingKeys = new ulong[16];
    public static readonly ulong[] EnPassantFileKeys = new ulong[8];
    public static readonly ulong SideToMoveKey;

    static Zobrist()
    {
        // Sabit tohum: her çalıştırmada aynı sayılar üretilsin ki
        // hata ayıklarken sonuçlar tekrarlanabilir olsun.
        var rng = new Random(20250921);

        for (int piece = 0; piece < 24; piece++)
            for (int sq = 0; sq < 64; sq++)
                PieceKeys[piece, sq] = RandomUlong(rng);

        for (int i = 0; i < 16; i++) CastlingKeys[i] = RandomUlong(rng);
        for (int i = 0; i < 8; i++) EnPassantFileKeys[i] = RandomUlong(rng);
        SideToMoveKey = RandomUlong(rng);
    }

    private static ulong RandomUlong(Random rng)
    {
        Span<byte> buffer = stackalloc byte[8];
        rng.NextBytes(buffer);
        return BitConverter.ToUInt64(buffer);
    }

    /// <summary>Anahtarı tahtayı baştan tarayarak hesaplar.
    /// Sadece FEN yüklerken ve doğrulama testinde kullanılır.</summary>
    public static ulong Compute(Board board)
    {
        ulong key = 0;

        for (int sq = 0; sq < 64; sq++)
        {
            int piece = board.Squares[sq];
            if (piece != Piece.None) key ^= PieceKeys[piece, sq];
        }

        key ^= CastlingKeys[board.CastlingRights];

        if (board.EnPassantSquare >= 0)
            key ^= EnPassantFileKeys[Square.File(board.EnPassantSquare)];

        if (board.SideToMove == Piece.Black) key ^= SideToMoveKey;

        return key;
    }
}
