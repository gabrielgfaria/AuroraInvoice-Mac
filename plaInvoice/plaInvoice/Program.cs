using Microsoft.Extensions.Configuration;
using Syncfusion.HtmlConverter;
using Syncfusion.Pdf;
using System.CommandLine;

namespace plaInvoice
{
    internal class Program
    {
        static IConfiguration _configuration;
        
        static async Task<int> Main(string[] args)
        {
            SetConfiguration();
            ConfigureSyncFusion(args);
            
            var invoiceValue = new Option<decimal>(new string[] { "-a", "--amount" }, "Gross amount to be charged in this invoice");
            var companyToCharge = new Option<string>(new string[] { "-c", "--company" }, "Company that's going to be charged");
            var outputPath = new Option<string>(new string[] { "-o", "--output" }, "[optional] File output path");

            var rootCommand = new RootCommand("Simple invoice generator");

            var generateInvoiceCommand = new Command("generate", "Generates an invoice with the provided parameters")
            {
                invoiceValue,
                companyToCharge,
                outputPath
            };
            rootCommand.AddCommand(generateInvoiceCommand);

            generateInvoiceCommand.SetHandler(PrintValue, invoiceValue, companyToCharge, outputPath);

            return await rootCommand.InvokeAsync(args);
        }

        private static void SetConfiguration()
        {
            _configuration = new ConfigurationBuilder()
              .AddJsonFile("appsettings.json")
              .Build();
        }

        private static void ConfigureSyncFusion(string[] args)
        {
            var syncFusionKey = _configuration.GetValue<string>("syncfusionApplicationKey");
            Syncfusion.Licensing.SyncfusionLicenseProvider.RegisterLicense(syncFusionKey);
        }

        static void PrintValue(decimal value, string company, string outputPath)
        {
            ////Initialize HTML to PDF converter
            HtmlToPdfConverter htmlConverter = new HtmlToPdfConverter();
            BlinkConverterSettings blinkConverterSettings = new BlinkConverterSettings();
            blinkConverterSettings.ViewPortSize = new Syncfusion.Drawing.Size(1024, 0);
            htmlConverter.ConverterSettings = blinkConverterSettings;

            ////HTML string and Base URL 
            string htmlText = GetHtml(value, company);
            string baseUrl = Path.GetFullPath(_configuration.GetValue<string>("resourcesPath"));

            ////Convert URL to PDF
            PdfDocument document = htmlConverter.Convert(htmlText, baseUrl);
            FileStream fileStream = new FileStream(@$"{GetFileOutputPath(outputPath, company)}", FileMode.CreateNew, FileAccess.ReadWrite);
            
            ////Save and close the PDF document.
            document.Save(fileStream);
            document.Close(true);
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

        private static string GetHtml(decimal value, string company)
        {
            var path = Path.GetFullPath(_configuration.GetValue<string>("templatePath"));
            var html = File.ReadAllText(path);

            html = html.Replace("KEY_COMPANY", company.ToUpper());
            html = html.Replace("KEY_AMOUNT", value.ToString("#,##0.00"));
            html = html.Replace("KEY-DATE", DateTime.Now.ToString("MMM dd, yyyy"));

            return html;
        }
    }
}