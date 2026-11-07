using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Automation;
using Microsoft.Win32;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DshStudio;

public static class Paths
{
    public static readonly string Data =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DshStudio");

    public static string In(string file)
    {
        Directory.CreateDirectory(Data);
        return Path.Combine(Data, file);
    }
}

public sealed class Settings
{
    public string InstallDir { get; set; } = @"C:\OpenCodeStartup";
    public string Workspace { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public string LastModel { get; set; } = "";
    public string Lang { get; set; } = "";
    public Dictionary<string, ModelTuning> Models { get; set; } = new();

    public ModelTuning Tuning(string model)
    {
        if (!Models.TryGetValue(model, out var tuning)) Models[model] = tuning = new ModelTuning();
        return tuning;
    }

    public static Settings Load()
    {
        try
        {
            var file = Paths.In("settings.json");
            if (File.Exists(file))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(file)) ?? new Settings();
        }
        catch { /* испорченный файл настроек не повод не запуститься */ }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            var file = Paths.In("settings.json");
            var temp = file + ".tmp";
            File.WriteAllText(temp,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, file, true);
        }
        catch { }
    }
}

public sealed record InstalledModel(string Name, long Size, string Params, string Quant, string Digest);

public sealed record ModelFacts(int Context, string Parameters, IReadOnlyList<string> Capabilities)
{
    public bool Thinks => Capabilities.Contains("thinking");
}

public sealed class ModelTuning
{
    public int? ContextWindow { get; set; }
    public int? ModelContext { get; set; }
    public int? MaxTokens { get; set; }
    public bool? Reasoning { get; set; }
    public Dictionary<string, string> Params { get; set; } = new();
    /// <summary>null - ещё не смотрели: правки уходят в саму модель, поэтому запоминаем один раз.</summary>
    public string? Shipped { get; set; }
}

public static class Ollama
{
    public const string Base = "http://127.0.0.1:11434";
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    public static async Task<string?> VersionAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var json = await Http.GetStringAsync($"{Base}/api/version", cts.Token);
            return JsonDocument.Parse(json).RootElement.GetProperty("version").GetString();
        }
        catch { return null; }
    }

    public static async Task<List<InstalledModel>> ListAsync()
    {
        var list = new List<InstalledModel>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var json = await Http.GetStringAsync($"{Base}/api/tags", cts.Token);
        foreach (var m in JsonDocument.Parse(json).RootElement.GetProperty("models").EnumerateArray())
        {
            var details = m.TryGetProperty("details", out var d) ? d : default;
            list.Add(new InstalledModel(
                m.GetProperty("name").GetString() ?? "?",
                m.TryGetProperty("size", out var s) ? s.GetInt64() : 0,
                Str(details, "parameter_size"),
                Str(details, "quantization_level"),
                (m.TryGetProperty("digest", out var dg) ? dg.GetString() ?? "" : "").Replace("sha256:", "")));
        }
        return list.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();

        static string Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";
    }

    public static async Task<int> ContextAsync(string model) => (await FactsAsync(model)).Context;

    public static Task<string?> WarmAsync(string model, CancellationToken ct = default) =>
        AskAsync("/api/generate", new Dictionary<string, object>
        {
            ["model"] = model,
            ["prompt"] = "",
            ["stream"] = false,
        }, ct);

    public static Task<string?> ProbeAsync(string model, CancellationToken ct = default) =>
        AskAsync("/api/chat", new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = new[] { new Dictionary<string, string> { ["role"] = "user", ["content"] = "ok" } },
            ["stream"] = false,
            ["options"] = new Dictionary<string, object> { ["num_predict"] = 8 },
        }, ct);

    public static Task UnloadAsync(string model) =>
        AskAsync("/api/generate", new Dictionary<string, object>
        {
            ["model"] = model,
            ["prompt"] = "",
            ["stream"] = false,
            ["keep_alive"] = 0,
        }, default);

    private static async Task<string?> AskAsync(string path, Dictionary<string, object> body, CancellationToken ct)
    {
        try
        {
            using var resp = await Http.PostAsync($"{Base}{path}",
                new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"), ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (resp.IsSuccessStatusCode) return null;

            try
            {
                var message = JsonDocument.Parse(text).RootElement.GetProperty("error").GetString();
                return Short(message ?? text);
            }
            catch { return Short(text); }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Short(ex.Message); }

        static string Short(string text) => text.Split('\n')[0].Trim();
    }

    public static async Task<ModelFacts> FactsAsync(string model)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var resp = await Http.PostAsync($"{Base}/api/show",
                new StringContent($"{{\"model\":\"{model}\"}}", Encoding.UTF8, "application/json"), cts.Token);
            var root = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token)).RootElement;

            var context = 32768;
            if (root.TryGetProperty("model_info", out var info))
                foreach (var p in info.EnumerateObject())
                    if (p.Name.EndsWith(".context_length") && p.Value.TryGetInt32(out var ctx))
                        context = ctx;

            var parameters = root.TryGetProperty("parameters", out var pr) ? pr.GetString() ?? "" : "";
            var abilities = root.TryGetProperty("capabilities", out var caps)
                ? caps.EnumerateArray().Select(c => c.GetString() ?? "").ToList()
                : new List<string>();
            return new ModelFacts(context, parameters, abilities);
        }
        catch { return new ModelFacts(32768, "", Array.Empty<string>()); }
    }

    public static async Task CreateAsync(string model, IReadOnlyDictionary<string, string> parameters)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["from"] = model,
            ["parameters"] = parameters.ToDictionary(p => p.Key, p => Typed(p.Key, p.Value)),
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var resp = await Http.PostAsync($"{Base}/api/create",
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"), cts.Token);
        resp.EnsureSuccessStatusCode();
        await resp.Content.ReadAsStringAsync(cts.Token);

        static object Typed(string name, string value)
        {
            if (name == "stop") return value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (long.TryParse(value, out var whole)) return whole;
            if (double.TryParse(value, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out var real)) return real;
            if (bool.TryParse(value, out var flag)) return flag;
            return value;
        }
    }

    public static async Task DeleteAsync(string model)
    {
        var req = new HttpRequestMessage(HttpMethod.Delete, $"{Base}/api/delete")
        {
            Content = new StringContent($"{{\"model\":\"{model}\"}}", Encoding.UTF8, "application/json")
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        (await Http.SendAsync(req, cts.Token)).EnsureSuccessStatusCode();
    }

    public static async Task PullAsync(string model, Action<string, double?> report, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"{Base}/api/pull")
        {
            Content = new StringContent($"{{\"model\":\"{model}\",\"stream\":true}}", Encoding.UTF8, "application/json")
        };
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(ct));

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err))
                throw new InvalidOperationException(err.GetString());

            var status = root.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";
            double? share = null;
            if (root.TryGetProperty("total", out var t) && root.TryGetProperty("completed", out var c))
            {
                var total = t.GetInt64();
                if (total > 0) share = c.GetInt64() * 100.0 / total;
            }
            report(status, share);
        }
    }
}

