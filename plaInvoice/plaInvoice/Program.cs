using Microsoft.Extensions.Configuration;
using PuppeteerSharp;
using PuppeteerSharp.Media;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Net;
using System.Text;

namespace plaInvoice
{
    internal class Program
    {
        static IConfiguration _configuration;
        
        static async Task<int> Main(string[] args)
        {
            SetConfiguration();

            var invoiceValue = new Option<decimal>("--amount", "-a") { Description = "Gross amount to be charged in this invoice" };
            var companyToCharge = new Option<string>("--company", "-c") { Description = "Company that's going to be charged" };
            var outputPath = new Option<string>("--output", "-o") { Description = "[optional] File output path" };
            var extraItems = new Option<LineItem[]>("--item", "-i")
            {
                Description = "[optional, repeatable] Extra charge as \"<description> <amount>\", e.g. -i \"Bonus 100\"",
                Arity = ArgumentArity.ZeroOrMore,
                CustomParser = ParseLineItems
            };

            var rootCommand = new RootCommand("Simple invoice generator");

            var generateInvoiceCommand = new Command("generate", "Generates an invoice with the provided parameters")
            {
                invoiceValue,
                companyToCharge,
                outputPath,
                extraItems
            };
            rootCommand.Subcommands.Add(generateInvoiceCommand);

            generateInvoiceCommand.SetAction((parseResult, cancellationToken) => PrintValue(
                parseResult.GetValue(invoiceValue),
                parseResult.GetValue(companyToCharge)!,
                parseResult.GetValue(outputPath)!,
                parseResult.GetValue(extraItems) ?? []));

            return await rootCommand.Parse(args).InvokeAsync();
        }

        private record LineItem(string Description, decimal Amount);

        ////Each token is "<description> <amount>": the last word is the amount, everything before it the description
        private static LineItem[] ParseLineItems(ArgumentResult result)
        {
            var items = new List<LineItem>();
            foreach (var token in result.Tokens)
            {
                var text = token.Value.Trim();
                var lastSpace = text.LastIndexOf(' ');
                if (lastSpace <= 0 || !decimal.TryParse(text[(lastSpace + 1)..], out var amount))
                {
                    result.AddError($"Invalid item '{token.Value}'. Expected \"<description> <amount>\", e.g. \"Bonus 100\".");
                    return [];
                }
                items.Add(new LineItem(text[..lastSpace].Trim(), amount));
            }
            return items.ToArray();
        }

        private static void SetConfiguration()
        {
            _configuration = new ConfigurationBuilder()
              .AddJsonFile("appsettings.json")
              .Build();
        }

        static async Task PrintValue(decimal value, string company, string outputPath, LineItem[] extraItems)
        {
            var filePath = GetFileOutputPath(outputPath, company);
            if (File.Exists(filePath))
                throw new IOException($"The file '{filePath}' already exists.");

            ////HTML string with a base URL so relative resources (images) resolve, and without the body margin
            ////and stray <br/> that would push the full-page background onto a second page
            string baseUrl = Path.GetFullPath(_configuration.GetValue<string>("resourcesPath"));
            string baseTag = $"<base href=\"{new Uri(baseUrl + Path.DirectorySeparatorChar).AbsoluteUri}\"/>";
            string printStyle = "<style>body { margin: 0; } body > br { display: none; }</style>";
            string htmlText = GetHtml(value, company, extraItems).Replace("<head>", "<head>" + baseTag + printStyle);

            ////Download (once) a Chrome build native to this machine's CPU and launch it headless
            var browserCachePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "plaInvoice", "browser");
            var installedBrowser = await new BrowserFetcher(new BrowserFetcherOptions { Path = browserCachePath }).DownloadAsync();

            await using var browser = await Puppeteer.LaunchAsync(new LaunchOptions
            {
                Headless = true,
                ExecutablePath = installedBrowser.GetExecutablePath()
            });
            await using var page = await browser.NewPageAsync();

            ////Load from a temp file: Chrome only lets file:// pages load local resources
            var tempHtmlPath = Path.Combine(Path.GetTempPath(), $"plaInvoice_{Guid.NewGuid():N}.html");
            await File.WriteAllTextAsync(tempHtmlPath, htmlText);
            try
            {
                await page.GoToAsync(new Uri(tempHtmlPath).AbsoluteUri, WaitUntilNavigation.Load);
                ////The template is a 918x1188px page (US Letter at 1.5x); scale it to fill exactly one Letter page
                await page.PdfAsync(filePath, new PdfOptions
                {
                    Format = PaperFormat.Letter,
                    PrintBackground = true,
                    Scale = 816m / 918m,
                    MarginOptions = new MarginOptions { Top = "0", Right = "0", Bottom = "0", Left = "0" },
                    PageRanges = "1"
                });
            }
            finally
            {
                File.Delete(tempHtmlPath);
            }

            Console.WriteLine($"Invoice saved to {Path.GetFullPath(filePath)}");
        }

        private static string GetFileOutputPath(string outputPath, string company)
        {
            if (!string.IsNullOrWhiteSpace(outputPath))
                return outputPath;

            var rootPath = _configuration.GetValue<string>("rootPathForFileOutput");
            var date = DateTime.Now;

            if (company.ToLower() == "number8" || company.ToLower() == "number 8")
                return rootPath + $"Number8/Invoices/AURORA TI LTDA_invoice_{date:yyyy_MM_dd}.pdf";
            else if (company.ToLower() == "toptal")
                return rootPath + $"Toptal/Invoices/AURORA TI LTDA_invoice_{date:yyyy_MM_dd}.pdf";
            else
                return "default.pdf";
        }

        private static string GetHtml(decimal value, string company, LineItem[] extraItems)
        {
            ////Extra rows go below the main item (top 332px); the totals block moves down by the same amount
            const int firstItemTop = 332;
            const int itemRowHeight = 32;

            var path = Path.GetFullPath(_configuration.GetValue<string>("templatePath"));
            var html = File.ReadAllText(path);

            var extraRows = new StringBuilder();
            for (int i = 0; i < extraItems.Length; i++)
            {
                var top = firstItemTop + (i + 1) * itemRowHeight;
                var description = WebUtility.HtmlEncode(extraItems[i].Description);
                var amount = FormatMoney(extraItems[i].Amount);
                extraRows.AppendLine($"<p style=\"position:absolute;top:{top}px;left:66px;white-space:nowrap\" class=\"ft12\"><b>{description}</b></p>");
                extraRows.AppendLine($"<p style=\"position:absolute;top:{top}px;left:559px;white-space:nowrap\" class=\"ft14\">1</p>");
                extraRows.AppendLine($"<p style=\"position:absolute;top:{top}px;left:674px;white-space:nowrap\" class=\"ft14\">${amount}</p>");
                extraRows.AppendLine($"<p style=\"position:absolute;top:{top}px;left:786px;white-space:nowrap\" class=\"ft14\">${amount}</p>");
            }

            var total = value + extraItems.Sum(item => item.Amount);

            html = html.Replace("KEY_COMPANY", company.ToUpper());
            html = html.Replace("KEY_AMOUNT", FormatMoney(value));
            html = html.Replace("KEY_TOTAL", FormatMoney(total));
            html = html.Replace("KEY_EXTRA_ITEMS", extraRows.ToString());
            html = html.Replace("KEY_ROWS_OFFSET", (extraItems.Length * itemRowHeight).ToString());
            html = html.Replace("KEY-DATE", DateTime.Now.ToString("MMM dd, yyyy"));

            return html;
        }

        private static string FormatMoney(decimal value) => value.ToString("#,##0.00");
    }
}