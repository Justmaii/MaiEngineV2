using System.Threading;

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

/// <summary>
/// Kayıt iki 64-bit alandan ibaret: paketlenmiş veri ve "anahtar XOR veri".
/// Sebebi çok iş parçacıklı arama: kilit kullanmadan yazıyoruz, yani bir okuyucu
/// yarı yazılmış bir kaydı görebilir. XOR eşleşmesi bunu yakalar — yarım kayıtta
/// anahtar tutmaz ve kayıt yokmuş gibi davranılır. Stockfish de aynı numarayı
/// kullanır; kilitten çok daha ucuz ve yanlış kayıt kullanma ihtimali sıfır.
/// </summary>
public struct TranspositionEntry
{
    public ulong KeyXorData;
    public ulong Data;
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

    // ---- Paketleme düzeni (48 bit kullanılıyor) ----
    //  0..19  puan + 524288        (20 bit)
    // 20..27  derinlik + 64        ( 8 bit)
    // 28..29  düğüm türü           ( 2 bit)
    // 30..35  hamle: kalkış karesi ( 6 bit)
    // 36..41  hamle: varış karesi  ( 6 bit)
    // 42..44  hamle: bayrak        ( 3 bit)
    // 45..47  terfi taşı türü      ( 3 bit)
    private const int ScoreBias = 524288;
    private const int DepthBias = 64;

    private static ulong Pack(int score, int depth, NodeType type, Move move, int eval = short.MinValue)
    {
        eval = Math.Clamp(eval, short.MinValue, short.MaxValue);
        // 20 bit puan, 8 bit derinlik: gerçek arama bu sınırların çok altında
        // kalır, ama sessizce yanlış sayı saklamaktansa kırpmak daha güvenli.
        score = Math.Clamp(score, -ScoreBias + 1, ScoreBias - 1);
        depth = Math.Clamp(depth, -DepthBias, 191 - DepthBias);

        return (ulong)(uint)(score + ScoreBias)
        | ((ulong)(uint)(depth + DepthBias) << 20)
        | ((ulong)(byte)type << 28)
        | ((ulong)(uint)move.From << 30)
        | ((ulong)(uint)move.To << 36)
        | ((ulong)(uint)(int)move.Flag << 42)
        | ((ulong)(uint)move.PromotionType << 45)
        | ((ulong)(ushort)(short)eval << 48);
    }

    private static int UnpackScore(ulong data) => (int)(data & 0xFFFFF) - ScoreBias;
    private static int UnpackDepth(ulong data) => (int)((data >> 20) & 0xFF) - DepthBias;
    private static NodeType UnpackType(ulong data) => (NodeType)((data >> 28) & 0x3);

    private static Move UnpackMove(ulong data) => new(
        (int)((data >> 30) & 0x3F),
        (int)((data >> 36) & 0x3F),
        (MoveFlag)((data >> 42) & 0x7),
        (int)((data >> 45) & 0x7));

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

        const int entrySize = 16;   // iki ulong
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

    /// <summary>Tablo ne kadar dolu (binde). Arayüzler bunu "hashfull" olarak
    /// gösterir; tablonun küçük gelip gelmediğini anlamanın en pratik yolu.
    /// Bütün tabloyu taramak pahalı olur, ilk 1000 kayıt örnekleniyor.</summary>
    public int PermillFull()
    {
        int sample = Math.Min(1000, _entries.Length);
        if (sample == 0) return 0;

        int used = 0;
        for (int i = 0; i < sample; i++)
            if (_entries[i].Data != 0 || _entries[i].KeyXorData != 0) used++;

        return used * 1000 / sample;
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

        ulong data = Volatile.Read(ref entry.Data);
        if ((Volatile.Read(ref entry.KeyXorData) ^ data) != key) return false;

        // Hamle sıralaması için kayıtlı hamle derinlik yetersiz olsa da işe yarar.
        bestMove = UnpackMove(data);

        if (UnpackDepth(data) < depth) return false;

        int stored = FromTableScore(UnpackScore(data), ply);

        bool usable = UnpackType(data) switch
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

    /// <summary>Ham okuma: kayıt varsa derinliği, türü ve puanı ne olursa olsun
    /// döndürür. Kesme kararını arama verir (PV düğümlerinde kesme yapılmaz,
    /// singular extension kaydın türüne ve derinliğine bakar).</summary>
    /// <summary>Kayıttaki durağan değerlendirme (48..63. bitler). Yoksa short.MinValue.
    /// Ağ değerlendirmesi pahalı; aynı pozisyona tekrar gelindiğinde yeniden hesaplanmaz.</summary>
    public static int UnpackEval(ulong data) => (short)(ushort)(data >> 48);

    public bool Probe(ulong key, int ply, out int score, out int depth, out NodeType type, out Move move)
        => Probe(key, ply, out score, out depth, out type, out move, out _);

    public bool Probe(ulong key, int ply, out int score, out int depth, out NodeType type, out Move move, out int eval)
    {
        ref TranspositionEntry entry = ref _entries[key & _mask];
        ulong data = Volatile.Read(ref entry.Data);
        if ((Volatile.Read(ref entry.KeyXorData) ^ data) != key || (data == 0 && entry.KeyXorData == 0))
        {
            score = 0; depth = -DepthBias; type = NodeType.UpperBound; move = default;
            eval = short.MinValue;
            return false;
        }
        eval = UnpackEval(data);
        move = UnpackMove(data);
        depth = UnpackDepth(data);
        type = UnpackType(data);
        score = FromTableScore(UnpackScore(data), ply);
        return true;
    }

    public void Store(ulong key, int depth, int ply, int score, NodeType type, Move bestMove,
                      int eval = short.MinValue)
    {
        ref TranspositionEntry entry = ref _entries[key & _mask];

        // Derin arama sığ olanı ezer; aynı pozisyonun yeni sonucu da yazılır.
        ulong old = Volatile.Read(ref entry.Data);
        if ((Volatile.Read(ref entry.KeyXorData) ^ old) == key && UnpackDepth(old) > depth) return;

        ulong data = Pack(ToTableScore(score, ply), depth, type, bestMove, eval);
        entry.Data = data;
        Volatile.Write(ref entry.KeyXorData, key ^ data);
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
