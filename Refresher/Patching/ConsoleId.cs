using WebsiteParser;
using WebsiteParser.Attributes.StartAttributes;

namespace Refresher.Patching;

public class ConsoleId
{
    [Selector("input[name=idps1]", Attribute = "value")]
    public string FirstPart { get; set; } = "";

    [Selector("input[name=idps2]", Attribute = "value")]
    public string SecondPart { get; set; } = "";

    public string Id => FirstPart + SecondPart;

    public static ConsoleId FromHtml(string html) => WebContentParser.Parse<ConsoleId>(html);
}