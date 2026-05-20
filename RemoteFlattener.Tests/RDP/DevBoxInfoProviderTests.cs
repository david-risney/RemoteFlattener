using System;
using System.IO;
using RemoteFlattener.RDP;
using Xunit;

namespace RemoteFlattener.Tests.RDP;

public class DevBoxInfoProviderTests : IDisposable
{
    private readonly string _tempDir;

    public DevBoxInfoProviderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DevBoxInfoProviderTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        DevBoxInfoProvider.ResetCache();
    }

    public void Dispose()
    {
        DevBoxInfoProvider.ResetCache();
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public void ReturnsNull_WhenNotDevBox()
    {
        var result = DevBoxInfoProvider.GetDevBoxFriendlyName(_tempDir, _ => null);
        Assert.Null(result);
    }

    [Fact]
    public void ReturnsNull_WhenIsDevBoxFalse()
    {
        var result = DevBoxInfoProvider.GetDevBoxFriendlyName(_tempDir, name => name == "IsDevBox" ? "False" : null);
        Assert.Null(result);
    }

    [Fact]
    public void ReturnsNull_WhenDirectoryDoesNotExist()
    {
        var result = DevBoxInfoProvider.GetDevBoxFriendlyName(
            Path.Combine(_tempDir, "nonexistent"),
            name => name == "IsDevBox" ? "True" : null);
        Assert.Null(result);
    }

    [Fact]
    public void ReturnsNull_WhenNoConfigFiles()
    {
        var result = DevBoxInfoProvider.GetDevBoxFriendlyName(_tempDir, name => name == "IsDevBox" ? "True" : null);
        Assert.Null(result);
    }

    [Fact]
    public void ReturnsFriendlyName_FromValidConfig()
    {
        var subDir = Path.Combine(_tempDir, "1.0.0", "some-guid");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, "appsettings.Production.json"), """
        {
            "DevBoxAgent": {
                "metadata": {
                    "devBoxDataplaneId": "tenant-id:dev-center:project:pool:my-devbox"
                }
            }
        }
        """);

        var result = DevBoxInfoProvider.GetDevBoxFriendlyName(_tempDir, name => name == "IsDevBox" ? "True" : null);
        Assert.Equal("my-devbox", result);
    }

    [Fact]
    public void CachesSuccessfulResult()
    {
        var subDir = Path.Combine(_tempDir, "1.0.0", "guid1");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, "appsettings.Production.json"), """
        {
            "DevBoxAgent": {
                "metadata": {
                    "devBoxDataplaneId": "a:b:c:d:cached-name"
                }
            }
        }
        """);

        var result1 = DevBoxInfoProvider.GetDevBoxFriendlyName(_tempDir, name => name == "IsDevBox" ? "True" : null);
        Assert.Equal("cached-name", result1);

        // Delete the file — cached value should still be returned
        Directory.Delete(_tempDir, true);
        var result2 = DevBoxInfoProvider.GetDevBoxFriendlyName(_tempDir, name => name == "IsDevBox" ? "True" : null);
        Assert.Equal("cached-name", result2);
    }

    [Fact]
    public void DoesNotCacheNull_RetriesOnNextCall()
    {
        // First call: no config file → null
        var result1 = DevBoxInfoProvider.GetDevBoxFriendlyName(_tempDir, name => name == "IsDevBox" ? "True" : null);
        Assert.Null(result1);

        // Now create the config file
        var subDir = Path.Combine(_tempDir, "1.0.0", "guid1");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, "appsettings.Production.json"), """
        {
            "DevBoxAgent": {
                "metadata": {
                    "devBoxDataplaneId": "a:b:c:d:retry-name"
                }
            }
        }
        """);

        var result2 = DevBoxInfoProvider.GetDevBoxFriendlyName(_tempDir, name => name == "IsDevBox" ? "True" : null);
        Assert.Equal("retry-name", result2);
    }

    [Fact]
    public void SkipsMalformedJson()
    {
        var subDir = Path.Combine(_tempDir, "1.0.0", "guid1");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, "appsettings.Production.json"), "not valid json");

        var result = DevBoxInfoProvider.GetDevBoxFriendlyName(_tempDir, name => name == "IsDevBox" ? "True" : null);
        Assert.Null(result);
    }

    [Fact]
    public void SkipsConfig_MissingDevBoxDataplaneId()
    {
        var subDir = Path.Combine(_tempDir, "1.0.0", "guid1");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, "appsettings.Production.json"), """
        {
            "DevBoxAgent": {
                "metadata": {
                    "otherField": "value"
                }
            }
        }
        """);

        var result = DevBoxInfoProvider.GetDevBoxFriendlyName(_tempDir, name => name == "IsDevBox" ? "True" : null);
        Assert.Null(result);
    }

    [Fact]
    public void SkipsConfig_EmptyFriendlyName()
    {
        var subDir = Path.Combine(_tempDir, "1.0.0", "guid1");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, "appsettings.Production.json"), """
        {
            "DevBoxAgent": {
                "metadata": {
                    "devBoxDataplaneId": "a:b:c:d:"
                }
            }
        }
        """);

        var result = DevBoxInfoProvider.GetDevBoxFriendlyName(_tempDir, name => name == "IsDevBox" ? "True" : null);
        Assert.Null(result);
    }

    [Fact]
    public void FindsValidConfig_AmongMultipleFiles()
    {
        // First directory has malformed config
        var subDir1 = Path.Combine(_tempDir, "1.0.0", "guid1");
        Directory.CreateDirectory(subDir1);
        File.WriteAllText(Path.Combine(subDir1, "appsettings.Production.json"), "bad json");

        // Second directory has valid config
        var subDir2 = Path.Combine(_tempDir, "2.0.0", "guid2");
        Directory.CreateDirectory(subDir2);
        File.WriteAllText(Path.Combine(subDir2, "appsettings.Production.json"), """
        {
            "DevBoxAgent": {
                "metadata": {
                    "devBoxDataplaneId": "a:b:c:d:found-it"
                }
            }
        }
        """);

        var result = DevBoxInfoProvider.GetDevBoxFriendlyName(_tempDir, name => name == "IsDevBox" ? "True" : null);
        Assert.Equal("found-it", result);
    }

    [Fact]
    public void ExtractFriendlyName_HandlesNoColons()
    {
        var file = Path.Combine(_tempDir, "test.json");
        File.WriteAllText(file, """
        {
            "DevBoxAgent": {
                "metadata": {
                    "devBoxDataplaneId": "nocolons"
                }
            }
        }
        """);

        // No colon means lastIndexOf returns -1, should return null
        var result = DevBoxInfoProvider.ExtractFriendlyName(file);
        Assert.Null(result);
    }
}
