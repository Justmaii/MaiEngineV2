using System.Text;

namespace ChessEngine;

public class Board
{
    public const string StartFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";

    /// <summary>0 = a1, 7 = h1, 56 = a8, 63 = h8</summary>
    public readonly int[] Squares = new int[64];

    public int SideToMove = Piece.White;
    public int CastlingRights;
    public int EnPassantSquare = -1;
    public int HalfmoveClock;
    public int FullmoveNumber = 1;

    /// <summary>[0] beyaz şah karesi, [1] siyah şah karesi.</summary>
    public readonly int[] KingSquare = { -1, -1 };

    /// <summary>Pozisyonun Zobrist anahtarı — her hamlede artımlı güncellenir.</summary>
    public ulong ZobristKey;

    /// <summary>Oynanan her pozisyonun anahtarı — tekrardan beraberlik tespiti için.</summary>
    public readonly List<ulong> PositionHistory = new();

    /// <summary>NNUE ilk katman toplamları. Ağ yüklüyse kurulur, yoksa null.</summary>
    public NnueAccumulator? Nnue;

    /// <summary>Yeni ağın (HalfKAv2_hm) accumulator'ı. İkisinden biri kurulur.</summary>
    public NnueBigAccumulator? NnueBig;

    /// <summary>Sadece PİYONLARIN yerini özetleyen anahtar. Piyon yapısı
    /// değerlendirmesini önbelleğe almak için: piyonlar nadiren oynandığı için
    /// bu anahtar uzun süre sabit kalır ve hesap tekrar tekrar yapılmaz.</summary>
    public ulong PawnKey;

    // ---- Yön tabloları (dosya, satır) ikilileri ----
    public static readonly (int df, int dr)[] RookDirs = { (1, 0), (-1, 0), (0, 1), (0, -1) };
    public static readonly (int df, int dr)[] BishopDirs = { (1, 1), (1, -1), (-1, 1), (-1, -1) };
    public static readonly (int df, int dr)[] QueenDirs =
        { (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1) };
    public static readonly (int df, int dr)[] KnightDirs =
        { (1, 2), (2, 1), (2, -1), (1, -2), (-1, -2), (-2, -1), (-2, 1), (-1, 2) };


    // ---- Bitboard'lar ----
    // Mailbox dizisi (Squares) kalıyor, bitboard'lar onun ikinci bir görünümü.
    // Tek yazma noktası SetSquare olduğu için ikisi asla ayrışamaz; yine de
    // perft ağacında her düğümde karşılaştırılıyor (BitboardsConsistent).

    /// <summary>[renk indeksi * 7 + taş türü] — o türden taşların maskesi.</summary>
    public readonly ulong[] PieceBB = new ulong[14];

    /// <summary>[0] beyazın bütün taşları, [1] siyahın.</summary>
    public readonly ulong[] ColorBB = new ulong[2];

    /// <summary>Üzerinde taş olan bütün kareler.</summary>
    public ulong Occupied;

    public ulong PiecesOf(int color, int type) => PieceBB[Piece.ColorIndex(color) * 7 + type];

    /// <summary>Bir karenin içeriğini değiştirir ve bitboard'ları aynı anda günceller.
    /// Tahtaya yazan HER yol buradan geçmek zorunda.</summary>
    private void SetSquare(int sq, int piece)
    {
        ulong mask = 1UL << sq;
        int old = Squares[sq];

        if (old != Piece.None)
        {
            int c = Piece.ColorIndex(Piece.Color(old));
            PieceBB[c * 7 + Piece.Type(old)] &= ~mask;
            ColorBB[c] &= ~mask;
            Occupied &= ~mask;
        }

        Squares[sq] = piece;

        if (piece != Piece.None)
        {
            int c = Piece.ColorIndex(Piece.Color(piece));
            PieceBB[c * 7 + Piece.Type(piece)] |= mask;
            ColorBB[c] |= mask;
            Occupied |= mask;
        }
    }

