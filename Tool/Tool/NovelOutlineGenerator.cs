using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tool;

/// <summary>
/// 从指定范围的章节摘要 JSON 中生成一份结构化剧情大纲。
/// </summary>
internal sealed class NovelOutlineGenerator
{
    private const string CodexCommandEnvironmentVariable = "CODEX_CMD";
    private const string CodexCommandPathEnvironmentVariable = "CODEX_CMD_PATH";
    private const string OutlineSchemaFileName = "novel-outline.schema.json";

    private static readonly JsonSerializerOptions ReadJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions WriteJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string summaryRootPath;
    private readonly string outlineRootPath;

    public NovelOutlineGenerator(string summaryRootPath, string outlineRootPath)
    {
        this.summaryRootPath = summaryRootPath;
        this.outlineRootPath = outlineRootPath;
    }

    public void Run()
    {
        if (!Directory.Exists(summaryRootPath))
        {
            Console.WriteLine($"摘要目录不存在：{summaryRootPath}");
            return;
        }

        var selectedDirectory = SelectSummaryDirectory();
        if (string.IsNullOrWhiteSpace(selectedDirectory))
        {
            return;
        }

        var summaries = ReadSummaryJsonFiles(selectedDirectory);
        if (summaries.Count == 0)
        {
            Console.WriteLine("所选文件夹中没有可用的章节摘要。");
            return;
        }

        if (!TryReadChapterRange(out var startChapter, out var endChapter))
        {
            return;
        }

        var selectedSummaries = summaries
            .Where(summary => summary.ChapterIndex >= startChapter && summary.ChapterIndex <= endChapter)
            .OrderBy(summary => summary.ChapterIndex)
            .ToArray();

        if (selectedSummaries.Length == 0)
        {
            Console.WriteLine($"没有找到第 {startChapter} 章到第 {endChapter} 章的摘要数据。");
            return;
        }

        PrintMissingChapters(selectedSummaries, startChapter, endChapter);

        var mergedSummaryJson = JsonSerializer.Serialize(selectedSummaries, WriteJsonOptions);
        Console.WriteLine($"已合并 {selectedSummaries.Length} 章摘要，正在让 AI 生成大纲……");

        NovelOutline outline;
        try
        {
            outline = GenerateOutline(mergedSummaryJson, startChapter, endChapter);
        }
        catch (Exception ex) when (ex is IOException
                                   or InvalidOperationException
                                   or JsonException
                                   or System.ComponentModel.Win32Exception)
        {
            Console.WriteLine($"大纲生成失败：{ex.Message}");
            return;
        }

        Directory.CreateDirectory(outlineRootPath);
        var sourceDirectoryName = Path.GetFileName(
            selectedDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var outputDirectory = Path.Combine(outlineRootPath, sourceDirectoryName);
        Directory.CreateDirectory(outputDirectory);

        var outputPath = Path.Combine(
            outputDirectory,
            $"{startChapter:0000}-{endChapter:0000}_大纲.json");
        var outlineJson = JsonSerializer.Serialize(outline, WriteJsonOptions);
        File.WriteAllText(outputPath, outlineJson, Encoding.UTF8);

        Console.WriteLine($"大纲已写入：{outputPath}");
    }

    private string? SelectSummaryDirectory()
    {
        var directories = Directory.GetDirectories(summaryRootPath, "*", SearchOption.TopDirectoryOnly);
        Array.Sort(directories, StringComparer.CurrentCultureIgnoreCase);

        if (directories.Length == 0)
        {
            Console.WriteLine($"摘要目录下没有可选择的文件夹：{summaryRootPath}");
            return null;
        }

        Console.WriteLine("请选择摘要 JSON 文件夹：");
        Console.WriteLine("0. 返回");
        for (var index = 0; index < directories.Length; index++)
        {
            var jsonCount = Directory.GetFiles(directories[index], "*.json", SearchOption.TopDirectoryOnly).Length;
            Console.WriteLine($"{index + 1}. {Path.GetFileName(directories[index])}（{jsonCount} 个 JSON）");
        }

        Console.Write("请输入序号：");
        var input = Console.ReadLine();
        if (!int.TryParse(input, out var selectedIndex)
            || selectedIndex < 0
            || selectedIndex > directories.Length)
        {
            Console.WriteLine("无效序号");
            return null;
        }

        return selectedIndex == 0 ? null : directories[selectedIndex - 1];
    }

    private static bool TryReadChapterRange(out int startChapter, out int endChapter)
    {
        Console.Write("请输入起始章节数：");
        if (!int.TryParse(Console.ReadLine(), out startChapter))
        {
            Console.WriteLine("起始章节数无效。");
            endChapter = 0;
            return false;
        }

        Console.Write("请输入结束章节数：");
        if (!int.TryParse(Console.ReadLine(), out endChapter))
        {
            Console.WriteLine("结束章节数无效。");
            return false;
        }

        if (startChapter <= 0 || endChapter <= 0 || startChapter > endChapter)
        {
            Console.WriteLine("章节数必须为正整数，且起始章节数不能大于结束章节数。");
            return false;
        }

        return true;
    }

    private static List<ChapterSummaryInput> ReadSummaryJsonFiles(string directory)
    {
        var files = Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly);
        Array.Sort(files, StringComparer.CurrentCultureIgnoreCase);

        var summaries = new List<ChapterSummaryInput>();
        var chapterIndexes = new HashSet<int>();

        foreach (var file in files)
        {
            try
            {
                var json = File.ReadAllText(file, Encoding.UTF8);
                var summary = JsonSerializer.Deserialize<ChapterSummaryInput>(json, ReadJsonOptions);
                if (summary is null || summary.ChapterIndex <= 0)
                {
                    Console.WriteLine($"摘要内容为空或章节数无效，已跳过：{Path.GetFileName(file)}");
                    continue;
                }

                if (!chapterIndexes.Add(summary.ChapterIndex))
                {
                    Console.WriteLine($"第 {summary.ChapterIndex} 章摘要重复，已跳过：{Path.GetFileName(file)}");
                    continue;
                }

                summaries.Add(summary);
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                Console.WriteLine($"读取摘要失败，已跳过 {Path.GetFileName(file)}：{ex.Message}");
            }
        }

        return summaries;
    }

