using Prospect.Client.Diagnostics;
using Xunit;

namespace Prospect.Tests;

public sealed class RtsCaptureTests
{
    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(1, 1, false)]
    [InlineData(1, 0, true)]
    public void CaptureCompletionRequiresEverySuccessfulSave(int saved, int failed, bool complete)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "[{}]");
            var capture = new RtsCapture(path);
            Assert.False(capture.Report(false, 0, 0, saved, failed));
            using var metadata = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path + ".csv.meta.json"));
            Assert.Equal(complete, metadata.RootElement.GetProperty("captures_complete"u8).GetBoolean());
            Assert.False(metadata.RootElement.GetProperty("verified"u8).GetBoolean());
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".csv");
            File.Delete(path + ".csv.meta.json");
        }
    }

    [Fact]
    public void ScriptAcceptsNumericPosesAndRejectsUnboundedOrNegativeHolds()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "[{\"x\":0,\"y\":0,\"z\":0,\"distance\":90000,\"yaw\":0,\"pitch\":-0.8,\"hold\":0.1}]");
            Assert.Equal(1, new RtsCapture(path).Count);
            File.WriteAllText(path, "[]");
            Assert.Throws<InvalidDataException>(() => new RtsCapture(path));
            File.WriteAllText(path, "[{\"hold\":-1}]");
            Assert.Throws<InvalidDataException>(() => new RtsCapture(path));
            File.WriteAllText(path, "[" + string.Join(',', Enumerable.Repeat("{}", 257)) + "]");
            Assert.Throws<InvalidDataException>(() => new RtsCapture(path));
        }
        finally { File.Delete(path); }
    }
}
