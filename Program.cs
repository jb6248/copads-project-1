﻿
using System.Collections.Concurrent;

public abstract record Mode;
public record SingleThreaded() : Mode;
public record MultiThreaded() : Mode;
public record BothModes() : Mode;

public record FileCountResult(int folders, int files, long bytes);
public record ImageCountResult(int images, long bytes);
public record DuResult(double seconds, FileCountResult fileCountResult, ImageCountResult imageCountResult);

public record TaggedDuResult(DuResult result, Mode mode);

public class UsageCalculator {
    private readonly Mode mode;
    private readonly string path;
    public UsageCalculator(Mode mode, string path) {
        this.mode = mode;
        this.path = path;
    }

    private static bool IsImageFile(string path) {
        string[] imageExtensions = {".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tiff", ".webp"};
        foreach (string ext in imageExtensions) {
            if (path.EndsWith(ext)) {
                return true;
            }
        }
        return false;
    }

    private static FileCountResult CombineResult(FileCountResult a, FileCountResult b) {
        return new FileCountResult(a.folders + b.folders, a.files + b.files, a.bytes + b.bytes);
    }

    private static ImageCountResult CombineResult(ImageCountResult a, ImageCountResult b) {
        return new ImageCountResult(a.images + b.images, a.bytes + b.bytes);
    }

    private static DuResult CombineResult(DuResult a, DuResult b) {
        return new DuResult(a.seconds + b.seconds, CombineResult(a.fileCountResult, b.fileCountResult), CombineResult(a.imageCountResult, b.imageCountResult));
    }

    private static DuResult EmptyResult() {
        return new DuResult(0.0, new FileCountResult(0, 0, 0), new ImageCountResult(0, 0));
    }

    public List<TaggedDuResult> Calculate() {
        return mode switch {
            SingleThreaded => new List<TaggedDuResult>{this.CalculateUsageSequential()},
            MultiThreaded => new List<TaggedDuResult>{this.CalculateUsageParallel()},
            BothModes => this.CalculateBoth(),
            _ => throw new Exception("Not a valid mode")
        };
    }

    private void CheckPath() {
        if (!Directory.Exists(path) && !File.Exists(path)) {
            throw new Exception("Not a file");
        }
    }

    public List<TaggedDuResult> CalculateBoth() {
        return new List<TaggedDuResult>{this.CalculateUsageParallel(), this.CalculateUsageSequential()};
    }

    public TaggedDuResult CalculateUsageSequential() {
        CheckPath();
        int folders = 0;
        int files = 0;
        long bytes = 0;
        int images = 0;
        long imageBytes = 0;
        // start timer
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Queue<string> fileQueue = new Queue<string>();
        fileQueue.Enqueue(this.path);
        while (fileQueue.Count > 0) {
            string filepath = fileQueue.Dequeue();
            if (File.Exists(filepath)) {
                // get information and add to analytics
                try {
                    var fileInfo = new FileInfo(filepath);
                    long fileSize = fileInfo.Length;
                    bool isImage = IsImageFile(filepath);
                    files ++;
                    if (isImage) {
                        images ++;
                        imageBytes += fileSize;
                    }
                    bytes += fileSize;
                } catch (Exception e) {
                    // skip file if we can't get info about it
                }
            } else if (Directory.Exists(filepath)) {
                try {
                    foreach (string path in Directory.GetFileSystemEntries(filepath)) {
                        fileQueue.Enqueue(path);
                    }
                    folders ++;
                } catch (Exception e) {
                    // skip silently if we can't open directory
                }
            }
        }
        watch.Stop();
        return new TaggedDuResult(new DuResult(watch.Elapsed.TotalSeconds,
            new FileCountResult(folders, files, bytes),
            new ImageCountResult(images, imageBytes)),
            new SingleThreaded());
    }

    public TaggedDuResult CalculateUsageParallel() {
        CheckPath();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        DuResult result = CalculateUsageParallel(this.path);
        watch.Stop();
        return new TaggedDuResult(
            result with { seconds = watch.Elapsed.TotalSeconds },
            new MultiThreaded());
    }

