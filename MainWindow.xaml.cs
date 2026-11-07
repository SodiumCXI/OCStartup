using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace DshStudio;

public partial class MainWindow : Window
{
    private readonly Settings _settings = Settings.Load();
    private readonly OpenCode _opencode = new();
    private readonly ObservableCollection<ModelCard> _models = new();
    private readonly ObservableCollection<StepCard> _steps = new()
    {
        new StepCard(Loc.S("L_StepOpenCode")),
        new StepCard(Loc.S("L_StepModels")),
        new StepCard(Loc.S("L_StepConfig")),
    };
    private readonly ObservableCollection<SessionRow> _sessions = new();
    private readonly ObservableCollection<StepCard> _runSteps = new();
    private CancellationTokenSource? _launching;
    private readonly ObservableCollection<UpdateRow> _updates = new();
    private readonly ObservableCollection<object> _harnessRows = new();
    private readonly ObservableCollection<ParamRow> _modelRows = new();

    private List<string> _catalog = new();
    private List<CatalogTag> _tags = new();
    private string _ollamaSetup = "";
    private bool _ollamaNudged;
    private bool _ollamaWasUp;
    private bool _openCodeWasUp;
    private bool _pulsing;
    private TaskCompletionSource<bool>? _confirm;
    private ModelCard? _tuned;
    private ParamRow? _ctxRow;
    private ParamRow? _maxRow;
    private FlagRow? _reasonRow;
    private Dictionary<string, string> _shipped = new();

    /// <summary>/api/show отдаёт только то, что модель переопределила, остальное - умолчания Ollama.</summary>
    private static readonly (string Name, string Default)[] OllamaParams =
    {
        ("temperature", "0.8"),
        ("top_k", "40"),
        ("top_p", "0.9"),
        ("min_p", "0.0"),
        ("typical_p", "1.0"),
        ("repeat_penalty", "1.1"),
        ("repeat_last_n", "64"),
        ("presence_penalty", "0.0"),
        ("frequency_penalty", "0.0"),
        ("mirostat", "0"),
        ("mirostat_tau", "5.0"),
        ("mirostat_eta", "0.1"),
        ("seed", "0"),
        ("num_predict", "-1"),
        ("num_ctx", "4096"),
        ("num_keep", "4"),
        ("num_batch", "512"),
        ("stop", ""),
        ("num_gpu", ""),
        ("main_gpu", "0"),
        ("num_thread", ""),
        ("use_mmap", "true"),
        ("use_mlock", "false"),
    };

    private int ContextFor(string model, ModelFacts facts)
    {
        var tuning = _settings.Tuning(model);
        if (tuning.ModelContext != facts.Context)
        {
            tuning.ModelContext = facts.Context;
            _settings.Save();
        }
        return tuning.ContextWindow ?? facts.Context;
    }

