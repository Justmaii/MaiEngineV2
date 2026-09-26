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

## Sonuç: v1'e karşı +206 elo

64 oyun, hamle başına 150 ms, iki tarafta da açılış kitabı kapalı:

| | |
|---|---|
| v1 (elle yazılmış değerlendirme) | 15,0/64 (%23,4) |
| **v2 (bugünkü hâli)** | **49,0/64 (%76,6)** |
| Elo farkı | **+206** (%95 güven: +130 .. +281) |
| v2 için G/B/Y | 39 / 20 / 5 |

Bu sayı doğrudan ölçüldü. Gün içinde ölçülen tek tek kazançlar (+117 NNUE,
+168 accumulator hızı, +114 yasallık testi, +83 lazy SMP) farklı rakiplere
karşı alındığı için **toplanamaz**; v1'e karşı gerçek fark yukarıdaki tek
maçtan gelir.

## İlk NNUE ölçümü: +117 elo


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

## SEE: pahalıyken zarardı, ucuzlayınca kazanç

SEE (static exchange evaluation) bir alışın, o karede yaşanacak bütün
alışveriş bittiğinde kaç santipiyon bıraktığını arama yapmadan hesaplar.
İlk sürümü mailbox tahtayla yazılmıştı: her çağrıda 64 kare kopyalanıyor ve
saldıran yön yön aranıyordu. 150 oyunda **%49** verdi — hesap doğruydu ama
maliyeti kazancını yiyordu, o yüzden kapalı bırakılmıştı.

Bitboard sürümü aynı cevabı veriyor, ama tahta kopyalamıyor: doluluk
maskesinden taş çıktıkça arkasındaki uzun menzilli taş kendiliğinden
devreye giriyor (x-ray), ayrı koda gerek kalmıyor.

| | |
|---|---|
| 176 oyun, 150 ms | 99,5/176 (%56,5) |
| Elo farkı | **+46** (%95 güven: +13 .. +79) |

Artık varsayılan olarak açık. Doğrulama: `seecompare` eski mailbox sürümüyle
**1.272.951 alışta** birebir aynı sonucu verdi, `seecheck` 6 birim testi geçiyor.

## Lazy SMP: çok çekirdekli arama

Yardımcı iş parçacıkları aynı pozisyonu bağımsız arar. Aralarındaki tek bağ
paylaşılan transposition table: biri bir dalı çözünce sonucu tabloya yazar,
diğerleri o dalı ucuza geçer. Kimse kimseye iş dağıtmaz — "lazy" adı buradan.

Bunun için transposition table kilitsiz hale getirildi. Kayıt artık iki
64-bit alan: paketlenmiş veri ve *anahtar XOR veri*. Bir okuyucu yarı yazılmış
bir kaydı görürse XOR tutmaz ve kayıt yokmuş sayılır — kilit maliyeti olmadan
bozuk kayıt kullanma ihtimali sıfır. Yan etki olarak kayıt 40 bayttan 16 bayta
indi, yani aynı bellekte 2,5 kat daha çok pozisyon tutuluyor.

| | |
|---|---|
| 2 iş parçacığı, 64 oyun | 39,5/64 (%61,7) |
| Elo farkı | **+83** (%95 güven: +24 .. +142) |
| Gezilen düğüm | ~1,8x |

Ölçüm iki çekirdekli bir makinede yapıldı: sıra kimdeyse sadece o taraf
düşündüğü için iki iş parçacıklı taraf iki çekirdeği, tek iş parçacıklı taraf
bir çekirdeği kullandı — yani ölçülen şey tam olarak "aynı sürede iki çekirdek,
bir çekirdeğe karşı". Daha çok çekirdekte kazancın artması beklenir ama bu
ölçülmedi, iddia edilmiyor.

Varsayılan hâlâ tek iş parçacığı; turnuva arayüzü `setoption name Threads`
ile ayarlar.

### Doğrulama

