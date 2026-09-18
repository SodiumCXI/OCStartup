using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Automation;
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
    /// <summary>Язык интерфейса. Пусто - ещё не выбирали, берём язык системы.</summary>
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
            // Пишем рядом и переставляем: выключение посреди записи оставило бы
            // обрезанный файл, а с ним приложение забывает все настройки.
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
    /// <summary>Окно контекста самой модели: чтобы знать его до запуска Ollama.</summary>
    public int? ModelContext { get; set; }
    public int? MaxTokens { get; set; }
    /// <summary>Рассуждения у модели: OpenCode знает про них только «да» или «нет».</summary>
    public bool? Reasoning { get; set; }
    public Dictionary<string, string> Params { get; set; } = new();
    /// <summary>
    /// Параметры, с которыми модель приехала: нужны кнопке «Сбросить». Пусто и
    /// «ещё не смотрели» - разные вещи: свои правки уходят в саму модель, и
    /// приняв их за заводские, мы бы уже никогда не вернули её к исходному.
    /// </summary>
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

    /// <summary>
    /// Прогрев: просим Ollama просто загрузить модель в память - как она это
    /// делает сама, без наших указаний. Вернёт текст ошибки, если не влезла:
    /// лучше сказать об этом самим, чем оставить человека наедине с невнятной
    /// красной строкой в чужом окне.
    /// </summary>
    public static Task<string?> WarmAsync(string model, CancellationToken ct = default) =>
        AskAsync("/api/generate", new Dictionary<string, object>
        {
            ["model"] = model,
            ["prompt"] = "",
            ["stream"] = false,
        }, ct);

    /// <summary>
    /// Короткий вопрос уже загруженной модели. Загрузиться она может и молча,
    /// а сломаться на первом же ответе - шаблон, инструменты, нехватка памяти
    /// под ответ. Лучше это выяснить здесь, чем в чужом окне.
    /// </summary>
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

    /// <summary>Запрос к Ollama: null - всё хорошо, иначе её же текст ошибки.</summary>
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

        // Ollama отдаёт простыню из нескольких строк - человеку хватит первой.
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

    /// <summary>
    /// Переписывает параметры модели поверх неё самой (from = она же). Слои
    /// не копируются - рождается только новый манифест, поэтому это дёшево.
    /// </summary>
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
        await resp.Content.ReadAsStringAsync(cts.Token);   // поток ответа дочитываем до конца

        // stop приходит списком, числа - числами: строкой Ollama их не примет.
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

    /// <summary>Тянет модель, отдавая долю и подпись шага по потоку JSON-строк.</summary>
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

/// <summary>
/// Сама программа Ollama: где лежат её модели и как её перезапустить.
/// Папку моделей она читает только при старте, поэтому смена пути без
/// перезапуска ничего не меняет.
/// </summary>
public static class OllamaHost
{
    private static string Programs =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama");

    private static string Tray => Path.Combine(Programs, "ollama app.exe");
    private static string Cli => Path.Combine(Programs, "ollama.exe");

    public static bool IsInstalled() => File.Exists(Tray) || File.Exists(Cli);

    /// <summary>
    /// Версия установленной Ollama. Спрашиваем у самой программы, а не у её
    /// сервера: когда он не поднят, версия всё равно нужна - иначе выходит,
    /// что стоит неизвестно что, и любое сравнение с выпуском врёт.
    /// </summary>
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
            // Без сервера она пишет «client version is X», с ним - «ollama version is X».
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

    /// <summary>
    /// Кладёт путь из реестра в собственное окружение. Дочерние процессы -
    /// установщик Ollama и сама Ollama - наследуют переменные от нас, а мы
    /// от оболочки, которая могла запуститься ещё со старым путём. Именно так
    /// после обновления Ollama уходила искать модели в прежнюю папку.
    /// </summary>
    public static void SyncProcessEnv()
    {
        var dir = ModelsDir();
        if (!string.Equals(Environment.GetEnvironmentVariable("OLLAMA_MODELS"), dir,
                           StringComparison.OrdinalIgnoreCase))
            Environment.SetEnvironmentVariable("OLLAMA_MODELS", dir);
    }

    /// <summary>
    /// Сколько байт модели уже прочитано с диска. О ходе загрузки Ollama не
    /// сообщает никак, зато счётчик чтения её рабочего процесса растёт ровно
    /// до размера модели - по нему и видно, сколько из скольки загрузилось.
    /// </summary>
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

