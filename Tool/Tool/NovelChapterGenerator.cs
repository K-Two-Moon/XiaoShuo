using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tool;

/// <summary>根据改写大纲和同目录配置逐章生成正文。</summary>
internal sealed class NovelChapterGenerator
{
    private const string ConfigFileName = "大纲配置.json";
    private const string ChapterSchemaFileName = "novel-chapter.schema.json";
    private const string CodexCommandEnvironmentVariable = "CODEX_CMD";
    private const string CodexCommandPathEnvironmentVariable = "CODEX_CMD_PATH";
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    private readonly string rewrittenOutlineRootPath;
    private readonly string contentRootPath;

    public NovelChapterGenerator(string rewrittenOutlineRootPath, string contentRootPath)
    {
        this.rewrittenOutlineRootPath = rewrittenOutlineRootPath;
        this.contentRootPath = contentRootPath;
    }

    public void Run()
    {
        if (!Directory.Exists(rewrittenOutlineRootPath))
        {
            Console.WriteLine($"大纲改写目录不存在：{rewrittenOutlineRootPath}");
            return;
        }

        var outlinePath = SelectOutlineFile();
        if (outlinePath is null)
        {
            return;
        }

        var configPath = Path.Combine(Path.GetDirectoryName(outlinePath)!, ConfigFileName);
        if (!File.Exists(configPath))
        {
            Console.WriteLine($"所选大纲同目录下没有“{ConfigFileName}”，请先运行选项 5。");
            return;
        }

        string outlineJson;
        string configJson;
        OutlineInfo outline;
        GenerationConfig config;
        try
        {
            outlineJson = File.ReadAllText(outlinePath, Encoding.UTF8);
            configJson = File.ReadAllText(configPath, Encoding.UTF8);
            outline = ParseOutline(outlineJson, Path.GetFileName(outlinePath));
            config = ParseConfig(configJson);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        {
            Console.WriteLine($"读取大纲或配置失败：{ex.Message}");
            return;
        }

        if (!ReadGenerationRange(outline, out var generationStartChapter, out var generationEndChapter))
        {
            return;
        }

        var bookName = GetBookName(outlinePath, outline.Title);
        var bookOutputPath = Path.Combine(contentRootPath, bookName);
        var totalCount = generationEndChapter - generationStartChapter + 1;
        var existingCount = Enumerable.Range(generationStartChapter, totalCount)
            .Count(number => FindExistingChapterFile(bookOutputPath, number) is not null);
        var pendingCount = totalCount - existingCount;

        Console.WriteLine();
        Console.WriteLine($"已选择大纲：{Path.GetRelativePath(rewrittenOutlineRootPath, outlinePath)}");
        Console.WriteLine($"已读取配置：{Path.GetRelativePath(rewrittenOutlineRootPath, configPath)}");
        Console.WriteLine($"大纲标题：{outline.Title}");
        Console.WriteLine($"书名文件夹：{bookName}");
        Console.WriteLine($"大纲章节范围：第 {outline.StartChapter} 章至第 {outline.EndChapter} 章");
        Console.WriteLine($"本次生成范围：第 {generationStartChapter} 章至第 {generationEndChapter} 章，共 {totalCount} 章");
        Console.WriteLine($"章节字数：{config.MinimumLength}-{config.MaximumLength}");
        Console.WriteLine($"输出目录：{bookOutputPath}");
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
        var previousChapterPath = FindExistingChapterFile(bookOutputPath, generationStartChapter - 1);
        string? previousChapterText = previousChapterPath is null
            ? null
            : TryReadText(previousChapterPath);
        for (var chapterNumber = generationStartChapter;
             chapterNumber <= generationEndChapter;
             chapterNumber++)
        {
            var existingPath = FindExistingChapterFile(bookOutputPath, chapterNumber);
            if (existingPath is not null)
            {
                Console.WriteLine($"跳过第 {chapterNumber} 章，文件已存在：{Path.GetFileName(existingPath)}");
                previousChapterText = TryReadText(existingPath);
                continue;
            }

            Console.WriteLine();
            Console.WriteLine($"正在生成第 {chapterNumber} 章（{chapterNumber - generationStartChapter + 1}/{totalCount}）……");
            try
            {
                var output = CallAi(
                    outlineJson,
                    configJson,
                    config,
                    chapterNumber,
                    previousChapterText);
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

                previousChapterText = chapterText;
            }
            catch (Exception ex) when (ex is IOException
                                       or JsonException
                                       or InvalidOperationException
                                       or UnauthorizedAccessException
                                       or System.ComponentModel.Win32Exception)
            {
                Console.WriteLine($"第 {chapterNumber} 章生成失败：{ex.Message}");
                Console.WriteLine("已生成章节会保留；重新运行选项 6 可从缺失章节继续。");
                return;
            }
        }

        Console.WriteLine();
        Console.WriteLine($"正文生成完成：{bookOutputPath}");
    }

    private static bool ReadGenerationRange(
        OutlineInfo outline,
        out int generationStartChapter,
        out int generationEndChapter)
    {
        generationStartChapter = 0;
        generationEndChapter = 0;

        Console.WriteLine();
        Console.WriteLine($"所选大纲可生成章节范围：第 {outline.StartChapter} 章至第 {outline.EndChapter} 章");
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

        if (generationStartChapter < outline.StartChapter
            || generationEndChapter > outline.EndChapter)
        {
            Console.WriteLine(
                $"生成范围必须位于所选大纲的第 {outline.StartChapter} 章至第 {outline.EndChapter} 章之间。");
            return false;
        }

        return true;
    }

    private string? SelectOutlineFile()
    {
        var files = Directory.GetFiles(rewrittenOutlineRootPath, "*.json", SearchOption.AllDirectories)
            .Where(path => !string.Equals(Path.GetFileName(path), ConfigFileName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(
                path => Path.GetRelativePath(rewrittenOutlineRootPath, path),
                StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        if (files.Length == 0)
        {
            Console.WriteLine($"“大纲改写”目录下没有大纲 JSON 文件：{rewrittenOutlineRootPath}");
            return null;
        }

        Console.WriteLine("请选择用于生成正文的大纲：");
        Console.WriteLine("0. 返回");
        for (var index = 0; index < files.Length; index++)
        {
            var hasConfig = File.Exists(Path.Combine(Path.GetDirectoryName(files[index])!, ConfigFileName));
            Console.WriteLine($"{index + 1}. {Path.GetRelativePath(rewrittenOutlineRootPath, files[index])}" +
                              $"{(hasConfig ? string.Empty : "（缺少大纲配置）")}");
        }

        Console.Write("请输入序号：");
        if (!int.TryParse(Console.ReadLine(), out var selectedIndex)
            || selectedIndex < 0
            || selectedIndex > files.Length)
        {
            Console.WriteLine("无效序号");
            return null;
        }

        return selectedIndex == 0 ? null : files[selectedIndex - 1];
    }

    private static OutlineInfo ParseOutline(string json, string sourceName)
    {
        var root = JsonNode.Parse(json) as JsonObject
                   ?? throw new JsonException($"{sourceName} 不是有效的 JSON 对象。");
        var title = GetRequiredString(root, "大纲标题", sourceName);
        var start = GetRequiredInteger(root, "起始章节数", sourceName);
        var end = GetRequiredInteger(root, "结束章节数", sourceName);
        if (start <= 0 || end < start)
        {
            throw new JsonException($"{sourceName} 的章节范围无效：{start}-{end}。");
        }

        return new OutlineInfo(title, start, end);
    }

    private static GenerationConfig ParseConfig(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject
                   ?? throw new JsonException($"{ConfigFileName} 不是有效的 JSON 对象。");
        var styleObject = root["作者个人风格"] as JsonObject
                          ?? throw new JsonException("配置缺少对象字段“作者个人风格”。");
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
            throw new JsonException($"{sourceName} 的字段“{propertyName}”必须是非空字符串。");
        }

        return result.Trim();
    }

    private static int GetRequiredInteger(JsonObject root, string propertyName, string sourceName)
    {
        if (root[propertyName] is not JsonValue value || !value.TryGetValue<int>(out var result))
        {
            throw new JsonException($"{sourceName} 的字段“{propertyName}”必须是整数。");
        }

        return result;
    }

    private static string CallAi(
        string outlineJson,
        string configJson,
        GenerationConfig config,
        int chapterNumber,
        string? previousChapterText)
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
        process.StandardInput.Write(BuildPrompt(
            outlineJson,
            configJson,
            config,
            chapterNumber,
            previousChapterText));
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
        string outlineJson,
        string configJson,
        GenerationConfig config,
        int chapterNumber,
        string? previousChapterText)
    {
        var style = string.IsNullOrWhiteSpace(config.AuthorStyle)
            ? "未指定额外个人风格；使用自然、流畅、有画面感的中文商业小说文风。"
            : config.AuthorStyle;
        var previous = string.IsNullOrWhiteSpace(previousChapterText)
            ? "这是本次大纲范围内的第一章，没有上一章正文。"
            : previousChapterText;

        return $$"""
你是一名中文商业小说作者。请严格根据结构化大纲撰写第 {{chapterNumber}} 章正文。

硬性要求：
1. 输出必须严格符合给定 JSON Schema，只输出 JSON，不要输出 Markdown、代码块或解释。
2. “章节数”必须为 {{chapterNumber}}；“章节标题”简短明确；“正文”中不要重复章节标题。
3. 正文尽量控制在 {{config.MinimumLength}} 至 {{config.MaximumLength}} 字，不得用重复句或无意义对话凑字数。
4. 只展开大纲中属于第 {{chapterNumber}} 章的事件、人物变化和线索，不得提前完成后续章节剧情。
5. 结合总体概述、阶段大纲、人物脉络、主线、设定与阶段衔接，保证动机、规则、状态、时间线和因果一致。
6. 与上一章自然衔接，保留已发生的事实，避免重复已经完整描写过的情节。
7. 作者个人风格：{{style}}
8. 正文应有场景推进、动作、对话及必要描写，章末形成自然收束、悬念或下一章推动力。
9. 使用简体中文，不要提及大纲、配置、AI、提示词或创作过程，不要写本章总结。

正文生成配置 JSON：
{{configJson}}

完整结构化大纲 JSON：
{{outlineJson}}

上一章正文或衔接信息：
{{previous}}
""";
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

    private static string? TryReadText(string path)
    {
        try
        {
            return File.ReadAllText(path, Encoding.UTF8);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string GetBookName(string outlinePath, string fallbackTitle)
    {
        var directoryName = Path.GetFileName(Path.GetDirectoryName(outlinePath)) ?? string.Empty;
        if (directoryName.EndsWith("_章节", StringComparison.OrdinalIgnoreCase))
        {
            directoryName = directoryName[..^"_章节".Length];
        }

        return SanitizePathSegment(directoryName, fallbackTitle, 80);
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

    private sealed record OutlineInfo(string Title, int StartChapter, int EndChapter);
    private sealed record GenerationConfig(string AuthorStyle, int MinimumLength, int MaximumLength);
    private sealed record GeneratedChapter(int Number, string Title, string Content);
}
