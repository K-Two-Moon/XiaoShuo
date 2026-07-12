using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tool;

/// <summary>
/// 按用户给出的修改提示词重写已有大纲，并保持原有的结构化 JSON 格式。
/// </summary>
internal sealed class NovelOutlineStyleRewriter
{
    private const string CodexCommandEnvironmentVariable = "CODEX_CMD";
    private const string CodexCommandPathEnvironmentVariable = "CODEX_CMD_PATH";
    private const string OutlineSchemaFileName = "novel-outline.schema.json";

    private static readonly JsonSerializerOptions WriteJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly string[] RequiredRootProperties =
    [
        "大纲标题",
        "起始章节数",
        "结束章节数",
        "总体概述",
        "核心冲突",
        "叙事主线",
        "情感主线",
        "阶段大纲",
        "人物脉络",
        "设定与线索",
        "阶段衔接"
    ];

    private readonly string outlineRootPath;
    private readonly string rewrittenOutlineRootPath;

    public NovelOutlineStyleRewriter(string outlineRootPath, string rewrittenOutlineRootPath)
    {
        this.outlineRootPath = outlineRootPath;
        this.rewrittenOutlineRootPath = rewrittenOutlineRootPath;
    }

    public void Run()
    {
        if (!Directory.Exists(outlineRootPath))
        {
            Console.WriteLine($"大纲目录不存在：{outlineRootPath}");
            return;
        }

        var selectedFile = SelectOutlineFile();
        if (string.IsNullOrWhiteSpace(selectedFile))
        {
            return;
        }

        string originalJson;
        JsonObject originalOutline;
        try
        {
            originalJson = File.ReadAllText(selectedFile, Encoding.UTF8);
            originalOutline = ParseAndValidateOutline(originalJson, Path.GetFileName(selectedFile));
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        {
            Console.WriteLine($"读取大纲失败：{ex.Message}");
            return;
        }

        Console.WriteLine();
        Console.WriteLine("请输入大纲修改提示词（例如题材、风格、时代背景、主角职业等）：");
        Console.Write("> ");
        var modificationPrompt = Console.ReadLine()?.Trim();
        if (string.IsNullOrWhiteSpace(modificationPrompt))
        {
            Console.WriteLine("修改提示词不能为空。");
            return;
        }

        var startChapter = GetRequiredInteger(originalOutline, "起始章节数");
        var endChapter = GetRequiredInteger(originalOutline, "结束章节数");

        Console.WriteLine();
        Console.WriteLine("正在调用 AI 修改大纲……");

        string normalizedJson;
        try
        {
            var rewrittenJson = RewriteOutline(
                originalJson,
                modificationPrompt,
                startChapter,
                endChapter);

            var rewrittenOutline = ParseAndValidateOutline(rewrittenJson, "AI 返回结果");

            // 章节范围属于大纲元数据，必须与源大纲保持一致。
            rewrittenOutline["起始章节数"] = startChapter;
            rewrittenOutline["结束章节数"] = endChapter;
            normalizedJson = rewrittenOutline.ToJsonString(WriteJsonOptions);
        }
        catch (Exception ex) when (ex is IOException
                                   or InvalidOperationException
                                   or JsonException
                                   or System.ComponentModel.Win32Exception)
        {
            Console.WriteLine($"大纲改写失败：{ex.Message}");
            return;
        }

        var outputPath = BuildOutputPath(selectedFile);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, normalizedJson, Encoding.UTF8);

        Console.WriteLine();
        Console.WriteLine("大纲改写完成。相似度要求仅作为 AI 改写目标，请后续人工审核。");
        Console.WriteLine($"已写入：{outputPath}");
    }

