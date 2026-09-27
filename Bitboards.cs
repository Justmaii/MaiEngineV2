using System.Numerics;

namespace ChessEngine;

/// <summary>
/// 64-bit maskelerle tahta işlemleri. Bit numarası kare numarasıyla aynı:
/// a1 = 0, h8 = 63, yani bit = rank * 8 + file.
///
/// Neden: eski kod bir karenin saldırı altında olup olmadığını sormak için
/// yönlerde tek tek yürüyordu — her adımda sınır kontrolü, dizi okuması, dal.
/// Bitboard'da aynı soru birkaç tamsayı işlemine iniyor. Arama süresinin
/// büyük kısmı bu sorguda geçtiği için kazanç doğrudan derinliğe yazılıyor.
///
/// Kayan taşlar için "klasik" yöntem: her kare ve her yön için o yöndeki
/// karelerin maskesi hazır duruyor. Maskeyi doluluk ile kesiştirip ilk
/// engeli buluyoruz; engelin arkası, engelin kendi maskesiyle siliniyor.
/// Magic bitboard kadar hızlı değil ama tablosu küçük, kurulumu açık ve
/// yavaş referansla birebir doğrulanabiliyor.
/// </summary>
public static class Bitboards
{
    public const int North = 0, South = 1, East = 2, West = 3;
    public const int NorthEast = 4, NorthWest = 5, SouthEast = 6, SouthWest = 7;

    private static readonly int[] DirFile = { 0, 0, 1, -1, 1, -1, 1, -1 };
    private static readonly int[] DirRank = { 1, -1, 0, 0, 1, 1, -1, -1 };

    /// <summary>Yön artan kare numarasına mı gidiyor? İlk engeli bulurken
    /// artanda en küçük bit, azalanda en büyük bit aranır.</summary>
    private static readonly bool[] DirIncreases = { true, false, true, false, true, true, false, false };

    private static readonly ulong[][] Rays = new ulong[8][];

    public static readonly ulong[] KnightAttacks = new ulong[64];
    public static readonly ulong[] KingAttacks = new ulong[64];

    /// <summary>[renk indeksi][kare] — o karedeki piyonun vurduğu kareler.</summary>
    public static readonly ulong[][] PawnAttacks = { new ulong[64], new ulong[64] };

    static Bitboards()
    {
        for (int dir = 0; dir < 8; dir++)
        {
            Rays[dir] = new ulong[64];
            for (int sq = 0; sq < 64; sq++)
            {
                int f = Square.File(sq) + DirFile[dir];
                int r = Square.Rank(sq) + DirRank[dir];
                ulong mask = 0;
                while (Square.IsValid(f, r))
                {
                    mask |= 1UL << Square.FromFileRank(f, r);
                    f += DirFile[dir];
                    r += DirRank[dir];
                }
                Rays[dir][sq] = mask;
            }
        }

        var knightSteps = new (int df, int dr)[]
            { (1, 2), (2, 1), (2, -1), (1, -2), (-1, -2), (-2, -1), (-2, 1), (-1, 2) };
        var kingSteps = new (int df, int dr)[]
            { (0, 1), (0, -1), (1, 0), (-1, 0), (1, 1), (1, -1), (-1, 1), (-1, -1) };

        for (int sq = 0; sq < 64; sq++)
        {
            int file = Square.File(sq), rank = Square.Rank(sq);

            foreach (var (df, dr) in knightSteps)
                if (Square.IsValid(file + df, rank + dr))
                    KnightAttacks[sq] |= 1UL << Square.FromFileRank(file + df, rank + dr);

            foreach (var (df, dr) in kingSteps)
                if (Square.IsValid(file + df, rank + dr))
                    KingAttacks[sq] |= 1UL << Square.FromFileRank(file + df, rank + dr);

            // Beyaz piyon yukarı, siyah aşağı vurur.
            foreach (int df in new[] { -1, 1 })
            {
                if (Square.IsValid(file + df, rank + 1))
                    PawnAttacks[Piece.ColorIndex(Piece.White)][sq] |= 1UL << Square.FromFileRank(file + df, rank + 1);
                if (Square.IsValid(file + df, rank - 1))
                    PawnAttacks[Piece.ColorIndex(Piece.Black)][sq] |= 1UL << Square.FromFileRank(file + df, rank - 1);
            }
        }

        BuildMagics();
    }

