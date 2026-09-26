namespace ChessEngine;

/// <summary>
/// Polyglot (.bin) açılış kitabı okuyucusu.
///
/// Dosya, anahtara göre SIRALI 16 baytlık kayıtlardan oluşur:
///   8 bayt anahtar, 2 bayt hamle, 2 bayt ağırlık, 4 bayt öğrenme (kullanılmıyor).
/// Hepsi big-endian. Sıralı olduğu için dosyayı belleğe almadan ikili arama
/// yapabiliyoruz — 11 milyon kayıtlık bir kitapta ~24 okuma yetiyor.
///
/// Polyglot'un ürettiği dosyalarda başta 96 baytlık bir başlık bloğu olabilir
/// (anahtarı sıfır olan kayıtlar); ikili arama onları doğal olarak atlar.
/// </summary>
public sealed class PolyglotBook : IDisposable
{
    private const int EntrySize = 16;

    private readonly FileStream? _stream;
    private readonly long _entryCount;

    public bool IsOpen => _stream != null;
    public long EntryCount => _entryCount;
    public string? Path { get; }

    public PolyglotBook(string path)
    {
        Path = path;
        if (!File.Exists(path)) return;

        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        _entryCount = _stream.Length / EntrySize;
    }

    /// <summary>Motorun her yerden ulastigi ortak kitap. Yoksa null kalir
    /// ve motor kendi gomulu kitabina duser.</summary>
    public static PolyglotBook? Shared { get; private set; }

    /// <summary>
    /// Kitabi acar. Yol verilmezse once calisma klasorunde, sonra calistirilabilir
    /// dosyanin yaninda arar — "dotnet run" ile bin/Release'den calistirmak
    /// arasindaki fark yuzunden ikisine de bakmak gerekiyor.
    /// </summary>
    public static bool OpenDefault(string? explicitPath = null)
    {
        Shared?.Dispose();
        Shared = null;

        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(explicitPath)) candidates.Add(explicitPath);
        else
        {
            candidates.Add(System.IO.Path.Combine("Book", "book.bin"));
            candidates.Add("book.bin");
            string exeDir = AppContext.BaseDirectory;
            candidates.Add(System.IO.Path.Combine(exeDir, "Book", "book.bin"));
            candidates.Add(System.IO.Path.Combine(exeDir, "..", "..", "..", "Book", "book.bin"));
        }