    private static void PrintMissingChapters(
        IReadOnlyCollection<ChapterSummaryInput> selectedSummaries,
        int startChapter,
        int endChapter)
    {
        var existingChapters = selectedSummaries.Select(summary => summary.ChapterIndex).ToHashSet();
        var missingChapters = Enumerable.Range(startChapter, endChapter - startChapter + 1)
            .Where(chapter => !existingChapters.Contains(chapter))
            .ToArray();

        if (missingChapters.Length > 0)
        {
            Console.WriteLine($"提示：以下章节没有摘要，将使用其余章节继续生成：{string.Join("、", missingChapters)}");
        }
    }

    private static NovelOutline GenerateOutline(string mergedSummaryJson, int startChapter, int endChapter)
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

        process.StandardInput.Write(BuildPrompt(mergedSummaryJson, startChapter, endChapter));
        process.StandardInput.Close();
        process.WaitForExit();

        var output = outputTask.GetAwaiter().GetResult();
        var error = errorTask.GetAwaiter().GetResult();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Codex 执行失败，ExitCode={process.ExitCode}。{error}");
        }

        return JsonSerializer.Deserialize<NovelOutline>(output, ReadJsonOptions)
               ?? throw new JsonException("Codex 返回的 JSON 解析结果为空。");
    }

    private static string BuildPrompt(string mergedSummaryJson, int startChapter, int endChapter)
    {
        return $$"""
你是一名中文小说编辑。请根据下面合并后的章节摘要数组，整理第 {{startChapter}} 章到第 {{endChapter}} 章的结构化剧情大纲。

要求：
1. 只能依据输入摘要，不得补写原文中没有的信息。
2. 输出必须严格符合给定 JSON Schema，只输出 JSON，不要输出 Markdown 或解释。
3. “阶段大纲”只按故事目标、场景、冲突或因果关系的自然变化划分，并按故事发生顺序排列；不要给阶段预分章节数或章节范围。
4. “主要事件”按先后顺序合并重复内容，写清事件本身、前因后果及其推动作用；不要输出“涉及章节”等章节定位信息。
5. 大纲只负责把故事、人物动机、冲突、转折和结果交代清楚。具体章节拆分、篇幅分配和节奏快慢由后续 AI 根据生成范围自主决定。
6. “叙事主线”和“情感主线”提炼真正贯穿故事的内容，不要逐章复述。
7. “人物脉络”只保留对剧情有推动作用的重要人物；没有明确变化时写“无明确变化”。
8. “设定与线索”需要合并重复信息，并判断其类型和在本范围内的状态；无法判断时状态写“未明”，不要附加章节序号。
9. 如果只有一个剧情阶段，“阶段衔接”输出空数组。
10. 顶层“起始章节数”和“结束章节数”只是工具使用的范围元数据，必须分别为 {{startChapter}} 和 {{endChapter}}，不得据此给内部事件强行分章。
11. 所有文字字段使用简体中文，表达具体、清晰、紧凑，避免空泛评价。

合并后的章节摘要 JSON 数组：
{{mergedSummaryJson}}
""";
    }

    private static string ResolveCodexCommand()
    {
        var command = Environment.GetEnvironmentVariable(CodexCommandEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(command))
        {
            return NormalizeCommandPath(command);
        }

        command = Environment.GetEnvironmentVariable(CodexCommandPathEnvironmentVariable);
        return string.IsNullOrWhiteSpace(command)
            ? "codex.cmd"
            : NormalizeCommandPath(command);
    }

    private static string NormalizeCommandPath(string command) => command.Trim().Trim('"');

    private static string ResolveSchemaPath()
    {
        var appSchemaPath = Path.Combine(AppContext.BaseDirectory, OutlineSchemaFileName);
        if (File.Exists(appSchemaPath))
        {
            return appSchemaPath;
        }

        var projectSchemaPath = Path.Combine(
            Directory.GetCurrentDirectory(), "Tool", "Tool", OutlineSchemaFileName);
        if (File.Exists(projectSchemaPath))
        {
            return projectSchemaPath;
        }

        throw new FileNotFoundException($"未找到结构化输出 Schema：{OutlineSchemaFileName}");
    }

    private sealed record ChapterSummaryInput(
        [property: JsonPropertyName("章节数")] int ChapterIndex,
        [property: JsonPropertyName("情节概括")] string PlotSummary,
        [property: JsonPropertyName("叙述主线")] string NarrativeLine,
        [property: JsonPropertyName("情感主线")] string EmotionalLine,
        [property: JsonPropertyName("人物心情变化")] string MoodChange,
        [property: JsonPropertyName("人物性格改变")] string PersonalityChange,
        [property: JsonPropertyName("当前时间线")] string CurrentTimeline,
        [property: JsonPropertyName("增量信息")] string NewInformation);

    private sealed record NovelOutline(
        [property: JsonPropertyName("大纲标题")] string Title,
        [property: JsonPropertyName("起始章节数")] int StartChapter,
        [property: JsonPropertyName("结束章节数")] int EndChapter,
        [property: JsonPropertyName("总体概述")] string Overview,
        [property: JsonPropertyName("核心冲突")] string CoreConflict,
        [property: JsonPropertyName("叙事主线")] string[] NarrativeLines,
        [property: JsonPropertyName("情感主线")] string[] EmotionalLines,
        [property: JsonPropertyName("阶段大纲")] OutlineStage[] Stages,
        [property: JsonPropertyName("人物脉络")] CharacterArc[] CharacterArcs,
        [property: JsonPropertyName("设定与线索")] SettingOrClue[] SettingsAndClues,
        [property: JsonPropertyName("阶段衔接")] StageTransition[] StageTransitions);

    private sealed record OutlineStage(
        [property: JsonPropertyName("阶段名称")] string Name,
        [property: JsonPropertyName("阶段概述")] string Overview,
        [property: JsonPropertyName("主要事件")] OutlineEvent[] MainEvents,
        [property: JsonPropertyName("关键转折")] string[] TurningPoints,
        [property: JsonPropertyName("阶段结果")] string Result);

    private sealed record OutlineEvent(
        [property: JsonPropertyName("事件")] string Event,
        [property: JsonPropertyName("作用")] string Purpose);

    private sealed record CharacterArc(
        [property: JsonPropertyName("人物")] string Character,
        [property: JsonPropertyName("阶段目标")] string Goal,
        [property: JsonPropertyName("主要行动")] string[] MainActions,
        [property: JsonPropertyName("心理或性格变化")] string Change,
        [property: JsonPropertyName("关系变化")] string[] RelationshipChanges);

    private sealed record SettingOrClue(
        [property: JsonPropertyName("内容")] string Content,
        [property: JsonPropertyName("类型")] string Type,
        [property: JsonPropertyName("状态")] string Status,
        [property: JsonPropertyName("大纲作用")] string Purpose);

    private sealed record StageTransition(
        [property: JsonPropertyName("前一阶段")] string PreviousStage,
        [property: JsonPropertyName("后一阶段")] string NextStage,
        [property: JsonPropertyName("衔接说明")] string Description);
}
