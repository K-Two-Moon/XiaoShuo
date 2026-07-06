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
Read the following Chinese novel chapter and output a concise JSON summary that matches the JSON Schema.

Rules:
1. Output only JSON. No Markdown or explanation.
2. Use exactly the schema fields. Keep every value short and avoid repetition.
3. Base the summary only on the chapter text. Do not invent missing information.
4. 情节概括 must combine setup/development/turning/climax/resolution in 1-3 short Chinese sentences; do not split them into separate fields.
5. 人物心情变化, 人物性格改变, 当前时间线 and 增量信息: write "无" if there is no clear change/information.
6. Write all string values in Chinese.

章节数: {chapter.ChapterIndex}
章节名: {chapter.ChapterName}

Fields:
- 章节数: current chapter number.
- 情节概括: combined plot summary, 1-3 short sentences.
- 叙述主线: narrative through-line, 1 sentence.
- 情感主线: feelings/relationship changes toward family, friends, lover, enemy, strangers, etc.; write "无" if no clear change.
- 人物心情变化: important characters' mood changes, brief; write "无" if no clear change.
- 人物性格改变: important characters' personality or behavioral tendency changes, brief; write "无" if no clear change.
- 当前时间线: time point, sequence, or time progression, brief; write "无" if unclear.
- 增量信息: new settings, clues, background, relationships, goals, abilities, or risks, brief; write "无" if none.

正文:
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
        [property: JsonPropertyName("情节概括")] string PlotSummary,
        [property: JsonPropertyName("叙述主线")] string NarrativeLine,
        [property: JsonPropertyName("情感主线")] string EmotionalLine,
        [property: JsonPropertyName("人物心情变化")] string MoodChange,
        [property: JsonPropertyName("人物性格改变")] string PersonalityChange,
        [property: JsonPropertyName("当前时间线")] string CurrentTimeline,
        [property: JsonPropertyName("增量信息")] string NewInformation);
}
