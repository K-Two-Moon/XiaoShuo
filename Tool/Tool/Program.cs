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
        public const string rewrittenOutlineFolderName = "大纲改写";
        public const string contentFolderName = "正文";
        public const string rewrittenSummaryFolderName = "新改写摘要";
        public static void Main(string[] args)
        {
            while (true)
            {
                Console.WriteLine("=== 小说工具 ===");
                Console.WriteLine("1. 按章节拆分小说为JSON文件");
                Console.WriteLine("2. 按章读取小说JSON文件生成章节摘要");
                Console.WriteLine("3. 合并章节摘要并生成结构化大纲");
                Console.WriteLine("4. 修改已有大纲的题材和风格");
                Console.WriteLine("5. 初始化正文风格配置（保存至新改写摘要）");
                Console.WriteLine("6. 根据改写大纲分批生成章节摘要（每批 20 章）");
                Console.WriteLine("7. 根据新改写摘要和正文风格配置逐章生成正文");
                Console.WriteLine("0. 退出");
                Console.Write("请选择：");

                var input = Console.ReadLine()?.Trim();
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
                    case "4":
                    {
                        var outlineRootPath = FindSourceDirectory(outlineFolderName);
                        if (string.IsNullOrWhiteSpace(outlineRootPath))
                        {
                            Console.WriteLine("未找到工程目录下的“大纲”文件夹");
                            Console.WriteLine();
                            break;
                        }

                        var projectRootPath = Directory.GetParent(outlineRootPath)?.FullName
                                              ?? Directory.GetCurrentDirectory();
                        var rewrittenOutlineRootPath = Path.Combine(projectRootPath, rewrittenOutlineFolderName);
                        var outlineRewriter = new NovelOutlineStyleRewriter(
                            outlineRootPath,
                            rewrittenOutlineRootPath);
                        outlineRewriter.Run();
                        Console.WriteLine();
                        break;
                    }
                    case "5":
                    {
                        var rewrittenSummaryRootPath = FindSourceDirectory(rewrittenSummaryFolderName);
                        if (string.IsNullOrWhiteSpace(rewrittenSummaryRootPath))
                        {
                            Console.WriteLine("未找到工程目录下的“新改写摘要”文件夹，请先运行选项 6 生成摘要。");
                            Console.WriteLine();
                            break;
                        }

                        var configInitializer = new NovelStyleConfigInitializer(rewrittenSummaryRootPath);
                        configInitializer.Run();
                        Console.WriteLine();
                        break;
                    }
                    case "6":
                    {
                        var rewrittenOutlineRootPath = FindSourceDirectory(rewrittenOutlineFolderName);
                        if (string.IsNullOrWhiteSpace(rewrittenOutlineRootPath))
                        {
                            Console.WriteLine("未找到工程目录下的“大纲改写”文件夹");
                            Console.WriteLine();
                            break;
                        }

                        var projectRootPath = Directory.GetParent(rewrittenOutlineRootPath)?.FullName
                                              ?? Directory.GetCurrentDirectory();
                        var rewrittenSummaryRootPath = Path.Combine(projectRootPath, rewrittenSummaryFolderName);
                        var rewrittenOutlineSummarizer = new RewrittenOutlineSummarizer(
                            rewrittenOutlineRootPath,
                            rewrittenSummaryRootPath);
                        rewrittenOutlineSummarizer.Run();
                        Console.WriteLine();
                        break;
                    }
                    case "7":
                    {
                        var rewrittenSummaryRootPath = FindSourceDirectory(rewrittenSummaryFolderName);
                        if (string.IsNullOrWhiteSpace(rewrittenSummaryRootPath))
                        {
                            Console.WriteLine("未找到工程目录下的“新改写摘要”文件夹，请先运行选项 6 生成摘要。");
                            Console.WriteLine();
                            break;
                        }

                        var projectRootPath = Directory.GetParent(rewrittenSummaryRootPath)?.FullName
                                              ?? Directory.GetCurrentDirectory();
                        var contentRootPath = Path.Combine(projectRootPath, contentFolderName);
                        var chapterGenerator = new NovelChapterGenerator(
                            rewrittenSummaryRootPath,
                            contentRootPath);
                        chapterGenerator.Run();
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
