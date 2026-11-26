using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Porchlight.Core.Tests;

[TestClass]
public class ProgramNameTests
{
    static readonly ProgramNames BuiltIn = ProgramNames.Load(null);
    static void Check(ProgramNames names, string key, string name, params string[] argv) =>
        Assert.AreEqual(new ProgramName(key, name), names.FromArgv(JsonSerializer.SerializeToElement(new { argv })));

    static ProgramNames LoadConfig(string json)
    {
        string path = Path.GetTempFileName();
        try { File.WriteAllText(path, json); return ProgramNames.Load(path); }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void AgentsMatchKeysOrAliasesButUnknownReportsKeepTheirOwnName()
    {
        Assert.AreEqual(new ProgramName("claude", "Claude Code"), BuiltIn.Agent("CLAUDE-Code"));
        Assert.AreEqual(new ProgramName("cursor", "Cursor"), BuiltIn.Agent("AGENT"));
        Assert.AreEqual(new ProgramName("dsh-tui", "DeepSeek Harness"), BuiltIn.Agent("dsh-tui"));
        Assert.AreEqual(new ProgramName("agent", "company/agent"), BuiltIn.Agent("company/agent"));
    }

    [TestMethod]
    public void LaunchAndForegroundUseExecutableNamesNotOtherArguments()
    {
        Check(BuiltIn, "lazygit", "Git Changes", @"C:\bin\--GitUI.ExE", "--path", "secret");
        Check(BuiltIn, "lazygit", "Git Changes", "tig");
        Check(BuiltIn, "git", "Git", "/bin/git", "lazygit");
        Check(BuiltIn, "cursor", "Cursor", "/opt/agent");
        Check(BuiltIn, "shell", "zsh", "/bin/-zsh");
        Check(BuiltIn, "program", "My.Tool", @"C:\private\My.Tool.exe", "--token=private");
    }

    [TestMethod]
    [DataRow("null")]
    [DataRow("{}")]
    [DataRow("{\"argv\":null,\"name\":\"lazygit\"}")]
    [DataRow("{\"argv\":[]}")]
    [DataRow("{\"argv\":\"lazygit\"}")]
    public void NoArgvLeavesFallbackToTheCaller(string json)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.IsNull(BuiltIn.FromArgv(doc.RootElement));
        Assert.AreEqual(new ProgramName("shell", "Shell"), BuiltIn.Shell);
    }

    [TestMethod]
    public void InterpretersFindTheFirstKnownNonInterpreterScript()
    {
        Check(BuiltIn, "vite", "Vite", "env", "node", "--require", "/tmp/unknown.cjs", @"C:\scripts\vite.mjs", "jest");
        Check(BuiltIn, "pytest", "pytest", "uv", "run", "pytest.py");
        Check(BuiltIn, "python", "Python", "python3", "-m", "http.server");
        Check(BuiltIn, "node", "Node", "node", "-vite.js", "server.js", "vite.json");
    }

    [TestMethod]
    public void UnixAndFishLaunchWrappersCanNest()
    {
        Check(BuiltIn, "lazygit", "Git Changes", "/bin/bash", "-ic", "\"$@\"; exec \"$0\" -i", "bash", "tig");
        Check(BuiltIn, "vim", "Vim", "/bin/zsh", "-lic", "\"$@\"; exec \"$0\" -li", "zsh",
            "/bin/fish", "-lic", "command $argv[2..]; exec $argv[1] -li", "fish", "vi");
        Check(BuiltIn, "vim", "Vim", "fish", "-ic", "command $argv[2..]; exec $argv[1] -i", "fish", "vi");
        Check(BuiltIn, "shell", "fish", "fish", "-ic", "command $argv[2..]", "fish", "vi");
        Check(BuiltIn, "shell", "sh", "sh", "-c", "echo tig", "sh", "tig");
    }

    [TestMethod]
    public void PowerShellUnwrapsLiteralWordsNotGeneralCommands()
    {
        const string pwsh = @"C:\Program Files\PowerShell\7\pwsh.EXE";
        Check(BuiltIn, "lazygit", "Git Changes", pwsh, "-NoExit", "-Command", "& 'node' '/dir with spaces/tig.js' ''");
        Check(BuiltIn, "program", "O’Brien's tool", pwsh, "-NoExit", "-Command", "& ‘O’’Brien''s tool’ 'second word'");
        Check(BuiltIn, "shell", "pwsh", pwsh, "-NoExit", "-Command", "& 'tig'; git");
        Check(BuiltIn, "shell", "pwsh", pwsh, "-noexit", "-Command", "& 'tig'");
        Check(BuiltIn, "shell", "pwsh", pwsh, "-NoExit", "-Command", "& 'tig'", "extra");
    }

    [TestMethod]
    public void OverlayMergesNamesReplacesOnlyNonemptyAliasListsAndExtendsRunners()
    {
        var custom = LoadConfig("""
            {"shells":["mysh"],"interpreters":["myrunner"],"programs":{
              "shell":{"name":"My shell"},"python3":{"name":"Project Python"},
              "lazygit":{"name":"Changes","match":[]},"vim":{"match":["editor"]},"task":{}}}
            """);
        Assert.AreEqual(new ProgramName("shell", "My shell"), custom.Shell);
        Assert.AreEqual(new ProgramName("lazygit", "Changes"), custom.Agent("gitui"));
        Check(custom, "python3", "Project Python", "Python3.EXE"); // key wins over the Python alias
        Check(custom, "vim", "Vim", "mysh", "-ic", "\"$@\"", "mysh", "myrunner", "/bin/editor.rb");
        Check(custom, "program", "vi", "vi");
        Check(custom, "shell", "mysh", "mysh");
        Check(custom, "task", "task", "task");
        Assert.AreEqual(new ProgramName("lazygit", "Git Changes"), BuiltIn.Agent("gitui"));
    }

    [TestMethod]
    [DataRow("not JSON")]
    [DataRow("""{"programs":{"vim":{"name":"Changed"},"broken":{"match":[null]}}}""")]
    [DataRow("""{"programs":{"vim":{"name":"Changed","tint":"invalid"}}}""")]
    public void InvalidOverlayCannotPartiallyReplaceTheBuiltInTable(string json) =>
        Check(LoadConfig(json), "vim", "Vim", "vi");

    [TestMethod, DoNotParallelize]
    public void LoadUsesAppHomeBeforeXdgConfigHome()
    {
        var temp = Directory.CreateTempSubdirectory();
        var app = Environment.GetEnvironmentVariable("HERDR_APP_HOME");
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        try
        {
            File.WriteAllText(Path.Combine(temp.FullName, "programs.json"), """{"programs":{"shell":{"name":"App"}}}""");
            string xdgApp = temp.CreateSubdirectory("herdr-app").FullName;
            File.WriteAllText(Path.Combine(xdgApp, "programs.json"), """{"programs":{"shell":{"name":"XDG"}}}""");
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", temp.FullName);
            Environment.SetEnvironmentVariable("HERDR_APP_HOME", temp.FullName);
            Assert.AreEqual("App", ProgramNames.Load().Shell.Name);
            Environment.SetEnvironmentVariable("HERDR_APP_HOME", null);
            Assert.AreEqual("XDG", ProgramNames.Load().Shell.Name);
            File.Delete(Path.Combine(xdgApp, "programs.json"));
            Assert.AreEqual(BuiltIn.Shell, ProgramNames.Load().Shell);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HERDR_APP_HOME", app);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", xdg);
            temp.Delete(true);
        }
    }
}
