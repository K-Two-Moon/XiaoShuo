using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Newtonsoft.Json;

namespace Tool;

internal class NovelSummarizer
{
    private const string CodexCommandEnvironmentVariable = "CODEX_CMD";
    private const string CodexCommandPathEnvironmentVariable = "CODEX_CMD_PATH";
    private const string SummarySchemaFileName = "chapter-summary.schema.json";

    private readonly string chapterSplitPath;
    private readonly string summaryOutputPath;
    private int startChapter;
    private int endChapter;

    public NovelSummarizer(string chapterSplitPath, string summaryOutputPath)
    {
        this.chapterSplitPath = chapterSplitPath;
        this.summaryOutputPath = summaryOutputPath;
    }

    public void run()
    {
        if (string.IsNullOrWhiteSpace(chapterSplitPath) || !Directory.Exists(chapterSplitPath))
        {
            Console.WriteLine($"章节拆分目录不存在或路径为空：{chapterSplitPath}");
            return;
        }

        if (string.IsNullOrWhiteSpace(summaryOutputPath) || !Directory.Exists(summaryOutputPath))
        {
            Console.WriteLine($"摘要输出目录不存在或路径为空：{summaryOutputPath}");
            return;
        }

        var selectedDirectory = SelectChapterDirectory();
        if (string.IsNullOrWhiteSpace(selectedDirectory))
        {
            return;
        }

        var chapters = ReadChapterJsonFiles(selectedDirectory);
        if (chapters.Length == 0)
        {
            return;
        }

        if (!ReadChapterRange())
        {
            return;
        }

        var selectedDirectoryName = Path.GetFileName(selectedDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var outputDirectory = Path.Combine(summaryOutputPath, selectedDirectoryName);
        Directory.CreateDirectory(outputDirectory);

        for (var chapterNumber = startChapter; chapterNumber <= endChapter; chapterNumber++)
        {
            var chapter = chapterNumber <= chapters.Length ? chapters[chapterNumber - 1] : null;
            if (chapter == null)
            {
                Console.WriteLine($"第 {chapterNumber} 章不存在，跳过。");
                continue;
            }

            Console.WriteLine($"正在生成第 {chapter.ChapterIndex} 章摘要：{chapter.ChapterName}");

            ChapterSummary? summary;
            try
            {
                summary = GenerateSummary(chapter);
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidOperationException || ex is System.Text.Json.JsonException)
            {
                Console.WriteLine($"第 {chapter.ChapterIndex} 章摘要生成失败：{ex.Message}");
                continue;
            }

            var outputFilePath = Path.Combine(outputDirectory, BuildSummaryFileName(chapter));
            var json = System.Text.Json.JsonSerializer.Serialize(summary, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });

            File.WriteAllText(outputFilePath, json, Encoding.UTF8);
            Console.WriteLine($"已写入：{outputFilePath}");
        }
    }

    private string? SelectChapterDirectory()
    {
        var directories = Directory.GetDirectories(chapterSplitPath, "*", SearchOption.TopDirectoryOnly);
        Array.Sort(directories, StringComparer.CurrentCultureIgnoreCase);

        if (directories.Length == 0)
        {
            Console.WriteLine($"目录下没有可选择的章节文件夹：{chapterSplitPath}");
            return null;
        }

        Console.WriteLine("请选择章节JSON文件夹：");
        Console.WriteLine("0. 返回");
        for (var i = 0; i < directories.Length; i++)
        {
            Console.WriteLine($"{i + 1}. {Path.GetFileName(directories[i])}");
        }

        Console.Write("请输入序号：");
        var input = Console.ReadLine();

        if (!int.TryParse(input, out var index) || index < 0 || index > directories.Length)
        {
            Console.WriteLine("无效序号");
            return null;
        }

        if (index == 0)
        {
            return null;
        }

        return directories[index - 1];
    }

    private bool ReadChapterRange()
    {
        Console.WriteLine("输入起始章节数");
        if (!int.TryParse(Console.ReadLine(), out startChapter))
        {
            Console.WriteLine("起始章节数无效");
            return false;
        }

        Console.WriteLine("输入结束章节数");
        if (!int.TryParse(Console.ReadLine(), out endChapter))
        {
            Console.WriteLine("结束章节数无效");
            return false;
        }

        if (startChapter <= 0 || endChapter <= 0 || startChapter > endChapter)
        {
            Console.WriteLine("起始章节数和结束章节数必须为正整数，且起始章节数不能大于结束章节数。");
            return false;
        }

        return true;
    }

    private NovelSplitter.ChapterData?[] ReadChapterJsonFiles(string directory)
    {
        var files = Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly);
        Array.Sort(files, StringComparer.CurrentCultureIgnoreCase);

        if (files.Length == 0)
        {
            Console.WriteLine($"文件夹下没有JSON文件：{directory}");
            return Array.Empty<NovelSplitter.ChapterData?>();
        }

        var chapters = new NovelSplitter.ChapterData?[files.Length];

        foreach (var file in files)
        {
            NovelSplitter.ChapterData? chapter;
            try
            {
                var json = File.ReadAllText(file, Encoding.UTF8);
                chapter = JsonConvert.DeserializeObject<NovelSplitter.ChapterData>(json);
            }
            catch (Exception ex) when (ex is IOException || ex is Newtonsoft.Json.JsonException)
            {
                Console.WriteLine($"读取JSON失败：{Path.GetFileName(file)}，原因：{ex.Message}");
                continue;
            }

            if (chapter == null)
            {
                Console.WriteLine($"JSON内容为空或格式不正确：{Path.GetFileName(file)}");
                continue;
            }

            if (chapter.ChapterIndex <= 0)
            {
                Console.WriteLine($"章节数无效：{Path.GetFileName(file)}");
                continue;
            }

            if (chapter.ChapterIndex > chapters.Length)
            {
                Array.Resize(ref chapters, chapter.ChapterIndex);
            }

            var arrayIndex = chapter.ChapterIndex - 1;
            if (chapters[arrayIndex] != null)
            {
                Console.WriteLine($"第 {chapter.ChapterIndex} 章重复，已跳过：{Path.GetFileName(file)}");
                continue;
            }

            chapters[arrayIndex] = chapter;
        }

