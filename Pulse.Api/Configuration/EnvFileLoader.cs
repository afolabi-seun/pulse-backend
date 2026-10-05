namespace Pulse.Api.Configuration;

public static class EnvFileLoader
{
    public static void Load(string fileName = ".env")
    {
        var path = Path.Combine(Directory.GetCurrentDirectory(), fileName);
        if (!File.Exists(path)) return;

        foreach (var line in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#')) continue;

            var idx = line.IndexOf('=');
            if (idx < 0) continue;

            var key = line[..idx].Trim();
            var value = line[(idx + 1)..].Trim();

            // Only set if not already present — lets real env vars win over the file
            if (Environment.GetEnvironmentVariable(key) is null)
                Environment.SetEnvironmentVariable(key, value);
        }
    }
}