public static class OllamaHost
{
    public static string Folder { get; set; } = "";

    private static string SystemCli =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "Programs", "Ollama", "ollama.exe");

    private static string Own(string dir) => Path.Combine(dir, "Ollama", "ollama.exe");
    private static string Cli => File.Exists(Own(Folder)) ? Own(Folder) : SystemCli;

    public static bool IsInstalled() => File.Exists(Cli);
    public static bool IsOwn(string dir) => File.Exists(Own(dir));

    public static void Settle(string dir)
    {
        var old = Path.GetDirectoryName(Own(Folder))!;
        var moved = Folder.Length > 0 && !string.Equals(Path.GetFullPath(old),
            Path.GetFullPath(Path.GetDirectoryName(Own(dir))!), StringComparison.OrdinalIgnoreCase);
        Folder = dir;
        if (moved) OpenCode.TryDelete(old);
    }

    public static string? InstalledVersion()
    {
        if (!File.Exists(Cli)) return null;
        try
        {
            var psi = new ProcessStartInfo(Cli, "--version")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi)!;
            var text = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(10000);
            var found = Regex.Match(text, @"version is ([0-9][^\s]*)");
            return found.Success ? found.Groups[1].Value : null;
        }
        catch { return null; }
    }

    public static bool IsRunning() =>
        Process.GetProcessesByName("ollama").Length > 0 || Process.GetProcessesByName("ollama app").Length > 0;

    public static string ModelsDir()
    {
        var chosen = Environment.GetEnvironmentVariable("OLLAMA_MODELS", EnvironmentVariableTarget.User);
        if (!string.IsNullOrWhiteSpace(chosen)) return chosen.TrimEnd('\\');
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ollama", "models");
    }

    public static long LoadedBytes()
    {
        long read = 0;
        foreach (var p in Process.GetProcessesByName("llama-server"))
        {
            try { if (GetProcessIoCounters(p.Handle, out var io)) read += (long)io.ReadBytes; }
            catch { }
            p.Dispose();
        }
        return read;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);

    public static bool HasModels() => Manifests().Any();

    public static IEnumerable<string> Manifests() =>
        !Directory.Exists(ModelsDir()) ? Enumerable.Empty<string>() :
        Directory.GetDirectories(ModelsDir(), "manifests*")
                 .SelectMany(dir => Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories));

    public static void Stop()
    {
        foreach (var name in new[] { "ollama app", "ollama" })
            foreach (var p in Process.GetProcessesByName(name))
            {
                try { p.Kill(entireProcessTree: true); p.WaitForExit(15000); } catch { }
            }
    }

    public static int Context { get; private set; }

    public static void Start(string modelsDir, int context = 0)
    {
        if (!IsInstalled()) return;

        // Свой вывод Ollama никуда не пишет. Заворачиваем его в файл через cmd:
        // труба оборвалась бы вместе с нами, а файл останется её собственным.
        var psi = new ProcessStartInfo("cmd.exe", $"/c \"\"{Cli}\" serve > \"{OllamaLog.File}\" 2>&1\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        };
        psi.Environment["OLLAMA_MODELS"] = modelsDir;
        if (context > 0) psi.Environment["OLLAMA_CONTEXT_LENGTH"] = context.ToString();
        Context = context;
        try { Process.Start(psi); } catch { }
    }

    /// <summary>OLLAMA_CONTEXT_LENGTH читается только при старте сервера, своё умолчание у неё 4096.</summary>
    public static async Task UseContextAsync(int context, Action<string> log)
    {
        if (context <= 0 || (IsRunning() && Context == context)) return;

        if (IsRunning())
        {
            Stop();
            await Task.Delay(1500);
        }
        Start(ModelsDir(), context);
        for (var i = 0; i < 20 && await Ollama.VersionAsync() is null; i++) await Task.Delay(500);
        log(Loc.F("L_LogContext", context));
    }

    public static async Task UseModelsDirAsync(string target, Action<string> log)
    {
        target = target.TrimEnd('\\');
        var current = ModelsDir();
        if (string.Equals(current, target, StringComparison.OrdinalIgnoreCase))
        {
            log(Loc.F("L_LogModelsHere", target));
            return;
        }

        log(Loc.F("L_LogModelsMove", current, target));
        Stop();
        await Task.Delay(1500);

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (Directory.Exists(current) && Directory.Exists(target))
            log(Loc.S("L_LogTargetExists"));
        else if (Directory.Exists(current) && SameVolume(current, target))
            Directory.Move(current, target);
        else if (Directory.Exists(current))
        {
            log(Loc.S("L_LogOtherDrive"));
            Start(current, Context);
            return;
        }
        else
            Directory.CreateDirectory(target);

        Environment.SetEnvironmentVariable("OLLAMA_MODELS", target, EnvironmentVariableTarget.User);
        Start(target, Context);
        await Task.Delay(2500);
        log(Loc.F("L_LogOllamaRestarted", target));
    }

    private static bool SameVolume(string a, string b) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)),
                      StringComparison.OrdinalIgnoreCase);
}

