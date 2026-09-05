using System.Collections.Frozen;

namespace TurboBoxManager.Catalog;

/// <summary>
/// Editorial popularity snapshot, researched on 2026-09-05; not live player telemetry.
/// Only display order changes. IDs, authority, descriptors and download policy stay untouched.
/// See docs/ORDEM-POPULARIDADE-20260905.md for sources and exact catalog mappings.
/// </summary>
internal static class CatalogPopularityOrder
{
    internal static FrozenDictionary<string, FrozenDictionary<string, int>> Priorities { get; } =
        new Dictionary<string, FrozenDictionary<string, int>>(StringComparer.OrdinalIgnoreCase)
        {
            ["playstation-3"] = Rank(
                "f509d43b3a96eea44b343a70b451e7af", // Minecraft - PlayStation 3 Edition.ps3
                "c1677fb00ce8f95bb336803299527fb0", // Grand Theft Auto 4.ps3
                "235e17b536ff05a3e2e60c901db0c159", // GOW3 BCES00510.ps3
                "7bf66984c9db0b804535cdfc7f126c0b", // Metal Gear Solid 4 BR.ps3
                "9f6a9bbe53542ddb0a0f58898512c577", // Demon's Souls.ps3
                "625ec22f3b9f387d1464cd68a3335197", // Shadow of the Colossus.ps3
                "41bec0114f44a0b122371b1913cfc0b3", // inFamous.ps3
                "19fe61ebb52620c4a70e4ff86d435fac", // God_of_War Ascension.ps3
                "18783d8b1fae98e712ec035e989f6c67", // Skate 3.ps3
                "f7c35f67f7c5c98d1d3f120db3c34552", // Asuras Wrath.ps3
                "1572c579c2db96fc162c340132ede4bc", // Dante's Inferno.ps3
                "3fbe144fa3ad2be85d6d9b96dadc55c4" // Devil May Cry 4 (USA) (En,Ja,Fr,De,Es,It)
            ),
            ["playstation-4"] = Rank(
                "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c516", // God of War
                "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c511", // Spider-Man R
                "3e1865367d9cd2462edab290e30e9926", // Bloodbourne
                "dea65db365776f77619772f1ba72508a", // assassin's creed odyssey
                "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c514", // Detroit Become Human
                "7f06a058b720846a4eeff8269003eb77", // Days gone
                "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c501", // Assassin's Creed 4 Black Flag
                "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c520", // Tekken 7
                "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c513", // Dead Cells
                "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c505", // No Mans Sky
                "840e22d2c2e339db35feb5dc8e6d4bca", // Crash bandicoot 4
                "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c519" // Street Fighter V
            ),
            ["playstation-5"] = Rank(
                "c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8", // Clair Obscure 33
                "f2a3b4c5d6e7f8a9b0c1d2e3f4a5b6c7", // God of War Ragnarok
                "a7b8c9d0e1f2a3b4c5d6e7f8a9b0c1dd", // Spider-Man 2
                "ea29a5ea4392df2f595db4f77592920d", // Black myth wukong
                "b4c5d6e7f8a9b0c1d2e3f4a5b6c7d8e9", // Hogwarts Legacy
                "ac08292caa33236dc6e11422a4e51927", // Assasins creed Shadows
                "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d7", // Resident Evil 4 Remake
                "d2e3f4a5b6c7d8e9f0a1b2c3d4e5f6a7", // The Last of Us Part I
                "d6e7f8a9b0c1d2e3f4a5b6c7d8e9f0a1", // Horizon Forbidden West
                "e1f2a3b4c5d6e7f8a9b0c1d2e3f4a5b6", // Ghost of Tsushima
                "c7d8e9f0a1b2c3d4e5f6a7b8c9d0e1f2", // Ratchet & Clank Rift Apart
                "b8c9d0e1f2a3b4c5d6e7f8a9b0c1d2ee" // Stellar Blade
            ),
            ["xbox"] = Rank(
                "ade6bf70ed8eb08f47ef3571ea87182a", // Halo - Combat Evolved
                "b36e23a25251bc2a977d18336f1d857f", // Halo 2
                "793420d22886c0b197e6b3ae490e0e5c", // Burnout 3 Takedown
                "21baa8c1afc524bf9b237438c99c6ca3", // Forza Motorsport
                "88c1d4b29944adeb1dbf8ebb9888e85a", // Conker Live and Reloaded
                "d8185eaa6b648cde6253f61a8afa7412", // Def Jam Fight for NY
                "5ad95390b1f33a017bd7c3dc3f292c99", // Doom 3
                "342598946c06419318b6cf4aa818c11a", // Burnout Revenge
                "3e496deb4abb4a0c6fdc3d6c1d2569e6", // Max Payne
                "cc6afce63c2eb29e71e0c93f20a447f7", // Max Payne 2
                "570f3d9a5accf905bff123ffda735827" // 007 Nightfire
            ),
            ["xbox-360"] = Rank(
                "2e4cbf194e54ff111debb4141a47b9d7", // Halo 3 (Brazil) (En,Ja,Fr,De,Es,It,Pt,Zh,Ko)
                "31d2cd9059f61edd85a45089fc0d6b06", // Red Dead Redemption (World) (En,Fr,De,Es,It)
                "ef1a491dabedd5fe7ef7c4757074c08b", // Gears of War 3 (World) (En,Fr,Pt,Zh,Ko,Pl,Ru,Cs,Hu)
                "e444b2fe070fcccb0cdb35d5bb58822f", // Gears of War 2 (USA) (En,Fr,De,Es,It,Zh,Ko,Pl,Ru,Cs,Hu)
                "ccb5d0d143ff250512622369df95cfa1", // Forza Horizon (USA) (En,Ja,Fr,De,Es,It,Pt,Zh,Pl,Ru)
                "0bd94d15bf5b3444e5a34cc8852afed9", // Batman Arkham City
                "78627a688b5c9ea45188aa806560b10f", // Assassins Creed 2
                "dfcd3f3b902d4d862ed38fd4b1dca11b", // Forza Horizon 2
                "ec46d6412e6be0955cbe3bc7b8e571fb", // Midnight Club - Los Angeles (USA) (En,Fr,Es)
                "850be59518fbf7f9c71a385d6c32e17a", // Metal Gear Solid V - Ground Zeroes (USA, Europe) (En,Ja,Fr,De,Es,It,Pt,Ru)
                "e2d63c1119f4aba660c7a16431f7d5df" // Dante's Inferno (USA) (En,Fr,Es)
            ),
            ["xbox-one"] = Rank(
                "d21e2f02a2516c10fd48b85696d17ed4", // Grand Theft Auto V
                "3c6b5a1a4aa0058096430b62a59bc94b", // Cuphead
                "bf461d8776725cce4ff251ac0ff3097c", // Doom Eternal
                "98ce47e718f6ec4d3f6c87f556e5a2a8", // ori blind forest
                "92d774eae11dc642cafe1065d52694b6", // Batman Arkham knight
                "cc4928753dda7997914f29a7fc18428a", // Sunset overdrive
                "184c571b640d0d2e4f0ea54eb32e941a", // Mortal Kombat XL
                "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d9", // Killer Instinct
                "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5db", // Ryse: Son of Rome
                "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5de", // Spyro
                "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5dd", // South Park: The Stick of Truth
                "bbe05d52b50342ce5a5a9bdb91935f09" // Red Dead Redemption
            ),
            ["xbox-series"] = Rank(
                "3614c8d0b3c33a426afa134478748a7b", // Elden Ring
                "ecdb2fe985d557eaebaa7bc989c891b6", // Forza 5
                "79c52e3f3d9e5020faaa9a256183192f", // Cyber Punk
                "995bd1da6ee5eccf0d7d430dba6295e7", // if takes two
                "602149579a65bf7495fc954260f14530", // Split fiction
                "ba3703f53f2d6150f397f99eca54f583", // Gear of  war 5
                "06e476a764ffc46615e1b252fcf69a01", // Sekiro
                "793bb2aa74eaec6b4d49a40fc1b12409", // Palworld
                "1c655216ea2234604621d3aa54abef0d", // Star Field
                "2bf53412ea1747fcbc1c297394d636f9", // Mortal kombat 11
                "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c601", // Age of Empires IV
                "5c61fcdaf7d9a6a92d4d3801bdef66d7" // Metal gear 5
            ),
            ["nintendo-switch"] = Rank(
                "57c6c762fb0d62b1322780012a9c1a16", // Mario Kart 8 Deluxe [0100152000022000][v0]
                "e7f0ba9833cef82112794ad5415fbcfa", // Animal Crossing New Horizons [01006F8002326000][v0]
                "254a64690e1ef9313799ece019e9aaf6", // Super Smash Bros. Ultimate
                "a5fd31a98a0bd1e9b5130e54217f78c5", // The Legend of Zelda Breath of the Wild [01007EF00011E000] [v786432] (1G+1U+2D)
                "46cf298511752a961c1a1c9cbbf61662", // Super Mario Odyssey (World)
                "830ff16f2e1fe831800e545b3fcabae5", // Pokemon Scarlet [0100A3D008C5C000][v0]
                "e850bbd92fc7c140e61798d61d0d75f3", // Pokemon Sword [JOGO BASE + UPDATE + DLCs]-005
                "9f6009d445a61f0e87dac50f1ce713c0", // Pokemon Shield [Jogo Base + Update v1.3.2 + DLCs]-002
                "406b5b9df3537e36776d5b78153b259b", // The Legend of Zelda Tears of the Kingdom [0100F2C0115B6000][v0][US](nsw2u.com)
                "cbe0d497ad6dd6a590aeb4ebca980f05" // New Super Mario Bros. U Deluxe [v0][0100EA80032EA000]
            ),
            ["windows"] = Rank(
                "ea887c347dc994e97790aa28545ff34d", // minecraft
                "4bed986220f797749264460c38668a30", // Balatro
                "c48051252aaa529ca4079acfbf1168e2", // Vampire Survivors
                "bbcc1f58a152ced352f70aadac838824", // Cuphead
                "d9c809a61fd286566031ef4b0f8f2382", // Brotato
                "08f0fd464b53c404d2c557effcba1087", // Celeste
                "7bc78042ec4be2d1904c0504a2d8389b", // Streets of Rage 4
                "fedcb824ab03b697aedf9be2970ce6b2", // Sonic Mania
                "5701aa19629ef6bdf8e5e2b2e0b21052", // Blasphemous
                "b92260712cb6df226e5a7d83bca1a245", // Teenage Mutant Ninja Turtles Shredders Revenge
                "e6b5a1503fdbe7483be9372f39176bd7", // Shovel Knight
                "09e90956b4ea35e58a7ac186e56deea9" // UFO 50
            )
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    internal static int GetRank(CatalogItem item) =>
        Priorities.TryGetValue(item.CategoryId, out var ranks)
        && ranks.TryGetValue(item.Id, out var rank)
            ? rank
            : int.MaxValue;

    private static FrozenDictionary<string, int> Rank(params string[] ids) =>
        ids.Select((id, rank) => new KeyValuePair<string, int>(id, rank))
            .ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
}
