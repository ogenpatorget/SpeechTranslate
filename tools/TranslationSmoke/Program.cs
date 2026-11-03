using ChurchTranslate.Application;
using ChurchTranslate.Infrastructure;
using System.Text.Json;
using System.Xml.Linq;

var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
if (string.IsNullOrWhiteSpace(key))
{
    var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName,
        "src", "ChurchTranslate.Api", "ChurchTranslate.Api.csproj"))) directory = directory.Parent;
    var project = directory is null ? null : Path.Combine(directory.FullName,
        "src", "ChurchTranslate.Api", "ChurchTranslate.Api.csproj");
    var id = project is null ? null : XDocument.Load(project).Descendants("UserSecretsId").FirstOrDefault()?.Value;
    var root = OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "UserSecrets")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".microsoft", "usersecrets");
    var path = id is null ? "" : Path.Combine(root, id, "secrets.json");
    if (File.Exists(path))
    {
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        key = json.RootElement.TryGetProperty("OPENAI_API_KEY", out var value) ? value.GetString() : null;
    }
}
if (string.IsNullOrWhiteSpace(key))
{
    Console.Error.WriteLine("OPENAI_API_KEY is not set.");
    return 2;
}

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
try
{
    var provider = new OpenAiTranslationProvider(key);
    await using var session = await provider.StartAsync(
        new TranslationOptions("sv", "en", 24000, 200), timeout.Token);
    var received = 0;
    var receiver = Task.Run(async () =>
    {
        await foreach (var chunk in session.ReceiveAsync(timeout.Token))
            received += chunk.Payload.Length;
    }, timeout.Token);
    for (var i = 0; i < 10; i++)
    {
        await session.SendAudioAsync(new byte[9600], timeout.Token);
        await Task.Delay(200, timeout.Token);
    }
    await session.CompleteInputAsync(timeout.Token);
    await receiver;
    Console.WriteLine($"OpenAI translation session succeeded. Sent 2 seconds of silence; received {received} audio bytes.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"OpenAI translation session failed: {ex.GetType().Name}: {ex.Message}");
    return 1;
}
