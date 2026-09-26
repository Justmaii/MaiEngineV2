namespace ChessEngine;

/// <summary>
/// Bir hamlenin özellik listesinde ne değiştirdiği.
///
/// En fazla iki kaldırma (oynayan taş + alınan taş) ve iki ekleme
/// (varan taş + rok kalesi) olur. Dizi yerine düz alanlar kullanılıyor
/// çünkü bu yapı her hamlede oluşturuluyor ve tahsisat istemiyoruz.
/// </summary>
public struct NnueMoveDelta
{
    /// <summary>Şah oynadıysa rengi, yoksa Piece.None.
    /// O bakışın bütün indeksleri kaydığı için baştan hesap gerekir.</summary>
    public int MovedKingColor;

    public int RemovedCount, AddedCount;
    public int Removed0Square, Removed0Piece;
    public int Removed1Square, Removed1Piece;
    public int Added0Square, Added0Piece;
    public int Added1Square, Added1Piece;

    public void Remove(int square, int piece)
    {
        if (RemovedCount == 0) { Removed0Square = square; Removed0Piece = piece; }
        else { Removed1Square = square; Removed1Piece = piece; }
        RemovedCount++;
    }

    public void Add(int square, int piece)
    {
        if (AddedCount == 0) { Added0Square = square; Added0Piece = piece; }
        else { Added1Square = square; Added1Piece = piece; }
        AddedCount++;
    }
}

/// <summary>
/// NNUE'nin ilk katman toplamlarını hamleler boyunca taşıyan yığın.
///
/// Ağın ilk katmanı 41024 özelliğin ağırlık toplamıdır. Bir hamlede bunların
/// sadece birkaçı değişir, yani toplamı baştan hesaplamak yerine çıkanı çıkarıp
/// gireni eklemek yeter — "efficiently updatable" tam olarak bu.
///
/// Tek istisna şah: özellik indeksi kendi şahının karesini içerdiğinden, şah
/// oynayınca o bakışın bütün indeksleri kayar ve baştan hesap gerekir.
/// Rakibin şahı özellik olmadığı için diğer bakış etkilenmez.
/// </summary>
public sealed class NnueAccumulator
{
    public const int HalfDimensions = NnueNetwork.HalfDimensions;

    private readonly NnueNetwork _net;
    private readonly List<short[]> _stack = new();
    private int _depth;

    public NnueAccumulator(NnueNetwork net)
    {
        _net = net;
        _stack.Add(new short[2 * HalfDimensions]);
    }

    /// <summary>[0..255] beyazın bakışı, [256..511] siyahın bakışı.</summary>
    public short[] Current => _stack[_depth];

    public void RefreshAll(Board board)
    {
        _depth = 0;
        _net.Refresh(board, Piece.White, Current.AsSpan(0, HalfDimensions));
        _net.Refresh(board, Piece.Black, Current.AsSpan(HalfDimensions, HalfDimensions));
    }

    public void Pop() => _depth--;

    /// <summary>Hamleyi uygular. board hamle YAPILDIKTAN SONRAKİ durumu
    /// göstermelidir; şah tazelemesi yeni şah karesini kullanır.</summary>
    public void ApplyMove(Board board, in NnueMoveDelta delta)
    {
        _depth++;
        while (_stack.Count <= _depth) _stack.Add(new short[2 * HalfDimensions]);
        Array.Copy(_stack[_depth - 1], _stack[_depth], 2 * HalfDimensions);

        Update(board, delta, Piece.White);
        Update(board, delta, Piece.Black);
    }

    private void Update(Board board, in NnueMoveDelta delta, int perspective)
    {
        int offset = perspective == Piece.White ? 0 : HalfDimensions;
        var slice = _stack[_depth].AsSpan(offset, HalfDimensions);

        if (delta.MovedKingColor == perspective)
        {
            _net.Refresh(board, perspective, slice);
            return;
        }

        int orientedKing = NnueNetwork.Orient(
            perspective, board.KingSquare[Piece.ColorIndex(perspective)]);

        if (delta.RemovedCount > 0)
            _net.AccumulateFeature(perspective, delta.Removed0Square, delta.Removed0Piece, orientedKing, slice, -1);
        if (delta.RemovedCount > 1)
            _net.AccumulateFeature(perspective, delta.Removed1Square, delta.Removed1Piece, orientedKing, slice, -1);
        if (delta.AddedCount > 0)
            _net.AccumulateFeature(perspective, delta.Added0Square, delta.Added0Piece, orientedKing, slice, +1);
        if (delta.AddedCount > 1)
            _net.AccumulateFeature(perspective, delta.Added1Square, delta.Added1Piece, orientedKing, slice, +1);
    }
}
