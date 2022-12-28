using CommandLine;
using Eto.Forms;
using Refresher.CLI;
using Refresher.Patching;
using Refresher.UI;
using WebsiteParser;

namespace Refresher;

public class Program {
    public static Application App;

    [STAThread]
    public static void Main(string[] args)
    {
        HttpClient client = new();
        string html = client.GetStringAsync("http://10.1.0.192/home.ps3mapi/sman.ps3").Result;
        Console.WriteLine(ConsoleId.FromHtml(html).Id);

        return;
        if (args.Length > 0)
        {
            Console.WriteLine("Launching in CLI mode");
            Parser.Default.ParseArguments<CommandLineOptions>(args)
                .WithParsed(CLI.CommandLine.Run);
        }
        else
        {
            Console.WriteLine("Launching in GUI mode");
            App = new Application();
            App.Run(new PatchForm());
            App.Dispose();
        }
    }
}