public static class Catalog
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static string? ParseLink(string text)
    {
        const RegexOptions Plain = RegexOptions.IgnoreCase;
        var link = Regex.Replace(text.Trim(), @"^ollama\s+(run|pull)\s+", "", Plain);
        var address = Regex.IsMatch(link, @"^https?://", Plain);
        link = Regex.Replace(link, @"^https?://", "", Plain);
        link = Regex.Replace(link, @"^(www\.)?(huggingface\.co|hf\.co)/", "hf.co/", Plain);
        if (address && !link.StartsWith("hf.co/", StringComparison.Ordinal)) return null;

        var file = Regex.Match(link, @"^(hf\.co/[^/]+/[^/:?#]+)/[^?#]*-([^-/?#]+)\.gguf", Plain);
        link = file.Success
            ? $"{file.Groups[1].Value}:{file.Groups[2].Value}"
            : Regex.Replace(link, @"^(hf\.co/[^/]+/[^/:?#]+)(:[^/?#]+)?[/?#].*$", "$1$2");

        return Regex.IsMatch(link, @"^[A-Za-z0-9][A-Za-z0-9_.-]*(/[A-Za-z0-9_.-]+)*(:[A-Za-z0-9_.-]+)?$") ? link : null;
    }

    public static async Task<List<string>> NamesAsync(bool refresh)
    {
        var cache = Paths.In("catalog.json");
        if (!refresh && File.Exists(cache))
        {
            try { return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(cache)) ?? new(); }
            catch { }
        }

        var html = await Http.GetStringAsync("https://ollama.com/library");
        var names = Regex.Matches(html, "href=\"/library/([^\"/]+)\"")
                         .Select(m => m.Groups[1].Value)
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                         .ToList();
        if (names.Count > 0) File.WriteAllText(cache, JsonSerializer.Serialize(names));
        return names;
    }

    public static async Task<List<CatalogTag>> TagsAsync(string name)
    {
        var tags = new List<CatalogTag>();
        var digests = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var html = await Http.GetStringAsync($"https://ollama.com/library/{name}/tags");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Match entry in Regex.Matches(html, "href=\"/library/" + Regex.Escape(name) + ":([A-Za-z0-9._-]+)\""))
            {
                var tag = entry.Groups[1].Value;
                if (!seen.Add(tag)) continue;

                var tail = html.Substring(entry.Index, Math.Min(900, html.Length - entry.Index));
                var size = Regex.Match(tail, @"(\d+(?:\.\d+)?\s?[KMGT]B)").Groups[1].Value;
                digests[tag] = Regex.Match(tail, @"font-mono[^>]*>\s*([0-9a-f]{8,})").Groups[1].Value;
                tags.Add(new CatalogTag($"{name}:{tag}", size, Digest: digests[tag]));
            }
        }
        catch { }

        if (digests.TryGetValue("latest", out var pointsTo) && pointsTo.Length > 0)
        {
            tags.RemoveAll(t => t.Name.EndsWith(":latest", StringComparison.OrdinalIgnoreCase));
            for (var i = 0; i < tags.Count; i++)
            {
                var tag = tags[i].Name[(tags[i].Name.IndexOf(':') + 1)..];
                if (digests.TryGetValue(tag, out var digest) && digest == pointsTo)
                    tags[i] = tags[i] with { Latest = true };
            }
        }

        if (tags.Count == 0) tags.Add(new CatalogTag($"{name}:latest", ""));
        return tags;
    }
}

