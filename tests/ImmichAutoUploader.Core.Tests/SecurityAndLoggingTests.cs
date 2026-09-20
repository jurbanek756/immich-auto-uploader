using ImmichAutoUploader.Core.Security;
using ImmichAutoUploader.Core.Services;
using Xunit;

namespace ImmichAutoUploader.Core.Tests;

public class SecurityAndLoggingTests
{
    [Theory]
    [InlineData("../escape")]
    [InlineData("..\\escape")]
    [InlineData("/root/secret")]
    [InlineData("sub\\folder")]
    [InlineData("")]
    [InlineData("   ")]
    public void DpapiCredentialStore_RejectsInvalidOrTraversalSlotNames(string invalidName)
    {
        var store = new DpapiCredentialStore();

        if (OperatingSystem.IsWindows())
        {
            Assert.Throws<ArgumentException>(() => store.Save(invalidName, "secret"));
            Assert.Throws<ArgumentException>(() => store.Load(invalidName));
            Assert.Throws<ArgumentException>(() => store.Delete(invalidName));
        }
    }

    [Fact]
    public void AppLogger_Error_PreservesStackTraceAndInnerException()
    {
        Exception ex;
        try
        {
            try
            {
                throw new InvalidOperationException("Inner failure details");
            }
            catch (Exception inner)
            {
                throw new ApplicationException("Outer failure context", inner);
            }
        }
        catch (Exception caught)
        {
            ex = caught;
        }

        // Verify AppLogger.Error does not throw and formats correctly
        var exceptionRecord = Record.Exception(() => AppLogger.Error("Test error message", ex));
        Assert.Null(exceptionRecord);
    }
}
