// Build:
// dotnet new console -n ColumnFileManager
// Replace Program.cs with this file
// dotnet run
//
// Tested: .NET 8 / Windows 10+ / macOS 12+
// Single file, no external dependencies, LINQ-free
//
// Features:
// - Column-based file browser (Finder-like)
// - Vertical scroll with ScrollOffset
// - Horizontal scroll (multiple columns)
// - Windows drive list / macOS root support
// - Symbolic link cycle prevention
// - Unicode width handling (East Asian)
// - Incremental rendering (diff-based)
// - Dynamic resize handling
//
// Operations:
// ↑↓ = scroll / select
// ← → = switch column
// Enter = open folder
// Backspace = parent directory
// 'r' = refresh
// Escape = quit

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Linq;

internal enum FileCategory { Text, Image, Video, Audio, Archive, Executable, Pdf, Drive, Binary }

internal record FileType(FileCategory Category, string Label, string MimeType = "");

internal static class FileTypeDetector
{
    private static readonly (int Offset, byte[] Sig, FileType Type)[] _sigs =
    [
        (0,  [0x89,0x50,0x4E,0x47,0x0D,0x0A,0x1A,0x0A], new(FileCategory.Image,      "PNG Image",         "image/png")),
        (0,  [0x66,0x4C,0x61,0x43],                      new(FileCategory.Audio,      "FLAC Audio",        "audio/flac")),
        (0,  [0x4F,0x67,0x67,0x53],                      new(FileCategory.Audio,      "OGG Audio",         "audio/ogg")),
        (0,  [0x1A,0x45,0xDF,0xA3],                      new(FileCategory.Video,      "Matroska Video",    "video/x-matroska")),
        (0,  [0x47,0x49,0x46,0x38],                      new(FileCategory.Image,      "GIF Image",         "image/gif")),
        (0,  [0x25,0x50,0x44,0x46],                      new(FileCategory.Pdf,        "PDF Document",      "application/pdf")),
        (0,  [0x50,0x4B,0x03,0x04],                      new(FileCategory.Archive,    "ZIP Archive",       "application/zip")),
        (0,  [0x50,0x4B,0x05,0x06],                      new(FileCategory.Archive,    "ZIP Archive",       "application/zip")),
        (0,  [0x37,0x7A,0xBC,0xAF,0x27,0x1C],            new(FileCategory.Archive,    "7-Zip Archive")),
        (0,  [0x52,0x61,0x72,0x21,0x1A,0x07],            new(FileCategory.Archive,    "RAR Archive")),
        (0,  [0x7F,0x45,0x4C,0x46],                      new(FileCategory.Executable, "ELF Executable")),
        // RIFF is handled by dedicated early-return before the loop
        (4,  [0x66,0x74,0x79,0x70],                      new(FileCategory.Video,      "MP4 Video",         "video/mp4")), // ftyp box
        (0,  [0x4D,0x5A],                                new(FileCategory.Executable, "PE Executable",     "application/x-msdownload")),
        (0,  [0x49,0x44,0x33],                           new(FileCategory.Audio,      "MP3 Audio",         "audio/mpeg")),
        (0,  [0x42,0x5A,0x68],                           new(FileCategory.Archive,    "BZip2 Archive")),
        (0,  [0x1F,0x8B],                                new(FileCategory.Archive,    "GZip Archive",      "application/gzip")),
        (0,  [0xFF,0xD8],                                new(FileCategory.Image,      "JPEG Image",        "image/jpeg")),
        (0,  [0x42,0x4D],                                new(FileCategory.Image,      "BMP Image",         "image/bmp")),
        (0,  [0xFF,0xFB],                                new(FileCategory.Audio,      "MP3 Audio",         "audio/mpeg")),
        (0,  [0xFF,0xF3],                                new(FileCategory.Audio,      "MP3 Audio",         "audio/mpeg")),
        (0,  [0xFF,0xF2],                                new(FileCategory.Audio,      "MP3 Audio",         "audio/mpeg")),
    ];

    private static readonly Dictionary<string, FileType> _extMap = new(StringComparer.OrdinalIgnoreCase)
    {
        [".txt"]  = new(FileCategory.Text, "Plain Text",       "text/plain"),
        [".md"]   = new(FileCategory.Text, "Markdown",         "text/markdown"),
        [".cs"]   = new(FileCategory.Text, "C# Source"),
        [".js"]   = new(FileCategory.Text, "JavaScript"),
        [".ts"]   = new(FileCategory.Text, "TypeScript"),
        [".py"]   = new(FileCategory.Text, "Python Source"),
        [".rb"]   = new(FileCategory.Text, "Ruby Source"),
        [".go"]   = new(FileCategory.Text, "Go Source"),
        [".rs"]   = new(FileCategory.Text, "Rust Source"),
        [".c"]    = new(FileCategory.Text, "C Source"),
        [".h"]    = new(FileCategory.Text, "C Header"),
        [".cpp"]  = new(FileCategory.Text, "C++ Source"),
        [".java"] = new(FileCategory.Text, "Java Source"),
        [".json"] = new(FileCategory.Text, "JSON"),
        [".xml"]  = new(FileCategory.Text, "XML"),
        [".html"] = new(FileCategory.Text, "HTML"),
        [".htm"]  = new(FileCategory.Text, "HTML"),
        [".css"]  = new(FileCategory.Text, "CSS"),
        [".sh"]   = new(FileCategory.Text, "Shell Script"),
        [".bash"] = new(FileCategory.Text, "Shell Script"),
        [".bat"]  = new(FileCategory.Text, "Batch Script"),
        [".cmd"]  = new(FileCategory.Text, "Batch Script"),
        [".ps1"]  = new(FileCategory.Text, "PowerShell Script"),
        [".yaml"] = new(FileCategory.Text, "YAML"),
        [".yml"]  = new(FileCategory.Text, "YAML"),
        [".toml"] = new(FileCategory.Text, "TOML"),
        [".ini"]  = new(FileCategory.Text, "INI Config"),
        [".cfg"]  = new(FileCategory.Text, "Config File"),
        [".conf"] = new(FileCategory.Text, "Config File"),
        [".log"]  = new(FileCategory.Text, "Log File"),
        [".csv"]  = new(FileCategory.Text, "CSV"),
        [".sql"]  = new(FileCategory.Text, "SQL Script"),
        [".svg"]  = new(FileCategory.Image, "SVG Image",       "image/svg+xml"),
        [".ico"]  = new(FileCategory.Image, "Icon",            "image/x-icon"),
        [".webp"] = new(FileCategory.Image, "WebP Image",      "image/webp"),
        [".mp4"]  = new(FileCategory.Video, "MP4 Video",       "video/mp4"),
        [".mov"]  = new(FileCategory.Video, "QuickTime Video", "video/quicktime"),
        [".avi"]  = new(FileCategory.Video, "AVI Video",       "video/x-msvideo"),
        [".wmv"]  = new(FileCategory.Video, "WMV Video",       "video/x-ms-wmv"),
        [".webm"] = new(FileCategory.Video, "WebM Video",      "video/webm"),
        [".mkv"]  = new(FileCategory.Video, "Matroska Video"),
        [".flv"]  = new(FileCategory.Video, "Flash Video"),
        [".wav"]  = new(FileCategory.Audio, "WAV Audio",       "audio/wav"),
        [".mp3"]  = new(FileCategory.Audio, "MP3 Audio",       "audio/mpeg"),
        [".flac"] = new(FileCategory.Audio, "FLAC Audio",      "audio/flac"),
        [".aac"]  = new(FileCategory.Audio, "AAC Audio"),
        [".m4a"]  = new(FileCategory.Audio, "M4A Audio"),
        [".ogg"]  = new(FileCategory.Audio, "OGG Audio"),
        [".opus"] = new(FileCategory.Audio, "Opus Audio"),
        [".zip"]  = new(FileCategory.Archive, "ZIP Archive",   "application/zip"),
        [".tar"]  = new(FileCategory.Archive, "TAR Archive"),
        [".gz"]   = new(FileCategory.Archive, "GZip Archive",  "application/gzip"),
        [".bz2"]  = new(FileCategory.Archive, "BZip2 Archive"),
        [".xz"]   = new(FileCategory.Archive, "XZ Archive"),
        [".7z"]   = new(FileCategory.Archive, "7-Zip Archive"),
        [".rar"]  = new(FileCategory.Archive, "RAR Archive"),
        [".lzh"]  = new(FileCategory.Archive, "LZH Archive"),
        [".lha"]  = new(FileCategory.Archive, "LZH Archive"),
        [".exe"]  = new(FileCategory.Executable, "Executable"),
        [".dll"]  = new(FileCategory.Executable, "DLL Library"),
        [".sys"]  = new(FileCategory.Executable, "System Driver"),
        [".pdf"]  = new(FileCategory.Pdf, "PDF Document"),
        [".ttf"]  = new(FileCategory.Binary, "TrueType Font"),
        [".otf"]  = new(FileCategory.Binary, "OpenType Font"),
        [".woff"] = new(FileCategory.Binary, "Web Font"),
        [".woff2"]= new(FileCategory.Binary, "Web Font"),
    };

    public static FileType Detect(string path, ReadOnlySpan<byte> magic)
    {
        // Disambiguate RIFF before general table scan
        if (magic.Length >= 4 && magic[0]==0x52&&magic[1]==0x49&&magic[2]==0x46&&magic[3]==0x46)
        {
            if (magic.Length < 12) return new(FileCategory.Binary, "RIFF Data");
            if (magic[8]==0x41&&magic[9]==0x56&&magic[10]==0x49&&magic[11]==0x20)
                return new(FileCategory.Video, "AVI Video",  "video/x-msvideo");
            if (magic[8]==0x57&&magic[9]==0x41&&magic[10]==0x56&&magic[11]==0x45)
                return new(FileCategory.Audio, "WAV Audio",  "audio/wav");
            if (magic[8]==0x57&&magic[9]==0x45&&magic[10]==0x42&&magic[11]==0x50)
                return new(FileCategory.Image, "WebP Image", "image/webp");
            return new(FileCategory.Binary, "RIFF Data");
        }

        foreach (var (offset, sig, type) in _sigs)
        {
            if (magic.Length < offset + sig.Length) continue;
            bool match = true;
            for (int i = 0; i < sig.Length && match; i++)
                if (magic[offset + i] != sig[i]) match = false;
            if (!match) continue;

            // Refine MP4 ftyp: check brand at bytes 8-11
            if (offset == 4 && sig[0] == 0x66 && magic.Length >= 12)
            {
                if (magic[8]==0x71&&magic[9]==0x74&&magic[10]==0x20&&magic[11]==0x20)
                    return new(FileCategory.Video, "QuickTime Video", "video/quicktime");
                if (magic[8]==0x4D&&magic[9]==0x34&&magic[10]==0x41&&magic[11]==0x20)
                    return new(FileCategory.Audio, "M4A Audio", "audio/mp4");
            }

            // Refine MZ: use extension for dll/sys
            if (sig[0] == 0x4D && sig[1] == 0x5A)
            {
                string e = System.IO.Path.GetExtension(path).ToLowerInvariant();
                if (e == ".dll") return new(FileCategory.Executable, "DLL Library",    "application/x-msdownload");
                if (e == ".sys") return new(FileCategory.Executable, "System Driver");
            }

            return type;
        }

        string ext = System.IO.Path.GetExtension(path);
        if (!string.IsNullOrEmpty(ext) && _extMap.TryGetValue(ext, out var extType))
            return extType;

        // Heuristic: check for null bytes — text files have none
        if (magic.Length > 0)
        {
            int check = Math.Min(magic.Length, 512);
            bool hasNull = false;
            for (int i = 0; i < check; i++) if (magic[i] == 0) { hasNull = true; break; }
            if (!hasNull) return new(FileCategory.Text, "Text File", "text/plain");
        }

        return new(FileCategory.Binary, "Binary Data", "application/octet-stream");
    }
}

sealed class Line
{
    private readonly List<string> _cols = new List<string>();
    private int _totalDisplayWidth;

    // Separator takes 1 display col; content slot shrinks for non-first columns
    public void AddColumn(string content, int displayWidth, int columnWidth)
    {
        bool isFirst = _cols.Count == 0;
        int contentSlot = columnWidth - (isFirst ? 0 : 1);
        int padding = contentSlot - displayWidth;
        if (padding > 0)
            content += new string(' ', padding);
        if (!isFirst)
            content = "|" + content;
        _cols.Add(content);
        _totalDisplayWidth += columnWidth;
    }

    public string Render(int targetWidth)
    {
        string result = string.Concat(_cols);
        // Pad using display width, not character count
        int padding = targetWidth - _totalDisplayWidth;
        if (padding > 0)
            result += new string(' ', padding);
        return result;
    }
}

static class AnsiColors
{
    public const string Blue   = "\x1b[94m";
    public const string Yellow = "\x1b[33m";
    public const string Gray   = "\x1b[90m";   // 減光: ディレクトリ・選択文字・マッチ文字用
    public const string Reset  = "\x1b[0m";
    public static string Colorize(string text, string color) => color + text + Reset;
}

static class NavigationHelper
{
    public static void PageDown(Column c, int visibleHeight)
    {
        if (c.Entries.Count == 0) return;
        c.Selected = Math.Min(c.Selected + visibleHeight, c.Entries.Count - 1);
    }

    public static void PageUp(Column c, int visibleHeight)
    {
        c.Selected = Math.Max(c.Selected - visibleHeight, 0);
    }

    public static void GoHome(Column c)
    {
        c.Selected = 0;
        c.ScrollOffset = 0;
    }

    public static void GoEnd(Column c)
    {
        c.Selected = Math.Max(0, c.Entries.Count - 1);
    }

    /// <summary>
    /// Cursor-memory fallback: entries is sorted as two blocks (directories then files).
    /// Returns the index of the nearest neighbor to a missingTarget name within its own
    /// block — the last entry sorting before it (previous), or the block's first entry
    /// if missingTarget was the block's smallest (next). Falls back to clamping
    /// currentSelected into range if the whole block is now empty.
    /// </summary>
    public static int FindNearestIndex(List<string> entries, int directoriesCount, string missingTarget, int currentSelected)
    {
        bool wasDir = missingTarget.EndsWith("/");
        int start = wasDir ? 0 : directoriesCount;
        int end = wasDir ? directoriesCount : entries.Count;

        if (start >= end)
            return entries.Count == 0 ? 0 : Math.Clamp(currentSelected, 0, entries.Count - 1);

        int nearest = start;
        for (int i = start; i < end; i++)
        {
            if (string.Compare(entries[i], missingTarget, StringComparison.OrdinalIgnoreCase) < 0)
                nearest = i;
            else
                break;
        }
        return nearest;
    }
}

static class MarkHelper
{
    // カーソル下エントリのマークをトグルし、カーソルを1つ下へ移動する。
    // ::DRIVES:: カラムでは呼び出し禁止(呼び出し側で判定する)。
    public static void ToggleMarkAndAdvance(Column column)
    {
        if (!ToggleMarkAtCursor(column)) return;
        if (column.Selected < column.Entries.Count - 1)
            column.Selected++;
    }

    // カーソル下エントリのマークをトグルし、カーソルを1つ上へ移動する(Space の逆方向)。
    // ::DRIVES:: カラムでは呼び出し禁止(呼び出し側で判定する)。
    public static void ToggleMarkAndRetreat(Column column)
    {
        if (!ToggleMarkAtCursor(column)) return;
        if (column.Selected > 0)
            column.Selected--;
    }

    // カーソル下エントリのマークをトグルする(カーソル移動なし)。トグルできた場合 true。
    static bool ToggleMarkAtCursor(Column column)
    {
        if (column.Entries.Count == 0) return false;
        if (column.Selected < 0 || column.Selected >= column.Entries.Count) return false;

        string name = column.Entries[column.Selected];
        if (name == "<No file>") return false; // 空ディレクトリのプレースホルダはマーク対象外

        if (column.Marked.Contains(name))
            column.Marked.Remove(name);
        else
            column.Marked.Add(name);
        return true;
    }

    // アクティブカラムの全マーク解除。
    public static void ClearAll(Column column)
    {
        column.Marked.Clear();
    }

    // 再読込後の Entries に存在しなくなった名前を Marked から除去する。
    // 名前ベース突合により、ソート順の変化や新規追加には影響されない。
    public static void ReconcileAfterReload(Column column)
    {
        if (column.Marked.Count == 0) return;
        var entrySet = new HashSet<string>(column.Entries, StringComparer.Ordinal);
        column.Marked.RemoveWhere(name => !entrySet.Contains(name));
    }
}

/// <summary>
/// Thread-safe "redraw requested" latch. Background completions call Request();
/// the main loop calls ShouldDraw() once per tick, consuming any pending request
/// atomically. A Request() arriving during BuildFrame survives to the next tick.
/// Convention: any new background mutation of render-visible state must call
/// Request() as its LAST step.
/// </summary>
sealed class RedrawGate
{
    int _requested = 1; // start dirty: first frame always draws

    public void Request() => Interlocked.Exchange(ref _requested, 1);

    public bool ShouldDraw(bool activityInProgress)
    {
        bool requested = Interlocked.Exchange(ref _requested, 0) == 1;
        return requested || activityInProgress;
    }
}

/// <summary>
/// Proper CJK character width handling (adapted from twf\Utilities\CharacterWidthHelper.cs)
/// </summary>
static class CharacterWidth
{
    public static int CJKCharacterWidth { get; set; } = 2;
    public static string DefaultEllipsis { get; set; } = "…";

    public static int GetCharWidth(char c)
    {
        if (CJKCharacterWidth == 0)
            return 1;
        if (IsZeroWidthCharacter(c))
            return 0;
        if (IsCJKCharacter(c))
            return CJKCharacterWidth;
        return 1;
    }

    private static bool IsCJKCharacter(char c)
    {
        int code = (int)c;
        if (code >= 0x4E00 && code <= 0x9FFF) return true;   // CJK Unified Ideographs
        if (code >= 0x3400 && code <= 0x4DBF) return true;   // CJK Extension A
        if (code >= 0x3040 && code <= 0x309F) return true;   // Hiragana
        if (code >= 0x30A0 && code <= 0x30FF) return true;   // Katakana
        if (code >= 0x31F0 && code <= 0x31FF) return true;   // Katakana Phonetic Extensions
        if (code >= 0xAC00 && code <= 0xD7AF) return true;   // Hangul Syllables
        if (code >= 0x1100 && code <= 0x11FF) return true;   // Hangul Jamo
        if (code >= 0xFF00 && code <= 0xFFEF) return true;   // Fullwidth Forms
        if (code >= 0xF900 && code <= 0xFAFF) return true;   // CJK Compatibility Ideographs
        if (code >= 0x2E80 && code <= 0x2EFF) return true;   // CJK Radicals Supplement
        if (code >= 0x3000 && code <= 0x303F) return true;   // CJK Symbols and Punctuation
        if (code >= 0x3100 && code <= 0x31EF) return true;   // Bopomofo, Hangul Compat Jamo, Kanbun, CJK Strokes
        if (code >= 0x3200 && code <= 0x32FF) return true;   // Enclosed CJK Letters and Months (㉒ etc.)
        if (code >= 0x3300 && code <= 0x33FF) return true;   // CJK Compatibility
        if (code >= 0xFE30 && code <= 0xFE4F) return true;   // CJK Compatibility Forms
        return false;
    }

    private static bool IsZeroWidthCharacter(char c)
    {
        int code = (int)c;
        if (code >= 0x0300 && code <= 0x036F) return true;   // Combining Diacritical Marks
        if (code >= 0x1AB0 && code <= 0x1AFF) return true;
        if (code >= 0x1DC0 && code <= 0x1DFF) return true;
        if (code >= 0xFE20 && code <= 0xFE2F) return true;
        if (code == 0x200B || code == 0x200C || code == 0x200D) return true;  // Zero-width joiners
        if (code >= 0xFE00 && code <= 0xFE0F) return true;   // Variation Selectors
        return false;
    }

    public static int GetStringWidth(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;
        if (CJKCharacterWidth == 0)
            return text.Length;
        int width = 0;
        foreach (char c in text)
            width += GetCharWidth(c);
        return width;
    }

    public static string SmartTruncate(string? text, int maxWidth, string? ellipsis = null)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        if (maxWidth < 0)
            throw new ArgumentException("Max width cannot be negative");

        ellipsis ??= DefaultEllipsis;
        int textWidth = GetStringWidth(text);
        if (textWidth <= maxWidth)
            return text;

        int ellipsisWidth = GetStringWidth(ellipsis);
        int availableWidth = maxWidth - ellipsisWidth;

        if (availableWidth < 2)
            return TruncateToWidth(text, maxWidth, ellipsis);

        // Keep 2/3 at start, 1/3 at end (to preserve extensions)
        int widthForEnd = availableWidth / 3;
        int widthForStart = availableWidth - widthForEnd;

        int currentWidth = 0;
        int startIndex = 0;
        for (int i = 0; i < text.Length; i++)
        {
            int len = GetCharWidth(text[i]);
            if (currentWidth + len > widthForStart) break;
            currentWidth += len;
            startIndex++;
        }

        currentWidth = 0;
        int endIndex = text.Length;
        for (int i = text.Length - 1; i >= 0; i--)
        {
            int len = GetCharWidth(text[i]);
            if (currentWidth + len > widthForEnd) break;
            currentWidth += len;
            endIndex--;
        }

        if (startIndex >= endIndex)
            return TruncateToWidth(text, maxWidth, ellipsis);

        return text.Substring(0, startIndex) + ellipsis + text.Substring(endIndex);
    }

    private static string TruncateToWidth(string? text, int maxWidth, string? ellipsis = null)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        ellipsis ??= DefaultEllipsis;
        int currentWidth = 0;
        int ellipsisWidth = GetStringWidth(ellipsis);
        int targetWidth = maxWidth - ellipsisWidth;

        if (targetWidth < 0)
        {
            targetWidth = maxWidth;
            ellipsis = string.Empty;
        }

        for (int i = 0; i < text.Length; i++)
        {
            int charWidth = GetCharWidth(text[i]);
            if (currentWidth + charWidth > targetWidth)
                return text.Substring(0, i) + ellipsis;
            currentWidth += charWidth;
        }

        return text;
    }

    public static string PadToWidth(string? text, int targetWidth, char padChar = ' ')
    {
        if (string.IsNullOrEmpty(text))
            return new string(padChar, targetWidth);

        int textWidth = GetStringWidth(text);
        if (textWidth >= targetWidth)
            return text;

        int spacesNeeded = targetWidth - textWidth;
        return text + new string(padChar, spacesNeeded);
    }
}

enum ConflictResolution { Overwrite, Skip, Rename }

sealed record ConflictDecision(ConflictResolution Resolution, string? NewName = null);

sealed record FileOpResult(int Succeeded, int Skipped, int Failed, string? FirstError, bool WasCancelled = false);

sealed record FileOpProgressInfo(int DoneCount, int TotalCount, long BytesTransferred, string CurrentFile);

interface IFileOpProgress
{
    void Report(FileOpProgressInfo info);
    void ReportItemResult(OpLogEntry entry);
}

static class FileOpEngine
{
    const int CopyBufferSize = 1024 * 1024; // 1MB
    const FileOptions CopyFileOptions = FileOptions.Asynchronous | FileOptions.SequentialScan;

    enum ItemOutcome { Succeeded, Skipped, Failed, Cancelled }

    public static Task<FileOpResult> CopyAsync(
        IReadOnlyList<string> sources, string destDir, IFileOpProgress progress,
        Func<string, Task<ConflictDecision?>> onConflict, CancellationToken ct)
        => RunBatchAsync(sources, destDir, progress, onConflict, ct, isMove: false);

    public static Task<FileOpResult> MoveAsync(
        IReadOnlyList<string> sources, string destDir, IFileOpProgress progress,
        Func<string, Task<ConflictDecision?>> onConflict, CancellationToken ct)
        => RunBatchAsync(sources, destDir, progress, onConflict, ct, isMove: true);

    static async Task<FileOpResult> RunBatchAsync(
        IReadOnlyList<string> sources, string destDir, IFileOpProgress progress,
        Func<string, Task<ConflictDecision?>> onConflict, CancellationToken ct, bool isMove)
    {
        int succeeded = 0, skipped = 0, failed = 0;
        string? firstError = null;
        long bytesTransferred = 0;
        int doneCount = 0;
        bool wasCancelled = false;

        int totalCount = 0;
        foreach (string src in sources)
            totalCount += CountUnits(src);

        foreach (string src in sources)
        {
            if (ct.IsCancellationRequested) { wasCancelled = true; break; }
            string name = Path.GetFileName(src.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            string dest = Path.Combine(destDir, name);

            bool isDir;
            try { isDir = Directory.Exists(src); }
            catch (Exception ex)
            {
                failed++;
                firstError ??= ex.Message;
                progress.ReportItemResult(new OpLogEntry(DateTime.Now, null, isMove ? "MOVE" : "COPY", "FAIL", src, dest, ex.Message));
                continue;
            }

            if (isDir)
            {
                var sub = await CopyOrMoveDirectoryAsync(src, dest, progress, onConflict, ct, isMove);
                succeeded += sub.Succeeded; skipped += sub.Skipped; failed += sub.Failed;
                firstError ??= sub.FirstError;
                bytesTransferred += sub.Bytes;
                doneCount += sub.Succeeded + sub.Skipped + sub.Failed;
                progress.Report(new FileOpProgressInfo(doneCount, totalCount, bytesTransferred, src));
                if (sub.WasCancelled) { wasCancelled = true; break; }
            }
            else
            {
                var (outcome, bytes, error) = await CopyOrMoveFileAsync(src, dest, progress, onConflict, ct, isMove);
                bytesTransferred += bytes;
                doneCount++;
                progress.Report(new FileOpProgressInfo(doneCount, totalCount, bytesTransferred, src));
                switch (outcome)
                {
                    case ItemOutcome.Succeeded: succeeded++; break;
                    case ItemOutcome.Skipped: skipped++; break;
                    case ItemOutcome.Failed: failed++; firstError ??= error; break;
                    case ItemOutcome.Cancelled: wasCancelled = true; break;
                }
                if (wasCancelled) break;
            }
        }

        return new FileOpResult(succeeded, skipped, failed, firstError, wasCancelled);
    }

    static int CountUnits(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                int count = 0;
                foreach (string _ in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    count++;
                return Math.Max(count, 1);
            }
        }
        catch { }
        return 1;
    }

    // Conflict check happens immediately before the copy/move attempt (not a pre-scan). A TOCTOU
    // race (external process creates the same-name file between the check and the actual write)
    // surfaces as an IOException from overwrite:false, which is folded into the same onConflict
    // callback below rather than handled by a dedicated exclusion mechanism.
    static async Task<(ItemOutcome, long, string? Error)> CopyOrMoveFileAsync(
        string src, string dest, IFileOpProgress progress,
        Func<string, Task<ConflictDecision?>> onConflict, CancellationToken ct, bool isMove)
    {
        string finalDest = dest;
        bool overwrite = false;

        if (File.Exists(finalDest) || Directory.Exists(finalDest))
        {
            var decision = await onConflict(Path.GetFileName(finalDest));
            if (decision == null)
                return (ItemOutcome.Cancelled, 0, null);

            if (decision.Resolution == ConflictResolution.Skip)
            {
                progress.ReportItemResult(new OpLogEntry(DateTime.Now, null, isMove ? "MOVE" : "COPY", "SKIP", src, finalDest, ""));
                return (ItemOutcome.Skipped, 0, null);
            }
            if (decision.Resolution == ConflictResolution.Rename)
                finalDest = Path.Combine(Path.GetDirectoryName(finalDest) ?? "", decision.NewName ?? Path.GetFileName(finalDest));
            else
                overwrite = true;
        }

        try
        {
            long? bytes = isMove
                ? await MoveWithConflictRetryAsync(src, finalDest, overwrite, onConflict, ct)
                : await CopyWithConflictRetryAsync(src, finalDest, overwrite, onConflict, ct);
            if (bytes == null)
            {
                progress.ReportItemResult(new OpLogEntry(DateTime.Now, null, isMove ? "MOVE" : "COPY", "SKIP", src, finalDest, ""));
                return (ItemOutcome.Skipped, 0, null);
            }
            progress.ReportItemResult(new OpLogEntry(DateTime.Now, null, isMove ? "MOVE" : "COPY", "OK", src, finalDest, ""));
            return (ItemOutcome.Succeeded, bytes.Value, null);
        }
        catch (OperationCanceledException)
        {
            TryDeletePartialFile(finalDest);
            progress.ReportItemResult(new OpLogEntry(DateTime.Now, null, isMove ? "MOVE" : "COPY", "CANCEL", src, finalDest, ""));
            return (ItemOutcome.Cancelled, 0, null);
        }
        catch (Exception ex)
        {
            TryDeletePartialFile(finalDest);
            progress.ReportItemResult(new OpLogEntry(DateTime.Now, null, isMove ? "MOVE" : "COPY", "FAIL", src, finalDest, ex.Message));
            return (ItemOutcome.Failed, 0, ex.Message);
        }
    }

