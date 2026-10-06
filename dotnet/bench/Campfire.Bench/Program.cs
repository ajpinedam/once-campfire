// HTTP throughput for the workloads in the top-level README's comparison table:
// room page, messages page, sidebar, search and posting a message.
//
//   dotnet run -c Release --project bench/Campfire.Bench -- --url http://127.0.0.1:5200 \
//     --email bench@example.test --password ... [--concurrency 16] [--duration 10] [--seed 400]
//     [--room ID --before MESSAGE_ID --query WORD]   (fixed paths, e.g. those of a labels.json seed)
//     [--only room,messages,sidebar,search,post]     (a subset of workloads)
//     [--json PATH]                                  (also write the results as JSON)
//
// Each client keeps one keep-alive connection, asks for uncompressed responses and reads the
// whole body (the Rails bench/compare_http.rb methodology). Every response must be 200.
// Run the server on its own CPUs (e.g. `taskset -c 0-3`) and this tool on others.

using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;

var options = Options.Parse(args);
var cookies = new CookieContainer();
using var setup = Client(options, cookies);

await SignInAsync(setup, options);
var csrf = await CsrfTokenAsync(setup, "/");
var roomPath = (await setup.GetAsync("/")).RequestMessage!.RequestUri!.AbsolutePath;
if (!roomPath.StartsWith("/rooms/", StringComparison.Ordinal))
{
    var landing = await setup.GetAsync("/rooms");
    roomPath = landing.RequestMessage!.RequestUri!.AbsolutePath;
}
var roomId = options.RoomId ?? roomPath.Split('/')[2];

await SeedAsync(setup, csrf, roomId, options.Seed);
var middleId = options.BeforeId ?? await MiddleMessageIdAsync(setup, roomId);

var workloads = new (string Key, string Name, Func<HttpRequestMessage> Request)[]
{
    ("room", "Room page", () => new(HttpMethod.Get, $"/rooms/{roomId}")),
    ("messages", "Messages page", () => new(HttpMethod.Get, $"/rooms/{roomId}/messages?before={middleId}")),
    ("sidebar", "Sidebar", () => new(HttpMethod.Get, "/users/me/sidebar")),
    ("search", "Search", () => new(HttpMethod.Get, $"/searches?q={Uri.EscapeDataString(options.Query)}")),
    ("post", "Post a message", () => PostMessage(csrf, roomId, $"Benchmark message {Guid.NewGuid():N} about campfire")),
}.Where(workload => options.Only is null || options.Only.Contains(workload.Key)).ToArray();

Console.WriteLine($"{options.Concurrency} clients, {options.Duration.TotalSeconds:0}s per workload (+2s warmup), against {options.Url}");
Console.WriteLine();
Console.WriteLine($"| {"Workload",-15} | {"req/s",9} | {"p50 ms",7} | {"p99 ms",7} |");
Console.WriteLine($"|{new string('-', 17)}|{new string('-', 10)}:|{new string('-', 8)}:|{new string('-', 8)}:|");

var results = new Dictionary<string, Result>();
foreach (var (key, name, request) in workloads)
{
    await RunAsync(options, cookies, request, TimeSpan.FromSeconds(2)); // warmup
    var result = results[key] = await RunAsync(options, cookies, request, options.Duration);
    Console.WriteLine($"| {name,-15} | {result.Throughput,9:N0} | {result.P50,7:0.00} | {result.P99,7:0.00} |");
}

if (options.Json is { } jsonPath)
{
    await File.WriteAllTextAsync(jsonPath, System.Text.Json.JsonSerializer.Serialize(
        results.ToDictionary(pair => pair.Key, pair => new Dictionary<string, double> { ["rps"] = pair.Value.Throughput, ["p50"] = pair.Value.P50, ["p99"] = pair.Value.P99 }),
        new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
}

static async Task<Result> RunAsync(Options options, CookieContainer cookies, Func<HttpRequestMessage> request, TimeSpan duration)
{
    var clients = Enumerable.Range(0, options.Concurrency).Select(_ => Client(options, cookies)).ToArray();
    var latencies = new List<double>[clients.Length];
    var stopAt = Stopwatch.GetTimestamp() + (long)(duration.TotalSeconds * Stopwatch.Frequency);
    var started = Stopwatch.GetTimestamp();

    await Task.WhenAll(clients.Select(async (client, index) =>
    {
        var samples = latencies[index] = new List<double>(capacity: 1 << 16);
        var buffer = new byte[64 * 1024];
        while (Stopwatch.GetTimestamp() < stopAt)
        {
            var begin = Stopwatch.GetTimestamp();
            using var response = await client.SendAsync(request(), HttpCompletionOption.ResponseHeadersRead);
            await using var body = await response.Content.ReadAsStreamAsync();
            while (await body.ReadAsync(buffer) > 0)
            {
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new InvalidOperationException($"{response.RequestMessage!.RequestUri} answered {(int)response.StatusCode}");
            }

            samples.Add(Stopwatch.GetElapsedTime(begin).TotalMilliseconds);
        }
    }));

    var elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;
    foreach (var client in clients)
    {
        client.Dispose();
    }

    var all = latencies.SelectMany(samples => samples).Order().ToArray();
    return new Result(all.Length / elapsed, Percentile(all, 0.50), Percentile(all, 0.99));
}

static double Percentile(double[] sorted, double percentile) =>
    sorted.Length == 0 ? 0 : sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * percentile))];

