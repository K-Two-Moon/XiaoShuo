using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tool;

/// <summary>根据“新改写摘要”中每章对应的摘要和正文风格配置逐章生成正文。</summary>
internal sealed class NovelChapterGenerator
{
    private const string ConfigFileName = "正文风格配置.json";
    private const string SummaryFileSearchPattern = "*_改写摘要.json";
    private const string ChapterSchemaFileName = "novel-chapter.schema.json";
    private const string CodexCommandEnvironmentVariable = "CODEX_CMD";
    private const string CodexCommandPathEnvironmentVariable = "CODEX_CMD_PATH";

    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private static readonly JsonSerializerOptions PromptJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string rewrittenSummaryRootPath;
    private readonly string contentRootPath;

    public NovelChapterGenerator(string rewrittenSummaryRootPath, string contentRootPath)
    {
        this.rewrittenSummaryRootPath = rewrittenSummaryRootPath;
        this.contentRootPath = contentRootPath;
    }

    public void Run()
    {
        if (!Directory.Exists(rewrittenSummaryRootPath))
        {
            Console.WriteLine($"新改写摘要目录不存在：{rewrittenSummaryRootPath}");
            return;
        }

        var summaryDirectory = SelectSummaryDirectory();
        if (summaryDirectory is null)
        {
            return;
        }

        var configPath = Path.Combine(summaryDirectory, ConfigFileName);
        if (!File.Exists(configPath))
        {
            Console.WriteLine($"所选小说摘要目录下没有“{ConfigFileName}”，请先运行选项 5。");
            return;
        }

        ChapterSummaries summaries;
        string configJson;
        GenerationConfig config;
        try
        {
            summaries = ReadChapterSummaries(summaryDirectory);
            configJson = File.ReadAllText(configPath, Encoding.UTF8);
            config = ParseConfig(configJson);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        {
            Console.WriteLine($"读取改写摘要或正文风格配置失败：{ex.Message}");
            return;
        }

        if (!ReadGenerationRange(summaries, out var generationStartChapter, out var generationEndChapter))
        {
            return;
        }

        var bookName = SanitizePathSegment(Path.GetFileName(summaryDirectory), "未命名小说", 80);
        var bookOutputPath = Path.Combine(contentRootPath, bookName);
        var totalCount = generationEndChapter - generationStartChapter + 1;
        var existingCount = Enumerable.Range(generationStartChapter, totalCount)
            .Count(number => FindExistingChapterFile(bookOutputPath, number) is not null);
        var pendingCount = totalCount - existingCount;

        Console.WriteLine();
        Console.WriteLine($"已选择改写摘要目录：{Path.GetRelativePath(rewrittenSummaryRootPath, summaryDirectory)}");
        Console.WriteLine($"已读取正文风格配置：{Path.GetRelativePath(rewrittenSummaryRootPath, configPath)}");
        Console.WriteLine($"摘要章节范围：第 {summaries.StartChapter} 章至第 {summaries.EndChapter} 章");
        Console.WriteLine($"本次生成范围：第 {generationStartChapter} 章至第 {generationEndChapter} 章，共 {totalCount} 章");
        Console.WriteLine($"章节字数：{config.MinimumLength}-{config.MaximumLength}");
        Console.WriteLine($"输出目录：{bookOutputPath}");
        Console.WriteLine("每次调用 AI 仅传入当前章节摘要和正文风格配置，不传入整套改写摘要。");
        if (existingCount > 0)
        {
            Console.WriteLine($"检测到 {existingCount} 个已生成章节，将跳过；本次待生成 {pendingCount} 章。");
        }

        if (pendingCount == 0)
        {
            Console.WriteLine("所有章节均已存在，无需再次生成。");
            return;
        }

        Console.Write("确认开始逐章调用 AI？输入 y 确认：");
        if (!string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("已取消生成正文。");
            return;
        }

        Directory.CreateDirectory(bookOutputPath);
        for (var chapterNumber = generationStartChapter;
             chapterNumber <= generationEndChapter;
             chapterNumber++)
        {
            var existingPath = FindExistingChapterFile(bookOutputPath, chapterNumber);
            if (existingPath is not null)
            {
                Console.WriteLine($"跳过第 {chapterNumber} 章，文件已存在：{Path.GetFileName(existingPath)}");
                continue;
            }

            Console.WriteLine();
            Console.WriteLine($"正在生成第 {chapterNumber} 章（{chapterNumber - generationStartChapter + 1}/{totalCount}）……");
            try
            {
                var currentSummaryJson = summaries.ByChapter[chapterNumber].ToJsonString(PromptJsonOptions);
                var output = CallAi(configJson, config, chapterNumber, currentSummaryJson);
                var chapter = ParseGeneratedChapter(output, chapterNumber);
                var chapterText = $"第{chapter.Number}章 {chapter.Title}{Environment.NewLine}{Environment.NewLine}" +
                                  $"{chapter.Content.Trim()}{Environment.NewLine}";
                var safeTitle = SanitizePathSegment(chapter.Title, "未命名章节", 60);
                var outputPath = Path.Combine(
                    bookOutputPath,
                    $"{chapter.Number:D4}_第{chapter.Number}章_{safeTitle}.txt");

                using (var stream = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, Utf8WithoutBom))
                {
                    writer.Write(chapterText);
                }

                var length = chapter.Content.Count(character => !char.IsWhiteSpace(character));
                Console.WriteLine($"已生成第 {chapterNumber} 章，正文约 {length} 字：{outputPath}");
                if (length < config.MinimumLength || length > config.MaximumLength)
                {
                    Console.WriteLine($"提示：本章字数未落在配置范围 {config.MinimumLength}-{config.MaximumLength} 内。");
                }
            }
            catch (Exception ex) when (ex is IOException
                                       or InvalidOperationException
                                       or JsonException
                                       or System.ComponentModel.Win32Exception)
            {
                Console.WriteLine($"第 {chapterNumber} 章生成失败：{ex.Message}");
                Console.WriteLine("已停止，修复问题后重新运行即可从未完成章节继续。");
                return;
            }
        }

        Console.WriteLine($"正文生成完成：{bookOutputPath}");
    }