    /// <summary>Bitboard'ları mailbox dizisinden sıfırdan kurar.</summary>
    public void RebuildBitboards()
    {
        Array.Clear(PieceBB);
        ColorBB[0] = ColorBB[1] = 0;
        Occupied = 0;

        for (int sq = 0; sq < 64; sq++)
        {
            int piece = Squares[sq];
            if (piece == Piece.None) continue;
            ulong mask = 1UL << sq;
            int c = Piece.ColorIndex(Piece.Color(piece));
            PieceBB[c * 7 + Piece.Type(piece)] |= mask;
            ColorBB[c] |= mask;
            Occupied |= mask;
        }
    }

    /// <summary>Artımlı tutulan bitboard'lar hâlâ mailbox ile aynı şeyi mi anlatıyor?
    /// Sadece doğrulama için — perft ağacında her düğümde çağrılıyor.</summary>
    public bool BitboardsConsistent()
    {
        var pieceCopy = (ulong[])PieceBB.Clone();
        var colorCopy = (ulong[])ColorBB.Clone();
        ulong occupiedCopy = Occupied;

        RebuildBitboards();

        bool same = occupiedCopy == Occupied
                    && colorCopy[0] == ColorBB[0] && colorCopy[1] == ColorBB[1];
        for (int i = 0; i < PieceBB.Length && same; i++)
            same = pieceCopy[i] == PieceBB[i];

        return same;
    }

    public Board(string fen = StartFen) => LoadFen(fen);

    public void LoadFen(string fen)
    {
        Array.Fill(Squares, Piece.None);
        Array.Clear(PieceBB);
        ColorBB[0] = ColorBB[1] = 0;
        Occupied = 0;
        KingSquare[0] = KingSquare[1] = -1;
        CastlingRights = 0;
        EnPassantSquare = -1;
        HalfmoveClock = 0;
        FullmoveNumber = 1;

        string[] parts = fen.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        int file = 0, rank = 7;
        foreach (char c in parts[0])
        {
            if (c == '/') { file = 0; rank--; }
            else if (char.IsDigit(c)) file += c - '0';
            else
            {
                int piece = Piece.FromChar(c);
                int sq = Square.FromFileRank(file, rank);
                SetSquare(sq, piece);
                if (Piece.Type(piece) == Piece.King)
                    KingSquare[Piece.ColorIndex(Piece.Color(piece))] = sq;
                file++;
            }
        }

        SideToMove = parts.Length > 1 && parts[1] == "b" ? Piece.Black : Piece.White;

        if (parts.Length > 2 && parts[2] != "-")
        {
            foreach (char c in parts[2])
            {
                CastlingRights |= c switch
                {
                    'K' => Castling.WhiteKingSide,
                    'Q' => Castling.WhiteQueenSide,
                    'k' => Castling.BlackKingSide,
                    'q' => Castling.BlackQueenSide,
                    _ => 0
                };
            }
        }

        if (parts.Length > 3) EnPassantSquare = Square.FromName(parts[3]);
        if (parts.Length > 4 && int.TryParse(parts[4], out int hm)) HalfmoveClock = hm;
        if (parts.Length > 5 && int.TryParse(parts[5], out int fm)) FullmoveNumber = fm;

        ZobristKey = Zobrist.Compute(this);
        PawnKey = ComputePawnKey();

        if (NnueBigNetwork.Shared != null)
        {
            NnueBig ??= new NnueBigAccumulator(NnueBigNetwork.Shared);
            NnueBig.RefreshAll(this);
        }
        else if (NnueNetwork.Shared != null)
        {
            Nnue ??= new NnueAccumulator(NnueNetwork.Shared);
            Nnue.RefreshAll(this);
        }
        PositionHistory.Clear();
        PositionHistory.Add(ZobristKey);
    }

    /// <summary>Piyon anahtarını sıfırdan hesaplar (FEN yüklerken ve testte).</summary>
    public ulong ComputePawnKey()
    {
        ulong key = 0;
        for (int sq = 0; sq < 64; sq++)
        {
            int piece = Squares[sq];
            if (Piece.Type(piece) == Piece.Pawn) key ^= Zobrist.PieceKeys[piece, sq];
        }
        return key;
    }

