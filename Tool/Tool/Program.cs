using System;
using System.Collections.Generic;
using System.IO;


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
}
