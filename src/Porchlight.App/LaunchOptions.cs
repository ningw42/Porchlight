using Porchlight.Core.Configuration;

namespace Porchlight.App;

internal sealed class LaunchOptions
{
    public string? Pipe, Machine, Select;
    public bool Browse { get; private set; }
    public bool NoSidebarAnimation { get; private set; }
    public TerminalFont Font { get; private set; } = new();
    public bool ImportedFont { get; private set; }
    public TerminalScheme? Scheme { get; private set; }

    public static LaunchOptions Parse(IEnumerable<string> args)
    {
        var options = new LaunchOptions();
        string? fontPath = null, face = null, schemeName = null;
        var overrides = new Dictionary<string, float>();
        foreach (var arg in args)
        {
            if (arg == "--browse") { options.Browse = true; continue; }
            if (arg == "--no-sidebar-animation") { options.NoSidebarAnimation = true; continue; }
            var pair = arg.Split('=', 2);
            if (pair.Length != 2 || string.IsNullOrWhiteSpace(pair[1])) throw new FormatException("Expected --option=value. See README.");
            switch (pair[0])
            {
                case "--pipe": options.Pipe = pair[1]; break;
                case "--machine": options.Machine = pair[1]; break;
                case "--select": options.Select = pair[1]; break;
                case "--wt-font": fontPath = pair[1]; break;
                case "--wt-scheme": schemeName = pair[1]; break;
                case "--face": face = pair[1]; break;
                case "--ss01": overrides["ss01"] = Bit(pair[1]); break;
                case "--calt": overrides["calt"] = Bit(pair[1]); break;
                default: throw new FormatException("Unknown launch option. See README.");
            }
        }
        if (fontPath != null)
        {
            using var input = File.OpenRead(fontPath);
            options.Font = TerminalFont.ReadWindowsTerminalDefaults(input);
            options.ImportedFont = true;
        }
        if (schemeName != null)
        {
            var path = fontPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Packages\Microsoft.WindowsTerminal_8wekyb3d8bbwe\LocalState\settings.json");
            using var input = File.OpenRead(path);
            options.Scheme = TerminalScheme.Read(input, schemeName);
        }
        if (face != null) options.Font = options.Font with { Face = face };
        foreach (var (key, value) in overrides) options.Font.Features[key] = value;
        return options;
    }
    static int Bit(string value) => value switch { "0" => 0, "1" => 1, _ => throw new FormatException("Font features accept 0 or 1.") };
}