    /// <summary>Tek yönde, ilk engele kadar (engel dahil) kareler.</summary>
    private static ulong RayAttacks(int dir, int sq, ulong occupied)
    {
        ulong ray = Rays[dir][sq];
        ulong blockers = ray & occupied;
        if (blockers == 0) return ray;

        int first = DirIncreases[dir]
            ? BitOperations.TrailingZeroCount(blockers)
            : 63 - BitOperations.LeadingZeroCount(blockers);

        return ray ^ Rays[dir][first];   // engelin arkasını sil
    }

    private static ulong RookAttacksClassic(int sq, ulong occupied) =>
        RayAttacks(North, sq, occupied) | RayAttacks(South, sq, occupied) |
        RayAttacks(East, sq, occupied) | RayAttacks(West, sq, occupied);

    private static ulong BishopAttacksClassic(int sq, ulong occupied) =>
        RayAttacks(NorthEast, sq, occupied) | RayAttacks(NorthWest, sq, occupied) |
        RayAttacks(SouthEast, sq, occupied) | RayAttacks(SouthWest, sq, occupied);


    // ------------------------------------------------------------------
    //  Magic bitboard
    // ------------------------------------------------------------------
    //
    // Klasik yöntem her yön için maskeyi alıp ilk engeli arıyordu: dört yön,
    // dört dallanma. Magic bitboard aynı cevabı TEK çarpma ve tek tablo
    // okumasıyla veriyor.
    //
    // Fikir şu: bir kareden çıkan ışınların üstündeki taşlar dışında hiçbir
    // şey o taşın nereye gidebileceğini değiştirmez. O karelere "ilgili
    // doluluk" diyoruz (kenarlar hariç — kenardaki taş ışını zaten
    // durdurmuyor, arkası yok). Kale için en fazla 12, fil için 9 kare.
    //
    // Yani 2^12 = 4096 farklı durum var ve hepsinin cevabını önceden
    // hesaplayıp saklayabiliriz. Geriye tek soru kalıyor: 12 dağınık biti
    // 0-4095 arası bir indekse nasıl çeviririz? Cevap: öyle bir "sihirli"
    // sayıyla çarp ki, çarpımın üst bitleri her durum için farklı çıksın.
    // O sayı analitik olarak bulunmuyor — rastgele deneyip tutanı alıyoruz,
    // ve burada yükleme sırasında bir kez yapılıyor.

    private static readonly ulong[] RookMasks = new ulong[64];
    private static readonly ulong[] BishopMasks = new ulong[64];
    private static readonly ulong[] RookMagics = new ulong[64];
    private static readonly ulong[] BishopMagics = new ulong[64];
    private static readonly int[] RookShifts = new int[64];
    private static readonly int[] BishopShifts = new int[64];
    private static readonly ulong[][] RookTable = new ulong[64][];
    private static readonly ulong[][] BishopTable = new ulong[64][];

    /// <summary>Ölçüm için: eski klasik ray yöntemine dönmeyi sağlar.</summary>
    public static readonly bool UseMagic =
        Environment.GetEnvironmentVariable("MAIENGINE_CLASSICRAYS") != "1";

    public static ulong RookAttacks(int sq, ulong occupied)
    {
        if (!UseMagic) return RookAttacksClassic(sq, occupied);
        ulong index = ((occupied & RookMasks[sq]) * RookMagics[sq]) >> RookShifts[sq];
        return RookTable[sq][index];
    }

    public static ulong BishopAttacks(int sq, ulong occupied)
    {
        if (!UseMagic) return BishopAttacksClassic(sq, occupied);
        ulong index = ((occupied & BishopMasks[sq]) * BishopMagics[sq]) >> BishopShifts[sq];
        return BishopTable[sq][index];
    }

    /// <summary>Işının üstündeki "önemli" kareler: kenarlar hariç, çünkü
    /// kenardaki bir taşın arkası yok, ışını kesmesi sonucu değiştirmez.</summary>
    private static ulong RelevantMask(int sq, bool diagonal)
    {
        ulong mask = 0;
        int start = diagonal ? 4 : 0;

        for (int dir = start; dir < start + 4; dir++)
        {
            int f = Square.File(sq) + DirFile[dir];
            int r = Square.Rank(sq) + DirRank[dir];
            while (Square.IsValid(f + DirFile[dir], r + DirRank[dir]))
            {
                mask |= 1UL << Square.FromFileRank(f, r);
                f += DirFile[dir];
                r += DirRank[dir];
            }
        }
        return mask;
    }

