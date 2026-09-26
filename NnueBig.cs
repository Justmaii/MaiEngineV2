using System.Buffers.Binary;

namespace ChessEngine;

/// <summary>
/// Stockfish 15.1'in NNUE mimarisi: HalfKAv2_hm, 1024x2 ilk katman,
/// 8 kral kovası ve 8 katman yığını.
///
/// Eski ağdan (HalfKP, 256x2) farkları:
///
/// 1. **Kral kovaları ve yatay aynalama.** Özellik indeksi artık sadece
///    "kendi şahımın karesi"ni değil, o karenin hangi bölgede olduğunu
///    kullanıyor (32 kova). Ayrıca şah vezir kanadındaysa tahta yatay
///    aynalanıyor — böylece ağ simetriyi baştan biliyor ve aynı şeyi iki
///    kez öğrenmek zorunda kalmıyor.
///
/// 2. **Şahlar da özellik.** HalfKP'de şahlar özellik değildi; burada
///    ikisi de var (ve iki renk için AYNI indeks bloğunu paylaşıyorlar).
///
/// 3. **İkişerli çarpım.** İlk katmanın 1024 çıktısı ikişer ikişer
///    çarpılıp 512'ye indiriliyor (sum0*sum1/128). Bu, ağa ikinci dereceden
///    bir terim kazandırıyor ve tek katmanla yapılamayacak ilişkileri
///    öğrenmesini sağlıyor.
///
/// 4. **8 katman yığını.** Tahtadaki taş sayısına göre farklı ağ
///    kullanılıyor: açılış ve final aynı ağırlıklarla değerlendirilmiyor.
///
/// 5. **PSQT dalı.** Ağın yanında, doğrudan özelliklerden gelen bir
///    materyal/konum terimi var; sonuç ikisinin toplamı.
/// </summary>
public sealed class NnueBigNetwork
{
    public const uint ExpectedVersion = 0x7AF32F20u;

    public const int HalfDimensions = 1024;
    public const int InputDimensions = 22528;      // 32 kova * 11 taş tipi * 64 kare
    public const int PsqtBuckets = 8;
    public const int LayerStacks = 8;

    private const int Fc0Outputs = 15;             // +1 gizli çıkış daha var (indeks 15)
    private const int Fc0Total = Fc0Outputs + 1;   // 16
    private const int Fc1Inputs = 32;              // 30 gerçek, 32'ye yuvarlanmış
    private const int Fc1Outputs = 32;
    private const int Fc2Inputs = 32;

    private const int WeightScaleBits = 6;
    private const int OutputScale = 16;

    /// <summary>Stockfish'in bir piyona verdiği iç değer. Kendi ölçeğimize
    /// (piyon = 100) çevirmek için kullanılıyor.</summary>
    public const int SfPawnValue = 208;

    // ---- Ağırlıklar ----
    private readonly short[] _ftBiases = new short[HalfDimensions];
    private readonly short[] _ftWeights = new short[HalfDimensions * InputDimensions];
    private readonly int[] _psqtWeights = new int[PsqtBuckets * InputDimensions];

    private readonly int[][] _fc0Biases = new int[LayerStacks][];
    private readonly sbyte[][] _fc0Weights = new sbyte[LayerStacks][];
    private readonly int[][] _fc1Biases = new int[LayerStacks][];
    private readonly sbyte[][] _fc1Weights = new sbyte[LayerStacks][];
    private readonly int[][] _fc2Biases = new int[LayerStacks][];
    private readonly sbyte[][] _fc2Weights = new sbyte[LayerStacks][];

    /// <summary>Aynı ağırlıkların short'a genişletilmiş kopyaları.
    /// Vektör komutları sbyte ile short'u aynı anda çarpamıyor; genişletmeyi
    /// her değerlendirmede yapmak yerine yükleme sırasında bir kez yapıyoruz.
    /// Maliyeti birkaç yüz kilobayt, kazancı aramanın en sıcak döngüsü.</summary>
    private readonly short[][] _fc0Short = new short[LayerStacks][];
    private readonly short[][] _fc1Short = new short[LayerStacks][];
    private readonly short[][] _fc2Short = new short[LayerStacks][];

    public string Description { get; private set; } = "";

    public static NnueBigNetwork? Shared { get; private set; }