    /// <summary>
    /// Bu pozisyon daha önce görüldü mü? Arama içinde tek tekrarı bile
    /// beraberlik saymak yaygın ve güvenli bir kısayoldur: motor kazandığı
    /// pozisyonda tekrara girmez, kaybettiğinde tekrarı hedefler.
    /// Piyon hamlesi veya alış olduğunda geçmiş sıfırlanır (geri dönüş imkânsız),
    /// bu yüzden sadece HalfmoveClock kadar geriye bakmak yeterli.
    /// </summary>
    public bool IsRepetition()
    {
        int last = PositionHistory.Count - 1;
        int limit = Math.Max(0, last - HalfmoveClock);

        // Aynı tarafın sırası olan pozisyonlar ikişer atlanarak gelir.
        for (int i = last - 2; i >= limit; i -= 2)
            if (PositionHistory[i] == ZobristKey) return true;

        return false;
    }

    public string ToFen()
    {
        var sb = new StringBuilder();
        for (int rank = 7; rank >= 0; rank--)
        {
            int empty = 0;
            for (int file = 0; file < 8; file++)
            {
                int piece = Squares[Square.FromFileRank(file, rank)];
                if (piece == Piece.None) empty++;
                else
                {
                    if (empty > 0) { sb.Append(empty); empty = 0; }
                    sb.Append(Piece.ToChar(piece));
                }
            }
            if (empty > 0) sb.Append(empty);
            if (rank > 0) sb.Append('/');
        }

        sb.Append(SideToMove == Piece.White ? " w " : " b ");

        if (CastlingRights == 0) sb.Append('-');
        else
        {
            if ((CastlingRights & Castling.WhiteKingSide) != 0) sb.Append('K');
            if ((CastlingRights & Castling.WhiteQueenSide) != 0) sb.Append('Q');
            if ((CastlingRights & Castling.BlackKingSide) != 0) sb.Append('k');
            if ((CastlingRights & Castling.BlackQueenSide) != 0) sb.Append('q');
        }

        sb.Append(' ').Append(Square.Name(EnPassantSquare));
        sb.Append(' ').Append(HalfmoveClock);
        sb.Append(' ').Append(FullmoveNumber);
        return sb.ToString();
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        for (int rank = 7; rank >= 0; rank--)
        {
            sb.Append(rank + 1).Append("  ");
            for (int file = 0; file < 8; file++)
                sb.Append(Piece.ToChar(Squares[Square.FromFileRank(file, rank)])).Append(' ');
            sb.AppendLine();
        }
        sb.AppendLine("\n   a b c d e f g h");
        sb.AppendLine(ToFen());
        return sb.ToString();
    }

    // ------------------------------------------------------------------
    //  Hamle yapma / geri alma
    // ------------------------------------------------------------------

    /// <summary>Her karenin, üzerinden hamle geçtiğinde hangi rok haklarını
    /// yok ettiğini tutar (şah veya kale kıpırdadıysa hak gider).</summary>
    private static readonly int[] CastlingMask = BuildCastlingMask();

    private static int[] BuildCastlingMask()
    {
        var mask = new int[64];
        Array.Fill(mask, 0b1111);
        mask[Square.FromName("e1")] &= ~(Castling.WhiteKingSide | Castling.WhiteQueenSide);
        mask[Square.FromName("h1")] &= ~Castling.WhiteKingSide;
        mask[Square.FromName("a1")] &= ~Castling.WhiteQueenSide;
        mask[Square.FromName("e8")] &= ~(Castling.BlackKingSide | Castling.BlackQueenSide);
        mask[Square.FromName("h8")] &= ~Castling.BlackKingSide;
        mask[Square.FromName("a8")] &= ~Castling.BlackQueenSide;
        return mask;
    }

