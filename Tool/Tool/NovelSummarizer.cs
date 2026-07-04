using System.Text;
using Newtonsoft.Json;

namespace Tool;

internal class NovelSummarizer
{
    // 要求是读取一章然后调用AI生成一章摘要
    private string path;
    private int startChapter;
    private int endChapter;

    public NovelSummarizer(string path)
    {
        this.path = path;
    }

    public void run()
    {
        // 读取 path 下所有的文件夹名，然后按序号选择文件夹，然后读取该文件夹下的所有 JSON 文件，为数组，
        // 然后按顺序读取 JSON 文件，读取到的 JSON 文件是 NovelSplitter.ChapterData 类型的对象
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            Console.WriteLine($"目录不存在或路径为空：{path}");
            return;
        }

        var directories = Directory.GetDirectories(path, "*", SearchOption.TopDirectoryOnly);
        Array.Sort(directories, StringComparer.CurrentCultureIgnoreCase);

        if (directories.Length == 0)
        {
            Console.WriteLine($"目录下没有可选择的章节文件夹：{path}");
            return;
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
            return;
        }

        if (index == 0)
        {
            return;
        }

        var selectedDirectory = directories[index - 1];
        NovelSplitter.ChapterData?[] chapters = ReadChapterJsonFiles(selectedDirectory);
        if (chapters.Length == 0)
        {
            return;
        }

        Console.WriteLine("输入起始章节数");
        int startChapter = int.Parse(Console.ReadLine() ?? "-1");
        Console.WriteLine("输入结束章节数");
        int endChapter = int.Parse(Console.ReadLine() ?? "-1");
        if (startChapter <= 0 || endChapter <= 0 || startChapter > endChapter)
        {
            Console.WriteLine("起始章节数和结束章节数必须为正整数，且起始章节数不能大于结束章节数。");
            return;
        }
        this.startChapter = startChapter;
        this.endChapter = endChapter;

        for (var chapterNumber = startChapter; chapterNumber <= endChapter; chapterNumber++)
        {
            var chapter = chapters[chapterNumber - 1];
            if (chapter == null)
            {
                Console.WriteLine($"第 {chapterNumber} 章不存在，跳过。");
                continue;
            }

            Console.WriteLine($"读取第 {chapter.ChapterIndex} 章：{chapter.ChapterName}");
            // TODO: 在这里调用AI生成章节摘要。
        }
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

        var chapters = new NovelSplitter.ChapterData?[Math.Max(endChapter, files.Length)];

        foreach (var file in files)
        {
            NovelSplitter.ChapterData? chapter;
            try
            {
                var json = File.ReadAllText(file, Encoding.UTF8);
                chapter = JsonConvert.DeserializeObject<NovelSplitter.ChapterData>(json);
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException)
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
}