    public static bool TryLoadShared(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            Shared = Load(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------------------------------------------
    //  Özellik indeksi
    // ------------------------------------------------------------------

    /// <summary>Şahın karesine göre tahtanın nasıl döndürüleceği.
    /// Vezir kanadındaki şah için yatay ayna; siyah bakışı için ayrıca dikey.</summary>
    public static int Orient(int perspective, int kingSquare)
    {
        bool queenSide = Square.File(kingSquare) <= 3;
        return perspective == Piece.White
            ? (queenSide ? 7 : 0)
            : (queenSide ? 63 : 56);
    }

    /// <summary>Şahın karesi hangi kovaya düşüyor (0-31)?</summary>
    public static int KingBucket(int perspective, int kingSquare)
    {
        int file = Square.File(kingSquare);
        int rank = Square.Rank(kingSquare);
        int column = Math.Min(file, 7 - file);
        int row = perspective == Piece.White ? 7 - rank : rank;
        return row * 4 + column;
    }

    /// <summary>Taşın özellik bloğundaki yeri. Şahlar iki renk için de
    /// aynı bloğu kullanır (10 * 64).</summary>
    public static int PieceIndex(int perspective, int piece)
    {
        int type = Piece.Type(piece);
        if (type == Piece.King) return 10 * 64;

        bool own = Piece.Color(piece) == perspective;
        return ((type - 1) * 2 + (own ? 0 : 1)) * 64;
    }

    public static int FeatureIndex(int perspective, int square, int piece, int orient, int bucketBase) =>
        (square ^ orient) + PieceIndex(perspective, piece) + bucketBase;

    // ------------------------------------------------------------------
    //  Yükleme
    // ------------------------------------------------------------------

    public static NnueBigNetwork Load(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        var net = new NnueBigNetwork();

        uint version = reader.ReadUInt32();
        if (version != ExpectedVersion)
            throw new InvalidDataException($"Beklenmeyen NNUE surumu: 0x{version:X8}");

        reader.ReadUInt32();                       // mimari karması
        uint descSize = reader.ReadUInt32();
        net.Description = System.Text.Encoding.UTF8.GetString(reader.ReadBytes((int)descSize));

        reader.ReadUInt32();                       // ilk katman karması

        for (int i = 0; i < HalfDimensions; i++) net._ftBiases[i] = reader.ReadInt16();

        // 46 MB ağırlık: tek tek ReadInt16 yavaş, bloğu bir kerede alıp çeviriyoruz.
        ReadInt16Block(reader, net._ftWeights);
        ReadInt32Block(reader, net._psqtWeights);

        for (int stack = 0; stack < LayerStacks; stack++)
        {
            reader.ReadUInt32();                   // katman yığını karması

            net._fc0Biases[stack] = ReadInt32Array(reader, Fc0Total);
            net._fc0Weights[stack] = reader.ReadBytes(Fc0Total * HalfDimensions).Select(b => (sbyte)b).ToArray();

            net._fc1Biases[stack] = ReadInt32Array(reader, Fc1Outputs);
            net._fc1Weights[stack] = reader.ReadBytes(Fc1Outputs * Fc1Inputs).Select(b => (sbyte)b).ToArray();

            net._fc2Biases[stack] = ReadInt32Array(reader, 1);
            net._fc2Weights[stack] = reader.ReadBytes(1 * Fc2Inputs).Select(b => (sbyte)b).ToArray();
        }

        for (int stack = 0; stack < LayerStacks; stack++)
        {
            net._fc0Short[stack] = Widen(net._fc0Weights[stack]);
            net._fc1Short[stack] = Widen(net._fc1Weights[stack]);
            net._fc2Short[stack] = Widen(net._fc2Weights[stack]);
        }

        if (stream.Position != stream.Length)
            throw new InvalidDataException(
                $"Dosyanin sonuna gelinemedi: {stream.Position} / {stream.Length}");

        return net;
    }

    private static short[] Widen(sbyte[] source)
    {
        var result = new short[source.Length];
        for (int i = 0; i < source.Length; i++) result[i] = source[i];
        return result;
    }

    private static void ReadInt16Block(BinaryReader reader, short[] target)
    {
        byte[] bytes = reader.ReadBytes(target.Length * 2);
        if (bytes.Length != target.Length * 2) throw new InvalidDataException("Ag dosyasi eksik.");
        for (int i = 0; i < target.Length; i++)
            target[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i * 2, 2));
    }

    private static void ReadInt32Block(BinaryReader reader, int[] target)
    {
        byte[] bytes = reader.ReadBytes(target.Length * 4);
        if (bytes.Length != target.Length * 4) throw new InvalidDataException("Ag dosyasi eksik.");
        for (int i = 0; i < target.Length; i++)
            target[i] = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(i * 4, 4));
    }

    private static int[] ReadInt32Array(BinaryReader reader, int count)
    {
        var result = new int[count];
        for (int i = 0; i < count; i++) result[i] = reader.ReadInt32();
        return result;
    }

