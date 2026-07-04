using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using System.Text.RegularExpressions;


namespace Tool
{
    internal class Program
    {
        public static void Main(string[] args)
        {
            while (true)
            {
                Console.WriteLine("=== 小说工具 ===");
                Console.WriteLine("1. 按章节拆分小说为JSON文件");
                Console.WriteLine("0. 退出");
                Console.Write("请选择：");

                var input = Console.ReadLine();
                Console.WriteLine();

                switch (input)
                {
                    case "1":
                        var path = NovelSplitter.SelectTxtFileFromSourceDirectory();
                        if (string.IsNullOrWhiteSpace(path))
                        {
                            Console.WriteLine();
                            break;
                        }

                        var splitter = new NovelSplitter();
                        splitter.SplitToJsonFiles(path);

                        Console.WriteLine("处理完成");
                        Console.WriteLine();
                        break;

                    case "0":
                        Console.WriteLine("已退出");
                        return;

                    default:
                        Console.WriteLine("无效选项");
                        Console.WriteLine();
                        break;
                }
            }
        }
    }


    public class NovelSplitter
    {
        public class ChapterData
        {
            public int ChapterIndex { get; set; }
            public string ChapterName { get; set; } = string.Empty;
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

            var outputDir = Path.Combine(dir, "output_json");
            Directory.CreateDirectory(outputDir);

            foreach (var chapter in chapters)
            {
                var json = JsonConvert.SerializeObject(chapter, Formatting.Indented);

                var fileName = $"Chapter_{chapter.ChapterIndex}.json";
                var outPath = Path.Combine(outputDir, fileName);

                File.WriteAllText(outPath, json, Encoding.UTF8);
            }

            Console.WriteLine($"共拆分 {chapters.Count} 章");
            Console.WriteLine($"输出目录：{outputDir}");
        }

        private List<ChapterData> ParseChapters(string text)
        {
            var result = new List<ChapterData>();

            // 更安全：只匹配行首章节
            var pattern = @"^第\s*(\d+)\s*章\s*(.*)$";
            var matches = Regex.Matches(text, pattern, RegexOptions.Multiline);

            if (matches.Count == 0)
            {
                Console.WriteLine("未找到章节格式（第X章 xxx）");
                return result;
            }

            for (int i = 0; i < matches.Count; i++)
            {
                var match = matches[i];

                int chapterIndex = int.Parse(match.Groups[1].Value);
                string chapterName = match.Groups[2].Value.Trim();

                // 正文起点：标题行结束
                int startIndex = match.Index + match.Length;

                // 正文终点：下一章标题开始
                int endIndex = (i + 1 < matches.Count)
                    ? matches[i + 1].Index
                    : text.Length;

                string content = text.Substring(startIndex, endIndex - startIndex).Trim();

                result.Add(new ChapterData
                {
                    ChapterIndex = chapterIndex,
                    ChapterName = chapterName,
                    Content = content
                });
            }

            return result;
        }

        public static string? SelectTxtFileFromSourceDirectory()
        {
            var sourceDir = FindSourceDirectory();
            if (string.IsNullOrWhiteSpace(sourceDir))
            {
                Console.WriteLine("未找到工程目录下的“原文”文件夹");
                return null;
            }

            var files = Directory.GetFiles(sourceDir, "*.txt", SearchOption.TopDirectoryOnly);
            Array.Sort(files, StringComparer.CurrentCultureIgnoreCase);

            if (files.Length == 0)
            {
                Console.WriteLine($"“原文”文件夹中没有txt文件：{sourceDir}");
                return null;
            }

            Console.WriteLine("请选择小说txt文件：");
            Console.WriteLine("0. 返回");
            for (var i = 0; i < files.Length; i++)
            {
                Console.WriteLine($"{i + 1}. {Path.GetFileName(files[i])}");
            }

            Console.Write("请输入序号：");
            var input = Console.ReadLine();

            if (!int.TryParse(input, out var index) || index < 0 || index > files.Length)
            {
                Console.WriteLine("无效序号");
                return null;
            }

            if (index == 0)
            {
                return null;
            }

            return files[index - 1];
        }

        private static string? FindSourceDirectory()
        {
            return FindSourceDirectoryFrom(Directory.GetCurrentDirectory())
                   ?? FindSourceDirectoryFrom(AppContext.BaseDirectory);
        }

        private static string? FindSourceDirectoryFrom(string startPath)
        {
            var dir = new DirectoryInfo(startPath);

            while (dir != null)
            {
                var sourceDir = Path.Combine(dir.FullName, "原文");
                if (Directory.Exists(sourceDir))
                {
                    return sourceDir;
                }

                dir = dir.Parent;
            }

            return null;
        }
    }
}