    private static bool ReadGenerationRange(
        ChapterSummaries summaries,
        out int generationStartChapter,
        out int generationEndChapter)
    {
        generationStartChapter = 0;
        generationEndChapter = 0;

        Console.WriteLine();
        Console.WriteLine($"所选改写摘要可生成章节范围：第 {summaries.StartChapter} 章至第 {summaries.EndChapter} 章");
        Console.Write("请输入生成的起始章节数：");
        if (!int.TryParse(Console.ReadLine(), out generationStartChapter))
        {
            Console.WriteLine("起始章节数无效。");
            return false;
        }

        Console.Write("请输入生成的结束章节数：");
        if (!int.TryParse(Console.ReadLine(), out generationEndChapter))
        {
            Console.WriteLine("结束章节数无效。");
            return false;
        }

        if (generationStartChapter <= 0
            || generationEndChapter <= 0
            || generationStartChapter > generationEndChapter)
        {
            Console.WriteLine("起始章节数和结束章节数必须为正整数，且起始章节数不能大于结束章节数。");
            return false;
        }

        if (generationStartChapter < summaries.StartChapter
            || generationEndChapter > summaries.EndChapter)
        {
            Console.WriteLine($"生成范围必须位于改写摘要的第 {summaries.StartChapter} 章至第 {summaries.EndChapter} 章之间。");
            return false;
        }

        return true;
    }

    private string? SelectSummaryDirectory()
    {
        var directories = Directory.GetDirectories(rewrittenSummaryRootPath, "*", SearchOption.TopDirectoryOnly)
            .Where(directory => Directory.GetFiles(directory, SummaryFileSearchPattern, SearchOption.TopDirectoryOnly).Length > 0)
            .OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        if (directories.Length == 0)
        {
            Console.WriteLine($"“新改写摘要”目录下没有可用于生成正文的小说文件夹：{rewrittenSummaryRootPath}");
            return null;
        }

        Console.WriteLine("请选择用于生成正文的小说：");
        Console.WriteLine("0. 返回");
        for (var index = 0; index < directories.Length; index++)
        {
            var hasConfig = File.Exists(Path.Combine(directories[index], ConfigFileName));
            Console.WriteLine($"{index + 1}. {Path.GetFileName(directories[index])}" +
                              (hasConfig ? string.Empty : "（缺少正文风格配置）"));
        }

        Console.Write("请输入序号：");
        if (!int.TryParse(Console.ReadLine(), out var selectedIndex)
            || selectedIndex < 0
            || selectedIndex > directories.Length)
        {
            Console.WriteLine("无效序号");
            return null;
        }

        return selectedIndex == 0 ? null : directories[selectedIndex - 1];
    }

