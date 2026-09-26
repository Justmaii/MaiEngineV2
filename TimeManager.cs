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
        if (timeLeft <= 0) return (50, 100);

        // Süre kontrolü hamle sayılıysa payı ona göre böl; değilse oyunun
        // kalanını kabaca 25 hamle say.
        int divisor = movesToGo > 0 ? Math.Min(movesToGo, 30) : 25;
        int soft = timeLeft / divisor + increment * 3 / 4;

        // Hard, soft'un 1,5 katı olabilir ama kalan sürenin dörtte birini
        // geçmesin: tek bir hamlede saati yakmak, sonraki yirmi hamleyi mahveder.
        // (3 kat denendi ve 58 oyunda kazanç vermedi — bkz. README.)
        int hard = Math.Min(soft * 3 / 2, timeLeft / 4);

        // Gecikme payı: arayüze cevap ulaşması da zaman alır.
        int safety = Math.Max(20, timeLeft / 50);
        hard = Math.Min(hard, Math.Max(10, timeLeft - safety));
        soft = Math.Min(soft, hard);

        return (Math.Max(10, soft), Math.Max(15, hard));
    }
}
