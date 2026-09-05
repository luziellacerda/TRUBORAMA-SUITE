using TurboBoxManager.Catalog;

namespace TurboBoxManager.CatalogVerifier;

internal static class CatalogPopularityVerifier
{
    internal static void Run(CatalogRepository repository)
    {
        var expectedCategories = new[]
        {
            "playstation-3", "playstation-4", "playstation-5",
            "xbox", "xbox-360", "xbox-one", "xbox-series", "nintendo-switch", "windows"
        };
        Require(expectedCategories.ToHashSet(StringComparer.Ordinal).SetEquals(
            CatalogPopularityOrder.Priorities.Keys), "As nove plataformas solicitadas devem ter prioridades.");

        var original = repository.Items.ToDictionary(item => item.Id, item => item.Order);
        foreach (var category in repository.Categories)
        {
            var source = repository.Items.Where(item => item.CategoryId == category.Id).ToArray();
            var all = repository.Query(category.Id, "", 1, Math.Max(1, source.Length));
            Require(all.TotalItems == source.Length, $"A ordenação alterou a contagem de {category.Id}.");
            Require(all.Items.Select(item => item.Id).Distinct().Count() == source.Length,
                $"A ordenação repetiu ou perdeu itens de {category.Id}.");
            Require(all.Items.All(item => ReferenceEquals(item, repository.FindById(item.Id))),
                "A ordenação deve conservar as instâncias com seus descriptors, sem recriar downloads.");

            if (CatalogPopularityOrder.Priorities.TryGetValue(category.Id, out var priorities))
            {
                foreach (var id in priorities.Keys)
                    Require(source.Any(item => item.Id == id), $"Prioridade sem jogo em {category.Id}: {id}.");
                var expected = priorities.OrderBy(pair => pair.Value).Select(pair => pair.Key);
                Require(all.Items.Take(priorities.Count).Select(item => item.Id).SequenceEqual(expected),
                    $"Sequência de destaques incorreta em {category.Id}.");
                var first = all.Items[0];
                Require(repository.Query(category.Id.ToUpperInvariant(), first.Title, 1, 5)
                    .Items.Any(item => item.Id == first.Id), "Busca e categoria case-insensitive devem continuar válidas.");
            }

            var remainingExpected = source.Where(item => CatalogPopularityOrder.GetRank(item) == int.MaxValue)
                .OrderBy(item => item.Order).ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase);
            var remainingActual = all.Items.Where(item => CatalogPopularityOrder.GetRank(item) == int.MaxValue);
            Require(remainingActual.Select(item => item.Id).SequenceEqual(remainingExpected.Select(item => item.Id)),
                $"A ordem anterior dos jogos sem destaque deve ser preservada: {category.Id}.");

            var paged = new List<string>();
            const int pageSize = 5;
            for (var page = 1; page <= Math.Max(1, (source.Length + pageSize - 1) / pageSize); page++)
                paged.AddRange(repository.Query(category.Id, "", page, pageSize).Items.Select(item => item.Id));
            Require(paged.SequenceEqual(all.Items.Select(item => item.Id)), "A paginação deve respeitar toda a sequência.");
        }
        Require(repository.Items.All(item => item.Order == original[item.Id]), "O catálogo original não pode ser reescrito.");
        var known = repository.Items.First(item => CatalogPopularityOrder.GetRank(item) == 0);
        Require(CatalogPopularityOrder.GetRank(new CatalogItem { Id = known.Id, CategoryId = "system-tools" }) == int.MaxValue,
            "Uma prioridade não pode vazar para outra plataforma.");
        Require(CatalogPopularityOrder.GetRank(new CatalogItem { Id = "future-id", CategoryId = "windows" }) == int.MaxValue,
            "Itens futuros devem continuar disponíveis depois dos destaques.");
        Console.WriteLine("PASS: 104 destaques em nove plataformas; busca, paginação, identidade e ordem restante preservadas.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