public static class Updates
{
    private const string OllamaArchive = "ollama-windows-amd64.zip";
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    public static async Task<(string Version, string Url)?> LatestOllamaAsync()
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get,
                "https://api.github.com/repos/ollama/ollama/releases/latest");
            request.Headers.Add("User-Agent", "OCStartup");   // GitHub без него отвечает отказом
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var resp = await Http.SendAsync(request, cts.Token);
            resp.EnsureSuccessStatusCode();

            var root = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token)).RootElement;
            var version = (root.GetProperty("tag_name").GetString() ?? "").TrimStart('v');
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                if (string.Equals(asset.GetProperty("name").GetString(), OllamaArchive, StringComparison.OrdinalIgnoreCase))
                    return (version, asset.GetProperty("browser_download_url").GetString() ?? "");
            }
            return (version, "");
        }
        catch { return null; }
    }

    public static async Task InstallOllamaAsync(string dir, (string Version, string Url) release,
                                                Action<string, double> report, CancellationToken ct)
    {
        var home = Path.Combine(dir, "Ollama");
        var fresh = home + ".new";
        var file = OpenCode.DownloadPath(dir, OllamaArchive);
        try
        {
            await OpenCode.DownloadAsync(release.Url, file,
                percent => report(Loc.F("L_DownloadingOllama", release.Version), percent), ct);

            OpenCode.TryDelete(fresh);
            await Task.Run(() => Unpack(file, fresh, percent => report(Loc.S("L_Unpacking"), percent), ct), ct);

            report(Loc.S("L_Installing"), -1);
            OllamaHost.Stop();
            await Task.Delay(1500, ct);
            if (Directory.Exists(home)) Directory.Delete(home, true);
            Directory.Move(fresh, home);
        }
        finally
        {
            OpenCode.TryDelete(fresh);
            OpenCode.DropDownload(file);
        }

        OllamaHost.Settle(dir);
        OllamaHost.Start(OllamaHost.ModelsDir(), OllamaHost.Context);
        for (var i = 0; i < 20 && await Ollama.VersionAsync() is null; i++) await Task.Delay(500, ct);
    }

    private static void Unpack(string archive, string target, Action<double> percent, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(archive);
        var root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
        var total = zip.Entries.Sum(entry => entry.Length);
        long done = 0;
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(entry.FullName);
            if (entry.Name.Length == 0) continue;

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path);
            done += entry.Length;
            percent(done * 100.0 / total);
        }
    }
}

public sealed record CatalogTag(string Name, string Size, bool Latest = false, string Digest = "")
{
    public string Display
    {
        get
        {
            var tag = Name.Contains(':') ? Name[(Name.IndexOf(':') + 1)..] : Name;
            var parts = new[] { tag, Size, Latest ? "latest" : "" }.Where(x => x.Length > 0);
            return string.Join("  ·  ", parts);
        }
    }
}

public sealed class OpenCode
{
    private const string Release = "https://api.github.com/repos/anomalyco/opencode/releases/latest";
    private const string Asset = "opencode-desktop-win-x64.exe";
    private const string InstallKey = @"Software\d074f30d-5f88-5885-b075-be1348cc7676";
    private static readonly string[] Ours = { "models", "Ollama", "download" };
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    public static string Exe(string dir) => Path.Combine(Home(dir), "OpenCode.exe");

    /// <summary>Установщик OpenCode при обновлении вычищает свою папку целиком, поэтому у него своя подпапка.</summary>
    private static string Home(string dir) =>
        File.Exists(Path.Combine(dir, "OpenCode.exe")) ? dir : Path.Combine(dir, "OpenCode");

    public static string ConfigFile(string dir) => Path.Combine(dir, "opencode.json");

    public static bool IsInstalled(string dir) =>
        !string.IsNullOrWhiteSpace(dir) && File.Exists(Exe(dir));

    public static string? InstalledVersion(string dir)
    {
        if (!IsInstalled(dir)) return null;
        try
        {
            var version = FileVersionInfo.GetVersionInfo(Exe(dir)).FileVersion?.Trim();
            return string.IsNullOrEmpty(version) ? null : version;
        }
        catch { return null; }
    }

    public static bool Running(string dir) => Live(dir).Count > 0;

    public static IntPtr Window(string dir)
    {
        foreach (var p in Live(dir))
        {
            var handle = p.MainWindowHandle;
            var titled = handle != IntPtr.Zero && p.MainWindowTitle.Length > 0;
            p.Dispose();
            if (titled) return handle;
        }
        return IntPtr.Zero;
    }

    private static List<Process> Live(string dir)
    {
        var found = new List<Process>();
        if (!IsInstalled(dir)) return found;
        var exe = Path.GetFullPath(Exe(dir));
        foreach (var p in Process.GetProcessesByName("OpenCode"))
        {
            try
            {
                if (string.Equals(p.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase)) found.Add(p);
                else p.Dispose();
            }
            catch { p.Dispose(); }
        }
        return found;
    }

    private static readonly HashSet<int> Awake = new();

