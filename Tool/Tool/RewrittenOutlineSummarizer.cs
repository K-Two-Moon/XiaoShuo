using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tool;

/// <summary>
/// 根据改写后的结构化大纲，以每批最多 20 章的方式生成章节摘要。
/// 每一批都会带上上一章的摘要，以保持剧情、人物状态和时间线连续。
/// </summary>
internal sealed class RewrittenOutlineSummarizer
{
    private const string ConfigFileName = "大纲配置.json";
    private const string SummarySchemaFileName = "rewritten-chapter-summary.schema.json";
    private const string CodexCommandEnvironmentVariable = "CODEX_CMD";
    private const string CodexCommandPathEnvironmentVariable = "CODEX_CMD_PATH";
    private const int ChaptersPerBatch = 20;

    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private static readonly JsonSerializerOptions WriteJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string rewrittenOutlineRootPath;
    private readonly string rewrittenSummaryRootPath;

    public RewrittenOutlineSummarizer(string rewrittenOutlineRootPath, string rewrittenSummaryRootPath)
    {
        this.rewrittenOutlineRootPath = rewrittenOutlineRootPath;
        this.rewrittenSummaryRootPath = rewrittenSummaryRootPath;
    }

    public void Run()
    {
        if (!Directory.Exists(rewrittenOutlineRootPath))
        {
            Console.WriteLine($"大纲改写目录不存在：{rewrittenOutlineRootPath}");
            return;
        }

        var novelDirectory = SelectNovelDirectory();
        if (novelDirectory is null)
        {
            return;
        }

        List<OutlineDocument> outlines;
        try
        {
            outlines = ReadOutlineDocuments(novelDirectory);
            EnsureContinuousChapterRanges(outlines);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        {
            Console.WriteLine($"读取改写大纲失败：{ex.Message}");
            return;
        }

        if (outlines.Count == 0)
        {
            Console.WriteLine("所选小说目录下没有可用的改写大纲 JSON 文件。");
            return;
        }

        var bookName = GetBookName(novelDirectory);
        var bookOutputPath = Path.Combine(rewrittenSummaryRootPath, bookName);
        Directory.CreateDirectory(bookOutputPath);

        var firstChapter = outlines[0].StartChapter;
        var lastChapter = outlines[^1].EndChapter;
        Console.WriteLine($"将生成第 {firstChapter} 章至第 {lastChapter} 章的改写摘要，每次最多 {ChaptersPerBatch} 章。");

        for (var batchStart = firstChapter; batchStart <= lastChapter; batchStart += ChaptersPerBatch)
        {
            var batchEnd = Math.Min(batchStart + ChaptersPerBatch - 1, lastChapter);
            var outputPath = Path.Combine(bookOutputPath, $"{batchStart:D4}-{batchEnd:D4}_改写摘要.json");

            if (TryReadValidBatch(outputPath, batchStart, batchEnd, out _))
            {
                Console.WriteLine($"第 {batchStart} 至 {batchEnd} 章摘要已存在且完整，跳过：{Path.GetFileName(outputPath)}");
                continue;
            }

            var previousSummary = batchStart <= 1
                ? null
                : TryReadChapterSummary(bookOutputPath, batchStart - 1);
            var relevantOutlines = outlines
                .Where(outline => outline.StartChapter <= batchEnd && outline.EndChapter >= batchStart)
                .ToList();

            Console.WriteLine($"正在生成第 {batchStart} 至 {batchEnd} 章改写摘要……");
            try
            {
                var generatedJson = GenerateBatchSummary(
                    relevantOutlines,
                    batchStart,
                    batchEnd,
                    previousSummary);
                var normalizedJson = ValidateAndNormalizeBatch(generatedJson, batchStart, batchEnd, "AI 返回结果");

                File.WriteAllText(outputPath, normalizedJson, Utf8WithoutBom);
                Console.WriteLine($"已写入：{outputPath}");
            }
            catch (Exception ex) when (ex is IOException
                                       or InvalidOperationException
                                       or JsonException
                                       or System.ComponentModel.Win32Exception)
            {
                Console.WriteLine($"第 {batchStart} 至 {batchEnd} 章摘要生成失败：{ex.Message}");
                Console.WriteLine("已停止，修复问题后重新运行即可从未完成的批次继续。");
                return;
            }
        }

        Console.WriteLine($"改写摘要生成完成：{bookOutputPath}");
    }

    private string? SelectNovelDirectory()
    {
        var directories = Directory.GetDirectories(rewrittenOutlineRootPath, "*", SearchOption.TopDirectoryOnly)
            .Where(ContainsOutlineJson)
            .OrderBy(path => Path.GetFileName(path), StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        if (directories.Length == 0)
        {
            Console.WriteLine($"“大纲改写”目录下没有包含改写大纲 JSON 的小说文件夹：{rewrittenOutlineRootPath}");
            return null;
        }

        Console.WriteLine("请选择小说：");
        Console.WriteLine("0. 返回");
        for (var index = 0; index < directories.Length; index++)
        {
            Console.WriteLine($"{index + 1}. {Path.GetFileName(directories[index])}");
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

    private static bool ContainsOutlineJson(string directory) => Directory
        .GetFiles(directory, "*.json", SearchOption.AllDirectories)
        .Any(path => !string.Equals(Path.GetFileName(path), ConfigFileName, StringComparison.OrdinalIgnoreCase));

    private static List<OutlineDocument> ReadOutlineDocuments(string novelDirectory)
    {
        var files = Directory.GetFiles(novelDirectory, "*.json", SearchOption.AllDirectories)
            .Where(path => !string.Equals(Path.GetFileName(path), ConfigFileName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        var outlines = new List<OutlineDocument>(files.Length);
        foreach (var path in files)
        {
            var json = File.ReadAllText(path, Encoding.UTF8);
            var root = JsonNode.Parse(json) as JsonObject
                       ?? throw new JsonException($"{Path.GetFileName(path)} 不是有效的 JSON 对象。");
            var start = GetRequiredInteger(root, "起始章节数", Path.GetFileName(path));
            var end = GetRequiredInteger(root, "结束章节数", Path.GetFileName(path));
            if (start <= 0 || end < start)
            {
                throw new JsonException($"{Path.GetFileName(path)} 的章节范围无效：{start}-{end}。");
            }

            outlines.Add(new OutlineDocument(path, json, start, end));
        }

        return outlines
            .OrderBy(outline => outline.StartChapter)
            .ThenBy(outline => outline.EndChapter)
            .ThenBy(outline => outline.Path, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static void EnsureContinuousChapterRanges(IReadOnlyList<OutlineDocument> outlines)
    {
        if (outlines.Count == 0)
        {
            return;
        }

        var expectedStart = outlines[0].StartChapter;
        foreach (var outline in outlines)
        {
            if (outline.StartChapter < expectedStart)
            {
                throw new InvalidOperationException(
                    $"改写大纲章节范围重叠：{Path.GetFileName(outline.Path)} 覆盖第 {outline.StartChapter}-{outline.EndChapter} 章。" +
                    "请在小说文件夹中只保留一套不重叠的大纲后再生成摘要。");
            }

            if (outline.StartChapter > expectedStart)
            {
                throw new InvalidOperationException(
                    $"改写大纲章节范围不连续：第 {expectedStart} 至 {outline.StartChapter - 1} 章没有对应大纲。" +
                    "请补齐大纲后再生成摘要。");
            }

            expectedStart = outline.EndChapter + 1;
        }
    }

    private static string GenerateBatchSummary(
        IReadOnlyCollection<OutlineDocument> outlines,
        int batchStart,
        int batchEnd,
        JsonObject? previousSummary)
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

        process.StandardInput.Write(BuildPrompt(outlines, batchStart, batchEnd, previousSummary));
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
        IReadOnlyCollection<OutlineDocument> outlines,
        int batchStart,
        int batchEnd,
        JsonObject? previousSummary)
    {
        var outlineJson = string.Join(
            "\n\n",
            outlines.Select(outline =>
                $"【改写大纲：第 {outline.StartChapter} 至 {outline.EndChapter} 章】\n{outline.Json}"));
        var previousSummaryJson = previousSummary is null
            ? "没有可用的上一章摘要。这是本次可生成范围的第一批，直接以改写大纲为准。"
            : previousSummary.ToJsonString(WriteJsonOptions);

        return $$"""
你是一名中文小说编辑。请根据以下“改写大纲”，为第 {{batchStart}} 至第 {{batchEnd}} 章分别生成章节摘要。

硬性要求：
1. 输出必须严格符合给定 JSON Schema；只输出 JSON，不要输出 Markdown、代码块、解释或额外字段。
2. 必须生成且只生成第 {{batchStart}} 至第 {{batchEnd}} 章的摘要：章节摘要数组按章节数升序排列，每章恰好一条，不能遗漏、合并、跳号或重复。
3. 每章摘要应服务于后续逐章写作：准确交代该章发生的关键事件、人物动机/关系/情绪变化、时间推进、伏笔与新增信息。
4. 严格服从改写大纲的世界观、事件因果和人物状态，但章节拆分与节奏由你自主安排。结合大纲顶层总范围、本批所在位置和上一章状态，合理分配铺垫、发展、转折与兑现；不要把阶段或事件机械地一项对应一章。
5. 摘要要具体、紧凑且有区分度。不要把整批压缩成阶段总述；每一章都必须有独立推进，同时为尚未展开的后续内容留出合理空间。
6. “上一章摘要”只用于承接已发生的事实。第 {{batchStart}} 章必须自然衔接它；本批后续章节则要按本批前一章的剧情自然递进。
7. 使用简体中文。不要提及大纲、AI、提示词、JSON Schema 或创作过程。

上一章摘要（第 {{batchStart - 1}} 章；若无则表示本范围首批）：
{{previousSummaryJson}}

相关改写大纲 JSON：
{{outlineJson}}
""";
    }

    private static string ValidateAndNormalizeBatch(string json, int batchStart, int batchEnd, string sourceName)
    {
        var root = JsonNode.Parse(json) as JsonObject
                   ?? throw new JsonException($"{sourceName} 不是有效的 JSON 对象。");
        ValidateBatch(root, batchStart, batchEnd, sourceName);
        return root.ToJsonString(WriteJsonOptions);
    }

    private static bool TryReadValidBatch(string path, int batchStart, int batchEnd, out JsonObject? root)
    {
        root = null;
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            var parsed = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) as JsonObject;
            if (parsed is null)
            {
                return false;
            }

            ValidateBatch(parsed, batchStart, batchEnd, Path.GetFileName(path));
            root = parsed;
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static JsonObject? TryReadChapterSummary(string bookOutputPath, int chapterNumber)
    {
        if (!Directory.Exists(bookOutputPath))
        {
            return null;
        }

        foreach (var path in Directory.GetFiles(bookOutputPath, "*_改写摘要.json", SearchOption.TopDirectoryOnly)
                     .OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase))
        {
            try
            {
                var root = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) as JsonObject;
                var summaries = root?["章节摘要"] as JsonArray;
                if (summaries is null)
                {
                    continue;
                }

                foreach (var summary in summaries)
                {
                    if (summary is JsonObject summaryObject
                        && TryGetInteger(summaryObject, "章节数", out var currentChapter)
                        && currentChapter == chapterNumber)
                    {
                        return summaryObject;
                    }
                }
            }
            catch (IOException)
            {
                // 某个旧文件无法读取时继续查找其他批次文件。
            }
            catch (JsonException)
            {
                // 不把格式错误的旧文件作为衔接上下文。
            }
        }

        return null;
    }

    private static void ValidateBatch(JsonObject root, int batchStart, int batchEnd, string sourceName)
    {
        var actualStart = GetRequiredInteger(root, "起始章节数", sourceName);
        var actualEnd = GetRequiredInteger(root, "结束章节数", sourceName);
        if (actualStart != batchStart || actualEnd != batchEnd)
        {
            throw new JsonException(
                $"{sourceName} 的章节范围为 {actualStart}-{actualEnd}，应为 {batchStart}-{batchEnd}。");
        }

        var summaries = root["章节摘要"] as JsonArray
                        ?? throw new JsonException($"{sourceName} 缺少数组字段“章节摘要”。");
        var expectedCount = batchEnd - batchStart + 1;
        if (summaries.Count != expectedCount)
        {
            throw new JsonException($"{sourceName} 应包含 {expectedCount} 条章节摘要，实际为 {summaries.Count} 条。");
        }

        for (var index = 0; index < summaries.Count; index++)
        {
            if (summaries[index] is not JsonObject summary)
            {
                throw new JsonException($"{sourceName} 的第 {index + 1} 条章节摘要不是 JSON 对象。");
            }

            var actualChapter = GetRequiredInteger(summary, "章节数", sourceName);
            var expectedChapter = batchStart + index;
            if (actualChapter != expectedChapter)
            {
                throw new JsonException(
                    $"{sourceName} 的第 {index + 1} 条摘要章节数为 {actualChapter}，应为 {expectedChapter}。");
            }
        }
    }

    private static int GetRequiredInteger(JsonObject root, string propertyName, string sourceName)
    {
        if (!TryGetInteger(root, propertyName, out var result))
        {
            throw new JsonException($"{sourceName} 缺少整数字段“{propertyName}”。");
        }

        return result;
    }

    private static bool TryGetInteger(JsonObject root, string propertyName, out int result)
    {
        result = 0;
        return root[propertyName] is JsonValue value && value.TryGetValue<int>(out result);
    }

    private static string GetBookName(string novelDirectory)
    {
        var name = Path.GetFileName(novelDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (name.EndsWith("_章节", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^"_章节".Length];
        }

        return SanitizePathSegment(name, "未命名小说", 80);
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
        var appPath = Path.Combine(AppContext.BaseDirectory, SummarySchemaFileName);
        if (File.Exists(appPath))
        {
            return appPath;
        }

        var projectPath = Path.Combine(Directory.GetCurrentDirectory(), "Tool", "Tool", SummarySchemaFileName);
        return File.Exists(projectPath)
            ? projectPath
            : throw new FileNotFoundException($"未找到结构化输出 Schema：{SummarySchemaFileName}");
    }

    private sealed record OutlineDocument(string Path, string Json, int StartChapter, int EndChapter);
}
