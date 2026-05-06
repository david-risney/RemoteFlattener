using System;
using System.IO;
using Xunit;

namespace RemoteFlattener.Tests;

public class AppPathsTests
{
    [Fact]
    public void DataDirectory_IsNotNullOrEmpty()
    {
        Assert.False(string.IsNullOrEmpty(AppPaths.DataDirectory));
    }

    [Fact]
    public void DataDirectory_IsAbsolutePath()
    {
        Assert.True(Path.IsPathRooted(AppPaths.DataDirectory));
    }

    [Fact]
    public void DataDirectory_ContainsAppName()
    {
        Assert.Contains("RemoteFlattener", AppPaths.DataDirectory, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DataDirectory_IsUnderLocalAppData()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.StartsWith(localAppData, AppPaths.DataDirectory, StringComparison.OrdinalIgnoreCase);
    }
}