    public Undo MakeMove(Move move)
    {
        int from = move.From, to = move.To;
        int piece = Squares[from];
        int color = Piece.Color(piece);
        int captured = Squares[to];

        var undo = new Undo(move, move.Flag == MoveFlag.EnPassant ? Piece.Pawn | Piece.Opposite(color) : captured,
                            CastlingRights, EnPassantSquare, HalfmoveClock, ZobristKey, PawnKey);

        // NNUE: bu hamlenin özellik listesinde ne değiştirdiğini topla.
        var nnueDelta = new NnueMoveDelta { MovedKingColor = Piece.None };
        bool trackNnue = Nnue != null || NnueBig != null;
        if (trackNnue)
        {
            // Şah hamlesi de kaydedilir: eski ağ şahları özellik saymadığı için
            // bu kayıtlar orada işe yaramaz ve atlanır, ama yeni ağda şahlar da
            // özellik ve ÖTEKİ bakış için bu hareketin işlenmesi gerekir.
            if (Piece.Type(piece) == Piece.King) nnueDelta.MovedKingColor = color;

            nnueDelta.Remove(from, piece);
            int landing = move.Flag == MoveFlag.Promotion ? move.PromotionType | color : piece;
            nnueDelta.Add(to, landing);

            if (move.Flag == MoveFlag.EnPassant)
                nnueDelta.Remove(color == Piece.White ? to - 8 : to + 8,
                                 Piece.Pawn | Piece.Opposite(color));
            else if (captured != Piece.None)
                nnueDelta.Remove(to, captured);
        }

        // Piyon anahtarı: sadece piyon giren/çıkan durumlarda değişir.
        bool movingPawn = Piece.Type(piece) == Piece.Pawn;
        if (movingPawn) PawnKey ^= Zobrist.PieceKeys[piece, from];
        if (captured != Piece.None && Piece.Type(captured) == Piece.Pawn)
            PawnKey ^= Zobrist.PieceKeys[captured, to];

        // --- Zobrist: önce ESKİ rok hakkı ve geçerken alma karesini anahtardan çıkar ---
        ulong key = ZobristKey;
        key ^= Zobrist.CastlingKeys[CastlingRights];
        if (EnPassantSquare >= 0) key ^= Zobrist.EnPassantFileKeys[Square.File(EnPassantSquare)];

        key ^= Zobrist.PieceKeys[piece, from]; // taş kalkış karesinden çıktı

        SetSquare(to, piece);
        SetSquare(from, Piece.None);

        switch (move.Flag)
        {
            case MoveFlag.Promotion:
                if (captured != Piece.None) key ^= Zobrist.PieceKeys[captured, to];
                SetSquare(to, move.PromotionType | color);
                key ^= Zobrist.PieceKeys[move.PromotionType | color, to];
                break;

            case MoveFlag.EnPassant:
                // Alınan piyon varış karesinde değil, onun arkasında durur.
                int capturedPawnSquare = color == Piece.White ? to - 8 : to + 8;
                PawnKey ^= Zobrist.PieceKeys[Piece.Pawn | Piece.Opposite(color), capturedPawnSquare];
                PawnKey ^= Zobrist.PieceKeys[piece, to];
                key ^= Zobrist.PieceKeys[Piece.Pawn | Piece.Opposite(color), capturedPawnSquare];
                key ^= Zobrist.PieceKeys[piece, to];
                SetSquare(capturedPawnSquare, Piece.None);
                break;

            case MoveFlag.Castle:
                (int rookFrom, int rookTo) = to switch
                {
                    6 => (7, 5),    // e1g1
                    2 => (0, 3),    // e1c1
                    62 => (63, 61), // e8g8
                    58 => (56, 59), // e8c8
                    _ => (-1, -1)
                };
                int rook = Squares[rookFrom];
                if (trackNnue) { nnueDelta.Remove(rookFrom, rook); nnueDelta.Add(rookTo, rook); }
                key ^= Zobrist.PieceKeys[piece, to];
                key ^= Zobrist.PieceKeys[rook, rookFrom];
                key ^= Zobrist.PieceKeys[rook, rookTo];
                SetSquare(rookTo, rook);
                SetSquare(rookFrom, Piece.None);
                break;

            default:
                if (captured != Piece.None) key ^= Zobrist.PieceKeys[captured, to];
                key ^= Zobrist.PieceKeys[piece, to];
                if (movingPawn) PawnKey ^= Zobrist.PieceKeys[piece, to];
                break;
        }

        if (Piece.Type(piece) == Piece.King)
            KingSquare[Piece.ColorIndex(color)] = to;

        CastlingRights &= CastlingMask[from] & CastlingMask[to];

        EnPassantSquare = move.Flag == MoveFlag.DoublePawnPush
            ? (color == Piece.White ? from + 8 : from - 8)
            : -1;

        if (Piece.Type(piece) == Piece.Pawn || captured != Piece.None) HalfmoveClock = 0;
        else HalfmoveClock++;

        if (color == Piece.Black) FullmoveNumber++;
        SideToMove = Piece.Opposite(color);

        // --- Zobrist: YENİ rok hakkı, yeni geçerken alma karesi ve sıra değişimi ---
        key ^= Zobrist.CastlingKeys[CastlingRights];
        if (EnPassantSquare >= 0) key ^= Zobrist.EnPassantFileKeys[Square.File(EnPassantSquare)];
        key ^= Zobrist.SideToMoveKey;
        ZobristKey = key;
        PositionHistory.Add(key);

        // Tahta artık yeni durumda; şah tazelemesi doğru kareyi görsün diye
        // accumulator en son güncelleniyor.
        if (trackNnue)
        {
            Nnue?.ApplyMove(this, nnueDelta);
            NnueBig?.ApplyMove(this, nnueDelta);
        }

        return undo;
    }