    public static bool HasModels()
    {
        var manifests = Path.Combine(ModelsDir(), "manifests");
        return Directory.Exists(manifests) &&
               Directory.EnumerateFiles(manifests, "*", SearchOption.AllDirectories).Any();
    }

    public static void Stop()
    {
        foreach (var name in new[] { "ollama app", "ollama" })
            foreach (var p in Process.GetProcessesByName(name))
            {
                try { p.Kill(entireProcessTree: true); p.WaitForExit(15000); } catch { }
            }
    }

    public static int Context { get; private set; }

    /// <summary>
    /// Поднимаем именно ollama.exe serve: «ollama app.exe» тянет за собой своё
    /// окно и лезет поверх чужих, а нам нужен только сервер.
    /// Окно контекста задаётся при запуске: без него Ollama берёт полное окно
    /// модели, а это десятки гигабайт под буферы и отказ загрузки.
    /// </summary>
    public static void Start(string modelsDir, int context = 0)
    {
        if (!IsInstalled()) return;

        // Свой вывод Ollama никуда не пишет - её журнал ведёт только приложение
        // из трея. Заворачиваем вывод в файл через cmd: труба оборвалась бы
        // вместе с нами, а файл останется её собственным.
        var psi = File.Exists(Cli)
            ? new ProcessStartInfo("cmd.exe", $"/c \"\"{Cli}\" serve > \"{OllamaLog.File}\" 2>&1\"")
              { CreateNoWindow = true }
            : new ProcessStartInfo(Tray) { WindowStyle = ProcessWindowStyle.Hidden };
        psi.UseShellExecute = false;
        psi.Environment["OLLAMA_MODELS"] = modelsDir;
        if (context > 0) psi.Environment["OLLAMA_CONTEXT_LENGTH"] = context.ToString();
        Context = context;
        try { Process.Start(psi); } catch { }
    }

    /// <summary>
    /// Поднять Ollama с нужным окном контекста. Своё умолчание у неё 4096 -
    /// для работы с инструментами этого мало, поэтому окно модели ей задаём
    /// сами. Читается оно только при запуске, так что ради другого окна
    /// Ollama приходится перезапускать.
    /// </summary>
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

    /// <summary>Переселяет модели в папку установки: на том же диске это переименование.</summary>
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
        await Task.Delay(1500);   // дать серверу отпустить файлы

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (Directory.Exists(current) && Directory.Exists(target))
            log(Loc.S("L_LogTargetExists"));
        else if (Directory.Exists(current) && SameVolume(current, target))
            Directory.Move(current, target);
        else if (Directory.Exists(current))
        {
            // Перенести не смогли - значит и переключать некуда: иначе Ollama
            // уедет на пустую папку, а модели «пропадут».
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

    /// <summary>Версии модели вместе с весом. latest - не отдельная версия, а пометка.</summary>
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

        // latest - псевдоним: убираем его из списка и метим ту версию, на которую он указывает.
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
                var name = asset.GetProperty("name").GetString() ?? "";
                if (name.StartsWith("OllamaSetup", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe"))
                    return (version, asset.GetProperty("browser_download_url").GetString() ?? "");
            }
            return (version, "");
        }
        catch { return null; }
    }

    /// <summary>Качает установщик Ollama и запускает его: дальше решает человек.</summary>
    public static async Task<string> RunOllamaSetupAsync(string url, IProgress<double> percent, CancellationToken ct)
    {
        var file = Path.Combine(Path.GetTempPath(), "OllamaSetup.exe");
        using (var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? 0;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(file);
            var buffer = new byte[81920];
            long got = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                got += read;
                if (total > 0) percent.Report(got * 100.0 / total);
            }
        }

        var psi = new ProcessStartInfo(file) { UseShellExecute = false };
        psi.Environment["OLLAMA_MODELS"] = OllamaHost.ModelsDir();
        Process.Start(psi);
        return file;
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

/// <summary>
/// Приложение OpenCode: установка с GitHub, файл настроек и запуск. Своего
/// сервера у нас нет - его поднимает само приложение, поэтому наша часть это
/// настройки, переменные окружения и подготовленное окно.
/// </summary>
public sealed class OpenCode
{
    private const string Release = "https://api.github.com/repos/anomalyco/opencode/releases/latest";
    private const string Asset = "opencode-desktop-win-x64.exe";
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    public static string Exe(string dir) => Path.Combine(dir, "OpenCode.exe");
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