static HttpClient Client(Options options, CookieContainer cookies) =>
    new(new SocketsHttpHandler
    {
        CookieContainer = cookies,
        UseCookies = true,
        AutomaticDecompression = DecompressionMethods.None,
        MaxConnectionsPerServer = 1,
        PooledConnectionLifetime = Timeout.InfiniteTimeSpan
    })
    {
        BaseAddress = new Uri(options.Url),
        DefaultRequestHeaders =
        {
            { "User-Agent", "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36" },
            { "Accept", "text/html, application/xhtml+xml" }
        }
    };

static async Task SignInAsync(HttpClient client, Options options)
{
    var page = await client.GetAsync("/session/new");
    var path = page.RequestMessage!.RequestUri!.AbsolutePath;

    if (path == "/first_run")
    {
        await PostFormAsync(client, "/first_run", await CsrfTokenAsync(client, "/first_run"),
            ("user[name]", "Bench Admin"), ("user[email_address]", options.Email), ("user[password]", options.Password));
        return;
    }

    if (path == "/session/new")
    {
        var response = await PostFormAsync(client, "/session", await CsrfTokenAsync(client, "/session/new"),
            ("email_address", options.Email), ("password", options.Password));
        if (response.RequestMessage!.RequestUri!.AbsolutePath == "/session")
        {
            throw new InvalidOperationException("Sign-in failed; check --email/--password");
        }
    }
}

static async Task<string> CsrfTokenAsync(HttpClient client, string path)
{
    var html = await client.GetStringAsync(path);
    return CsrfMeta().Match(html) is { Success: true } match ? match.Groups[1].Value : throw new InvalidOperationException($"No CSRF token on {path}");
}

static async Task<HttpResponseMessage> PostFormAsync(HttpClient client, string path, string csrf, params (string Name, string Value)[] fields)
{
    using var request = new HttpRequestMessage(HttpMethod.Post, path)
    {
        Content = new FormUrlEncodedContent(fields.Select(field => KeyValuePair.Create(field.Name, field.Value)))
    };
    request.Headers.Add("X-CSRF-Token", csrf);
    return await client.SendAsync(request);
}

static HttpRequestMessage PostMessage(string csrf, string roomId, string text)
{
    var request = new HttpRequestMessage(HttpMethod.Post, $"/rooms/{roomId}/messages")
    {
        Content = new FormUrlEncodedContent([KeyValuePair.Create("message[body]", $"<p>{text}</p>")])
    };
    request.Headers.Add("X-CSRF-Token", csrf);
    request.Headers.Accept.ParseAdd("text/vnd.turbo-stream.html");
    return request;
}

static async Task SeedAsync(HttpClient client, string csrf, string roomId, int count)
{
    var existing = MessageIds().Count(await client.GetStringAsync($"/rooms/{roomId}"));
    if (existing >= Math.Min(count, 40))
    {
        return;
    }

    string[] words = ["campfire", "chat", "deploy", "review", "coffee", "release", "design", "standup", "lunch", "bug"];
    for (var i = 0; i < count; i++)
    {
        var text = string.Join(' ', Enumerable.Range(0, 12).Select(n => words[(i * 7 + n * 3) % words.Length])) + $" https://example.com/{i}";
        using var response = await client.SendAsync(PostMessage(csrf, roomId, text));
        response.EnsureSuccessStatusCode();
    }
}

static async Task<string> MiddleMessageIdAsync(HttpClient client, string roomId)
{
    var ids = MessageIds().Matches(await client.GetStringAsync($"/rooms/{roomId}")).Select(match => match.Groups[1].Value).ToArray();
    return ids.Length > 0 ? ids[ids.Length / 2] : throw new InvalidOperationException("The room has no messages");
}

internal sealed record Result(double Throughput, double P50, double P99);

internal sealed record Options(string Url, string Email, string Password, int Concurrency, TimeSpan Duration, int Seed,
    string? RoomId, string? BeforeId, string Query, IReadOnlySet<string>? Only, string? Json)
{
    public static Options Parse(string[] args)
    {
        string Value(string name, string? fallback = null) => Optional(name) ?? fallback ?? throw new ArgumentException($"{name} is required");

        string? Optional(string name)
        {
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }

        return new Options(
            Value("--url", "http://127.0.0.1:5200"),
            Value("--email"),
            Value("--password"),
            int.Parse(Value("--concurrency", "16"), System.Globalization.CultureInfo.InvariantCulture),
            TimeSpan.FromSeconds(double.Parse(Value("--duration", "10"), System.Globalization.CultureInfo.InvariantCulture)),
            int.Parse(Value("--seed", "400"), System.Globalization.CultureInfo.InvariantCulture),
            RoomId: Optional("--room"),
            BeforeId: Optional("--before"),
            Query: Value("--query", "campfire"),
            Only: Optional("--only")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal),
            Json: Optional("--json"));
    }
}

internal static partial class Program
{
    [GeneratedRegex("<meta name=\"csrf-token\" content=\"([^\"]+)\"")]
    private static partial Regex CsrfMeta();

    [GeneratedRegex("data-message-id=\"(\\d+)\"")]
    private static partial Regex MessageIds();
}
