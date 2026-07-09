using LlamaSwapManager.ViewModels;
using Xunit;

namespace LlamaSwapManager.Tests;

/// <summary>
/// Golden-path coverage for ModelEditItem.BuildCmd / Parse — reasoning, gpu-layers, chat-template.
/// </summary>
public class ModelCmdBuilderTests
{
    [Theory]
    [InlineData("off", "--reasoning off")]
    [InlineData("on", "--reasoning on")]
    [InlineData("auto", "--reasoning auto")]
    public void BuildCmd_EmitsReasoningValue(string value, string expectedSnippet)
    {
        var item = new ModelEditItem
        {
            LlamaServerPath = "/bin/llama-server",
            Reasoning = value
        };
        var cmd = item.BuildCmd();
        Assert.Contains(expectedSnippet, cmd);
    }

    [Fact]
    public void BuildCmd_OmitsReasoningWhenEmpty()
    {
        var item = new ModelEditItem
        {
            LlamaServerPath = "/bin/llama-server",
            Reasoning = ""
        };
        Assert.DoesNotContain("--reasoning", item.BuildCmd());
    }

    [Fact]
    public void BuildCmd_TreatsDefaultTokenAsOmit()
    {
        var item = new ModelEditItem
        {
            LlamaServerPath = "/bin/llama-server",
            Reasoning = "(default)"
        };
        Assert.DoesNotContain("--reasoning", item.BuildCmd());
    }

    [Theory]
    [InlineData("40")]
    [InlineData("all")]
    [InlineData("0")]
    [InlineData("auto")]
    public void BuildCmd_EmitsGpuLayersNumericOrKeyword(string layers)
    {
        var item = new ModelEditItem
        {
            LlamaServerPath = "/bin/llama-server",
            GpuLayers = layers
        };
        Assert.Contains($"--gpu-layers {layers}", item.BuildCmd());
    }

    [Fact]
    public void BuildCmd_EmitsChatTemplate()
    {
        var item = new ModelEditItem
        {
            LlamaServerPath = "/bin/llama-server",
            ChatTemplate = "chatml"
        };
        Assert.Contains("--chat-template chatml", item.BuildCmd());
    }

    [Fact]
    public void BuildCmd_QuotesChatTemplatePath()
    {
        var item = new ModelEditItem
        {
            LlamaServerPath = "/bin/llama-server",
            ChatTemplate = "/tmp/foo bar.jinja"
        };
        var cmd = item.BuildCmd();
        Assert.Contains("--chat-template", cmd);
        Assert.Contains("foo bar.jinja", cmd);
    }

    [Fact]
    public void Parse_RoundTripReasoningOffGenderGpuAndTemplate()
    {
        var config = new LlamaSwapManager.Models.ModelConfig
        {
            Name = "m1",
            Cmd = "/bin/llama-server --reasoning off --gpu-layers 33 --chat-template chatml"
        };
        var item = ModelEditItem.Parse("m1", config);
        Assert.Equal("off", item.Reasoning);
        Assert.Equal("33", item.GpuLayers);
        Assert.Equal("chatml", item.ChatTemplate);
        Assert.Contains("--reasoning off", item.BuildCmd());
        Assert.Contains("--gpu-layers 33", item.BuildCmd());
        Assert.Contains("--chat-template chatml", item.BuildCmd());
    }
}