Yarış koşulu sessizdir: bozuk bir kayıt yanlış hamle ürettirir ve bu maçta
"kötü oynadı" gibi görünür. O yüzden `smp` modu her pozisyonda dönen hamlenin
gerçekten legal olduğunu kontrol ediyor, ve 64 oyunluk maç boyunca (yaklaşık
5.000 hamle, iki iş parçacığı) tek bir legal olmayan hamle ya da çökme olmadı.
Tek iş parçacıklı düğüm sayıları değişiklikten önce ve sonra birebir aynı.

## Bitboard'lar: kazanç beklenen yerde değildi

8x8 dizi (mailbox) korundu, yanına 64-bit maskeler eklendi: her renk ve her taş
türü için bir maske, artı doluluk maskesi. Tahtaya yazan **tek** bir fonksiyon
var (`SetSquare`), bitboard'lar orada güncelleniyor — yani ikisi ayrışamaz, ve
yine de perft ağacında her düğümde karşılaştırılıyor.

Kayan taş saldırıları "klasik" yöntemle: her kare ve yön için hazır maske,
doluluk ile kesiştir, ilk engeli bul, arkasını sil.

| Adım | Ölçüm | Sonuç |
|---|---|---|
| `IsSquareAttacked` bitboard'a çevrildi | derinlik 16: 2479 → 2456 ms | **~%1, yok sayılır** |
| Yasallık testi hamleyi oynamadan yapılıyor | derinlik 16: 2479 → 1647 ms | **+114 elo** |
| Hamle üretimi bitboard'a çevrildi | 60 maç: %50,8 | **+6 elo — ölçülemez, geri alındı** |

**Asıl bulgu ikinci satırda.** Eski kod bir hamlenin legal olup olmadığını
anlamak için hamleyi gerçekten oynuyordu: `MakeMove` → şah kontrolü →
`UnmakeMove`. Ama `MakeMove` Zobrist anahtarını, piyon anahtarını, tekrar
geçmişini ve en pahalısı **NNUE accumulator'ını** da güncelliyor — oysa test
edilen hamlelerin çoğu hiç oynanmayacak. Yeni `IsMoveLegal` sadece kareleri
(ve onlara bağlı bitboard'ları) geçici değiştirip soruyor, geri alıyor:

| | önce | sonra |
|---|---|---|
| perft (pozisyon 5, derinlik 4) | 809 ms | **126 ms** (6,4x) |
| bench derinlik 14 | 1144 ms | **725 ms** |
| 3 saniyede ulaşılan derinlik | 16 | **17** |
| 60 oyun, 100 ms | — | **39,5/60 (%65,8) → +114 elo** |

Düğüm sayıları birebir aynı kaldı: davranış değişmedi, sadece boşa yapılan iş
kalktı. Yani bitboard'ların kazancı "maskeler daha hızlı" değil, **"artık
hamleyi oynamadan yasallığını sorabiliyoruz"** oldu.

Üçüncü satır neden geri alındı: hamle üretimi bitboard'la %6 hızlandı, ama
üretim sırası değişince hamle sıralamasının eşitlik kırma düzeni bozuldu ve
arama aynı derinlik için ~%25 daha çok düğüm gezdi. İkisi birbirini götürdü
(60 maçta %50,8). Kazanç olmadığı ölçüldüğü için karmaşıklık da tutulmadı —
sıralama üretim sırasından bağımsız hâle getirilirse yeniden denenebilir.

### Bu adımın doğrulamaları

| Test | Ne yapıyor | Sonuç |
|---|---|---|
| `bbcheck` | Hızlı kayan taş maskesini yavaş referansla karşılaştırır | **512.384 maske birebir** |
| `attackcheck` | Bitboard saldırı sorgusunu eski mailbox sürümüyle, 64 kare x 2 renk | **23.800.192 karşılaştırma aynı** |
| `perft` içinde | Bitboard'lar mailbox ile tutuyor mu + ucuz yasallık testi pahalı referansla aynı mı | her düğümde, 27 test OK |

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