    // Returns null when the nested conflict retry resolves to Skip (caller must report Skipped, not Succeeded).
    static async Task<long?> MoveWithConflictRetryAsync(
        string src, string dest, bool overwrite, Func<string, Task<ConflictDecision?>> onConflict, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            File.Move(src, dest, overwrite);
            return new FileInfo(dest) is { Exists: true } fi ? fi.Length : 0;
        }
        catch (IOException) when (!overwrite)
        {
            var decision = await onConflict(Path.GetFileName(dest));
            if (decision == null) throw new OperationCanceledException(ct);
            if (decision.Resolution == ConflictResolution.Skip) return null;

            string finalDest = decision.Resolution == ConflictResolution.Rename
                ? Path.Combine(Path.GetDirectoryName(dest) ?? "", decision.NewName ?? Path.GetFileName(dest))
                : dest;
            File.Move(src, finalDest, decision.Resolution == ConflictResolution.Overwrite);
            return new FileInfo(finalDest) is { Exists: true } fi2 ? fi2.Length : 0;
        }
    }

    // Returns null when the nested conflict retry resolves to Skip (caller must report Skipped, not Succeeded).
    static async Task<long?> CopyWithConflictRetryAsync(
        string src, string dest, bool overwrite, Func<string, Task<ConflictDecision?>> onConflict, CancellationToken ct)
    {
        FileMode mode = overwrite ? FileMode.Create : FileMode.CreateNew;
        try
        {
            return await StreamCopyAsync(src, dest, mode, ct);
        }
        catch (IOException) when (!overwrite)
        {
            var decision = await onConflict(Path.GetFileName(dest));
            if (decision == null) throw new OperationCanceledException(ct);
            if (decision.Resolution == ConflictResolution.Skip) return null;

            string finalDest = decision.Resolution == ConflictResolution.Rename
                ? Path.Combine(Path.GetDirectoryName(dest) ?? "", decision.NewName ?? Path.GetFileName(dest))
                : dest;
            FileMode retryMode = decision.Resolution == ConflictResolution.Overwrite ? FileMode.Create : FileMode.CreateNew;
            return await StreamCopyAsync(src, finalDest, retryMode, ct);
        }
    }

    static async Task<long> StreamCopyAsync(string src, string dest, FileMode mode, CancellationToken ct)
    {
        long total = 0;
        using var sourceStream = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, CopyFileOptions);
        using var destStream = new FileStream(dest, mode, FileAccess.Write, FileShare.None, CopyBufferSize, CopyFileOptions);
        byte[] buffer = new byte[CopyBufferSize];
        int bytesRead;
        while ((bytesRead = await sourceStream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
        {
            await destStream.WriteAsync(buffer, 0, bytesRead, ct);
            total += bytesRead;
        }
        return total;
    }

    static void TryDeletePartialFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }

    static async Task<(int Succeeded, int Skipped, int Failed, string? FirstError, long Bytes, bool WasCancelled)> CopyOrMoveDirectoryAsync(
        string srcDir, string destDir, IFileOpProgress progress,
        Func<string, Task<ConflictDecision?>> onConflict, CancellationToken ct, bool isMove)
    {
        int succeeded = 0, skipped = 0, failed = 0;
        string? firstError = null;
        long bytes = 0;

        try { Directory.CreateDirectory(destDir); }
        catch (Exception ex)
        {
            progress.ReportItemResult(new OpLogEntry(DateTime.Now, null, isMove ? "MOVE" : "COPY", "FAIL", srcDir, destDir, ex.Message));
            return (0, 0, 1, ex.Message, 0, false);
        }

        string[] entries;
        try { entries = Directory.GetFileSystemEntries(srcDir); }
        catch (Exception ex)
        {
            progress.ReportItemResult(new OpLogEntry(DateTime.Now, null, isMove ? "MOVE" : "COPY", "FAIL", srcDir, destDir, ex.Message));
            return (0, 0, 1, ex.Message, 0, false);
        }

        foreach (string entry in entries)
        {
            if (ct.IsCancellationRequested)
                return (succeeded, skipped, failed, firstError, bytes, true);

            string entryDest = Path.Combine(destDir, Path.GetFileName(entry));

            if (Directory.Exists(entry))
            {
                var sub = await CopyOrMoveDirectoryAsync(entry, entryDest, progress, onConflict, ct, isMove);
                succeeded += sub.Succeeded; skipped += sub.Skipped; failed += sub.Failed;
                firstError ??= sub.FirstError;
                bytes += sub.Bytes;
                if (sub.WasCancelled)
                    return (succeeded, skipped, failed, firstError, bytes, true);
            }
            else
            {
                var (outcome, fileBytes, error) = await CopyOrMoveFileAsync(entry, entryDest, progress, onConflict, ct, isMove);
                bytes += fileBytes;
                switch (outcome)
                {
                    case ItemOutcome.Succeeded: succeeded++; break;
                    case ItemOutcome.Skipped: skipped++; break;
                    case ItemOutcome.Failed: failed++; firstError ??= error; break;
                    case ItemOutcome.Cancelled:
                        return (succeeded, skipped, failed, firstError, bytes, true);
                }
            }
        }

        if (isMove)
        {
            try { if (!Directory.EnumerateFileSystemEntries(srcDir).Any()) Directory.Delete(srcDir); }
            catch { }
        }

        return (succeeded, skipped, failed, firstError, bytes, false);
    }
}

enum JobStatus { Pending, Running, Completed, Failed, Cancelled }

sealed class BackgroundJob
{
    public int ShortId;
    public string OpName = ""; // "Copy" / "Move"
    public JobStatus Status;
    public int DoneCount;
    public int TotalCount;
    public long BytesTransferred;
    public string CurrentFile = "";
    public CancellationTokenSource Cts = new();
    public List<string> SourcePaths = new();
    public string DestDir = "";
    public FileOpResult? Result;
    public DateTime StartTime;
    public DateTime? EndTime;
}

static class JobManager
{
    const int MaxConcurrentJobs = 4;
    const int MaxCompletedHistory = 20;

    public static readonly object ProgressLock = new object();

    static readonly object _lock = new object();
    static readonly List<BackgroundJob> _jobs = new();
    static readonly SemaphoreSlim _semaphore = new(MaxConcurrentJobs);
    static int _nextShortId = 1;

    public static BackgroundJob Start(string opName, IReadOnlyList<string> sources, string destDir,
        Func<BackgroundJob, CancellationToken, Task<FileOpResult>> work)
    {
        var job = new BackgroundJob
        {
            OpName = opName,
            Status = JobStatus.Pending,
            SourcePaths = new List<string>(sources),
            DestDir = destDir,
            StartTime = DateTime.Now,
        };

        lock (_lock)
        {
            job.ShortId = _nextShortId++;
            _jobs.Add(job);
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await _semaphore.WaitAsync(job.Cts.Token);
                try
                {
                    lock (_lock) { job.Status = JobStatus.Running; }
                    FileOpResult result = await work(job, job.Cts.Token);
                    lock (_lock)
                    {
                        job.Result = result;
                        job.Status = result.WasCancelled ? JobStatus.Cancelled : JobStatus.Completed;
                        job.EndTime = DateTime.Now;
                    }
                }
                finally
                {
                    _semaphore.Release();
                }
            }
            catch (OperationCanceledException)
            {
                lock (_lock) { job.Status = JobStatus.Cancelled; job.EndTime = DateTime.Now; }
            }
            catch (Exception)
            {
                lock (_lock) { job.Status = JobStatus.Failed; job.EndTime = DateTime.Now; }
            }
            finally
            {
                TrimCompletedHistory();
            }
        });

        return job;
    }

    public static List<BackgroundJob> Snapshot()
    {
        lock (_lock) { return new List<BackgroundJob>(_jobs); }
    }

    public static void Cancel(int shortId)
    {
        BackgroundJob? job;
        lock (_lock) { job = _jobs.Find(j => j.ShortId == shortId); }
        job?.Cts.Cancel();
    }

    public static List<string> GetBusyPaths()
    {
        lock (_lock)
        {
            var paths = new List<string>();
            foreach (var job in _jobs)
            {
                if (job.Status != JobStatus.Pending && job.Status != JobStatus.Running) continue;
                paths.AddRange(job.SourcePaths);
                paths.Add(job.DestDir);
            }
            return paths;
        }
    }

    public static bool HasActiveJobs
    {
        get { lock (_lock) { return _jobs.Exists(j => j.Status == JobStatus.Pending || j.Status == JobStatus.Running); } }
    }

    public static async Task CancelAllAndWaitAsync(TimeSpan timeout)
    {
        List<BackgroundJob> active;
        lock (_lock) { active = _jobs.FindAll(j => j.Status == JobStatus.Pending || j.Status == JobStatus.Running); }
        foreach (var job in active) job.Cts.Cancel();

        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            bool anyActive;
            lock (_lock) { anyActive = _jobs.Exists(j => j.Status == JobStatus.Pending || j.Status == JobStatus.Running); }
            if (!anyActive) return;
            await Task.Delay(50);
        }
    }

    static void TrimCompletedHistory()
    {
        lock (_lock)
        {
            var completed = _jobs.FindAll(j => j.Status == JobStatus.Completed || j.Status == JobStatus.Failed || j.Status == JobStatus.Cancelled);
            if (completed.Count <= MaxCompletedHistory) return;

            completed.Sort((a, b) => (a.EndTime ?? DateTime.MinValue).CompareTo(b.EndTime ?? DateTime.MinValue));
            int toRemove = completed.Count - MaxCompletedHistory;
            for (int i = 0; i < toRemove; i++)
                _jobs.Remove(completed[i]);
        }
    }
}

static class PathOverlapHelper
{
    // a と b が同一パス、または一方が他方のディレクトリ配下であれば true。
    // 前方一致は「セパレータ境界」でのみ成立させる(C:\a と C:\ab を誤って重複と判定しない)。
    public static bool PathsOverlap(string a, string b)
    {
        string na = NormalizeForOverlap(a);
        string nb = NormalizeForOverlap(b);

        if (string.Equals(na, nb, StringComparison.OrdinalIgnoreCase))
            return true;

        string naWithSep = na.EndsWith("\\", StringComparison.Ordinal) ? na : na + "\\";
        string nbWithSep = nb.EndsWith("\\", StringComparison.Ordinal) ? nb : nb + "\\";

        if (nb.StartsWith(naWithSep, StringComparison.OrdinalIgnoreCase))
            return true;
        if (na.StartsWith(nbWithSep, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static string NormalizeForOverlap(string path)
    {
        string full = System.IO.Path.GetFullPath(path);
        return full.TrimEnd('\\', '/');
    }
}

static class BusyWarningHelper
{
    // targetPaths のいずれかが実行中ジョブの busy パスと重なれば true(= 警告が必要)。
    public static bool AnyOverlapsBusyPaths(IReadOnlyList<string> targetPaths)
    {
        List<string> busyPaths = JobManager.GetBusyPaths();
        if (busyPaths.Count == 0) return false;

        foreach (string target in targetPaths)
        {
            foreach (string busy in busyPaths)
            {
                if (PathOverlapHelper.PathsOverlap(target, busy))
                    return true;
            }
        }
        return false;
    }
}

sealed class Column
{
    public string Path;
    public List<string> Entries;
    public int Selected;
    public int ScrollOffset;
    public string? RestoreTo;
    public Dictionary<string, List<string>> CachedChildren;
    public DateTime CachedTime;
    public HashSet<string> Marked;

    // Loading state for async operations
    public bool IsLoading { get; set; }
    public int EntriesRead { get; set; }
    public int DirectoriesCount { get; set; }
    public CancellationTokenSource? ReadCts { get; set; }
    public Task? LoadingTask { get; set; }
    public bool NeedsMarkReconcile;

    public Column(string path)
    {
        Path = path;
        Entries = new List<string>();
        Selected = 0;
        ScrollOffset = 0;
        CachedChildren = new Dictionary<string, List<string>>();
        CachedTime = DateTime.UtcNow;
        Marked = new HashSet<string>(StringComparer.Ordinal);
        IsLoading = false;
        EntriesRead = 0;
        DirectoriesCount = 0;
    }
}

sealed class Pane
{
    public List<Column> Columns = new();
    public int ActiveColumn;
    public int HorizontalScroll;
    public SearchState Search = new();
    public LeapState Leap = new();
    public PreviewPane Preview = new();
    public bool NeedsRefresh; // ジョブ完了時の遅延再読込用 dirty フラグ(Phase 7 で使用)
    public bool NavRebuildPending; // cursor moved but right-side rebuild deferred to idle tick (Phase 9)
    public DateTime LastNavigationTime = DateTime.MinValue;
}

sealed class ScreenState
{
    public string[]? PrevFrame;
    public int PrevWidth;
    public int PrevHeight;
    public List<Pane> Panes = new();   // 常に1〜2要素
    public int FocusedPane;            // 0 = 上(または単一), 1 = 下
    public bool SplitActive;
    public DialogState? Dialog;        // Phase 3 で追加。今は宣言のみ。
}

sealed record OpLogEntry(DateTime Timestamp, int? JobId, string Op, string Result, string Source, string Dest, string Message);

static class OperationLog
{
    const int RingLimit = 1000;
    const long MaxLogSizeBytes = 10L * 1024 * 1024;

    static readonly object _lock = new object();
    static readonly LinkedList<OpLogEntry> _ring = new LinkedList<OpLogEntry>(); // newest at First

    // Overridable only from tests (InternalsVisibleTo covers Zurari.Tests).
    static string? _logDirectoryOverride;

    static string LogDirectory
    {
        get
        {
            if (_logDirectoryOverride != null) return _logDirectoryOverride;
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "zurari");
        }
    }

    static string LogFilePath => Path.Combine(LogDirectory, "operations.log");
    static string RotatedFilePath => Path.Combine(LogDirectory, "operations.log.1");

    internal static void ResetForTest(string logDirectory)
    {
        lock (_lock)
        {
            _logDirectoryOverride = logDirectory;
            _ring.Clear();
        }
    }

    public static void Append(OpLogEntry e)
    {
        lock (_lock)
        {
            _ring.AddFirst(e);
            while (_ring.Count > RingLimit)
                _ring.RemoveLast();

            TryAppendToFile(e);
        }
    }

    static void TryAppendToFile(OpLogEntry e)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);

            RotateIfNeeded();

            string line = string.Join('\t',
                e.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                e.JobId?.ToString() ?? "-",
                e.Op,
                e.Result,
                e.Source,
                e.Dest,
                e.Message)
                .Replace('\n', ' ')
                .Replace('\r', ' ');

            using var stream = new FileStream(LogFilePath, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, Encoding.UTF8);
            writer.WriteLine(line);
        }
        catch
        {
            // Logging must never crash the app. Silently give up on the file sink;
            // the in-memory ring already has the entry.
        }
    }

    static void RotateIfNeeded()
    {
        try
        {
            var info = new FileInfo(LogFilePath);
            if (!info.Exists || info.Length < MaxLogSizeBytes) return;

            if (File.Exists(RotatedFilePath))
                File.Delete(RotatedFilePath);
            File.Move(LogFilePath, RotatedFilePath);
        }
        catch
        {
            // Best-effort rotation only.
        }
    }

    public static IReadOnlyList<OpLogEntry> Snapshot()
    {
        lock (_lock)
        {
            return new List<OpLogEntry>(_ring); // already newest-first due to AddFirst
        }
    }
}

static class RecycleBin
{
    const uint FO_DELETE = 0x0003;
    const ushort FOF_ALLOWUNDO = 0x0040;
    const ushort FOF_NOCONFIRMATION = 0x0010;
    const ushort FOF_SILENT = 0x0004;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    struct SHFILEOPSTRUCTW
    {
        public IntPtr hwnd;
        public uint wFunc;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)]
        public string pFrom;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)]
        public string? pTo;
        public ushort fFlags;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)]
        public string? lpszProgressTitle;
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern int SHFileOperationW(ref SHFILEOPSTRUCTW lpFileOp);

    // SHFileOperationW requires pFrom to be a list of paths terminated by TWO null characters
    // (a single trailing '\0' only terminates the last path in the list; Windows scans for the
    // *second* consecutive null to know the whole multi-string buffer has ended). Forgetting the
    // second '\0' causes SHFileOperationW to read past the buffer or silently fail.
    public static bool TrySend(string path, out string error)
    {
        error = "";

        if (!System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
            System.Runtime.InteropServices.OSPlatform.Windows))
        {
            error = "not supported on this platform";
            return false;
        }

        try
        {
            var fileOp = new SHFILEOPSTRUCTW
            {
                hwnd = IntPtr.Zero,
                wFunc = FO_DELETE,
                pFrom = path + "\0\0", // double-null terminated, single-entry list
                pTo = null,
                fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT),
                fAnyOperationsAborted = false,
                hNameMappings = IntPtr.Zero,
                lpszProgressTitle = null,
            };

            int result = SHFileOperationW(ref fileOp);
            if (result != 0)
            {
                error = $"SHFileOperationW failed (code {result})";
                return false;
            }
            if (fileOp.fAnyOperationsAborted)
            {
                error = "operation aborted";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}

static class Bookmarks
{
    static string? _pathOverride;

    internal static void ResetForTest(string filePath)
    {
        _pathOverride = filePath;
    }

