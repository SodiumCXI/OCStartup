using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;

namespace DshStudio;

public static class Loc
{
    /// <summary>Порядок колонок в таблице ниже.</summary>
    public static readonly string[] Codes = { "en", "ru", "zh" };
    public static readonly string[] Names = { "English", "Русский", "中文" };

    private static int _column;

    public static string Current => Codes[_column];

    public static string Detect() => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName switch
    {
        "ru" => "ru",
        "zh" => "zh",
        _ => "en",
    };

    public static void Use(string code)
    {
        var index = Array.IndexOf(Codes, code);
        _column = index < 0 ? 0 : index;

        if (Application.Current is { } app)
            foreach (var pair in Table)
                app.Resources[pair.Key] = pair.Value[_column];
    }

    public static string S(string key) => Table.TryGetValue(key, out var row) ? row[_column] : key;

    public static string[] All(string key) => Table.TryGetValue(key, out var row) ? row : new[] { key };

    public static string F(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, S(key), args);

    private static readonly Dictionary<string, string[]> Table = new()
    {
        ["L_Language"] = new[] { "Language", "Язык", "语言" },
        ["L_Pause"] = new[] { "Pause", "Пауза", "暂停" },
        ["L_Resume"] = new[] { "Resume", "Продолжить", "继续" },
        ["L_CancelDownload"] = new[] { "Cancel download", "Отменить загрузку", "取消下载" },
        ["L_ModelSettings"] = new[] { "Model settings", "Настройки модели", "模型设置" },
        ["L_DeleteModel"] = new[] { "Delete model", "Удалить модель", "删除模型" },
        ["L_StartOllama"] = new[] { "Start Ollama", "Запустить Ollama", "启动 Ollama" },
        ["L_Updates"] = new[] { "Updates", "Обновления", "更新" },
        ["L_Log"] = new[] { "Log", "Журнал", "日志" },
        ["L_InstallFolder"] = new[] { "Install folder", "Папка установки", "安装目录" },
        ["L_SetupFailed"] = new[]
        {
            "the OpenCode installer returned {0}",
            "установщик OpenCode вернул {0}",
            "OpenCode 安装程序返回 {0}",
        },
        ["L_Models"] = new[] { "Models", "Модели", "模型" },
        ["L_Catalog"] = new[] { "Catalog", "Каталог", "模型库" },
        ["L_Run"] = new[] { "Run", "Запуск", "运行" },
        ["L_NoModels"] = new[] { "No models yet", "Моделей нет", "还没有模型" },
        ["L_NoModelSelected"] = new[] { "No model selected", "Модель не выбрана", "未选择模型" },
        ["L_ProjectFolder"] = new[] { "Project folder", "Папка проекта", "项目文件夹" },
        ["L_PickProjectFolder"] = new[] { "Choose project folder", "Выбрать папку проекта", "选择项目文件夹" },
        ["L_Session"] = new[] { "Session", "Сессия", "会话" },
        ["L_NewSession"] = new[] { "New session", "Новая сессия", "新会话" },
        ["L_Untitled"] = new[] { "Untitled", "Без названия", "未命名" },
        ["L_Start"] = new[] { "Start", "Запустить", "启动" },
        ["L_Stop"] = new[] { "Stop", "Остановить", "停止" },
        ["L_OpenInBrowser"] = new[] { "Open in browser", "Открыть в браузере", "在浏览器中打开" },
        ["L_Copy"] = new[] { "Copy", "Скопировать", "复制" },
        ["L_CatalogOllama"] = new[] { "Ollama catalog", "Каталог Ollama", "Ollama 模型库" },
        ["L_Download"] = new[] { "Download", "Скачать", "下载" },
        ["L_Versions"] = new[] { "Versions", "Версии", "版本" },
        ["L_Recheck"] = new[] { "Check again", "Проверить снова", "重新检查" },
        ["L_Settings"] = new[] { "Settings", "Настройки", "设置" },
        ["L_Reset"] = new[] { "Reset", "Сбросить", "重置" },
        ["L_Cancel"] = new[] { "Cancel", "Отмена", "取消" },
        ["L_Close"] = new[] { "Close", "Закрыть", "关闭" },

        ["L_Save"] = new[] { "Save", "Сохранить", "保存" },
        ["L_SetupTitle"] = new[] { "Install OpenCode and Ollama", "Установка OpenCode и Ollama", "安装 OpenCode 和 Ollama" },
        ["L_SetupNote"] = new[]
        {
            "OpenCode, Ollama and the models all go into this folder, silently, from their GitHub releases.",
            "OpenCode, Ollama и модели ставятся в эту папку тихо, из выпусков на GitHub.",
            "OpenCode、Ollama 和模型都会从 GitHub 发行版静默安装到此目录。",
        },
        ["L_PickFolder"] = new[] { "Choose folder", "Выбрать папку", "选择文件夹" },
        ["L_Install"] = new[] { "Install", "Установить", "安装" },
        ["L_Reinstall"] = new[] { "Reinstall", "Переустановить", "重新安装" },
        ["L_Update"] = new[] { "Update", "Обновить", "更新" },
        ["L_Delete"] = new[] { "Delete", "Удалить", "删除" },
        ["L_Crash"] = new[] { "Crash", "Сбой", "崩溃" },

        ["L_StepOpenCode"] = new[]
        {
            "OpenCode app from the GitHub release",
            "Приложение OpenCode из выпуска на GitHub",
            "来自 GitHub 发行版的 OpenCode 应用",
        },
        ["L_StepOllama"] = new[]
        {
            "Ollama without the interface",
            "Ollama без интерфейса",
            "不带界面的 Ollama",
        },
        ["L_StepModels"] = new[]
        {
            "Ollama models move into the install folder",
            "Модели Ollama переезжают в папку установки",
            "Ollama 模型迁移到安装目录",
        },
        ["L_StepConfig"] = new[] { "Config file", "Файл настроек", "配置文件" },
        ["L_DownloadingOpenCode"] = new[] { "Downloading OpenCode {0}", "Загрузка OpenCode {0}", "正在下载 OpenCode {0}" },
        ["L_DownloadingOllama"] = new[] { "Downloading Ollama {0}", "Загрузка Ollama {0}", "正在下载 Ollama {0}" },
        ["L_Unpacking"] = new[] { "Unpacking", "Распаковка", "正在解压" },
        ["L_Installing"] = new[] { "Installing", "Установка", "正在安装" },
        ["L_Queued"] = new[] { "queued", "в очереди", "排队中" },
        ["L_OllamaReleaseMissing"] = new[]
        {
            "no Windows release of Ollama found",
            "выпуск Ollama для Windows не найден",
            "未找到 Ollama 的 Windows 发行版",
        },
        ["L_DriveRoot"] = new[]
        {
            "OpenCode sits in the root of {0}: move it into a folder first",
            "OpenCode стоит в корне {0}: сначала перенесите его в папку",
            "OpenCode 位于 {0} 根目录，请先移到文件夹中",
        },
        ["L_OpenCodeReady"] = new[] { "OpenCode {0} is in place", "OpenCode {0} на месте", "OpenCode {0} 已就位" },
        ["L_ReleaseMissing"] = new[]
        {
            "no Windows release of OpenCode found",
            "выпуск OpenCode под Windows не найден",
            "未找到 Windows 版 OpenCode 发行包",
        },

        ["L_OllamaMissing"] = new[] { "Ollama is not installed", "Ollama не установлена", "未安装 Ollama" },
        ["L_OllamaStopped"] = new[] { "Ollama is not running", "Ollama не запущена", "Ollama 未运行" },
        ["L_OpenCodeMissing"] = new[] { "OpenCode is not installed", "OpenCode не установлен", "未安装 OpenCode" },

        ["L_TuneCtx"] = new[] { "context window", "окно контекста", "上下文窗口" },
        ["L_TuneMax"] = new[] { "max response", "максимум ответа", "最大输出" },
        ["L_TuneReasoning"] = new[] { "reasoning", "рассуждения", "推理" },
        ["L_FromModel"] = new[] { "model", "модель", "模型" },
        ["L_FromDefault"] = new[] { "default", "умолчание", "默认" },
        ["L_FromMine"] = new[] { "yours", "вручную", "手动" },
        ["L_FromAuto"] = new[] { "auto", "авто", "自动" },
        ["L_ModelCan"] = new[] { "model can", "модель умеет", "模型支持" },
        ["L_ModelCant"] = new[] { "model cannot", "модель не умеет", "模型不支持" },
        ["L_TuneNote"] = new[]
        {
            "The values shown are the ones in effect: «model» - its own, «default» - Ollama's. Only what you change is sent to the model.",
            "Значения показаны те, что действуют сейчас: «модель» - её собственные, «умолчание» - Ollama. В модель уходит только то, что вы измените.",
            "这里显示的是当前生效的值：“模型”为模型自带，“默认”来自 Ollama。只有改动过的内容才会写入模型。",
        },

        ["L_Checking"] = new[] { "checking...", "проверка...", "检查中…" },
        ["L_NotInstalledOpenCode"] = new[] { "not installed", "не установлен", "未安装" },
        ["L_NotInstalledOllama"] = new[] { "not installed", "не установлена", "未安装" },
        ["L_NotRunning"] = new[] { "not running", "не запущена", "未运行" },
        ["L_NoInstaller"] = new[]
        {
            "update available, but the release has no Windows archive",
            "обновление есть, архива для Windows в выпуске нет",
            "有更新，但发行包中没有 Windows 压缩包",
        },
        ["L_CheckedAt"] = new[] { "checked at {0}", "проверено в {0}", "已于 {0} 检查" },
        ["L_NotInCatalog"] = new[] { "not in the ollama.com catalog", "нет в каталоге ollama.com", "ollama.com 模型库中没有" },
        ["L_ModelFresh"] = new[] { "up to date", "актуальна", "已是最新" },
        ["L_NewBuild"] = new[] { "a new build is out", "вышла новая сборка", "有新的构建" },
        ["L_CheckFailed"] = new[] { "check failed", "проверить не удалось", "检查失败" },
        ["L_UpToDate"] = new[] { "up to date", "актуально", "已是最新" },
        ["L_UpdateAvailable"] = new[] { "update available", "есть обновление", "有可用更新" },
        ["L_Updated"] = new[] { "updated", "обновлено", "已更新" },

        ["L_CatalogLoading"] = new[] { "loading the list...", "список загружается...", "正在加载列表…" },
        ["L_CatalogCount"] = new[]
        {
            "{0} models in the ollama.com library",
            "{0} моделей в библиотеке ollama.com",
            "ollama.com 库中有 {0} 个模型",
        },
        ["L_CatalogFailed"] = new[] { "catalog did not load", "каталог не загрузился", "模型库加载失败" },
        ["L_Loading"] = new[] { "loading...", "загрузка...", "加载中…" },

        ["L_GB"] = new[] { "GB", "ГБ", "GB" },
        ["L_Preparing"] = new[] { "preparing", "подготовка", "准备中" },
        ["L_PausedAt"] = new[] { "paused - {0:0}%", "на паузе - {0:0}%", "已暂停 - {0:0}%" },
        ["L_PullFailed"] = new[] { "download failed: {0}", "не скачалась: {0}", "下载失败：{0}" },
        ["L_DeleteQuestion"] = new[] { "Delete {0} from disk?", "Удалить {0} с диска?", "从磁盘删除 {0}？" },

        ["L_CtxChecking"] = new[] { "context window: checking...", "окно контекста уточняется...", "正在获取上下文窗口…" },
        ["L_CtxTokens"] = new[] { "context window {0:N0} tokens", "окно контекста {0:N0} токенов", "上下文窗口 {0:N0} 个 token" },
        ["L_PickModelFirst"] = new[] { "Choose a model first.", "Сначала выберите модель.", "请先选择模型。" },
        ["L_OllamaDown"] = new[]
        {
            "Ollama is not running - start it and try again.",
            "Ollama не запущена - запустите её и повторите.",
            "Ollama 未运行 - 请先启动它再试一次。",
        },
        ["L_ProjectMissing"] = new[] { "Project folder not found.", "Папка проекта не найдена.", "未找到项目文件夹。" },
        ["L_StartFailed"] = new[] { "Did not start: {0}", "Не запустилось: {0}", "启动失败：{0}" },
        ["L_Opened"] = new[]
        {
            "OpenCode is open: project {0}",
            "OpenCode открыт: проект {0}",
            "OpenCode 已打开：项目 {0}",
        },
        ["L_AlreadyOpen"] = new[]
        {
            "OpenCode is already open - brought its window to the front. The project and session apply the next time it starts.",
            "OpenCode уже открыт - показали его окно. Проект и сессия применятся при следующем его запуске.",
            "OpenCode 已经打开 - 已将其窗口置前。所选项目与会话将在下次启动时生效。",
        },
        ["L_Closed"] = new[] { "OpenCode closed.", "OpenCode закрыт.", "OpenCode 已关闭。" },
        ["L_CloseApp"] = new[] { "Close OpenCode", "Закрыть OpenCode", "关闭 OpenCode" },

        ["L_LogOllamaStart"] = new[]
        {
            "Ollama is not running, starting it with folder {0}",
            "Ollama не запущена, запуск с папкой {0}",
            "Ollama 未运行，正在以目录 {0} 启动",
        },
        ["L_LogNoTab"] = new[]
        {
            "Could not open the tab in OpenCode - it stays in its tab bar",
            "Открыть вкладку в OpenCode не удалось - она осталась в его списке",
            "未能在 OpenCode 中打开该标签页 - 它仍在标签栏里",
        },
        ["L_LogTabOpened"] = new[] { "Tab opened in OpenCode", "Вкладка открыта в OpenCode", "已在 OpenCode 中打开标签页" },
        ["L_StepWriteConfig"] = new[]
        {
            "Settings for OpenCode",
            "Настройки для OpenCode",
            "为 OpenCode 准备配置",
        },
        ["L_StepOllamaContext"] = new[]
        {
            "Ollama with a {0:N0}-token context window",
            "Ollama с окном контекста {0:N0} токенов",
            "以 {0:N0} token 上下文窗口运行的 Ollama",
        },
        ["L_StepWarm"] = new[]
        {
            "Loading the model into memory",
            "Загрузка модели в память",
            "把模型载入内存",
        },
        ["L_StepProbe"] = new[] { "Checking the reply", "Проверка ответа", "验证模型应答" },
        ["L_StepOpenCode2"] = new[] { "Starting OpenCode", "Запуск OpenCode", "启动 OpenCode" },
        ["L_LoadedOf"] = new[]
        {
            "{0:0.#} of {1:0.#} GB loaded",
            "Загружено {0:0.#} из {1:0.#} ГБ",
            "已载入 {0:0.#} / {1:0.#} GB",
        },
        ["L_LaunchCancelled"] = new[] { "Start cancelled.", "Запуск отменён.", "启动已取消。" },
        ["L_OllamaSays"] = new[] { "Ollama says: {0}", "Ollama пишет: {0}", "Ollama 提示：{0}" },
        ["L_LowerContext"] = new[]
        {
            "Not enough memory: the context window is {0:N0} tokens, lower it in the model settings.",
            "Не хватило памяти: окно контекста {0:N0} токенов, уменьшите его в настройках модели.",
            "内存不足：上下文窗口为 {0:N0} token，请在模型设置中调小。",
        },
        ["L_ModelBroke"] = new[]
        {
            "The model failed while working: {0}. OpenCode was closed.",
            "Модель отказала во время работы: {0}. OpenCode закрыт.",
            "模型在运行中出错：{0}。已关闭 OpenCode。",
        },
        ["L_LogModelBroke"] = new[]
        {
            "Model failed while working: {0}",
            "Модель отказала во время работы: {0}",
            "模型在运行中出错：{0}",
        },
        ["L_ModelFailed"] = new[]
        {
            "The model did not load: {0}",
            "Модель не загрузилась: {0}",
            "模型未能加载：{0}",
        },
        ["L_LogModelFailed"] = new[] { "Model did not load: {0}", "Модель не загрузилась: {0}", "模型未能加载：{0}" },
        ["L_LogContext"] = new[]
        {
            "Ollama restarted with a {0:N0}-token context window",
            "Ollama перезапущена с окном контекста {0:N0} токенов",
            "Ollama 已以 {0:N0} token 的上下文窗口重启",
        },
        ["L_OllamaGone"] = new[]
        {
            "Ollama stopped - OpenCode was closed with it.",
            "Ollama остановилась - вместе с ней закрыт и OpenCode.",
            "Ollama 已停止 - OpenCode 也随之关闭。",
        },
        ["L_LogOllamaGone"] = new[]
        {
            "Ollama is gone - closing OpenCode as well",
            "Ollama закрылась - закрываем и OpenCode",
            "Ollama 已关闭 - 一并关闭 OpenCode",
        },
        ["L_LogOllamaSilent"] = new[] { "Ollama does not answer: {0}", "Ollama не отвечает: {0}", "Ollama 无响应：{0}" },
        ["L_LogOllamaBlind"] = new[]
        {
            "Ollama sees no models in {0}, restarting it with the right folder",
            "Ollama не видит моделей в {0}, перезапуск с нужной папкой",
            "Ollama 在 {0} 中看不到模型，正在以正确的目录重启",
        },
        ["L_LogInstallTo"] = new[] { "=== Installing into {0}", "=== Установка в {0}", "=== 正在安装到 {0}" },
        ["L_LogDone"] = new[] { "=== Done", "=== Готово", "=== 完成" },
        ["L_LogInstallFailed"] = new[] { "Installation failed: {0}", "Установка сорвалась: {0}", "安装失败：{0}" },
        ["L_LogUpdateFailed"] = new[] { "Update failed: {0}", "Обновление не прошло: {0}", "更新失败：{0}" },
        ["L_LogCatalog"] = new[] { "Catalog: {0}", "Каталог: {0}", "模型库：{0}" },
        ["L_LogCancelled"] = new[] { "Cancelled: {0}", "Отменено: {0}", "已取消：{0}" },
        ["L_LogManifest"] = new[]
        {
            "A manifest could not be read - cleanup of partial downloads skipped.",
            "Манифест не прочитался - уборка недокачанного пропущена.",
            "清单读取失败 - 已跳过未完成下载的清理。",
        },
        ["L_LogFreed"] = new[]
        {
            "Partial downloads removed, {0:0.#} MB freed",
            "Недокачанное удалено, освободилось {0:0.#} МБ",
            "已删除未完成的下载，释放 {0:0.#} MB",
        },
        ["L_LogPulling"] = new[] { "=== Downloading {0}", "=== Загрузка {0}", "=== 正在下载 {0}" },
        ["L_LogPulled"] = new[] { "=== {0} is in place", "=== {0} на месте", "=== {0} 已就位" },
        ["L_LogPullPaused"] = new[] { "Download paused: {0}", "Загрузка приостановлена: {0}", "下载已暂停：{0}" },
        ["L_LogPullFailed"] = new[] { "Download failed: {0}", "Закачка сорвалась: {0}", "下载失败：{0}" },
        ["L_LogDeleted"] = new[] { "Deleted: {0}", "Удалено: {0}", "已删除：{0}" },
        ["L_LogDeleteFailed"] = new[] { "Could not delete: {0}", "Не удалилось: {0}", "删除失败：{0}" },
        ["L_LogParams"] = new[] { "Parameters for {0} written: {1}", "Параметры {0} записаны: {1}", "已写入 {0} 的参数：{1}" },
        ["L_LogParamsFailed"] = new[] { "Parameters not written: {0}", "Параметры не записались: {0}", "参数写入失败：{0}" },
        ["L_LogRun"] = new[]
        {
            "=== OpenCode: model {0}, project {1}",
            "=== OpenCode: модель {0}, проект {1}",
            "=== OpenCode：模型 {0}，项目 {1}",
        },
        ["L_LogModelsHere"] = new[] { "Models already live in {0}", "Модели уже лежат в {0}", "模型已经在 {0} 中" },
        ["L_LogModelsMove"] = new[] { "Moving models: {0} -> {1}", "Перенос моделей: {0} -> {1}", "正在迁移模型：{0} -> {1}" },
        ["L_LogTargetExists"] = new[]
        {
            "The target folder already exists - the old location was left as is.",
            "Папка назначения уже есть - старое место оставлено как есть.",
            "目标文件夹已存在 - 旧位置保持原样。",
        },
        ["L_LogOtherDrive"] = new[]
        {
            "Different drives - a volume this size is not moved automatically, please move the folder by hand.",
            "Диски разные - такой объём автоматически не переносится, перенесите папку вручную.",
            "磁盘不同 - 这么大的数据不会自动迁移，请手动移动该文件夹。",
        },
        ["L_LogOllamaRestarted"] = new[]
        {
            "Ollama restarted, models in {0}",
            "Ollama перезапущена, модели в {0}",
            "Ollama 已重启，模型位于 {0}",
        },
    };
}