    private static ChapterSummaries ReadChapterSummaries(string summaryDirectory)
    {
        var files = Directory.GetFiles(summaryDirectory, SummaryFileSearchPattern, SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        if (files.Length == 0)
        {
            throw new InvalidOperationException($"目录下没有改写摘要文件：{summaryDirectory}");
        }

        var summaries = new Dictionary<int, JsonObject>();
        foreach (var path in files)
        {
            var sourceName = Path.GetFileName(path);
            var root = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) as JsonObject
                       ?? throw new JsonException($"{sourceName} 不是有效的 JSON 对象。");
            var batchStart = GetRequiredInteger(root, "起始章节数", sourceName);
            var batchEnd = GetRequiredInteger(root, "结束章节数", sourceName);
            if (batchStart <= 0 || batchEnd < batchStart)
            {
                throw new JsonException($"{sourceName} 的章节范围无效：{batchStart}-{batchEnd}。");
            }

            var batchSummaries = root["章节摘要"] as JsonArray
                                 ?? throw new JsonException($"{sourceName} 缺少数组字段“章节摘要”。");
            if (batchSummaries.Count != batchEnd - batchStart + 1)
            {
                throw new JsonException($"{sourceName} 的章节摘要数量与章节范围不一致。");
            }

            for (var index = 0; index < batchSummaries.Count; index++)
            {
                if (batchSummaries[index] is not JsonObject summary)
                {
                    throw new JsonException($"{sourceName} 的第 {index + 1} 条章节摘要不是 JSON 对象。");
                }

                var chapterNumber = GetRequiredInteger(summary, "章节数", sourceName);
                var expectedChapter = batchStart + index;
                if (chapterNumber != expectedChapter)
                {
                    throw new JsonException($"{sourceName} 的第 {index + 1} 条摘要章节数为 {chapterNumber}，应为 {expectedChapter}。");
                }

                if (!summaries.TryAdd(chapterNumber, summary))
                {
                    throw new JsonException($"第 {chapterNumber} 章在多个改写摘要文件中重复出现。");
                }
            }
        }

        var startChapter = summaries.Keys.Min();
        var endChapter = summaries.Keys.Max();
        for (var chapterNumber = startChapter; chapterNumber <= endChapter; chapterNumber++)
        {
            if (!summaries.ContainsKey(chapterNumber))
            {
                throw new JsonException($"改写摘要不连续：缺少第 {chapterNumber} 章摘要。");
            }
        }

        return new ChapterSummaries(startChapter, endChapter, summaries);
    }

    private static GenerationConfig ParseConfig(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject
                   ?? throw new JsonException($"{ConfigFileName} 不是有效的 JSON 对象。");
        var styleObject = root["正文风格"] as JsonObject
                          ?? throw new JsonException("配置缺少对象字段“正文风格”。");
        var style = styleObject["值"]?.GetValue<string>()?.Trim() ?? string.Empty;
        var lengthObject = root["章节字数（范围区间）"] as JsonObject
                           ?? throw new JsonException("配置缺少对象字段“章节字数（范围区间）”。");
        var minimum = GetRequiredInteger(lengthObject, "最少字数", ConfigFileName);
        var maximum = GetRequiredInteger(lengthObject, "最多字数", ConfigFileName);
        if (minimum <= 0 || maximum < minimum)
        {
            throw new JsonException($"章节字数范围无效：{minimum}-{maximum}。");
        }

        return new GenerationConfig(style, minimum, maximum);
    }

    private static string CallAi(
        string configJson,
        GenerationConfig config,
        int chapterNumber,
        string currentSummaryJson)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveCodexCommand(),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add("--skip-git-repo-check");
        startInfo.ArgumentList.Add("--output-schema");
        startInfo.ArgumentList.Add(ResolveSchemaPath());
        startInfo.ArgumentList.Add("-");

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("启动 Codex 失败。");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        process.StandardInput.Write(BuildPrompt(configJson, config, chapterNumber, currentSummaryJson));
        process.StandardInput.Close();
        process.WaitForExit();

