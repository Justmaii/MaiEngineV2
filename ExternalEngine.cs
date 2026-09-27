using System.Diagnostics;

namespace ChessEngine;

/// <summary>
/// Dışarıdaki bir UCI motorunu alt süreç olarak çalıştırıp konuşur.
/// Kendi motorumuzu Stockfish'e veya arkadaşının motoruna karşı
/// oynatabilmek için gerekli olan taraf budur.
/// </summary>
public sealed class ExternalEngine : IDisposable
{
    private readonly Process _process;
    public string Name { get; private set; }

    public ExternalEngine(string path, string arguments = "")
    {
        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = path,
                Arguments = arguments,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        };
        _process.Start();
        Name = Path.GetFileNameWithoutExtension(path);

        Send("uci");
        // "uciok" gelene kadar oku; arada motorun adı da geçer.
        while (true)
        {
            string? line = _process.StandardOutput.ReadLine();
            if (line == null) throw new IOException($"Motor cevap vermedi: {path}");
            if (line.StartsWith("id name ")) Name = line[8..].Trim();
            if (line.Trim() == "uciok") break;
        }
    }

    private void Send(string command)
    {
        _process.StandardInput.WriteLine(command);
        _process.StandardInput.Flush();
    }

    public void SetOption(string name, string value) =>
        Send($"setoption name {name} value {value}");

    public void IsReady()
    {
        Send("isready");
        while (true)
        {
            string? line = _process.StandardOutput.ReadLine();
            if (line == null || line.Trim() == "readyok") break;
        }
    }

    public void NewGame() { Send("ucinewgame"); IsReady(); }

    /// <returns>UCI hamlesi, veya oynayacak hamle yoksa null.</returns>
    public string? Think(List<string> movesSoFar, int moveTimeMs, string? startFen = null)
    {
        string start = startFen == null ? "position startpos" : $"position fen {startFen}";
        Send(start + (movesSoFar.Count > 0 ? " moves " + string.Join(' ', movesSoFar) : ""));
        Send($"go movetime {moveTimeMs}");

        while (true)
        {
            string? line = _process.StandardOutput.ReadLine();
            if (line == null) return null;
            if (!line.StartsWith("bestmove")) continue;

            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string move = parts.Length > 1 ? parts[1] : "0000";
            return move == "0000" || move == "(none)" ? null : move;
        }
    }

    /// <summary>Saatli düşünme: motor payını kendi ayırır (turnuvadaki gibi).</summary>
    public string? ThinkWithClock(List<string> movesSoFar, int wtime, int btime, int winc, int binc)
    {
        Send("position startpos" + (movesSoFar.Count > 0 ? " moves " + string.Join(' ', movesSoFar) : ""));
        Send($"go wtime {wtime} btime {btime} winc {winc} binc {binc}");

        while (true)
        {
            string? line = _process.StandardOutput.ReadLine();
            if (line == null) return null;
            if (!line.StartsWith("bestmove")) continue;

            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string move = parts.Length > 1 ? parts[1] : "0000";
            return move == "0000" || move == "(none)" ? null : move;
        }
    }

    public record Analysis(string BestMove, int ScoreCp, int MateIn, int Depth, string Pv);

    /// <summary>
    /// Bir FEN pozisyonunu analiz eder ve motorun puanını da döndürür.
    /// Puan MOTORUN gözünden, yani sırası gelen tarafın lehine pozitif.
    /// </summary>
    public Analysis? Analyse(string fen, int moveTimeMs)
    {
        Send($"position fen {fen}");
        Send($"go movetime {moveTimeMs}");

        int scoreCp = 0, mateIn = 0, depth = 0;
        string pv = "";

        while (true)
        {
            string? line = _process.StandardOutput.ReadLine();
            if (line == null) return null;

            if (line.StartsWith("info ") && line.Contains(" score "))
            {
                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                // Çok değişkenli çıktıda sadece ana varyantı (multipv 1) al.
                int multipv = IndexAfter(parts, "multipv");
                if (multipv > 0 && parts[multipv] != "1") continue;

                int d = IndexAfter(parts, "depth");
                if (d > 0 && int.TryParse(parts[d], out int parsedDepth)) depth = parsedDepth;

                int scoreIndex = Array.IndexOf(parts, "score");
                if (scoreIndex >= 0 && scoreIndex + 2 < parts.Length)
                {
                    if (parts[scoreIndex + 1] == "cp" && int.TryParse(parts[scoreIndex + 2], out int cp))
                    { scoreCp = cp; mateIn = 0; }
                    else if (parts[scoreIndex + 1] == "mate" && int.TryParse(parts[scoreIndex + 2], out int mate))
                    { mateIn = mate; scoreCp = mate > 0 ? 100000 - mate : -100000 - mate; }
                }

                int pvIndex = Array.IndexOf(parts, "pv");
                if (pvIndex >= 0) pv = string.Join(' ', parts[(pvIndex + 1)..]);
                continue;
            }

            if (!line.StartsWith("bestmove")) continue;

            string[] best = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string move = best.Length > 1 ? best[1] : "0000";
            if (move is "0000" or "(none)") return null;

            return new Analysis(move, scoreCp, mateIn, depth, pv);
        }
    }

    private static int IndexAfter(string[] parts, string token)
    {
        int i = Array.IndexOf(parts, token);
        return i >= 0 && i + 1 < parts.Length ? i + 1 : -1;
    }

    /// <summary>Yaygın kurulum yerlerinde Stockfish arar.</summary>
    public static string? FindStockfish()
    {
        string[] candidates =
        {
            "/opt/homebrew/bin/stockfish",   // Apple Silicon Homebrew
            "/usr/local/bin/stockfish",      // Intel Homebrew
            "/usr/games/stockfish",          // Debian/Ubuntu
            "/usr/bin/stockfish",
        };

        foreach (string path in candidates)
            if (File.Exists(path)) return path;

        // PATH üzerinde de bak.
        string? env = Environment.GetEnvironmentVariable("PATH");
        if (env != null)
        {
            foreach (string dir in env.Split(System.IO.Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                string candidate = System.IO.Path.Combine(dir, "stockfish");
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    public void Dispose()
    {
        try
        {
            Send("quit");
            if (!_process.WaitForExit(2000)) _process.Kill(true);
        }
        catch { /* süreç zaten kapanmış olabilir */ }
        _process.Dispose();
    }
}
