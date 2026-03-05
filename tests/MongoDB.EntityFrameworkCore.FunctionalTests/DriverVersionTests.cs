using System.Reflection;
using MongoDB.Driver;

namespace MongoDB.EntityFrameworkCore.FunctionalTests;

public class DriverVersionTests
{
    [Fact]
    public void TestDriverVersion()
    {
        var envDriverVersion = Environment.GetEnvironmentVariable("DRIVER_VERSION");
        if (string.IsNullOrEmpty(envDriverVersion) || envDriverVersion == "latest")
        {
            return;
        }

        var driverAssembly = typeof(IMongoClient).Assembly;
        var versionAttribute = driverAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();

        Assert.NotNull(versionAttribute);
        var driverVersion = versionAttribute.InformationalVersion;
        var hashIndex = driverVersion.IndexOf('+');
        if (hashIndex != -1)
        {
            driverVersion = versionAttribute.InformationalVersion.Substring(0, hashIndex);
        }

        Assert.Equal(envDriverVersion, driverVersion);
    }
}