    public void UnmakeMove(Undo undo)
    {
        Nnue?.Pop();
        NnueBig?.Pop();
        PositionHistory.RemoveAt(PositionHistory.Count - 1);
        Move move = undo.Move;
        int from = move.From, to = move.To;

        SideToMove = Piece.Opposite(SideToMove);
        int color = SideToMove;
        if (color == Piece.Black) FullmoveNumber--;

        int piece = Squares[to];
        if (move.Flag == MoveFlag.Promotion) piece = Piece.Pawn | color;

        SetSquare(from, piece);
        SetSquare(to, Piece.None);

        switch (move.Flag)
        {
            case MoveFlag.EnPassant:
                SetSquare(color == Piece.White ? to - 8 : to + 8, undo.CapturedPiece);
                break;

            case MoveFlag.Castle:
                (int rookFrom, int rookTo) = to switch
                {
                    6 => (7, 5),
                    2 => (0, 3),
                    62 => (63, 61),
                    58 => (56, 59),
                    _ => (-1, -1)
                };
                SetSquare(rookFrom, Squares[rookTo]);
                SetSquare(rookTo, Piece.None);
                break;

            default:
                SetSquare(to, undo.CapturedPiece);
                break;
        }

        if (Piece.Type(piece) == Piece.King)
            KingSquare[Piece.ColorIndex(color)] = from;

        CastlingRights = undo.CastlingRights;
        EnPassantSquare = undo.EnPassantSquare;
        HalfmoveClock = undo.HalfmoveClock;
        ZobristKey = undo.ZobristKey; // geri alırken XOR tekrarlamak yerine saklananı kullan
        PawnKey = undo.PawnKey;
    }

    /// <summary>
    /// Boş hamle: sırayı hiç hamle yapmadan rakibe verir. Satrançta böyle bir
    /// hamle yoktur; null-move budaması için kullanılan bir arama hilesidir.
    /// </summary>
    public Undo MakeNullMove()
    {
        var undo = new Undo(default, Piece.None, CastlingRights, EnPassantSquare,
                            HalfmoveClock, ZobristKey, PawnKey);

        if (EnPassantSquare >= 0)
            ZobristKey ^= Zobrist.EnPassantFileKeys[Square.File(EnPassantSquare)];
        EnPassantSquare = -1;

        ZobristKey ^= Zobrist.SideToMoveKey;
        SideToMove = Piece.Opposite(SideToMove);
        HalfmoveClock++;
        PositionHistory.Add(ZobristKey);

        // Boş hamlede taş kıpırdamaz; yığın dengesi için yine de seviye açılır.
        var emptyDelta = new NnueMoveDelta { MovedKingColor = Piece.None };
        Nnue?.ApplyMove(this, emptyDelta);
        NnueBig?.ApplyMove(this, emptyDelta);

        return undo;
    }

    public void UnmakeNullMove(Undo undo)
    {
        Nnue?.Pop();
        NnueBig?.Pop();
        PositionHistory.RemoveAt(PositionHistory.Count - 1);
        SideToMove = Piece.Opposite(SideToMove);
        CastlingRights = undo.CastlingRights;
        EnPassantSquare = undo.EnPassantSquare;
        HalfmoveClock = undo.HalfmoveClock;
        ZobristKey = undo.ZobristKey;
    }

