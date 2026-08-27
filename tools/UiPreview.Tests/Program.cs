using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Turborama.UiPreview;

if (args.Length != 0)
{
    VerifyGeneratedPackage(args);
    return;
}

const string Commit = "abcdef0123456789abcdef0123456789abcdef01";
const string ManifestSha256 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
var passwordSeed = Encoding.UTF8.GetBytes(
    "Turborama.UiPreview.Tests.PasswordFixture/v2");
var passwordDigest = SHA256.HashData(passwordSeed);
var password = Convert.ToBase64String(passwordDigest);
CryptographicOperations.ZeroMemory(passwordSeed);
CryptographicOperations.ZeroMemory(passwordDigest);
var now = new DateTimeOffset(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);

VerifyCredentialRules(password, now);
VerifyPackageManifest();
VerifyCatalogAndPaths();
VerifySquareLayout();
VerifyProductionIsolation();

Console.WriteLine(
    "PASS: credencial, integridade, catálogo 22/850, caminhos locais, layout quadrado e isolamento verificados.");

static void VerifyGeneratedPackage(string[] arguments)
{
    if (arguments.Length != 3
        || !arguments[0].Equals("--verify-generated", StringComparison.Ordinal)
        || !PreviewBuildInfo.IsCanonicalCommit(arguments[2]))
        throw new InvalidDataException("Argumentos de verificação inválidos.");

    var packageDirectory = LocalAssetPolicy.NormalizeBaseDirectory(arguments[1]);
    var passwordText = Console.ReadLine()
                       ?? throw new InvalidDataException("Senha de verificação ausente.");
    using var password = ToSecureString(passwordText);
    var verification = PreviewCredentialVerifier.VerifyFile(
        Path.Combine(
            packageDirectory,
            PreviewCredentialVerifier.CredentialFileName),
        password,
        arguments[2],
        DateTimeOffset.UtcNow);
    Assert(verification.IsValid,
        "A credencial produzida pelo build foi recusada.");
    PreviewPackageIntegrity.Verify(
        packageDirectory,
        verification.ManifestSha256,
        arguments[2],
        verification.ExpiresAtUtc);
    var catalog = PreviewCatalog.Load(packageDirectory);
    Assert(catalog.Categories.Count == 22 && catalog.Items.Count == 850,
        "O pacote gerado não contém o catálogo completo.");
    Console.WriteLine("PASS: pacote e credencial gerados verificados ponta a ponta.");
}

static void VerifyCredentialRules(string password, DateTimeOffset now)
{
    Assert(PreviewBuildInfo.IsCanonicalCommit(Commit),
        "Commit canônico deveria ser aceito.");
    Assert(!PreviewBuildInfo.IsCanonicalCommit(Commit.ToUpperInvariant()),
        "Commit com casing não canônico deveria falhar.");
    Assert(!PreviewBuildInfo.IsCanonicalCommit("abc"),
        "Commit curto deveria falhar.");

    var payload = CreatePayload(
        password,
        Commit,
        now.AddMinutes(-1),
        now.AddHours(12));
    using (var correct = ToSecureString(password))
    {
        var result = PreviewCredentialVerifier.VerifyPayload(
            payload,
            correct,
            Commit,
            now);
        Assert(result.IsValid
               && result.ExpiresAtUtc == now.AddHours(12)
               && result.ManifestSha256 == ManifestSha256,
            "Senha correta e credencial vigente deveriam ser aceitas.");
    }
    using (var wrong = ToSecureString(password + "!"))
    {
        Assert(!PreviewCredentialVerifier.VerifyPayload(
                payload,
                wrong,
                Commit,
                now).IsValid,
            "Senha incorreta deveria falhar.");
    }
    using (var correct = ToSecureString(password))
    {
        Assert(!PreviewCredentialVerifier.VerifyPayload(
                payload,
                correct,
                "2222222222222222222222222222222222222222",
                now).IsValid,
            "Credencial de outro commit deveria falhar.");
    }

    var expiredPayload = CreatePayload(
        password,
        Commit,
        now.AddHours(-2),
        now.AddMinutes(-1));
    var excessiveLifetime = CreatePayload(
        password,
        Commit,
        now,
        now.AddHours(73));
    var futurePayload = CreatePayload(
        password,
        Commit,
        now.AddMinutes(6),
        now.AddHours(1));
    using (var correct = ToSecureString(password))
    {
        Assert(!PreviewCredentialVerifier.VerifyPayload(
                expiredPayload,
                correct,
                Commit,
                now).IsValid,
            "Credencial expirada deveria falhar.");
        Assert(!PreviewCredentialVerifier.VerifyPayload(
                excessiveLifetime,
                correct,
                Commit,
                now).IsValid,
            "Credencial acima de 72 horas deveria falhar.");
        Assert(!PreviewCredentialVerifier.VerifyPayload(
                futurePayload,
                correct,
                Commit,
                now).IsValid,
            "Emissão além da tolerância deveria falhar.");
    }

    var duplicateJson = Encoding.UTF8.GetString(payload).Replace(
        "{\"schemaVersion\":1,",
        "{\"schemaVersion\":1,\"schemaVersion\":1,",
        StringComparison.Ordinal);
    var unknownJson = Encoding.UTF8.GetString(payload).Replace(
        "{\"schemaVersion\":1,",
        "{\"unknown\":1,\"schemaVersion\":1,",
        StringComparison.Ordinal);
    var nullManifest = Encoding.UTF8.GetString(payload).Replace(
        $"\"manifestSha256\":\"{ManifestSha256}\"",
        "\"manifestSha256\":null",
        StringComparison.Ordinal);
    foreach (var invalidJson in new[] { duplicateJson, unknownJson, nullManifest })
    {
        using var correct = ToSecureString(password);
        Assert(!PreviewCredentialVerifier.VerifyPayload(
                Encoding.UTF8.GetBytes(invalidJson),
                correct,
                Commit,
                now).IsValid,
            "JSON não canônico deveria falhar.");
    }

    CryptographicOperations.ZeroMemory(payload);
    CryptographicOperations.ZeroMemory(expiredPayload);
    CryptographicOperations.ZeroMemory(excessiveLifetime);
    CryptographicOperations.ZeroMemory(futurePayload);
}