    private DuResult CalculateUsageParallel(string path) {
        if (File.Exists(path)) {
            try {
                var fileInfo = new FileInfo(path);
                return new DuResult(0.0,
                    new FileCountResult(0, 1, fileInfo.Length),
                    new ImageCountResult(IsImageFile(path) ? 1 : 0, IsImageFile(path) ? fileInfo.Length : 0));
            } catch (Exception e) {
                return EmptyResult();
            }
        } else if (Directory.Exists(path)) {
            try {
                var results = new List<DuResult>();
                Parallel.ForEach(Directory.GetFileSystemEntries(path), filepath => {
                    var result = CalculateUsageParallel(filepath);
                    lock (results) {
                        results.Add(result);
                    }
                });
                // join results together
                if (results.Count == 0) {
                    return new DuResult(0.0, new FileCountResult(1, 0, 0), new ImageCountResult(0, 0));
                } else {
                    DuResult result = results[0];
                    for (int i = 1; i < results.Count; i++) {
                        result = CombineResult(result, results[i]);
                    }
                    return result with { fileCountResult = result.fileCountResult with { folders = result.fileCountResult.folders + 1 } };
                }
            } catch (Exception e) {
                return EmptyResult();
            }
        } else {
            throw new Exception("Not a valid file or directory: " + path);
        }
    }
}

public class DuProgram {
    private static string USAGE_MESSAGE = 
@"Usage: du [-s] [-d] [-b] <path>
Summarize disk usage of the set of FILES, recursively for directories.
You MUST specify one of the parameters, -s, -d, or -b
-s      Run in single threaded mode
-d      Run in parallel mode (uses all available processors)
-b      Run in both parallel and single threaded mode.
Runs parallel followed by sequential mode";

    public static void PrintResults(TaggedDuResult result) {
        /*
        In this format:

        Parallel Calculated in: 7.5724931s
        76,133 folders, 332,707 files, 42,299,411,348 bytes
        200 image files, 4, 224,4340 bytes
        */
        if (result == null) {
            return;
        }
        var duResult = result.result;
        var prelude = result.mode switch {
            SingleThreaded => "Sequential",
            MultiThreaded => "Parallel",
            BothModes => "Both",
            _ => "Unknown"
        };
        string imageFilesMessage = duResult.imageCountResult.images == 0 ? 
              "No image files found in the directory." 
            : $"{duResult.imageCountResult.images:n0} image files, {duResult.imageCountResult.bytes:n0} bytes";
        Console.WriteLine($"{prelude} Calculated in: {duResult.seconds}s");
        Console.WriteLine($"{duResult.fileCountResult.folders:n0} folders, {duResult.fileCountResult.files:n0} files, {duResult.fileCountResult.bytes:n0} bytes");
        Console.WriteLine(imageFilesMessage);
    }

    private static Mode GetModeFromArgs(string[] args) {
        if (args.Length == 0) {
            throw new Exception("Requires an argument: -s, -d, or -b");
        }
        
        bool singleThreaded = false, multiThreaded = false;
        if (!args[0].StartsWith("-")) {
            throw new Exception("First argument must be a command line option: -s, -d, or -b");
        }
        var arg = args[0];
        if (arg.Contains("-s"))
            singleThreaded = true;
        if (arg.Contains("-d"))
            multiThreaded = true;
        if (arg.Contains("-b")) {
            singleThreaded = true;
            multiThreaded = true;
        }
        return singleThreaded ? 
                      (multiThreaded ? new BothModes() : new SingleThreaded())
                    : (multiThreaded ? new MultiThreaded() : throw new Exception("No mode selected"));
    }
    private static string GetPathFromArgs(string[] args) {
        if (args.Length < 2) {
            throw new Exception("Not enough arguments - requires an option (-bsd) and a path");
        }
        return args[1];
    }
    static void Main() {
        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        try {
            Mode mode = GetModeFromArgs(args);
            string path = GetPathFromArgs(args);
            if (args.Length > 2) {
                throw new Exception("Too many arguments. 2 arguments only");
            }
            var program = new UsageCalculator(mode, path);
            var results = program.Calculate();
            Console.WriteLine("Directory '" + path + "':\n");
            foreach (TaggedDuResult result in results) {
                PrintResults(result);
                Console.WriteLine();
            }
        } catch (Exception _e) {
            // Console.WriteLine(_e);
            // Console.WriteLine("Arguments provided: ");
            // foreach (string arg in args) {
            //     Console.Write(arg + ", ");
            // }
            // Console.WriteLine();
            Console.WriteLine(USAGE_MESSAGE);
        }
    }
}
