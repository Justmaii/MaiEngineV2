namespace ChessEngine;

public enum MoveFlag
{
    Normal,
    DoublePawnPush,
    EnPassant,
    Castle,
    Promotion
}

public readonly struct Move
{
    public readonly int From;
    public readonly int To;
    public readonly MoveFlag Flag;
    /// <summary>Terfi hamlesinde yeni taşın türü (Piece.Queen vb.), yoksa Piece.None.</summary>
    public readonly int PromotionType;

    public Move(int from, int to, MoveFlag flag = MoveFlag.Normal, int promotionType = Piece.None)
    {
        From = from;
        To = to;
        Flag = flag;
        PromotionType = promotionType;
    }

    public bool IsNull => From == To;

    /// <summary>UCI gösterimi: e2e4, e7e8q ...</summary>
    public override string ToString()
    {
        string s = Square.Name(From) + Square.Name(To);
        if (Flag == MoveFlag.Promotion)
        {
            s += PromotionType switch
            {
                Piece.Queen => "q",
                Piece.Rook => "r",
                Piece.Bishop => "b",
                Piece.Knight => "n",
                _ => ""
            };
        }
        return s;
    }
}

public static class Square
{
    public static int File(int sq) => sq & 7;
    public static int Rank(int sq) => sq >> 3;
    public static int FromFileRank(int file, int rank) => rank * 8 + file;
    public static bool IsValid(int file, int rank) => file >= 0 && file < 8 && rank >= 0 && rank < 8;

    public static string Name(int sq)
    {
        if (sq < 0 || sq > 63) return "-";
        return $"{(char)('a' + File(sq))}{Rank(sq) + 1}";
    }

    public static int FromName(string name)
    {
        if (name == "-" || name.Length < 2) return -1;
        return FromFileRank(name[0] - 'a', name[1] - '1');
    }
}

/// <summary>MakeMove'un geri alınabilmesi için saklanan bilgiler.</summary>
public readonly struct Undo
{
    public readonly Move Move;
    public readonly int CapturedPiece;
    public readonly int CastlingRights;
    public readonly int EnPassantSquare;
    public readonly int HalfmoveClock;
    public readonly ulong ZobristKey;
    public readonly ulong PawnKey;

    public Undo(Move move, int captured, int castling, int ep, int halfmove,
                ulong zobristKey, ulong pawnKey)
    {
        Move = move;
        CapturedPiece = captured;
        CastlingRights = castling;
        EnPassantSquare = ep;
        HalfmoveClock = halfmove;
        ZobristKey = zobristKey;
        PawnKey = pawnKey;
    }
}

public static class Castling
{
    public const int WhiteKingSide = 1;
    public const int WhiteQueenSide = 2;
    public const int BlackKingSide = 4;
    public const int BlackQueenSide = 8;
}