    private int RememberedContext(string model)
    {
        var tuning = _settings.Tuning(model);
        return tuning.ContextWindow ?? tuning.ModelContext ?? 0;
    }

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute,
                                                                             ref int value, int size);

    private void Transitions(bool enabled)
    {
        const int DWMWA_TRANSITIONS_FORCEDISABLED = 3;
        var off = enabled ? 0 : 1;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero) DwmSetWindowAttribute(hwnd, DWMWA_TRANSITIONS_FORCEDISABLED, ref off, sizeof(int));
    }

    public MainWindow()
    {
        InitializeComponent();

        ModelList.ItemsSource = _models;
        StepList.ItemsSource = _steps;
        SessionList.ItemsSource = _sessions;
        LaunchSteps.ItemsSource = _runSteps;
        UpdateList.ItemsSource = _updates;
        HarnessRows.ItemsSource = _harnessRows;
        ModelRows.ItemsSource = _modelRows;
        DirBox.Text = _settings.InstallDir;
        WorkBox.Text = _settings.Workspace;

        LangList.SelectedIndex = Array.IndexOf(Loc.Codes, Loc.Current);
        if (_settings.Lang != Loc.Current) { _settings.Lang = Loc.Current; _settings.Save(); }
        WorkBox.LostFocus += (_, _) => ReloadSessions();
        ReloadSessions();

        SourceInitialized += (_, _) =>
        {
            const int GWL_STYLE = -16, WS_CAPTION = 0x00C00000, WS_MINIMIZEBOX = 0x00020000,
                      WS_MAXIMIZEBOX = 0x00010000, WS_THICKFRAME = 0x00040000;
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowLong(hwnd, GWL_STYLE,
                GetWindowLong(hwnd, GWL_STYLE) | WS_CAPTION | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_THICKFRAME);
        };
        PreviewKeyDown += (_, args) =>
        {
            if (args.Key is Key.Tab) args.Handled = true;
        };
        StateChanged += (_, _) => Root.Padding = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        Loaded += async (_, _) => await StartupAsync();

        var pulse = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3),
        };
        pulse.Tick += async (_, _) => await PulseAsync();
        pulse.Start();

        Closing += (_, _) =>
        {
            pulse.Stop();
            if (!OpenCode.Running(DirBox.Text.Trim())) OllamaHost.Stop();
            _settings.InstallDir = DirBox.Text.Trim();
            _settings.Save();
        };
    }

    internal void ShowScreen(string screen)
    {
        switch (screen)
        {
            case "setup": SetupOverlay.Visibility = Visibility.Visible; break;
            case "catalog": OpenCatalog_Click(this, new RoutedEventArgs()); break;
            case "tags": _ = ShowTagsAsync("qwen3.5"); break;
            case "confirm": _ = AskAsync(Loc.F("L_DeleteQuestion", "ornith-1.5:35b")); break;
            case "pull": _ = PullModelAsync("all-minilm:latest"); break;
            case "tune": _ = OpenTuneAsync(_models.FirstOrDefault()); break;
            case "log": LogPanel.Visibility = Visibility.Visible; break;
            case "lang": LangOverlay.Visibility = Visibility.Visible; break;
            case "config": _ = WriteConfigProbeAsync(); break;
            case "updates": OpenUpdates_Click(this, new RoutedEventArgs()); break;
            default: SetupOverlay.Visibility = Visibility.Collapsed; break;
        }
    }

    private async Task WriteConfigProbeAsync()
    {
        if (_models.FirstOrDefault(m => !m.Busy) is not { } card) return;
        var facts = await Ollama.FactsAsync(card.Name);
        OpenCode.WriteConfig(DirBox.Text.Trim(), new OpenCodeModel(
            card.Name, facts.Context, Math.Min(32768, facts.Context),
            facts.Capabilities.Contains("tools"), facts.Thinks));
    }

    private async Task ShowTagsAsync(string name)
    {
        OpenCatalog_Click(this, new RoutedEventArgs());
        for (var i = 0; i < 40 && _catalog.Count == 0; i++) await Task.Delay(200);
        CatalogList.SelectedItem = name;
    }

    private async Task StartupAsync()
    {
        OllamaHost.SyncProcessEnv();

        if (OllamaHost.IsInstalled() && await Ollama.VersionAsync() is null)
        {
            Log(Loc.F("L_LogOllamaStart", OllamaHost.ModelsDir()));
            OllamaHost.Start(OllamaHost.ModelsDir(), RememberedContext(_settings.LastModel));
            for (var i = 0; i < 20 && await Ollama.VersionAsync() is null; i++) await Task.Delay(500);
        }

        await RefreshHealthAsync();
        await RefreshModelsAsync();
        ReloadSessions();
        if (!OpenCode.IsInstalled(_settings.InstallDir)) SetupOverlay.Visibility = Visibility.Visible;
    }

    private async Task PulseAsync()
    {
        if (_pulsing) return;
        _pulsing = true;
        try
        {
            var up = await Ollama.VersionAsync() is not null;
            await RefreshHealthAsync();

            var dir = DirBox.Text.Trim();
            var opencode = OpenCode.Running(dir);

            if (opencode && !up && _ollamaWasUp)
            {
                Log(Loc.S("L_LogOllamaGone"));
                OpenCode.Quit(dir);
                opencode = false;
                if (!IsVisible) Reappear();
                Status(Loc.S("L_OllamaGone"));
            }

            if (opencode && OpenCodeLog.Trouble() is { } broke)
            {
                broke = Detailed(broke);
                Log(Loc.F("L_LogModelBroke", broke));
                OpenCode.Quit(dir);
                _ = Ollama.UnloadAsync(_settings.LastModel);
                opencode = false;
                if (!IsVisible) Reappear();
                Refuse(Loc.F("L_ModelBroke", broke));
            }

            if (opencode) OpenCode.KeepAwake(dir);

            if (_openCodeWasUp && !opencode) _ = Ollama.UnloadAsync(_settings.LastModel);
            _openCodeWasUp = opencode;

            StartBtn.Visibility = opencode ? Visibility.Collapsed : Visibility.Visible;
            StopBtn.Visibility = opencode ? Visibility.Visible : Visibility.Collapsed;
            if (!opencode && !IsVisible) Reappear();

            if (up && !_ollamaWasUp) await RefreshModelsAsync();
            _ollamaWasUp = up;
        }
        catch { }
        finally { _pulsing = false; }
    }

    private async void StartOllama_Click(object sender, RoutedEventArgs e)
    {
        StartOllamaBtn.IsEnabled = false;
        OllamaHost.SyncProcessEnv();
        OllamaHost.Start(OllamaHost.ModelsDir(), RememberedContext(_settings.LastModel));
        for (var i = 0; i < 20 && await Ollama.VersionAsync() is null; i++) await Task.Delay(500);
        StartOllamaBtn.IsEnabled = true;
        await RefreshHealthAsync();
        await RefreshModelsAsync();
    }

    private async Task RefreshHealthAsync()
    {
        var version = await Ollama.VersionAsync();
        var present = OllamaHost.IsInstalled();
        OllamaDot.Fill = (SolidColorBrush)FindResource(version is null ? "Bad" : "Good");
        OllamaText.Text = !present ? Loc.S("L_OllamaMissing")
                        : version is null ? Loc.S("L_OllamaStopped")
                        : $"Ollama {version}";
        StartOllamaBtn.Visibility = present && version is null ? Visibility.Visible : Visibility.Collapsed;

        var dir = DirBox.Text.Trim();
        var installed = OpenCode.IsInstalled(dir);
        HarnessText.Text = installed ? dir : Loc.S("L_OpenCodeMissing");
        InstallBtn.Content = Loc.S(installed ? "L_Reinstall" : "L_Install");
    }

    private async Task RefreshModelsAsync()
    {
        List<InstalledModel> installed;
        try { installed = await Ollama.ListAsync(); }
        catch (Exception ex) { Log(Loc.F("L_LogOllamaSilent", ex.Message)); return; }

        // Пустой список при непустой папке моделей: Ollama запущена со старым OLLAMA_MODELS.
        if (installed.Count == 0 && !_ollamaNudged && OllamaHost.HasModels())
        {
            _ollamaNudged = true;
            Log(Loc.F("L_LogOllamaBlind", OllamaHost.ModelsDir()));
            OllamaHost.Stop();
            await Task.Delay(1500);
            OllamaHost.Start(OllamaHost.ModelsDir(), OllamaHost.Context);
            await Task.Delay(4000);
            try { installed = await Ollama.ListAsync(); } catch { }
            await RefreshHealthAsync();
        }

        var busy = _models.Where(m => m.Busy).ToList();
        var selected = (ModelList.SelectedItem as ModelCard)?.Name ?? _settings.LastModel;

        _models.Clear();
        foreach (var card in busy) _models.Add(card);
        foreach (var m in installed)
        {
            if (busy.Any(b => b.Name == m.Name)) continue;
            _models.Add(new ModelCard(m.Name)
            {
                Size = $"{m.Size / 1024.0 / 1024 / 1024:0.#} {Loc.S("L_GB")}",
                Bytes = m.Size,
                Params = string.IsNullOrEmpty(m.Params) ? "-" : m.Params,
                Quant = Quantization(m.Quant),
            });
        }

        EmptyHint.Visibility = _models.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ModelList.SelectedItem = _models.FirstOrDefault(m => m.Name == selected) ?? _models.FirstOrDefault(m => !m.Busy);
    }

    private static string Quantization(string value) => value switch
    {
        "" or null => "-",
        var v when v.Equals("unknown", StringComparison.OrdinalIgnoreCase) => "Unknown",
        var v => v.ToUpperInvariant(),
    };

    private void Log(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        Dispatcher.Invoke(() =>
        {
            LogBox.AppendText(line + Environment.NewLine);
            if (LogBox.Text.Length > 200_000) LogBox.Text = LogBox.Text[100_000..];
            LogBox.ScrollToEnd();
        });
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.ClickCount == 2) { Maximize_Click(sender, e); return; }

        var wasMaximized = WindowState == WindowState.Maximized;
        if (wasMaximized)
        {
            var inside = e.GetPosition(this);
            var ratio = ActualWidth > 0 ? inside.X / ActualWidth : 0.5;
            var onScreen = PresentationSource.FromVisual(this)!.CompositionTarget!
                           .TransformFromDevice.Transform(PointToScreen(inside));
            Transitions(false);
            Left = onScreen.X - RestoreBounds.Width * ratio;
            Top = onScreen.Y - inside.Y;
            WindowState = WindowState.Normal;
        }

        try { DragMove(); }
        catch (InvalidOperationException) { }
        finally { if (wasMaximized) Transitions(true); }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleLog_Click(object sender, RoutedEventArgs e) =>
        LogPanel.Visibility = LogPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(LogBox.Text); } catch { }
    }

    private void OpenLang_Click(object sender, RoutedEventArgs e) => LangOverlay.Visibility = Visibility.Visible;

    private void CloseLang_Click(object sender, RoutedEventArgs e) => LangOverlay.Visibility = Visibility.Collapsed;

    private void LangList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LangOverlay.Visibility != Visibility.Visible) return;
        if ((LangList.SelectedItem as ListBoxItem)?.Tag is not string code) return;

        LangOverlay.Visibility = Visibility.Collapsed;
        if (code == Loc.Current) return;
        _settings.Lang = code;
        _settings.Save();
        UseLanguage(code);
    }

    private void UseLanguage(string code)
    {
        Loc.Use(code);
        LangList.SelectedIndex = Array.IndexOf(Loc.Codes, Loc.Current);

        _steps[0].Caption = Loc.S("L_StepOpenCode");
        _steps[1].Caption = Loc.S("L_StepModels");
        _steps[2].Caption = Loc.S("L_StepConfig");
        _updates.Clear();
        ReloadSessions();
        _ = RefreshHealthAsync();
        _ = RefreshModelsAsync();
    }

    private static string? PickFolder(string start)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Multiselect = false };
        if (Directory.Exists(start)) dialog.InitialDirectory = start;
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    private Task<bool> AskAsync(string question, string? action = null)
    {
        action ??= Loc.S("L_Delete");
        ConfirmText.Text = question;
        ConfirmYes.Content = action;
        ConfirmOverlay.Visibility = Visibility.Visible;
        _confirm = new TaskCompletionSource<bool>();
        return _confirm.Task;
    }

    private void ConfirmYes_Click(object sender, RoutedEventArgs e)
    {
        ConfirmOverlay.Visibility = Visibility.Collapsed;
        _confirm?.TrySetResult(true);
    }

    private void ConfirmNo_Click(object sender, RoutedEventArgs e)
    {
        ConfirmOverlay.Visibility = Visibility.Collapsed;
        _confirm?.TrySetResult(false);
    }

    private void OpenSetup_Click(object sender, RoutedEventArgs e) => SetupOverlay.Visibility = Visibility.Visible;

    private void CloseSetup_Click(object sender, RoutedEventArgs e) => SetupOverlay.Visibility = Visibility.Collapsed;

    private void PickInstallDir_Click(object sender, RoutedEventArgs e)
    {
        if (PickFolder(DirBox.Text) is { } picked) DirBox.Text = picked;
    }

    private void PickWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (PickFolder(WorkBox.Text) is { } picked)
        {
            WorkBox.Text = picked;
            _settings.Workspace = picked;
            _settings.Save();
            ReloadSessions();
        }
    }

    private void ReloadSessions()
    {
        var chosen = (SessionList.SelectedItem as SessionRow)?.Id;
        _sessions.Clear();
        _sessions.Add(SessionRow.Fresh);
        foreach (var session in OpenCodeSessions.List(WorkBox.Text.Trim())) _sessions.Add(session);

        SessionList.SelectedItem = _sessions.FirstOrDefault(s => s.Id == chosen) ?? _sessions[0];
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        var dir = DirBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(dir)) return;

        InstallBtn.IsEnabled = false;
        SetupClose.IsEnabled = false;
        SetupProgress.Visibility = Visibility.Visible;
        foreach (var step in _steps) { step.Done = false; step.Active = false; }
        LogPanel.Visibility = Visibility.Visible;
        Log(Loc.F("L_LogInstallTo", dir));

        var progress = new Progress<InstallReport>(r =>
        {
            for (var i = 0; i < _steps.Count; i++) _steps[i].Active = i == r.Step && !r.Done;
            if (r.Done) _steps[r.Step].Done = true;
            _steps[r.Step].Caption = r.Caption;

            SetupProgress.IsIndeterminate = r.Percent < 0;
            if (r.Percent >= 0) SetupProgress.Value = r.Percent;
            if (r.Log is { } line) Log(line);
        });

        try
        {
            await _opencode.InstallAsync(dir, progress, CancellationToken.None);
            _settings.InstallDir = dir;
            _settings.Save();
            Log(Loc.S("L_LogDone"));
            SetupOverlay.Visibility = Visibility.Collapsed;
            await RefreshModelsAsync();
        }
        catch (Exception ex)
        {
            Log(Loc.F("L_LogInstallFailed", ex.Message));
            foreach (var step in _steps) step.Active = false;
        }
        finally
        {
            InstallBtn.IsEnabled = true;
            SetupClose.IsEnabled = true;
            SetupProgress.Visibility = Visibility.Collapsed;
            await RefreshHealthAsync();
        }
    }

    private async void OpenUpdates_Click(object sender, RoutedEventArgs e)
    {
        UpdatesOverlay.Visibility = Visibility.Visible;
        if (_updates.Count == 0) await CheckUpdatesAsync();
    }

    private void CloseUpdates_Click(object sender, RoutedEventArgs e) =>
        UpdatesOverlay.Visibility = Visibility.Collapsed;

    private async void RecheckUpdates_Click(object sender, RoutedEventArgs e) => await CheckUpdatesAsync();

    private async Task CheckUpdatesAsync()
    {
        RecheckBtn.IsEnabled = false;
        UpdatesStatus.Text = Loc.S("L_Checking");
        _updates.Clear();

        var dir = DirBox.Text.Trim();
        var installedHere = OpenCode.InstalledVersion(dir);
        var opencode = new UpdateRow("OpenCode", "opencode")
        {
            Current = installedHere ?? Loc.S("L_NotInstalledOpenCode"),
            ActionText = Loc.S(installedHere is null ? "L_Install" : "L_Update"),
        };
        var installedOllama = OllamaHost.IsInstalled();
        var ollamaVersion = installedOllama
            ? await Ollama.VersionAsync() ?? OllamaHost.InstalledVersion()
            : null;
        var ollama = new UpdateRow("Ollama", "ollama")
        {
            Current = !installedOllama ? Loc.S("L_NotInstalledOllama")
                    : ollamaVersion ?? Loc.S("L_NotRunning"),
            ActionText = Loc.S(installedOllama ? "L_Update" : "L_Install"),
        };
        _updates.Add(opencode);
        _updates.Add(ollama);

        var release = await OpenCode.LatestAsync();
        Settle(opencode, release?.Version);
        if (installedHere is null && release is not null)
        {
            opencode.State = Loc.S("L_NotInstalledOpenCode");
            opencode.CanUpdate = true;
        }

        var ollamaRelease = await Updates.LatestOllamaAsync();
        _ollamaSetup = ollamaRelease?.Url ?? "";
        if (!installedOllama)
        {
            ollama.Available = ollamaRelease?.Version ?? "";
            ollama.State = Loc.S("L_NotInstalledOllama");
            ollama.CanUpdate = ollamaRelease is not null;
        }
        else if (ollamaVersion is null)
        {
            ollama.State = Loc.S("L_NotRunning");
            ollama.CanUpdate = false;
        }
        else
        {
            Settle(ollama, ollamaRelease?.Version);
        }
        if (ollama.CanUpdate && _ollamaSetup.Length == 0)
        {
            ollama.CanUpdate = false;
            ollama.State = Loc.S("L_NoInstaller");
        }

        List<InstalledModel> models;
        try { models = await Ollama.ListAsync(); }
        catch { models = new List<InstalledModel>(); }

        var checks = models.Select(model =>
        {
            var row = new UpdateRow(model.Name, "model");
            _updates.Add(row);
            return CheckModelAsync(model, row);
        });
        await Task.WhenAll(checks);

        UpdatesStatus.Text = Loc.F("L_CheckedAt", DateTime.Now.ToString("HH:mm"));
        RecheckBtn.IsEnabled = true;

        static async Task CheckModelAsync(InstalledModel model, UpdateRow row)
        {
            var name = model.Name.Contains(':') ? model.Name[..model.Name.IndexOf(':')] : model.Name;
            var tag = model.Name.Contains(':') ? model.Name[(model.Name.IndexOf(':') + 1)..] : "latest";
            var published = (await Catalog.TagsAsync(name))
                .FirstOrDefault(t => t.Name.Equals($"{name}:{tag}", StringComparison.OrdinalIgnoreCase));

            if (published is null || published.Digest.Length == 0)
            {
                row.State = Loc.S("L_NotInCatalog");
                return;
            }

            var same = Short(model.Digest) == Short(published.Digest);
            row.State = Loc.S(same ? "L_ModelFresh" : "L_NewBuild");
            row.CanUpdate = !same;
        }

        static string Short(string digest) => digest.Length > 12 ? digest[..12] : digest;

        static void Settle(UpdateRow row, string? available)
        {
            if (string.IsNullOrWhiteSpace(available))
            {
                row.State = Loc.S("L_CheckFailed");
                return;
            }
            row.Available = available;
            var same = string.Equals(available, row.Current, StringComparison.OrdinalIgnoreCase);
            row.State = Loc.S(same ? "L_UpToDate" : "L_UpdateAvailable");
            row.CanUpdate = !same;
        }
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not UpdateRow row) return;

        row.Busy = true;
        var was = row.State;
        row.State = Loc.S("L_Updating");
        try
        {
            switch (row.Kind)
            {
                case "opencode":
                    LogPanel.Visibility = Visibility.Visible;
                    UpdateProgress.Visibility = Visibility.Visible;
                    var dir = DirBox.Text.Trim();
                    await _opencode.UpdateAsync(dir,
                        new Progress<double>(percent => UpdateProgress.Value = percent), CancellationToken.None);
                    UpdateProgress.Visibility = Visibility.Collapsed;
                    row.Current = OpenCode.InstalledVersion(dir) ?? row.Current;
                    row.State = Loc.S("L_Updated");
                    row.CanUpdate = false;
                    await RefreshHealthAsync();
                    break;

                case "ollama":
                    UpdateProgress.Visibility = Visibility.Visible;
                    var progress = new Progress<double>(percent => UpdateProgress.Value = percent);
                    var file = await Updates.RunOllamaSetupAsync(_ollamaSetup, progress, CancellationToken.None);
                    UpdateProgress.Visibility = Visibility.Collapsed;
                    row.State = Loc.S(row.ActionText == Loc.S("L_Install")
                        ? "L_InstallerRunningInstall"
                        : "L_InstallerRunningUpdate");
                    row.CanUpdate = false;
                    Log(Loc.F("L_LogOllamaInstaller", file));
                    break;

                case "model":
                    UpdatesOverlay.Visibility = Visibility.Collapsed;
                    await PullModelAsync(row.Title, _models.FirstOrDefault(m => m.Name == row.Title));
                    row.State = Loc.S("L_ModelFresh");
                    row.CanUpdate = false;
                    break;
            }
        }
        catch (Exception ex)
        {
            row.State = was;
            Log(Loc.F("L_LogUpdateFailed", ex.Message));
        }
        finally
        {
            row.Busy = false;
            UpdateProgress.Visibility = Visibility.Collapsed;
        }
    }

    private async void OpenCatalog_Click(object sender, RoutedEventArgs e)
    {
        CatalogOverlay.Visibility = Visibility.Visible;
        if (_catalog.Count > 0) return;

        CatalogCount.Text = Loc.S("L_CatalogLoading");
        try
        {
            _catalog = await Catalog.NamesAsync(false);
            CatalogList.ItemsSource = _catalog;
            CatalogCount.Text = Loc.F("L_CatalogCount", _catalog.Count);
        }
        catch (Exception ex)
        {
            CatalogCount.Text = Loc.S("L_CatalogFailed");
            Log(Loc.F("L_LogCatalog", ex.Message));
        }
    }

    private void CloseCatalog_Click(object sender, RoutedEventArgs e) => CatalogOverlay.Visibility = Visibility.Collapsed;

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = SearchBox.Text.Trim();
        CatalogList.ItemsSource = query.Length == 0
            ? _catalog
            : _catalog.Where(n => n.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private async void CatalogList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CatalogList.SelectedItem is not string name)
        {
            TagList.ItemsSource = null;
            PullBtn.IsEnabled = false;
            return;
        }

        TagsTitle.Text = Loc.S("L_Versions") + " · " + name;
        TagList.ItemsSource = new[] { new CatalogTag(Loc.S("L_Loading"), "") };
        var tags = await Catalog.TagsAsync(name);
        if (CatalogList.SelectedItem as string != name) return;

        _tags = tags;
        TagSearchBox.Clear();
        TagList.ItemsSource = _tags;
        TagList.SelectedIndex = 0;
        PullBtn.IsEnabled = true;
    }

    private void TagSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = TagSearchBox.Text.Trim();
        TagList.ItemsSource = query.Length == 0
            ? _tags
            : _tags.Where(t => t.Display.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (TagList.Items.Count > 0) TagList.SelectedIndex = 0;
        PullBtn.IsEnabled = TagList.Items.Count > 0;
    }

    private async void Pull_Click(object sender, RoutedEventArgs e)
    {
        if (TagList.SelectedItem is not CatalogTag tag) return;
        CatalogOverlay.Visibility = Visibility.Collapsed;
        await PullModelAsync(tag.Name);
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not ModelCard card) return;
        card.Paused = true;
        card.Cancellation?.Cancel();
    }

    private async void Resume_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not ModelCard card) return;
        await PullModelAsync(card.Name, card);
    }

    private void CancelPull_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not ModelCard card) return;
        card.Cancelled = true;
        if (card.Cancellation is { } running)
        {
            card.Paused = false;
            running.Cancel();
            return;
        }

        card.Busy = false;
        card.Paused = false;
        _models.Remove(card);
        Log(Loc.F("L_LogCancelled", card.Name));
        DropOrphanBlobs();
    }

    private void DropOrphanBlobs()
    {
        if (_models.Any(m => m.Busy)) return;

        var root = OllamaHost.ModelsDir();
        var blobs = Path.Combine(root, "blobs");
        var manifests = Path.Combine(root, "manifests");
        if (!Directory.Exists(blobs) || !Directory.Exists(manifests)) return;

        // Непрочитанный манифест отменяет уборку: лучше мусор, чем снести слой работающей модели.
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(manifests, "*", SearchOption.AllDirectories))
        {
            try
            {
                var text = File.ReadAllText(file);
                System.Text.Json.JsonDocument.Parse(text).Dispose();
                foreach (Match digest in Regex.Matches(text, "sha256:[0-9a-f]{64}"))
                    referenced.Add(digest.Value.Replace(':', '-'));
            }
            catch
            {
                Log(Loc.S("L_LogManifest"));
                return;
            }
        }

        long freed = 0;
        foreach (var blob in Directory.EnumerateFiles(blobs))
        {
            if (referenced.Contains(Path.GetFileName(blob))) continue;
            try
            {
                freed += new FileInfo(blob).Length;
                File.Delete(blob);
            }
            catch { }
        }
        if (freed > 0) Log(Loc.F("L_LogFreed", freed / 1024.0 / 1024));
    }

    private async Task PullModelAsync(string tag, ModelCard? existing = null)
    {
        var card = existing ?? new ModelCard(tag);
        if (existing is null)
        {
            _models.Insert(0, card);
            EmptyHint.Visibility = Visibility.Collapsed;
        }

        card.Busy = true;
        card.Paused = false;
        card.Cancelled = false;
        card.Status = Loc.S("L_Preparing");
        using var cts = new CancellationTokenSource();
        card.Cancellation = cts;
        Log(Loc.F("L_LogPulling", card.Name));

        try
        {
            await Ollama.PullAsync(card.Name, (status, share) => Dispatcher.Invoke(() =>
            {
                card.Status = share is { } p ? $"{status} - {p:0}%" : status;
                if (share is { } value) card.Progress = value;
            }), cts.Token);

            Log(Loc.F("L_LogPulled", card.Name));
            card.Busy = false;
            await RefreshModelsAsync();
        }
        catch (Exception) when (cts.IsCancellationRequested)
        {
            card.Busy = false;
            if (card.Cancelled)
            {
                _models.Remove(card);
                Log(Loc.F("L_LogCancelled", card.Name));
                DropOrphanBlobs();
            }
            else
            {
                card.Busy = true;
                card.Status = Loc.F("L_PausedAt", card.Progress);
                Log(Loc.F("L_LogPullPaused", card.Name));
            }
        }
        catch (Exception ex)
        {
            card.Status = Loc.F("L_PullFailed", ex.Message);
            Log(Loc.F("L_LogPullFailed", ex.Message));
        }
        finally
        {
            card.Cancellation = null;
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not ModelCard card) return;
        if (!await AskAsync(Loc.F("L_DeleteQuestion", card.Name))) return;

        try
        {
            await Ollama.DeleteAsync(card.Name);
            Log(Loc.F("L_LogDeleted", card.Name));
        }
        catch (Exception ex) { Log(Loc.F("L_LogDeleteFailed", ex.Message)); }
        await RefreshModelsAsync();
    }

    private void ModelSettings_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is ModelCard card) _ = OpenTuneAsync(card);
    }

    private async Task OpenTuneAsync(ModelCard? card)
    {
        if (card is null) return;
        _tuned = card;
        TuneModelText.Text = card.Name;
        TuneOverlay.Visibility = Visibility.Visible;
        _harnessRows.Clear();
        _modelRows.Clear();

        var facts = await Ollama.FactsAsync(card.Name);
        var tuning = _settings.Tuning(card.Name);
        if (tuning.Shipped is null)
        {
            tuning.Shipped = facts.Parameters ?? "";
            _settings.Save();
        }
        _shipped = ParseParams(tuning.Shipped ?? "");
        if (_tuned != card) return;

        var output = Math.Min(32768, facts.Context);
        _ctxRow = new ParamRow(Loc.S("L_TuneCtx"), Loc.S("L_FromModel"),
            (tuning.ContextWindow ?? facts.Context).ToString()) { Baseline = facts.Context.ToString() };
        _maxRow = new ParamRow(Loc.S("L_TuneMax"), Loc.S("L_FromDefault"),
            (tuning.MaxTokens ?? output).ToString()) { Baseline = output.ToString() };
        _reasonRow = new FlagRow(Loc.S("L_TuneReasoning"),
            Loc.S(facts.Thinks ? "L_ModelCan" : "L_ModelCant"),
            tuning.Reasoning ?? facts.Thinks) { Baseline = facts.Thinks };
        _harnessRows.Add(_ctxRow);
        _harnessRows.Add(_maxRow);
        _harnessRows.Add(_reasonRow);

        foreach (var (name, fallback) in OllamaParams.Concat(_shipped.Keys.Where(NotListed).Select(k => (k, ""))))
        {
            var fromModel = _shipped.TryGetValue(name, out var own);
            var baseline = (fromModel ? own : fallback) ?? "";
            var mine = tuning.Params.TryGetValue(name, out var saved) ? saved : baseline;
            _modelRows.Add(new ParamRow(name,
                Loc.S(fromModel ? "L_FromModel" : baseline.Length == 0 ? "L_FromAuto" : "L_FromDefault"), mine)
            {
                Baseline = baseline,
            });
        }

        TuneNote.Text = Loc.S("L_TuneNote");

        static bool NotListed(string name) => !OllamaParams.Any(p => p.Name == name);
    }

    private void CloseTune_Click(object sender, RoutedEventArgs e)
    {
        TuneOverlay.Visibility = Visibility.Collapsed;
        _tuned = null;
    }

    private void TuneReset_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _harnessRows.OfType<ParamRow>().Concat(_modelRows)) row.Value = row.Baseline;
        if (_reasonRow is { } flag) flag.Value = flag.Baseline;
    }

    private async void TuneSave_Click(object sender, RoutedEventArgs e)
    {
        if (_tuned is not { } card) return;
        var tuning = _settings.Tuning(card.Name);
        var was = tuning.Params;

        tuning.ContextWindow = _ctxRow?.Touched == true ? Number(_ctxRow.Value) : null;
        tuning.MaxTokens = _maxRow?.Touched == true ? Number(_maxRow.Value) : null;
        tuning.Reasoning = _reasonRow is { } flag && flag.Value != flag.Baseline ? flag.Value : null;
        tuning.Params = _modelRows.Where(r => r.Touched && r.Value.Trim().Length > 0)
                                  .ToDictionary(r => r.Name, r => r.Value.Trim());
        _settings.Save();
        TuneOverlay.Visibility = Visibility.Collapsed;
        _tuned = null;

        var effective = new Dictionary<string, string>(_shipped);
        foreach (var pair in tuning.Params) effective[pair.Key] = pair.Value;

        // Забыть параметр Ollama не умеет: убранное перебиваем заводским значением или её умолчанием.
        foreach (var name in was.Keys.Where(k => !effective.ContainsKey(k)))
        {
            var back = OllamaParams.FirstOrDefault(p => p.Name == name).Default;
            if (!string.IsNullOrEmpty(back)) effective[name] = back;
        }
        if (effective.Count > 0)
        {
            try
            {
                await Ollama.CreateAsync(card.Name, effective);
                Log(Loc.F("L_LogParams", card.Name,
                    string.Join(", ", effective.Select(p => $"{p.Key}={p.Value}"))));
            }
            catch (Exception ex) { Log(Loc.F("L_LogParamsFailed", ex.Message)); }
        }

        if (ModelList.SelectedItem as ModelCard == card) ModelList_SelectionChanged(this, null!);

        static int? Number(string? text) =>
            int.TryParse(text?.Trim(), out var value) ? value : null;
    }

    private static Dictionary<string, string> ParseParams(string text)
    {
        var result = new Dictionary<string, string>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 2) continue;
            var value = parts[1].Trim().Trim('"');
            result[parts[0]] = result.TryGetValue(parts[0], out var had) ? $"{had}|{value}" : value;
        }
        return result;
    }

    private async void ModelList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModelList.SelectedItem is not ModelCard card)
        {
            RunModelText.Text = Loc.S("L_NoModelSelected");
            RunContextText.Text = "";
            return;
        }

        RunModelText.Text = card.Name;
        RunContextText.Text = Loc.S("L_CtxChecking");
        var facts = await Ollama.FactsAsync(card.Name);
        if (ModelList.SelectedItem as ModelCard != card) return;
        RunContextText.Text = Loc.F("L_CtxTokens", ContextFor(card.Name, facts));
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (ModelList.SelectedItem is not ModelCard card) { Refuse(Loc.S("L_PickModelFirst")); return; }
        var dir = DirBox.Text.Trim();
        if (!OpenCode.IsInstalled(dir)) { SetupOverlay.Visibility = Visibility.Visible; return; }
        if (await Ollama.VersionAsync() is null) { Refuse(Loc.S("L_OllamaDown")); return; }

        var work = WorkBox.Text.Trim();
        if (!Directory.Exists(work)) { Refuse(Loc.S("L_ProjectMissing")); return; }

        _settings.LastModel = card.Name;
        _settings.InstallDir = dir;
        _settings.Workspace = work;
        _settings.Save();

        var chosen = SessionList.SelectedItem as SessionRow;
        var brandNew = chosen is null || chosen.IsNew;
        await LaunchAsync(card, dir, work,
            brandNew ? null : chosen!.Id,
            brandNew ? Loc.All("L_NewSession") : new[] { chosen!.Name });
    }

    private async Task LaunchAsync(ModelCard card, string dir, string work, string? sessionId, string[] titles)
    {
        var tune = _settings.Tuning(card.Name);
        var facts = await Ollama.FactsAsync(card.Name);
        var context = ContextFor(card.Name, facts);

        _runSteps.Clear();
        _runSteps.Add(new StepCard(Loc.S("L_StepWriteConfig")));
        _runSteps.Add(new StepCard(Loc.F("L_StepOllamaContext", context)));
        _runSteps.Add(new StepCard(Loc.S("L_StepWarm")));
        _runSteps.Add(new StepCard(Loc.S("L_StepProbe")));
        _runSteps.Add(new StepCard(Loc.S("L_StepOpenCode2")));

        LaunchModelText.Text = card.Name;
        LaunchNote.Text = "";
        LaunchProgress.Value = 0;
        LaunchProgress.Visibility = Visibility.Visible;
        LaunchCancel.Visibility = Visibility.Visible;
        LaunchOverlay.Visibility = Visibility.Visible;
        StartBtn.IsEnabled = false;

        using var launching = new CancellationTokenSource();
        _launching = launching;
        var ct = launching.Token;
        Log(Loc.F("L_LogRun", card.Name, work));

        try
        {
            Step(0);
            OpenCode.WriteConfig(dir, new OpenCodeModel(
                card.Name,
                context,
                tune.MaxTokens ?? Math.Min(32768, context),
                facts.Capabilities.Contains("tools"),
                tune.Reasoning ?? facts.Thinks));

            Step(1);
            await OllamaHost.UseContextAsync(context, Log);

            Step(2);
            if (await WarmAsync(card, ct) is { } cold) { Failed(2, Detailed(cold)); return; }

            Step(3);
            if (await Ollama.ProbeAsync(card.Name, ct) is { } mute) { Failed(3, mute); return; }

            Step(4);
            OpenCodeLog.Mark();
            var fresh = OpenCode.Launch(dir, work, sessionId, titles, Log);
            Done(4);

            LaunchOverlay.Visibility = Visibility.Collapsed;
            Status(fresh ? Loc.F("L_Opened", work) : Loc.S("L_AlreadyOpen"));
            StartBtn.Visibility = Visibility.Collapsed;
            StopBtn.Visibility = Visibility.Visible;
            Vanish();
        }
        catch (OperationCanceledException)
        {
            LaunchOverlay.Visibility = Visibility.Collapsed;
            Status(Loc.S("L_LaunchCancelled"));
            _ = Ollama.UnloadAsync(card.Name);
        }
        catch (Exception ex)
        {
            Failed(_runSteps.Count - 1, ex.Message);
        }
        finally
        {
            _launching = null;
            StartBtn.IsEnabled = true;
        }

        void Step(int index)
        {
            for (var i = 0; i < _runSteps.Count; i++)
            {
                _runSteps[i].Done = i < index;
                _runSteps[i].Active = i == index;
            }
            LaunchProgress.Value = index switch { 0 => 2, 1 => 6, 2 => 10, 3 => 88, _ => 96 };
        }

        void Done(int index)
        {
            _runSteps[index].Active = false;
            _runSteps[index].Done = true;
            LaunchProgress.Value = 100;
        }

        void Failed(int index, string trouble)
        {
            _runSteps[index].Active = false;
            LaunchProgress.Visibility = Visibility.Collapsed;
            LaunchCancel.Content = Loc.S("L_Close");
            LaunchNote.Text = Loc.F("L_ModelFailed", trouble);
            Log(Loc.F("L_LogModelFailed", trouble));
        }
    }

    private async Task<string?> WarmAsync(ModelCard card, CancellationToken ct)
    {
        var warm = Ollama.WarmAsync(card.Name, ct);
        var start = OllamaHost.LoadedBytes();

        while (!warm.IsCompleted)
        {
            await Task.WhenAny(warm, Task.Delay(400, ct));
            if (card.Bytes <= 0) continue;

            var read = OllamaHost.LoadedBytes() - start;
            if (read <= 0) continue;

            var share = Math.Min(1.0, (double)read / card.Bytes);
            LaunchProgress.Value = 10 + 78 * share;
            LaunchNote.Text = Loc.F("L_LoadedOf", read / 1073741824.0, card.Bytes / 1073741824.0);
        }

        LaunchNote.Text = "";
        return await warm;
    }

    private void LaunchCancel_Click(object sender, RoutedEventArgs e)
    {
        if (_launching is { } launching && !launching.IsCancellationRequested)
        {
            launching.Cancel();
            return;
        }
        LaunchOverlay.Visibility = Visibility.Collapsed;
        LaunchCancel.Content = Loc.S("L_Cancel");
    }

    private void Vanish()
    {
        ShowInTaskbar = false;
        Hide();
    }

    internal void Reappear()
    {
        ShowInTaskbar = true;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        ReloadSessions();
        _ = RefreshModelsAsync();
        Status(Loc.S("L_Closed"));
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        OpenCode.Quit(DirBox.Text.Trim());
        _ = Ollama.UnloadAsync(_settings.LastModel);
        StopBtn.Visibility = Visibility.Collapsed;
        StartBtn.Visibility = Visibility.Visible;
        ReloadSessions();
        Status(Loc.S("L_Closed"));
    }

    private static string Detailed(string trouble)
    {
        var said = OllamaLog.LastTrouble(TimeSpan.FromMinutes(3));
        return said is null || trouble.Contains(said, StringComparison.OrdinalIgnoreCase)
            ? trouble
            : $"{trouble} - {Loc.F("L_OllamaSays", said)}";
    }

    private void Status(string text) => Log(text);

    private void Refuse(string text)
    {
        Log(text);
        _runSteps.Clear();
        LaunchModelText.Text = "";
        LaunchNote.Text = text;
        LaunchProgress.Visibility = Visibility.Collapsed;
        LaunchCancel.Content = Loc.S("L_Close");
        LaunchOverlay.Visibility = Visibility.Visible;
    }
}
