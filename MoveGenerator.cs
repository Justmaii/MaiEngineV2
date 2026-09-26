namespace ChessEngine;

public static class MoveGenerator
{
    private static readonly int[] PromotionTypes = { Piece.Queen, Piece.Rook, Piece.Bishop, Piece.Knight };

    /// <summary>Sırası gelen taraf için TÜM legal hamleleri üretir.</summary>
    public static List<Move> GenerateLegalMoves(Board board)
    {
        var pseudo = GeneratePseudoLegalMoves(board);
        var legal = new List<Move>(pseudo.Count);

        foreach (var move in pseudo)
            if (board.IsMoveLegal(move)) legal.Add(move);

        return legal;
    }

    /// <summary>Şah açma (pin) kontrolü yapılmamış hamleler. Rok hamleleri burada
    /// zaten tam kontrol edilir, çünkü geçilen kareler de güvenli olmalı.</summary>
    public static List<Move> GeneratePseudoLegalMoves(Board board)
    {
        var moves = new List<Move>(64);
        int color = board.SideToMove;

        for (int sq = 0; sq < 64; sq++)
        {
            int piece = board.Squares[sq];
            if (piece == Piece.None || !Piece.IsColor(piece, color)) continue;

            switch (Piece.Type(piece))
            {
                case Piece.Pawn: GeneratePawnMoves(board, sq, color, moves); break;
                case Piece.Knight: GenerateStepMoves(board, sq, color, Board.KnightDirs, moves); break;
                case Piece.King: GenerateStepMoves(board, sq, color, Board.QueenDirs, moves); break;
                case Piece.Bishop: GenerateSlidingMoves(board, sq, color, Board.BishopDirs, moves); break;
                case Piece.Rook: GenerateSlidingMoves(board, sq, color, Board.RookDirs, moves); break;
                case Piece.Queen: GenerateSlidingMoves(board, sq, color, Board.QueenDirs, moves); break;
            }
        }

        GenerateCastlingMoves(board, color, moves);
        return moves;
    }

    private static void GenerateStepMoves(Board board, int from, int color, (int df, int dr)[] dirs, List<Move> moves)
    {
        int file = Square.File(from), rank = Square.Rank(from);
        foreach (var (df, dr) in dirs)
        {
            int f = file + df, r = rank + dr;
            if (!Square.IsValid(f, r)) continue;
            int to = Square.FromFileRank(f, r);
            int target = board.Squares[to];
            if (target == Piece.None || !Piece.IsColor(target, color))
                moves.Add(new Move(from, to));
        }
    }

    private static void GenerateSlidingMoves(Board board, int from, int color, (int df, int dr)[] dirs, List<Move> moves)
    {
        int file = Square.File(from), rank = Square.Rank(from);
        foreach (var (df, dr) in dirs)
        {
            int f = file + df, r = rank + dr;
            while (Square.IsValid(f, r))
            {
                int to = Square.FromFileRank(f, r);
                int target = board.Squares[to];
                if (target == Piece.None) moves.Add(new Move(from, to));
                else
                {
                    if (!Piece.IsColor(target, color)) moves.Add(new Move(from, to));
                    break;
                }
                f += df; r += dr;
            }
        }
    }

    private static void GeneratePawnMoves(Board board, int from, int color, List<Move> moves)
    {
        int dir = color == Piece.White ? 1 : -1;
        int startRank = color == Piece.White ? 1 : 6;
        int promoRank = color == Piece.White ? 7 : 0;
        int file = Square.File(from), rank = Square.Rank(from);

        // Tek kare ileri
        int r1 = rank + dir;
        if (Square.IsValid(file, r1))
        {
            int to = Square.FromFileRank(file, r1);
            if (board.Squares[to] == Piece.None)
            {
                AddPawnMove(moves, from, to, r1 == promoRank, MoveFlag.Normal);

                // İki kare ileri (sadece başlangıç sırasından ve önü boşsa)
                if (rank == startRank)
                {
                    int to2 = Square.FromFileRank(file, rank + 2 * dir);
                    if (board.Squares[to2] == Piece.None)
                        moves.Add(new Move(from, to2, MoveFlag.DoublePawnPush));
                }
            }
        }

        // Çapraz alışlar
        foreach (int df in stackalloc int[] { -1, 1 })
        {
            int f = file + df;
            if (!Square.IsValid(f, r1)) continue;
            int to = Square.FromFileRank(f, r1);
            int target = board.Squares[to];

            if (target != Piece.None && !Piece.IsColor(target, color))
                AddPawnMove(moves, from, to, r1 == promoRank, MoveFlag.Normal);
            else if (to == board.EnPassantSquare)
                moves.Add(new Move(from, to, MoveFlag.EnPassant));
        }
    }

    private static void AddPawnMove(List<Move> moves, int from, int to, bool isPromotion, MoveFlag flag)
    {
        if (isPromotion)
            foreach (int type in PromotionTypes)
                moves.Add(new Move(from, to, MoveFlag.Promotion, type));
        else
            moves.Add(new Move(from, to, flag));
    }

    private static void GenerateCastlingMoves(Board board, int color, List<Move> moves)
    {
        int enemy = Piece.Opposite(color);
        int kingSq = board.KingSquare[Piece.ColorIndex(color)];
        if (kingSq < 0) return;

        int kingSide = color == Piece.White ? Castling.WhiteKingSide : Castling.BlackKingSide;
        int queenSide = color == Piece.White ? Castling.WhiteQueenSide : Castling.BlackQueenSide;
        int home = color == Piece.White ? 4 : 60; // e1 / e8
        if (kingSq != home) return;

        // Şahtayken rok yapılamaz.
        if (board.IsSquareAttacked(kingSq, enemy)) return;

        if ((board.CastlingRights & kingSide) != 0
            && board.Squares[home + 1] == Piece.None
            && board.Squares[home + 2] == Piece.None
            && !board.IsSquareAttacked(home + 1, enemy)
            && !board.IsSquareAttacked(home + 2, enemy))
        {
            moves.Add(new Move(kingSq, home + 2, MoveFlag.Castle));
        }

        if ((board.CastlingRights & queenSide) != 0
            && board.Squares[home - 1] == Piece.None
            && board.Squares[home - 2] == Piece.None
            && board.Squares[home - 3] == Piece.None
            && !board.IsSquareAttacked(home - 1, enemy)
            && !board.IsSquareAttacked(home - 2, enemy))
        {
            moves.Add(new Move(kingSq, home - 2, MoveFlag.Castle));
        }
    }
}
