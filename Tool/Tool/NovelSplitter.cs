using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace Tool;

internal class NovelSplitter
{
    public class ChapterData
    {
        [JsonProperty("章节数")]
        public int ChapterIndex { get; set; }

        [JsonProperty("章节名")]
        public string ChapterName { get; set; } = string.Empty;

        [JsonProperty("正文")]
        public string Content { get; set; } = string.Empty;
    }

    public void SplitToJsonFiles(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            Console.WriteLine("文件不存在或路径为空");
            return;
        }

        var text = File.ReadAllText(filePath, Encoding.UTF8);

        var chapters = ParseChapters(text);

        if (chapters.Count == 0)
        {
            Console.WriteLine("未解析到章节");
            return;
        }

        var dir = Path.GetDirectoryName(filePath);

        if (string.IsNullOrEmpty(dir))
        {
            Console.WriteLine("路径无效");
            return;
        }

        var parentDir = Directory.GetParent(dir)?.FullName ?? dir;
        var novelName = Path.GetFileNameWithoutExtension(filePath);
        var outputDir = Path.Combine(parentDir, Program.chapterSplitFolderName, $"{novelName}_章节");
        Directory.CreateDirectory(outputDir);

        for (var i = 0; i < chapters.Count; i++)
        {
            var chapter = chapters[i];
            var json = JsonConvert.SerializeObject(chapter, Formatting.Indented);
            var fileName = BuildChapterFileName(i + 1, chapter);
            var outPath = Path.Combine(outputDir, fileName);

            File.WriteAllText(outPath, json, Encoding.UTF8);
        }

        Console.WriteLine($"共拆分 {chapters.Count} 章");
        Console.WriteLine($"输出目录：{outputDir}");
    }

    private List<ChapterData> ParseChapters(string text)
    {
        var result = new List<ChapterData>();

        // 只匹配独立成行的章节标题，N 可以是数字或常见中文数字。
        var pattern = @"^\s*第\s*([0-9零〇一二两三四五六七八九十百千万]+)\s*章\s*(.*)$";
        var matches = Regex.Matches(text, pattern, RegexOptions.Multiline);

        if (matches.Count == 0)
        {
            Console.WriteLine("未找到章节格式（第N章 xxx）");
            return result;
        }

        for (int i = 0; i < matches.Count; i++)
        {
            var match = matches[i];

            var chapterNumberText = match.Groups[1].Value;
            var chapterTitle = match.Value.Trim();
            string chapterName = match.Groups[2].Value.Trim();

            // 正文起点：标题行结束。
            int startIndex = match.Index + match.Length;

            // 正文终点：下一章标题开始
            int endIndex = (i + 1 < matches.Count)
                ? matches[i + 1].Index
                : text.Length;

            string content = text.Substring(startIndex, endIndex - startIndex).Trim();

            result.Add(new ChapterData
            {
                ChapterIndex = i + 1,
                ChapterName = string.IsNullOrWhiteSpace(chapterName) ? chapterTitle : $"第{chapterNumberText}章 {chapterName}",
                Content = content
            });
        }

        return result;
    }

    private static string BuildChapterFileName(int order, ChapterData chapter)
    {
        var rawName = string.IsNullOrWhiteSpace(chapter.ChapterName)
            ? $"第{order}章"
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

        return $"{order:0000}_{rawName}.json";
    }
}
