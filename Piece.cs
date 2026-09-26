namespace ChessEngine;

/// <summary>
/// Taşlar tek bir int içinde saklanır: tür (alt 3 bit) | renk bayrağı.
/// Örn: beyaz at = Knight | White = 2 | 8 = 10
/// </summary>
public static class Piece
{
    public const int None = 0;
    public const int Pawn = 1;
    public const int Knight = 2;
    public const int Bishop = 3;
    public const int Rook = 4;
    public const int Queen = 5;
    public const int King = 6;

    public const int White = 8;
    public const int Black = 16;

    public const int TypeMask = 0b0111;
    public const int ColorMask = 0b11000;

    public static int Type(int piece) => piece & TypeMask;
    public static int Color(int piece) => piece & ColorMask;
    public static bool IsColor(int piece, int color) => (piece & ColorMask) == color;
    public static int Opposite(int color) => color == White ? Black : White;

    /// <summary>Beyaz için 0, siyah için 1. Dizi indeksi olarak kullanılır.</summary>
    public static int ColorIndex(int color) => color == White ? 0 : 1;

    public static bool IsSliding(int piece)
    {
        int t = Type(piece);
        return t == Bishop || t == Rook || t == Queen;
    }

    public static char ToChar(int piece)
    {
        char c = Type(piece) switch
        {
            Pawn => 'p',
            Knight => 'n',
            Bishop => 'b',
            Rook => 'r',
            Queen => 'q',
            King => 'k',
            _ => '.'
        };
        return IsColor(piece, White) ? char.ToUpper(c) : c;
    }

    public static int FromChar(char c)
    {
        int color = char.IsUpper(c) ? White : Black;
        int type = char.ToLower(c) switch
        {
            'p' => Pawn,
            'n' => Knight,
            'b' => Bishop,
            'r' => Rook,
            'q' => Queen,
            'k' => King,
            _ => None
        };
        return type == None ? None : type | color;
    }
}
