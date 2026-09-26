namespace ChessEngine;

public enum NodeType : byte
{
    /// <summary>Puan kesin: alpha ile beta arasında kaldı.</summary>
    Exact,
    /// <summary>Gerçek puan EN AZ bu kadar (beta kesmesi oldu).</summary>
    LowerBound,
    /// <summary>Gerçek puan EN FAZLA bu kadar (alpha hiç aşılmadı).</summary>
    UpperBound
}

public struct TranspositionEntry
{
    public ulong Key;
    public int Score;
    public int Depth;
    public NodeType Type;
    public Move BestMove;
}

/// <summary>
/// Aynı pozisyona farklı hamle sıralarıyla ulaşmak çok sık olur
/// (1.e4 e5 2.Nf3 ile 1.Nf3 e5 2.e4 aynı yere çıkar). Transposition table
/// bir kez hesaplanan sonucu saklar; ikinci gelişte arama atlanır.
///
/// Tablo sabit boyutlu bir dizidir ve çakışma olursa üzerine yazar.
/// Bu kabul edilebilir: anahtar kontrolü yanlış girdiyi kullanmamızı engeller.
/// </summary>
public class TranspositionTable
{
    private TranspositionEntry[] _entries;
    private ulong _mask;

    public int Hits { get; private set; }
    public int Stores { get; private set; }

    /// <summary>Tablonun şu anki boyutu (MB).</summary>
    public int SizeMb { get; private set; }

    /// <param name="sizeMb">Tablo boyutu (MB). 64 MB makul bir başlangıç.</param>
    public TranspositionTable(int sizeMb = 64)
    {
        _entries = Array.Empty<TranspositionEntry>();
        Resize(sizeMb);
    }

    /// <summary>
    /// Tabloyu yeniden boyutlandırır. UCI'de arayüz "setoption name Hash value N"
    /// gönderdiğinde çağrılır; içerik silinir, çünkü indeksler maskeye bağlı.
    /// </summary>
    public void Resize(int sizeMb)
    {
        sizeMb = Math.Clamp(sizeMb, 1, 4096);

        int entrySize = System.Runtime.InteropServices.Marshal.SizeOf<TranspositionEntry>();
        long count = (long)sizeMb * 1024 * 1024 / entrySize;

        // İndeksleme "key & mask" ile yapılacağı için boyut 2'nin kuvveti olmalı.
        long size = 1;
        while (size * 2 <= count) size *= 2;

        if (_entries.Length == size) { SizeMb = sizeMb; return; }

        _entries = new TranspositionEntry[size];
        _mask = (ulong)(size - 1);
        SizeMb = sizeMb;
        Hits = 0;
        Stores = 0;
    }

    public void Clear()
    {
        Array.Clear(_entries);
        Hits = 0;
        Stores = 0;
    }

    /// <summary>
    /// Tabloda kullanılabilir bir kayıt var mı? Kayıt ancak EN AZ istenen
    /// derinlikte aranmışsa güvenilirdir — sığ bir sonuç derin aramanın
    /// yerine geçemez.
    /// </summary>
    public bool TryProbe(ulong key, int depth, int ply, int alpha, int beta,
                         out int score, out Move bestMove)
    {
        ref TranspositionEntry entry = ref _entries[key & _mask];
        bestMove = default;
        score = 0;

        if (entry.Key != key) return false;

        // Hamle sıralaması için kayıtlı hamle derinlik yetersiz olsa da işe yarar.
        bestMove = entry.BestMove;

        if (entry.Depth < depth) return false;

        int stored = FromTableScore(entry.Score, ply);

        bool usable = entry.Type switch
        {
            NodeType.Exact => true,
            NodeType.LowerBound => stored >= beta,
            NodeType.UpperBound => stored <= alpha,
            _ => false
        };

        if (!usable) return false;

        score = stored;
        Hits++;
        return true;
    }

    public void Store(ulong key, int depth, int ply, int score, NodeType type, Move bestMove)
    {
        ref TranspositionEntry entry = ref _entries[key & _mask];

        // Derin arama sığ olanı ezer; aynı pozisyonun yeni sonucu da yazılır.
        if (entry.Key == key && entry.Depth > depth) return;

        entry.Key = key;
        entry.Score = ToTableScore(score, ply);
        entry.Depth = depth;
        entry.Type = type;
        entry.BestMove = bestMove;
        Stores++;
    }

    // Mat puanları kök'e olan uzaklığa göredir. Tabloya yazarken bu uzaklığı
    // çıkarıp "mata kaç hamle" bilgisini mutlak hale getiririz, okurken geri ekleriz.
    // Yoksa aynı pozisyon farklı derinliklerde yanlış mat mesafesi verir.
    private static int ToTableScore(int score, int ply)
    {
        if (score > Search.MateScore - 1000) return score + ply;
        if (score < -Search.MateScore + 1000) return score - ply;
        return score;
    }

    private static int FromTableScore(int score, int ply)
    {
        if (score > Search.MateScore - 1000) return score - ply;
        if (score < -Search.MateScore + 1000) return score + ply;
        return score;
    }
}