static void VerifyPackageManifest()
{
    var testDirectory = Path.Combine(
        Path.GetTempPath(),
        "turborama-ui-preview-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(testDirectory);
    try
    {
        var samplePath = Path.Combine(testDirectory, "sample.bin");
        var sampleBytes = Encoding.UTF8.GetBytes("read-only-preview-fixture");
        File.WriteAllBytes(samplePath, sampleBytes);
        var sampleHash = Convert.ToHexString(SHA256.HashData(sampleBytes))
            .ToLowerInvariant();
        var expiry = new DateTimeOffset(
            2026,
            8,
            28,
            12,
            0,
            0,
            TimeSpan.Zero);
        var manifest = new Dictionary<string, object>
        {
            ["schemaVersion"] = 1,
            ["marker"] = PreviewPackageIntegrity.Marker,
            ["commit"] = Commit,
            ["expiresAtUtc"] = expiry.ToString("O"),
            ["files"] = new[]
            {
                new Dictionary<string, object>
                {
                    ["path"] = "sample.bin",
                    ["bytes"] = sampleBytes.Length,
                    ["sha256"] = sampleHash
                }
            }
        };
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
        File.WriteAllBytes(
            Path.Combine(testDirectory, PreviewPackageIntegrity.ManifestFileName),
            manifestBytes);
        var manifestHash = Convert.ToHexString(SHA256.HashData(manifestBytes))
            .ToLowerInvariant();

        PreviewPackageIntegrity.Verify(
            testDirectory,
            manifestHash,
            Commit,
            expiry);

        var nestedDirectory = Path.Combine(testDirectory, "nested");
        Directory.CreateDirectory(nestedDirectory);
        var nestedReservedPath = Path.Combine(
            nestedDirectory,
            PreviewCredentialVerifier.CredentialFileName);
        File.WriteAllBytes(nestedReservedPath, [0x1]);
        AssertThrows<InvalidDataException>(() => PreviewPackageIntegrity.Verify(
            testDirectory,
            manifestHash,
            Commit,
            expiry));
        File.Delete(nestedReservedPath);
        Directory.Delete(nestedDirectory);

        sampleBytes[0] ^= 0x1;
        File.WriteAllBytes(samplePath, sampleBytes);
        AssertThrows<InvalidDataException>(() => PreviewPackageIntegrity.Verify(
            testDirectory,
            manifestHash,
            Commit,
            expiry));
        CryptographicOperations.ZeroMemory(sampleBytes);
        CryptographicOperations.ZeroMemory(manifestBytes);
    }
    finally
    {
        Directory.Delete(testDirectory, recursive: true);
    }
}

static void VerifyCatalogAndPaths()
{
    var root = FindRepositoryRoot();
    var catalog = PreviewCatalog.Load(root);
    Assert(catalog.Categories.Count == 22,
        "O catálogo precisa conter 22 categorias.");
    Assert(catalog.Items.Count == 850,
        "O catálogo precisa conter 850 itens.");
    Assert(catalog.Categories.Sum(category => category.Items.Count) == 850,
        "Todos os itens precisam pertencer a uma categoria.");
    Assert(catalog.Categories.All(category =>
            File.Exists(category.IconPath)
            && File.Exists(category.BackgroundVideoPath)),
        "Ícones e vídeos de categoria precisam ser locais.");
    Assert(catalog.Items.All(item =>
            File.Exists(item.ImagePath)
            && (item.VideoPath is null || File.Exists(item.VideoPath))),
        "Capas e vídeos de itens precisam ser locais.");

    _ = LocalAssetPolicy.ResolvePackageFile(
        root,
        "Assets/Catalog/catalog.json",
        16 * 1024 * 1024);
    foreach (var invalidPath in new[]
             {
                 "../catalog.json",
                 "Assets/../catalog.json",
                 "/Assets/Catalog/catalog.json",
                 "Assets\\Catalog\\catalog.json",
                 "Assets/Catalog/catalog.json:stream",
                 "Assets//Catalog/catalog.json"
             })
    {
        AssertThrows<SecurityException>(() => LocalAssetPolicy.ResolvePackageFile(
            root,
            invalidPath,
            16 * 1024 * 1024));
    }
}

static void VerifySquareLayout()
{
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        try
        {
            foreach (var size in new[]
                     {
                         new Size(1024, 768),
                         new Size(1366, 768),
                         new Size(1920, 1080),
                         new Size(2560, 1080)
                     })
            {
                var child = new Border();
                var host = new SquareMediaHost { Child = child };
                host.Measure(size);
                host.Arrange(new Rect(0, 0, size.Width, size.Height));
                Assert(Math.Abs(child.RenderSize.Width - child.RenderSize.Height) < 0.01,
                    "O contêiner de vídeo precisa permanecer quadrado.");
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    if (!thread.Join(TimeSpan.FromSeconds(30)))
        throw new TimeoutException("O teste de layout WPF não concluiu.");
    if (failure is not null)
        throw new InvalidOperationException("O layout quadrado falhou.", failure);
}

static void VerifyProductionIsolation()
{
    var root = FindRepositoryRoot();
    var previewProject = File.ReadAllText(Path.Combine(
        root,
        "tools",
        "UiPreview",
        "Turborama.UiPreview.csproj"));
    Assert(!previewProject.Contains("<ProjectReference", StringComparison.Ordinal)
           && !previewProject.Contains("<PackageReference", StringComparison.Ordinal)
           && !previewProject.Contains("<Reference ", StringComparison.Ordinal),
        "A prévia não pode ter dependências de projeto ou pacote.");

    var previewSourceDirectory = Path.Combine(root, "tools", "UiPreview");
    var sources = Directory.EnumerateFiles(
            previewSourceDirectory,
            "*.cs",
            SearchOption.AllDirectories)
        .Where(path =>
            !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        .Select(File.ReadAllText)
        .ToArray();
    var forbiddenSourceTokens = new[]
    {
        "PreviewStoreLauncher",
        "AuthorizedStoreContext",
        "CatalogDownloadService",
        "SuiteLicensing",
        "System.Net.",
        "HttpClient",
        "WebView",
        "Process.Start",
        "Microsoft.Win32.Registry",
        "FileAccess.Write",
        "FileMode.Create",
        "Directory.CreateDirectory",
        "Directory.Delete(",
        "Directory.Move(",
        "File.WriteAll",
        "File.Delete(",
        "File.Move("
    };
    foreach (var token in forbiddenSourceTokens)
    {
        Assert(sources.All(source => !source.Contains(token, StringComparison.Ordinal)),
            $"Token proibido encontrado na prévia: {token}");
    }

    var referencedAssemblies = typeof(PreviewBuildInfo).Assembly
        .GetReferencedAssemblies()
        .Select(reference => reference.Name ?? string.Empty)
        .ToArray();
    Assert(referencedAssemblies.All(name =>
            !name.Equals("Turborama", StringComparison.OrdinalIgnoreCase)
            && !name.Equals("SharpCompress", StringComparison.OrdinalIgnoreCase)
            && !name.StartsWith("System.Net", StringComparison.OrdinalIgnoreCase)),
        "O assembly da prévia não pode referenciar o cliente ou bibliotecas de rede.");

    var typeNames = typeof(PreviewBuildInfo).Assembly
        .GetTypes()
        .Select(type => type.FullName ?? string.Empty)
        .ToArray();
    Assert(typeNames.All(name =>
            !name.Contains("StoreWindow", StringComparison.Ordinal)
            && !name.Contains("Licensing", StringComparison.Ordinal)
            && !name.Contains("Download", StringComparison.Ordinal)),
        "O assembly da prévia contém uma superfície mutável.");

    var appXaml = File.ReadAllText(Path.Combine(root, "App.xaml"));
    var loginCode = File.ReadAllText(Path.Combine(root, "PremiumLoginWindow.xaml.cs"));
    var mainProject = File.ReadAllText(Path.Combine(root, "TurboBoxManager.csproj"));
    var licensingSources = Directory.EnumerateFiles(
            Path.Combine(root, "Licensing"),
            "*.cs",
            SearchOption.AllDirectories)
        .Select(File.ReadAllText)
        .ToArray();
    Assert(appXaml.Contains(
            "StartupUri=\"PremiumLoginWindow.xaml\"",
            StringComparison.Ordinal),
        "O aplicativo principal precisa continuar no login licenciado.");
    Assert(!loginCode.Contains("UiPreview", StringComparison.OrdinalIgnoreCase)
           && licensingSources.All(source =>
               !source.Contains("UiPreview", StringComparison.OrdinalIgnoreCase)),
        "Produção não pode conter entrada para a prévia.");
    foreach (var requiredExclusion in new[]
             {
                 "<Compile Remove=\"tools\\**\\*.cs\" />",
                 "<Page Remove=\"tools\\**\\*.xaml;",
                 "<ApplicationDefinition Remove=\"tools\\**\\App.xaml;",
                 "<Resource Remove=\"tools\\UiPreview\\**\\*\" />",
                 "<Content Remove=\"tools\\UiPreview\\**\\*;",
                 "<None Remove=\"tools\\UiPreview\\**\\*;"
             })
    {
        Assert(mainProject.Contains(requiredExclusion, StringComparison.Ordinal),
            "O projeto principal precisa excluir todos os itens da prévia.");
    }
}

static byte[] CreatePayload(
    string password,
    string commit,
    DateTimeOffset issuedAt,
    DateTimeOffset expiresAt)
{
    var saltInput = Encoding.UTF8.GetBytes(
        $"salt:{commit}:{issuedAt:O}:{expiresAt:O}");
    var salt = SHA256.HashData(saltInput);
    var passwordBytes = Encoding.UTF8.GetBytes(password);
    var passwordHash = Rfc2898DeriveBytes.Pbkdf2(
        passwordBytes,
        salt,
        PreviewCredentialVerifier.Pbkdf2Iterations,
        HashAlgorithmName.SHA256,
        32);
    try
    {
        return JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["schemaVersion"] = 1,
            ["purpose"] = PreviewBuildInfo.Purpose,
            ["commit"] = commit,
            ["issuedAtUtc"] = issuedAt.ToUniversalTime().ToString("O"),
            ["expiresAtUtc"] = expiresAt.ToUniversalTime().ToString("O"),
            ["iterations"] = PreviewCredentialVerifier.Pbkdf2Iterations,
            ["salt"] = Convert.ToBase64String(salt),
            ["passwordHash"] = Convert.ToBase64String(passwordHash),
            ["manifestSha256"] = ManifestSha256
        });
    }
    finally
    {
        CryptographicOperations.ZeroMemory(saltInput);
        CryptographicOperations.ZeroMemory(salt);
        CryptographicOperations.ZeroMemory(passwordBytes);
        CryptographicOperations.ZeroMemory(passwordHash);
    }
}

static SecureString ToSecureString(string value)
{
    var secure = new SecureString();
    foreach (var character in value)
        secure.AppendChar(character);
    secure.MakeReadOnly();
    return secure;
}

static string FindRepositoryRoot()
{
    var current = new DirectoryInfo(AppContext.BaseDirectory);
    while (current is not null)
    {
        if (File.Exists(Path.Combine(current.FullName, "TurboBoxManager.csproj")))
            return current.FullName;
        current = current.Parent;
    }
    throw new DirectoryNotFoundException("Raiz do repositório não encontrada.");
}

static void AssertThrows<TException>(Action action)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }
    throw new InvalidDataException(
        $"Era esperada a exceção {typeof(TException).Name}.");
}

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidDataException(message);
}
