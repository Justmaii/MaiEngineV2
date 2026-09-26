# MaiEngine v2 (NNUE)

[MaiEngine](https://github.com/Justmaii/MaiEngine)'in ikinci sürümü.
Arama aynı, **değerlendirme farklı**: elle yazılmış materyal + kare tabloları
yerine bir sinir ağı (NNUE) kullanıyor.

Amaç bir soruyu ölçmek: **bir motorun gücünün ne kadarı araması, ne kadarı
değerlendirmesi?** v1 ile v2 arasındaki tek fark değerlendirme olduğu için,
aradaki elo farkı doğrudan bunun cevabı.

## Lisans: GPLv3

v1 MIT lisanslı. v2 **GPLv3**, çünkü kullandığı ağ Stockfish'in ağı ve
Stockfish GPLv3. Bu yüzden ayrı repo — v1'in lisansı değişmesin diye.

## Ağ dosyası depoda değil

`nn-82215d0fd0df.nnue` (21 MB) buraya dahil edilmedi. Kendin indir:

```bash
curl -L -o nn.nnue \
  https://github.com/official-stockfish/networks/raw/master/nn-82215d0fd0df.nnue
```

Motor açılışta `nn.nnue` dosyasını arar; `MAIENGINE_NNUE` ortam değişkeniyle
başka bir yol da verebilirsin.

Bu, Stockfish 12 dönemi **HalfKP** mimarisidir: 41024 girdi → 256x2 → 32 → 32 → 1.
Güncel Stockfish ağları farklı mimaride ve bu okuyucu onları açmaz.

## NNUE nasıl çalışıyor

Pozisyondaki her (kendi şahının karesi, taş, taşın karesi) üçlüsü bir
**özellik** numarasıdır. Ağın ilk katmanı bu özelliklerin ağırlıklarını
toplar — yani aslında dev bir arama tablosu.

Toplama işlemi olduğu için bir hamlede her şeyi baştan hesaplamak gerekmez:
çıkan özelliği çıkar, gireni ekle. Adındaki "efficiently updatable" bu.
Ölçümde **30 kat** fark yaratıyor.

Tek istisna şah: özellik indeksi kendi şahının karesini içerdiği için şah
oynayınca o bakışın bütün indeksleri kayar ve baştan hesap gerekir. Rakibin
şahı özellik olmadığından diğer bakış etkilenmez.

## Doğrulama

Nicemlenmiş aritmetikte hata sessizdir — motor "çalışır" ama yanlış oynar.
O yüzden her parça bir referansa karşı sınanıyor:

| Test | Ne yapıyor | Sonuç |
|---|---|---|
| `nnuecheck` | 300 pozisyonda değeri Stockfish 12'nin kendi çıktısıyla karşılaştırır | **300/300 birebir** |
| `accverify` | Ağaçta her düğümde artımlı accumulator ile sıfırdan hesabı karşılaştırır | **7.037.881 düğüm** |
| `perft` | Hamle üretimi, Zobrist ve piyon anahtarı (v1'den devralındı) | 27 test |

Referans değerler, Stockfish 12 kaynağına iki satırlık yama atılıp ham tamsayı
değeri bastırılarak üretildi — normal `eval` çıktısı iki ondalığa yuvarlıyor
ve birebir karşılaştırmaya yetmiyor.

```bash
dotnet run -c Release nnuecheck nn.nnue docs/nnue-ref.txt
dotnet run -c Release accverify nn.nnue 3
```

## Çalıştırma

v1 ile aynı komutlar:

```bash
dotnet run -c Release web        # tarayıcıda oyna
dotnet run -c Release uci        # UCI modu
dotnet run -c Release bench      # derinlik ölçümü
```

## Sonuç: v1'e karşı +117 elo

40 oyun, hamle başına 200 ms, iki tarafta da açılış kitabı kapalı:

| | |
|---|---|
| v1 (elle yazılmış değerlendirme) | 13,5/40 (%33,8) |
| **v2 (NNUE)** | **26,5/40 (%66,2)** |
| Elo farkı | **+117** |
| v2 için G/B/Y | 18 / 17 / 5 |

40 oyunda hata payı kabaca ±85 elo, yani büyüklük yaklaşık. Ama yön net:
belirleyici 23 oyunun 18'ini v2 kazandı.

**Bu sonucun anlamı:** v2 aynı sürede v1'den **2 hamle daha sığ** arıyor
(3 sn'de derinlik 14 vs 16). Buna rağmen kazanıyor. Yani bu motorda
değerlendirme kalitesi, iki hamlelik arama derinliğinden daha değerli.

### Hız

NNUE değerlendirme elle yazılandan pahalı, iki şey bunu telafi ediyor:

| Aşama | Derinlik (3 sn) | Derinlik 14 süresi |
|---|---|---|
| Naif (her seferinde sıfırdan) | — | oynanamaz |
| Artımlı accumulator | 12 | — |
| + katmanlarda vektör komutları (SIMD) | 14 | 2918 ms |
| + int16 accumulator, vektörleştirilmiş özellik güncellemesi | **16** | **1565 ms** |

Artımlı güncelleme olmadan v2 oynanabilir değildi. Son adımda accumulator
`int32` yerine `int16` tutuluyor (kopyalanan veri yarıya indi) ve özellik
ekleme/çıkarma döngüsü ile clipped ReLU vektörleştirildi: **1,86x hızlanma,
aynı sürede 2 ply daha derin**.

Bu adımın doğruluğu üç ayrı yerden kontrol edildi: `nnuecheck` 300/300 birebir,
`accverify` 7 milyon düğüm birebir, ve `bench` çıktısında **düğüm sayıları her
derinlikte tıpatıp eşit** — yani değerlendirme bit düzeyinde değişmedi, sadece
hızlandı.

Ölçüm (60 oyun, hamle başına 100 ms, aynı ağ, tek değişken hız):

| | |
|---|---|
| Hızlandırılmış sürüm | 43,5/60 (%72,5) — 30 G / 27 B / 3 Y |
| Elo farkı | **+168** |

## Ağ denemesi: ölçtük, fark yok

Stockfish'in ağ deposundaki 15 ağ bu mimariyle (HalfKP, dosya boyutu tam
21.022.697 bayt) uyumlu. Hepsi indirilip yüklenebildiği doğrulandı, sonra her
biri kullandığımız ağa karşı 20 maç oynadı. Çıkan tablo ±90 elo arasına
dağıldı — ama 20 maçın istatistiksel gürültüsü de tam olarak bu genişlikte,
yani tablo hiçbir şey söylemiyor.

Ön elemede en iyi görünen ağ (`nn-04cf2b4ed1da`) 60 maça çıkarıldı:
**%45, −35 elo**. Yani "+89" tesadüftü.

**Sonuç: ağ değiştirilmedi.** Bu, kaydedilmeye değer bir negatif sonuç —
ön elemedeki ilk tabloya güvenip ağı değiştirmek, "geliştirdim" diyerek
motoru kötüleştirmek olurdu.
