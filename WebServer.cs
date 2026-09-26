using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;

namespace ChessEngine;

/// <summary>
/// Tarayıcı arayüzü için küçük bir HTTP sunucusu.
/// Tahta durumu istemcide FEN olarak tutulur; sunucu durumsuzdur (stateless),
/// yani her istek kendi FEN'ini taşır. Böylece sekmeyi yenilemek,
/// aynı anda iki oyun açmak gibi şeyler bedavaya gelir.
/// </summary>
public static class WebServer
{
    public record LegalRequest(string Fen);
    public record PlayRequest(string Fen, string Move);
    public record ThinkRequest(string Fen, int MoveTimeMs);
    public record AnalyseRequest(string Fen, int MoveTimeMs);

    // Stockfish tek sefer başlatılıp açık tutulur; her istekte yeniden
    // başlatmak saniyeler alırdı. Tek iş parçacığı kullandığı için
    // istekler kilitle sıraya alınıyor.
    private static ExternalEngine? _stockfish;
    private static bool _stockfishTried;
    private static readonly object _stockfishLock = new();

    private static ExternalEngine? GetStockfish()
    {
        lock (_stockfishLock)
        {
            if (_stockfishTried) return _stockfish;
            _stockfishTried = true;

            string? path = ExternalEngine.FindStockfish();
            if (path == null) return null;

            try
            {
                _stockfish = new ExternalEngine(path);
                _stockfish.SetOption("Threads", "1");
                _stockfish.SetOption("Hash", "64");
                _stockfish.IsReady();
                Console.WriteLine($"Stockfish bulundu: {path}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Stockfish baslatilamadi: {ex.Message}");
                _stockfish = null;
            }
            return _stockfish;
        }
    }

    public static void Run(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://localhost:5050");

        var app = builder.Build();
        app.UseDefaultFiles();
        app.UseStaticFiles();

        // Bu pozisyondaki legal hamleler + oyunun durumu
        app.MapPost("/api/legal", (LegalRequest request) =>
        {
            var board = new Board(request.Fen);
            var moves = MoveGenerator.GenerateLegalMoves(board);
            return Results.Json(new
            {
                moves = moves.Select(m => m.ToString()).ToArray(),
                status = GetStatus(board, moves.Count),
                check = board.IsInCheck(board.SideToMove),
                turn = board.SideToMove == Piece.White ? "w" : "b"
            });
        });

        // Oyuncunun hamlesini uygula
        app.MapPost("/api/play", (PlayRequest request) =>
        {
            var board = new Board(request.Fen);
            var move = Uci.ParseMove(board, request.Move);
            if (move.IsNull) return Results.BadRequest(new { error = "Geçersiz hamle" });

            board.MakeMove(move);
            var next = MoveGenerator.GenerateLegalMoves(board);
            return Results.Json(new
            {
                fen = board.ToFen(),
                status = GetStatus(board, next.Count),
                check = board.IsInCheck(board.SideToMove)
            });
        });

        // Motorun hamlesi
        app.MapPost("/api/think", (ThinkRequest request) =>
        {
            var board = new Board(request.Fen);
            var search = new Search();
            var move = search.FindBestMove(board, maxDepth: 30,
                                           timeLimitMs: Math.Clamp(request.MoveTimeMs, 100, 30000),
                                           verbose: false);
            if (move.IsNull) return Results.Json(new { move = (string?)null });

            board.MakeMove(move);
            var next = MoveGenerator.GenerateLegalMoves(board);

            // Puan motorun (hamleyi yapan tarafın) gözünden gelir;
            // arayüzde satrançta âdet olduğu gibi hep beyazın gözünden gösteriyoruz.
            int whiteScore = board.SideToMove == Piece.White ? -search.LastScore : search.LastScore;

            return Results.Json(new
            {
                move = move.ToString(),
                fen = board.ToFen(),
                score = whiteScore,
                scoreText = Search.FormatScore(whiteScore),
                depth = search.DepthReached,
                nodes = search.NodesSearched,
                status = GetStatus(board, next.Count),
                check = board.IsInCheck(board.SideToMove)
            });
        });

        // Antrenman modu: pozisyonu Stockfish'e analiz ettirir.
        app.MapPost("/api/analyse", (AnalyseRequest request) =>
        {
            var engine = GetStockfish();
            if (engine == null)
                return Results.Json(new { available = false, reason = "Stockfish bulunamadi" });

            ExternalEngine.Analysis? result;
            lock (_stockfishLock)
                result = engine.Analyse(request.Fen, Math.Clamp(request.MoveTimeMs, 50, 5000));

            if (result == null) return Results.Json(new { available = true, over = true });

            var board = new Board(request.Fen);
            return Results.Json(new
            {
                available = true,
                over = false,
                bestMove = result.BestMove,
                // Puan sirasi gelen tarafin gozunden gelir; beyazin gozune cevir.
                scoreWhite = board.SideToMove == Piece.White ? result.ScoreCp : -result.ScoreCp,
                mateIn = result.MateIn,
                depth = result.Depth,
                pv = result.Pv,
            });
        });

        Console.WriteLine("Tahta hazır: http://localhost:5050");
        app.Run();
    }

    private static string GetStatus(Board board, int legalMoveCount)
    {
        if (legalMoveCount == 0)
            return board.IsInCheck(board.SideToMove) ? "mate" : "stalemate";
        if (board.HalfmoveClock >= 100) return "fifty";
        // Not: sunucu durumsuz olduğu için tekrar geçmişi burada yok —
        // tekrardan beraberlik sadece arama içinde tespit ediliyor.
        return "ongoing";
    }
}
