using BetterGenshinImpact.GameTask.AutoQuest.Process;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoQuest.Process;

public class QuestProcessParserTests
{
    [Fact]
    public void Parse_ExtendedText_PreservesMetadataBlocksAndCommands()
    {
        const string content = """
                               作者：星野
                               描述：测试流程

                               与特纳对话：
                               地图追踪 路线1.json
                               追踪图标 Question
                               按键 长按 Shift,W 4000

                               默认：
                               提示 fallback # 尾部注释
                               任务完成
                               """;

        var result = new QuestProcessParser().Parse(content, @"C:\quest\process.json");

        Assert.Equal("星野", result.Author);
        Assert.Equal("测试流程", result.Description);
        Assert.Equal(2, result.Blocks.Count);
        Assert.False(result.Blocks[0].IsDefault);
        Assert.Equal("与特纳对话", result.Blocks[0].Description);
        Assert.Collection(
            result.Blocks[0].Instructions,
            instruction => Assert.Equal(QuestProcessCommandType.MapTracking, instruction.Type),
            instruction =>
            {
                Assert.Equal(QuestProcessCommandType.TrackIcon, instruction.Type);
                Assert.Equal("Question", instruction.Argument);
            },
            instruction => Assert.Equal("长按 Shift,W 4000", instruction.Argument));
        Assert.True(result.Blocks[1].IsDefault);
        Assert.Equal("fallback", result.Blocks[1].Instructions[0].Argument);
        Assert.Equal(QuestProcessCommandType.Complete, result.Blocks[1].Instructions[1].Type);
    }

    [Fact]
    public void Parse_LegacyLines_MapsJsonAndFToCompatibleCommands()
    {
        const string content = """
                               route.json
                               F 纳西妲
                               F
                               """;

        var result = new QuestProcessParser().Parse(content);

        var block = Assert.Single(result.Blocks);
        Assert.True(block.IsDefault);
        Assert.Equal(QuestProcessCommandType.MapTracking, block.Instructions[0].Type);
        Assert.Equal("route.json", block.Instructions[0].Argument);
        Assert.Equal(QuestProcessCommandType.Dialogue, block.Instructions[1].Type);
        Assert.Equal("纳西妲", block.Instructions[1].Argument);
        Assert.Equal(QuestProcessCommandType.Dialogue, block.Instructions[2].Type);
        Assert.Empty(block.Instructions[2].Argument);
    }

    [Fact]
    public void Parse_JsonArray_MapsStringAndObjectSteps()
    {
        const string content = """
                               [
                                 "route.json",
                                 { "type": "等待", "data": 250 },
                                 { "type": "追踪图标", "data": "Into" }
                               ]
                               """;

        var result = new QuestProcessParser().Parse(content);

        var instructions = Assert.Single(result.Blocks).Instructions;
        Assert.Equal(QuestProcessCommandType.MapTracking, instructions[0].Type);
        Assert.Equal(QuestProcessCommandType.Wait, instructions[1].Type);
        Assert.Equal("250", instructions[1].Argument);
        Assert.Equal(QuestProcessCommandType.TrackIcon, instructions[2].Type);
        Assert.Equal("Into", instructions[2].Argument);
    }

    [Theory]
    [InlineData("与 纳西妲 对话！", "与纳西妲对话", 1)]
    [InlineData("Find NPC-01", "findnpc01", 1)]
    public void TextMatcher_NormalizesLikePlugin(string left, string right, double expected)
    {
        Assert.Equal(expected, QuestProcessTextMatcher.Similarity(left, right), 6);
    }
}