    /// <summary>Bu tarafın piyon ve şah dışında taşı var mı?
    /// Null-move budaması sadece varsa güvenlidir (zugzwang riski).</summary>
    public bool HasNonPawnMaterial(int color)
    {
        for (int sq = 0; sq < 64; sq++)
        {
            int piece = Squares[sq];
            if (piece == Piece.None || !Piece.IsColor(piece, color)) continue;
            int type = Piece.Type(piece);
            if (type != Piece.Pawn && type != Piece.King) return true;
        }
        return false;
    }

    // ------------------------------------------------------------------
    //  Tehdit kontrolü
    // ------------------------------------------------------------------

    /// <summary><paramref name="sq"/> karesi <paramref name="byColor"/> tarafından tehdit ediliyor mu?</summary>
    /// <summary>
    /// Bu hamle bu pozisyonda üretilebilir bir hamle mi? (Şah açma kontrolü
    /// YOK — o ayrı, IsMoveLegal'ın işi.)
    ///
    /// Neden gerekli: transposition table'dan gelen hamleyi hamle üretmeden
    /// önce denemek istiyoruz. Ama tablo çakışabilir ve başka bir pozisyonun
    /// hamlesini verebilir; onu doğrulamadan oynamak tahtayı bozar.
    /// Bu fonksiyon, hamle listesini üretmeden aynı soruyu cevaplıyor.
    /// </summary>
    public bool IsPseudoLegal(Move move)
    {
        if (move.IsNull) return false;

        int from = move.From, to = move.To;
        if ((uint)from > 63 || (uint)to > 63) return false;

        int piece = Squares[from];
        if (piece == Piece.None || Piece.Color(piece) != SideToMove) return false;

        int target = Squares[to];
        if (target != Piece.None && Piece.Color(target) == SideToMove) return false;

        int type = Piece.Type(piece);
        int color = SideToMove;
        ulong toMask = 1UL << to;

        switch (move.Flag)
        {
            case MoveFlag.Castle:
            {
                // Rok nadir ve kuralları uzun; üretip karşılaştırmak en güvenlisi.
                Span<Move> buffer = stackalloc Move[8];
                int count = 0;
                CastlingMovesFor(color, buffer, ref count);
                for (int i = 0; i < count; i++)
                    if (buffer[i].From == from && buffer[i].To == to) return true;
                return false;
            }

            case MoveFlag.EnPassant:
                return type == Piece.Pawn
                       && to == EnPassantSquare
                       && target == Piece.None
                       && (Bitboards.PawnAttacks[Piece.ColorIndex(color)][from] & toMask) != 0;

            case MoveFlag.DoublePawnPush:
            {
                if (type != Piece.Pawn) return false;
                int step = color == Piece.White ? 8 : -8;
                int startRank = color == Piece.White ? 1 : 6;
                return Square.Rank(from) == startRank
                       && to == from + 2 * step
                       && Squares[from + step] == Piece.None
                       && target == Piece.None;
            }

            case MoveFlag.Promotion:
            {
                if (type != Piece.Pawn) return false;
                int promoRank = color == Piece.White ? 7 : 0;
                if (Square.Rank(to) != promoRank) return false;
                if (move.PromotionType is not (Piece.Queen or Piece.Rook or Piece.Bishop or Piece.Knight))
                    return false;
                return PawnReaches(from, to, color, target);
            }

            default:
            {
                if (type == Piece.Pawn)
                {
                    int promoRank = color == Piece.White ? 7 : 0;
                    if (Square.Rank(to) == promoRank) return false;   // terfi bayrağı gerekirdi
                    return PawnReaches(from, to, color, target);
                }

                ulong attacks = type switch
                {
                    Piece.Knight => Bitboards.KnightAttacks[from],
                    Piece.King => Bitboards.KingAttacks[from],
                    Piece.Bishop => Bitboards.BishopAttacks(from, Occupied),
                    Piece.Rook => Bitboards.RookAttacks(from, Occupied),
                    Piece.Queen => Bitboards.QueenAttacks(from, Occupied),
                    _ => 0UL
                };
                return (attacks & toMask) != 0;
            }
        }
    }