    public static void KeepAwake(string dir)
    {
        const int PowerThrottlingInfo = 4;
        const uint ExecutionSpeed = 1;
        const uint SetInformation = 0x0200, SetQuota = 0x0100, QueryLimited = 0x1000;

        var live = Live(dir);
        if (live.Count == 0)
        {
            Awake.Clear();
            return;
        }

        foreach (var p in live)
        {
            if (!Awake.Add(p.Id)) { p.Dispose(); continue; }

            var handle = OpenProcess(SetInformation | SetQuota | QueryLimited, false, p.Id);
            if (handle != IntPtr.Zero)
            {
                var full = new PowerThrottling
                {
                    Version = 1,
                    ControlMask = ExecutionSpeed,
                    StateMask = 0,
                };
                SetProcessInformation(handle, PowerThrottlingInfo, ref full, Marshal.SizeOf<PowerThrottling>());
                CloseHandle(handle);
            }

            try
            {
                if (p.PriorityClass is ProcessPriorityClass.Idle or ProcessPriorityClass.BelowNormal)
                    p.PriorityClass = ProcessPriorityClass.Normal;
            }
            catch { }
            p.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottling
    {
        public uint Version, ControlMask, StateMask;
    }

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessInformation(IntPtr process, int kind, ref PowerThrottling info, int size);

    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    public static void Quit(string dir)
    {
        var live = Live(dir);
        foreach (var p in live)
        {
            try { if (!p.CloseMainWindow()) p.Kill(entireProcessTree: true); } catch { }
        }
        foreach (var p in live)
        {
            try { if (!p.WaitForExit(6000)) p.Kill(entireProcessTree: true); } catch { }
            p.Dispose();
        }
    }

    public static async Task<(string Version, string Url)?> LatestAsync()
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, Release);
            request.Headers.Add("User-Agent", "OCStartup");   // GitHub без него отвечает отказом
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var resp = await Http.SendAsync(request, cts.Token);
            resp.EnsureSuccessStatusCode();

            var root = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token)).RootElement;
            var version = (root.GetProperty("tag_name").GetString() ?? "").TrimStart('v');
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
                if (string.Equals(asset.GetProperty("name").GetString(), Asset, StringComparison.OrdinalIgnoreCase))
                    return (version, asset.GetProperty("browser_download_url").GetString() ?? "");
            return null;
        }
        catch { return null; }
    }

    public async Task InstallAsync(string dir, IProgress<InstallReport> report, CancellationToken ct)
    {
        Directory.CreateDirectory(dir);

        var release = await LatestAsync()
            ?? throw new InvalidOperationException(Loc.S("L_ReleaseMissing"));
        await FetchAsync(dir, release, (caption, percent) => report.Report(new InstallReport(0, percent, caption)), ct);
        report.Report(new InstallReport(0, 100, Loc.F("L_OpenCodeReady", release.Version), Done: true));

        if (OllamaHost.IsOwn(dir))
            OllamaHost.Settle(dir);
        else
        {
            var ollama = await Updates.LatestOllamaAsync();
            if (ollama is not { Url.Length: > 0 } found)
                throw new InvalidOperationException(Loc.S("L_OllamaReleaseMissing"));
            await Updates.InstallOllamaAsync(dir, found,
                (caption, percent) => report.Report(new InstallReport(1, percent, caption)), ct);
        }
        report.Report(new InstallReport(1, 100, Loc.S("L_StepOllama"), Done: true));

        var models = Loc.S("L_StepModels");
        report.Report(new InstallReport(2, -1, models));
        await OllamaHost.UseModelsDirAsync(Path.Combine(dir, "models"),
            line => report.Report(new InstallReport(2, -1, models, Log: line)));
        report.Report(new InstallReport(2, 100, models, Done: true));

        report.Report(new InstallReport(3, 100, Loc.S("L_StepConfig"), Done: true));
    }

    public async Task UpdateAsync(string dir, Action<string, double> report, CancellationToken ct)
    {
        var release = await LatestAsync()
            ?? throw new InvalidOperationException(Loc.S("L_ReleaseMissing"));
        await FetchAsync(dir, release, report, ct);
    }

    /// <summary>NSIS: /S ставит молча, /D обязан идти последним и без кавычек.</summary>
    private static async Task FetchAsync(string dir, (string Version, string Url) release,
                                         Action<string, double> report, CancellationToken ct)
    {
        var file = DownloadPath(dir, Asset);
        try
        {
            await DownloadAsync(release.Url, file,
                percent => report(Loc.F("L_DownloadingOpenCode", release.Version), percent), ct);
            report(Loc.S("L_Installing"), -1);

            Quit(dir);
            await Task.Delay(1000, ct);

            var owner = Registered();
            var aside = await ParkAsync(owner, ct);
            try
            {
                var setup = File.Exists(file) ? file : Path.Combine(aside!, "download", Asset);
                var psi = new ProcessStartInfo(setup, $"/S /D={Path.Combine(dir.TrimEnd('\\'), "OpenCode")}")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                psi.Environment["TMP"] = psi.Environment["TEMP"] = Path.GetDirectoryName(setup);
                using var installer = Process.Start(psi)!;
                await installer.WaitForExitAsync(ct);
                if (installer.ExitCode != 0)
                    throw new InvalidOperationException(Loc.F("L_SetupFailed", installer.ExitCode));
            }
            finally
            {
                if (aside is not null)
                {
                    await UnparkAsync(owner!, aside);
                    OllamaHost.Start(OllamaHost.ModelsDir(), OllamaHost.Context);
                }
            }
        }
        finally
        {
            DropDownload(file);
        }
    }

    private static string? Registered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(InstallKey);
        return key?.GetValue("InstallLocation") as string;
    }

    /// <summary>Версия 1.0.0 ставила OpenCode рядом с моделями: установщик удалил бы и их.</summary>
    internal static async Task<string?> ParkAsync(string? owner, CancellationToken ct)
    {
        if (owner is null) return null;
        bool Holds(string name) => Directory.Exists(Path.Combine(owner, name));
        if (Path.GetDirectoryName(owner) is null)
            return Ours.Any(Holds) ? throw new InvalidOperationException(Loc.F("L_DriveRoot", owner)) : null;

        var aside = Aside(owner);
        if (Directory.Exists(aside)) await UnparkAsync(owner, aside);

        var found = Ours.Where(Holds).ToList();
        if (found.Count == 0) return null;

        OllamaHost.Stop();
        await Task.Delay(1500, ct);

        Directory.CreateDirectory(aside);
        try
        {
            foreach (var name in found) Directory.Move(Path.Combine(owner, name), Path.Combine(aside, name));
        }
        catch
        {
            await UnparkAsync(owner, aside);
            throw;
        }
        return aside;
    }

    /// <summary>Имя не должно начинаться с имени папки: старый установщик снимает процессы по началу пути, а запущен он отсюда.</summary>
    private static string Aside(string owner)
    {
        var path = owner.TrimEnd('\\');
        return Path.Combine(Path.GetDirectoryName(path)!, ".keep-" + Path.GetFileName(path));
    }

    internal static async Task UnparkAsync(string owner, string aside)
    {
        Directory.CreateDirectory(owner);
        foreach (var name in Ours.Where(name => name != "download"))
        {
            var from = Path.Combine(aside, name);
            if (!Directory.Exists(from)) continue;

            // Снятый процесс отпускает файлы не сразу.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    Directory.Move(from, Path.Combine(owner, name));
                    break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 10)
                {
                    await Task.Delay(1000);
                }
            }
        }
        TryDelete(aside);
    }

    public static void WriteConfig(string dir, OpenCodeModel model)
    {
        var entry = new Dictionary<string, object>
        {
            ["name"] = model.Name,
            ["tool_call"] = model.Tools,
            ["reasoning"] = model.Reasoning,
            ["limit"] = new Dictionary<string, object>
            {
                ["context"] = model.Context,
                ["output"] = model.Output,
            },
        };

        var config = new Dictionary<string, object>
        {
            ["$schema"] = "https://opencode.ai/config.json",
            ["autoupdate"] = false,
            ["enabled_providers"] = new[] { "ollama" },
            ["model"] = $"ollama/{model.Name}",
            ["small_model"] = $"ollama/{model.Name}",
            ["provider"] = new Dictionary<string, object>
            {
                ["ollama"] = new Dictionary<string, object>
                {
                    ["npm"] = "@ai-sdk/openai-compatible",
                    ["name"] = "Ollama (local)",
                    ["options"] = new Dictionary<string, object> { ["baseURL"] = $"{Ollama.Base}/v1" },
                    ["models"] = new Dictionary<string, object> { [model.Name] = entry },
                },
            },
        };

        Directory.CreateDirectory(dir);
        File.WriteAllText(ConfigFile(dir),
            JsonSerializer.Serialize(config, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }),
            new UTF8Encoding(false));
    }

    public static bool Launch(string dir, string workspace, string? sessionId, string[] titles, Action<string> log)
    {
        var fresh = !Running(dir);
        if (fresh) OpenCodeDesktop.Prepare(workspace, sessionId);

        // Без этих флагов Chromium усыпляет фоновые процессы, и работа агента встаёт.
        var flags = string.Join(' ',
            "--force-renderer-accessibility",       // нужен нам: по нему находится вкладка
            "--disable-background-timer-throttling",
            "--disable-renderer-backgrounding",
            "--disable-backgrounding-occluded-windows",
            "--disable-features=CalculateNativeWinOcclusion,IntensiveWakeUpThrottling," +
                "UseEcoQoSForBackgroundProcess,BackgroundTabProcessScheduling");

        var psi = new ProcessStartInfo(Exe(dir), flags)
        {
            WorkingDirectory = workspace,
            UseShellExecute = false,
        };
        psi.Environment["OPENCODE_CONFIG"] = ConfigFile(dir);
        psi.Environment["OPENCODE_DISABLE_AUTOUPDATE"] = "1";
        psi.Environment["OLLAMA_MODELS"] = OllamaHost.ModelsDir();
        Process.Start(psi);

        _ = OpenCodeDesktop.OpenTabAsync(dir, titles, log);
        return fresh;
    }

    internal static string DownloadPath(string dir, string name)
    {
        var folder = Path.Combine(dir, "download");
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, name);
    }

    internal static void DropDownload(string file)
    {
        TryDelete(file);
        try { Directory.Delete(Path.GetDirectoryName(file)!); } catch { }
    }

    internal static async Task DownloadAsync(string url, string file, Action<double> pct, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? 0;
        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = File.Create(file);
        var buffer = new byte[81920];
        long got = 0, shown = -1;
        int read;
        while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            got += read;
            if (total > 0 && got * 100 / total != shown) pct(shown = got * 100 / total);
        }
    }

    internal static void TryDelete(params string[] targets)
    {
        foreach (var target in targets)
        {
            try
            {
                if (Directory.Exists(target)) Directory.Delete(target, true);
                else if (File.Exists(target)) File.Delete(target);
            }
            catch { }
        }
    }
}