    /// <summary>
    /// Само окно. У Electron процессов много, и служебные тоже отдают дескриптор,
    /// поэтому берём то, у которого есть заголовок - настоящее окно одно.
    /// </summary>
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
            catch { p.Dispose(); }   // чужой процесс под другим пользователем нам не отвечает
        }
        return found;
    }

    /// <summary>Кому уже сказали работать в полную силу: второй раз не нужно.</summary>
    private static readonly HashSet<int> Awake = new();

    /// <summary>
    /// Запретить процессам OpenCode работать вполсилы. Windows умеет это
    /// только для каждого процесса отдельно, общего «не усыплять приложение»
    /// у неё нет, а Chromium заводит свои процессы по ходу дела - поэтому
    /// говорим это каждому новому, и ровно один раз.
    /// </summary>
    public static void KeepAwake(string dir)
    {
        const int PowerThrottlingInfo = 4;
        const uint ExecutionSpeed = 1;
        const uint SetInformation = 0x0200, SetQuota = 0x0100, QueryLimited = 0x1000;

        var live = Live(dir);
        if (live.Count == 0)
        {
            Awake.Clear();      // приложение закрылось, номера можно забыть
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
                    ControlMask = ExecutionSpeed,   // скоростью управляем мы...
                    StateMask = 0,                  // ...и требуем полную
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
        await FetchAsync(dir, release, pct => report.Report(
            new InstallReport(0, pct, Loc.F("L_DownloadingOpenCode", release.Version))), ct);
        report.Report(new InstallReport(0, 100, Loc.F("L_OpenCodeReady", release.Version), Done: true));

        // Модели держим рядом с самим OpenCode, а не в стороннем каталоге.
        var models = Loc.S("L_StepModels");
        report.Report(new InstallReport(1, -1, models));
        await OllamaHost.UseModelsDirAsync(Path.Combine(dir, "models"),
            line => report.Report(new InstallReport(1, -1, models, Log: line)));
        report.Report(new InstallReport(1, 100, models, Done: true));

        // Настройки пишутся перед каждым запуском - здесь только место под них.
        Directory.CreateDirectory(dir);
        report.Report(new InstallReport(2, 100, Loc.S("L_StepConfig"), Done: true));
    }

    public async Task UpdateAsync(string dir, IProgress<double> percent, CancellationToken ct)
    {
        var release = await LatestAsync()
            ?? throw new InvalidOperationException(Loc.S("L_ReleaseMissing"));
        await FetchAsync(dir, release, percent.Report, ct);
    }

    /// <summary>
    /// Установщик OpenCode - обычный NSIS: /S ставит молча, /D задаёт папку и
    /// обязан идти последним и без кавычек.
    /// </summary>
    private static async Task FetchAsync(string dir, (string Version, string Url) release,
                                         Action<double> percent, CancellationToken ct)
    {
        var setup = Path.Combine(Path.GetTempPath(), Asset);
        await DownloadAsync(release.Url, setup, percent, ct);

        Quit(dir);                       // работающее приложение установщик не перезапишет
        await Task.Delay(1000, ct);

        var psi = new ProcessStartInfo(setup, $"/S /D={dir.TrimEnd('\\')}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var installer = Process.Start(psi)!;
        await installer.WaitForExitAsync(ct);
        if (installer.ExitCode != 0)
            throw new InvalidOperationException(Loc.F("L_SetupFailed", installer.ExitCode));
        TryDelete(setup);
    }

    /// <summary>
    /// Файл настроек OpenCode. Пишется перед каждым запуском и содержит ровно
    /// одну модель: всё, что попадёт сюда, можно выбрать прямо в окне OpenCode,
    /// а запускать модель мимо её настроек нельзя.
    /// </summary>
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
            ["autoupdate"] = false,          // обновлениями занимается это приложение
            ["enabled_providers"] = new[] { "ollama" },   // облачные провайдеры в списке не нужны
            ["model"] = $"ollama/{model.Name}",
            ["small_model"] = $"ollama/{model.Name}",     // мелкие задачи той же моделью
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

    /// <summary>
    /// Запуск приложения в выбранном проекте. Возвращает управление сразу:
    /// открыть нужную вкладку - дело фоновое, окну запуска ждать незачем.
    /// Флаг доступности нужен нам самим: без него дерево окна пустое и найти
    /// в нём вкладку невозможно.
    /// </summary>
    public static bool Launch(string dir, string workspace, string? sessionId, string[] titles, Action<string> log)
    {
        var fresh = !Running(dir);
        if (fresh) OpenCodeDesktop.Prepare(workspace, sessionId);

        // Флаги Chromium, без которых он усыпляет собственные фоновые процессы:
        // стоит убрать окно OpenCode из виду - и его работа (поток ответа,
        // вызовы инструментов) встаёт, а модель молча простаивает.
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

    private static async Task DownloadAsync(string url, string file, Action<double> pct, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? 0;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(file);
        var buffer = new byte[81920];
        long got = 0;
        int read;
        while ((read = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), ct);
            got += read;
            if (total > 0) pct(got * 100.0 / total);
        }
    }

    private static void TryDelete(params string[] targets)
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

/// <summary>
/// Своё окно OpenCode помнит сам: список проектов и открытые вкладки лежат
/// рядом с ним в файлах Electron. Правим их перед запуском, иначе приложение
/// откроется на прошлом проекте и без выбранной сессии. Формат чужой, поэтому
/// любая осечка молча оставляет файлы как были.
/// </summary>
public static class OpenCodeDesktop
{
    /// <summary>Ключ сервера внутри приложения: у встроенного он всегда такой.</summary>
    private const string Server = "sidecar";

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ai.opencode.desktop");

    public static void Prepare(string workspace, string? sessionId)
    {
        if (!Directory.Exists(Dir)) return;   // приложение ещё ни разу не запускали
        try
        {
            AddProject(workspace);
            OpenTab(workspace, sessionId);
        }
        catch { }
    }

    /// <summary>
    /// Переход на нужную вкладку. Дерево доступности окна - единственный
    /// надёжный способ: горячие клавиши приложение видит не всегда, а здесь
    /// вкладка сама говорит, что она уже отрисована, и сама себя открывает.
    /// Заголовок задан - ищем вкладку с ним, пусто - берём последнюю: свою мы
    /// только что дописали в конец.
    /// </summary>
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

    /// <summary>
    /// Нажать свою вкладку. Ищем её по заголовку: приложение при запуске
    /// возвращает и свои прошлые вкладки, поэтому «последняя в строке» - не
    /// обязательно наша. Пока вкладки нет, её просто ещё не отрисовали.
    /// </summary>
    private static bool Click(IntPtr window, string[] titles)
    {
        AutomationElement root;
        try { root = AutomationElement.FromHandle(window); }
        catch { return false; }

        var strip = root.Current.BoundingRectangle.Top + 48;   // полоса вкладок
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
        if (projects["local"] is not JsonArray local) projects["local"] = local = new JsonArray();
        if (!local.Any(p => string.Equals((string?)p?["worktree"], workspace, StringComparison.OrdinalIgnoreCase)))
            local.Add(new JsonObject { ["worktree"] = workspace, ["expanded"] = true });
        Child(server, "lastProject")["local"] = workspace;
        store["server"] = server.ToJsonString();

        // Тот же проект - выбранным на главной: откроется она - список сессий
        // будет уже нужной папки.
        var layout = Nested(store, "layout");
        Child(layout, "home")["selection"] = new JsonObject
        {
            ["server"] = Server,
            ["directory"] = workspace,
        };
        store["layout"] = layout.ToJsonString();

        Save(file, store);
    }

    /// <summary>Вкладка выбранной сессии или черновик новой - всегда последней.</summary>
    private static void OpenTab(string workspace, string? sessionId)
    {
        if (Load(Path.Combine(Dir, "opencode.settings"))["windowIds"] is not JsonArray windows) return;

        foreach (var id in windows.Select(w => (string?)w).Where(w => !string.IsNullOrEmpty(w)))
        {
            var file = Path.Combine(Dir, $"opencode.window.{id}.dat");
            if (!File.Exists(file)) continue;

            var store = Load(file);
            var tabs = store["tabs"] is JsonValue value && value.TryGetValue<string>(out var text)
                       && JsonNode.Parse(text) is JsonArray list ? list : new JsonArray();

            if (!string.IsNullOrEmpty(sessionId))
            {
                Drop(tabs, t => (string?)t?["sessionId"] == sessionId);
                tabs.Add(new JsonObject
                {
                    ["type"] = "session",
                    ["server"] = Server,
                    ["sessionId"] = sessionId,
                });
            }
            else
            {
                // Пустую вкладку этой же папки заводим одну, а не по штуке на запуск.
                var draft = tabs.FirstOrDefault(t => (string?)t?["type"] == "draft"
                    && string.Equals((string?)t?["directory"], workspace, StringComparison.OrdinalIgnoreCase));
                var draftId = (string?)draft?["draftID"] ?? Guid.NewGuid().ToString();
                Drop(tabs, t => (string?)t?["draftID"] == draftId);
                tabs.Add(new JsonObject
                {
                    ["type"] = "draft",
                    ["draftID"] = draftId,
                    ["server"] = Server,
                    ["directory"] = workspace,
                });
            }

            store["tabs"] = tabs.ToJsonString();
            Save(file, store);
        }

        static void Drop(JsonArray tabs, Func<JsonNode?, bool> match)
        {
            for (var i = tabs.Count - 1; i >= 0; i--)
                if (match(tabs[i])) tabs.RemoveAt(i);
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

/// <summary>
/// Вывод Ollama, перенаправленный нами в файл. Её ответ на сбой модели -
/// «смотрите логи сервера», и вот они: тут лежит настоящая причина вроде
/// нехватки видеопамяти.
/// </summary>
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
                // Служебные строки самой Ollama пересказывают ту же беду своими
                // словами и тянут за собой пути к файлам - нам нужна причина,
                // а её пишет llama.cpp своей строкой.
                if (line.Contains("level=INFO", StringComparison.Ordinal)) continue;
                if (line.Contains("level=WARN", StringComparison.Ordinal)) continue;
                if (!Marks.Any(m => line.Contains(m, StringComparison.OrdinalIgnoreCase))) continue;
                if (Stamp(line) is { } when && DateTime.Now - when > fresh) return null;   // старая беда
                return line.Length > 160 ? line[..160] : line;
            }
        }
        catch { }
        return null;
    }

    /// <summary>Строки Ollama начинаются с её же времени - по нему судим о свежести.</summary>
    private static DateTime? Stamp(string line)
    {
        var found = Regex.Match(line, @"time=(\S+)");
        return found.Success && DateTime.TryParse(found.Groups[1].Value, out var when) ? when : null;
    }
}

/// <summary>
/// Журнал OpenCode. Своих бед он никому не докладывает, но пишет их сюда:
/// отказ модели посреди работы видно только отсюда.
/// </summary>
public static class OpenCodeLog
{
    private static string LogFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".local", "share", "opencode", "log", "opencode.log");

    private static long _offset;

    /// <summary>Запомнить конец журнала: всё, что было до запуска, не наше.</summary>
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
            if (info.Length < _offset) _offset = 0;      // журнал начали заново
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
                var found = Regex.Match(line, "error(?:\\.error)?=\"(.*?)\"");
                if (!found.Success) continue;

                // Внутри значения переводы строк записаны как \n - берём первую.
                var trouble = found.Groups[1].Value.Split(new[] { "\\n" }, StringSplitOptions.None)[0].Trim();
                if (trouble.Length > 0) return trouble;
            }
            return null;
        }
        catch { return null; }
    }
}

/// <summary>
/// Сессии OpenCode лежат в его собственной базе. Читаем их оттуда, чтобы
/// показать список до запуска сервера: спрашивать API было бы не у кого.
/// </summary>
public static class OpenCodeSessions
{
    private static string Database => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".local", "share", "opencode", "opencode.db");

    public static List<SessionRow> List(string directory)
    {
        var rows = new List<SessionRow>();
        if (!File.Exists(Database) || string.IsNullOrWhiteSpace(directory)) return rows;

        // Работаем по копии: живую базу держит сам OpenCode, а её журнал WAL
        // при чтении нужно проигрывать на запись.
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

    /// <summary>В базе каталог записан через прямые косые: D:/temp.</summary>
    private static string Normalize(string directory) =>
        directory.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
}

public sealed record OpenCodeModel(string Name, int Context, int Output, bool Tools, bool Reasoning);

/// <summary>Шаг установки: номер, доля (-1 - неизвестна), подпись, строка лога.</summary>
public readonly record struct InstallReport(int Step, double Percent, string Caption, string? Log = null, bool Done = false);