        return chapters;
    }

    private ChapterSummary GenerateSummary(NovelSplitter.ChapterData chapter)
    {
        var schemaPath = ResolveSchemaPath();
        var codexCommand = ResolveCodexCommand();

        var startInfo = new ProcessStartInfo
        {
            FileName = codexCommand,
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
        startInfo.ArgumentList.Add(schemaPath);
        startInfo.ArgumentList.Add("-");

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            throw new InvalidOperationException("启动 Codex 失败。");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        process.StandardInput.WriteLine(BuildPrompt(chapter));
        process.StandardInput.Close();

        process.WaitForExit();

        var output = outputTask.GetAwaiter().GetResult();
        var error = errorTask.GetAwaiter().GetResult();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Codex 执行失败，ExitCode={process.ExitCode}。{error}");
        }

        var summary = System.Text.Json.JsonSerializer.Deserialize<ChapterSummary>(output, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (summary == null)
        {
            throw new System.Text.Json.JsonException("Codex 返回的 JSON 解析结果为空。");
        }

        return summary;
    }

    private static string ResolveCodexCommand()
    {
        var command = Environment.GetEnvironmentVariable(CodexCommandEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(command))
        {
            return NormalizeCommandPath(command);
        }

        command = Environment.GetEnvironmentVariable(CodexCommandPathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(command))
        {
            return NormalizeCommandPath(command);
        }

        return "codex.cmd";
    }

    private static string NormalizeCommandPath(string command)
    {
        return command.Trim().Trim('"');
    }

    private static string ResolveSchemaPath()
    {
        var appSchemaPath = Path.Combine(AppContext.BaseDirectory, SummarySchemaFileName);
        if (File.Exists(appSchemaPath))
        {
            return appSchemaPath;
        }

        var currentDirectorySchemaPath = Path.Combine(Directory.GetCurrentDirectory(), "Tool", "Tool", SummarySchemaFileName);
        if (File.Exists(currentDirectorySchemaPath))
        {
            return currentDirectorySchemaPath;
        }

        throw new FileNotFoundException($"未找到结构化输出 Schema：{SummarySchemaFileName}");
    }

    private static string BuildPrompt(NovelSplitter.ChapterData chapter)
    {
        return $"""
请阅读下面这一章小说正文，并按 JSON Schema 输出章节摘要。

要求：
1. 只能输出符合 schema 的 JSON，不要输出 Markdown 或解释。
2. 字段必须完整。
3. 如果某个字段在本章没有明确内容，填“无”。
4. 摘要要基于正文，不要补写正文没有出现的信息。

章节数：{chapter.ChapterIndex}
章节名：{chapter.ChapterName}

字段说明：
- 章节数：当前章节数。
- 铺垫：本章前段设置的人物、场景、问题或线索。
- 发展：事件如何推进。
- 转折：本章关键变化或意外。
- 高潮：冲突或情绪最强处。
- 收束：本章末尾如何结束、留下什么状态。
- 核心事件：本章最重要的事件。
- 叙述主线：本章叙事推进的主线。
- 情感主线：本章情绪和关系变化的主线。
- 人物变化：重要人物在处境、认知、关系或能力上的变化。
- 心情：重要人物的主要心情。
- 性格：本章体现出的人物性格特征。
- 感情：人物之间的感情变化。
- 信息增量：新增设定、线索、背景、关系、目标、能力或风险。
- 当前时间线：本章所处时间点、先后关系或时间推进；不明确填“无”。

正文：
{chapter.Content}
""";
    }

    private static string BuildSummaryFileName(NovelSplitter.ChapterData chapter)
    {
        var rawName = string.IsNullOrWhiteSpace(chapter.ChapterName)
            ? $"第{chapter.ChapterIndex}章"
            : chapter.ChapterName;

        foreach (var invalidChar in Path.GetInvalidFileNameChars())
        {
            rawName = rawName.Replace(invalidChar, '_');
        }

        rawName = rawName.Trim();
        if (rawName.Length > 80)
        {
            rawName = rawName[..80].Trim();
        }

        return $"{chapter.ChapterIndex:0000}_{rawName}_摘要.json";
    }

    private sealed record ChapterSummary(
        [property: JsonPropertyName("章节数")] int ChapterIndex,
        [property: JsonPropertyName("铺垫")] string Setup,
        [property: JsonPropertyName("发展")] string Development,
        [property: JsonPropertyName("转折")] string TurningPoint,
        [property: JsonPropertyName("高潮")] string Climax,
        [property: JsonPropertyName("收束")] string Resolution,
        [property: JsonPropertyName("核心事件")] string CoreEvent,
        [property: JsonPropertyName("叙述主线")] string NarrativeLine,
        [property: JsonPropertyName("情感主线")] string EmotionalLine,
        [property: JsonPropertyName("人物变化")] string CharacterChange,
        [property: JsonPropertyName("心情")] string Mood,
        [property: JsonPropertyName("性格")] string Personality,
        [property: JsonPropertyName("感情")] string RelationshipEmotion,
        [property: JsonPropertyName("信息增量")] string NewInformation,
        [property: JsonPropertyName("当前时间线")] string CurrentTimeline);
}
