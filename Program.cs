
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
    private Mode mode;
    private string path;
    public UsageCalculator(Mode mode, string path) {
        this.mode = mode;
        this.path = path;
    }

    private static bool isImageFile(string path) {
        string[] imageExtensions = {".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tiff", ".webp"};
        foreach (string ext in imageExtensions) {
            if (path.EndsWith(ext)) {
                return true;
            }
        }
        return false;
    }

    private static FileCountResult combineResult(FileCountResult a, FileCountResult b) {
        return new FileCountResult(a.folders + b.folders, a.files + b.files, a.bytes + b.bytes);
    }

    private static ImageCountResult combineResult(ImageCountResult a, ImageCountResult b) {
        return new ImageCountResult(a.images + b.images, a.bytes + b.bytes);
    }

    private static DuResult combineResult(DuResult a, DuResult b) {
        return new DuResult(a.seconds + b.seconds, combineResult(a.fileCountResult, b.fileCountResult), combineResult(a.imageCountResult, b.imageCountResult));
    }

    private static DuResult emptyResult() {
        return new DuResult(0.0, new FileCountResult(0, 0, 0), new ImageCountResult(0, 0));
    }

    public List<TaggedDuResult> calculate() {
        return mode switch {
            SingleThreaded => new List<TaggedDuResult>{this.calculateUsageSequential()},
            MultiThreaded => new List<TaggedDuResult>{this.calculateUsageParallel()},
            BothModes => this.calculateBoth(),
            _ => throw new Exception("Not a valid mode")
        };
    }

    private void checkPath() {
        if (!Directory.Exists(path) && !File.Exists(path)) {
            throw new Exception("Not a file");
        }
    }

    public List<TaggedDuResult> calculateBoth() {
        return new List<TaggedDuResult>{this.calculateUsageParallel(), this.calculateUsageSequential()};
    }

    public TaggedDuResult calculateUsageSequential() {
        checkPath();
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
                    bool isImage = isImageFile(filepath);
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

    public TaggedDuResult calculateUsageParallel() {
        checkPath();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        DuResult result = calculateUsageParallel(this.path);
        watch.Stop();
        return new TaggedDuResult(
            result with { seconds = watch.Elapsed.TotalSeconds },
            new MultiThreaded());
    }

    private DuResult calculateUsageParallel(string path) {
        if (File.Exists(path)) {
            try {
                var fileInfo = new FileInfo(path);
                return new DuResult(0.0,
                    new FileCountResult(0, 1, fileInfo.Length),
                    new ImageCountResult(isImageFile(path) ? 1 : 0, isImageFile(path) ? fileInfo.Length : 0));
            } catch (Exception e) {
                return emptyResult();
            }
        } else if (Directory.Exists(path)) {
            try {
                var results = new List<DuResult>();
                Parallel.ForEach(Directory.GetFileSystemEntries(path), filepath => {
                    var result = calculateUsageParallel(filepath);
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
                        result = combineResult(result, results[i]);
                    }
                    return result with { fileCountResult = result.fileCountResult with { folders = result.fileCountResult.folders + 1 } };
                }
            } catch (Exception e) {
                return emptyResult();
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

    public static void printResults(TaggedDuResult result) {
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

    private static Mode getMode(string[] args) {
        bool singleThreaded = false, multiThreaded = false;
        foreach(string arg in args) {
            if (arg.Contains("-s"))
                singleThreaded = true;
            if (arg.Contains("-d"))
                multiThreaded = true;
            if (arg.Contains("-b")) {
                singleThreaded = true;
                multiThreaded = true;
            }
        }
        return singleThreaded ? 
                      (multiThreaded ? new BothModes() : new SingleThreaded())
                    : (multiThreaded ? new MultiThreaded() : throw new Exception("No mode selected"));
    }
    private static string getFirstPath(string[] args) {
        foreach(string arg in args) {
            if (!arg.StartsWith("-")) {
                return arg;
            }
        }
        throw new Exception("Filepath not in arguments");
    }
    static void Main() {
        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        try {
            Mode mode = getMode(args);
            string path = getFirstPath(args);
            var program = new UsageCalculator(mode, path);
            var results = program.calculate();
            Console.WriteLine("Directory '" + path + "':");
            foreach (TaggedDuResult result in results) {
                printResults(result);
                Console.WriteLine();
            }
        } catch (Exception _e) {
            Console.WriteLine(_e);
            Console.WriteLine("Arguments provided: ");
            foreach (string arg in args) {
                Console.Write(arg + ", ");
            }
            Console.WriteLine();
            Console.WriteLine(USAGE_MESSAGE);
        }
    }
}
