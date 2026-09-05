using System.Security.Cryptography;

namespace TurboBoxManager.CatalogVerifier;

internal static class MusicStartupVerifier
{
    internal static void Run()
    {
        var tracks = EmbeddedMusicLibrary.Tracks;
        Require(tracks.Count == 9, "A playlist interna deve conter nove faixas.");
        Require(tracks[0].FileName == "Aperta Start.mp3", "Aperta Start precisa ser a primeira faixa da playlist.");
        Require(tracks.Select(track => track.FileName).Distinct().Count() == tracks.Count, "Faixas duplicadas.");

        using var resource = typeof(EmbeddedMusicLibrary).Assembly.GetManifestResourceStream(tracks[0].ResourceName)
            ?? throw new InvalidOperationException("Aperta Start não foi incorporada.");
        Require(resource.Length == tracks[0].Length, "Tamanho da música incorporada incorreto.");
        Require(Convert.ToHexString(SHA256.HashData(resource)).Equals(tracks[0].Sha256, StringComparison.OrdinalIgnoreCase),
            "Integridade da música incorporada incorreta.");

        // Exhaust every ticket and every non-featured slot: exact, non-flaky verification.
        var counts = new int[tracks.Count];
        for (var ticket = 0; ticket < 100; ticket++)
        {
            for (var slot = 0; slot < tracks.Count - 1; slot++)
            {
                var index = EmbeddedMusicLibrary.SelectStartupIndex(tracks.Count, new FixedDrawRandom(ticket, slot));
                Require(index >= 0 && index < tracks.Count, "Sorteio fora da playlist.");
                counts[index]++;
            }
        }
        Require(counts[0] == 320 && counts.Skip(1).All(count => count == 60),
            "Aperta Start deve ter 40% e cada uma das oito demais, 7,5%.");
        Require(EmbeddedMusicLibrary.SelectStartupIndex(1) == 0, "Playlist com uma faixa deve ser válida.");
        var emptyRejected = false;
        try { _ = EmbeddedMusicLibrary.SelectStartupIndex(0); }
        catch (ArgumentOutOfRangeException) { emptyRejected = true; }
        Require(emptyRejected, "Playlist vazia deve ser rejeitada.");
        for (var attempt = 0; attempt < 32; attempt++)
        {
            var index = EmbeddedMusicLibrary.SelectStartupIndex(tracks.Count);
            Require(index >= 0 && index < tracks.Count, "Sorteio real fora da playlist.");
        }
        Console.WriteLine("PASS: Aperta Start na posição 1; abertura 40%/60% verificada sem teste estatístico instável.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FixedDrawRandom(int ticket, int slot) : Random
    {
        private int _draws;
        public override int Next(int maxValue)
        {
            var value = _draws++ == 0 ? ticket : slot;
            if (value < 0 || value >= maxValue) throw new InvalidOperationException("Sorteio de teste inválido.");
            return value;
        }
    }
}