    private string? SelectOutlineFile()
    {
        var files = Directory.GetFiles(outlineRootPath, "*.json", SearchOption.AllDirectories)
            .OrderBy(path => Path.GetRelativePath(outlineRootPath, path), StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        if (files.Length == 0)
        {
            Console.WriteLine($"大纲目录下没有 JSON 文件：{outlineRootPath}");
            return null;
        }

        Console.WriteLine("请选择需要修改风格的大纲：");
        Console.WriteLine("0. 返回");
        for (var index = 0; index < files.Length; index++)
        {
            Console.WriteLine($"{index + 1}. {Path.GetRelativePath(outlineRootPath, files[index])}");
        }

        Console.Write("请输入序号：");
        var input = Console.ReadLine();
        if (!int.TryParse(input, out var selectedIndex)
            || selectedIndex < 0
            || selectedIndex > files.Length)
        {
            Console.WriteLine("无效序号");
            return null;
        }

        return selectedIndex == 0 ? null : files[selectedIndex - 1];
    }

    private string BuildOutputPath(string selectedFile)
    {
        var relativePath = Path.GetRelativePath(outlineRootPath, selectedFile);
        var relativeDirectory = Path.GetDirectoryName(relativePath);
        var outputDirectory = string.IsNullOrWhiteSpace(relativeDirectory)
            ? rewrittenOutlineRootPath
            : Path.Combine(rewrittenOutlineRootPath, relativeDirectory);

        var baseFileName = Path.GetFileNameWithoutExtension(selectedFile);
        var candidatePath = Path.Combine(outputDirectory, $"{baseFileName}_风格改写.json");
        if (!File.Exists(candidatePath))
        {
            return candidatePath;
        }

        for (var version = 2; ; version++)
        {
            candidatePath = Path.Combine(outputDirectory, $"{baseFileName}_风格改写_{version}.json");
            if (!File.Exists(candidatePath))
            {
                return candidatePath;
            }
        }
    }

    private static string RewriteOutline(
        string originalJson,
        string modificationPrompt,
        int startChapter,
        int endChapter)
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
            originalJson,
            modificationPrompt,
            startChapter,
            endChapter));
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
        string originalJson,
        string modificationPrompt,
        int startChapter,
        int endChapter)
    {
        return $$"""
你是一名中文商业小说策划编辑。请根据用户的修改要求，对输入的结构化小说大纲进行大幅度风格改写。

请从底层设定和故事因果开始重新构思，不要只替换名称。
整体内容相似度尽量控制在 65% 以下，但这是一项目标，不需要在输出中报告或证明相似度。

用户的大纲修改提示词：
{{modificationPrompt}}

硬性要求：
1. 输出必须严格符合给定 JSON Schema，只输出 JSON，不要输出 Markdown、相似度说明或其他解释。
2. 输出 JSON 的字段和层级必须与原大纲结构一致；起始章节数保持 {{startChapter}}，结束章节数保持 {{endChapter}}。
3. 保留原大纲可借鉴的节奏功能和“阶段推进方式”，但必须重新设计具体创意，整体内容相似度尽量不高于 65%。
4. 禁止仅做同义改写、人物改名、地点改名或物品改名；不得沿用原大纲的连续事件链、核心冲突组合和相同结局。
5. 必须按照用户提示词，重点重新设计以下内容：
   - 设定、世界观、时代背景、社会环境、地域与周边势力结构；
   - 主角人物设定，包括年龄、性别、身份、性格、缺点、目标与行为方式；
   - 核心角色及其与主角的关系；
   - 主角持续推进故事的原因、目标变化和完整因果链；
   - 配角的身份、立场、功能以及与主线的交叉方式；
   - 故事线、核心主线、阶段冲突与关键转折；
   - 金手指的来源、规则、限制、成长方式和剧情代价；
   - 爽点的铺垫、触发、兑现及后续影响，避免无因果的强行打脸；
   - 主角获得某种物质、资源、道具、资格或能力后，由此延伸出的新故事链；
   - 阶段结尾与整体结尾，必须形成新的结果、代价或下一阶段钩子。
6. 新大纲内部必须逻辑自洽。世界观、人物动机、金手指规则、资源获取、爽点兑现和结尾之间要形成清晰因果关系。
7. “阶段大纲”仍按剧情目标或冲突变化划分，不要机械地每章一个阶段；各阶段覆盖章节必须位于 {{startChapter}} 到 {{endChapter}} 之间。
8. “人物脉络”写清人物目标、行动、性格或心理变化以及关系变化；“设定与线索”写清新设定、金手指、资源、伏笔和风险。
9. 所有文字字段使用简体中文。不要在结果中提及“原大纲”“改写”“相似度”或本提示词。

需要改写的原始大纲 JSON：
{{originalJson}}
""";
    }

    private static JsonObject ParseAndValidateOutline(string json, string sourceName)
    {
        var root = JsonNode.Parse(json) as JsonObject
                   ?? throw new JsonException($"{sourceName} 不是有效的 JSON 对象。");

        foreach (var propertyName in RequiredRootProperties)
        {
            if (!root.ContainsKey(propertyName))
            {
                throw new JsonException($"{sourceName} 缺少字段“{propertyName}”。");
            }
        }

        _ = GetRequiredInteger(root, "起始章节数");
        _ = GetRequiredInteger(root, "结束章节数");
        return root;
    }

    private static int GetRequiredInteger(JsonObject outline, string propertyName)
    {
        if (outline[propertyName] is not JsonValue value || !value.TryGetValue<int>(out var result))
        {
            throw new JsonException($"字段“{propertyName}”必须是整数。");
        }

        return result;
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
}