    // ------------------------------------------------------------------
    //  Accumulator işlemleri
    // ------------------------------------------------------------------

    /// <summary>Bir bakışın toplamını sıfırdan hesaplar.</summary>
    public void Refresh(Board board, int perspective, Span<short> accumulator, Span<int> psqt)
    {
        _ftBiases.AsSpan().CopyTo(accumulator);
        psqt.Clear();

        int kingSquare = board.KingSquare[Piece.ColorIndex(perspective)];
        int orient = Orient(perspective, kingSquare);
        int bucketBase = KingBucket(perspective, kingSquare) * 11 * 64;

        ulong occupied = board.Occupied;
        while (occupied != 0)
        {
            int square = Bitboards.PopLsb(ref occupied);
            int index = FeatureIndex(perspective, square, board.Squares[square], orient, bucketBase);
            AddFeature(index, accumulator, psqt, +1);
        }
    }

    /// <summary>Tek bir özelliğin ağırlıklarını ekler (sign +1) veya çıkarır (-1).</summary>
    public void AddFeature(int index, Span<short> accumulator, Span<int> psqt, int sign)
    {
        int offset = HalfDimensions * index;
        int lanes = System.Numerics.Vector<short>.Count;
        int i = 0;

        if (System.Numerics.Vector.IsHardwareAccelerated)
        {
            if (sign > 0)
                for (; i + lanes <= HalfDimensions; i += lanes)
                {
                    var a = new System.Numerics.Vector<short>(accumulator[i..]);
                    var w = new System.Numerics.Vector<short>(_ftWeights, offset + i);
                    (a + w).CopyTo(accumulator[i..]);
                }
            else
                for (; i + lanes <= HalfDimensions; i += lanes)
                {
                    var a = new System.Numerics.Vector<short>(accumulator[i..]);
                    var w = new System.Numerics.Vector<short>(_ftWeights, offset + i);
                    (a - w).CopyTo(accumulator[i..]);
                }
        }

        if (sign > 0)
            for (; i < HalfDimensions; i++) accumulator[i] += _ftWeights[offset + i];
        else
            for (; i < HalfDimensions; i++) accumulator[i] -= _ftWeights[offset + i];

        int psqtOffset = index * PsqtBuckets;
        if (sign > 0)
            for (int k = 0; k < PsqtBuckets; k++) psqt[k] += _psqtWeights[psqtOffset + k];
        else
            for (int k = 0; k < PsqtBuckets; k++) psqt[k] -= _psqtWeights[psqtOffset + k];
    }

    // ------------------------------------------------------------------
    //  Değerlendirme
    // ------------------------------------------------------------------

    /// <summary>Pozisyonun ağ değerlendirmesi — sıfırdan, accumulator kullanmadan.</summary>
    public int Evaluate(Board board)
    {
        Span<short> white = stackalloc short[HalfDimensions];
        Span<short> black = stackalloc short[HalfDimensions];
        Span<int> whitePsqt = stackalloc int[PsqtBuckets];
        Span<int> blackPsqt = stackalloc int[PsqtBuckets];

        Refresh(board, Piece.White, white, whitePsqt);
        Refresh(board, Piece.Black, black, blackPsqt);

        return EvaluateAccumulated(white, black, whitePsqt, blackPsqt,
                                   board.SideToMove, Bitboards.PopCount(board.Occupied));
    }

    /// <summary>Hazır accumulator üzerinden değerlendirme — aramada kullanılan yol.
    /// Sonuç Stockfish'in iç ölçeğinde, sırası gelen tarafın gözünden.</summary>
    public int EvaluateAccumulated(ReadOnlySpan<short> white, ReadOnlySpan<short> black,
                                   ReadOnlySpan<int> whitePsqt, ReadOnlySpan<int> blackPsqt,
                                   int sideToMove, int pieceCount)
    {
        // Taş sayısı hangi katman yığınını kullanacağımızı belirler:
        // açılış ve final farklı ağırlıklarla değerlendirilir.
        int bucket = (pieceCount - 1) / 4;

        var us = sideToMove == Piece.White ? white : black;
        var them = sideToMove == Piece.White ? black : white;
        var usPsqt = sideToMove == Piece.White ? whitePsqt : blackPsqt;
        var themPsqt = sideToMove == Piece.White ? blackPsqt : whitePsqt;

        int psqt = (usPsqt[bucket] - themPsqt[bucket]) / 2;

        // İkişerli çarpım: 1024 toplam, 512 çıktıya iniyor.
        Span<short> transformed = stackalloc short[HalfDimensions];
        Transform(us, transformed[..(HalfDimensions / 2)]);
        Transform(them, transformed[(HalfDimensions / 2)..]);

        int positional = Propagate(transformed, bucket);

        return (psqt + positional) / OutputScale;
    }

