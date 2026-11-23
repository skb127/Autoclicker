using System;
using System.IO;
using Autoclicker.Logging;
using Xunit;

namespace Autoclicker.Tests;

public sealed class LogMaintenanceTests : IDisposable
{
    private readonly string _tempDir;

    public LogMaintenanceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "AutoclickerLogMaintenanceTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup
        }
    }

    [Fact]
    public void GetStats_WhenDirectoryMissingOrNull_ReturnsZero()
    {
        // Arrange
        string missingDirectory = Path.Combine(_tempDir, "nonexistent");

        // Act
        LogFolderStats nullStats = LogMaintenance.GetStats(null);
        LogFolderStats missingStats = LogMaintenance.GetStats(missingDirectory);

        // Assert
        Assert.Equal(new LogFolderStats(0, 0), nullStats);
        Assert.Equal(new LogFolderStats(0, 0), missingStats);
    }

    [Fact]
    public void GetStats_CountsOnlyMatchingLogFiles()
    {
        // Arrange
        string log1 = Path.Combine(_tempDir, "autoclicker-20261119.log");
        string log2 = Path.Combine(_tempDir, "autoclicker-20261120.log");
        string unrelated = Path.Combine(_tempDir, "other.txt");

        File.WriteAllBytes(log1, new byte[120]);
        File.WriteAllBytes(log2, new byte[280]);
        File.WriteAllBytes(unrelated, new byte[999]);

        // Act
        var stats = LogMaintenance.GetStats(_tempDir);

        // Assert
        Assert.Equal(2, stats.FileCount);
        Assert.Equal(400, stats.TotalBytes);
    }

    [Fact]
    public void DeleteOldLogs_PreservesMostRecentLogAndUnrelatedFiles()
    {
        // Arrange
        string oldLog1 = Path.Combine(_tempDir, "autoclicker-20261118.log");
        string oldLog2 = Path.Combine(_tempDir, "autoclicker-20261119.log");
        string newestLog = Path.Combine(_tempDir, "autoclicker-20261121.log");
        string unrelated = Path.Combine(_tempDir, "notes.txt");

        File.WriteAllBytes(oldLog1, new byte[100]);
        File.WriteAllBytes(oldLog2, new byte[250]);
        File.WriteAllBytes(newestLog, new byte[500]);
        File.WriteAllBytes(unrelated, new byte[50]);

        var baseTime = new DateTime(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(oldLog1, baseTime.AddDays(-3));
        File.SetLastWriteTimeUtc(oldLog2, baseTime.AddDays(-1));
        File.SetLastWriteTimeUtc(newestLog, baseTime);

        // Act
        var result = LogMaintenance.DeleteOldLogs(_tempDir);

        // Assert
        Assert.Equal(2, result.DeletedFiles);
        Assert.Equal(350, result.FreedBytes);
        Assert.Equal(0, result.Failed);

        Assert.False(File.Exists(oldLog1));
        Assert.False(File.Exists(oldLog2));
        Assert.True(File.Exists(newestLog));
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public void DeleteOldLogs_WhenSingleLogExists_DeletesNothing()
    {
        // Arrange
        string onlyLog = Path.Combine(_tempDir, "autoclicker-20261121.log");
        File.WriteAllBytes(onlyLog, new byte[300]);

        // Act
        var result = LogMaintenance.DeleteOldLogs(_tempDir);

        // Assert
        Assert.Equal(0, result.DeletedFiles);
        Assert.Equal(0, result.FreedBytes);
        Assert.Equal(0, result.Failed);
        Assert.True(File.Exists(onlyLog));
    }
}
