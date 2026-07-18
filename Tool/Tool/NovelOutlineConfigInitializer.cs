using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tool;

/// <summary>
/// 在“大纲改写”的指定子目录中创建正文生成所需的大纲配置模板。
/// </summary>
internal sealed class NovelOutlineConfigInitializer
{
    private const string ConfigFileName = "大纲配置.json";

    private static readonly JsonSerializerOptions WriteJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string rewrittenOutlineRootPath;

    public NovelOutlineConfigInitializer(string rewrittenOutlineRootPath)
    {
        this.rewrittenOutlineRootPath = rewrittenOutlineRootPath;
    }

    public void Run()
    {
        if (!Directory.Exists(rewrittenOutlineRootPath))
        {
            Console.WriteLine($"大纲改写目录不存在：{rewrittenOutlineRootPath}");
            return;
        }

        var selectedDirectory = SelectSubdirectory();
        if (string.IsNullOrWhiteSpace(selectedDirectory))
        {
            return;
        }

        var configPath = Path.Combine(selectedDirectory, ConfigFileName);
        if (File.Exists(configPath))
        {
            Console.WriteLine($"配置文件已存在，不会替换：{configPath}");
            return;
        }

        var config = CreateDefaultConfig();

        try
        {
            // CreateNew 可确保即使文件在检查后刚被创建，也不会覆盖已有配置。
            using var stream = new FileStream(
                configPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);
            using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.Write(config.ToJsonString(WriteJsonOptions));
        }
        catch (IOException) when (File.Exists(configPath))
        {
            Console.WriteLine($"配置文件已存在，不会替换：{configPath}");
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"创建配置文件失败：{ex.Message}");
            return;
        }

        Console.WriteLine("大纲配置初始化完成，可在生成正文前按需修改各项内容。");
        Console.WriteLine($"已写入：{configPath}");
    }

    private string? SelectSubdirectory()
    {
        var directories = Directory.GetDirectories(
                rewrittenOutlineRootPath,
                "*",
                SearchOption.TopDirectoryOnly)
            .OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        if (directories.Length == 0)
        {
            Console.WriteLine($"“大纲改写”目录下没有可选择的子文件夹：{rewrittenOutlineRootPath}");
            return null;
        }

        Console.WriteLine("请选择要初始化大纲配置的子文件夹：");
        Console.WriteLine("0. 返回");
        for (var index = 0; index < directories.Length; index++)
        {
            Console.WriteLine($"{index + 1}. {Path.GetFileName(directories[index])}");
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

    private static JsonObject CreateDefaultConfig()
    {
        return new JsonObject
        {
            ["作者个人风格"] = new JsonObject
            {
                ["值"] = "",
                ["说明"] = "填写希望正文长期保持的作者个人风格，例如叙事视角、语言节奏、描写偏好、对话特点和需要避免的写法。"
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
