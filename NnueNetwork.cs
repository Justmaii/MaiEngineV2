namespace ChessEngine;

/// <summary>
/// HalfKP mimarisinde NNUE ağı okuyucusu ve değerlendiricisi
/// (Stockfish 12 dönemi formatı: 41024 girdi -> 256x2 -> 32 -> 32 -> 1).
///
/// Ağın kendisi bu repoya dahil değildir; yolu dışarıdan verilir.
///
/// Temel fikir: pozisyonun her (şah karesi, taş, taş karesi) üçlüsü bir
/// "özellik" numarasıdır. Ağın ilk katmanı bu özelliklerin ağırlıklarını
/// toplar — yani aslında dev bir arama tablosu. Toplama işlemi olduğu için
/// taş oynadığında baştan hesaplamak gerekmez, çıkan özelliği çıkarıp
/// gireni eklemek yeter. "Efficiently updatable" adı buradan geliyor.
///
/// Bu ilk sürüm her değerlendirmede baştan hesaplıyor — önce DOĞRULUK.
/// Artımlı güncelleme doğruluk kanıtlandıktan sonra eklenecek.
/// </summary>
public sealed class NnueNetwork
{
    public const uint ExpectedVersion = 0x7AF32F16u;

    /// <summary>Vektör komutları kullanılsın mı? Karşılaştırma ölçümü için
    /// MAIENGINE_NOSIMD=1 ile kapatılabilir.</summary>
    public static readonly bool UseSimd =
        Environment.GetEnvironmentVariable("MAIENGINE_NOSIMD") != "1";

    public const int HalfDimensions = 256;
    private const int InputDimensions = 41024;   // 64 şah karesi * 641
    private const int PsEnd = 641;

    private const int L1In = 512, L1Out = 32;
    private const int L2In = 32, L2Out = 32;
    private const int L3In = 32;

    /// <summary>Ağın ham çıktısı bu sayıya bölünerek santipiyona çevrilir.</summary>
    private const int FvScale = 16;

    /// <summary>Ara katmanlarda ağırlıklar bu kadar bit kaydırılarak ölçeklenir.</summary>
    private const int WeightScaleBits = 6;

    private readonly short[] _ftBiases = new short[HalfDimensions];
    private readonly short[] _ftWeights = new short[HalfDimensions * InputDimensions];

    private readonly int[] _l1Biases = new int[L1Out];
    private readonly sbyte[] _l1Weights = new sbyte[L1Out * L1In];
    private readonly int[] _l2Biases = new int[L2Out];
    private readonly sbyte[] _l2Weights = new sbyte[L2Out * L2In];
    private readonly int[] _l3Biases = new int[1];
    private readonly sbyte[] _l3Weights = new sbyte[L3In];

    // Ağırlıkların short kopyaları. Vektör komutları sbyte ile doğrudan
    // çarpamıyor; genişletmeyi her değerlendirmede yapmak yerine yükleme
    // sırasında bir kez yapıyoruz. Bellek maliyeti 35 KB, tamamen ihmal edilebilir.
    private readonly short[] _l1Short = new short[L1Out * L1In];
    private readonly short[] _l2Short = new short[L2Out * L2In];

    public string Description { get; private set; } = "";
    public static NnueNetwork? Shared { get; private set; }

    private NnueNetwork() { }

    public static NnueNetwork Load(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);

        var net = new NnueNetwork();

        uint version = reader.ReadUInt32();
        if (version != ExpectedVersion)
            throw new InvalidDataException(
                $"Beklenmeyen NNUE sürümü: 0x{version:X8} (beklenen 0x{ExpectedVersion:X8}). " +
                "Bu okuyucu HalfKP (Stockfish 12 dönemi) ağları içindir.");

        reader.ReadUInt32();                       // mimari özeti (hash)
        uint descriptionSize = reader.ReadUInt32();
        net.Description = System.Text.Encoding.UTF8.GetString(reader.ReadBytes((int)descriptionSize));

        reader.ReadUInt32();                       // özellik katmanı hash
        for (int i = 0; i < HalfDimensions; i++) net._ftBiases[i] = reader.ReadInt16();
        for (int i = 0; i < net._ftWeights.Length; i++) net._ftWeights[i] = reader.ReadInt16();

