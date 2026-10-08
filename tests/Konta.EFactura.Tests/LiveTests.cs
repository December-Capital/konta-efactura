namespace Konta.EFactura.Tests;

/// <summary>
/// Calls SFS's test service for real. Skipped unless an API user is given, in the environment or
/// in the git-ignored <c>.env</c> at the repository root:
/// <c>KONTA_EFACTURA_USER</c>, <c>KONTA_EFACTURA_PASSWORD</c>, and <c>KONTA_EFACTURA_PROXY</c>
/// (for example <c>socks5://127.0.0.1:1080</c>) when this computer's address is not the one SFS
/// registered. Each exchange is written to <c>fixtures/live/</c>, also git-ignored, without the
/// password; read a fixture before copying it anywhere tracked.
/// </summary>
public sealed class LiveTests
{
    [LiveFact]
    public async Task The_test_service_answers_the_api_user()
    {
        var live = LiveSettings.Load()!;
        var recorded = new List<(string Direction, string Message)>();

        await using (var client = new EFacturaClient(
            EFacturaEndpoints.Test, live.User, live.Password, TimeSpan.FromMinutes(1), live.Proxy, (d, m) => recorded.Add((d, m))))
        {
            var answer = await client.TestAsync("Konta");

            Assert.NotNull(answer);
        }

        live.Write("Test", recorded);
        Assert.Equal(["request", "response"], recorded.Select(r => r.Direction));
    }
}

/// <summary>A fact that runs only when <see cref="LiveSettings"/> finds an API user.</summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (LiveSettings.Load() is null)
        {
            Skip = "No e-Factura API user: set KONTA_EFACTURA_USER and KONTA_EFACTURA_PASSWORD, or put them in .env.";
        }
    }
}

internal sealed record LiveSettings(string User, string Password, Uri? Proxy, string Root)
{
    public static LiveSettings? Load()
    {
        var root = RepositoryRoot();
        var file = root is null ? new Dictionary<string, string>() : ReadDotEnv(Path.Combine(root, ".env"));

        string? Value(string name) =>
            Environment.GetEnvironmentVariable(name) is { Length: > 0 } fromEnvironment
                ? fromEnvironment
                : file.GetValueOrDefault(name);

        var user = Value("KONTA_EFACTURA_USER");
        var password = Value("KONTA_EFACTURA_PASSWORD");
        var proxy = Value("KONTA_EFACTURA_PROXY");

        return string.IsNullOrWhiteSpace(user) || string.IsNullOrEmpty(password) || root is null
            ? null
            : new LiveSettings(user, password, string.IsNullOrWhiteSpace(proxy) ? null : new Uri(proxy), root);
    }

    /// <summary>Writes one exchange under <c>fixtures/live/</c>, refusing to keep the password.</summary>
    public void Write(string operation, IEnumerable<(string Direction, string Message)> messages)
    {
        var folder = Path.Combine(Root, "fixtures", "live");
        Directory.CreateDirectory(folder);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture);

        foreach (var (direction, message) in messages)
        {
            Assert.DoesNotContain(Password, message, StringComparison.Ordinal);
            File.WriteAllText(Path.Combine(folder, $"{stamp}-{operation}-{direction}.xml"), message);
        }
    }

    private static string? RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Konta.EFactura.sln")))
            {
                return dir.FullName;
            }
        }

        return null;
    }

    private static Dictionary<string, string> ReadDotEnv(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path))
        {
            return values;
        }

        foreach (var line in File.ReadAllLines(path))
        {
            var trimmed = line.Trim();
            var equals = trimmed.IndexOf('=', StringComparison.Ordinal);
            if (trimmed.Length == 0 || trimmed[0] == '#' || equals < 1)
            {
                continue;
            }

            values[trimmed[..equals].Trim()] = trimmed[(equals + 1)..].Trim().Trim('"');
        }

        return values;
    }
}
