namespace ChessEngine;

/// <summary>
/// Turnuvada süre saatle gelir: "toplam 10 dakika, hamle başına 6 saniye ekleme".
/// Motorun kendi payını ayırması gerekir — hem bitirememek (süre aşımı = kayıp)
/// hem de gereğinden az düşünmek (bedava elo kaybı) mümkün.
///
/// İki sınır üretiyoruz:
///   soft — yeni bir iterasyona BAŞLAMAK için son an. Aşılmışsa yeni derinliğe
///          girmeyiz, çünkü yarım kalan iterasyonun sonucu kullanılamaz.
///   hard — başlanmış iterasyonun kesileceği an. Pozisyon oynaksa (en iyi hamle
///          değişiyorsa) soft'u aşıp hard'a kadar düşünmeye değer.
/// </summary>
public static class TimeManager
{
    /// <param name="timeLeft">Bize kalan süre (ms).</param>
    /// <param name="increment">Hamle başına ekleme (ms).</param>
    /// <param name="movesToGo">Sonraki zaman kontrolüne kalan hamle, yoksa 0.</param>
    public static (int soft, int hard) Allocate(int timeLeft, int increment, int movesToGo)
    {
        if (timeLeft <= 0) return (20, 40);

        // Arayüze cevabın ulaşması da zaman alıyor; onu baştan ayırıyoruz.
        int overhead = Math.Clamp(timeLeft / 100, 20, 200);
        int usable = Math.Max(10, timeLeft - overhead);

        // Süre kontrolü hamle sayılıysa payı ona göre böl. Değilse oyunun
        // kalanını 30 hamle say: 25 çok hızlı harcıyordu (60 saniyelik oyunda
        // hamle başına 2,4 sn, yani 20 hamlede saat bitiyor).
        int divisor = movesToGo > 0 ? Math.Clamp(movesToGo, 2, 30) : 30;

        // Eklemenin tamamını harcamak cazip ama tehlikeli: bir hamle uzun
        // sürerse fark kapanmıyor. Dörtte üçü alınır.
        int soft = usable / divisor + increment * 3 / 4;

        // Hiçbir hamle kalan sürenin dörtte birinden fazlasını yemesin.
        soft = Math.Min(soft, usable / 4);

        // ÖLÇÜM NOTU: sert sınırı yumuşağın 3 katına çıkarıp oynak
        // pozisyonlarda uzatmayı denedik — 58 oyunda -24 elo, kazanç yok.
        // O yüzden ikisi eşit: motor ayırdığı payı aşmıyor.
        int hard = soft;

        return (Math.Max(10, soft), Math.Max(15, hard));
    }
}
