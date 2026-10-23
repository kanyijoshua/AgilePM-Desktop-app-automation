using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace AutoPM_Desktop.Services;

/// <summary>What was found in a project folder: a short text for the AI, and how many items it lists.</summary>
public sealed record FolderActivity(string Summary, int Items, bool FromGit);

/// <summary>
/// Reads what the user has done recently in a local code folder, so the AI can turn it into task updates.
/// In a git repository that is the user's own commits (subject, time and changed files) plus uncommitted changes;
/// in any other folder it is the files modified in the period. Only names and messages are read, not file contents.
/// </summary>
public static class ProjectFolderScanner
{
    private const int MaxSummaryChars = 3500;
    private const int MaxFilesPerCommit = 6;
    private const int MaxRecentFiles = 60;
    private const int MaxFilesScanned = 20_000;

    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", ".vscode", "bin", "obj", "node_modules", "packages", "dist", "build", "out", "target",
        "venv", ".venv", "__pycache__", ".next", ".nuget", "coverage", "logs", "log", "tmp", "temp",
    };

    /// <summary>Files that change without anyone working on the code (logs, editor state, temp files).</summary>
    private static readonly HashSet<string> SkippedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".log", ".tmp", ".temp", ".user", ".suo", ".cache", ".lock", ".pid", ".swp",
    };

    public static async Task<FolderActivity> ReadAsync(string folder, DateTime since, CancellationToken ct = default)
    {
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException($"The folder '{folder}' no longer exists. Choose it again.");

        try
        {
            if (await GitAsync(folder, ct, "rev-parse", "--is-inside-work-tree") is { } inside && inside.Trim() == "true")
            {
                return await ReadGitAsync(folder, since, ct);
            }
        }
        catch (Win32Exception)
        {
            // git isn't installed: fall back to file dates.
        }

        return await Task.Run(() => ReadRecentFiles(folder, since, ct), ct);
    }

    private static async Task<FolderActivity> ReadGitAsync(string folder, DateTime since, CancellationToken ct)
    {
        var email = (await GitAsync(folder, ct, "config", "user.email"))?.Trim();
        var name = (await GitAsync(folder, ct, "config", "user.name"))?.Trim();
        var author = !string.IsNullOrEmpty(email) ? email : name;
        var branch = (await GitAsync(folder, ct, "rev-parse", "--abbrev-ref", "HEAD"))?.Trim();

        var args = new List<string>
        {
            "log", "--all", "--no-merges", $"--since={since:yyyy-MM-dd HH:mm}", "--date=format:%Y-%m-%d %H:%M",
            "--pretty=format:%x1e%ad%x1f%s", "--name-only",
        };
        if (!string.IsNullOrEmpty(author)) args.Add($"--author={author}");
        var log = await GitAsync(folder, ct, [.. args]) ?? "";

        var sb = new StringBuilder();
        sb.AppendLine($"Code changes in the project folder \"{Path.GetFileName(folder.TrimEnd('\\', '/'))}\" " +
                      $"(git{(string.IsNullOrEmpty(branch) ? "" : $", branch {branch}")}) since {since:yyyy-MM-dd HH:mm}" +
                      $"{(string.IsNullOrEmpty(author) ? "" : $", commits by {author}")}:");

        var commits = 0;
        foreach (var record in log.Split('\x1e', StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = record.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length == 0) continue;
            var head = lines[0].Split('\x1f', 2);
            if (head.Length < 2) continue;

            var files = lines.Skip(1).ToList();
            var fileText = files.Count == 0 ? "" : $" [{string.Join(", ", files.Take(MaxFilesPerCommit))}{(files.Count > MaxFilesPerCommit ? $", +{files.Count - MaxFilesPerCommit} more" : "")}]";
            sb.AppendLine($"- {head[0]} {TextTools.Cut(head[1], 160)}{fileText}");
            commits++;
        }
        if (commits == 0) sb.AppendLine("- No commits in this period.");

        var status = await GitAsync(folder, ct, "status", "--porcelain") ?? "";
        var changed = status.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Length > 3 ? l[3..].Trim().Trim('"') : l.Trim())
            .ToList();
        if (changed.Count > 0)
        {
            sb.AppendLine($"Uncommitted changes (work in progress), {changed.Count} file(s): {string.Join(", ", changed.Take(15))}{(changed.Count > 15 ? ", …" : "")}");
        }

        return new FolderActivity(TextTools.Cut(sb.ToString().TrimEnd(), MaxSummaryChars), commits + (changed.Count > 0 ? 1 : 0), FromGit: true);
    }

    /// <summary>
    /// What the AI needs to know about the codebase to break a story into tasks: the layout, the files whose names
    /// match the story, the README, and the git history (recent commits, commits mentioning the story, uncommitted
    /// work). Apart from the README, only names, paths and commit messages are read, not file contents.
    /// </summary>
    public static async Task<string> ReadCodeContextAsync(string folder, string storyText, int maxChars, CancellationToken ct = default)
    {
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException($"The folder '{folder}' no longer exists. Choose it again.");

        var isGit = false;
        try
        {
            isGit = await GitAsync(folder, ct, "rev-parse", "--is-inside-work-tree") is { } inside && inside.Trim() == "true";
        }
        catch (Win32Exception)
        {
            // git isn't installed: read the folder only.
        }

        var files = isGit
            ? ((await GitAsync(folder, ct, "-c", "core.quotepath=off", "ls-files") ?? "")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(f => !f.Split('/').Any(SkippedFolders.Contains))
                .ToList())
            : await Task.Run(() => ListFiles(folder, ct), ct);
        var keywords = Keywords(storyText);

        var sb = new StringBuilder();
        var name = Path.GetFileName(folder.TrimEnd('\\', '/'));
        var branch = isGit ? (await GitAsync(folder, ct, "rev-parse", "--abbrev-ref", "HEAD"))?.Trim() : null;
        sb.AppendLine($"Project folder \"{name}\"{(isGit ? $" (git{(string.IsNullOrEmpty(branch) ? "" : $", branch {branch}")})" : "")}, {files.Count} files.");

        // Files named after the story's terms are the likeliest places to change.
        var relevant = files
            .Select(f => (Path: f, Hits: keywords.Count(k => f.Contains(k, StringComparison.OrdinalIgnoreCase))))
            .Where(f => f.Hits > 0)
            .OrderByDescending(f => f.Hits).ThenBy(f => f.Path.Length)
            .Take(MaxRelevantFiles)
            .Select(f => f.Path)
            .ToList();
        if (relevant.Count > 0)
        {
            sb.AppendLine("Files whose names match the story:");
            foreach (var f in relevant) sb.AppendLine($"- {f}");
        }

        if (isGit)
        {
            if (keywords.Count > 0)
            {
                var args = new List<string> { "log", "--all", "--no-merges", "-i", "-n", "12", "--date=short", "--pretty=format:%x1e%ad%x1f%s", "--name-only" };
                args.AddRange(keywords.Take(4).Select(k => $"--grep={k}"));
                AppendCommits(sb, "Earlier commits that mention the story's terms:", await GitAsync(folder, ct, [.. args]) ?? "");
            }

            AppendCommits(sb, "Most recent commits:", await GitAsync(folder, ct,
                "log", "--no-merges", "-n", "15", "--date=short", "--pretty=format:%x1e%ad%x1f%s", "--name-only") ?? "");

            var stat = (await GitAsync(folder, ct, "diff", "--stat=120", "HEAD") ?? "").Trim();
            var untracked = (await GitAsync(folder, ct, "ls-files", "--others", "--exclude-standard") ?? "")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            if (stat.Length > 0 || untracked.Count > 0)
            {
                sb.AppendLine("Uncommitted changes (work in progress):");
                if (stat.Length > 0) sb.AppendLine(TextTools.Cut(stat, 1200));
                if (untracked.Count > 0) sb.AppendLine($"New files: {string.Join(", ", untracked.Take(15))}{(untracked.Count > 15 ? ", …" : "")}");
            }
        }

        // Layout: each folder (two levels deep) with how many files it holds.
        var layout = files
            .GroupBy(f => string.Join('/', f.Split('/').SkipLast(1).Take(2)))
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Take(MaxLayoutFolders)
            .Select(g => $"- {(g.Key.Length == 0 ? "(root)" : g.Key)}: {g.Count()} files, e.g. {string.Join(", ", g.Take(3).Select(Path.GetFileName))}");
        sb.AppendLine("Folder layout:");
        foreach (var line in layout) sb.AppendLine(line);

        if (new[] { "README.md", "README.txt", "README" }.Select(r => Path.Combine(folder, r)).FirstOrDefault(File.Exists) is { } readme)
        {
            try
            {
                sb.AppendLine("README (start):");
                sb.AppendLine(TextTools.Cut((await File.ReadAllTextAsync(readme, ct)).Trim(), MaxReadmeChars));
            }
            catch (IOException)
            {
                // Unreadable README: the rest still helps.
            }
        }

        return TextTools.Cut(sb.ToString().TrimEnd(), maxChars);
    }

    private const int MaxRelevantFiles = 25;
    private const int MaxLayoutFolders = 40;
    private const int MaxReadmeChars = 1500;

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "that", "this", "shall", "should", "will", "must", "system", "allow", "able",
        "user", "users", "from", "into", "their", "them", "they", "have", "been", "when", "where", "which", "while",
        "before", "after", "other", "such", "only", "also", "each", "data", "information", "accordance", "including",
        "applicable", "unless", "within", "using", "based", "view", "make", "sure", "want", "need", "acceptance", "criteria",
    };

    /// <summary>The story's most frequent meaningful words (singular), to match file names and commit messages.</summary>
    private static List<string> Keywords(string text) =>
        System.Text.RegularExpressions.Regex.Matches(text, "[A-Za-z]{4,}")
            .Select(m => m.Value.ToLowerInvariant())
            .Where(w => !StopWords.Contains(w))
            .Select(w => w.Length > 5 && w.EndsWith('s') && !w.EndsWith("ss") ? w[..^1] : w)
            .GroupBy(w => w)
            .OrderByDescending(g => g.Count())
            .Take(8)
            .Select(g => g.Key)
            .ToList();

    private static void AppendCommits(StringBuilder sb, string heading, string log)
    {
        var lines = new List<string>();
        foreach (var record in log.Split('\x1e', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = record.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0 || parts[0].Split('\x1f', 2) is not [var date, var subject]) continue;
            var files = parts.Skip(1).ToList();
            var fileText = files.Count == 0 ? "" : $" [{string.Join(", ", files.Take(MaxFilesPerCommit))}{(files.Count > MaxFilesPerCommit ? $", +{files.Count - MaxFilesPerCommit} more" : "")}]";
            lines.Add($"- {date} {TextTools.Cut(subject, 160)}{fileText}");
        }
        if (lines.Count == 0) return;
        sb.AppendLine(heading);
        foreach (var line in lines) sb.AppendLine(line);
    }

    /// <summary>Relative paths (with '/') of the files in a folder that isn't a git repository.</summary>
    private static List<string> ListFiles(string folder, CancellationToken ct)
    {
        var files = new List<string>();
        var pending = new Stack<string>([folder]);
        while (pending.Count > 0 && files.Count < MaxFilesScanned)
        {
            ct.ThrowIfCancellationRequested();
            var dir = pending.Pop();
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    if (!SkippedFolders.Contains(Path.GetFileName(sub))) pending.Push(sub);
                }
                files.AddRange(Directory.EnumerateFiles(dir)
                    .Where(f => !SkippedExtensions.Contains(Path.GetExtension(f)))
                    .Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/')));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // Skip folders we can't read.
            }
        }
        return files;
    }

    private static FolderActivity ReadRecentFiles(string folder, DateTime since, CancellationToken ct)
    {
        var recent = new List<(string Path, DateTime Modified)>();
        var scanned = 0;
        var pending = new Stack<string>([folder]);
        while (pending.Count > 0 && scanned < MaxFilesScanned)
        {
            ct.ThrowIfCancellationRequested();
            var dir = pending.Pop();
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    if (!SkippedFolders.Contains(Path.GetFileName(sub))) pending.Push(sub);
                }
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    scanned++;
                    if (SkippedExtensions.Contains(Path.GetExtension(file))) continue;
                    var modified = File.GetLastWriteTime(file);
                    if (modified >= since) recent.Add((Path.GetRelativePath(folder, file), modified));
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // Skip folders we can't read.
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Files changed in the project folder \"{Path.GetFileName(folder.TrimEnd('\\', '/'))}\" since {since:yyyy-MM-dd HH:mm} " +
                      "(not a git repository, so only file names and times are known):");
        foreach (var day in recent.OrderByDescending(f => f.Modified).Take(MaxRecentFiles).GroupBy(f => f.Modified.Date))
        {
            sb.AppendLine($"- {day.Key:yyyy-MM-dd}: {string.Join(", ", day.Select(f => f.Path))}");
        }
        if (recent.Count == 0) sb.AppendLine("- No files changed in this period.");
        else if (recent.Count > MaxRecentFiles) sb.AppendLine($"(+{recent.Count - MaxRecentFiles} more files)");

        return new FolderActivity(TextTools.Cut(sb.ToString().TrimEnd(), MaxSummaryChars), recent.Count, FromGit: false);
    }

    /// <summary>Runs git in the folder; returns its output, or null when it fails (e.g. not a repository).</summary>
    private static async Task<string?> GitAsync(string folder, CancellationToken ct, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = folder,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in args) start.ArgumentList.Add(a);

        using var process = Process.Start(start) ?? throw new Win32Exception("git could not be started.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));

        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        _ = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw new TimeoutException("git took too long to answer in that folder.");
        }

        return process.ExitCode == 0 ? await output : null;
    }
}