    private static void Transform(ReadOnlySpan<short> accumulator, Span<short> output)
    {
        int half = HalfDimensions / 2;
        int lanes = System.Numerics.Vector<short>.Count;
        int j = 0;

        if (System.Numerics.Vector.IsHardwareAccelerated)
        {
            var zero = System.Numerics.Vector<short>.Zero;
            var max = new System.Numerics.Vector<short>(127);
            for (; j + lanes <= half; j += lanes)
            {
                var a = new System.Numerics.Vector<short>(accumulator[j..]);
                var b = new System.Numerics.Vector<short>(accumulator[(j + half)..]);
                a = System.Numerics.Vector.Min(System.Numerics.Vector.Max(a, zero), max);
                b = System.Numerics.Vector.Min(System.Numerics.Vector.Max(b, zero), max);

                // 127*127 = 16129, short'a sığar; 128'e bölünce 0..126 kalır.
                ((a * b) >> 7).CopyTo(output[j..]);
            }
        }

        for (; j < half; j++)
        {
            int sum0 = Math.Clamp((int)accumulator[j], 0, 127);
            int sum1 = Math.Clamp((int)accumulator[j + half], 0, 127);
            output[j] = (short)(sum0 * sum1 / 128);
        }
    }

    private int Propagate(ReadOnlySpan<short> input, int bucket)
    {
        Span<int> fc0 = stackalloc int[Fc0Total];
        var fc0Bias = _fc0Biases[bucket];
        var fc0Weight = _fc0Short[bucket];

        for (int o = 0; o < Fc0Total; o++)
            fc0[o] = fc0Bias[o] + Dot(input, fc0Weight, o * HalfDimensions, HalfDimensions);

        // Birinci katmanın çıkışı iki ayrı aktivasyondan geçip yan yana konuyor:
        // kare alınmış hâli ve normal kırpılmış hâli. Ağ ikisini birlikte görüyor.
        Span<short> hidden = stackalloc short[Fc1Inputs];
        hidden.Clear();
        for (int i = 0; i < Fc0Outputs; i++)
        {
            long squared = ((long)fc0[i] * fc0[i]) >> (2 * WeightScaleBits);
            hidden[i] = (short)Math.Clamp(squared / 128, 0, 127);
            hidden[Fc0Outputs + i] = (short)Math.Clamp(fc0[i] >> WeightScaleBits, 0, 127);
        }

        var fc1Bias = _fc1Biases[bucket];
        var fc1Weight = _fc1Short[bucket];

        Span<short> hidden2 = stackalloc short[Fc2Inputs];
        for (int o = 0; o < Fc1Outputs; o++)
        {
            int sum = fc1Bias[o] + Dot(hidden, fc1Weight, o * Fc1Inputs, Fc1Inputs);
            hidden2[o] = (short)Math.Clamp(sum >> WeightScaleBits, 0, 127);
        }

        int output = _fc2Biases[bucket][0] + Dot(hidden2, _fc2Short[bucket], 0, Fc2Inputs);

        // Birinci katmanın 16. çıkışı ağı atlayıp doğrudan sonuca ekleniyor.
        int forward = fc0[Fc0Outputs] * (600 * OutputScale) / (127 * (1 << WeightScaleBits));

        return output + forward;
    }

    /// <summary>
    /// İç çarpım — değerlendirmenin en pahalı yeri. İlk katmanda 16 çıkış x
    /// 1024 girdi = 16.384 çarpma var ve bu her düğümde yapılıyor.
    ///
    /// Ara toplam int'te tutuluyor çünkü 1024 çarpımın toplamı short'a sığmaz.
    /// </summary>
    private static int Dot(ReadOnlySpan<short> input, short[] weights, int offset, int count)
    {
        int lanes = System.Numerics.Vector<short>.Count;
        int i = 0;
        int sum = 0;

        if (System.Numerics.Vector.IsHardwareAccelerated && count >= lanes)
        {
            var accumulator = System.Numerics.Vector<int>.Zero;
            for (; i + lanes <= count; i += lanes)
            {
                var w = new System.Numerics.Vector<short>(weights, offset + i);
                var x = new System.Numerics.Vector<short>(input[i..]);
                System.Numerics.Vector.Widen(w * x, out var low, out var high);
                accumulator += low + high;
            }
            sum = System.Numerics.Vector.Dot(accumulator, System.Numerics.Vector<int>.One);
        }

        for (; i < count; i++) sum += input[i] * weights[offset + i];
        return sum;
    }
}