    private static void BuildMagics()
    {
        // Sabit tohum: aynı sihirli sayılar her çalıştırmada bulunsun,
        // yoksa "bugün çalıştı yarın çalışmadı" tuzağına düşeriz.
        ulong state = 0x246C_CB2D_3B2F_1E5AUL;

        ulong NextRandom()
        {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            return state;
        }

        // Sihirli sayı az bitli olmalı: çarpım üst bitlerde çok fazla
        // karışmadan yayılsın istiyoruz. Üç rastgele sayının AND'i bunu verir.
        ulong SparseRandom() => NextRandom() & NextRandom() & NextRandom();

        for (int sq = 0; sq < 64; sq++)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                bool diagonal = pass == 1;
                ulong mask = RelevantMask(sq, diagonal);
                int bits = PopCount(mask);
                int size = 1 << bits;
                int shift = 64 - bits;

                // Maskedeki bitlerin bütün alt kümeleri ve her birinin cevabı.
                var occupancies = new ulong[size];
                var attacks = new ulong[size];

                ulong subset = 0;
                for (int i = 0; i < size; i++)
                {
                    occupancies[i] = subset;
                    attacks[i] = SlowSliderAttacks(sq, subset, diagonal);
                    subset = (subset - mask) & mask;   // carry-rippler: sıradaki alt küme
                }

                var table = new ulong[size];
                var used = new int[size];
                int stamp = 0;
                ulong magic;

                while (true)
                {
                    magic = SparseRandom();

                    // Hızlı eleme: çarpımın üst baytı yeterince dolmuyorsa
                    // indeksler kümelenir, denemeye değmez.
                    if (PopCount((mask * magic) >> 56) < 6) continue;

                    stamp++;
                    bool ok = true;

                    for (int i = 0; i < size; i++)
                    {
                        ulong index = (occupancies[i] * magic) >> shift;

                        if (used[index] != stamp)
                        {
                            used[index] = stamp;
                            table[index] = attacks[i];
                        }
                        else if (table[index] != attacks[i])
                        {
                            // İki farklı doluluk aynı indekse düştü ve cevapları
                            // farklı — bu sihirli sayı işe yaramaz.
                            ok = false;
                            break;
                        }
                    }

                    if (ok) break;
                }

                if (diagonal)
                {
                    BishopMasks[sq] = mask; BishopMagics[sq] = magic;
                    BishopShifts[sq] = shift; BishopTable[sq] = table;
                }
                else
                {
                    RookMasks[sq] = mask; RookMagics[sq] = magic;
                    RookShifts[sq] = shift; RookTable[sq] = table;
                }
            }
        }
    }

    public static ulong QueenAttacks(int sq, ulong occupied) =>
        RookAttacks(sq, occupied) | BishopAttacks(sq, occupied);

    /// <summary>
    /// Kayan taş saldırılarının yavaş ama apaçık doğru hâli — kare kare yürür.
    /// Sadece doğrulama için: hızlı yolun her kare ve her doluluk örneği için
    /// bununla aynı sonucu vermesi gerekiyor.
    /// </summary>
    public static ulong SlowSliderAttacks(int sq, ulong occupied, bool diagonal)
    {
        ulong result = 0;
        int start = diagonal ? 4 : 0;

        for (int dir = start; dir < start + 4; dir++)
        {
            int f = Square.File(sq) + DirFile[dir];
            int r = Square.Rank(sq) + DirRank[dir];
            while (Square.IsValid(f, r))
            {
                int target = Square.FromFileRank(f, r);
                result |= 1UL << target;
                if ((occupied & (1UL << target)) != 0) break;
                f += DirFile[dir];
                r += DirRank[dir];
            }
        }
        return result;
    }

    public static int PopCount(ulong b) => BitOperations.PopCount(b);
    public static int Lsb(ulong b) => BitOperations.TrailingZeroCount(b);

    /// <summary>En küçük biti alır ve maskeden siler — bit döngülerinin gövdesi.</summary>
    public static int PopLsb(ref ulong b)
    {
        int sq = BitOperations.TrailingZeroCount(b);
        b &= b - 1;
        return sq;
    }
}
