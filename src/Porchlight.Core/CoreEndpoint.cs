using System.IO.Pipes;
using System.Security.Principal;
using Tomlyn;

namespace Porchlight.Core;

public sealed record CoreEndpoint
{
    public const string Prefix = @"\\.\pipe\";
    public string Path { get; }
    public string Source { get; }

    private CoreEndpoint(string path, string source)
    {
        if (!path.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) || path.Length > 256 ||
            path[Prefix.Length..] is "" or "." or ".." || path[Prefix.Length..].Any(c => char.IsControl(c) || "/\\:".Contains(c)))
            throw new ArgumentException(@"Select a local Windows core pipe, for example \\.\pipe\herdr-core-user-default.sock.");
        Path = path;
        Source = source;
    }

    public static CoreEndpoint Select(string path) => new(path, "selected");
    public static CoreEndpoint Discover() => Discover(Environment.GetEnvironmentVariable, File.ReadAllText);

    internal static CoreEndpoint Discover(Func<string, string?> env, Func<string, string> readConfig)
    {
        if (env("HC_SOCKET") is { Length: > 0 } socket) return new(socket, "HC_SOCKET");
        var home = env("XDG_CONFIG_HOME") is { Length: > 0 } xdg ? xdg : env("APPDATA") ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var config = System.IO.Path.Combine(home, "herdr-core", "config.toml");
        string? text = null;
        try { text = readConfig(config); }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new IOException("Cannot read HerdR config.toml. Check permissions or select the exact core pipe."); }
        if (text != null)
        {
            var parsed = Toml.Parse(text);
            if (parsed.HasErrors) throw new FormatException("Invalid HerdR config.toml. Correct it or select the exact core pipe.");
            if (parsed.ToModel().TryGetValue("socket", out var value))
                return new(value as string ?? throw new FormatException("HerdR config.socket must be a pipe path string."), "config.toml");
        }
        string session = env("HC_SESSION")?.Trim() is { Length: > 0 } s ? s : "default";
        if (session is "." or ".." || session.IndexOfAny("/\\:*?\"<>|".ToCharArray()) >= 0 || session.Any(char.IsControl))
            throw new ArgumentException("Invalid HC_SESSION. Correct it or select the exact core pipe.");
        string user = new((env("USERNAME") ?? "").Where(char.IsAsciiLetterOrDigit).ToArray());
        return new($"{Prefix}herdr-core-{user}-{session}.sock", "session: " + session);
    }

    internal async Task<NamedPipeClientStream> OpenAsync(CancellationToken token)
    {
        var pipe = new NamedPipeClientStream(".", Path[Prefix.Length..], PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
            TokenImpersonationLevel.Identification);
        try { await pipe.ConnectAsync(2000, token).ConfigureAwait(false); return pipe; }
        catch { pipe.Dispose(); throw; }
    }
}