public static class OpenCodeDesktop
{
    private const string Server = "sidecar";

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ai.opencode.desktop");

    public static void Prepare(string workspace, string? sessionId)
    {
        if (!Directory.Exists(Dir)) return;
        try
        {
            AddProject(workspace);
            OpenTab(workspace, sessionId);
        }
        catch { }
    }

    public static async Task OpenTabAsync(string dir, string[] titles, Action<string> log)
    {
        if (titles.Length == 0) return;
        try
        {
            for (var i = 0; i < 120; i++)
            {
                await Task.Delay(500);
                var window = OpenCode.Window(dir);
                if (window == IntPtr.Zero) continue;
                if (Click(window, titles)) { log(Loc.S("L_LogTabOpened")); return; }
            }
            log(Loc.S("L_LogNoTab"));
        }
        catch (Exception ex) { log(ex.Message); }
    }

    private static bool Click(IntPtr window, string[] titles)
    {
        AutomationElement root;
        try { root = AutomationElement.FromHandle(window); }
        catch { return false; }

        var strip = root.Current.BoundingRectangle.Top + 48;
        AutomationElement? found = null;
        var right = double.MinValue;

        foreach (AutomationElement item in root.FindAll(TreeScope.Descendants, Condition.TrueCondition))
        {
            try
            {
                var box = item.Current.BoundingRectangle;
                if (box.IsEmpty || box.Top > strip || box.Height > 40 || box.Width < 80) continue;
                // У вкладки внутри есть крестик: он тоже элемент, и нажимать
                // нужно именно саму вкладку, а не её обёртку с ним вместе.
                if (item.FindFirst(TreeScope.Children, Condition.TrueCondition) is not null) continue;

                var name = item.Current.Name ?? "";
                if (!titles.Any(t => name.Contains(t, StringComparison.Ordinal))) continue;
                if (box.Left <= right) continue;
                right = box.Left;
                found = item;
            }
            catch { }
        }

        if (found is null) return false;
        try
        {
            ((InvokePattern)found.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            return true;
        }
        catch { return false; }
    }

    private static void AddProject(string workspace)
    {
        var file = Path.Combine(Dir, "opencode.global.dat");
        var store = Load(file);

        var server = Nested(store, "server");
        var projects = Child(server, "projects");
        projects["local"] = new JsonArray(new JsonObject { ["worktree"] = workspace, ["expanded"] = true });
        Child(server, "lastProject")["local"] = workspace;
        store["server"] = server.ToJsonString();

        var layout = Nested(store, "layout");
        Child(layout, "home")["selection"] = new JsonObject
        {
            ["server"] = Server,
            ["directory"] = workspace,
        };
        store["layout"] = layout.ToJsonString();

        Save(file, store);
    }

    private static void OpenTab(string workspace, string? sessionId)
    {
        var settingsFile = Path.Combine(Dir, "opencode.settings");
        var settings = Load(settingsFile);
        if (settings["windowIds"] is JsonArray windows && windows.Count > 1)
        {
            while (windows.Count > 1) windows.RemoveAt(1);
            Save(settingsFile, settings);
        }

        // Список windowIds устаревает: окно бывает и не записано в нём, поэтому правим все файлы окон.
        foreach (var file in Directory.GetFiles(Dir, "opencode.window.*.dat"))
        {
            var store = Load(file);
            var tabs = store["tabs"] is JsonValue value && value.TryGetValue<string>(out var text)
                       && JsonNode.Parse(text) is JsonArray list ? list : new JsonArray();

            var tab = !string.IsNullOrEmpty(sessionId)
                ? new JsonObject { ["type"] = "session", ["server"] = Server, ["sessionId"] = sessionId }
                : new JsonObject
                {
                    ["type"] = "draft",
                    ["draftID"] = (string?)tabs.FirstOrDefault(t => (string?)t?["type"] == "draft"
                        && string.Equals((string?)t?["directory"], workspace, StringComparison.OrdinalIgnoreCase))?["draftID"]
                        ?? Guid.NewGuid().ToString(),
                    ["server"] = Server,
                    ["directory"] = workspace,
                };

            store["tabs"] = new JsonArray(tab).ToJsonString();
            store.Remove("tabs.recent");
            store.Remove("tabs.closed");
            Save(file, store);
        }
    }

    private static JsonObject Load(string file)
    {
        try { return JsonNode.Parse(File.ReadAllText(file))?.AsObject() ?? new JsonObject(); }
        catch { return new JsonObject(); }
    }

    private static void Save(string file, JsonObject store) =>
        File.WriteAllText(file, store.ToJsonString(), new UTF8Encoding(false));

    /// <summary>Значения в этих файлах - сами по себе JSON, просто строкой.</summary>
    private static JsonObject Nested(JsonObject store, string key) =>
        store[key] is JsonValue value && value.TryGetValue<string>(out var text)
        && JsonNode.Parse(text) is JsonObject obj ? obj : new JsonObject();

    private static JsonObject Child(JsonObject parent, string key)
    {
        if (parent[key] is JsonObject existing) return existing;
        var made = new JsonObject();
        parent[key] = made;
        return made;
    }

    private static string Base64Url(string text) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(text))
               .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public static class OllamaLog
{
    public static string File => Paths.In("ollama.log");

    private static readonly string[] Marks =
    {
        "out of memory", "bad_alloc", "cudaMalloc", "error:", "exception", "failed to allocate",
    };

    public static string? LastTrouble(TimeSpan fresh)
    {
        try
        {
            using var stream = new FileStream(File, FileMode.Open, FileAccess.Read,
                                              FileShare.ReadWrite | FileShare.Delete);
            var tail = Math.Max(0, stream.Length - 64 * 1024);
            stream.Seek(tail, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            var lines = reader.ReadToEnd().Split('\n');

            for (var i = lines.Length - 1; i >= 0; i--)
            {
                var line = lines[i].Trim();
                if (line.Length == 0) continue;
                if (line.Contains("level=INFO", StringComparison.Ordinal)) continue;
                if (line.Contains("level=WARN", StringComparison.Ordinal)) continue;
                if (!Marks.Any(m => line.Contains(m, StringComparison.OrdinalIgnoreCase))) continue;
                if (Stamp(line) is { } when && DateTime.Now - when > fresh) return null;
                return line.Length > 160 ? line[..160] : line;
            }
        }
        catch { }
        return null;
    }

    private static DateTime? Stamp(string line)
    {
        var found = Regex.Match(line, @"time=(\S+)");
        return found.Success && DateTime.TryParse(found.Groups[1].Value, out var when) ? when : null;
    }
}

public static class OpenCodeLog
{
    private static string LogFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".local", "share", "opencode", "log", "opencode.log");

    private static long _offset;

    public static void Mark()
    {
        try { _offset = new FileInfo(LogFile).Length; }
        catch { _offset = 0; }
    }

    public static string? Trouble()
    {
        try
        {
            var info = new FileInfo(LogFile);
            if (!info.Exists) return null;
            if (info.Length < _offset) _offset = 0;
            if (info.Length == _offset) return null;

            using var stream = new FileStream(LogFile, FileMode.Open, FileAccess.Read,
                                              FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(_offset, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();
            _offset = stream.Position;

            foreach (var line in text.Split('\n'))
            {
                if (!line.Contains("level=ERROR", StringComparison.Ordinal)) continue;
                // Без сессии это ошибка самого приложения, а не модели.
                if (!line.Contains("session.id=", StringComparison.Ordinal)) continue;
                var found = Regex.Match(line, "error(?:\\.error)?=\"(.*?)\"");
                if (!found.Success) continue;

                var trouble = found.Groups[1].Value.Split(new[] { "\\n" }, StringSplitOptions.None)[0].Trim();
                if (trouble.Length > 0) return trouble;
            }
            return null;
        }
        catch { return null; }
    }
}

public static class OpenCodeSessions
{
    private static string Database => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".local", "share", "opencode", "opencode.db");

    public static List<SessionRow> List(string directory)
    {
        var rows = new List<SessionRow>();
        if (!File.Exists(Database) || string.IsNullOrWhiteSpace(directory)) return rows;

        var copy = Path.Combine(Path.GetTempPath(), "oc-startup-sessions.db");
        try
        {
            // Копируем базу и её журнал, но не -shm: он описывает состояние
            // чужого процесса, и с чужим -shm SQLite не станет читать журнал -
            // свежие сессии просто не покажутся.
            File.Copy(Database, copy, true);
            if (File.Exists(Database + "-wal")) File.Copy(Database + "-wal", copy + "-wal", true);
            else if (File.Exists(copy + "-wal")) File.Delete(copy + "-wal");
            if (File.Exists(copy + "-shm")) File.Delete(copy + "-shm");

            // Pooling=False обязателен: иначе соединение остаётся в пуле,
            // держит файл копии открытым, и следующее чтение падает на copy.
            using var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={copy};Pooling=False");
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText =
                "select id, title, coalesce(time_updated, time_created) as stamp from session " +
                "where time_archived is null " +
                "  and replace(lower(directory), '\', '/') = $dir " +
                "order by stamp desc limit 100";
            command.Parameters.AddWithValue("$dir", Normalize(directory));

            using var reader = command.ExecuteReader();
            while (reader.Read())
                rows.Add(new SessionRow(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? "" : reader.GetString(1),
                    reader.IsDBNull(2) ? 0 : reader.GetInt64(2)));
        }
        catch { }
        return rows;
    }

    private static string Normalize(string directory) =>
        directory.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
}

public sealed record OpenCodeModel(string Name, int Context, int Output, bool Tools, bool Reasoning);

public readonly record struct InstallReport(int Step, double Percent, string Caption, string? Log = null, bool Done = false);
