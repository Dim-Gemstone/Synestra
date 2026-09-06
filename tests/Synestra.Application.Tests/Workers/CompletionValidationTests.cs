using Synestra.Application.Workers;
using Xunit;

namespace Synestra.Application.Tests.Workers;

public sealed class CompletionValidationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("true")]
    [InlineData("42")]
    [InlineData("\"text\"")]
    [InlineData("{\"a\":1,\"a\":2}")]
    [InlineData("{\"a\":1,\"\\u0061\":2}")]
    [InlineData("{\"array\":[{\"a\":1,\"a\":2}]}")]
    [InlineData("{\"\\u0000\":1}")]
    [InlineData("{\"a\":\"\\u0000\"}")]
    [InlineData("{\"a\":\"\\uD800\"}")]
    [InlineData("{\"\\uD800\":1}")]
    [InlineData("{\"a\":1e131072}")]
    [InlineData("{\"a\":1e-16384}")]
    [InlineData("{\"a\":1e2147483648}")]
    public void Result_RejectsInvalidOrUnstorableData(string? result) =>
        Assert.False(CompletionValidation.IsValid(new(Guid.CreateVersion7(), "succeeded", result)));

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"A\":1,\"a\":2}")]
    [InlineData("{\"a\":[true,null,1.25,\"Привіт\",{}]}")]
    [InlineData("{\"a\":1e131071}")]
    [InlineData("{\"a\":1e-16383}")]
    public void Result_AcceptsOpaqueObjectsAndJsonbNumericBoundaries(string result) =>
        Assert.True(CompletionValidation.IsValid(new(Guid.CreateVersion7(), "succeeded", result)));

    [Fact]
    public void Result_UsesReceivedUtf8BytesIncludingEscapingAndWhitespace()
    {
        var report = new CompletionReport(Guid.CreateVersion7(), "succeeded", "{\"a\":\"" + new string('x', 65528) + "\"}");
        Assert.True(CompletionValidation.IsValid(report));
        Assert.False(CompletionValidation.IsValid(report with { Result = report.Result + " " }));
        Assert.False(CompletionValidation.IsValid(report with { Result = "{\"a\":\"" + new string('я', 32765) + "\"}" }));
        Assert.False(CompletionValidation.IsValid(report with { Result = "{\"a\":\"" + string.Concat(Enumerable.Repeat("\\u0061", 11000)) + "\"}" }));
    }

    [Fact]
    public void Result_DepthCountsRootAndNestedArrays()
    {
        var report = new CompletionReport(Guid.CreateVersion7(), "succeeded", "{\"a\":" + new string('[', 31) + "0" + new string(']', 31) + "}");
        Assert.True(CompletionValidation.IsValid(report));
        Assert.False(CompletionValidation.IsValid(report with { Result = "{\"a\":" + new string('[', 32) + "0" + new string(']', 32) + "}" }));
    }

    [Fact]
    public void DiscriminatorAndReportId_AreStrict()
    {
        var report = new CompletionReport(Guid.CreateVersion7(), "succeeded", "{}");
        foreach (var outcome in new[] { "Succeeded", "SUCCEEDED", " failed", "failure", "", "unknown" })
            Assert.False(CompletionValidation.IsValid(report with { Outcome = outcome }));
        foreach (var id in new[] { Guid.Empty, Guid.NewGuid(), Guid.Parse("019ec569-5a00-7000-0000-000000000001") })
            Assert.False(CompletionValidation.IsValid(report with { ReportId = id }));
        Assert.False(CompletionValidation.IsValid(report with { Error = new("error", "message") }));
        Assert.False(CompletionValidation.IsValid(report with { Outcome = "failed", Error = new("error", "message") }));
        Assert.False(CompletionValidation.IsValid(report with { Outcome = "failed", Result = null }));
    }

    [Fact]
    public void Error_UsesCharacterLimitsAndRejectsWhitespaceNulAndInvalidUnicode()
    {
        var report = new CompletionReport(Guid.CreateVersion7(), "failed", Error: new(new string('я', 100), new string('я', 2000)));
        Assert.True(CompletionValidation.IsValid(report));
        foreach (var code in new[] { "", " ", "\u2000", "\0", "a\0", "\ud800", new string('x', 101) })
            Assert.False(CompletionValidation.IsValid(report with { Error = report.Error! with { Code = code } }));
        foreach (var message in new[] { "", " ", "\u2000", "\0", "a\0", "\ud800", new string('x', 2001) })
            Assert.False(CompletionValidation.IsValid(report with { Error = report.Error! with { Message = message } }));
    }

    [Fact]
    public void Token_HasCanonical256BitRepresentationAndRejectsAlternativeSpellings()
    {
        var first = LeaseToken.Generate();
        var second = LeaseToken.Generate();
        Assert.NotEqual(first, second);
        Assert.Equal(43, first.Length);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", first);
        Assert.Equal(32, LeaseToken.Hash(first)!.Length);
        Assert.Equal(LeaseToken.Hash(first), LeaseToken.Hash(first));
        foreach (var invalid in new[] { null, "", " ", first + "=", first + "," + first, first[..42], new string('+', 43), new string('/', 43), new string('A', 42) + "B" })
            Assert.Null(LeaseToken.Hash(invalid));
    }
}