        reader.ReadUInt32();                       // ağ hash
        ReadAffine(reader, net._l1Biases, net._l1Weights);
        ReadAffine(reader, net._l2Biases, net._l2Weights);
        ReadAffine(reader, net._l3Biases, net._l3Weights);

        for (int i = 0; i < net._l1Weights.Length; i++) net._l1Short[i] = net._l1Weights[i];
        for (int i = 0; i < net._l2Weights.Length; i++) net._l2Short[i] = net._l2Weights[i];

        if (stream.Position != stream.Length)
            throw new InvalidDataException(
                $"Dosyanın sonuna gelinmedi: {stream.Position}/{stream.Length} bayt okundu.");

        return net;
    }

    private static void ReadAffine(BinaryReader reader, int[] biases, sbyte[] weights)
    {
        for (int i = 0; i < biases.Length; i++) biases[i] = reader.ReadInt32();
        for (int i = 0; i < weights.Length; i++) weights[i] = reader.ReadSByte();
    }

    public static bool TryLoadShared(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            Shared = Load(path);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"NNUE yüklenemedi: {ex.Message}");
            Shared = null;
            return false;
        }
    }

    // ------------------------------------------------------------------
    //  Özellik indeksleri
    // ------------------------------------------------------------------

    // Taş türüne göre "kendi taşım" ve "rakip taşı" slot başlangıçları.
    // Şahlar özellik değildir; sadece kendi şahının karesi indeksi böler.
    private static readonly int[] OwnPieceOffset  = { 0, 1, 129, 257, 385, 513, 0 };
    private static readonly int[] TheirPieceOffset = { 0, 65, 193, 321, 449, 577, 0 };

    /// <summary>Siyahın gözünden tahta 180 derece döner.</summary>
    public static int Orient(int perspective, int square) =>
        perspective == Piece.White ? square : square ^ 63;

    private static int FeatureIndex(int perspective, int square, int piece, int orientedKing)
    {
        int type = Piece.Type(piece);
        int[] offsets = Piece.IsColor(piece, perspective) ? OwnPieceOffset : TheirPieceOffset;
        return Orient(perspective, square) + offsets[type] + PsEnd * orientedKing;
    }

    // ------------------------------------------------------------------
    //  Değerlendirme
    // ------------------------------------------------------------------

    /// <summary>Pozisyonun ağ değerlendirmesi, SIRASI GELEN tarafın gözünden.</summary>
    public int Evaluate(Board board)
    {
        Span<short> accumulator = stackalloc short[2 * HalfDimensions];
        Accumulate(board, Piece.White, accumulator[..HalfDimensions]);
        Accumulate(board, Piece.Black, accumulator[HalfDimensions..]);

        return EvaluateAccumulated(accumulator, board.SideToMove);
    }

    /// <summary>Tek bir özelliğin ağırlıklarını ekler (sign +1) veya çıkarır (-1).
    /// Artımlı güncellemenin çekirdeği.</summary>
    public void AccumulateFeature(int perspective, int square, int piece,
                                  int orientedKing, Span<short> accumulator, int sign)
    {
        if (piece == Piece.None || Piece.Type(piece) == Piece.King) return;

        int index = FeatureIndex(perspective, square, piece, orientedKing);
        int offset = HalfDimensions * index;
        AddWeights(offset, accumulator, sign);
    }

    /// <summary>Bir ozelligin 256 agirligini toplama ekler/cikarir.
    /// Aramada en sik cagrilan dongu burasi — vektorle isliyoruz.</summary>
    private void AddWeights(int offset, Span<short> accumulator, int sign)
    {
        int lanes = System.Numerics.Vector<short>.Count;
        int i = 0;

        if (UseSimd && System.Numerics.Vector.IsHardwareAccelerated)
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
    }

    /// <summary>Bir bakışın toplamını sıfırdan hesaplar.</summary>
    public void Refresh(Board board, int perspective, Span<short> output) =>
        Accumulate(board, perspective, output);

    /// <summary>Hazır accumulator üzerinden değerlendirme — aramada kullanılan yol.</summary>
    public int EvaluateAccumulated(ReadOnlySpan<short> accumulator, int sideToMove)
    {
        Span<short> input = stackalloc short[L1In];
        int firstHalf = sideToMove == Piece.White ? 0 : HalfDimensions;
        int secondHalf = HalfDimensions - firstHalf;

        ClippedRelu(accumulator.Slice(firstHalf, HalfDimensions), input[..HalfDimensions]);
        ClippedRelu(accumulator.Slice(secondHalf, HalfDimensions), input[HalfDimensions..]);

        Span<short> hidden1 = stackalloc short[L1Out];
        Affine(input, _l1Biases, _l1Short, L1In, L1Out, hidden1);

        Span<short> hidden2 = stackalloc short[L2Out];
        Affine(hidden1, _l2Biases, _l2Short, L2In, L2Out, hidden2);

        int output = _l3Biases[0];
        for (int i = 0; i < L3In; i++) output += _l3Weights[i] * hidden2[i];

        return output / FvScale;
    }


    /// <summary>Toplamlari [0,127] arasina kirpar (clipped ReLU).</summary>
    private static void ClippedRelu(ReadOnlySpan<short> source, Span<short> target)
    {
        int lanes = System.Numerics.Vector<short>.Count;
        int i = 0;

        if (UseSimd && System.Numerics.Vector.IsHardwareAccelerated)
        {
            var lo = System.Numerics.Vector<short>.Zero;
            var hi = new System.Numerics.Vector<short>(127);
            for (; i + lanes <= source.Length; i += lanes)
            {
                var v = new System.Numerics.Vector<short>(source[i..]);
                System.Numerics.Vector.Min(System.Numerics.Vector.Max(v, lo), hi).CopyTo(target[i..]);
            }
        }

        for (; i < source.Length; i++)
        {
            short v = source[i];
            target[i] = v < 0 ? (short)0 : v > 127 ? (short)127 : v;
        }
    }

    /// <summary>Bir bakış açısı için ilk katmanın toplamı.</summary>
    private void Accumulate(Board board, int perspective, Span<short> output)
    {
        _ftBiases.AsSpan().CopyTo(output);

        int kingSquare = board.KingSquare[Piece.ColorIndex(perspective)];
        int orientedKing = Orient(perspective, kingSquare);

        for (int square = 0; square < 64; square++)
        {
            int piece = board.Squares[square];
            if (piece == Piece.None || Piece.Type(piece) == Piece.King) continue;

            int index = FeatureIndex(perspective, square, piece, orientedKing);
            AddWeights(HalfDimensions * index, output, +1);
        }
    }

    /// <summary>
    /// Tam bağlı katman + kırpılmış ReLU.
    ///
    /// Değerlendirmenin en pahalı yeri burası: 512 girdi x 32 çıkış = 16.384
    /// çarpma. Ağırlıklar short olarak hazır tutulduğu için vektör komutları
    /// diziden doğrudan okuyabiliyor; ara toplam int'te tutuluyor çünkü
    /// 512 çarpımın toplamı short'a sığmaz.
    ///
    /// Skaler ve vektör yolu aynı sonucu vermek zorunda — nnuecheck sınıyor.
    /// </summary>
    private static void Affine(ReadOnlySpan<short> input, int[] biases, short[] weights,
                               int inputCount, int outputCount, Span<short> output)
    {
        int lanes = System.Numerics.Vector<short>.Count;
        bool vectorised = UseSimd && System.Numerics.Vector.IsHardwareAccelerated && inputCount >= lanes;

        for (int j = 0; j < outputCount; j++)
        {
            int offset = j * inputCount;
            int sum = biases[j];
            int i = 0;

            if (vectorised)
            {
                var accumulator = System.Numerics.Vector<int>.Zero;

                for (; i + lanes <= inputCount; i += lanes)
                {
                    var w = new System.Numerics.Vector<short>(weights, offset + i);
                    var x = new System.Numerics.Vector<short>(input[i..]);
                    System.Numerics.Vector.Widen(w * x, out var low, out var high);
                    accumulator += low + high;
                }

                sum += System.Numerics.Vector.Dot(accumulator, System.Numerics.Vector<int>.One);
            }

            for (; i < inputCount; i++) sum += weights[offset + i] * input[i];

            output[j] = (short)Math.Clamp(sum >> WeightScaleBits, 0, 127);
        }
    }
}