    /// <summary>Piyon tek adım ilerledi mi, yoksa çapraz alış mı yaptı?</summary>
    private bool PawnReaches(int from, int to, int color, int target)
    {
        int step = color == Piece.White ? 8 : -8;

        if (to == from + step) return target == Piece.None;

        return target != Piece.None
               && (Bitboards.PawnAttacks[Piece.ColorIndex(color)][from] & (1UL << to)) != 0;
    }

    /// <summary>
    /// Bir pseudo-legal hamle gerçekten legal mi — yani oynayan taraf kendi şahını
    /// açıkta bırakıyor mu?
    ///
    /// Eskiden bu soru MakeMove + IsInCheck + UnmakeMove ile cevaplanıyordu. Ama
    /// MakeMove artık Zobrist anahtarını, piyon anahtarını, tekrar geçmişini ve
    /// en pahalısı NNUE accumulator'ını da güncelliyor — oysa bu testten sonra
    /// hamlelerin çoğu hiç oynanmayacak. Burada sadece kareler (ve onlara bağlı
    /// bitboard'lar) geçici olarak değiştiriliyor, soru soruluyor, geri alınıyor.
    ///
    /// Rok hamleleri üretim sırasında tam kontrol edildiği için burada ek iş yok.
    /// </summary>
    public bool IsMoveLegal(Move move)
    {
        int from = move.From, to = move.To;
        int piece = Squares[from];
        int color = Piece.Color(piece);
        int colorIndex = Piece.ColorIndex(color);
        int captured = Squares[to];

        int landing = move.Flag == MoveFlag.Promotion ? move.PromotionType | color : piece;
        SetSquare(to, landing);
        SetSquare(from, Piece.None);

        // Geçerken alışta alınan piyon varış karesinde değil, arkasında durur.
        int epSquare = -1, epPiece = Piece.None;
        if (move.Flag == MoveFlag.EnPassant)
        {
            epSquare = color == Piece.White ? to - 8 : to + 8;
            epPiece = Squares[epSquare];
            SetSquare(epSquare, Piece.None);
        }

        int savedKing = KingSquare[colorIndex];
        if (Piece.Type(piece) == Piece.King) KingSquare[colorIndex] = to;

        bool legal = !IsSquareAttacked(KingSquare[colorIndex], Piece.Opposite(color));

        KingSquare[colorIndex] = savedKing;
        SetSquare(from, piece);
        SetSquare(to, captured);
        if (epSquare >= 0) SetSquare(epSquare, epPiece);

        return legal;
    }

    /// <summary>
    /// sq karesi byColor tarafından vurulmuş mu? Aramanın en sık çağrılan
    /// fonksiyonu: her hamlenin yasallığı ve her şah kontrolü buradan geçiyor.
    ///
    /// Bitboard mantığı tersten çalışıyor: "hangi taşlar buraya vuruyor?"
    /// sorusu yerine "bu kareden o taş gibi hareket etsem nereye giderim?"
    /// diye soruyoruz. At ve şah simetrik olduğu için maske doğrudan işe yarar;
    /// piyon simetrik olmadığı için KARŞI rengin piyon saldırı maskesi alınır.
    /// </summary>
    public bool IsSquareAttacked(int sq, int byColor)
    {
        int b = Piece.ColorIndex(byColor) * 7;

        if ((Bitboards.PawnAttacks[Piece.ColorIndex(Piece.Opposite(byColor))][sq]
             & PieceBB[b + Piece.Pawn]) != 0) return true;

        if ((Bitboards.KnightAttacks[sq] & PieceBB[b + Piece.Knight]) != 0) return true;
        if ((Bitboards.KingAttacks[sq] & PieceBB[b + Piece.King]) != 0) return true;

        ulong queens = PieceBB[b + Piece.Queen];
        if ((PieceBB[b + Piece.Rook] | queens) != 0
            && (Bitboards.RookAttacks(sq, Occupied) & (PieceBB[b + Piece.Rook] | queens)) != 0)
            return true;
        if ((PieceBB[b + Piece.Bishop] | queens) != 0
            && (Bitboards.BishopAttacks(sq, Occupied) & (PieceBB[b + Piece.Bishop] | queens)) != 0)
            return true;

        return false;
    }