    static string FilePath
    {
        get
        {
            if (_pathOverride != null) return _pathOverride;
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "zurari");
            return Path.Combine(dir, "bookmarks.txt");
        }
    }

    public static List<string> Load()
    {
        var result = new List<string>();
        try
        {
            if (!File.Exists(FilePath)) return result;
            foreach (string line in File.ReadAllLines(FilePath, Encoding.UTF8))
            {
                string trimmed = line.Trim();
                if (trimmed.Length > 0)
                    result.Add(trimmed);
            }
        }
        catch
        {
            // Ignore unreadable bookmarks file; treat as empty.
        }
        return result;
    }

    public static bool Add(string path)
    {
        var existing = Load();
        foreach (string p in existing)
        {
            if (string.Equals(p, path, StringComparison.OrdinalIgnoreCase))
                return false; // already present
        }

        try
        {
            string dir = Path.GetDirectoryName(FilePath) ?? ".";
            Directory.CreateDirectory(dir);
            existing.Add(path);
            File.WriteAllLines(FilePath, existing, Encoding.UTF8);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

abstract class DialogState
{
    public string Title = "";
}

sealed class ConfirmDialogState : DialogState
{
    public string Message = "";
    public Action<bool?> OnResult = _ => { };   // true=Yes, false=No, null=Esc
}

sealed class TextInputDialogState : DialogState
{
    public const string DefaultHintText = "[Enter]Confirm  [Esc]Cancel";

    public string Buffer = "";
    public int CursorPos;
    public Action<string?> OnResult = _ => { }; // null=キャンセル
    public Func<char, bool>? CharFilter;         // null = accept all (Rename's existing behavior)
    public string HintText = DefaultHintText;
}

sealed class ListSelectDialogState : DialogState
{
    public List<string> AllItems = new();
    public List<string> FilteredItems = new();
    public string FilterQuery = "";
    public int Selected;
    public Action<string?> OnResult = _ => { }; // 確定項目の文字列 or null
}

sealed class JobListDialogState : DialogState
{
    public List<BackgroundJob> Snapshot = new();
    public int Selected;
}

static class JobListFormatter
{
    // 例: "#3 コピー Running 45% (12/40) src -> dst"
    public static string FormatRow(BackgroundJob job, int width)
    {
        string statusText = job.Status switch
        {
            JobStatus.Pending => "Pending",
            JobStatus.Running => "Running",
            JobStatus.Completed => "Completed",
            JobStatus.Failed => "Failed",
            JobStatus.Cancelled => "Cancelled",
            _ => job.Status.ToString(),
        };

        // Pending/Running は「処理中」の生カウンタ(doneCount は成功/失敗を問わず処理済み件数)を
        // そのまま表示してよいが、終了後は誤解を招く(キャンセル時の最後の1件は未完了でも
        // カウントされる)ため、確定した内訳(FileOpResult)がある場合はそちらを使う。
        string progressLabel;
        if (job.Status == JobStatus.Pending || job.Status == JobStatus.Running)
        {
            int doneCount, totalCount;
            lock (JobManager.ProgressLock) { doneCount = job.DoneCount; totalCount = job.TotalCount; }
            int percent = totalCount > 0 ? (int)(doneCount * 100L / totalCount) : 0;
            progressLabel = $"{percent}% ({doneCount}/{totalCount})";
        }
        else if (job.Result is { } result)
        {
            var parts = new List<string> { $"{result.Succeeded} succeeded" };
            if (result.Skipped > 0) parts.Add($"{result.Skipped} skipped");
            if (result.Failed > 0) parts.Add($"{result.Failed} failed");
            progressLabel = string.Join("/", parts);
        }
        else
        {
            progressLabel = "not started";
        }

        // 複数選択時も送り元が識別できるよう、件数だけでなく元フォルダ名も添える
        // (GetOperationTargets は単一カラム内のマークから対象を組み立てるため、
        // SourcePaths は常に同じ親ディレクトリを共有する)。
        string srcLabel;
        if (job.SourcePaths.Count == 1)
        {
            srcLabel = System.IO.Path.GetFileName(job.SourcePaths[0].TrimEnd('\\', '/'));
        }
        else
        {
            string? parentDir = System.IO.Path.GetDirectoryName(job.SourcePaths[0].TrimEnd('\\', '/'));
            string parentName = string.IsNullOrEmpty(parentDir) ? "" : System.IO.Path.GetFileName(parentDir.TrimEnd('\\', '/'));
            srcLabel = string.IsNullOrEmpty(parentName)
                ? $"{job.SourcePaths.Count} items"
                : $"{parentName} ({job.SourcePaths.Count} items)";
        }
        string dstLabel = System.IO.Path.GetFileName(job.DestDir.TrimEnd('\\', '/'));

        string row = $"#{job.ShortId} {job.OpName} {statusText} {progressLabel} {srcLabel} -> {dstLabel}";
        return CharacterWidth.SmartTruncate(row, width);
    }
}

static class OperationLogFormatter
{
    // 例: "2026-07-06 14:32:10 [#3] COPY OK C:\src\a.txt -> C:\dst\a.txt"
    public static string FormatEntry(OpLogEntry entry)
    {
        string jobLabel = entry.JobId.HasValue ? $"[#{entry.JobId.Value}]" : "[-]";
        string line = $"{entry.Timestamp:yyyy-MM-dd HH:mm:ss} {jobLabel} {entry.Op} {entry.Result} {entry.Source} -> {entry.Dest}";
        if (!string.IsNullOrEmpty(entry.Message))
        {
            line += $" {entry.Message}";
        }
        return line;
    }
}

readonly record struct DialogRect(int X, int Y, int Width, int Height);

static class DialogLayout
{
    const int MarginEachSide = 1; // 画面端からの最小余白(片側)
    const int MinContentWidth = 2;
    const int MinContentHeight = 1;

    public static DialogRect CalcDialogRect(int contentWidth, int contentHeight, int screenW, int screenH)
    {
        int cw = Math.Max(contentWidth, MinContentWidth);
        int ch = Math.Max(contentHeight, MinContentHeight);

        int outerWidth = cw + 2;   // 左右の枠線1文字ずつ
        int outerHeight = ch + 2;  // 上下の枠線1文字ずつ

        int maxWidth = Math.Max(4, screenW - MarginEachSide * 2);
        int maxHeight = Math.Max(3, screenH - MarginEachSide * 2);

        outerWidth = Math.Min(outerWidth, maxWidth);
        outerHeight = Math.Min(outerHeight, maxHeight);

        int x = Math.Max(0, (screenW - outerWidth) / 2);
        int y = Math.Max(0, (screenH - outerHeight) / 2);

        // 画面外にはみ出さないようクランプ(screenW/screenH が極小な場合の保険)
        if (x + outerWidth > screenW) x = Math.Max(0, screenW - outerWidth);
        if (y + outerHeight > screenH) y = Math.Max(0, screenH - outerHeight);

        return new DialogRect(x, y, outerWidth, outerHeight);
    }
}

static class TextInputBuffer
{
    public static (string Buffer, int CursorPos) Insert(string buffer, int cursorPos, char ch)
    {
        int pos = Math.Clamp(cursorPos, 0, buffer.Length);
        string newBuffer = buffer.Insert(pos, ch.ToString());
        return (newBuffer, pos + 1);
    }

    public static (string Buffer, int CursorPos) InsertString(string buffer, int cursorPos, string text)
    {
        int pos = Math.Clamp(cursorPos, 0, buffer.Length);
        string newBuffer = buffer.Insert(pos, text);
        return (newBuffer, pos + text.Length);
    }

    public static (string Buffer, int CursorPos) Backspace(string buffer, int cursorPos)
    {
        int pos = Math.Clamp(cursorPos, 0, buffer.Length);
        if (pos == 0) return (buffer, pos);
        string newBuffer = buffer.Remove(pos - 1, 1);
        return (newBuffer, pos - 1);
    }

    public static (string Buffer, int CursorPos) Delete(string buffer, int cursorPos)
    {
        int pos = Math.Clamp(cursorPos, 0, buffer.Length);
        if (pos >= buffer.Length) return (buffer, pos);
        string newBuffer = buffer.Remove(pos, 1);
        return (newBuffer, pos);
    }

    public static int MoveLeft(string buffer, int cursorPos)
    {
        return Math.Max(0, cursorPos - 1);
    }

    public static int MoveRight(string buffer, int cursorPos)
    {
        return Math.Min(buffer.Length, cursorPos + 1);
    }

    public static int Home(string buffer, int cursorPos)
    {
        return 0;
    }

    public static int End(string buffer, int cursorPos)
    {
        return buffer.Length;
    }

    // カーソルより手前の文字列を CharacterWidth で表示幅換算し、画面上の列位置を返す。
    public static int DisplayColumnAtCursor(string buffer, int cursorPos)
    {
        int pos = Math.Clamp(cursorPos, 0, buffer.Length);
        return CharacterWidth.GetStringWidth(buffer.Substring(0, pos));
    }

    // Returns the [Start, End) character-index range of `buffer` that fits within `maxWidth`
    // display columns while always including `cursorPos`. Used to keep the cursor visible in a
    // width-capped dialog field instead of letting it scroll off-screen (see spec: "Cursor
    // visibility & horizontal scrolling").
    public static (int Start, int End) ComputeScrollWindow(string buffer, int cursorPos, int maxWidth)
    {
        int pos = Math.Clamp(cursorPos, 0, buffer.Length);
        int totalWidth = CharacterWidth.GetStringWidth(buffer);
        if (totalWidth <= maxWidth)
            return (0, buffer.Length);

        int cursorCol = CharacterWidth.GetStringWidth(buffer.Substring(0, pos));

        int start;
        if (cursorCol <= maxWidth)
        {
            start = 0;
        }
        else
        {
            // Right-align: walk backward from the cursor until one more character would
            // overflow maxWidth.
            start = pos;
            int width = 0;
            while (start > 0)
            {
                int w = CharacterWidth.GetStringWidth(buffer[start - 1].ToString());
                if (width + w > maxWidth) break;
                width += w;
                start--;
            }
        }

        int startCol = CharacterWidth.GetStringWidth(buffer.Substring(0, start));
        int end = start;
        int endCol = startCol;
        while (end < buffer.Length)
        {
            int w = CharacterWidth.GetStringWidth(buffer[end].ToString());
            if (endCol - startCol + w > maxWidth) break;
            endCol += w;
            end++;
        }

        return (start, end);
    }
}

static class ListFilter
{
    public static List<string> Recompute(List<string> allItems, string query, bool regexMode, MigemoProvider? migemo)
    {
        if (string.IsNullOrEmpty(query))
            return new List<string>(allItems);

        var result = new List<string>();
        foreach (string item in allItems)
        {
            if (SearchHelper.MatchesSearchQuery(item, query, regexMode, migemo))
                result.Add(item);
        }
        return result;
    }

    public static int ClampSelected(int selected, int filteredCount)
    {
        if (filteredCount <= 0) return 0;
        return Math.Clamp(selected, 0, filteredCount - 1);
    }
}

sealed class SearchState
{
    public bool Active;
    public string Query = "";
    public int Anchor;           // column.Selected when search started; restored on Esc
    public List<int> Matches = new();
    public int MatchIndex;       // index into Matches of current highlighted match
    public bool RegexMode;       // false = literal/migemo, true = raw regex
    public CancellationTokenSource? SearchCts;
    public bool NeedsRecompute;  // set by keystroke, consumed by debounce tick
    public DateTime LastInputTime;
    public bool SearchDone;      // false while async scan in progress
}

sealed class LeapState
{
    public bool Active;
    public string Buffer = "";
    public string RootDir = "";
    public int RootColumnIndex;
    public int RootCursorIndex;
    public List<(string Dir, int BufferLen)> DirStack = new();
    public bool NeedsRecompute;
    public DateTime LastInputTime;
    public bool NoMatch;             // Mode B: true when filter produced 0 results
    public List<int> VisibleIndices = new();  // absolute entry indices passing the filter
    public int ColumnScrollOffset;   // scroll offset within VisibleIndices
    public bool PreviewWasVisible;   // Preview.IsVisible before F3; restored on exit
}

sealed class MigemoProvider : IDisposable
{
    [System.Runtime.InteropServices.DllImport("migemo",
        CharSet = System.Runtime.InteropServices.CharSet.Ansi,
        CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
    static extern IntPtr migemo_open(string dict_path);

    [System.Runtime.InteropServices.DllImport("migemo",
        CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
    static extern void migemo_close(IntPtr h);

    [System.Runtime.InteropServices.DllImport("migemo",
        CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
    static extern IntPtr migemo_query(IntPtr h, byte[] query);

    [System.Runtime.InteropServices.DllImport("migemo",
        CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
    static extern void migemo_release(IntPtr h, IntPtr result);

    IntPtr _handle = IntPtr.Zero;
    bool _disposed;

    public bool DllLoaded { get; private set; }   // true if migemo.dll was found
    public bool IsAvailable { get; private set; } // true if DLL + dict both loaded

    public MigemoProvider()
    {
        // Probe DLL availability before touching the dict
        if (!System.Runtime.InteropServices.NativeLibrary.TryLoad("migemo", out _))
            return; // DLL not present — silent
        DllLoaded = true;

        string? dictFile = FindDictFile();
        if (dictFile == null) return; // DLL present but no dict found

        try
        {
            _handle = migemo_open(dictFile);
            IsAvailable = _handle != IntPtr.Zero;
        }
        catch
        {
            // migemo_open failed — silent
        }
    }

    static string? FindDictFile()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "dict", "utf-8", "migemo-dict"),
            Path.Combine(AppContext.BaseDirectory, "dict", "migemo-dict"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ColumnFileManager", "dict", "utf-8", "migemo-dict"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ColumnFileManager", "dict", "migemo-dict"),
            "/usr/share/cmigemo/utf-8/migemo-dict",
            "/usr/local/share/migemo/utf-8/migemo-dict",
            "/opt/homebrew/share/migemo/utf-8/migemo-dict",
        };
        foreach (var path in candidates)
            if (File.Exists(path)) return path;
        return null;
    }

    public string ExpandPattern(string romaji)
    {
        if (!IsAvailable || string.IsNullOrEmpty(romaji)) return romaji;
        try
        {
            byte[] queryBytes = System.Text.Encoding.UTF8.GetBytes(romaji + '\0');
            IntPtr ptr = migemo_query(_handle, queryBytes);
            if (ptr == IntPtr.Zero) return romaji;
            try
            {
                string? result = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(ptr);
                return result ?? romaji;
            }
            finally
            {
                migemo_release(_handle, ptr);
            }
        }
        catch { return romaji; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_handle != IntPtr.Zero)
        {
            try { migemo_close(_handle); } catch { }
            _handle = IntPtr.Zero;
        }
    }
}

static class SearchHelper
{
    public static bool MatchesSearchQuery(string entry, string query, bool regexMode, MigemoProvider? migemo)
    {
        if (string.IsNullOrEmpty(query)) return false;

        if (regexMode)
        {
            try { return System.Text.RegularExpressions.Regex.IsMatch(entry, query, System.Text.RegularExpressions.RegexOptions.IgnoreCase); }
            catch (System.Text.RegularExpressions.RegexParseException) { return false; }
        }

        if (migemo?.IsAvailable == true)
        {
            string pattern = migemo.ExpandPattern(query);
            try { return System.Text.RegularExpressions.Regex.IsMatch(entry, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase); }
            catch { return entry.Contains(query, StringComparison.OrdinalIgnoreCase); }
        }

        return entry.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    public static int FindNearestMatchIndex(List<int> matches, int anchor)
    {
        for (int i = 0; i < matches.Count; i++)
            if (matches[i] >= anchor) return i;
        return 0;
    }

    public static string BuildSearchStatusBar(SearchState search, List<int> matches, bool done, int width)
    {
        bool noMatch = matches.Count == 0 && search.Query.Length > 0 && done;

        // Left: /query▌
        string promptColor = noMatch ? "\x1b[31m" : "\x1b[33m";
        string reset = "\x1b[0m";
        string left = promptColor + "/" + reset + search.Query + "\x1b[7m \x1b[0m";
        int leftLen = 1 + search.Query.Length + 1; // '/' + query + cursor

        // Right: [regex]  (n/m*)
        string regexTag = search.RegexMode ? "\x1b[33m[regex]\x1b[0m " : "";
        int regexTagLen = search.RegexMode ? 8 : 0; // "[regex] " = 8 chars

        string countPart;
        int countLen;
        if (search.Query.Length == 0)
        {
            countPart = "";
            countLen = 0;
        }
        else if (noMatch)
        {
            countPart = "\x1b[31m(0)\x1b[0m";
            countLen = 3;
        }
        else
        {
            int pos = matches.Count > 0 ? search.MatchIndex + 1 : 0;
            int total = matches.Count;
            string starPart = done ? "" : "\x1b[33m*\x1b[0m";
            int starLen = done ? 0 : 1;
            string inner = $"{pos}/{total}";
            countPart = $"\x1b[90m({reset}{inner}{reset}{starPart}\x1b[90m)\x1b[0m";
            countLen = 1 + inner.Length + starLen + 1; // ( inner * )
        }

        string right = regexTag + countPart;
        int rightLen = regexTagLen + countLen;

        int spaces = Math.Max(1, width - leftLen - rightLen);
        return left + new string(' ', spaces) + right;
    }

    public static string BuildLeapStatusBar(LeapState leap, List<Column> columns, int activeColumn, int width)
    {
        // Parse buffer into trail and local filter
        int lastSlash = leap.Buffer.LastIndexOf('/');
        string trail = lastSlash >= 0 ? leap.Buffer[..(lastSlash + 1)] : "";
        string localFilter = lastSlash >= 0 ? leap.Buffer[(lastSlash + 1)..] : leap.Buffer;

        // Right anchor text (plain, for width measurement)
        string rightPlain;
        bool rightIsNoMatch = false;
        if (leap.NoMatch)
        {
            rightPlain = "(no match)";
            rightIsNoMatch = true;
        }
        else if (leap.VisibleIndices.Count == 0)
        {
            rightPlain = "";
        }
        else if (leap.VisibleIndices.Count == 1)
        {
            Column col = activeColumn < columns.Count ? columns[activeColumn] : null!;
            bool isDir = col != null && col.Entries.Count > 0
                         && col.Selected >= 0 && col.Selected < col.Entries.Count
                         && col.Entries[col.Selected].EndsWith("/");
            rightPlain = isDir ? "1 match · dir" : "1 match · file";
        }
        else
        {
            rightPlain = $"{leap.VisibleIndices.Count} matches";
        }

        int rightDisplayLen = rightPlain.Length;
        int rightGapLen = rightDisplayLen > 0 ? 2 : 0;

        // LEAP label: "LEAP  " = 6 display chars
        int leapLabelLen = 6;
        // Cursor block = 1 display char
        int cursorLen = 1;

        // Scrollable zone width (between LEAP label and cursor, before right anchor)
        int scrollZoneWidth = width - leapLabelLen - cursorLen - rightGapLen - rightDisplayLen;
        if (scrollZoneWidth < 0) scrollZoneWidth = 0;

        // Full buffer display (plain chars for width)
        string bufferPlain = trail + localFilter;
        int bufferLen = bufferPlain.Length;

        bool needsScroll = bufferLen > scrollZoneWidth;
        int scrollIndLen = needsScroll ? 1 : 0;
        int availableForBuffer = scrollZoneWidth - scrollIndLen;
        if (availableForBuffer < 0) availableForBuffer = 0;

        int skipLeft = needsScroll ? bufferLen - availableForBuffer : 0;

        // How much of trail and localFilter is visible
        int trailVisible = Math.Max(0, trail.Length - skipLeft);
        int filterStart = Math.Max(0, skipLeft - trail.Length);
        string visibleTrail = trailVisible > 0 ? trail[^trailVisible..] : "";
        string visibleFilter = filterStart < localFilter.Length ? localFilter[filterStart..] : localFilter;

        // Colored segments
        string leapLabel = "\x1b[91mLEAP\x1b[0m  ";
        string scrollInd = needsScroll ? "\x1b[2;37m◂\x1b[0m" : "";
        string coloredTrail = visibleTrail.Length > 0 ? ("\x1b[2;37m" + visibleTrail + "\x1b[0m") : "";
        string coloredFilter = visibleFilter.Length > 0 ? ("\x1b[93m" + visibleFilter + "\x1b[0m") : "";
        string cursorBlock = "\x1b[93m\x1b[7m \x1b[0m";

        // Padding
        int usedSoFar = leapLabelLen + scrollIndLen + trailVisible + visibleFilter.Length + cursorLen + rightGapLen + rightDisplayLen;
        int padding = Math.Max(0, width - usedSoFar);

        string rightGap = new string(' ', rightGapLen + padding);
        string coloredRight = rightDisplayLen == 0 ? (padding > 0 ? new string(' ', padding) : "") :
            rightIsNoMatch ? ("\x1b[38;5;102m" + rightPlain + "\x1b[0m") :
                             ("\x1b[92m" + rightPlain + "\x1b[0m");

        if (rightDisplayLen == 0)
            return leapLabel + scrollInd + coloredTrail + coloredFilter + cursorBlock + rightGap;

        return leapLabel + scrollInd + coloredTrail + coloredFilter + cursorBlock + rightGap + coloredRight;
    }
}

internal record PreviewContent(
    string    TypeLabel,
    string    InfoLine,
    string    Modified,
    string[]  BodyLines,
    bool      IsPartial,
    string?   ExtMismatch = null,  // set when magic-detected type ≠ extension type
    string[]? PixelLines  = null,  // pre-rendered ANSI half-block rows; bypasses SmartTruncate
    int       PixelWidth  = 0      // visual width of each pixel line (count of ▄ chars)
);

internal sealed class PreviewPane
{
    public bool IsVisible;
    public string? CurrentPath;
    public FileType? CurrentType;
    public PreviewContent? Content;
    public bool IsLoading;
    public CancellationTokenSource? Cts;

    public void Cancel()
    {
        Cts?.Cancel();
        Cts?.Dispose();
        Cts = null;
        IsLoading = false;
    }
}

internal static class PreviewLoader
{
    // ── Extension-mismatch detection ──────────────────────────────────────

    internal static string? CheckExtensionMismatch(string filePath, FileType detected)
    {
        if (detected.Category == FileCategory.Binary || detected.Category == FileCategory.Text)
            return null; // too ambiguous to warn on

        string ext = System.IO.Path.GetExtension(filePath);
        if (string.IsNullOrEmpty(ext)) return null;

        // Try extension-only detection (pass empty magic so only ext map is used)
        var extOnly = FileTypeDetector.Detect(filePath, ReadOnlySpan<byte>.Empty);
        if (extOnly.Category == FileCategory.Binary) return null; // extension unknown

        if (extOnly.Category != detected.Category || extOnly.Label != detected.Label)
            return $"⚠ {ext} extension but detected as {detected.Label}";
        return null;
    }

    // ── Public dispatch ────────────────────────────────────────────────────

    public static async Task<PreviewContent> LoadAsync(
        string filePath, bool isDrive, FileType fileType,
        int width, int height, CancellationToken ct)
    {
        string modified = GetModified(filePath, isDrive);
        int bodyHeight = Math.Max(1, height - 5); // header + info + date + divider + status rows

        try
        {
            if (isDrive)
                return LoadDrive(filePath, width, bodyHeight);

            string? mismatch = CheckExtensionMismatch(filePath, fileType);

            var content = fileType.Category switch
            {
                FileCategory.Text      => await LoadTextAsync(filePath, fileType, modified, width, bodyHeight, ct),
                FileCategory.Image     => await LoadImagePreviewAsync(filePath, fileType, modified, width, bodyHeight, ct),
                FileCategory.Video     => await LoadVideoPreviewAsync(filePath, fileType, modified, width, bodyHeight, ct),
                FileCategory.Audio     => LoadAudioMeta(filePath, fileType, modified, width),
                FileCategory.Archive   => await LoadArchiveAsync(filePath, fileType, modified, width, bodyHeight, ct),
                FileCategory.Executable=> LoadExecutable(filePath, fileType, modified, width),
                FileCategory.Pdf       => LoadPdf(filePath, fileType, modified, width),
                _                      => await LoadBinaryAsync(filePath, fileType, modified, width, bodyHeight, ct),
            };

            // If type-specific loader produced no body content, fall back to hex dump
            if (content.BodyLines.Length == 0 && content.PixelLines is null)
            {
                var hex = await LoadBinaryAsync(filePath, fileType, modified, width, bodyHeight, ct);
                content = content with { BodyLines = hex.BodyLines };
            }

            return mismatch != null ? content with { ExtMismatch = mismatch } : content;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new PreviewContent(fileType.Label, ex.Message, modified, [], false);
        }
    }

    // ── Hex helpers (internal for tests) ───────────────────────────────────

    internal static int CalcBytesPerRow(int previewWidth)
    {
        // Layout per row: 8 (addr) + 2 (gap) + N*3 (hex) + 1 (gap) + N (ascii) + 4 (margin) = 15 + 4N
        int n = (previewWidth - 15) / 4;
        return Math.Clamp(n, 4, 16);
    }

    internal static string[] FormatHexLines(byte[] data, int bytesPerRow, int previewWidth)
    {
        int rowCount = (data.Length + bytesPerRow - 1) / bytesPerRow;
        string[] lines = new string[rowCount];
        var sb = new System.Text.StringBuilder(previewWidth + 4);

        for (int row = 0; row < rowCount; row++)
        {
            sb.Clear();
            int offset = row * bytesPerRow;
            sb.Append(offset.ToString("X8"));
            sb.Append("  ");

            // Hex part
            for (int i = 0; i < bytesPerRow; i++)
            {
                if (offset + i < data.Length)
                    sb.Append(data[offset + i].ToString("X2")).Append(' ');
                else
                    sb.Append("   ");
            }

            sb.Append(' ');

            // ASCII part
            for (int i = 0; i < bytesPerRow; i++)
            {
                if (offset + i >= data.Length) break;
                byte b = data[offset + i];
                sb.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
            }

            lines[row] = sb.ToString();
        }
        return lines;
    }

    // ── Text ───────────────────────────────────────────────────────────────

    private static async Task<PreviewContent> LoadTextAsync(
        string filePath, FileType fileType, string modified,
        int width, int bodyHeight, CancellationToken ct)
    {
        string encoding = "UTF-8";
        int lineCount = 0;
        var bodyLines = new List<string>(bodyHeight + 1);

        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);

        // Detect encoding from BOM
        byte[] bom = new byte[4];
        int bomRead = await fs.ReadAsync(bom, ct);
        if (bomRead >= 3 && bom[0]==0xEF && bom[1]==0xBB && bom[2]==0xBF) encoding = "UTF-8 BOM";
        else if (bomRead >= 2 && bom[0]==0xFF && bom[1]==0xFE) encoding = "UTF-16 LE";
        else if (bomRead >= 2 && bom[0]==0xFE && bom[1]==0xFF) encoding = "UTF-16 BE";
        fs.Seek(0, SeekOrigin.Begin);

        using var reader = new StreamReader(fs, detectEncodingFromByteOrderMarks: true, leaveOpen: false);
        const int maxCountLines = 10_000;
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            ct.ThrowIfCancellationRequested();
            lineCount++;
            if (bodyLines.Count < bodyHeight)
                bodyLines.Add(CharacterWidth.SmartTruncate(line, width - 1));
            if (lineCount >= maxCountLines && bodyLines.Count >= bodyHeight)
                break;
        }

        string lineInfo = lineCount >= maxCountLines ? $"> {maxCountLines} lines" : $"{lineCount} lines";
        string infoLine = $"{encoding} · {lineInfo} · {FormatSize(new FileInfo(filePath).Length)}";
        return new PreviewContent(fileType.Label, infoLine, modified, [.. bodyLines], false);
    }

    // ── Binary / hex fallback ──────────────────────────────────────────────

    private static async Task<PreviewContent> LoadBinaryAsync(
        string filePath, FileType fileType, string modified,
        int width, int bodyHeight, CancellationToken ct)
    {
        int bytesPerRow = CalcBytesPerRow(width);
        int bytesToRead = Math.Min(bytesPerRow * bodyHeight, 4096);

        byte[] buf = new byte[bytesToRead];
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        int read = await fs.ReadAsync(buf.AsMemory(0, bytesToRead), ct);

        byte[] data = buf[..read];
        string[] hexLines = FormatHexLines(data, bytesPerRow, width);

        long size = new FileInfo(filePath).Length;
        string infoLine = FormatSize(size);
        return new PreviewContent(fileType.Label, infoLine, modified, hexLines, false);
    }

    // ── Shared utilities ───────────────────────────────────────────────────

    private static string GetModified(string filePath, bool isDrive)
    {
        if (isDrive) return "";
        try { return File.GetLastWriteTime(filePath).ToString("yyyy-MM-dd HH:mm"); }
        catch { return ""; }
    }

    internal static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
    }

    private static byte[] ReadHeader(string filePath, int bytes)
    {
        using var fs = File.OpenRead(filePath);
        byte[] buf = new byte[Math.Min((int)fs.Length, bytes)];
        fs.Read(buf, 0, buf.Length);
        return buf;
    }

    private static string FormatDuration(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.Hours > 0
            ? $"{ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    // ── Audio metadata ────────────────────────────────────────────────────
    private static PreviewContent LoadAudioMeta(string filePath, FileType fileType, string modified, int width)
    {
        try
        {
            byte[] header = ReadHeader(filePath, 512);
            string info = fileType.Label switch
            {
                "WAV Audio"  => ParseWavInfo(header, filePath),
                "FLAC Audio" => ParseFlacInfo(header),
                "MP3 Audio"  => ParseMp3Info(header, filePath),
                _ => FormatSize(new FileInfo(filePath).Length)
            };
            return new PreviewContent(fileType.Label, info, modified, [], false);
        }
        catch { return new PreviewContent(fileType.Label, FormatSize(new FileInfo(filePath).Length), modified, [], false); }
    }

    private static string ParseWavInfo(byte[] h, string filePath)
    {
        // RIFF/WAVE: read fmt chunk dynamically to find data offset
        // Standard RIFF header: "RIFF"(0) size(4) "WAVE"(8)
        // offset 12 = "fmt "(ASCII) or other chunks
        // offset 16 = fmt chunk size (LE32), then the fmt data follows
        if (h.Length < 20) return FormatSize(new FileInfo(filePath).Length);

        // Try to read fmt chunk size from standard offset (WAVE files often have fmt at 12)
        int fmtChunkSize = h[16]|(h[17]<<8)|(h[18]<<16)|(h[19]<<24);
        int dataOffset = 20 + fmtChunkSize + 8; // skip fmt data + "data" tag (4) + chunk size (4)

        if (h.Length < dataOffset + 4) return FormatSize(new FileInfo(filePath).Length);

        // Extract fmt parameters from the fmt chunk (always at offset 20 after the size field)
        int channels      = h[22]|(h[23]<<8);
        int sampleRate    = h[24]|(h[25]<<8)|(h[26]<<16)|(h[27]<<24);
        int byteRate      = h[28]|(h[29]<<8)|(h[30]<<16)|(h[31]<<24);
        int bitsPerSample = h[34]|(h[35]<<8);

        long dataBytes    = h[dataOffset]|((long)h[dataOffset+1]<<8)|((long)h[dataOffset+2]<<16)|((long)h[dataOffset+3]<<24);
        double seconds    = byteRate > 0 ? (double)dataBytes / byteRate : 0;
        string dur = FormatDuration(seconds);
        string ch  = channels == 1 ? "Mono" : channels == 2 ? "Stereo" : $"{channels}ch";
        return $"{dur} · {sampleRate / 1000.0:F1} kHz · {ch} · {bitsPerSample}-bit";
    }

    private static string ParseFlacInfo(byte[] h)
    {
        // STREAMINFO block: after 4-byte "fLaC" marker
        // byte 4 = block type (0 = STREAMINFO), bytes 5-7 = length
        // bytes 18-21: sample rate (20 bits BE), channels (3 bits), bitsPerSample-1 (5 bits)
        // bytes 21-25: total samples (36 bits)
        if (h.Length < 38) return "";
        int sampleRate = (h[18]<<12)|(h[19]<<4)|(h[20]>>4);
        int channels   = ((h[20]>>1) & 0x07) + 1;
        int bits       = (((h[20]&0x01)<<4)|(h[21]>>4)) + 1;
        long samples   = ((long)(h[21]&0x0F)<<32)|((long)h[22]<<24)|((long)h[23]<<16)|((long)h[24]<<8)|h[25];
        double seconds = sampleRate > 0 ? (double)samples / sampleRate : 0;
        string dur = FormatDuration(seconds);
        string ch  = channels == 1 ? "Mono" : channels == 2 ? "Stereo" : $"{channels}ch";
        return $"{dur} · {sampleRate / 1000.0:F1} kHz · {ch} · {bits}-bit";
    }

    private static string ParseMp3Info(byte[] h, string filePath)
    {
        long fileSize = new FileInfo(filePath).Length;
        int bitrate = 0;
        for (int i = 0; i < h.Length - 3; i++)
        {
            if (h[i] != 0xFF || (h[i+1] & 0xE0) != 0xE0) continue;
            int b3 = h[i+2];
            int brIdx = (b3 >> 4) & 0x0F;
            int[] table = [0,32,40,48,56,64,80,96,112,128,160,192,224,256,320,0];
            bitrate = table[brIdx] * 1000;
            break;
        }
        double seconds = bitrate > 0 ? (fileSize * 8.0) / bitrate : 0;
        string dur = seconds > 0 ? FormatDuration(seconds) : "unknown duration";
        return bitrate > 0 ? $"{dur} · {bitrate/1000} kbps" : $"{dur} · {FormatSize(fileSize)}";
    }

    // ── Video metadata ────────────────────────────────────────────────────
    private static PreviewContent LoadVideoMeta(string filePath, FileType fileType, string modified, int width)
    {
        try
        {
            string info = fileType.Label switch
            {
                "MP4 Video" or "QuickTime Video" or "M4A Audio" => ParseMp4Info(filePath),
                "AVI Video" => ParseAviInfo(filePath),
                _ => FormatSize(new FileInfo(filePath).Length)
            };
            return new PreviewContent(fileType.Label, info, modified, [], false);
        }
        catch { return new PreviewContent(fileType.Label, FormatSize(new FileInfo(filePath).Length), modified, [], false); }
    }

    internal static string GetVideoCachePath(string filePath)
    {
        var fi = new FileInfo(filePath);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(filePath));
        string prefix = Convert.ToHexString(hash[..16]).ToLowerInvariant();
        string cacheDir = Path.Combine(Path.GetTempPath(), "ColumnFileManager");
        Directory.CreateDirectory(cacheDir);
        return Path.Combine(cacheDir, $"{prefix}_{fi.LastWriteTimeUtc.Ticks:x16}_{fi.Length:x16}.png");
    }

    private static async Task<bool> ExtractVideoFrameAsync(string filePath, string cachePath, CancellationToken ct)
    {
        string tempFile = cachePath + ".tmp";
        Process? process = null;
        try
        {
            var psi = new ProcessStartInfo("ffmpeg")
            {
                UseShellExecute       = false,
                CreateNoWindow        = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-ss");       psi.ArgumentList.Add("00:00:05");
            psi.ArgumentList.Add("-i");        psi.ArgumentList.Add(filePath);
            psi.ArgumentList.Add("-vframes");  psi.ArgumentList.Add("1");
            psi.ArgumentList.Add("-vf");       psi.ArgumentList.Add("scale=63:200:force_original_aspect_ratio=decrease");
            psi.ArgumentList.Add("-f");        psi.ArgumentList.Add("image2");
            psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-y");        psi.ArgumentList.Add(tempFile);

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var linkedCts  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            process = Process.Start(psi);
            if (process is null) return false;
            await process.WaitForExitAsync(linkedCts.Token);

            if (process.ExitCode != 0 || !File.Exists(tempFile) || new FileInfo(tempFile).Length == 0)
                return false;

            File.Move(tempFile, cachePath, overwrite: true);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return false; }
        catch (Win32Exception) { return false; }
        catch { return false; }
        finally
        {
            try { process?.Kill(entireProcessTree: true); } catch { }
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
        }
    }

    internal static async Task<PreviewContent> LoadVideoPreviewAsync(
        string filePath, FileType fileType, string modified,
        int width, int bodyHeight, CancellationToken ct)
    {
        var meta = LoadVideoMeta(filePath, fileType, modified, width);

        try
        {
            ct.ThrowIfCancellationRequested();

            int pixelCols = width - 1;
            int pixelRows = (bodyHeight - 1) * 2;
            if (pixelCols <= 0 || pixelRows <= 0) return meta;

            string cachePath = GetVideoCachePath(filePath);

            if (!File.Exists(cachePath))
            {
                bool extracted = await ExtractVideoFrameAsync(filePath, cachePath, ct);
                if (!extracted) return meta;
            }

            var (pixelLines, pixelWidth) = await Task.Run(
                () => ImageSharpProxy.LoadFrameAndRender(cachePath, pixelCols, pixelRows, ct), ct);

            ct.ThrowIfCancellationRequested();
            return meta with { PixelLines = pixelLines, PixelWidth = pixelWidth };
        }
        catch (OperationCanceledException) { throw; }
        catch { return meta; }
    }

    private static string ParseMp4Info(string filePath)
    {
        long fileSize = new FileInfo(filePath).Length;
        double duration = 0;
        int vidW = 0, vidH = 0;
        using var fs = File.OpenRead(filePath);
        WalkMp4Boxes(fs, fs.Length, ref duration, ref vidW, ref vidH, 0);
        var parts = new List<string>();
        if (vidW > 0 && vidH > 0) parts.Add($"{vidW} × {vidH}");
        if (duration > 0) parts.Add(FormatDuration(duration));
        parts.Add(FormatSize(fileSize));
        return string.Join(" · ", parts);
    }

    private static void WalkMp4Boxes(Stream s, long limit, ref double duration, ref int w, ref int h, int depth)
    {
        if (depth > 6) return;
        byte[] buf = new byte[8];
        while (s.Position < limit - 8)
        {
            long boxStart = s.Position;
            if (s.Read(buf, 0, 8) < 8) return;
            long size = (long)(uint)((buf[0]<<24)|(buf[1]<<16)|(buf[2]<<8)|buf[3]);
            string type = System.Text.Encoding.ASCII.GetString(buf, 4, 4);
            if (size == 1)
            {
                byte[] ext = new byte[8];
                s.Read(ext, 0, 8);
                size = (long)(((ulong)ext[0]<<56)|((ulong)ext[1]<<48)|((ulong)ext[2]<<40)|((ulong)ext[3]<<32)
                            | ((ulong)ext[4]<<24)|((ulong)ext[5]<<16)|((ulong)ext[6]<<8)|(ulong)ext[7]);
            }
            if (size < 8) return;
            long contentStart = s.Position;
            long boxEnd = boxStart + size;

            if (type == "moov" || type == "trak")
            {
                WalkMp4Boxes(s, boxEnd, ref duration, ref w, ref h, depth + 1);
                s.Seek(boxEnd, SeekOrigin.Begin);
                continue;
            }
            if (type == "mvhd" && duration == 0)
            {
                byte[] mvhd = new byte[Math.Min(28, (int)(boxEnd - contentStart))];
                s.Read(mvhd, 0, mvhd.Length);
                int ver = mvhd[0];
                long ts, dur;
                if (ver == 1)
                {
                    ts  = (long)(((ulong)mvhd[4]<<56)|((ulong)mvhd[5]<<48)|((ulong)mvhd[6]<<40)|((ulong)mvhd[7]<<32)
                               | ((ulong)mvhd[8]<<24)|((ulong)mvhd[9]<<16)|((ulong)mvhd[10]<<8)|(ulong)mvhd[11]);
                    dur = (long)(((ulong)mvhd[12]<<56)|((ulong)mvhd[13]<<48)|((ulong)mvhd[14]<<40)|((ulong)mvhd[15]<<32)
                               | ((ulong)mvhd[16]<<24)|((ulong)mvhd[17]<<16)|((ulong)mvhd[18]<<8)|(ulong)mvhd[19]);
                }
                else
                {
                    ts  = (uint)((uint)mvhd[4]<<24|(uint)mvhd[5]<<16|(uint)mvhd[6]<<8|(uint)mvhd[7]);
                    dur = (uint)((uint)mvhd[8]<<24|(uint)mvhd[9]<<16|(uint)mvhd[10]<<8|(uint)mvhd[11]);
                }
                if (ts > 0) duration = (double)dur / ts;
            }
            if (type == "tkhd" && w == 0)
            {
                byte[] tkhd = new byte[Math.Min(100, (int)(boxEnd - contentStart))];
                s.Read(tkhd, 0, tkhd.Length);
                int off = tkhd[0] == 1 ? 92 : 76;
                if (tkhd.Length >= off + 8)
                {
                    w = (tkhd[off]<<8)|tkhd[off+1];
                    h = (tkhd[off+4]<<8)|tkhd[off+5];
                }
            }
            s.Seek(boxEnd, SeekOrigin.Begin);
        }
    }

    private static string ParseAviInfo(string filePath)
    {
        // AVI layout: RIFF(0) filesize(4) "AVI "(8) LIST(12) listsize(16) "hdrl"(20)
        //   "avih"(24) chunksize(28)  avih-data(32):
        //   usPerFrame(32) maxBytesPerSec(36) padding(40) flags(44) totalFrames(48)
        //   initialFrames(52) streams(56) bufferSize(60) width(64) height(68)
        long fileSize = new FileInfo(filePath).Length;
        byte[] h = ReadHeader(filePath, 72);
        if (h.Length < 72) return FormatSize(fileSize);
        if (h[24]!=0x61||h[25]!=0x76||h[26]!=0x69||h[27]!=0x68) return FormatSize(fileSize); // "avih"
        long usPerFrame  = (long)h[32]|((long)h[33]<<8)|((long)h[34]<<16)|((long)h[35]<<24);
        long totalFrames = (long)h[48]|((long)h[49]<<8)|((long)h[50]<<16)|((long)h[51]<<24);
        int  vidW        = h[64]|(h[65]<<8)|(h[66]<<16)|(h[67]<<24);
        int  vidH        = h[68]|(h[69]<<8)|(h[70]<<16)|(h[71]<<24);
        double fps     = usPerFrame > 0 ? 1_000_000.0 / usPerFrame : 0;
        double seconds = fps > 0 && totalFrames > 0 ? totalFrames / fps : 0;
        var parts = new List<string>();
        if (vidW > 0 && vidH > 0) parts.Add($"{vidW} × {vidH}");
        if (seconds > 0) parts.Add(FormatDuration(seconds));
        parts.Add(FormatSize(fileSize));
        return string.Join(" · ", parts);
    }

    // ── Archive metadata ──────────────────────────────────────────────────
    private static async Task<PreviewContent> LoadArchiveAsync(
        string filePath, FileType fileType, string modified,
        int width, int bodyHeight, CancellationToken ct)
    {
        try
        {
            long size = new FileInfo(filePath).Length;
            switch (fileType.Label)
            {
                case "ZIP Archive":
                    return await LoadZipAsync(filePath, fileType, modified, size, width, bodyHeight, ct);
                case "GZip Archive":
                {
                    string orig = ReadGzipOriginalName(filePath);
                    string info = string.IsNullOrEmpty(orig)
                        ? FormatSize(size)
                        : $"{orig} · {FormatSize(size)}";
                    return new PreviewContent(fileType.Label, info, modified, [], false);
                }
                default:
                    return new PreviewContent(fileType.Label, FormatSize(size), modified, [], false);
            }
        }
        catch { return new PreviewContent(fileType.Label, FormatSize(new FileInfo(filePath).Length), modified, [], false); }
    }

    private static async Task<PreviewContent> LoadZipAsync(
        string filePath, FileType fileType, string modified,
        long size, int width, int bodyHeight, CancellationToken ct)
    {
        await Task.Yield();
        using var za = System.IO.Compression.ZipFile.OpenRead(filePath);
        int total = za.Entries.Count;
        var body = new List<string>(Math.Min(total, bodyHeight));
        foreach (var entry in za.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (body.Count >= bodyHeight) break;
            string name = CharacterWidth.SmartTruncate(entry.FullName, width - 12);
            string entSize = FormatSize(entry.Length).PadLeft(9);
            body.Add($"  {name.PadRight(width - 13)}{entSize}");
        }
        string info = $"{total} entries · {FormatSize(size)}";
        return new PreviewContent(fileType.Label, info, modified, [.. body], false);
    }

    private static string ReadGzipOriginalName(string filePath)
    {
        byte[] h = ReadHeader(filePath, 256);
        if (h.Length < 10) return "";
        byte flags = h[3];
        if ((flags & 0x08) == 0) return "";
        int pos = 10;
        if ((flags & 0x04) != 0) { if (pos + 2 > h.Length) return ""; int xlen = h[pos]|(h[pos+1]<<8); pos += 2 + xlen; }
        var name = new System.Text.StringBuilder();
        while (pos < h.Length && h[pos] != 0) { name.Append((char)h[pos]); pos++; }
        return name.ToString();
    }

    // ── Drive info ────────────────────────────────────────────────────────
    private static PreviewContent LoadDrive(string drivePath, int width, int bodyHeight)
    {
        var di = new DriveInfo(drivePath);
        if (!di.IsReady)
            return new PreviewContent("Drive", "Not ready", "", [], false);

        long total = di.TotalSize;
        long free  = di.AvailableFreeSpace;
        long used  = total - free;
        double pct = total > 0 ? (double)used / total : 0;

        int barWidth = Math.Max(4, width - 8);
        int filled = Math.Min((int)(pct * barWidth), barWidth);
        string bar = new string('█', filled) + new string('░', barWidth - filled);

        string driveType = di.DriveType switch
        {
            DriveType.Fixed      => "Fixed Drive",
            DriveType.Removable  => "Removable Drive",
            DriveType.Network    => "Network Drive",
            DriveType.CDRom      => "Optical Drive",
            DriveType.Ram        => "RAM Disk",
            _ => "Drive"
        };

        var body = new List<string>
        {
            $"Label:      {(string.IsNullOrEmpty(di.VolumeLabel) ? "(none)" : di.VolumeLabel)}",
            $"Filesystem: {di.DriveFormat}",
            $"Total:      {FormatSize(total)}",
            $"Used:       {FormatSize(used)} ({pct:P0})",
            $"Free:       {FormatSize(free)}",
            "",
            bar + $" {pct:P0}"
        };

        string typeLabel = $"{di.DriveFormat} · {driveType}";
        return new PreviewContent(typeLabel, "", "", [.. body], false);
    }

    // ── Image metadata ────────────────────────────────────────────────────
    private static PreviewContent LoadImageMeta(string filePath, FileType fileType, string modified, int width)
    {
        try
        {
            byte[] header = ReadHeader(filePath, 64);
            string dims = "unknown dimensions";
            string extra = "";

            switch (fileType.Label)
            {
                case "PNG Image":
                {
                    var (w, h, bits, color) = ParsePngHeader(header);
                    dims = $"{w} × {h} px";
                    extra = $"{bits}-bit {color}";
                    break;
                }
                case "JPEG Image":
                {
                    var (w, h) = ParseJpegDimensions(filePath);
                    dims = $"{w} × {h} px";
                    break;
                }
                case "GIF Image":
                {
                    var (w, h) = ParseGifHeader(header);
                    dims = $"{w} × {h} px";
                    break;
                }
                case "BMP Image":
                {
                    var (w, h) = ParseBmpHeader(header);
                    dims = $"{w} × {h} px";
                    break;
                }
                case "WebP Image":
                {
                    var (w, h) = ParseWebpHeader(header);
                    dims = $"{w} × {h} px";
                    break;
                }
                case "SVG Image":
                {
                    var (w, h) = ParseSvgDimensions(filePath);
                    dims = string.IsNullOrEmpty(w) ? "vector (no size)" : $"{w} × {h}";
                    break;
                }
            }

            long size = new FileInfo(filePath).Length;
            string info = string.IsNullOrEmpty(extra)
                ? $"{dims} · {FormatSize(size)}"
                : $"{dims} · {extra} · {FormatSize(size)}";
            return new PreviewContent(fileType.Label, info, modified, [], false);
        }
        catch { return new PreviewContent(fileType.Label, FormatSize(new FileInfo(filePath).Length), modified, [], false); }
    }

    internal static async Task<PreviewContent> LoadImagePreviewAsync(
        string filePath, FileType fileType, string modified, int width, int bodyHeight, CancellationToken ct)
    {
        // Always get metadata first — InfoLine (dimensions + size) comes from LoadImageMeta
        var meta = LoadImageMeta(filePath, fileType, modified, width);

        // SVG and ICO cannot be rasterized by ImageSharp (no bundled rasterizer)
        if (fileType.Label == "SVG Image" || fileType.Label == "Icon")
            return meta;

        try
        {
            ct.ThrowIfCancellationRequested();

            // pixel columns = previewWidth - 1  (separator takes 1 col)
            // pixel rows    = (bodyHeight-1) * 2  (DrawPreviewToLines draws bodyHeight-1 body rows)
            int pixelCols = width - 1;
            int pixelRows = (bodyHeight - 1) * 2;

            var (pixelLines, pixelWidth) = await Task.Run(
                () => ImageSharpProxy.LoadAndRender(filePath, pixelCols, pixelRows, ct), ct);

            ct.ThrowIfCancellationRequested();
            return meta with { PixelLines = pixelLines, PixelWidth = pixelWidth };
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // Corrupted file, unsupported sub-format, etc. → fall back to metadata only
            return meta;
        }
    }

    internal static (int W, int H, int Bits, string Color) ParsePngHeader(byte[] h)
    {
        if (h.Length < 26) return (0, 0, 0, "");
        int w     = (h[16]<<24)|(h[17]<<16)|(h[18]<<8)|h[19];
        int ht    = (h[20]<<24)|(h[21]<<16)|(h[22]<<8)|h[23];
        int bits  = h[24];
        string color = h[25] switch { 0=>"Grayscale", 2=>"RGB", 3=>"Indexed", 4=>"Grayscale+A", 6=>"RGBA", _=>"?" };
        return (w, ht, bits, color);
    }

    internal static (int W, int H) ParseGifHeader(byte[] h)
    {
        if (h.Length < 10) return (0, 0);
        int w = h[6] | (h[7] << 8);
        int ht = h[8] | (h[9] << 8);
        return (w, ht);
    }

    internal static (int W, int H) ParseBmpHeader(byte[] h)
    {
        if (h.Length < 26) return (0, 0);
        int w  = h[18]|(h[19]<<8)|(h[20]<<16)|(h[21]<<24);
        int ht = h[22]|(h[23]<<8)|(h[24]<<16)|(h[25]<<24);
        return (w, Math.Abs(ht));
    }

    private static (int W, int H) ParseWebpHeader(byte[] h)
    {
        // VP8 : bytes 26-27 = width-1, 28-29 = height-1 (14-bit LE)
        if (h.Length < 30) return (0, 0);
        if (h[12]==0x56&&h[13]==0x50&&h[14]==0x38&&h[15]==0x20) // "VP8 "
        {
            int w = 1 + ((h[26]|(h[27]<<8)) & 0x3FFF);
            int ht= 1 + ((h[28]|(h[29]<<8)) & 0x3FFF);
            return (w, ht);
        }
        return (0, 0);
    }

    private static (int W, int H) ParseJpegDimensions(string filePath)
    {
        using var fs = File.OpenRead(filePath);
        byte[] buf = new byte[2];
        if (fs.Read(buf, 0, 2) < 2 || buf[0] != 0xFF || buf[1] != 0xD8) return (0, 0);
        while (fs.Position < fs.Length - 9)
        {
            if (fs.ReadByte() != 0xFF) continue;
            int marker = fs.ReadByte();
            bool isSof = marker == 0xC0 || marker == 0xC1 || marker == 0xC2
                       || marker == 0xC9 || marker == 0xCA;
            if (fs.Read(buf, 0, 2) < 2) break;
            int segLen = (buf[0] << 8) | buf[1];
            if (isSof)
            {
                byte[] sof = new byte[5];
                if (fs.Read(sof, 0, 5) < 5) break;
                int h = (sof[1] << 8) | sof[2];
                int w = (sof[3] << 8) | sof[4];
                return (w, h);
            }
            fs.Seek(segLen - 2, SeekOrigin.Current);
        }
        return (0, 0);
    }

    private static (string W, string H) ParseSvgDimensions(string filePath)
    {
        try
        {
            byte[] buf = new byte[1024];
            using var fs = File.OpenRead(filePath);
            int read = fs.Read(buf, 0, buf.Length);
            string text = System.Text.Encoding.UTF8.GetString(buf, 0, read);
            string w = ExtractAttr(text, "width");
            string h = ExtractAttr(text, "height");
            return (w, h);
        }
        catch { return ("", ""); }
    }

    private static string ExtractAttr(string text, string attr)
    {
        int idx = text.IndexOf(attr + "=\"", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return "";
        int start = idx + attr.Length + 2;
        int end = text.IndexOf('"', start);
        return end > start ? text[start..end] : "";
    }

    // ── PDF ───────────────────────────────────────────────────────────────
    private static PreviewContent LoadPdf(string filePath, FileType fileType, string modified, int width)
    {
        byte[] header = ReadHeader(filePath, 512);
        string version = ParsePdfVersion(header);
        int pages = CountPdfPages(filePath);
        string title = ReadPdfInfoField(filePath, "Title");
        string author = ReadPdfInfoField(filePath, "Author");

        var body = new List<string>();
        if (!string.IsNullOrEmpty(title))  body.Add(CharacterWidth.SmartTruncate($"Title:  {title}", width - 1));
        if (!string.IsNullOrEmpty(author)) body.Add(CharacterWidth.SmartTruncate($"Author: {author}", width - 1));

        long size = new FileInfo(filePath).Length;
        string pageStr = pages > 0 ? $"{pages} pages" : "? pages";
        string info = $"PDF {version} · {pageStr} · {FormatSize(size)}";
        return new PreviewContent(fileType.Label, info, modified, [.. body], false);
    }

    internal static string ParsePdfVersion(byte[] header)
    {
        string text = System.Text.Encoding.ASCII.GetString(header, 0, Math.Min(header.Length, 16));
        int idx = text.IndexOf("%PDF-", StringComparison.Ordinal);
        if (idx < 0) return "?";
        int start = idx + 5;
        int end = start;
        while (end < text.Length && (char.IsDigit(text[end]) || text[end] == '.')) end++;
        return end > start ? text[start..end] : "?";
    }

    private static int CountPdfPages(string filePath)
    {
        try
        {
            long length = new FileInfo(filePath).Length;
            int scanSize = (int)Math.Min(65536, length);
            byte[] buf = new byte[scanSize];
            using var fs = File.OpenRead(filePath);
            fs.Seek(-scanSize, SeekOrigin.End);
            fs.Read(buf, 0, scanSize);
            string tail = System.Text.Encoding.Latin1.GetString(buf);
            int best = 0, idx = 0;
            while ((idx = tail.IndexOf("/Count ", idx, StringComparison.Ordinal)) >= 0)
            {
                idx += 7;
                int end = idx;
                while (end < tail.Length && char.IsDigit(tail[end])) end++;
                if (end > idx && int.TryParse(tail[idx..end], out int n) && n > best) best = n;
            }
            return best;
        }
        catch { return 0; }
    }

    private static string ReadPdfInfoField(string filePath, string fieldName)
    {
        try
        {
            long length = new FileInfo(filePath).Length;
            int scanSize = (int)Math.Min(131072, length);
            byte[] buf = new byte[scanSize];
            using var fs = File.OpenRead(filePath);
            fs.Seek(-scanSize, SeekOrigin.End);
            fs.Read(buf, 0, scanSize);
            string tail = System.Text.Encoding.Latin1.GetString(buf);
            string key = "/" + fieldName + " (";
            int idx = tail.LastIndexOf(key, StringComparison.Ordinal);
            if (idx < 0) return "";
            int start = idx + key.Length;
            int end = tail.IndexOf(')', start);
            return end > start ? tail[start..end].Trim() : "";
        }
        catch { return ""; }
    }

    // ── PE Executable ─────────────────────────────────────────────────────
    private static PreviewContent LoadExecutable(string filePath, FileType fileType, string modified, int width)
    {
        try
        {
            byte[] header = ReadHeader(filePath, 512);
            if (header.Length < 64 || header[0] != 0x4D || header[1] != 0x5A)
                return new PreviewContent(fileType.Label, FormatSize(new FileInfo(filePath).Length), modified, [], false);

            int peOffset = header[60]|(header[61]<<8)|(header[62]<<16)|(header[63]<<24);
            if (peOffset + 24 >= header.Length)
                header = ReadHeader(filePath, peOffset + 256);
            if (peOffset + 24 >= header.Length)
                return new PreviewContent(fileType.Label, FormatSize(new FileInfo(filePath).Length), modified, [], false);

            if (header[peOffset]!=0x50||header[peOffset+1]!=0x45)
                return new PreviewContent(fileType.Label, FormatSize(new FileInfo(filePath).Length), modified, [], false);

            int machine = header[peOffset+4]|(header[peOffset+5]<<8);
            string arch = machine switch
            {
                0x014C => "x86",
                0x8664 => "x86-64",
                0xAA64 => "ARM64",
                0x01C4 => "ARM",
                _ => $"0x{machine:X4}"
            };

            int optMagic = header[peOffset+24]|(header[peOffset+25]<<8);
            int subOffset = peOffset + 92;
            int subsystem = subOffset + 1 < header.Length
                ? header[subOffset]|(header[subOffset+1]<<8) : 0;
            string sub = subsystem switch
            {
                2 => "GUI",
                3 => "Console",
                1 => "Native/Driver",
                _ => $"sub={subsystem}"
            };

            bool isDotNet = false;
            int ddOffset = optMagic == 0x20B ? peOffset + 136 : peOffset + 120;
            int clrDirOffset = ddOffset + 14 * 8;
            if (clrDirOffset + 4 < header.Length)
                isDotNet = (header[clrDirOffset]|(header[clrDirOffset+1]<<8)|(header[clrDirOffset+2]<<16)|(header[clrDirOffset+3]<<24)) != 0;

            long size = new FileInfo(filePath).Length;
            string dotnet = isDotNet ? " · .NET" : "";
            string info = $"{arch} · {sub}{dotnet} · {FormatSize(size)}";
            return new PreviewContent(fileType.Label, info, modified, [], false);
        }
        catch { return new PreviewContent(fileType.Label, FormatSize(new FileInfo(filePath).Length), modified, [], false); }
    }
}

static class Program
{
    const int ColumnWidth = 32;
    const int CacheExpireMs = 2000;
    const int MaxPathLength = 100;
    const int NavigationDebounceMs = 300;
    const int MinPaneHeight = 4; // ヘッダ1行 + 本文3行

    internal static readonly ScreenState State = new ScreenState();
    static Pane FocusedPane => State.Panes[State.FocusedPane];
    static string? _lastErrorMessage = null;
    static DateTime _lastJobSummaryRenderTime = DateTime.MinValue;
    static string? _cachedJobSummary = null;
    static readonly Dictionary<int, JobStatus> _lastKnownJobStatus = new();
    static DateTime _lastActiveJobRefreshTime = DateTime.MinValue;
    static DateTime _lastJobPollTime = DateTime.MinValue;
    static bool _shouldExit = false;
    static string? _missingEntryMessage = null;
    static Column? _missingEntryColumn = null;
    static int _missingEntrySelected = -1;
    static readonly RedrawGate _redrawGate = new RedrawGate();
    static long _framesBuilt; // diagnostic: printed on exit when ZRR_DEBUG_FRAMES is set
    static readonly object _searchLock = new object();
    static readonly object _dialogQueueLock = new object();
    static readonly Queue<Action> _dialogRequestQueue = new();
    static readonly Dictionary<int, ConflictResolution> _jobStickyChoice = new();
    static MigemoProvider? _migemo;

    static readonly Dictionary<string, string> _cursorMemory = new(StringComparer.OrdinalIgnoreCase);
    static readonly List<string> _cursorMemoryKeys = new();
    const int CursorMemoryLimit = 1000;

    static readonly List<string> _history = new();
    const int HistoryLimit = 100;

    static async Task Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        _migemo = new MigemoProvider();
        if (!_migemo.IsAvailable)
        {
            if (_migemo.DllLoaded)
                Console.WriteLine("migemo: dict not found — plain search active");
            _migemo.Dispose();
            _migemo = null;
        }

        Console.Write("\x1b[?1049h"); // Enter alternate screen buffer

        try
        {
            Console.CursorVisible = false;
        }
        catch { }

        try
        {
            string root = GetRootPath();
            State.Panes.Add(new Pane());
            State.FocusedPane = 0;
            FocusedPane.Columns.Add(await CreateColumnAsync(root));
            await RebuildRightSideAsync(FocusedPane, 0);

            while (true)
            {
                if (_shouldExit)
                    break;

                HandleResizeIfNeeded(FocusedPane);

                if (State.Dialog == null)
                {
                    Action? next = null;
                    lock (_dialogQueueLock)
                    {
                        if (_dialogRequestQueue.Count > 0)
                            next = _dialogRequestQueue.Dequeue();
                    }
                    if (next != null)
                    {
                        next.Invoke();
                        _redrawGate.Request(); // ShowXxx() doesn't request a redraw itself — do it here
                    }
                }

                await ProcessJobCompletionsAsync();
                await RefreshActiveJobColumnsAsync();

                if (_redrawGate.ShouldDraw(UiActivityInProgress(FocusedPane)) || State.PrevFrame == null)
                    Draw();

                if (!Console.KeyAvailable)
                {
                    Pane pane = FocusedPane;
                    if (State.Dialog == null && pane.Leap.Active
                        && pane.Leap.NeedsRecompute
                        && (DateTime.UtcNow - pane.Leap.LastInputTime).TotalMilliseconds >= NavigationDebounceMs)
                    {
                        pane.Leap.NeedsRecompute = false;
                        ApplyLeapFilter(pane);
                        await TryLeapAutoEnterAsync(pane); // auto-enter single directory match
                        await RebuildRightSideAsync(pane, pane.ActiveColumn);
                        _redrawGate.Request();
                    }
                    else if (State.Dialog == null && pane.Search.Active
                        && pane.Search.NeedsRecompute
                        && (DateTime.UtcNow - pane.Search.LastInputTime).TotalMilliseconds >= 300)
                    {
                        await RecomputeMatchesAsync(pane);
                        UpdateHorizontalScroll(pane);
                        await RebuildRightSideAsync(pane, pane.ActiveColumn);
                        if (pane.Preview.IsVisible) StartPreviewLoad(pane);
                        _redrawGate.Request();
                    }
                    else if (State.Dialog is JobListDialogState openJobList
                        && (DateTime.UtcNow - _lastJobPollTime).TotalMilliseconds >= 100)
                    {
                        int previousSelected = openJobList.Selected;
                        openJobList.Snapshot = JobManager.Snapshot();
                        openJobList.Selected = Math.Min(previousSelected, Math.Max(0, openJobList.Snapshot.Count - 1));
                        _lastJobPollTime = DateTime.UtcNow;
                        _redrawGate.Request();
                    }

                    if (State.Dialog == null)
                    {
                        foreach (Pane p in State.Panes)
                        {
                            await ProcessPendingNavigationAsync(p);
                            await ExtendRightSideIfReadyAsync(p);
                            ProcessPendingMarkReconciles(p);
                        }
                    }
                    System.Threading.Thread.Sleep(50);
                    continue;
                }

                ConsoleKeyInfo key = Console.ReadKey(true);
                _redrawGate.Request(); // any key → state may change → redraw next loop pass

                Pane focusedPane = FocusedPane;

                if (State.Dialog != null)
                {
                    await HandleDialogKeyAsync(focusedPane, key);
                    continue;
                }

                if (focusedPane.Leap.Active)
                {
                    await HandleLeapKeyAsync(focusedPane, key);
                    continue;
                }

                if (focusedPane.Search.Active)
                {
                    await HandleSearchKeyAsync(focusedPane, key);
                    continue;
                }

                switch (key.KeyChar)
                {
                    case (char)27: // Esc
                        if (JobManager.HasActiveJobs)
                        {
                            int activeCount = JobManager.Snapshot().FindAll(j => j.Status == JobStatus.Pending || j.Status == JobStatus.Running).Count;
                            ShowConfirm("Exit Confirmation", $"{activeCount} job(s) are still running. Cancel all and exit?", async result =>
                            {
                                if (result == true)
                                {
                                    await JobManager.CancelAllAndWaitAsync(TimeSpan.FromSeconds(5));
                                    _shouldExit = true;
                                }
                            });
                        }
                        else
                        {
                            return;
                        }
                        break;

                    default:
                        await HandleSpecialKeyAsync(focusedPane, key);
                        break;
                }
            }
        }
        finally
        {
            if (JobManager.HasActiveJobs)
                await JobManager.CancelAllAndWaitAsync(TimeSpan.FromSeconds(5));

            try { Console.CursorVisible = true; } catch { }
            Console.Write("\x1b[?1049l"); // Leave alternate screen buffer
            _migemo?.Dispose();
            if (Environment.GetEnvironmentVariable("ZRR_DEBUG_FRAMES") != null)
                Console.WriteLine($"frames built: {_framesBuilt}");
        }
    }

    static string GetRootPath()
    {
        // Special marker for drive list view
        if (IsWindows())
        {
            return "::DRIVES::";
        }
        return "/";
    }

    internal static bool IsWindows()
    {
        return System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
            System.Runtime.InteropServices.OSPlatform.Windows);
    }

    // Console.WindowWidth/Height throw IOException when no console is attached (tests, redirected IO).
    internal static int SafeWindowWidth()  { try { return Console.WindowWidth; }  catch { return 120; } }
    internal static int SafeWindowHeight() { try { return Console.WindowHeight; } catch { return 30; } }

    // Shared by BuildFrame (drawing) and StartPreviewLoad (canvas sizing) so a pane's actual
    // available height can never drift between what gets drawn and what gets rasterized.
    internal static (int Top, int Bottom) ComputeSplitPaneHeights(int consoleHeight)
    {
        int available = consoleHeight - 1 /* status row */ - 1 /* separator row */;
        int topHeight = available / 2;
        int bottomHeight = available - topHeight; // 端数は下ペインへ
        return (topHeight, bottomHeight);
    }

    // Y-range of the focused pane's own sub-rectangle within the full screen, so overlays
    // (currently: the dialog box) can center within the pane the user is looking at instead of
    // the whole screen when split mode is active. See spec: "Centering within the active pane".
    internal static (int Y, int Height) ComputeFocusedPaneYRange(bool splitActive, int focusedPane, int screenHeight)
    {
        if (!splitActive)
            return (0, screenHeight);

        (int topHeight, int bottomHeight) = ComputeSplitPaneHeights(screenHeight);
        if (focusedPane == 0)
            return (0, topHeight);
        return (topHeight + 1, bottomHeight);
    }

    // The real height available to `pane` for preview rendering — matches what DrawPaneToLines
    // will actually pass to DrawPreviewToLines as `bodyFrameHeight`, so preview content is
    // rasterized for the space it will actually be drawn into (see ComputeSplitPaneHeights).
    internal static int ComputePaneBodyFrameHeight(Pane pane)
    {
        int height = SafeWindowHeight();
        if (!State.SplitActive)
            return height; // showOwnPathLine=false, single pane: bodyFrameHeight == height

        (int topHeight, int bottomHeight) = ComputeSplitPaneHeights(height);
        bool isUpperPane = State.Panes.Count > 0 && ReferenceEquals(pane, State.Panes[0]);
        int paneHeight = isUpperPane ? topHeight : bottomHeight;
        return paneHeight + 1; // showOwnPathLine=true in split mode: bodyFrameHeight == height + 1
    }

    static bool SelectedItemIsFileOrDrive(Pane pane)
    {
        if (pane.ActiveColumn < 0 || pane.ActiveColumn >= pane.Columns.Count) return false;
        Column c = pane.Columns[pane.ActiveColumn];
        if (c.Selected < 0 || c.Selected >= c.Entries.Count) return false;
        string name = c.Entries[c.Selected];
        // Regular file (not a directory) OR a drive entry in the drives column
        if (!name.EndsWith("/")) return true;
        if (c.Path == "::DRIVES::") return true;
        return false;
    }

    static (string Path, bool IsDrive)? GetPreviewTarget(Pane pane)
    {
        if (!SelectedItemIsFileOrDrive(pane)) return null;
        Column c = pane.Columns[pane.ActiveColumn];
        string name = c.Entries[c.Selected];

        if (c.Path == "::DRIVES::")
        {
            // "C:/" → "C:\"
            string drivePath = name.TrimEnd('/') + "\\";
            return (drivePath, true);
        }

        return (System.IO.Path.Combine(c.Path, name), false);
    }

    internal static void StartPreviewLoad(Pane pane, bool forceReload = false)
    {
        if (!pane.Preview.IsVisible) return;

        // Empty-directory placeholder — show a clean message instead of trying to open a non-existent path
        if (pane.ActiveColumn >= 0 && pane.ActiveColumn < pane.Columns.Count)
        {
            Column col = pane.Columns[pane.ActiveColumn];
            if (col.Selected >= 0 && col.Selected < col.Entries.Count && col.Entries[col.Selected] == "<No file>")
            {
                pane.Preview.Cancel();
                pane.Preview.CurrentPath = null;
                pane.Preview.Content = new PreviewContent("No preview", "Empty folder", "", [], false);
                pane.Preview.IsLoading = false;
                return;
            }
        }

        // Directory preview — show item count from the already-loaded right column
        if (pane.ActiveColumn >= 0 && pane.ActiveColumn < pane.Columns.Count)
        {
            Column col = pane.Columns[pane.ActiveColumn];
            if (col.Selected >= 0 && col.Selected < col.Entries.Count)
            {
                string selName = col.Entries[col.Selected];
                if (selName.EndsWith("/") && col.Path != "::DRIVES::")
                {
                    string dirPath = System.IO.Path.Combine(col.Path, selName.TrimEnd('/'));
                    if (!forceReload && pane.Preview.CurrentPath == dirPath && pane.Preview.Content != null)
                        return;
                    string subtitle = "";
                    int rcIdx = pane.ActiveColumn + 1;
                    if (rcIdx < pane.Columns.Count)
                    {
                        Column rc = pane.Columns[rcIdx];
                        if (!rc.IsLoading && rc.Entries.Count > 0)
                        {
                            int count = (rc.Entries.Count == 1 && rc.Entries[0] == "<No file>") ? 0 : rc.Entries.Count;
                            subtitle = count == 0 ? "Empty" : $"{count} item{(count == 1 ? "" : "s")}";
                        }
                    }
                    pane.Preview.Cancel();
                    pane.Preview.CurrentPath = dirPath;
                    pane.Preview.Content = new PreviewContent("Directory", subtitle, "", [], false);
                    pane.Preview.IsLoading = false;
                    return;
                }
            }
        }

        var target = GetPreviewTarget(pane);
        if (target == null) return;

        string path = target.Value.Path;
        bool isDrive = target.Value.IsDrive;

        // Skip reload if already showing this path at current dimensions
        if (!forceReload && pane.Preview.CurrentPath == path && pane.Preview.Content != null && !pane.Preview.IsLoading)
            return;

        pane.Preview.Cancel();
        pane.Preview.CurrentPath = path;
        pane.Preview.IsLoading = true;
        pane.Preview.Content = null;

        var cts = new CancellationTokenSource();
        pane.Preview.Cts = cts;
        int termWidth = SafeWindowWidth();
        int drawnCols = 0;
        for (int i = pane.HorizontalScroll; i < pane.Columns.Count && (drawnCols + 1) * ColumnWidth <= termWidth; i++)
            drawnCols++;
        int width = Math.Max(ColumnWidth, termWidth - drawnCols * ColumnWidth);
        int height = ComputePaneBodyFrameHeight(pane);

        _ = Task.Run(async () =>
        {
            try
            {
                // Type detection (file open + magic-byte read) moved here from the caller:
                // opening a handle on a slow/cloud drive can stall on placeholder hydration,
                // and this now runs on every keystroke during file-to-file navigation — it
                // must never block the main input thread.
                byte[] magic = new byte[16];
                if (!isDrive)
                {
                    try
                    {
                        using var fs = File.OpenRead(path);
                        await fs.ReadAsync(magic, 0, 16, cts.Token);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { }
                }
                FileType fileType = isDrive
                    ? new FileType(FileCategory.Drive, "Drive")
                    : FileTypeDetector.Detect(path, magic);
                pane.Preview.CurrentType = fileType;

                var content = await PreviewLoader.LoadAsync(path, isDrive, fileType, width, height, cts.Token);
                if (!cts.Token.IsCancellationRequested)
                {
                    pane.Preview.Content = content;
                    pane.Preview.IsLoading = false;
                    _redrawGate.Request(); // signal main loop to redraw with the new content
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { pane.Preview.Content = new PreviewContent("Error", ex.Message, "", [], false); pane.Preview.IsLoading = false; _redrawGate.Request(); }
        }, cts.Token);
    }

    static void CancelPreviewLoad(Pane pane)
    {
        pane.Preview.Cancel();
        pane.Preview.Content = null;
        pane.Preview.CurrentPath = null;
    }

    static async Task HandleSpecialKeyAsync(Pane pane, ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.UpArrow:
                MoveUp(pane);
                break;

            case ConsoleKey.DownArrow:
                MoveDown(pane);
                break;

            case ConsoleKey.LeftArrow:
                MoveLeft(pane);
                break;

            case ConsoleKey.RightArrow:
                await MoveRightAsync(pane);
                break;

            case ConsoleKey.Enter:
                if ((key.Modifiers & ConsoleModifiers.Shift) != 0)
                    await ShowContextMenuAsync(pane);
                else if ((key.Modifiers & ConsoleModifiers.Control) != 0)
                    await OpenFileAsync(pane);
                else
                    await EnterAsync(pane);
                break;

            case ConsoleKey.Backspace:
                Parent(pane);
                break;

            case ConsoleKey.Delete:
                RequestDeleteSelected(pane);
                break;

            case ConsoleKey.V:
                if ((key.Modifiers & ConsoleModifiers.Shift) != 0)
                {
                    pane.Preview.IsVisible = !pane.Preview.IsVisible;
                    UpdateHorizontalScroll(pane);
                    if (pane.Preview.IsVisible)
                        StartPreviewLoad(pane);
                    else
                        CancelPreviewLoad(pane);
                    Draw();
                }
                break;

            case ConsoleKey.L:
                if ((key.Modifiers & ConsoleModifiers.Control) != 0)
                {
                    RefreshCurrent(pane);
                }
                else if ((key.Modifiers & ConsoleModifiers.Shift) != 0)
                {
                    List<string> logLines = new List<string>();
                    foreach (OpLogEntry entry in OperationLog.Snapshot())
                        logLines.Add(OperationLogFormatter.FormatEntry(entry));
                    ShowListSelect("Operation Log", logLines, _ => { }); // 確定は閉じるのみ。選択結果は使わない
                }
                break;

            case ConsoleKey.J:
                if ((key.Modifiers & ConsoleModifiers.Shift) != 0)
                {
                    ShowJobList();
                }
                break;

            case ConsoleKey.W:
                if ((key.Modifiers & ConsoleModifiers.Control) != 0)
                {
                    await ToggleSplitAsync();
                }
                break;

            case ConsoleKey.Tab:
                if (State.SplitActive)
                {
                    State.FocusedPane = State.FocusedPane == 0 ? 1 : 0;
                }
                break;

            case ConsoleKey.Spacebar:
            {
                Column activeCol = pane.Columns[pane.ActiveColumn];
                if (activeCol.Path == "::DRIVES::")
                    break;

                if ((key.Modifiers & ConsoleModifiers.Shift) != 0)
                    MarkHelper.ToggleMarkAndRetreat(activeCol);
                else
                    MarkHelper.ToggleMarkAndAdvance(activeCol);

                UpdateHorizontalScroll(pane);
                if (pane.Preview.IsVisible) StartPreviewLoad(pane);
                break;
            }

            case ConsoleKey.C:
                if (key.Modifiers == ConsoleModifiers.None)
                    await StartCopyOrMoveAsync(pane, isMove: false);
                break;

            case ConsoleKey.M:
                if (key.Modifiers == ConsoleModifiers.None)
                    await StartCopyOrMoveAsync(pane, isMove: true);
                break;

            case ConsoleKey.F3:
                EnterLeapMode(pane);
                break;

            case ConsoleKey.F5:
                RefreshCurrent(pane);
                break;

            case ConsoleKey.PageUp:
                NavigationHelper.PageUp(pane.Columns[pane.ActiveColumn], Console.WindowHeight - 3);
                UpdateHorizontalScroll(pane);
                MarkNavDirty(pane);
                if (pane.Preview.IsVisible) StartPreviewLoad(pane);
                break;

            case ConsoleKey.PageDown:
                NavigationHelper.PageDown(pane.Columns[pane.ActiveColumn], Console.WindowHeight - 3);
                UpdateHorizontalScroll(pane);
                MarkNavDirty(pane);
                if (pane.Preview.IsVisible) StartPreviewLoad(pane);
                break;

            case ConsoleKey.Home:
                NavigationHelper.GoHome(pane.Columns[pane.ActiveColumn]);
                UpdateHorizontalScroll(pane);
                MarkNavDirty(pane);
                if (pane.Preview.IsVisible) StartPreviewLoad(pane);
                break;

            case ConsoleKey.End:
                NavigationHelper.GoEnd(pane.Columns[pane.ActiveColumn]);
                UpdateHorizontalScroll(pane);
                MarkNavDirty(pane);
                if (pane.Preview.IsVisible) StartPreviewLoad(pane);
                break;

            case ConsoleKey.G:
                if (key.Modifiers == ConsoleModifiers.None)
                {
                    // g → top (must have NO modifiers)
                    NavigationHelper.GoHome(pane.Columns[pane.ActiveColumn]);
                    UpdateHorizontalScroll(pane);
                    MarkNavDirty(pane);
                    if (pane.Preview.IsVisible) StartPreviewLoad(pane);
                }
                else if ((key.Modifiers & ConsoleModifiers.Shift) != 0)
                {
                    // G → bottom
                    NavigationHelper.GoEnd(pane.Columns[pane.ActiveColumn]);
                    UpdateHorizontalScroll(pane);
                    MarkNavDirty(pane);
                    if (pane.Preview.IsVisible) StartPreviewLoad(pane);
                }
                break;

            case ConsoleKey.B:
                if ((key.Modifiers & ConsoleModifiers.Control) != 0)
                {
                    NavigationHelper.PageUp(pane.Columns[pane.ActiveColumn], Console.WindowHeight - 3);
                    UpdateHorizontalScroll(pane);
                    MarkNavDirty(pane);
                    if (pane.Preview.IsVisible) StartPreviewLoad(pane);
                }
                else if ((key.Modifiers & ConsoleModifiers.Shift) != 0)
                {
                    RequestBookmarkAdd(pane);
                }
                else if (key.Modifiers == ConsoleModifiers.None)
                {
                    RequestBookmarkJump(pane);
                }
                break;

            case ConsoleKey.F:
                if ((key.Modifiers & ConsoleModifiers.Control) != 0)
                {
                    NavigationHelper.PageDown(pane.Columns[pane.ActiveColumn], Console.WindowHeight - 3);
                    UpdateHorizontalScroll(pane);
                    MarkNavDirty(pane);
                    if (pane.Preview.IsVisible) StartPreviewLoad(pane);
                }
                break;

            default:
                if (key.KeyChar == '/' && key.Modifiers == ConsoleModifiers.None)
                    EnterSearchMode(pane);
                else if (key.KeyChar == '\\' && key.Modifiers == ConsoleModifiers.None)
                    await ShowContextMenuAsync(pane);
                else if (key.KeyChar == 'd' && key.Modifiers == ConsoleModifiers.None)
                    RequestDeleteSelected(pane);
                else if (key.KeyChar == 'r' && key.Modifiers == ConsoleModifiers.None)
                    RequestRenameSelected(pane);
                else if (key.KeyChar == 'h' && key.Modifiers == ConsoleModifiers.None)
                    RequestHistoryJump(pane);
                else if (key.KeyChar == ':' && key.Modifiers == ConsoleModifiers.None)
                {
                    Column activeCol = pane.Columns[pane.ActiveColumn];
                    if (activeCol.Path != "::DRIVES::")
                        MarkHelper.ClearAll(activeCol);
                }
                break;
        }
    }

    static async Task StartCopyOrMoveAsync(Pane pane, bool isMove)
    {
        if (pane.Columns[pane.ActiveColumn].Path == "::DRIVES::")
        {
            _lastErrorMessage = isMove ? "Cannot move from the drive list" : "Cannot copy from the drive list";
            return;
        }

        List<string> targets = GetOperationTargets(pane);
        if (targets.Count == 0)
        {
            _lastErrorMessage = isMove ? "No items to move" : "No items to copy";
            return;
        }

        string opName = isMove ? "Move" : "Copy";

        if (State.SplitActive)
        {
            int otherPaneIndex = State.FocusedPane == 0 ? 1 : 0;
            Pane otherPane = State.Panes[otherPaneIndex];
            string destDir = otherPane.Columns[otherPane.ActiveColumn].Path;

            if (destDir == "::DRIVES::")
            {
                _lastErrorMessage = "Cannot use the drive list as a destination";
                return;
            }

            EnqueueCopyOrMoveJob(opName, targets, destDir, isMove);
        }
        else
        {
            List<string> candidates = BuildBookmarkAndHistoryList();
            ShowListSelect("Select Destination", candidates, dest =>
            {
                if (dest == null)
                {
                    _lastErrorMessage = $"{opName} cancelled";
                    return;
                }
                if (dest == "::DRIVES::")
                {
                    _lastErrorMessage = "Cannot use the drive list as a destination";
                    return;
                }
                EnqueueCopyOrMoveJob(opName, targets, dest, isMove);
            });
        }

        await Task.CompletedTask;
    }

    static void EnqueueCopyOrMoveJob(string opName, List<string> targets, string destDir, bool isMove)
    {
        if (BusyWarningHelper.AnyOverlapsBusyPaths(targets))
        {
            ShowConfirm("Busy Warning", "An active job is using this path. Continue anyway?", proceed =>
            {
                if (proceed == true)
                    StartJobNow(opName, targets, destDir, isMove);
            });
            return;
        }

        StartJobNow(opName, targets, destDir, isMove);
    }

    static void StartJobNow(string opName, List<string> targets, string destDir, bool isMove)
    {
        BackgroundJob job = JobManager.Start(opName, targets, destDir, (jobRef, ct) =>
        {
            var progress = new JobFileOpProgress(jobRef);
            Task<ConflictDecision?> OnConflict(string destName) => RequestConflictAsync(jobRef.ShortId, jobRef.OpName, destName);
            return isMove
                ? FileOpEngine.MoveAsync(targets, destDir, progress, OnConflict, ct)
                : FileOpEngine.CopyAsync(targets, destDir, progress, OnConflict, ct);
        });

        _lastErrorMessage = $"{opName} started: {targets.Count} item(s) → {destDir} (#{job.ShortId})";
    }

    sealed class JobFileOpProgress : IFileOpProgress
    {
        readonly BackgroundJob _job;
        public JobFileOpProgress(BackgroundJob job) { _job = job; }

        public void Report(FileOpProgressInfo info)
        {
            lock (JobManager.ProgressLock)
            {
                _job.DoneCount = info.DoneCount;
                _job.TotalCount = info.TotalCount;
                _job.BytesTransferred = info.BytesTransferred;
                _job.CurrentFile = info.CurrentFile;
            }
        }

        public void ReportItemResult(OpLogEntry entry)
        {
            OperationLog.Append(entry with { JobId = _job.ShortId });
        }
    }

    static async Task ProcessJobCompletionsAsync()
    {
        List<BackgroundJob> snapshot = JobManager.Snapshot();
        var newlyFinished = new List<BackgroundJob>();

        foreach (var job in snapshot)
        {
            bool isFinished = job.Status == JobStatus.Completed || job.Status == JobStatus.Failed || job.Status == JobStatus.Cancelled;
            bool wasKnown = _lastKnownJobStatus.TryGetValue(job.ShortId, out JobStatus prevStatus);

            if (isFinished && (!wasKnown || prevStatus != job.Status))
                newlyFinished.Add(job);

            _lastKnownJobStatus[job.ShortId] = job.Status;
        }

        foreach (var job in newlyFinished)
        {
            _lastErrorMessage = BuildJobResultSummary(job);
            await RefreshAffectedColumnsAsync(job);

            // The job is done, so its sticky conflict-resolution choice (if any) no longer applies to
            // anything — drop it so _jobStickyChoice doesn't accumulate an unbounded stale entry per job.
            lock (_dialogQueueLock) { _jobStickyChoice.Remove(job.ShortId); }

            _redrawGate.Request();
        }
    }

    static string BuildJobResultSummary(BackgroundJob job)
    {
        FileOpResult? result = job.Result;

        if (job.Status == JobStatus.Cancelled)
        {
            // result は work() が実際に開始していれば(RunBatchAsync が WasCancelled=true で
            // 返した)常に設定されている。null は semaphore 待ち中などまだ着手前のキャンセルのみ。
            if (result == null)
                return $"{job.OpName} #{job.ShortId}: Cancelled (not started)";

            var parts = new List<string> { $"{result.Succeeded} succeeded" };
            if (result.Skipped > 0) parts.Add($"{result.Skipped} skipped");
            if (result.Failed > 0) parts.Add($"{result.Failed} failed");
            return $"{job.OpName} #{job.ShortId}: Cancelled ({string.Join(" / ", parts)})";
        }

        if (result == null)
            return $"{job.OpName} #{job.ShortId}: Done";

        if (result.Failed > 0)
            return $"{job.OpName} #{job.ShortId}: {result.Succeeded} done / {result.Failed} failed: {result.FirstError}";
        if (result.Skipped > 0)
            return $"{job.OpName} #{job.ShortId}: {result.Succeeded} done / {result.Skipped} skipped";
        return $"{job.OpName} done: {result.Succeeded} item(s)";
    }

    static async Task RefreshAffectedColumnsAsync(BackgroundJob job)
    {
        var affectedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { job.DestDir };
        foreach (string src in job.SourcePaths)
        {
            string? parent = Path.GetDirectoryName(src.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (parent != null)
                affectedDirs.Add(parent);
        }

        foreach (Pane pane in State.Panes)
        {
            if (!pane.Columns.Exists(c => affectedDirs.Contains(c.Path)))
                continue;

            if (pane.Leap.Active || pane.Search.Active)
            {
                pane.NeedsRefresh = true;
                continue;
            }

            await RefreshPaneNowAsync(pane, affectedDirs);
        }
    }

    // NOTE: Column.CachedChildren/CachedTime are not actually consulted anywhere as a cache-hit
    // check (verified via full-file search — CreateColumn/CreateColumnAsync always re-list from
    // disk unconditionally; the only other writer, RefreshCurrent(), clears them purely as
    // bookkeeping before discarding the Column). So "invalidating" them here would refresh nothing.
    // The real mechanism (mirroring RefreshCurrentAsync) is: save cursor memory for the path, build
    // a brand-new Column via CreateColumnAsync (which re-lists from disk and restores the cursor via
    // ApplyCursorMemory/SeekRestoreTo), carry over Marked state, then swap it into the pane.
    internal static async Task RefreshPaneNowAsync(Pane pane, HashSet<string>? onlyDirs = null)
    {
        int deepestAffected = -1;
        for (int i = 0; i < pane.Columns.Count; i++)
        {
            Column current = pane.Columns[i];
            if (onlyDirs != null && !onlyDirs.Contains(current.Path))
                continue;

            SaveCursorMemory(current);
            Column refreshed = await CreateColumnAsync(current.Path);
            refreshed.Marked = current.Marked;
            if (refreshed.LoadingTask != null)
            {
                // Reconcile only once entries are loaded — and on the sequential main-loop
                // thread (idle tick), never in parallel with Spacebar mark toggling.
                refreshed.NeedsMarkReconcile = true;
            }
            else
            {
                MarkHelper.ReconcileAfterReload(refreshed);
            }

            pane.Columns[i] = refreshed;
            deepestAffected = i;
        }
        if (deepestAffected < 0) deepestAffected = 0;
        await RebuildRightSideAsync(pane, deepestAffected);
    }

    static async Task RefreshActiveJobColumnsAsync()
    {
        List<BackgroundJob> active = JobManager.Snapshot().FindAll(j => j.Status == JobStatus.Running);
        if (active.Count == 0) return;

        if ((DateTime.UtcNow - _lastActiveJobRefreshTime).TotalMilliseconds < 500)
            return;
        _lastActiveJobRefreshTime = DateTime.UtcNow;

        foreach (BackgroundJob job in active)
            await RefreshAffectedColumnsAsync(job);
    }

    internal static Task<Column> CreateColumnAsync(string path)
    {
        Column column = new Column(path);

        // Special case: drive list for Windows
        if (path == "::DRIVES::")
        {
            try
            {
                DriveInfo[] drives = DriveInfo.GetDrives();
                for (int i = 0; i < drives.Length; i++)
                {
                    if (drives[i].IsReady)
                    {
                        column.Entries.Add(drives[i].Name.TrimEnd('\\') + "/");
                    }
                }
            }
            catch
            {
            }
            column.IsLoading = false;
            ApplyCursorMemory(column);
            return Task.FromResult(column);
        }

        column.IsLoading = true;
        column.EntriesRead = 0;
        column.DirectoriesCount = 0;
        column.ReadCts = new CancellationTokenSource();
        CancellationToken ct = column.ReadCts.Token;
        ApplyCursorMemory(column);

        column.LoadingTask = Task.Run(async () =>
        {
            try
            {
                if (!IsValidPath(path)) return;

                int visibleHeight = SafeWindowHeight() - 3;
                int bufferSize = Math.Max(100, visibleHeight + 20);

                List<string> dirs = new List<string>();
                List<string> files = new List<string>();

                // Read directories first (non-blocking enumeration)
                try
                {
                    foreach (var dir in Directory.EnumerateDirectories(path))
                    {
                        ct.ThrowIfCancellationRequested();
                        dirs.Add(dir);
                        if (dirs.Count >= bufferSize)
                            break;
                    }
                    dirs.Sort(StringComparer.OrdinalIgnoreCase);
                    column.DirectoriesCount = dirs.Count;

                    // Add to entries with "/" suffix
                    foreach (var dir in dirs)
                    {
                        column.Entries.Add(Path.GetFileName(dir) + "/");
                        column.EntriesRead++;
                    }
                }
                catch
                {
                    // Permission denied or other errors
                }

                // Read files (up to remaining buffer)
                int remainingBuffer = bufferSize - dirs.Count;
                try
                {
                    foreach (var file in Directory.EnumerateFiles(path))
                    {
                        ct.ThrowIfCancellationRequested();
                        files.Add(file);
                        column.EntriesRead++;
                        if (files.Count >= remainingBuffer)
                            break;
                    }
                    files.Sort(StringComparer.OrdinalIgnoreCase);

                    // Add to entries
                    foreach (var file in files)
                    {
                        column.Entries.Add(Path.GetFileName(file));
                    }
                }
                catch
                {
                    // Permission denied or other errors
                }

                // Continue reading rest in background
                if (!ct.IsCancellationRequested)
                {
                    await Task.Run(() =>
                    {
                        try
                        {
                            List<string> restDirs = new List<string>();
                            int count = 0;
                            foreach (var dir in Directory.EnumerateDirectories(path))
                            {
                                if (ct.IsCancellationRequested)
                                    return;
                                if (count >= dirs.Count)
                                {
                                    restDirs.Add(dir);
                                }
                                count++;
                            }

                            List<string> restFiles = new List<string>();
                            count = 0;
                            foreach (var file in Directory.EnumerateFiles(path))
                            {
                                if (ct.IsCancellationRequested)
                                    return;
                                if (count >= files.Count)
                                {
                                    restFiles.Add(file);
                                }
                                count++;
                            }

                            restDirs.Sort(StringComparer.OrdinalIgnoreCase);
                            restFiles.Sort(StringComparer.OrdinalIgnoreCase);

                            foreach (var dir in restDirs)
                            {
                                if (ct.IsCancellationRequested)
                                    return;
                                column.Entries.Add(Path.GetFileName(dir) + "/");
                                column.EntriesRead++;
                            }

                            foreach (var file in restFiles)
                            {
                                if (ct.IsCancellationRequested)
                                    return;
                                column.Entries.Add(Path.GetFileName(file));
                                column.EntriesRead++;
                            }
                        }
                        catch
                        {
                            // Permission denied or other errors
                        }
                    });
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when navigating away
            }
            finally
            {
                // Add placeholder if folder is empty (allows cursor display and future file creation)
                if (column.Entries.Count == 0)
                {
                    column.Entries.Add("<No file>");
                }
                column.IsLoading = false;
                SeekRestoreTo(column);
                _redrawGate.Request();
            }
        });

        column.CachedTime = DateTime.UtcNow;
        return Task.FromResult(column);
    }

    static bool IsValidPath(string path)
    {
        try
        {
            DirectoryInfo di = new DirectoryInfo(path);
            return di.Exists;
        }
        catch
        {
            return false;
        }
    }

    // Resolves symlinks/junctions to their real target for cycle detection; returns the
    // input path (best-effort normalized) if it isn't a link or resolution fails.
    internal static string ResolveCanonicalPath(string path)
    {
        try
        {
            var target = Directory.ResolveLinkTarget(path, returnFinalTarget: true);
            if (target != null) return target.FullName;
        }
        catch
        {
            // Not a link, or resolution failed for a reason unrelated to "not a link"
            // (e.g. certain path forms make ResolveLinkTarget throw even though the path
            // is real and DirectoryInfo can still normalize it). Fall through below.
        }

        try
        {
            return new DirectoryInfo(path).FullName;
        }
        catch
        {
            return path;
        }
    }

    internal static void MoveUp(Pane pane)
    {
        Column c = pane.Columns[pane.ActiveColumn];
        int visibleHeight = SafeWindowHeight() - 2;

        if (c.Selected > 0)
        {
            c.Selected--;

            // Auto-scroll
            if (c.Selected < c.ScrollOffset)
                c.ScrollOffset = c.Selected;

            UpdateHorizontalScroll(pane);
            MarkNavDirty(pane);
            if (pane.Preview.IsVisible) StartPreviewLoad(pane);
        }
    }

    internal static void MoveDown(Pane pane)
    {
        Column c = pane.Columns[pane.ActiveColumn];
        int visibleHeight = SafeWindowHeight() - 2;

        if (c.Selected + 1 < c.Entries.Count)
        {
            c.Selected++;

            // Auto-scroll
            if (c.Selected >= c.ScrollOffset + visibleHeight)
                c.ScrollOffset = c.Selected - visibleHeight + 1;

            UpdateHorizontalScroll(pane);
            MarkNavDirty(pane);
            if (pane.Preview.IsVisible) StartPreviewLoad(pane);
        }
    }

    // Called on every cursor move. Never touches the disk: cancels in-flight right-side
    // reads, prunes right columns that no longer match the selection, and defers the
    // rebuild to ProcessPendingNavigationAsync (idle tick, after the cursor is stable).
    internal static void MarkNavDirty(Pane pane)
    {
        CancelRightSideReads(pane, pane.ActiveColumn);
        PruneStaleRightColumns(pane, pane.ActiveColumn);
        pane.NavRebuildPending = true;
        pane.LastNavigationTime = DateTime.UtcNow;
    }

    static void PruneStaleRightColumns(Pane pane, int columnIndex)
    {
        string? currentSelection = GetSelectedDirectory(pane, columnIndex);
        Column? right = pane.Columns.Count > columnIndex + 1 ? pane.Columns[columnIndex + 1] : null;
        // Keep a matching right column visible (avoids flicker, cf. commit f11252c);
        // prune when the cursor is on a file or the right column shows a different dir.
        if (right != null && currentSelection == right.Path) return;
        while (pane.Columns.Count > columnIndex + 1)
        {
            Column col = pane.Columns[pane.Columns.Count - 1];
            col.ReadCts?.Cancel();
            SaveCursorMemory(col);
            pane.Columns.RemoveAt(pane.Columns.Count - 1);
        }
    }

    // Runs on the idle tick (sequential with all mark mutation): finishes the deferred
    // mark reconciliation for columns whose reload has completed.
    internal static void ProcessPendingMarkReconciles(Pane pane)
    {
        foreach (Column col in pane.Columns)
        {
            if (col.NeedsMarkReconcile && !col.IsLoading)
            {
                col.NeedsMarkReconcile = false;
                MarkHelper.ReconcileAfterReload(col);
                _redrawGate.Request();
            }
        }
    }

    internal static async Task ProcessPendingNavigationAsync(Pane pane)
    {
        if (!pane.NavRebuildPending) return;
        if ((DateTime.UtcNow - pane.LastNavigationTime).TotalMilliseconds < NavigationDebounceMs) return;
        pane.NavRebuildPending = false;
        await RebuildRightSideAsync(pane, pane.ActiveColumn);
        if (pane.Preview.IsVisible) StartPreviewLoad(pane);
        _redrawGate.Request();
    }

    static void CancelRightSideReads(Pane pane, int columnIndex)
    {
        // Cancel any pending directory reads on columns to the right of current
        // This stops in-progress reads to prevent old data from updating after cursor moves
        for (int i = columnIndex + 1; i < pane.Columns.Count; i++)
        {
            pane.Columns[i].ReadCts?.Cancel();
        }
    }

    static void MoveLeft(Pane pane)
    {
        if (pane.ActiveColumn > 0)
        {
            pane.ActiveColumn--;
            UpdateHorizontalScroll(pane);
            if (pane.Preview.IsVisible) StartPreviewLoad(pane);
        }
    }

    static async Task MoveRightAsync(Pane pane)
    {
        if (pane.ActiveColumn + 1 < pane.Columns.Count)
        {
            // Right column exists, just move to it
            pane.ActiveColumn++;
            UpdateHorizontalScroll(pane);
            if (pane.Preview.IsVisible) StartPreviewLoad(pane);
        }
        else if (pane.ActiveColumn < pane.Columns.Count)
        {
            // No right column - force rebuild to show right pane (e.g., from slow read)
            // This will read the directory of the currently selected entry
            await RebuildRightSideAsync(pane, pane.ActiveColumn);

            // If a new column was created, move to it
            if (pane.ActiveColumn + 1 < pane.Columns.Count)
            {
                pane.ActiveColumn++;
                UpdateHorizontalScroll(pane);
                if (pane.Preview.IsVisible) StartPreviewLoad(pane);
            }
        }
    }

    internal static bool CanSplit(int terminalHeight)
    {
        int available = terminalHeight - 1 /* status row (shared) */ - 1 /* separator row */;
        return available >= MinPaneHeight * 2;
    }

    static void UpdateHorizontalScroll(Pane pane)
    {
        bool previewTakesSlot = pane.Preview.IsVisible && SelectedItemIsFileOrDrive(pane);
        int visibleColumns = Math.Max(1, SafeWindowWidth() / ColumnWidth - (previewTakesSlot ? 1 : 0));

        // Only reserve a right-column slot when on a directory — files never expand rightward,
        // and the old dir column may still be in Columns during async rebuild.
        bool hasRightColumn = !previewTakesSlot && pane.Columns.Count > pane.ActiveColumn + 1;
        int rightReserve = hasRightColumn ? 1 : 0;
        int minHS = Math.Max(0, pane.ActiveColumn - visibleColumns + 1 + rightReserve);
        // maxHS = AC: the only hard constraint is that AC must be visible (HS ≤ AC).
        // No Columns.Count upper bound — allows the rightmost slot to be blank when the
        // directory tree is shallow (Finder behavior: scroll position is preserved).
        int maxHS = pane.ActiveColumn;
        pane.HorizontalScroll = Math.Max(minHS, Math.Min(pane.HorizontalScroll, maxHS));
    }

    // 操作対象の組み立て規則(spec「複数マーク設計」節):
    // フォーカスペインのアクティブカラムに1件以上マークがあればマーク集合(Entries 順・フルパス化)、
    // なければカーソル下1件。他カラム・他ペインのマークは常に無視する。
    // internal: Zurari.Tests から InternalsVisibleTo 経由で直接呼び出すため。
    internal static List<string> GetOperationTargets(Pane pane)
    {
        Column active = pane.Columns[pane.ActiveColumn];
        var result = new List<string>();

        if (active.Marked.Count > 0)
        {
            // Entries の表示順を保つため、Marked(順序無保証の HashSet)ではなく
            // Entries を走査して Marked に含まれるものだけを拾う。
            foreach (string name in active.Entries)
            {
                if (active.Marked.Contains(name))
                    result.Add(BuildFullPath(active.Path, name));
            }
            return result;
        }

        if (active.Entries.Count > 0 && active.Selected >= 0 && active.Selected < active.Entries.Count)
        {
            string name = active.Entries[active.Selected];
            if (name != "<No file>")
                result.Add(BuildFullPath(active.Path, name));
        }

        return result;
    }

    // エントリ名の末尾 "/" (ディレクトリ)はフルパス側にも残す。
    // ExecuteDeleteAsync 等が isDir 判定・Marked 復元(エントリ名再構成)に使うため。
    static string BuildFullPath(string columnPath, string entryName)
    {
        if (columnPath == "::DRIVES::")
            return entryName.TrimEnd('/') + "\\";
        string combined = Path.Combine(columnPath, entryName.TrimEnd('/'));
        return entryName.EndsWith("/") ? combined + "/" : combined;
    }

    static readonly char[] WindowsInvalidDirNameChars = { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };

    // Control characters are already rejected by the caller (HandleTextInputDialogKey's
    // existing `!char.IsControl(key.KeyChar)` guard) before this runs, so this only needs to
    // cover each platform's printable-character restrictions.
    internal static bool IsValidDirNameChar(char c)
    {
        if (IsWindows())
            return Array.IndexOf(WindowsInvalidDirNameChars, c) < 0;
        return c != '/';
    }

    static readonly HashSet<string> WindowsReservedDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    // Checks the Windows-specific rules that can't be caught per-keystroke because they depend
    // on the whole name: reserved device names (with or without an extension) and a trailing
    // dot/space (Windows silently strips/rejects these).
    internal static bool IsInvalidDirectoryName(string name, out string reason)
    {
        if (IsWindows())
        {
            if (name.EndsWith('.') || name.EndsWith(' '))
            {
                reason = "cannot end with a dot or space";
                return true;
            }
            int dotIndex = name.IndexOf('.');
            string baseName = dotIndex >= 0 ? name.Substring(0, dotIndex) : name;
            if (WindowsReservedDirNames.Contains(baseName))
            {
                reason = "is a reserved name";
                return true;
            }
        }
        reason = "";
        return false;
    }

    internal static bool DirectoryNameCollides(List<string> entries, string name)
    {
        foreach (string entry in entries)
        {
            string entryName = entry.EndsWith("/") ? entry.Substring(0, entry.Length - 1) : entry;
            if (string.Equals(entryName, name, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    static bool SelectedIsDeletable(Pane pane, out Column col, out string name)
    {
        col = pane.Columns[pane.ActiveColumn];
        name = "";
        if (col.Path == "::DRIVES::") return false;
        if (col.Entries.Count == 0) return false;
        if (col.Selected < 0 || col.Selected >= col.Entries.Count) return false;
        name = col.Entries[col.Selected];
        if (name == "<No file>") return false;
        return true;
    }

    static void RequestDeleteSelected(Pane pane)
    {
        Column activeCol = pane.Columns[pane.ActiveColumn];
        if (activeCol.Path == "::DRIVES::") return;

        List<string> targets = GetOperationTargets(pane);
        if (targets.Count == 0) return;

        if (BusyWarningHelper.AnyOverlapsBusyPaths(targets))
        {
            ShowConfirm("Busy Warning", "An active job is using this path. Continue anyway?", proceed =>
            {
                if (proceed == true)
                    ShowDeleteConfirm(pane, targets);
            });
            return;
        }

        ShowDeleteConfirm(pane, targets);
    }

    static void ShowDeleteConfirm(Pane pane, List<string> targets)
    {
        string message = targets.Count == 1
            ? $"Delete {Path.GetFileName(targets[0].TrimEnd('/'))}? (to Recycle Bin)"
            : $"Delete {targets.Count} item(s)? (to Recycle Bin)";

        ShowConfirm(
            "Delete",
            message,
            async result =>
            {
                if (result != true) return;
                await ExecuteDeleteAsync(pane, targets);
            });
    }

    static async Task ExecuteDeleteAsync(Pane pane, List<string> targets)
    {
        Column activeCol = pane.Columns[pane.ActiveColumn];
        int okCount = 0, failCount = 0;
        string? firstError = null;

        foreach (string target in targets)
        {
            bool isDir = target.EndsWith("/");
            string fullPath = target.TrimEnd('/');
            string result;
            string message = "";

            bool sentToRecycleBin = false;
            if (IsWindows())
            {
                sentToRecycleBin = RecycleBin.TrySend(fullPath, out string rbError);
                if (!sentToRecycleBin)
                    message = rbError;
            }

            if (sentToRecycleBin)
            {
                result = "OK";
            }
            else
            {
                try
                {
                    if (isDir)
                        Directory.Delete(fullPath, recursive: true);
                    else
                        File.Delete(fullPath);
                    result = "OK";
                }
                catch (Exception ex)
                {
                    result = "FAIL";
                    message = ex.Message;
                }
            }

            OperationLog.Append(new OpLogEntry(DateTime.UtcNow, null, "DELETE", result, fullPath, "", message));

            if (result == "OK")
            {
                okCount++;
                // Marked のキーは Entries の文字列と完全一致(ディレクトリは "/" サフィックス付き)。
                // フルパスの末尾から同じ形式のエントリ名を復元する。
                string entryName = Path.GetFileName(fullPath) + (isDir ? "/" : "");
                activeCol.Marked.Remove(entryName);
            }
            else
            {
                failCount++;
                firstError ??= message;
            }
        }

        _lastErrorMessage = failCount == 0
            ? $"Delete complete: {okCount} item(s)"
            : $"Delete: {okCount} done / {failCount} failed: {firstError}";

        await RefreshCurrentAsync(pane);
    }

    static void RequestRenameSelected(Pane pane)
    {
        if (!SelectedIsDeletable(pane, out Column col, out string name))
            return;

        string targetPath = BuildFullPath(col.Path, name);
        if (BusyWarningHelper.AnyOverlapsBusyPaths(new List<string> { targetPath }))
        {
            ShowConfirm("Busy Warning", "An active job is using this path. Continue anyway?", proceed =>
            {
                if (proceed == true)
                    ShowRenameInput(pane, col, name);
            });
            return;
        }

        ShowRenameInput(pane, col, name);
    }

    static void ShowRenameInput(Pane pane, Column col, string name)
    {
        bool isDir = name.EndsWith("/");
        string currentName = isDir ? name.TrimEnd('/') : name;

        ShowTextInput(
            "Rename",
            currentName,
            async newNameResult =>
            {
                if (newNameResult == null) return;
                string newName = newNameResult.Trim();
                if (newName.Length == 0 || newName == currentName) return;
                await ExecuteRenameAsync(pane, col, name, currentName, newName, isDir);
            });
    }

    static async Task ExecuteRenameAsync(Pane pane, Column col, string originalEntry, string currentName, string newName, bool isDir)
    {
        string oldFullPath = Path.Combine(col.Path, currentName);
        string newFullPath = Path.Combine(col.Path, newName);
        string result;
        string message = "";

        try
        {
            if (isDir)
                Directory.Move(oldFullPath, newFullPath);
            else
                File.Move(oldFullPath, newFullPath);
            result = "OK";
            _lastErrorMessage = null;
        }
        catch (Exception ex)
        {
            result = "FAIL";
            message = ex.Message;
            _lastErrorMessage = $"Rename failed: {ex.Message}";
        }

        OperationLog.Append(new OpLogEntry(DateTime.UtcNow, null, "RENAME", result, oldFullPath, newFullPath, message));

        if (result == "OK")
        {
            // RefreshCurrentAsync replaces pane.Columns[pane.ActiveColumn] with a brand-new
            // Column instance, so setting col.RestoreTo on the pre-rename Column would not
            // propagate. Build the refreshed column directly and set RestoreTo on it instead.
            Column refreshed = await CreateColumnAsync(col.Path);
            if (refreshed.LoadingTask != null) await refreshed.LoadingTask;
            refreshed.RestoreTo = isDir ? newName + "/" : newName;
            SeekRestoreTo(refreshed);
            refreshed.Marked = col.Marked;
            MarkHelper.ReconcileAfterReload(refreshed);
            pane.Columns[pane.ActiveColumn] = refreshed;
            await RebuildRightSideAsync(pane, pane.ActiveColumn);
        }
        else
        {
            await RefreshCurrentAsync(pane);
        }
    }

    static async Task EnterAsync(Pane pane)
    {
        Column c = pane.Columns[pane.ActiveColumn];

        if (c.Entries.Count == 0)
            return;

        string name = c.Entries[c.Selected];

        if (!name.EndsWith("/"))
            return;

        if (pane.ActiveColumn + 1 < pane.Columns.Count)
        {
            pane.ActiveColumn++;
        }
        else
        {
            pane.ActiveColumn++;
        }

        UpdateHorizontalScroll(pane);
        CancelPreviewLoad(pane);
        if (pane.Preview.IsVisible) StartPreviewLoad(pane);
        await RebuildRightSideAsync(pane, pane.ActiveColumn - 1);

        string? enteredPath = GetSelectedDirectory(pane, pane.ActiveColumn - 1);
        if (enteredPath != null)
            RecordHistoryVisit(enteredPath);
    }

    static async Task OpenFileAsync(Pane pane)
    {
        Column c = pane.Columns[pane.ActiveColumn];

        if (c.Entries.Count == 0)
            return;

        string name = c.Entries[c.Selected];

        // Only open files, not directories
        if (name.EndsWith("/"))
            return;

        string filePath = GetCurrentFullPath(pane);
        await LaunchFileAsync(filePath);
    }

    static async Task LaunchFileAsync(string filePath)
    {
        try
        {
            if (IsWindows())
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = filePath,
                    UseShellExecute = true
                });
            }
            else if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                System.Runtime.InteropServices.OSPlatform.OSX))
            {
                Process.Start("/usr/bin/open", filePath);
            }
            else
            {
                // Linux and other Unix-like systems
                Process.Start("xdg-open", filePath);
            }
            _lastErrorMessage = null;
        }
        catch
        {
            _lastErrorMessage = "Cannot open file";
        }

        await Task.CompletedTask;
    }

    static async Task ShowContextMenuAsync(Pane pane)
    {
        // Windows only - show context menu
        if (!IsWindows())
            return;

        Column c = pane.Columns[pane.ActiveColumn];

        if (c.Entries.Count == 0)
            return;

        string name = c.Entries[c.Selected];


        string filePath = GetCurrentFullPath(pane);
        await LaunchContextMenuAsync(filePath);
    }

    static async Task LaunchContextMenuAsync(string filePath)
    {
        // Windows only
        if (!IsWindows())
        {
            await Task.CompletedTask;
            return;
        }

        try
        {
            string? ctxmenuPath = null;

            // Try to find ctxmenu.exe in application directory first
            string appDir = AppContext.BaseDirectory;
            string appDirPath = Path.Combine(appDir, "ctxmenu.exe");
            if (File.Exists(appDirPath))
            {
                ctxmenuPath = appDirPath;
            }
            else
            {
                // Try to find in PATH - just use the name and let Windows search PATH
                ctxmenuPath = "ctxmenu.exe";
            }

            // Use ArgumentList so .NET handles quoting correctly — avoids the "C:\" trap
            // where a trailing backslash before a closing quote is parsed as an escaped quote.
            var psi = new ProcessStartInfo(ctxmenuPath);
            psi.ArgumentList.Add(filePath);
            Process.Start(psi);
            _lastErrorMessage = null;
        }
        catch
        {
            _lastErrorMessage = "ctxmenu.exe not found";
        }

        await Task.CompletedTask;
    }

    static void Parent(Pane pane)
    {
        if (pane.ActiveColumn == 0)
            return;

        pane.ActiveColumn--;
        UpdateHorizontalScroll(pane);
        if (pane.Preview.IsVisible) StartPreviewLoad(pane);
        RecordHistoryVisit(pane.Columns[pane.ActiveColumn].Path);
    }

    static void SaveCursorMemory(Column col)
    {
        if (col.Selected < 0 || col.Selected >= col.Entries.Count) return;
        string entry = col.Entries[col.Selected];
        if (entry == "<No file>") return;
        if (!_cursorMemory.ContainsKey(col.Path))
        {
            if (_cursorMemoryKeys.Count >= CursorMemoryLimit)
            {
                _cursorMemory.Remove(_cursorMemoryKeys[0]);
                _cursorMemoryKeys.RemoveAt(0);
            }
            _cursorMemoryKeys.Add(col.Path);
        }
        _cursorMemory[col.Path] = entry;
    }

    static void ApplyCursorMemory(Column col)
    {
        if (!_cursorMemory.TryGetValue(col.Path, out string? target)) return;
        col.RestoreTo = target;
        SeekRestoreTo(col);
    }

    static void SeekRestoreTo(Column col)
    {
        if (col.RestoreTo == null) return;
        // Not loaded yet — safe no-op. col.RestoreTo survives this call, and the loader's
        // own `finally` (after entries are fully populated) calls SeekRestoreTo again —
        // that second call is the actual retry, not a loop here.
        if (col.Entries.Count == 0) return;

        string target = col.RestoreTo;
        for (int i = 0; i < col.Entries.Count; i++)
        {
            if (col.Entries[i] == target)
            {
                col.Selected = i;
                col.RestoreTo = null;
                return;
            }
        }

        // Remembered entry is gone (deleted/renamed/moved elsewhere). Land on the
        // nearest neighbor within the same block (directories vs. files, since those
        // are sorted and displayed as separate groups) instead of jumping to the top.
        col.RestoreTo = null;
        col.Selected = NavigationHelper.FindNearestIndex(col.Entries, col.DirectoriesCount, target, col.Selected);
        SetMissingEntryMessage(col, target); // snapshot taken AFTER Selected is finalized
    }

    static void SetMissingEntryMessage(Column col, string target)
    {
        string name = target.EndsWith("/") ? target[..^1] : target;
        _missingEntryMessage = $"missing: {name}";
        _missingEntryColumn = col;
        _missingEntrySelected = col.Selected;
    }

    static void RefreshCurrent(Pane pane)
    {
        Column c = pane.Columns[pane.ActiveColumn];
        c.CachedChildren.Clear();
        c.CachedTime = DateTime.UtcNow.AddMilliseconds(-CacheExpireMs - 1);
        c.ReadCts?.Cancel();
        _ = RefreshCurrentAsync(pane);
    }

    static async Task RefreshCurrentAsync(Pane pane)
    {
        Column current = pane.Columns[pane.ActiveColumn];
        string currentPath = current.Path;

        SaveCursorMemory(current);

        Column refreshed = await CreateColumnAsync(currentPath);
        if (refreshed.LoadingTask != null)
            await refreshed.LoadingTask;

        // マーク状態は Column インスタンスではなく「そのディレクトリの表示」に属するため、
        // 差し替え前に引き継いでから、新しい Entries に存在しない名前を除去する。
        refreshed.Marked = current.Marked;
        MarkHelper.ReconcileAfterReload(refreshed);

        pane.Columns[pane.ActiveColumn] = refreshed;

        await RebuildRightSideAsync(pane, pane.ActiveColumn);
        _redrawGate.Request();
    }

    static async Task RecomputeMatchesAsync(Pane pane)
    {
        // Cancel any in-progress scan
        pane.Search.SearchCts?.Cancel();
        pane.Search.SearchCts?.Dispose();
        var cts = new CancellationTokenSource();
        pane.Search.SearchCts = cts;

        string query = pane.Search.Query;
        bool regexMode = pane.Search.RegexMode;
        int anchor = pane.Search.Anchor;
        Column col = pane.Columns[pane.ActiveColumn];

        lock (_searchLock)
        {
            pane.Search.Matches.Clear();
            pane.Search.SearchDone = false;
            pane.Search.NeedsRecompute = false;
        }

        await Task.Run(() =>
        {
            for (int i = 0; i < col.Entries.Count; i++)
            {
                if (cts.Token.IsCancellationRequested) return;
                string name = col.Entries[i].TrimEnd('/');
                if (SearchHelper.MatchesSearchQuery(name, query, regexMode, _migemo))
                {
                    lock (_searchLock) { pane.Search.Matches.Add(i); }
                }
            }

            if (cts.Token.IsCancellationRequested) return;

            lock (_searchLock)
            {
                pane.Search.MatchIndex = SearchHelper.FindNearestMatchIndex(pane.Search.Matches, anchor);
                if (pane.Search.Matches.Count > 0)
                    col.Selected = pane.Search.Matches[pane.Search.MatchIndex];
                pane.Search.SearchDone = true;
            }
        }, cts.Token);
    }

    static void EnterSearchMode(Pane pane)
    {
        Column col = pane.Columns[pane.ActiveColumn];
        pane.Search.Active = true;
        pane.Search.Anchor = col.Selected;
        pane.Search.Query = "";
        pane.Search.RegexMode = false;
        lock (_searchLock)
        {
            pane.Search.Matches.Clear();
            pane.Search.SearchDone = true;
            pane.Search.NeedsRecompute = false;
        }
    }

    static async Task ExitSearchMode(Pane pane, bool restoreCursor)
    {
        pane.Search.SearchCts?.Cancel();
        pane.Search.SearchCts?.Dispose();
        pane.Search.SearchCts = null;
        pane.Search.Active = false;
        if (restoreCursor)
            pane.Columns[pane.ActiveColumn].Selected = pane.Search.Anchor;
        lock (_searchLock)
        {
            pane.Search.Matches.Clear();
            pane.Search.SearchDone = true;
        }
        if (pane.NeedsRefresh)
        {
            pane.NeedsRefresh = false;
            await RefreshPaneNowAsync(pane);
        }
    }

    static void EnterLeapMode(Pane pane)
    {
        Column col = pane.Columns[pane.ActiveColumn];
        pane.Leap.Active = true;
        pane.Leap.Buffer = "";
        pane.Leap.RootDir = col.Path;
        pane.Leap.RootColumnIndex = pane.ActiveColumn;
        pane.Leap.RootCursorIndex = col.Selected;
        pane.Leap.DirStack.Clear();
        pane.Leap.NeedsRecompute = false;
        pane.Leap.NoMatch = false;
        pane.Leap.VisibleIndices.Clear();
        pane.Leap.ColumnScrollOffset = 0;
        pane.Leap.PreviewWasVisible = pane.Preview.IsVisible;
        if (pane.Preview.IsVisible)
        {
            CancelPreviewLoad(pane);
            pane.Preview.IsVisible = false;
        }
        UpdateHorizontalScroll(pane);
        ApplyLeapFilter(pane); // empty filter = all entries visible
    }

    static async Task ExitLeapModeAsync(Pane pane, bool restoreCursor)
    {
        pane.Leap.Active = false;
        pane.Leap.NoMatch = false;
        if (restoreCursor)
        {
            pane.ActiveColumn = pane.Leap.RootColumnIndex;
            pane.Columns[pane.ActiveColumn].Selected = pane.Leap.RootCursorIndex;
        }
        pane.Preview.IsVisible = pane.Leap.PreviewWasVisible;
        UpdateHorizontalScroll(pane);
        await RebuildRightSideAsync(pane, pane.ActiveColumn);
        if (pane.Preview.IsVisible) StartPreviewLoad(pane);
        if (pane.NeedsRefresh)
        {
            pane.NeedsRefresh = false;
            await RefreshPaneNowAsync(pane);
        }
    }

    static async Task HandleLeapKeyAsync(Pane pane, ConsoleKeyInfo key)
    {
        LeapState leap = pane.Leap;
        Column col = pane.Columns[pane.ActiveColumn];

        if (key.Key == ConsoleKey.F3)
        {
            await ExitLeapModeAsync(pane, restoreCursor: false);
            return;
        }

        if (key.Key == ConsoleKey.Escape)
        {
            await ExitLeapModeAsync(pane, restoreCursor: true);
            return;
        }

        // Ctrl+Enter: land here and exit (no open, no enter)
        if (key.Key == ConsoleKey.Enter && (key.Modifiers & ConsoleModifiers.Control) != 0)
        {
            await ExitLeapModeAsync(pane, restoreCursor: false);
            return;
        }

        // Enter: navigate into dir or open file
        if (key.Key == ConsoleKey.Enter)
        {
            if (col.Entries.Count == 0) return;
            string name = col.Entries[col.Selected];
            if (name.EndsWith("/"))
                await EnterDirInLeapAsync(pane);
            else
            {
                string filePath = GetCurrentFullPath(pane);
                await ExitLeapModeAsync(pane, restoreCursor: false);
                await LaunchFileAsync(filePath);
            }
            return;
        }

        // Right: enter dir or select file (no open)
        if (key.Key == ConsoleKey.RightArrow)
        {
            if (col.Entries.Count == 0) return;
            string name = col.Entries[col.Selected];
            if (name.EndsWith("/"))
                await EnterDirInLeapAsync(pane);
            else
                await ExitLeapModeAsync(pane, restoreCursor: false);
            return;
        }

        // Left: go to parent (strip local filter + "/")
        if (key.Key == ConsoleKey.LeftArrow)
        {
            int lastSlash = leap.Buffer.LastIndexOf('/');
            if (lastSlash >= 0)
                leap.Buffer = leap.Buffer[..lastSlash];
            await LeapStepUpAsync(pane);
            return;
        }

        // Down / Ctrl+N
        if (key.Key == ConsoleKey.DownArrow ||
            (key.Key == ConsoleKey.N && (key.Modifiers & ConsoleModifiers.Control) != 0))
        {
            await MoveLeapCursorDownAsync(pane);
            return;
        }

        // Up / Ctrl+P
        if (key.Key == ConsoleKey.UpArrow ||
            (key.Key == ConsoleKey.P && (key.Modifiers & ConsoleModifiers.Control) != 0))
        {
            await MoveLeapCursorUpAsync(pane);
            return;
        }

        // Backspace
        if (key.Key == ConsoleKey.Backspace)
        {
            if (leap.Buffer.Length == 0) return;
            char removed = leap.Buffer[^1];
            leap.Buffer = leap.Buffer[..^1];
            if (removed == '/')
                await LeapStepUpAsync(pane);
            else
            {
                leap.NeedsRecompute = true;
                leap.LastInputTime = DateTime.UtcNow;
            }
            return;
        }

        // Ctrl+U: clear local filter, stay in current dir
        if (key.Key == ConsoleKey.U && (key.Modifiers & ConsoleModifiers.Control) != 0)
        {
            int lastSlash = leap.Buffer.LastIndexOf('/');
            leap.Buffer = lastSlash >= 0 ? leap.Buffer[..(lastSlash + 1)] : "";
            leap.NoMatch = false;
            ApplyLeapFilter(pane);
            await RebuildRightSideAsync(pane, pane.ActiveColumn);
            return;
        }

        // Ctrl+K: return to leap root
        if (key.Key == ConsoleKey.K && (key.Modifiers & ConsoleModifiers.Control) != 0)
        {
            await GoLeapRootAsync(pane);
            return;
        }

        // "/": enter the currently selected directory (same as Right/Enter on a dir)
        if (key.KeyChar == '/')
        {
            if (col.Entries.Count > 0 && col.Selected >= 0 && col.Selected < col.Entries.Count
                && col.Entries[col.Selected].EndsWith("/"))
            {
                await EnterDirInLeapAsync(pane);
            }
            return;
        }

        // Printable character: append to buffer
        if (!char.IsControl(key.KeyChar))
        {
            leap.Buffer += key.KeyChar;
            leap.NeedsRecompute = true;
            leap.LastInputTime = DateTime.UtcNow;
        }
    }

    static async Task EnterDirInLeapAsync(Pane pane)
    {
        Column col = pane.Columns[pane.ActiveColumn];
        if (col.Entries.Count == 0 || col.Selected >= col.Entries.Count) return;
        string name = col.Entries[col.Selected];
        if (!name.EndsWith("/")) return;

        // Append "/" to buffer (local filter stays as the part that matched)
        pane.Leap.Buffer += "/";
        pane.Leap.DirStack.Add((col.Path, pane.Leap.Buffer.Length));

        // Navigate into the directory (same as EnterAsync, but preview stays frozen during leap)
        pane.ActiveColumn++;
        UpdateHorizontalScroll(pane);
        await RebuildRightSideAsync(pane, pane.ActiveColumn - 1);

        // Reset leap filter state for new directory
        pane.Leap.NoMatch = false;
        pane.Leap.ColumnScrollOffset = 0;
        ApplyLeapFilter(pane); // empty local filter = all entries visible
        await TryLeapAutoEnterAsync(pane); // cascade through further singleton directories
    }

    static async Task TryLeapAutoEnterAsync(Pane pane)
    {
        if (pane.Leap.NoMatch || pane.Leap.VisibleIndices.Count != 1) return;
        Column ac = pane.Columns[pane.ActiveColumn];
        if (ac.Entries.Count > 0 && ac.Selected < ac.Entries.Count
            && ac.Entries[ac.Selected].EndsWith("/"))
            await EnterDirInLeapAsync(pane);
    }

    static async Task LeapStepUpAsync(Pane pane)
    {
        if (pane.ActiveColumn <= pane.Leap.RootColumnIndex) return;

        if (pane.Leap.DirStack.Count > 0)
            pane.Leap.DirStack.RemoveAt(pane.Leap.DirStack.Count - 1);

        pane.ActiveColumn--;
        UpdateHorizontalScroll(pane);
        await RebuildRightSideAsync(pane, pane.ActiveColumn);
        // Preview stays frozen during leap; updated on exit

        pane.Leap.NoMatch = false;
        pane.Leap.ColumnScrollOffset = 0;
        ApplyLeapFilter(pane);
    }

    static async Task GoLeapRootAsync(Pane pane)
    {
        while (pane.ActiveColumn > pane.Leap.RootColumnIndex)
            pane.ActiveColumn--;
        pane.Leap.DirStack.Clear();
        pane.Leap.Buffer = "";
        UpdateHorizontalScroll(pane);
        await RebuildRightSideAsync(pane, pane.ActiveColumn);
        // Preview stays frozen during leap; updated on exit
        pane.Leap.NoMatch = false;
        pane.Leap.ColumnScrollOffset = 0;
        ApplyLeapFilter(pane);
    }

    static Task MoveLeapCursorDownAsync(Pane pane)
    {
        List<int> vis = pane.Leap.VisibleIndices;
        if (vis.Count == 0) return Task.CompletedTask;
        int cur = vis.IndexOf(pane.Columns[pane.ActiveColumn].Selected);
        int next = Math.Min(cur + 1, vis.Count - 1);
        pane.Columns[pane.ActiveColumn].Selected = vis[next];
        MarkNavDirty(pane);
        return Task.CompletedTask;
    }

    static Task MoveLeapCursorUpAsync(Pane pane)
    {
        List<int> vis = pane.Leap.VisibleIndices;
        if (vis.Count == 0) return Task.CompletedTask;
        int cur = vis.IndexOf(pane.Columns[pane.ActiveColumn].Selected);
        if (cur < 0) cur = vis.Count;
        int prev = Math.Max(cur - 1, 0);
        pane.Columns[pane.ActiveColumn].Selected = vis[prev];
        MarkNavDirty(pane);
        return Task.CompletedTask;
    }

    static void ApplyLeapFilter(Pane pane)
    {
        LeapState leap = pane.Leap;
        Column col = pane.Columns[pane.ActiveColumn];

        // Extract local filter = everything after the last "/"
        int lastSlash = leap.Buffer.LastIndexOf('/');
        string localFilter = lastSlash >= 0 ? leap.Buffer[(lastSlash + 1)..] : leap.Buffer;

        leap.VisibleIndices.Clear();

        if (localFilter.Length == 0)
        {
            // Empty filter: all entries visible
            for (int i = 0; i < col.Entries.Count; i++)
                leap.VisibleIndices.Add(i);
            leap.NoMatch = false;
            if (col.Entries.Count > 0 && (col.Selected < 0 || col.Selected >= col.Entries.Count))
                col.Selected = 0;
            leap.ColumnScrollOffset = 0;
            return;
        }

        string[] segments = SplitLeapSegments(localFilter);

        var prefixMatches = new List<int>();
        var substringMatches = new List<int>();

        for (int i = 0; i < col.Entries.Count; i++)
        {
            string entry = col.Entries[i];
            if (!LeapAllSegmentsMatch(entry, segments)) continue;

            // Priority: prefix match on first segment > substring
            if (entry.StartsWith(segments[0].Replace("\\ ", " "), StringComparison.OrdinalIgnoreCase))
                prefixMatches.Add(i);
            else
                substringMatches.Add(i);
        }

        // Migemo fallback: only when no regular matches
        var migemoMatches = new List<int>();
        if (prefixMatches.Count == 0 && substringMatches.Count == 0 && _migemo?.IsAvailable == true)
        {
            foreach (string seg in segments)
            {
                string pattern = _migemo.ExpandPattern(seg.Replace("\\ ", " "));
                if (string.IsNullOrEmpty(pattern)) continue;
                try
                {
                    var rx = new System.Text.RegularExpressions.Regex(
                        pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    for (int i = 0; i < col.Entries.Count; i++)
                        if (!migemoMatches.Contains(i) && rx.IsMatch(col.Entries[i]))
                            migemoMatches.Add(i);
                }
                catch { }
            }
        }

        var added = new HashSet<int>();
        foreach (int i in prefixMatches) { if (added.Add(i)) leap.VisibleIndices.Add(i); }
        foreach (int i in substringMatches) { if (added.Add(i)) leap.VisibleIndices.Add(i); }
        foreach (int i in migemoMatches) { if (added.Add(i)) leap.VisibleIndices.Add(i); }

        if (leap.VisibleIndices.Count == 0)
        {
            leap.NoMatch = true; // Mode B: keep buffer, show (no match) in bar
            return;
        }

        leap.NoMatch = false;
        col.Selected = leap.VisibleIndices[0]; // cursor to highest-priority match
        leap.ColumnScrollOffset = 0;
    }

    static bool LeapAllSegmentsMatch(string entry, string[] segments)
    {
        foreach (string seg in segments)
            if (!entry.Contains(seg.Replace("\\ ", " "), StringComparison.OrdinalIgnoreCase))
                return false;
        return true;
    }

    static string[] SplitLeapSegments(string filter)
    {
        var segments = new List<string>();
        var current = new System.Text.StringBuilder();
        for (int i = 0; i < filter.Length; i++)
        {
            if (filter[i] == '\\' && i + 1 < filter.Length && filter[i + 1] == ' ')
            {
                current.Append("\\ ");
                i++;
            }
            else if (filter[i] == ' ')
            {
                if (current.Length > 0) { segments.Add(current.ToString()); current.Clear(); }
            }
            else
                current.Append(filter[i]);
        }
        if (current.Length > 0) segments.Add(current.ToString());
        return segments.Count > 0 ? segments.ToArray() : new[] { filter };
    }

    static void ShowConfirm(string title, string message, Action<bool?> onResult)
    {
        State.Dialog = new ConfirmDialogState
        {
            Title = title,
            Message = message,
            OnResult = onResult,
        };
    }

    static void ShowTextInput(string title, string initialValue, Action<string?> onResult,
        Func<char, bool>? charFilter = null, string hintText = TextInputDialogState.DefaultHintText)
    {
        State.Dialog = new TextInputDialogState
        {
            Title = title,
            Buffer = initialValue,
            CursorPos = initialValue.Length,
            OnResult = onResult,
            CharFilter = charFilter,
            HintText = hintText,
        };
    }

    static void ShowListSelect(string title, IReadOnlyList<string> items, Action<string?> onResult)
    {
        var allItems = new List<string>(items);
        State.Dialog = new ListSelectDialogState
        {
            Title = title,
            AllItems = allItems,
            FilteredItems = new List<string>(allItems),
            FilterQuery = "",
            Selected = 0,
            OnResult = onResult,
        };
    }

    static void ShowJobList()
    {
        var dialog = new JobListDialogState
        {
            Title = "Jobs",
            Snapshot = JobManager.Snapshot(),
            Selected = 0,
        };
        State.Dialog = dialog;
    }

    static Task<ConflictDecision?> RequestConflictAsync(int jobShortId, string opName, string destName)
    {
        var tcs = new TaskCompletionSource<ConflictDecision?>();

        lock (_dialogQueueLock)
        {
            if (_jobStickyChoice.TryGetValue(jobShortId, out ConflictResolution sticky))
            {
                tcs.SetResult(new ConflictDecision(sticky));
                return tcs.Task;
            }

            _dialogRequestQueue.Enqueue(() => ShowConflictChoiceDialog(jobShortId, opName, destName, tcs));
        }

        return tcs.Task;
    }

    // 衝突選択の一覧を表示する。リネーム入力で Esc された場合はジョブ全体をキャンセルせず、
    // この一覧に戻ってやり直させる(スキップしたいなら「スキップ」を明示的に選ばせる)。
    // 一覧自体で Esc された場合のみジョブキャンセル。
    static void ShowConflictChoiceDialog(int jobShortId, string opName, string destName, TaskCompletionSource<ConflictDecision?> tcs)
    {
        var options = new List<string> { "Overwrite", "Overwrite All", "Skip", "Skip All", "Rename" };
        ShowListSelect($"#{jobShortId} {opName} Conflict: \"{destName}\"", options, choice =>
        {
            switch (choice)
            {
                case null:
                    tcs.SetResult(null); // Esc = ジョブキャンセル
                    break;
                case "Overwrite":
                    tcs.SetResult(new ConflictDecision(ConflictResolution.Overwrite));
                    break;
                case "Overwrite All":
                    lock (_dialogQueueLock) { _jobStickyChoice[jobShortId] = ConflictResolution.Overwrite; }
                    tcs.SetResult(new ConflictDecision(ConflictResolution.Overwrite));
                    break;
                case "Skip":
                    tcs.SetResult(new ConflictDecision(ConflictResolution.Skip));
                    break;
                case "Skip All":
                    lock (_dialogQueueLock) { _jobStickyChoice[jobShortId] = ConflictResolution.Skip; }
                    tcs.SetResult(new ConflictDecision(ConflictResolution.Skip));
                    break;
                case "Rename":
                    ShowTextInput($"New name (was: \"{destName}\")", destName, newName =>
                    {
                        if (newName == null)
                        {
                            // Esc: リネーム入力のみ取り消し、衝突選択に戻る(ジョブはキャンセルしない)
                            ShowConflictChoiceDialog(jobShortId, opName, destName, tcs);
                            return;
                        }
                        tcs.SetResult(new ConflictDecision(ConflictResolution.Rename, newName));
                    });
                    break;
            }
        });
    }

    // 開いているダイアログを閉じ、結果コールバックを呼ぶ共通クローズ処理。
    // 呼び出し元(HandleDialogKeyAsync)が確定/キャンセル時に呼ぶ。
    static void CloseConfirmDialog(ConfirmDialogState dialog, bool? result)
    {
        State.Dialog = null;
        dialog.OnResult(result);
    }

    static void CloseTextInputDialog(TextInputDialogState dialog, string? result)
    {
        State.Dialog = null;
        dialog.OnResult(result);
    }

    static void CloseListSelectDialog(ListSelectDialogState dialog, string? result)
    {
        State.Dialog = null;
        dialog.OnResult(result);
    }

    static Task HandleDialogKeyAsync(Pane pane, ConsoleKeyInfo key)
    {
        DialogState? dialog = State.Dialog;
        if (dialog == null) return Task.CompletedTask;

        switch (dialog)
        {
            case ConfirmDialogState confirm:
                HandleConfirmDialogKey(confirm, key);
                break;

            case TextInputDialogState textInput:
                HandleTextInputDialogKey(textInput, key);
                break;

            case ListSelectDialogState listSelect:
                HandleListSelectDialogKey(listSelect, key);
                break;

            case JobListDialogState jobList:
                HandleJobListDialogKey(jobList, key);
                break;
        }

        return Task.CompletedTask;
    }

    static void HandleJobListDialogKey(JobListDialogState dialog, ConsoleKeyInfo key)
    {
        if (key.Key == ConsoleKey.Escape)
        {
            State.Dialog = null;
            return;
        }

        if (key.Key == ConsoleKey.UpArrow)
        {
            if (dialog.Selected > 0) dialog.Selected--;
            return;
        }

        if (key.Key == ConsoleKey.DownArrow)
        {
            if (dialog.Selected < dialog.Snapshot.Count - 1) dialog.Selected++;
            return;
        }

        if (key.Key == ConsoleKey.Enter || key.Key == ConsoleKey.Delete)
        {
            if (dialog.Selected < 0 || dialog.Selected >= dialog.Snapshot.Count) return;

            BackgroundJob target = dialog.Snapshot[dialog.Selected];
            if (target.Status != JobStatus.Pending && target.Status != JobStatus.Running) return;

            int shortId = target.ShortId;
            ShowConfirm("Cancel Job", $"Cancel #{shortId} {target.OpName}?", confirmed =>
            {
                if (confirmed == true)
                    JobManager.Cancel(shortId);
                ShowJobList(); // 一覧を最新状態で再表示
            });
        }
    }

    static void HandleConfirmDialogKey(ConfirmDialogState dialog, ConsoleKeyInfo key)
    {
        if (key.Key == ConsoleKey.Escape)
        {
            CloseConfirmDialog(dialog, null);
            return;
        }

        if (key.Key == ConsoleKey.Enter || key.KeyChar is 'y' or 'Y')
        {
            CloseConfirmDialog(dialog, true);
            return;
        }

        if (key.KeyChar is 'n' or 'N')
        {
            CloseConfirmDialog(dialog, false);
            return;
        }

        // Everything else is swallowed.
    }

    static void HandleTextInputDialogKey(TextInputDialogState dialog, ConsoleKeyInfo key)
    {
        if (key.Key == ConsoleKey.Escape)
        {
            CloseTextInputDialog(dialog, null);
            return;
        }

        if (key.Key == ConsoleKey.Enter)
        {
            CloseTextInputDialog(dialog, dialog.Buffer);
            return;
        }

        if (key.Key == ConsoleKey.Backspace)
        {
            (dialog.Buffer, dialog.CursorPos) = TextInputBuffer.Backspace(dialog.Buffer, dialog.CursorPos);
            return;
        }

        if (key.Key == ConsoleKey.Delete)
        {
            (dialog.Buffer, dialog.CursorPos) = TextInputBuffer.Delete(dialog.Buffer, dialog.CursorPos);
            return;
        }

        if (key.Key == ConsoleKey.LeftArrow)
        {
            dialog.CursorPos = TextInputBuffer.MoveLeft(dialog.Buffer, dialog.CursorPos);
            return;
        }

        if (key.Key == ConsoleKey.RightArrow)
        {
            dialog.CursorPos = TextInputBuffer.MoveRight(dialog.Buffer, dialog.CursorPos);
            return;
        }

        if (key.Key == ConsoleKey.Home)
        {
            dialog.CursorPos = TextInputBuffer.Home(dialog.Buffer, dialog.CursorPos);
            return;
        }

        if (key.Key == ConsoleKey.End)
        {
            dialog.CursorPos = TextInputBuffer.End(dialog.Buffer, dialog.CursorPos);
            return;
        }

        if (key.Key == ConsoleKey.U && (key.Modifiers & ConsoleModifiers.Control) != 0)
        {
            dialog.Buffer = "";
            dialog.CursorPos = 0;
            return;
        }

        // 文字受け取り: HandleSearchKeyAsync と同一の判定(制御文字を除外)。
        // IME確定文字・サロゲートペアもここを通り、TextInputBuffer.Insert が
        // string.Insert で正しく結合する(Program.cs の HandleSearchKeyAsync 実装を参照)。
        if (!char.IsControl(key.KeyChar) && (dialog.CharFilter == null || dialog.CharFilter(key.KeyChar)))
        {
            (dialog.Buffer, dialog.CursorPos) = TextInputBuffer.Insert(dialog.Buffer, dialog.CursorPos, key.KeyChar);
            return;
        }

        // Everything else is swallowed.
    }

    static void HandleListSelectDialogKey(ListSelectDialogState dialog, ConsoleKeyInfo key)
    {
        if (key.Key == ConsoleKey.Escape)
        {
            CloseListSelectDialog(dialog, null);
            return;
        }

        if (key.Key == ConsoleKey.Enter)
        {
            string? result = (dialog.Selected >= 0 && dialog.Selected < dialog.FilteredItems.Count)
                ? dialog.FilteredItems[dialog.Selected]
                : null;
            CloseListSelectDialog(dialog, result);
            return;
        }

        if (key.Key == ConsoleKey.DownArrow)
        {
            dialog.Selected = ListFilter.ClampSelected(dialog.Selected + 1, dialog.FilteredItems.Count);
            return;
        }

        if (key.Key == ConsoleKey.UpArrow)
        {
            dialog.Selected = ListFilter.ClampSelected(dialog.Selected - 1, dialog.FilteredItems.Count);
            return;
        }

        if (key.Key == ConsoleKey.Backspace)
        {
            if (dialog.FilterQuery.Length > 0)
            {
                dialog.FilterQuery = dialog.FilterQuery[..^1];
                RecomputeListSelectFilter(dialog);
            }
            return;
        }

        // 絞り込みクエリへの文字追加: HandleSearchKeyAsync と同一の判定。
        if (!char.IsControl(key.KeyChar))
        {
            dialog.FilterQuery += key.KeyChar;
            RecomputeListSelectFilter(dialog);
            return;
        }

        // Everything else is swallowed.
    }

    // 対象リストは高々数百件のため、キー入力ごとに全件再評価する(デバウンスなし。spec「ダイアログ設計」節)。
    static void RecomputeListSelectFilter(ListSelectDialogState dialog)
    {
        dialog.FilteredItems = ListFilter.Recompute(dialog.AllItems, dialog.FilterQuery, regexMode: false, _migemo);
        dialog.Selected = ListFilter.ClampSelected(dialog.Selected, dialog.FilteredItems.Count);
    }

    static async Task HandleSearchKeyAsync(Pane pane, ConsoleKeyInfo key)
    {
        SearchState s = pane.Search;
        Column col = pane.Columns[pane.ActiveColumn];

        if (key.Key == ConsoleKey.Escape)
        {
            await ExitSearchMode(pane, restoreCursor: true);
            UpdateHorizontalScroll(pane);
            await RebuildRightSideAsync(pane, pane.ActiveColumn);
            if (pane.Preview.IsVisible) StartPreviewLoad(pane);
            return;
        }

        if (key.Key == ConsoleKey.Enter)
        {
            await ExitSearchMode(pane, restoreCursor: false);
            UpdateHorizontalScroll(pane);
            await RebuildRightSideAsync(pane, pane.ActiveColumn);
            if (pane.Preview.IsVisible) StartPreviewLoad(pane);
            return;
        }

        if (key.Key == ConsoleKey.R && (key.Modifiers & ConsoleModifiers.Control) != 0)
        {
            s.RegexMode = !s.RegexMode;
            s.NeedsRecompute = true;
            s.LastInputTime = DateTime.UtcNow;
            return;
        }

        if (key.Key == ConsoleKey.DownArrow ||
            (key.Key == ConsoleKey.N && (key.Modifiers & ConsoleModifiers.Control) != 0))
        {
            bool moved = false;
            lock (_searchLock)
            {
                if (s.Matches.Count > 0)
                {
                    s.MatchIndex = (s.MatchIndex + 1) % s.Matches.Count;
                    col.Selected = s.Matches[s.MatchIndex];
                    moved = true;
                }
            }
            if (moved)
            {
                UpdateHorizontalScroll(pane);
                await RebuildRightSideAsync(pane, pane.ActiveColumn);
                if (pane.Preview.IsVisible) StartPreviewLoad(pane);
            }
            return;
        }

        if (key.Key == ConsoleKey.UpArrow ||
            (key.Key == ConsoleKey.P && (key.Modifiers & ConsoleModifiers.Control) != 0))
        {
            bool moved = false;
            lock (_searchLock)
            {
                if (s.Matches.Count > 0)
                {
                    s.MatchIndex = (s.MatchIndex - 1 + s.Matches.Count) % s.Matches.Count;
                    col.Selected = s.Matches[s.MatchIndex];
                    moved = true;
                }
            }
            if (moved)
            {
                UpdateHorizontalScroll(pane);
                await RebuildRightSideAsync(pane, pane.ActiveColumn);
                if (pane.Preview.IsVisible) StartPreviewLoad(pane);
            }
            return;
        }

        if (key.Key == ConsoleKey.Backspace)
        {
            if (s.Query.Length > 0)
            {
                s.Query = s.Query[..^1];
                s.NeedsRecompute = true;
                s.LastInputTime = DateTime.UtcNow;
            }
            return;
        }

        if (!char.IsControl(key.KeyChar))
        {
            s.Query += key.KeyChar;
            s.NeedsRecompute = true;
            s.LastInputTime = DateTime.UtcNow;
            return;
        }

        // Everything else is swallowed
    }

    static string GetCurrentFullPath(Pane pane)
    {
        if (pane.ActiveColumn < 0 || pane.ActiveColumn >= pane.Columns.Count)
            return "";

        Column c = pane.Columns[pane.ActiveColumn];

        if (c.Entries.Count == 0)
            return c.Path;

        if (c.Selected < 0 || c.Selected >= c.Entries.Count)
            return c.Path;

        string name = c.Entries[c.Selected];

        if (c.Path == "::DRIVES::")
            return name.TrimEnd('/') + "\\";

        return Path.Combine(c.Path, name.TrimEnd('/'));
    }

    internal static async Task RebuildRightSideAsync(Pane pane, int columnIndex)
    {
        while (pane.Columns.Count > columnIndex + 1)
        {
            Column col = pane.Columns[pane.Columns.Count - 1];
            col.ReadCts?.Cancel();
            SaveCursorMemory(col);
            pane.Columns.RemoveAt(pane.Columns.Count - 1);
        }

        string? nextPath = GetSelectedDirectory(pane, columnIndex);

        StringComparer pathComparer = IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        HashSet<string> visitedCanonical = new HashSet<string>(
            pane.Columns.Select(c => ResolveCanonicalPath(c.Path)), pathComparer);

        while (nextPath != null)
        {
            string canonicalNext = ResolveCanonicalPath(nextPath);
            if (visitedCanonical.Contains(canonicalNext))
                break; // cycle: this directory (or a symlink resolving to it) is already in the chain

            Column next = await CreateColumnAsync(nextPath);
            pane.Columns.Add(next);
            visitedCanonical.Add(canonicalNext);
            // Entries aren't loaded yet, so the chain can't walk deeper here;
            // ExtendRightSideIfReadyAsync continues it on idle ticks once the load lands.
            if (next.IsLoading) break;
            nextPath = GetSelectedDirectory(pane, pane.Columns.Count - 1);
        }

        // Recalculate scroll now that the right-side column count is final.
        UpdateHorizontalScroll(pane);
    }

    internal static async Task ExtendRightSideIfReadyAsync(Pane pane)
    {
        if (pane.NavRebuildPending) return;
        if (pane.Columns.Count == 0) return;
        Column last = pane.Columns[pane.Columns.Count - 1];
        if (last.IsLoading) return;
        if (GetSelectedDirectory(pane, pane.Columns.Count - 1) == null) return;
        await RebuildRightSideAsync(pane, pane.Columns.Count - 1);
        _redrawGate.Request();
    }

    static async Task ToggleSplitAsync()
    {
        if (!State.SplitActive)
        {
            if (!CanSplit(Console.WindowHeight))
            {
                _lastErrorMessage = "[terminal too small to split]";
                return;
            }

            Pane source = FocusedPane;
            string initialPath = source.Columns[source.ActiveColumn].Path;

            Pane newPane = new Pane();
            newPane.Columns.Add(await CreateColumnAsync(initialPath));
            await RebuildRightSideAsync(newPane, 0);

            State.Panes.Add(newPane);
            State.SplitActive = true;
            // フォーカスは元々見ていたペイン(常に State.Panes[0])のまま据え置く。
            // ユーザーは Ctrl+W 直後にそのまま同じペインを操作継続できる。
            State.FocusedPane = 0;
        }
        else
        {
            // 非フォーカス側 Pane を破棄する。フォーカス側だけを残す。
            int keepIndex = State.FocusedPane;
            int discardIndex = keepIndex == 0 ? 1 : 0;

            Pane discarded = State.Panes[discardIndex];
            CancelAllReadsInPane(discarded);

            Pane kept = State.Panes[keepIndex];
            State.Panes.Clear();
            State.Panes.Add(kept);
            State.FocusedPane = 0;
            State.SplitActive = false;
        }

        State.PrevFrame = null; // レイアウトが変わるため強制フル再描画
        UpdateHorizontalScroll(FocusedPane);
    }

    static void CancelAllReadsInPane(Pane pane)
    {
        // 破棄される Pane 配下の全カラムで非同期読み込み中の ReadCts をキャンセルし、
        // 再訪時に同じカーソル位置へ復元できるよう SaveCursorMemory も呼ぶ。
        foreach (Column col in pane.Columns)
        {
            col.ReadCts?.Cancel();
            SaveCursorMemory(col);
        }
    }

    static async Task JumpToPathAsync(Pane pane, string path)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch
        {
            _lastErrorMessage = $"Invalid path: {path}";
            return;
        }

        if (!Directory.Exists(fullPath))
        {
            _lastErrorMessage = $"Path not found: {path}";
            return;
        }

        // Cancel any in-flight loads on the existing column chain before discarding it.
        foreach (Column existing in pane.Columns)
            existing.ReadCts?.Cancel();

        // Split the target path into segments, e.g. "C:\Users\me\docs" -> ["C:\", "Users", "me", "docs"].
        var segments = new List<string>();
        string root = Path.GetPathRoot(fullPath) ?? "";
        string remainder = fullPath.Substring(root.Length);
        foreach (string part in remainder.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (part.Length > 0) segments.Add(part);
        }

        pane.Columns.Clear();

        string rootMarker = GetRootPath();
        Column rootColumn = await CreateColumnAsync(rootMarker);
        if (rootColumn.LoadingTask != null) await rootColumn.LoadingTask;
        pane.Columns.Add(rootColumn);

        // Select the drive (Windows) or first segment inside the root column.
        string accumulatedPath;
        if (IsWindows())
        {
            string driveEntry = root.TrimEnd('\\') + "/";
            int driveIndex = rootColumn.Entries.IndexOf(driveEntry);
            if (driveIndex < 0)
            {
                _lastErrorMessage = $"Path not found: {path}";
                return;
            }
            rootColumn.Selected = driveIndex;
            accumulatedPath = root; // e.g. "C:\"
        }
        else
        {
            accumulatedPath = "/";
        }

        pane.ActiveColumn = 0;

        foreach (string segment in segments)
        {
            Column current = pane.Columns[pane.ActiveColumn];
            string entryName = segment + "/";
            int idx = current.Entries.IndexOf(entryName);
            if (idx < 0)
            {
                _lastErrorMessage = $"Path not found: {path}";
                return;
            }
            current.Selected = idx;

            accumulatedPath = Path.Combine(accumulatedPath, segment);
            Column next = await CreateColumnAsync(accumulatedPath);
            if (next.LoadingTask != null) await next.LoadingTask;

            while (pane.Columns.Count > pane.ActiveColumn + 1)
                pane.Columns.RemoveAt(pane.Columns.Count - 1);

            pane.Columns.Add(next);
            pane.ActiveColumn++;
        }

        UpdateHorizontalScroll(pane);
        _lastErrorMessage = null;
        RecordHistoryVisit(fullPath);
    }

    static void RecordHistoryVisit(string fullPath)
    {
        int existingIndex = _history.FindIndex(p => string.Equals(p, fullPath, StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0)
            _history.RemoveAt(existingIndex);

        _history.Add(fullPath);

        while (_history.Count > HistoryLimit)
            _history.RemoveAt(0);
    }

    static void RequestHistoryJump(Pane pane)
    {
        if (_history.Count == 0)
        {
            _lastErrorMessage = "No history yet";
            return;
        }

        // Newest-first for display.
        var items = new List<string>(_history);
        items.Reverse();

        ShowListSelect(
            "History",
            items,
            async selected =>
            {
                if (selected == null) return;
                await JumpToPathAsync(pane, selected);
            });
    }

    static void RequestBookmarkAdd(Pane pane)
    {
        string currentDir = pane.Columns[pane.ActiveColumn].Path;
        if (currentDir == "::DRIVES::")
        {
            _lastErrorMessage = "Cannot bookmark the drive list";
            return;
        }

        bool added = Bookmarks.Add(currentDir);
        _lastErrorMessage = added ? $"Bookmarked: {currentDir}" : $"Already bookmarked: {currentDir}";
    }

    static void RequestBookmarkJump(Pane pane)
    {
        var items = Bookmarks.Load();
        if (items.Count == 0)
        {
            _lastErrorMessage = "No bookmarks yet";
            return;
        }

        ShowListSelect(
            "Bookmarks",
            items,
            async selected =>
            {
                if (selected == null) return;
                await JumpToPathAsync(pane, selected);
            });
    }

    static List<string> BuildBookmarkAndHistoryList()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        foreach (string b in Bookmarks.Load())
            if (seen.Add(b)) result.Add(b);

        var historyNewestFirst = new List<string>(_history);
        historyNewestFirst.Reverse();
        foreach (string h in historyNewestFirst)
            if (seen.Add(h)) result.Add(h);

        return result;
    }

    static string? GetSelectedDirectory(Pane pane, int columnIndex)
    {
        if (columnIndex < 0 || columnIndex >= pane.Columns.Count)
            return null;

        // Leap filter with zero matches means the active column has no effective
        // selection right now — cascade that as "no selection" to child panes.
        if (pane.Leap.Active && columnIndex == pane.ActiveColumn && pane.Leap.NoMatch)
            return null;

        Column c = pane.Columns[columnIndex];

        if (c.Entries.Count == 0)
            return null;

        if (c.Selected < 0 || c.Selected >= c.Entries.Count)
            return null;

        string name = c.Entries[c.Selected];

        if (!name.EndsWith("/"))
            return null;

        // Special case: drive list
        if (c.Path == "::DRIVES::")
        {
            string drive = name.Substring(0, name.Length - 1);  // "C:"
            // Ensure proper path format
            if (!drive.EndsWith("\\") && !drive.EndsWith("/"))
                drive += "\\";
            try
            {
                if (Directory.Exists(drive))
                    return drive;
            }
            catch
            {
            }
            return null;
        }

        string folderName = name.Substring(0, name.Length - 1);
        string full = Path.Combine(c.Path, folderName);

        try
        {
            if (Directory.Exists(full))
                return full;
        }
        catch
        {
        }

        return null;
    }

    static void HandleResizeIfNeeded(Pane pane)
    {
        int w = Console.WindowWidth;
        int h = Console.WindowHeight;
        if (w <= 0 || h <= 0) return;
        if (w == State.PrevWidth && h == State.PrevHeight) return;

        State.PrevWidth = w;
        State.PrevHeight = h;
        State.PrevFrame = null; // force full redraw in Draw()

        UpdateHorizontalScroll(pane);

        // Clamp every column's scroll offset for the new height
        int visibleHeight = Math.Max(1, h - 3);
        for (int i = 0; i < pane.Columns.Count; i++)
        {
            Column col = pane.Columns[i];
            if (col.ScrollOffset < 0)
                col.ScrollOffset = 0;
            if (col.Selected >= col.ScrollOffset + visibleHeight)
                col.ScrollOffset = col.Selected - visibleHeight + 1;
        }

        // Reload preview at new dimensions (cancels any in-flight load, bypasses same-path guard)
        if (pane.Preview.IsVisible)
            StartPreviewLoad(pane, forceReload: true);
    }

    static bool AnyColumnLoading(Pane pane)
    {
        for (int i = 0; i < pane.Columns.Count; i++)
            if (pane.Columns[i].IsLoading) return true;
        return false;
    }

    static bool UiActivityInProgress(Pane pane)
    {
        return AnyColumnLoading(pane)
            || pane.Preview.IsLoading
            || (pane.Search.Active && !pane.Search.SearchDone)
            || JobManager.HasActiveJobs;
    }

    static void Draw()
    {
        int width = Console.WindowWidth;
        int height = Console.WindowHeight;

        string[] currentFrame = BuildFrame(width, height);

        // Incremental rendering
        if (State.PrevFrame == null)
        {
            Console.Clear();
            for (int i = 0; i < currentFrame.Length; i++)
                WriteLineAt(0, i, currentFrame[i]);
        }
        else
        {
            for (int i = 0; i < currentFrame.Length; i++)
            {
                if (State.PrevFrame[i] != currentFrame[i])
                    WriteLineAt(0, i, currentFrame[i]);
            }
        }

        State.PrevFrame = currentFrame;
    }

    internal static string[] BuildFrame(int width, int height)
    {
        _framesBuilt++;
        Pane pane = FocusedPane;

        // Clear the "missing entry" message as soon as the cursor actually moves
        // away from where the fallback landed it (every cursor move triggers a
        // redraw, so this check runs on the frame that follows the move).
        if (_missingEntryMessage != null)
        {
            Column active = pane.Columns[pane.ActiveColumn];
            if (!ReferenceEquals(active, _missingEntryColumn) || active.Selected != _missingEntrySelected)
                _missingEntryMessage = null;
        }

        string[] frame = new string[height];

        // Build frame using segment-based approach
        Line[] lines = new Line[height];
        for (int i = 0; i < height; i++)
            lines[i] = new Line();

        // --- Layer 1: pane-rect drawing ---
        int footerRows; // rows at the bottom of `frame` written directly, not via `lines[]`
        if (!State.SplitActive)
        {
            // yOffset=0, focused=true fixed. DrawPaneToLines's `height` parameter conceptually
            // represents "the pane's assigned rectangle height"; in this single-pane phase that
            // rectangle still spans the full screen height, matching what the pre-refactor code
            // implicitly passed as frameHeight to DrawColumnToLines/DrawPreviewToLines. Passing
            // `height` here (not height-2) preserves the original visibleHeight = frameHeight - 3
            // math exactly, keeping output byte-identical. The shared 2-row footer (full path +
            // status) below still applies, since a single pane has no own path line of its own.
            DrawPaneToLines(lines, pane, 0, height, true, false, width);
            footerRows = 2;
        }
        else
        {
            // Each pane now draws its own full-path line at the bottom of its own rectangle
            // (so source/destination paths are both visible at once for copy/move), so the
            // shared footer shrinks to just the 1 status row.
            (int topHeight, int bottomHeight) = ComputeSplitPaneHeights(height);
            int separatorRow = topHeight;
            int bottomOffset = topHeight + 1;

            DrawPaneToLines(lines, State.Panes[0], 0, topHeight, State.FocusedPane == 0, true, width);

            string sep = new string('─', width);
            lines[separatorRow].AddColumn(sep, width, width);

            DrawPaneToLines(lines, State.Panes[1], bottomOffset, bottomHeight, State.FocusedPane == 1, true, width);
            footerRows = 1;
        }

        // Render each line to string and pad to window width
        for (int i = 0; i < height - footerRows; i++)
        {
            frame[i] = lines[i].Render(width);
        }

        // --- Layer 2: shared footer ---
        if (!State.SplitActive)
        {
            // Full path line (above status) — single pane, no own path line was drawn above.
            string fullPath = "\"" + GetCurrentFullPath(pane) + "\"";
            fullPath = CharacterWidth.SmartTruncate(fullPath, width);
            frame[height - 2] = CharacterWidth.PadToWidth(fullPath, width);
        }

        // Override status with preview-hidden hint only when terminal width is the limiting factor
        if (pane.Preview.IsVisible && SelectedItemIsFileOrDrive(pane) && width < ColumnWidth * 2)
        {
            _lastErrorMessage = "[preview hidden: narrow terminal]";
        }

        // Status line
        if (pane.Leap.Active)
        {
            string leapBar = SearchHelper.BuildLeapStatusBar(pane.Leap, pane.Columns, pane.ActiveColumn, width);
            frame[height - 1] = leapBar;
        }
        else if (pane.Search.Active)
        {
            List<int> matchSnapshot;
            bool done;
            lock (_searchLock)
            {
                matchSnapshot = new List<int>(pane.Search.Matches);
                done = pane.Search.SearchDone;
            }
            string searchBar = SearchHelper.BuildSearchStatusBar(pane.Search, matchSnapshot, done, width);
            frame[height - 1] = searchBar; // already padded to width
        }
        else if (_lastErrorMessage != null)
        {
            string status = _lastErrorMessage;
            _lastErrorMessage = null;  // Clear error after displaying
            status = CharacterWidth.SmartTruncate(status, width);
            frame[height - 1] = CharacterWidth.PadToWidth(status, width);
        }
        else if (_missingEntryMessage != null)
        {
            string status = CharacterWidth.SmartTruncate(_missingEntryMessage, width);
            frame[height - 1] = CharacterWidth.PadToWidth(status, width);
        }
        else if (TryBuildJobSummary(width, out string jobSummary))
        {
            frame[height - 1] = jobSummary;
        }
        else
        {
            string previewHint = pane.Preview.IsVisible ? "Shift+V=Preview[on]" : "Shift+V=Preview[off]";
            string status;
            if (IsWindows())
                status = $"Esc=Quit | Ctrl+Enter=Open | Shift+Enter/\\=Menu | {previewHint}";
            else
                status = $"Esc=Quit | Ctrl+Enter=Open File | {previewHint}";

            int markedCount = pane.Columns[pane.ActiveColumn].Marked.Count;
            if (markedCount > 0)
                status += $" | {markedCount} marked";

            status = CharacterWidth.SmartTruncate(status, width);
            frame[height - 1] = CharacterWidth.PadToWidth(status, width);
        }

        // --- Layer 3: overlay (empty hook; DialogState-based rendering is a future phase) ---
        DrawDialogOverlay(frame, width, height);

        return frame;
    }

    static bool TryBuildJobSummary(int width, out string summary)
    {
        List<BackgroundJob> active = JobManager.Snapshot().FindAll(j => j.Status == JobStatus.Pending || j.Status == JobStatus.Running);

        if (active.Count == 0)
        {
            summary = "";
            return false;
        }

        if (_cachedJobSummary == null || (DateTime.UtcNow - _lastJobSummaryRenderTime).TotalMilliseconds >= 100)
        {
            active.Sort((a, b) => a.StartTime.CompareTo(b.StartTime));
            BackgroundJob oldest = active[0];

            int doneCount, totalCount;
            string currentFile;
            lock (JobManager.ProgressLock)
            {
                doneCount = oldest.DoneCount;
                totalCount = oldest.TotalCount;
                currentFile = oldest.CurrentFile;
            }

            int percent = totalCount > 0 ? (int)(100.0 * doneCount / totalCount) : 0;
            string text = $"▶{active.Count} running #{oldest.ShortId} {oldest.OpName} {percent}% ({doneCount}/{totalCount}) {currentFile}";
            text = CharacterWidth.SmartTruncate(text, width);
            _cachedJobSummary = CharacterWidth.PadToWidth(text, width);
            _lastJobSummaryRenderTime = DateTime.UtcNow;
        }

        summary = _cachedJobSummary!;
        return true;
    }

    static void DrawDialogOverlay(string[] frame, int width, int height)
    {
        if (State.Dialog == null) return;

        (int dialogY, int dialogHeight) = ComputeFocusedPaneYRange(State.SplitActive, State.FocusedPane, height);
        ComposeDialogIntoFrame(frame, State.Dialog, width, dialogHeight, dialogY);
    }

    internal static void DrawPaneToLines(Line[] lines, Pane pane, int yOffset, int height, bool focused, bool showOwnPathLine, int width)
    {
        bool showPreview = pane.Preview.IsVisible && width >= ColumnWidth * 2;
        bool filePreview = pane.Preview.IsVisible && SelectedItemIsFileOrDrive(pane);

        int displayX = 0;
        int drawnColumnsWidth = 0;
        int columnDrawLimit = filePreview ? width - ColumnWidth : width;

        // Column/preview body height: normally the rectangle reserves 3 rows internally
        // (header + 2 rows matching the single-pane shared footer). When this pane draws
        // its own path line at its last row (split mode), only 2 rows need reserving
        // (header + this path line), so pad frameHeight by 1 to shrink that internal
        // reservation by 1 without touching DrawColumnToLines/DrawPreviewToLines math.
        int bodyFrameHeight = showOwnPathLine ? height + 1 : height;

        for (int i = pane.HorizontalScroll;
             i < pane.Columns.Count && displayX + ColumnWidth <= columnDrawLimit;
             i++)
        {
            bool isFirstVisible = (i == pane.HorizontalScroll);
            DrawColumnToLines(lines, pane, pane.Columns[i], displayX, i == pane.ActiveColumn, i < pane.ActiveColumn, width, bodyFrameHeight, isFirstVisible, focused, yOffset);
            displayX += ColumnWidth;
            drawnColumnsWidth += ColumnWidth;
        }

        if (showPreview)
        {
            int previewWidth = width - drawnColumnsWidth;
            PreviewContent content;
            if (pane.Preview.IsLoading || pane.Preview.Content == null)
            {
                content = new PreviewContent("Loading…", "", "", ["⠋"], true);
            }
            else
            {
                content = pane.Preview.Content;
            }

            if (previewWidth >= ColumnWidth)
                DrawPreviewToLines(lines, content, drawnColumnsWidth, previewWidth, bodyFrameHeight, yOffset);
        }

        if (showOwnPathLine)
        {
            string prefix = focused ? "> " : "  ";
            string fullPath = prefix + "\"" + GetCurrentFullPath(pane) + "\"";
            fullPath = CharacterWidth.SmartTruncate(fullPath, width);
            string padded = CharacterWidth.PadToWidth(fullPath, width);
            lines[yOffset + height - 1].AddColumn(padded, width, width);
        }
    }

    internal static void DrawColumnToLines(Line[] lines, Pane pane, Column column, int displayX, bool active, bool isLeft, int frameWidth, int frameHeight, bool isFirstVisible, bool focused, int yOffset)
    {
        // frameHeight is the height of the rectangle assigned to this pane (not the whole screen);
        // subtract 3 rows (header + full-path row + status row) from that rectangle height.
        int visibleHeight = frameHeight - 3;  // Reserve 3 rows: header + fullpath + status

        // 減光パレット選択(非フォーカスペイン用)。SGR 2 (dim) は端末依存で不安定なため使わず、
        // ANSI 90 系(明るい黒 = グレー)の色コードに明示的に差し替える。
        string dirColor      = focused ? "\x1b[94m" : AnsiColors.Gray;   // ディレクトリ色: 明青→グレー
        string selBgActive   = focused ? "\x1b[47m" : "\x1b[100m";       // 選択行背景(このカラムがアクティブ時): 白→暗グレー
        string selBgInactive = "\x1b[100m";                              // 選択行背景(このカラムが非アクティブ時): 常に暗グレー(変更なし)
        // 選択行(カーソル行)の文字色は非フォーカス時も背景の暗グレーに対して視認できる色を使う
        // (以前は AnsiColors.Gray を使っていたため、背景も文字もグレーで見えなくなっていた)。
        string selDirText    = focused ? "\x1b[94m" : "\x1b[34m";        // 選択行内ディレクトリ文字色: 明青→濃紺
        string selFileText   = "\x1b[30m";                               // 選択行内ファイル文字色: 常に黒
        string matchBg       = focused ? "\x1b[42m" : "\x1b[100m";       // Search 現在マッチ背景: 緑→暗グレー
        string matchText     = focused ? "\x1b[30m" : AnsiColors.Gray;   // Search 現在マッチ文字色
        string otherMatchFg  = focused ? "\x1b[32m" : AnsiColors.Gray;   // Search 他マッチ前景: 緑→グレー
        string leapSelBg     = focused ? "\x1b[47m" : "\x1b[100m";       // Leap 選択行背景
        string leapDirText   = focused ? "\x1b[94m" : "\x1b[34m";        // Leap 選択行ディレクトリ文字色: 明青→濃紺
        string leapFileText  = "\x1b[30m";                               // Leap 選択行ファイル文字色: 常に黒
        string markBg        = focused ? "\x1b[46m" : "\x1b[100m";      // マーク済み行背景(非選択時): シアン→暗グレー
        string markText      = focused ? "\x1b[30m" : AnsiColors.Gray;  // マーク済み行文字色(非選択時): 黒→グレー

        int entryCount = column.Entries.Count;

        bool inLeap = active && pane.Leap.Active;
        List<int>? leapVisible = inLeap ? pane.Leap.VisibleIndices : null;

        // Auto-scroll: leap uses virtual scroll over visible indices; normal uses raw index
        if (inLeap && leapVisible != null)
        {
            int visPos = leapVisible.Count > 0 ? leapVisible.IndexOf(column.Selected) : 0;
            if (visPos < 0) visPos = 0;
            int ls = pane.Leap.ColumnScrollOffset;
            if (visPos < ls) ls = visPos;
            if (visPos >= ls + visibleHeight) ls = visPos - visibleHeight + 1;
            pane.Leap.ColumnScrollOffset = Math.Max(0, ls);
        }
        else
        {
            if (column.Selected < column.ScrollOffset)
                column.ScrollOffset = column.Selected;
            if (column.Selected >= column.ScrollOffset + visibleHeight)
                column.ScrollOffset = column.Selected - visibleHeight + 1;
        }

        // Content slot: non-first columns lose 1 display col to the separator
        int separatorWidth = isFirstVisible ? 0 : 1;
        int contentSlot = ColumnWidth - separatorWidth;  // display cols available for content
        int maxEntryWidth = contentSlot - 2;             // minus 2-char prefix

        // Header - show only leaf name, not full path
        string header;
        if (column.Path == "::DRIVES::")
        {
            header = "Drives";
        }
        else
        {
            header = Path.GetFileName(column.Path);
            if (string.IsNullOrEmpty(header))
                header = column.Path;
        }

        // Append progress counter if loading
        if (column.IsLoading)
        {
            header += $" [loading {column.EntriesRead}]";
        }

        header = CharacterWidth.SmartTruncate(header, contentSlot);
        int headerDisplayWidth = CharacterWidth.GetStringWidth(header);
        lines[yOffset + 0].AddColumn(header, headerDisplayWidth, ColumnWidth);

        int startRow = 1;
        int scrollOffset = column.ScrollOffset;

        bool inSearch = active && pane.Search.Active;
        HashSet<int> matchSet = new HashSet<int>();
        int currentMatchEntry = -1;
        if (inSearch)
        {
            lock (_searchLock)
            {
                if (pane.Search.Matches.Count > 0)
                {
                    matchSet = new HashSet<int>(pane.Search.Matches);
                    currentMatchEntry = pane.Search.Matches[pane.Search.MatchIndex];
                }
            }
        }

        if (inLeap && leapVisible != null)
        {
            // Leap mode: render only visible (matching) entries; non-matching entries are hidden
            int leapScroll = pane.Leap.ColumnScrollOffset;
            for (int i = 0; i < visibleHeight; i++)
            {
                int vi = leapScroll + i;
                if (vi >= leapVisible.Count)
                {
                    lines[yOffset + startRow + i].AddColumn("", 0, ColumnWidth);
                    continue;
                }
                int absIdx = leapVisible[vi];
                string text = column.Entries[absIdx];
                bool isDirectory = text.EndsWith("/");
                bool isSelected = absIdx == column.Selected;
                bool isMarked = column.Marked.Contains(text);

                // prefix は常に2文字: 1文字目=カーソル、2文字目=マーク。
                // マーク有無で entry のトランケート幅は変わらない(空きスロットを再利用するため)。
                string prefix = ((isSelected && active) ? ">" : " ") + (isMarked ? "*" : " ");
                string entry = CharacterWidth.SmartTruncate(text, maxEntryWidth);

                if (isSelected && active)
                {
                    string bgColor = leapSelBg;
                    string textColor = isDirectory ? leapDirText : leapFileText;
                    string reset = "\x1b[0m";
                    string displayText = prefix + entry;
                    int displayTextWidth = CharacterWidth.GetStringWidth(displayText);
                    int paddingNeeded = Math.Max(0, contentSlot - displayTextWidth);
                    string fullLine = bgColor + textColor + displayText + new string(' ', paddingNeeded) + reset;
                    lines[yOffset + startRow + i].AddColumn(fullLine, displayTextWidth + paddingNeeded, ColumnWidth);
                }
                else if (isMarked)
                {
                    string bgColor = markBg;
                    string textColor = markText;
                    string reset = "\x1b[0m";
                    string displayText = prefix + entry;
                    int displayTextWidth = CharacterWidth.GetStringWidth(displayText);
                    int paddingNeeded = Math.Max(0, contentSlot - displayTextWidth);
                    string fullLine = bgColor + textColor + displayText + new string(' ', paddingNeeded) + reset;
                    lines[yOffset + startRow + i].AddColumn(fullLine, displayTextWidth + paddingNeeded, ColumnWidth);
                }
                else
                {
                    int displayWidth = 2 + CharacterWidth.GetStringWidth(entry);
                    if (isDirectory)
                        entry = AnsiColors.Colorize(entry, dirColor);
                    lines[yOffset + startRow + i].AddColumn(prefix + entry, displayWidth, ColumnWidth);
                }
            }
        }
        else
        {
            // Normal / search mode: render all entries, highlight matches
            for (int i = 0; i < visibleHeight; i++)
            {
                if (scrollOffset + i >= entryCount)
                {
                    lines[yOffset + startRow + i].AddColumn("", 0, ColumnWidth);
                    continue;
                }

                string text = column.Entries[scrollOffset + i];
                bool isDirectory = text.EndsWith("/");
                bool isSelected = (scrollOffset + i) == column.Selected;
                bool isMarked = column.Marked.Contains(text);

                int entryAbsIndex = scrollOffset + i;
                bool isCurrentMatch = inSearch && entryAbsIndex == currentMatchEntry;
                bool isOtherMatch   = inSearch && !isCurrentMatch && matchSet.Contains(entryAbsIndex);

                char cursorChar;
                if (isSelected)
                {
                    if (active)
                        cursorChar = '>';
                    else if (isLeft)
                        cursorChar = ']';
                    else
                        cursorChar = ' ';
                }
                else
                    cursorChar = ' ';

                // prefix は常に2文字: 1文字目=カーソル、2文字目=マーク。
                // マーク有無で entry のトランケート幅は変わらない(空きスロットを再利用するため)。
                string prefix = cursorChar + (isMarked ? "*" : " ");
                string entry = CharacterWidth.SmartTruncate(text, maxEntryWidth);

                if (isCurrentMatch)
                {
                    // Current match: green background, black text
                    string bgColor = matchBg;
                    string textColor = matchText;
                    string reset = "\x1b[0m";

                    string displayText = prefix + entry;
                    int displayTextWidth = CharacterWidth.GetStringWidth(displayText);
                    int paddingNeeded = Math.Max(0, contentSlot - displayTextWidth);

                    string fullLine = bgColor + textColor + displayText + new string(' ', paddingNeeded) + reset;
                    int totalDisplayWidth = displayTextWidth + paddingNeeded;

                    lines[yOffset + startRow + i].AddColumn(fullLine, totalDisplayWidth, ColumnWidth);
                }
                else if (isSelected)
                {
                    // Background extends across full pane width
                    // Active: white bg, Inactive: gray bg
                    // Text: black for files, dark blue for directories
                    string bgColor = active ? selBgActive : selBgInactive;
                    string textColor = isDirectory ? selDirText : selFileText;
                    string reset = "\x1b[0m";

                    string displayText = prefix + entry;
                    int displayTextWidth = CharacterWidth.GetStringWidth(displayText);
                    int paddingNeeded = Math.Max(0, contentSlot - displayTextWidth);

                    // Build full line: background + text color + content + padding + reset
                    string fullLine = bgColor + textColor + displayText + new string(' ', paddingNeeded) + reset;
                    int totalDisplayWidth = displayTextWidth + paddingNeeded;

                    lines[yOffset + startRow + i].AddColumn(fullLine, totalDisplayWidth, ColumnWidth);
                }
                else if (isMarked)
                {
                    // Marked (not selected/current-match): background extends across full pane width
                    string bgColor = markBg;
                    string textColor = markText;
                    string reset = "\x1b[0m";

                    string displayText = prefix + entry;
                    int displayTextWidth = CharacterWidth.GetStringWidth(displayText);
                    int paddingNeeded = Math.Max(0, contentSlot - displayTextWidth);

                    string fullLine = bgColor + textColor + displayText + new string(' ', paddingNeeded) + reset;
                    int totalDisplayWidth = displayTextWidth + paddingNeeded;

                    lines[yOffset + startRow + i].AddColumn(fullLine, totalDisplayWidth, ColumnWidth);
                }
                else if (isOtherMatch)
                {
                    // Other match: green foreground text on default background
                    int displayWidth = 2 + CharacterWidth.GetStringWidth(entry);
                    string coloredEntry = otherMatchFg + entry + "\x1b[0m";
                    lines[yOffset + startRow + i].AddColumn(prefix + coloredEntry, displayWidth, ColumnWidth);
                }
                else
                {
                    // Non-selected: use original logic
                    int displayWidth = 2 + CharacterWidth.GetStringWidth(entry);
                    if (isDirectory)
                        entry = AnsiColors.Colorize(entry, dirColor);
                    lines[yOffset + startRow + i].AddColumn(prefix + entry, displayWidth, ColumnWidth);
                }
            }
        }
    }

    internal static void DrawPreviewToLines(Line[] lines, PreviewContent content, int startX, int previewWidth, int frameHeight, int yOffset)
    {
        int visibleHeight = frameHeight - 3; // same as column body height

        // Row 0: type label
        string headerLabel = CharacterWidth.SmartTruncate(content.TypeLabel, previewWidth - 1);
        int headerW = CharacterWidth.GetStringWidth(headerLabel);
        lines[yOffset + 0].AddColumn(headerLabel, headerW, previewWidth);

        if (visibleHeight < 1) return;

        // Row 1: info line (size / dimensions / duration etc.)
        if (!string.IsNullOrEmpty(content.InfoLine))
        {
            string info = CharacterWidth.SmartTruncate(content.InfoLine, previewWidth - 1);
            int infoW = CharacterWidth.GetStringWidth(info);
            string infoColored = AnsiColors.Colorize(info, AnsiColors.Yellow);
            lines[yOffset + 1].AddColumn(infoColored, infoW, previewWidth);
        }
        else
            lines[yOffset + 1].AddColumn("", 0, previewWidth);

        // Row 2: modified date
        if (!string.IsNullOrEmpty(content.Modified))
        {
            string mod = CharacterWidth.SmartTruncate(content.Modified, previewWidth - 1);
            lines[yOffset + 2].AddColumn(mod, CharacterWidth.GetStringWidth(mod), previewWidth);
        }
        else
            lines[yOffset + 2].AddColumn("", 0, previewWidth);

        // Row 3: divider
        if (visibleHeight >= 3)
        {
            string divider = new string('─', previewWidth - 1);
            lines[yOffset + 3].AddColumn(divider, CharacterWidth.GetStringWidth(divider), previewWidth);
        }

        // Row 4 (optional): extension mismatch warning
        int bodyStart = 4;
        if (!string.IsNullOrEmpty(content.ExtMismatch) && visibleHeight >= bodyStart)
        {
            string warn = CharacterWidth.SmartTruncate(content.ExtMismatch, previewWidth - 1);
            string warnColored = AnsiColors.Colorize(warn, AnsiColors.Yellow);
            lines[yOffset + bodyStart].AddColumn(warnColored, CharacterWidth.GetStringWidth(warn), previewWidth);
            bodyStart = 5;
        }

        // Body lines
        if (content.PixelLines != null)
        {
            // Pixel render: pass ANSI-escaped strings verbatim with their visual width
            for (int i = 0; i < visibleHeight - bodyStart + 1 && i < content.PixelLines.Length; i++)
            {
                int row = bodyStart + i;
                if (row >= visibleHeight + 1) break;
                lines[yOffset + row].AddColumn(content.PixelLines[i], content.PixelWidth, previewWidth);
            }
            int lastPixelRow = bodyStart + Math.Min(content.PixelLines.Length, visibleHeight - bodyStart + 1);
            for (int i = lastPixelRow; i < visibleHeight + 1; i++)
                lines[yOffset + i].AddColumn("", 0, previewWidth);
        }
        else
        {
            for (int i = 0; i < visibleHeight - bodyStart + 1 && i < content.BodyLines.Length; i++)
            {
                int row = bodyStart + i;
                if (row >= visibleHeight + 1) break;
                string bodyLine = CharacterWidth.SmartTruncate(content.BodyLines[i], previewWidth - 1);
                int bodyW = CharacterWidth.GetStringWidth(bodyLine);
                lines[yOffset + row].AddColumn(bodyLine, bodyW, previewWidth);
            }
            int lastBodyRow = bodyStart + Math.Min(content.BodyLines.Length, visibleHeight - bodyStart + 1);
            for (int i = lastBodyRow; i < visibleHeight + 1; i++)
                lines[yOffset + i].AddColumn("", 0, previewWidth);
        }
    }

    static string BuildTitledTopBorder(string title, int innerWidth)
    {
        string label = string.IsNullOrEmpty(title) ? "" : $"┤ {title} ├";
        int labelWidth = CharacterWidth.GetStringWidth(label);
        if (labelWidth > innerWidth)
        {
            label = "";
            labelWidth = 0;
        }
        int leftDashes = Math.Max(1, (innerWidth - labelWidth) / 2);
        int rightDashes = Math.Max(1, innerWidth - labelWidth - leftDashes);
        return "┌" + new string('─', leftDashes) + label + new string('─', rightDashes) + "┐";
    }

    // ダイアログ種別ごとの内容行を組み立てる。カーソル位置は反転スペース(ANSI SGR 7)を
    // テキスト中に挿入することで表現する(コンソールカーソル自体は非表示のため)。
    static List<string> BuildDialogContentLines(DialogState dialog)
    {
        switch (dialog)
        {
            case ConfirmDialogState confirm:
                return new List<string>
                {
                    confirm.Message,
                    "",
                    "[Y]es  [N]o  [Esc]Cancel",
                };

            case TextInputDialogState textInput:
            {
                // Cap the field at a fixed width and scroll so the cursor is always visible,
                // instead of letting the dialog grow to the buffer's full width and truncating
                // (which used to silently drop the cursor marker on long input — see spec:
                // "Cursor visibility & horizontal scrolling"). -1 reserves a column for the
                // cursor marker itself, which is inserted after slicing.
                const int MaxFieldWidth = 50;
                (int start, int end) = TextInputBuffer.ComputeScrollWindow(textInput.Buffer, textInput.CursorPos, MaxFieldWidth - 1);
                string visible = textInput.Buffer.Substring(start, end - start);
                int clampedCursorPos = Math.Clamp(textInput.CursorPos, 0, textInput.Buffer.Length);
                int cursorInVisible = clampedCursorPos - start;
                string beforeCursor = visible.Substring(0, cursorInVisible);
                string atAndAfterCursor = visible.Substring(cursorInVisible);
                // 反転スペースだけだと配色によっては見えにくいため、実体のある全角ブロックを
                // 反転表示することで、入力欄であることとカーソル位置の両方を分かりやすくする。
                string displayLine = beforeCursor + "\x1b[7m█\x1b[0m" + atAndAfterCursor;
                return new List<string>
                {
                    displayLine,
                    "",
                    textInput.HintText,
                };
            }

            case ListSelectDialogState listSelect:
            {
                var lines = new List<string>
                {
                    "/" + listSelect.FilterQuery + "\x1b[7m \x1b[0m",
                    "",
                };

                if (listSelect.FilteredItems.Count == 0)
                {
                    lines.Add("(No matches)");
                }
                else
                {
                    const int maxVisibleRows = 12;
                    int total = listSelect.FilteredItems.Count;
                    int visibleRows = Math.Min(maxVisibleRows, total);

                    int scrollStart = 0;
                    if (listSelect.Selected >= visibleRows)
                        scrollStart = listSelect.Selected - visibleRows + 1;
                    scrollStart = Math.Min(scrollStart, Math.Max(0, total - visibleRows));

                    for (int i = 0; i < visibleRows; i++)
                    {
                        int idx = scrollStart + i;
                        if (idx >= total) break;
                        string prefix = idx == listSelect.Selected ? "> " : "  ";
                        string itemText = prefix + listSelect.FilteredItems[idx];
                        if (idx == listSelect.Selected)
                            itemText = "\x1b[7m" + itemText + "\x1b[0m";
                        lines.Add(itemText);
                    }
                }

                lines.Add("");
                lines.Add("[↑↓]Select  [Enter]Confirm  [Esc]Cancel");
                return lines;
            }

            case JobListDialogState jobListDlg:
            {
                const int rowWidth = 76;
                var lines = new List<string>();

                if (jobListDlg.Snapshot.Count == 0)
                {
                    lines.Add("(No jobs)");
                }
                else
                {
                    const int maxVisibleRows = 12;
                    int total = jobListDlg.Snapshot.Count;
                    int visibleRows = Math.Min(maxVisibleRows, total);

                    int scrollStart = 0;
                    if (jobListDlg.Selected >= visibleRows)
                        scrollStart = jobListDlg.Selected - visibleRows + 1;
                    scrollStart = Math.Min(scrollStart, Math.Max(0, total - visibleRows));

                    for (int i = 0; i < visibleRows; i++)
                    {
                        int idx = scrollStart + i;
                        if (idx >= total) break;
                        string rowText = JobListFormatter.FormatRow(jobListDlg.Snapshot[idx], rowWidth);
                        if (idx == jobListDlg.Selected)
                            rowText = "\x1b[7m" + rowText + "\x1b[0m";
                        lines.Add(rowText);
                    }
                }

                lines.Add("");
                lines.Add("[↑↓]Select  [Enter/Delete]Cancel  [Esc]Close");
                return lines;
            }

            default:
                return new List<string> { "" };
        }
    }

    // `paneHeight` is the sub-rectangle height to center within (the focused pane's height in
    // split mode, or the full screen otherwise — see ComputeFocusedPaneYRange). `yOffset` is
    // added to every absolute row this writes, so the centered box lands inside that
    // sub-rectangle instead of always starting at the top of the screen. Row-bounds checking
    // still uses `frame.Length` (the real total frame height), not `paneHeight`.
    static void ComposeDialogIntoFrame(string[] frame, DialogState dialog, int width, int paneHeight, int yOffset)
    {
        List<string> contentLines = BuildDialogContentLines(dialog);
        int contentWidth = 0;
        foreach (string l in contentLines)
            contentWidth = Math.Max(contentWidth, CharacterWidth.GetStringWidth(StripAnsiForWidth(l)));

        // タイトルもコンテンツ幅の下限に含める("┤ タイトル ├" 分の余白を確保)
        int titleWidth = CharacterWidth.GetStringWidth(dialog.Title) + 4;
        contentWidth = Math.Max(contentWidth, titleWidth);

        DialogRect rect = DialogLayout.CalcDialogRect(contentWidth, contentLines.Count, width, paneHeight);
        int innerWidth = rect.Width - 2;
        int innerHeight = rect.Height - 2;

        string topBorder = BuildTitledTopBorder(dialog.Title, innerWidth);
        OverwriteFrameRow(frame, yOffset + rect.Y, rect.X, topBorder, width, frame.Length);

        for (int i = 0; i < innerHeight; i++)
        {
            string rowText = i < contentLines.Count ? contentLines[i] : "";
            string rowText2 = FitRowToWidth(rowText, innerWidth);
            string row = "│" + rowText2 + "│";
            OverwriteFrameRow(frame, yOffset + rect.Y + 1 + i, rect.X, row, width, frame.Length);
        }

        string bottomBorder = "└" + new string('─', innerWidth) + "┘";
        OverwriteFrameRow(frame, yOffset + rect.Y + rect.Height - 1, rect.X, bottomBorder, width, frame.Length);
    }

    // ANSI付きの行を innerWidth ちょうどの表示幅に整形する。切り詰めが必要な行は
    // ANSI装飾を落としてプレーンテキストで切り詰め・パディングする(既存のステータスバー実装と
    // 同水準の割り切り。補足はTask 8.1参照)。
    static string FitRowToWidth(string rowText, int innerWidth)
    {
        string plain = StripAnsiForWidth(rowText);
        int plainWidth = CharacterWidth.GetStringWidth(plain);
        if (plainWidth <= innerWidth && plain.Length == rowText.Length)
        {
            // ANSIを含まない、かつ幅に収まる行はそのままパディングして返す。
            return rowText + new string(' ', innerWidth - plainWidth);
        }
        string truncated = CharacterWidth.SmartTruncate(plain, innerWidth);
        return CharacterWidth.PadToWidth(truncated, innerWidth);
    }

    // frame[y] の表示列 x から displayWidth 分を content で上書きする。
    // CJK混在時、文字インデックスと表示列インデックスが一致しないため、
    // 表示幅ベースで文字単位に分解してから置換区間を決定する。
    static readonly System.Text.RegularExpressions.Regex AnsiSgrRegex =
        new System.Text.RegularExpressions.Regex("\x1b\\[[0-9;]*m", System.Text.RegularExpressions.RegexOptions.Compiled);

    // frame[y] の表示列 x から displayWidth 分を content で上書きする。
    // 対象行はディレクトリ色分けなどでANSIエスケープを含みうる。エスケープシーケンスを
    // 直後の可視文字に付随させた「セル」単位で走査することで、表示幅計算にエスケープの
    // バイト数を含めない(ANSIバイトを幅1の通常文字として数えると桁計算がエスケープの
    // 長さ分ズレ、置換範囲がエスケープの途中で切れて色コードの対応が崩れる)。
    // 置換範囲の外側(ボックスの左右)の元の装飾は保持し、置換範囲の直前・直後には
    // 明示的なリセットを挿入することで、色が置換テキストや隣接セルへ漏れるのを防ぐ。
    static void OverwriteFrameRow(string[] frame, int y, int x, string content, int frameWidth, int frameHeight)
    {
        if (y < 0 || y >= frameHeight) return;

        string baseLine = frame[y];
        var escMatches = AnsiSgrRegex.Matches(baseLine);

        var cellText = new List<string>();
        var cellWidth = new List<int>();
        var pendingEscapes = new System.Text.StringBuilder();
        int escIdx = 0;
        int pos = 0;
        while (pos < baseLine.Length)
        {
            if (escIdx < escMatches.Count && escMatches[escIdx].Index == pos)
            {
                pendingEscapes.Append(escMatches[escIdx].Value);
                pos += escMatches[escIdx].Length;
                escIdx++;
                continue;
            }
            cellText.Add(pendingEscapes.ToString() + baseLine[pos]);
            cellWidth.Add(CharacterWidth.GetCharWidth(baseLine[pos]));
            pendingEscapes.Clear();
            pos++;
        }
        if (pendingEscapes.Length > 0)
        {
            // 行末に取り残されたエスケープ(直後に可視文字がない、例: 末尾の直前のリセット)。
            if (cellText.Count > 0)
                cellText[^1] = cellText[^1] + pendingEscapes.ToString();
            else
            {
                cellText.Add(pendingEscapes.ToString());
                cellWidth.Add(0);
            }
        }

        var startCol = new int[cellText.Count];
        int col = 0;
        for (int i = 0; i < cellText.Count; i++)
        {
            startCol[i] = col;
            col += cellWidth[i];
        }
        int totalWidth = col;

        int displayWidth = CharacterWidth.GetStringWidth(StripAnsiForWidth(content));
        if (x < 0 || x >= totalWidth || displayWidth <= 0) return;

        int startIdx = -1, endIdx = -1; // endIdx は exclusive
        for (int i = 0; i < cellText.Count; i++)
        {
            if (startCol[i] >= x && startIdx < 0) startIdx = i;
            if (startCol[i] < x + displayWidth) endIdx = i + 1;
        }
        if (startIdx < 0) return; // 置換範囲が見つからない場合は元の行(frame[y])に手を加えない
        if (endIdx < startIdx) endIdx = startIdx;
        endIdx = Math.Min(endIdx, cellText.Count);

        int removedWidth = 0;
        for (int i = startIdx; i < endIdx; i++)
            removedWidth += cellWidth[i];

        string replacement = content;
        int replacementWidth = CharacterWidth.GetStringWidth(StripAnsiForWidth(replacement));
        if (replacementWidth < removedWidth)
            replacement += new string(' ', removedWidth - replacementWidth);

        var result = new System.Text.StringBuilder();
        for (int i = 0; i < startIdx; i++) result.Append(cellText[i]);
        result.Append("\x1b[0m"); // 除去範囲より前の装飾が置換テキストへ漏れないようリセット
        result.Append(replacement);
        result.Append("\x1b[0m"); // 置換テキストの装飾(反転カーソル等)が後続セルへ漏れないようリセット
        for (int i = endIdx; i < cellText.Count; i++) result.Append(cellText[i]);

        frame[y] = result.ToString();
    }

    // ANSI エスケープシーケンス(色・反転等)を取り除いた表示幅計算用のプレーン文字列を返す。
    static string StripAnsiForWidth(string text)
    {
        return AnsiSgrRegex.Replace(text, "");
    }

    static void WriteLineAt(int x, int y, string text)
    {
        if (y >= Console.WindowHeight)
            return;

        try
        {
            Console.SetCursorPosition(x, y);
            Console.Write(text);
        }
        catch
        {
            // Window resize during write
        }
    }
}
