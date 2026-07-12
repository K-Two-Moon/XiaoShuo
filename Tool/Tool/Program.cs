using System;
using System.Collections.Generic;
using System.IO;


namespace Tool
{
    // 生成摘要
    internal class Program
    {
        // 原文文件夹名
        public const string sourceFolderName = "原文";
        public const string chapterSplitFolderName = "章节拆分";
        // 摘要文件夹名
        public const string summaryFolderName = "摘要";
        public const string outlineFolderName = "大纲";
        public static void Main(string[] args)
        {
            while (true)
            {
                Console.WriteLine("=== 小说工具 ===");
                Console.WriteLine("1. 按章节拆分小说为JSON文件");
                Console.WriteLine("2. 按章读取小说JSON文件生成章节摘要");
                Console.WriteLine("3. 合并章节摘要并生成结构化大纲");
                Console.WriteLine("0. 退出");
                Console.Write("请选择：");

                var input = Console.ReadLine();
                Console.WriteLine();

                switch (input)
                {
                    case "1":
                        var path = SelectTxtFileFromSourceDirectory(sourceFolderName);
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
                        var chapterSplitPath = FindSourceDirectory(chapterSplitFolderName);
                        if (string.IsNullOrWhiteSpace(chapterSplitPath))
                        {
                            Console.WriteLine("未找到工程目录下的“章节拆分”文件夹");
                            Console.WriteLine();
                            break;
                        }
                        var summaryFolderPath = FindSourceDirectory(summaryFolderName);
                        if (string.IsNullOrWhiteSpace(summaryFolderPath))
                        {
                            Console.WriteLine("未找到工程目录下的“摘要”文件夹");
                            Console.WriteLine();
                            break;
                        }
                        NovelSummarizer summarizer = new NovelSummarizer(chapterSplitPath, summaryFolderPath);
                        summarizer.run();
                        Console.WriteLine("处理完成");
                        break;

                    case "3":
                    {
                        var summaryRootPath = FindSourceDirectory(summaryFolderName);
                        if (string.IsNullOrWhiteSpace(summaryRootPath))
                        {
                            Console.WriteLine("未找到工程目录下的“摘要”文件夹");
                            Console.WriteLine();
                            break;
                        }

                        var projectRootPath = Directory.GetParent(summaryRootPath)?.FullName
                                              ?? Directory.GetCurrentDirectory();
                        var outlineRootPath = Path.Combine(projectRootPath, outlineFolderName);
                        var outlineGenerator = new NovelOutlineGenerator(summaryRootPath, outlineRootPath);
                        outlineGenerator.Run();
                        Console.WriteLine();
                        break;
                    }
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

        public static string? SelectTxtFileFromSourceDirectory(string folderName)
        {
            var sourceDir = FindSourceDirectory(folderName);
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

        private static string? FindSourceDirectory(string folderName)
        {
            return FindSourceDirectoryFrom(Directory.GetCurrentDirectory(), folderName)
                   ?? FindSourceDirectoryFrom(AppContext.BaseDirectory, folderName);
        }

        private static string? FindSourceDirectoryFrom(string startPath, string folderName)
        {
            var dir = new DirectoryInfo(startPath);

            while (dir != null)
            {
                var sourceDir = Path.Combine(dir.FullName, folderName);
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
