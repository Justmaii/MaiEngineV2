using System.Diagnostics;

namespace ChessEngine;

/// <summary>
/// Perft = belirli derinlikte üretilen legal hamle yapraklarını saymak.
/// Hamle üreticinin doğruluğunu test etmenin standart yolu: sayılar
/// bilinen referans değerlerle birebir tutmalı.
/// </summary>
public static class Perft
{
    public static long Run(Board board, int depth)
    {
        if (depth == 0) return 1;

        var moves = MoveGenerator.GenerateLegalMoves(board);
        if (depth == 1) return moves.Count;

        long nodes = 0;
        foreach (var move in moves)
        {
            var undo = board.MakeMove(move);
            nodes += Run(board, depth - 1);
            board.UnmakeMove(undo);
        }
        return nodes;
    }

    /// <summary>Kök hamle başına düğüm sayısı — hata ayıklamak için birebir karşılaştırma yaparsın.</summary>
    public static long Divide(Board board, int depth)
    {
        long total = 0;
        foreach (var move in MoveGenerator.GenerateLegalMoves(board))
        {
            var undo = board.MakeMove(move);
            long nodes = Run(board, depth - 1);
            board.UnmakeMove(undo);
            Console.WriteLine($"{move}: {nodes}");
            total += nodes;
        }
        Console.WriteLine($"Toplam: {total}");
        return total;
    }

    /// <summary>
    /// Artımlı güncellenen Zobrist anahtarı, sıfırdan hesaplananla aynı mı?
    /// MakeMove/UnmakeMove'daki bir XOR hatası burada anında yakalanır.
    /// </summary>
    public static bool VerifyZobrist(Board board, int depth)
    {
        if (board.ZobristKey != Zobrist.Compute(board))
        {
            Console.WriteLine($"  Zobrist uyuşmazlığı: {board.ToFen()}");
            return false;
        }
        if (board.PawnKey != board.ComputePawnKey())
        {
            Console.WriteLine($"  Piyon anahtarı uyuşmazlığı: {board.ToFen()}");
            return false;
        }
        if (!board.BitboardsConsistent())
        {
            Console.WriteLine($"  Bitboard uyuşmazlığı: {board.ToFen()}");
            return false;
        }
        // Ucuz yasallik testi (IsMoveLegal), pahali referansla (MakeMove +
        // IsInCheck + UnmakeMove) her hamlede ayni cevabi vermek zorunda.
        int mover = board.SideToMove;
        foreach (var candidate in MoveGenerator.GeneratePseudoLegalMoves(board))
        {
            bool fast = board.IsMoveLegal(candidate);
            var probe = board.MakeMove(candidate);
            bool reference = !board.IsInCheck(mover);
            board.UnmakeMove(probe);
            if (fast != reference)
            {
                Console.WriteLine($"  Yasallik uyusmazligi: {candidate} / {board.ToFen()}");
                return false;
            }
        }

        if (depth == 0) return true;

        foreach (var move in MoveGenerator.GenerateLegalMoves(board))
        {
            var undo = board.MakeMove(move);
            bool ok = VerifyZobrist(board, depth - 1);
            board.UnmakeMove(undo);
            if (!ok) { Console.WriteLine($"    hamle: {move}"); return false; }

            if (board.ZobristKey != undo.ZobristKey || board.PawnKey != undo.PawnKey)
            {
                Console.WriteLine($"  Geri alma sonrası anahtar bozuldu: {move}");
                return false;
            }
        }
        return true;
    }

    public record TestPosition(string Name, string Fen, long[] Expected);

    public static readonly TestPosition[] StandardTests =
    {
        new("Başlangıç", Board.StartFen, new long[] { 20, 400, 8902, 197281, 4865609 }),
        new("Kiwipete", "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1",
            new long[] { 48, 2039, 97862, 4085603 }),
        new("Pozisyon 3", "8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1",
            new long[] { 14, 191, 2812, 43238, 674624 }),
        new("Pozisyon 4", "r3k2r/Pppp1ppp/1b3nbN/nP6/BBP1P3/q4N2/Pp1P2PP/R2Q1RK1 w kq - 0 1",
            new long[] { 6, 264, 9467, 422333 }),
        new("Pozisyon 5", "rnbq1k1r/pp1Pbppp/2p5/8/2B5/8/PPP1NnPP/RNBQK2R w KQ - 1 8",
            new long[] { 44, 1486, 62379, 2103487 }),
    };

    public static bool RunAllTests()
    {
        bool allPassed = true;
        var sw = new Stopwatch();

        foreach (var test in StandardTests)
        {
            Console.WriteLine($"\n=== {test.Name} ===");
            var board = new Board(test.Fen);

            for (int depth = 1; depth <= test.Expected.Length; depth++)
            {
                sw.Restart();
                long nodes = Run(board, depth);
                sw.Stop();
                long expected = test.Expected[depth - 1];
                bool ok = nodes == expected;
                if (!ok) allPassed = false;

                Console.WriteLine($"  derinlik {depth}: {nodes,10} / beklenen {expected,10}  " +
                                  $"{(ok ? "OK" : "HATA")}  ({sw.ElapsedMilliseconds} ms)");
            }

            Console.Write("  Zobrist + piyon anahtarı + bitboard + yasallık doğrulaması (derinlik 3): ");
            Console.WriteLine(VerifyZobrist(new Board(test.Fen), 3) ? "OK" : "HATA");
        }
        return allPassed;
    }
}
