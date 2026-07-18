using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tool;

/// <summary>
/// 在“新改写摘要”的小说子目录中创建生成正文所需的风格配置模板。
/// </summary>
internal sealed class NovelStyleConfigInitializer
{
    private const string ConfigFileName = "正文风格配置.json";

    private static readonly JsonSerializerOptions WriteJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string rewrittenSummaryRootPath;

    public NovelStyleConfigInitializer(string rewrittenSummaryRootPath)
    {
        this.rewrittenSummaryRootPath = rewrittenSummaryRootPath;
    }

    public void Run()
    {
        if (!Directory.Exists(rewrittenSummaryRootPath))
        {
            Console.WriteLine($"新改写摘要目录不存在：{rewrittenSummaryRootPath}");
            return;
        }

        var selectedDirectory = SelectNovelDirectory();
        if (selectedDirectory is null)
        {
            return;
        }

        var configPath = Path.Combine(selectedDirectory, ConfigFileName);
        if (File.Exists(configPath))
        {
            Console.WriteLine($"正文风格配置文件已存在，不会替换：{configPath}");
            return;
        }

        var config = CreateDefaultConfig();
        try
        {
            // CreateNew 可确保即使文件在检查后刚被创建，也不会覆盖已有配置。
            using var stream = new FileStream(configPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.Write(config.ToJsonString(WriteJsonOptions));
        }
        catch (IOException) when (File.Exists(configPath))
        {
            Console.WriteLine($"正文风格配置文件已存在，不会替换：{configPath}");
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"创建正文风格配置文件失败：{ex.Message}");
            return;
        }

        Console.WriteLine("正文风格配置初始化完成，可在生成正文前按需修改各项内容。");
        Console.WriteLine($"已写入：{configPath}");
    }

    private string? SelectNovelDirectory()
    {
        var directories = Directory.GetDirectories(rewrittenSummaryRootPath, "*", SearchOption.TopDirectoryOnly)
            .Where(ContainsRewrittenSummary)
            .OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        if (directories.Length == 0)
        {
            Console.WriteLine($"“新改写摘要”目录下没有包含改写摘要的小说文件夹：{rewrittenSummaryRootPath}");
            return null;
        }

        Console.WriteLine("请选择要初始化正文风格配置的小说：");
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

    private static bool ContainsRewrittenSummary(string directory) => Directory
        .GetFiles(directory, "*_改写摘要.json", SearchOption.TopDirectoryOnly)
        .Length > 0;

    private static JsonObject CreateDefaultConfig()
    {
        return new JsonObject
        {
            ["正文风格"] = new JsonObject
            {
                ["值"] = "",
                ["说明"] = "填写希望正文长期保持的风格，例如叙事视角、语言节奏、描写偏好、对话特点和需要避免的写法。"
            },
            ["章节字数（范围区间）"] = new JsonObject
            {
                ["最少字数"] = 2000,
                ["最多字数"] = 3000,
                ["说明"] = "设置单章正文期望的字数范围；生成正文时应尽量落在最少字数与最多字数之间。"
            }
        };
    }
}
