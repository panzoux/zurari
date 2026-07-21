using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

public class OperationLogTests
{
    static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "zurari-oplog-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Append_ThenSnapshot_ReturnsNewestFirst()
    {
        string dir = NewTempDir();
        try
        {
            OperationLog.ResetForTest(dir);
            var e1 = new OpLogEntry(DateTime.UtcNow, null, "DELETE", "OK", "a.txt", "", "");
            var e2 = new OpLogEntry(DateTime.UtcNow, null, "RENAME", "OK", "b.txt", "c.txt", "");
            OperationLog.Append(e1);
            OperationLog.Append(e2);

            var snap = OperationLog.Snapshot();

            Assert.Equal(2, snap.Count);
            Assert.Equal("RENAME", snap[0].Op);   // newest first
            Assert.Equal("DELETE", snap[1].Op);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Append_Beyond1000_KeepsRingAt1000_NewestFirst()
    {
        string dir = NewTempDir();
        try
        {
            OperationLog.ResetForTest(dir);
            for (int i = 0; i < 1005; i++)
            {
                OperationLog.Append(new OpLogEntry(DateTime.UtcNow, null, "DELETE", "OK", $"file{i}.txt", "", ""));
            }

            var snap = OperationLog.Snapshot();

            Assert.Equal(1000, snap.Count);
            // Newest entry appended was file1004.txt, oldest retained is file5.txt
            Assert.Equal("file1004.txt", snap[0].Source);
            Assert.Equal("file5.txt", snap[999].Source);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Append_WritesPlainTextLineToLogFile()
    {
        string dir = NewTempDir();
        try
        {
            OperationLog.ResetForTest(dir);
            OperationLog.Append(new OpLogEntry(DateTime.UtcNow, 3, "COPY", "OK", "src.txt", "dst.txt", "note"));

            string logPath = Path.Combine(dir, "operations.log");
            Assert.True(File.Exists(logPath));
            string content = File.ReadAllText(logPath);
            Assert.Contains("COPY", content);
            Assert.Contains("src.txt", content);
            Assert.Contains("dst.txt", content);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Append_RotatesLogFileWhenExceeding10MB()
    {
        string dir = NewTempDir();
        try
        {
            OperationLog.ResetForTest(dir);
            string logPath = Path.Combine(dir, "operations.log");
            string rotatedPath = Path.Combine(dir, "operations.log.1");

            // Pre-fill the log file to just under/over the 10MB threshold directly on disk,
            // then append one more entry and expect a rotation to occur.
            byte[] filler = new byte[10 * 1024 * 1024 + 1];
            File.WriteAllBytes(logPath, filler);

            OperationLog.Append(new OpLogEntry(DateTime.UtcNow, null, "DELETE", "OK", "trigger.txt", "", ""));

            Assert.True(File.Exists(rotatedPath));
            Assert.True(File.Exists(logPath));
            // New log file should be small again (only the just-appended line), not the 10MB filler.
            var newInfo = new FileInfo(logPath);
            Assert.True(newInfo.Length < 10 * 1024 * 1024);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Append_NeverThrows_WhenLogDirectoryIsUnwritable()
    {
        // Point at a path that cannot be created as a directory (a file masquerading as a dir path).
        string dir = NewTempDir();
        string blockedPath = Path.Combine(dir, "blocked-file");
        File.WriteAllText(blockedPath, "x");
        try
        {
            // Use a subpath under the file, which cannot exist as a directory.
            string impossibleDir = Path.Combine(blockedPath, "sub");
            OperationLog.ResetForTest(impossibleDir);

            var ex = Record.Exception(() =>
                OperationLog.Append(new OpLogEntry(DateTime.UtcNow, null, "DELETE", "OK", "x.txt", "", "")));

            Assert.Null(ex);
            // Memory ring must still work even if file write silently failed.
            Assert.Single(OperationLog.Snapshot());
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
