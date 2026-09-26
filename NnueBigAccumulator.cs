namespace ChessEngine;

/// <summary>
/// Yeni ağın (HalfKAv2_hm) ilk katman toplamlarını hamleler boyunca taşıyan yığın.
///
/// Eski accumulator'dan iki farkı var:
///
/// 1. Yanında bir de PSQT toplamı taşınıyor (8 kova için ayrı ayrı).
/// 2. Şahlar da özellik olduğu için şah hamlesi ÖTEKİ bakışı da etkiliyor —
///    orada şah sadece yer değiştirmiş bir taş. Kendi bakışında ise kova ve
///    aynalama değiştiği için bütün indeksler kayar ve baştan hesap gerekir.
/// </summary>
public sealed class NnueBigAccumulator
{
    public const int HalfDimensions = NnueBigNetwork.HalfDimensions;
    public const int PsqtBuckets = NnueBigNetwork.PsqtBuckets;

    private readonly NnueBigNetwork _net;
    private readonly List<short[]> _stack = new();
    private readonly List<int[]> _psqtStack = new();
    private int _depth;

    public NnueBigAccumulator(NnueBigNetwork net)
    {
        _net = net;
        _stack.Add(new short[2 * HalfDimensions]);
        _psqtStack.Add(new int[2 * PsqtBuckets]);
    }

    /// <summary>[0..1023] beyazın bakışı, [1024..2047] siyahın bakışı.</summary>
    public short[] Current => _stack[_depth];
    public int[] CurrentPsqt => _psqtStack[_depth];

    public ReadOnlySpan<short> White => Current.AsSpan(0, HalfDimensions);
    public ReadOnlySpan<short> Black => Current.AsSpan(HalfDimensions, HalfDimensions);
    public ReadOnlySpan<int> WhitePsqt => CurrentPsqt.AsSpan(0, PsqtBuckets);
    public ReadOnlySpan<int> BlackPsqt => CurrentPsqt.AsSpan(PsqtBuckets, PsqtBuckets);

    public void RefreshAll(Board board)
    {
        _depth = 0;
        _net.Refresh(board, Piece.White, Current.AsSpan(0, HalfDimensions),
                                          CurrentPsqt.AsSpan(0, PsqtBuckets));
        _net.Refresh(board, Piece.Black, Current.AsSpan(HalfDimensions, HalfDimensions),
                                          CurrentPsqt.AsSpan(PsqtBuckets, PsqtBuckets));
    }

    public void Pop() => _depth--;

    /// <summary>Hamleyi uygular. board hamle YAPILDIKTAN SONRAKİ durumu
    /// göstermelidir; şah tazelemesi yeni şah karesini kullanır.</summary>
    public void ApplyMove(Board board, in NnueMoveDelta delta)
    {
        _depth++;
        while (_stack.Count <= _depth)
        {
            _stack.Add(new short[2 * HalfDimensions]);
            _psqtStack.Add(new int[2 * PsqtBuckets]);
        }

        Array.Copy(_stack[_depth - 1], _stack[_depth], 2 * HalfDimensions);
        Array.Copy(_psqtStack[_depth - 1], _psqtStack[_depth], 2 * PsqtBuckets);

        Update(board, delta, Piece.White);
        Update(board, delta, Piece.Black);
    }

    private void Update(Board board, in NnueMoveDelta delta, int perspective)
    {
        int offset = perspective == Piece.White ? 0 : HalfDimensions;
        int psqtOffset = perspective == Piece.White ? 0 : PsqtBuckets;

        var slice = _stack[_depth].AsSpan(offset, HalfDimensions);
        var psqtSlice = _psqtStack[_depth].AsSpan(psqtOffset, PsqtBuckets);

        if (delta.MovedKingColor == perspective)
        {
            // Kendi şahım oynadı: kova ve aynalama değişmiş olabilir,
            // bütün indeksler kayar. Baştan hesaplamaktan başka yol yok.
            _net.Refresh(board, perspective, slice, psqtSlice);
            return;
        }

        int kingSquare = board.KingSquare[Piece.ColorIndex(perspective)];
        int orient = NnueBigNetwork.Orient(perspective, kingSquare);
        int bucketBase = NnueBigNetwork.KingBucket(perspective, kingSquare) * 11 * 64;

        if (delta.RemovedCount > 0) Apply(perspective, delta.Removed0Square, delta.Removed0Piece, orient, bucketBase, slice, psqtSlice, -1);
        if (delta.RemovedCount > 1) Apply(perspective, delta.Removed1Square, delta.Removed1Piece, orient, bucketBase, slice, psqtSlice, -1);
        if (delta.AddedCount > 0) Apply(perspective, delta.Added0Square, delta.Added0Piece, orient, bucketBase, slice, psqtSlice, +1);
        if (delta.AddedCount > 1) Apply(perspective, delta.Added1Square, delta.Added1Piece, orient, bucketBase, slice, psqtSlice, +1);
    }

    private void Apply(int perspective, int square, int piece, int orient, int bucketBase,
                       Span<short> slice, Span<int> psqtSlice, int sign)
    {
        if (piece == Piece.None) return;
        int index = NnueBigNetwork.FeatureIndex(perspective, square, piece, orient, bucketBase);
        _net.AddFeature(index, slice, psqtSlice, sign);
    }
}