        foreach (string candidate in candidates)
        {
            var book = new PolyglotBook(candidate);
            if (book.IsOpen) { Shared = book; return true; }
            book.Dispose();
        }
        return false;
    }

    private readonly record struct Entry(ulong Key, ushort Move, ushort Weight);

    private Entry ReadEntry(long index)
    {
        Span<byte> buffer = stackalloc byte[EntrySize];
        _stream!.Seek(index * EntrySize, SeekOrigin.Begin);
        _stream.ReadExactly(buffer);

        ulong key = 0;
        for (int i = 0; i < 8; i++) key = (key << 8) | buffer[i];

        ushort move = (ushort)((buffer[8] << 8) | buffer[9]);
        ushort weight = (ushort)((buffer[10] << 8) | buffer[11]);
        return new Entry(key, move, weight);
    }

    /// <summary>Bu pozisyon için kitapta hamle varsa ağırlığa göre birini seçer.</summary>
    public Move Probe(Board board, Random random)
    {
        if (_stream == null || _entryCount == 0) return default;

        ulong key = ComputeKey(board);

        // Anahtarın ilk göründüğü kaydı ikili aramayla bul.
        long low = 0, high = _entryCount - 1, first = -1;
        while (low <= high)
        {
            long mid = (low + high) / 2;
            var entry = ReadEntry(mid);

            if (entry.Key < key) low = mid + 1;
            else
            {
                if (entry.Key == key) first = mid;
                high = mid - 1;
            }
        }
        if (first < 0) return default;

        // Aynı anahtarlı tüm kayıtlar peş peşedir; hepsini topla.
        var candidates = new List<(Move Move, int Weight)>();
        int totalWeight = 0;

        for (long i = first; i < _entryCount; i++)
        {
            var entry = ReadEntry(i);
            if (entry.Key != key) break;

            var move = DecodeMove(board, entry.Move);
            if (move.IsNull) continue;          // legal değilse atla

            int weight = Math.Max(1, (int)entry.Weight);
            candidates.Add((move, weight));
            totalWeight += weight;
        }

        if (candidates.Count == 0) return default;

        int pick = random.Next(totalWeight);
        foreach (var (move, weight) in candidates)
        {
            pick -= weight;
            if (pick < 0) return move;
        }
        return candidates[0].Move;
    }

    /// <summary>
    /// Polyglot hamle kodunu bizim Move yapımıza çevirir ve legal olduğunu
    /// doğrular. Kodlama: bit 0-2 varış dosyası, 3-5 varış sırası,
    /// 6-8 kalkış dosyası, 9-11 kalkış sırası, 12-14 terfi taşı.
    ///
    /// Rok, "şah kendi kalesini alır" diye kodlanır (e1h1 gibi) — bizim
    /// gösterimimizde e1g1 olduğu için çevirmek gerekiyor.
    /// </summary>
    private static Move DecodeMove(Board board, ushort encoded)
    {
        int toFile = encoded & 7;
        int toRank = (encoded >> 3) & 7;
        int fromFile = (encoded >> 6) & 7;
        int fromRank = (encoded >> 9) & 7;
        int promotion = (encoded >> 12) & 7;

        int from = Square.FromFileRank(fromFile, fromRank);
        int to = Square.FromFileRank(toFile, toRank);

        // Rok çevirisi: şahın kendi kalesine gitmesi olarak gelir.
        if (Piece.Type(board.Squares[from]) == Piece.King)
        {
            if (from == 4 && to == 7) to = 6;         // e1h1 -> e1g1
            else if (from == 4 && to == 0) to = 2;    // e1a1 -> e1c1
            else if (from == 60 && to == 63) to = 62; // e8h8 -> e8g8
            else if (from == 60 && to == 56) to = 58; // e8a8 -> e8c8
        }

        int promotionType = promotion switch
        {
            1 => Piece.Knight,
            2 => Piece.Bishop,
            3 => Piece.Rook,
            4 => Piece.Queen,
            _ => Piece.None
        };

        // Legal hamleler arasında eşleştir: bayraklar (rok, geçerken alma,
        // çift adım) böylece kendiliğinden doğru gelir.
        foreach (var move in MoveGenerator.GenerateLegalMoves(board))
        {
            if (move.From != from || move.To != to) continue;
            if (promotionType != Piece.None && move.PromotionType != promotionType) continue;
            if (promotionType == Piece.None && move.Flag == MoveFlag.Promotion) continue;
            return move;
        }
        return default;
    }

    /// <summary>
    /// Pozisyonun Polyglot anahtarı. Bizim Zobrist.Compute'tan ayrı tutuluyor
    /// çünkü sabit tablosu ve kuralları farklı.
    /// </summary>
    public static ulong ComputeKey(Board board)
    {
        ulong key = 0;
        var random = PolyglotRandom.Values;

        for (int sq = 0; sq < 64; sq++)
        {
            int piece = board.Squares[sq];
            if (piece == Piece.None) continue;

            // Polyglot taş sırası: siyah piyon 0, beyaz piyon 1, siyah at 2, ...
            int kind = Piece.Type(piece) switch
            {
                Piece.Pawn => 0,
                Piece.Knight => 2,
                Piece.Bishop => 4,
                Piece.Rook => 6,
                Piece.Queen => 8,
                Piece.King => 10,
                _ => 0
            };
            if (Piece.IsColor(piece, Piece.White)) kind++;

            key ^= random[PolyglotRandom.PieceOffset + 64 * kind + sq];
        }

        if ((board.CastlingRights & Castling.WhiteKingSide) != 0) key ^= random[PolyglotRandom.CastleOffset + 0];
        if ((board.CastlingRights & Castling.WhiteQueenSide) != 0) key ^= random[PolyglotRandom.CastleOffset + 1];
        if ((board.CastlingRights & Castling.BlackKingSide) != 0) key ^= random[PolyglotRandom.CastleOffset + 2];
        if ((board.CastlingRights & Castling.BlackQueenSide) != 0) key ^= random[PolyglotRandom.CastleOffset + 3];

        // Önemli fark: Polyglot geçerken alma karesini ancak GERÇEKTEN alabilecek
        // bir piyon varsa anahtara katar. Biz kendi Zobrist'imizde her zaman
        // katıyoruz; burada aynısını yaparsak anahtarlar tutmaz.
        int ep = board.EnPassantSquare;
        if (ep >= 0)
        {
            int epFile = Square.File(ep);
            int captureRank = board.SideToMove == Piece.White ? 4 : 3;
            int ownPawn = Piece.Pawn | board.SideToMove;

            bool canCapture = false;
            foreach (int df in stackalloc int[] { -1, 1 })
            {
                int f = epFile + df;
                if (f < 0 || f > 7) continue;
                if (board.Squares[Square.FromFileRank(f, captureRank)] == ownPawn) { canCapture = true; break; }
            }

            if (canCapture) key ^= random[PolyglotRandom.EnPassantOffset + epFile];
        }

        if (board.SideToMove == Piece.White) key ^= random[PolyglotRandom.TurnOffset];

        return key;
    }

    public void Dispose() => _stream?.Dispose();
}
