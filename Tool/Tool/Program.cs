using System;
using System.Collections.Generic;
using System.IO;


namespace Tool
{
    // 生成摘要
    internal class Program
    {
        // 原文文件夹名
        public const string SourceFolderName = "原文";
        // 摘要文件夹名
        public const string ChapterSplitFolderName = "章节拆分";
        public static void Main(string[] args)
        {
            while (true)
            {
                Console.WriteLine("=== 小说工具 ===");
                Console.WriteLine("1. 按章节拆分小说为JSON文件");
                Console.WriteLine("2. 按章读取小说JSON文件生成章节摘要");
                Console.WriteLine("0. 退出");
                Console.Write("请选择：");

                var input = Console.ReadLine();
                Console.WriteLine();

                switch (input)
                {
                    case "1":
                        var path = SelectTxtFileFromSourceDirectory(SourceFolderName);
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

                    case "2":
                        var summaryPath = FindSourceDirectory(ChapterSplitFolderName);
                        if (string.IsNullOrWhiteSpace(summaryPath))
                        {
                            Console.WriteLine("未找到工程目录下的“章节拆分”文件夹");
                            Console.WriteLine();
                            break;
                        }
                        NovelSummarizer summarizer = new NovelSummarizer(summaryPath);
                        summarizer.run();
                        Console.WriteLine("处理完成");
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

        public static string? SelectTxtFileFromSourceDirectory(string sourceFolderName)
        {
            var sourceDir = FindSourceDirectory(sourceFolderName);
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

            return Path.GetRelativePath(Directory.GetCurrentDirectory(), files[index - 1]);
        }

        private static string? FindSourceDirectory(string sourceFolderName)
        {
            return FindSourceDirectoryFrom(Directory.GetCurrentDirectory(), sourceFolderName)
                   ?? FindSourceDirectoryFrom(AppContext.BaseDirectory, sourceFolderName);
        }

        private static string? FindSourceDirectoryFrom(string startPath, string sourceFolderName)
        {
            var dir = new DirectoryInfo(startPath);

            while (dir != null)
            {
                var sourceDir = Path.Combine(dir.FullName, sourceFolderName);
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