        var output = outputTask.GetAwaiter().GetResult();
        var error = errorTask.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Codex 执行失败，ExitCode={process.ExitCode}。{error}");
        }

        return output;
    }

    private static string BuildPrompt(
        string configJson,
        GenerationConfig config,
        int chapterNumber,
        string currentSummaryJson)
    {
        var style = string.IsNullOrWhiteSpace(config.BodyStyle)
            ? "未指定额外正文风格；使用自然、流畅、有画面感的中文商业小说文风。"
            : config.BodyStyle;

        return $$"""
你是一名中文商业小说作者。请只根据“本章改写摘要”和“正文风格配置”撰写第 {{chapterNumber}} 章正文。

硬性要求：
1. 输出必须严格符合给定 JSON Schema，只输出 JSON，不要输出 Markdown、代码块或解释。
2. “章节数”必须为 {{chapterNumber}}；“章节标题”简短明确；“正文”中不要重复章节标题。
3. 正文尽量控制在 {{config.MinimumLength}} 至 {{config.MaximumLength}} 字，不得用重复句或无意义对话凑字数。
4. 只能展开本章改写摘要中的事件、人物状态、时间线、关系和线索；不得读取、概括、补写或提前完成其他章节的剧情。
5. 本次输入没有整套摘要，也没有整体大纲。信息不足时应保守处理，不要编造与本章摘要冲突的设定或关键事件。
6. 正文应有场景推进、动作、对话及必要描写，章末形成自然收束、悬念或下一章推动力。
7. 正文风格要求：{{style}}
8. 使用简体中文，不要提及摘要、配置、AI、提示词或创作过程，不要写本章总结。

正文风格配置 JSON：
{{configJson}}

本章改写摘要 JSON（仅此一章）：
{{currentSummaryJson}}
""";
    }

    private static GeneratedChapter ParseGeneratedChapter(string json, int expectedNumber)
    {
        var root = JsonNode.Parse(json) as JsonObject
                   ?? throw new JsonException("AI 返回结果不是有效的 JSON 对象。");
        var number = GetRequiredInteger(root, "章节数", "AI 返回结果");
        if (number != expectedNumber)
        {
            throw new JsonException($"AI 返回章节数 {number}，预期为 {expectedNumber}。");
        }

        return new GeneratedChapter(
            number,
            GetRequiredString(root, "章节标题", "AI 返回结果"),
            GetRequiredString(root, "正文", "AI 返回结果"));
    }

    private static string GetRequiredString(JsonObject root, string propertyName, string sourceName)
    {
        if (root[propertyName] is not JsonValue value
            || !value.TryGetValue<string>(out var result)
            || string.IsNullOrWhiteSpace(result))
        {
            throw new JsonException($"{sourceName} 缺少非空字符串字段“{propertyName}”。");
        }

        return result.Trim();
    }

    private static int GetRequiredInteger(JsonObject root, string propertyName, string sourceName)
    {
        if (root[propertyName] is not JsonValue value || !value.TryGetValue<int>(out var result))
        {
            throw new JsonException($"{sourceName} 缺少整数字段“{propertyName}”。");
        }

        return result;
    }

    private static string? FindExistingChapterFile(string bookOutputPath, int chapterNumber)
    {
        if (!Directory.Exists(bookOutputPath))
        {
            return null;
        }

        return Directory.GetFiles(bookOutputPath, $"{chapterNumber:D4}_*.txt")
            .OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase)
            .FirstOrDefault();
    }

    private static string SanitizePathSegment(string value, string fallback, int maximumLength)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars().ToHashSet();
        var result = new string(value.Trim()
                .Select(character => invalidCharacters.Contains(character) || char.IsControl(character) ? '_' : character)
                .ToArray())
            .Trim()
            .TrimEnd('.');
        if (string.IsNullOrWhiteSpace(result))
        {
            result = fallback;
        }

        return result.Length <= maximumLength ? result : result[..maximumLength].TrimEnd();
    }

    private static string ResolveCodexCommand()
    {
        var command = Environment.GetEnvironmentVariable(CodexCommandEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(command))
        {
            return command.Trim().Trim('"');
        }

        command = Environment.GetEnvironmentVariable(CodexCommandPathEnvironmentVariable);
        return string.IsNullOrWhiteSpace(command) ? "codex.cmd" : command.Trim().Trim('"');
    }

    private static string ResolveSchemaPath()
    {
        var appPath = Path.Combine(AppContext.BaseDirectory, ChapterSchemaFileName);
        if (File.Exists(appPath))
        {
            return appPath;
        }

        var projectPath = Path.Combine(Directory.GetCurrentDirectory(), "Tool", "Tool", ChapterSchemaFileName);
        return File.Exists(projectPath)
            ? projectPath
            : throw new FileNotFoundException($"未找到结构化输出 Schema：{ChapterSchemaFileName}");
    }

    private sealed record ChapterSummaries(
        int StartChapter,
        int EndChapter,
        IReadOnlyDictionary<int, JsonObject> ByChapter);

    private sealed record GenerationConfig(string BodyStyle, int MinimumLength, int MaximumLength);
    private sealed record GeneratedChapter(int Number, string Title, string Content);
}
