using System.Text;

namespace AccessCity.API.Configuration;

public static class EnvironmentBootstrap
{
    public static void LoadRepoRootDotEnv()
    {
        var envPath = FindDotEnvPath();

        if (envPath is null)
        {
            return;
        }

        foreach (var rawLine in File.ReadAllLines(envPath))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
            {
                continue;
            }

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim();

            if (value.Length >= 2 &&
                ((value.StartsWith('"') && value.EndsWith('"')) ||
                 (value.StartsWith('\'') && value.EndsWith('\''))))
            {
                value = value[1..^1];
            }

            value = DecodeEscapes(value);

            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }

    private static string DecodeEscapes(string value)
    {
        var builder = new StringBuilder(value.Length);

        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                var next = value[i + 1];
                if (next == 'n')
                {
                    builder.Append('\n');
                    i++;
                    continue;
                }

                if (next == 'r')
                {
                    builder.Append('\r');
                    i++;
                    continue;
                }

                if (next == 't')
                {
                    builder.Append('\t');
                    i++;
                    continue;
                }
            }

            builder.Append(value[i]);
        }

        return builder.ToString();
    }

    private static string? FindDotEnvPath()
    {
        var candidates = new List<string>();
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());

        while (current is not null)
        {
            candidates.Add(Path.Combine(current.FullName, ".env"));
            current = current.Parent;
        }

        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(File.Exists);
    }
}