    /// <summary>
    /// Eski mailbox sürümü. Artık aramada kullanılmıyor; bitboard sürümünün
    /// referansı olarak duruyor — ikisi her pozisyonda ve her karede aynı
    /// cevabı vermek zorunda (attackcheck modu bunu sınıyor).
    /// </summary>
    public bool IsSquareAttackedSlow(int sq, int byColor)
    {
        int file = Square.File(sq), rank = Square.Rank(sq);

        // Piyonlar: saldıran beyazsa bir alt sıradan, siyahsa bir üst sıradan vurur.
        int pawnRank = byColor == Piece.White ? rank - 1 : rank + 1;
        foreach (int df in stackalloc int[] { -1, 1 })
        {
            if (!Square.IsValid(file + df, pawnRank)) continue;
            if (Squares[Square.FromFileRank(file + df, pawnRank)] == (Piece.Pawn | byColor)) return true;
        }

        foreach (var (df, dr) in KnightDirs)
        {
            if (!Square.IsValid(file + df, rank + dr)) continue;
            if (Squares[Square.FromFileRank(file + df, rank + dr)] == (Piece.Knight | byColor)) return true;
        }

        foreach (var (df, dr) in QueenDirs)
        {
            if (!Square.IsValid(file + df, rank + dr)) continue;
            if (Squares[Square.FromFileRank(file + df, rank + dr)] == (Piece.King | byColor)) return true;
        }

        if (AttackedBySlider(file, rank, RookDirs, Piece.Rook, byColor)) return true;
        if (AttackedBySlider(file, rank, BishopDirs, Piece.Bishop, byColor)) return true;

        return false;
    }

    private bool AttackedBySlider(int file, int rank, (int df, int dr)[] dirs, int pieceType, int byColor)
    {
        foreach (var (df, dr) in dirs)
        {
            int f = file + df, r = rank + dr;
            while (Square.IsValid(f, r))
            {
                int piece = Squares[Square.FromFileRank(f, r)];
                if (piece != Piece.None)
                {
                    if (Piece.Color(piece) == byColor)
                    {
                        int t = Piece.Type(piece);
                        if (t == pieceType || t == Piece.Queen) return true;
                    }
                    break;
                }
                f += df; r += dr;
            }
        }
        return false;
    }

    /// <summary>Rok hamlelerini üretir — IsPseudoLegal ve hamle üreteci
    /// aynı kuralı kullansın diye tek yerde.</summary>
    internal void CastlingMovesFor(int color, Span<Move> moves, ref int count)
    {
        int enemy = Piece.Opposite(color);
        int kingSq = KingSquare[Piece.ColorIndex(color)];
        if (kingSq < 0) return;

        int kingSide = color == Piece.White ? Castling.WhiteKingSide : Castling.BlackKingSide;
        int queenSide = color == Piece.White ? Castling.WhiteQueenSide : Castling.BlackQueenSide;
        int home = color == Piece.White ? 4 : 60;
        if (kingSq != home) return;
        if ((CastlingRights & (kingSide | queenSide)) == 0) return;
        if (IsSquareAttacked(kingSq, enemy)) return;

        if ((CastlingRights & kingSide) != 0
            && Squares[home + 1] == Piece.None
            && Squares[home + 2] == Piece.None
            && !IsSquareAttacked(home + 1, enemy)
            && !IsSquareAttacked(home + 2, enemy))
            moves[count++] = new Move(kingSq, home + 2, MoveFlag.Castle);

        if ((CastlingRights & queenSide) != 0
            && Squares[home - 1] == Piece.None
            && Squares[home - 2] == Piece.None
            && Squares[home - 3] == Piece.None
            && !IsSquareAttacked(home - 1, enemy)
            && !IsSquareAttacked(home - 2, enemy))
            moves[count++] = new Move(kingSq, home - 2, MoveFlag.Castle);
    }

    public bool IsInCheck(int color) =>
        IsSquareAttacked(KingSquare[Piece.ColorIndex(color)], Piece.Opposite(color));
}
