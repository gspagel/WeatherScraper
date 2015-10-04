using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Mail;

namespace WeatherScraper
{
    internal sealed class Program
    {
        /// <summary>
        ///     The mail subject
        /// </summary>
        private const string MailSubject = "WeatherScraper Script Problem Notification";

        /// <summary>
        ///     Gets or sets the sender email address.
        /// </summary>
        /// <value>
        ///     The sender email address.
        /// </value>
        private static string MailFrom { get; set; }

        /// <summary>
        ///     Gets or sets the recipient email address.
        /// </summary>
        /// <value>
        ///     The recipient email address.
        /// </value>
        private static string MailTo { get; set; }

        /// <summary>
        ///     Gets or sets the mail server.
        /// </summary>
        /// <value>
        ///     The mail server.
        /// </value>
        private static string MailServer { get; set; }

        /// <summary>
        ///     Gets or sets the source URLs for weather data.
        /// </summary>
        /// <value>
        ///     The source URLs.
        /// </value>
        private static IEnumerable<WeatherDataSources> Sources { get; set; }

        /// <summary>
        ///     Gets or sets the data path.
        /// </summary>
        /// <value>
        ///     The data path.
        /// </value>
        private static string DataPath { get; set; }

        /// <summary>
        ///     Gets or sets the configuration file.
        /// </summary>
        /// <value>
        ///     The configuration file.
        /// </value>
        private static string ConfigurationFile { get; set; }

        /// <summary>
        ///     Gets or sets the HTML document.
        /// </summary>
        /// <value>
        ///     The HTML document.
        /// </value>
        private static HtmlAgilityPack.HtmlDocument HtmlDocument { get; set; }

        /// <summary>
        ///     Program entry point.
        /// </summary>
        /// <param name="args">The arguments.</param>
        /// <exception cref="System.ArgumentException">
        ///     You must specify a valid configuration file that specifies the weather data source URL(s).
        ///     or
        ///     You must specify a valid path in which to store the weather data file(s).
        /// </exception>
        public static void Main(string[] args)
        {
            try
            {
                Execute(args);
            }
            catch (FileNotFoundException)
            {
                // The third-party libraries are likely missing
                Console.WriteLine("\r\nERROR: The required third-party library files are missing or corrupt. Please ensure that you copy all of the DLL files that accompanied this package into the same folder in which this executable resides.");
            }
        }

        private static void Execute(string[] args)
        {
            try
            {
                bool showHelp = false;

                NDesk.Options.OptionSet optionSet = new NDesk.Options.OptionSet { { "f|file=", "the configuration {FILE} to use", v => ConfigurationFile = v }, { "p|path=", "the {PATH} to which the system will save the data file", v => DataPath = v }, { "h|help", "show this message and exit", v => showHelp = v != null }, { "mf|mailfrom=", "the email address {FROM} which the system sends notifications", v => MailFrom = v }, { "mt|mailto=", "the email address {TO} which the system sends notifications", v => MailTo = v }, { "ms|mailserver=", "the SMTP {SERVER} through which the system sends notifications", v => MailServer = v } };

                optionSet.Parse(args);

                if (showHelp)
                {
                    Console.WriteLine();
                    ShowHelp(optionSet);
                    return;
                }

                // Ensure that the command was invoked with a valid configuration file
                if (string.IsNullOrWhiteSpace(ConfigurationFile) || !File.Exists(ConfigurationFile) || !IsValidConfigurationFile(ConfigurationFile))
                {
                    throw new ArgumentException(string.Format("You must specify a valid configuration file that specifies the weather data source URL(s). Specified file was:\r\n\r\n{0}", ConfigurationFile));
                }

                // Ensure that the command was invoked with a valid data file path
                if (string.IsNullOrWhiteSpace(DataPath) || !Directory.Exists(DataPath))
                {
                    throw new ArgumentException(string.Format("You must specify a valid path in which to store the weather data file(s). Specified path was:\r\n\r\n{0}", DataPath));
                }

                GetWeatherData();
            }
            catch (Exception x)
            {
                Console.WriteLine();
                Console.WriteLine("ERROR: ");
                Console.WriteLine(x.Message);
                Console.WriteLine();
                Console.WriteLine("Try 'weatherscraper --help' for more information.");

                WriteToEventApplicationLog(x);
                SendMail(x.Message);
            }
        }

        /// <summary>
        ///     Determines whether the configuration file specified on invocation is valid.
        /// </summary>
        /// <param name="configurationFile">The configuration file.</param>
        /// <returns></returns>
        private static bool IsValidConfigurationFile(string configurationFile)
        {
            Debug.Assert(!string.IsNullOrWhiteSpace(configurationFile), "A valid configuration file must exist.");

            try
            {
                // Open the configuration file and get contents as a string
                if (File.Exists(configurationFile))
                {
                    using (StreamReader reader = new StreamReader(configurationFile))
                    {
                        string json = reader.ReadToEnd();

                        if (string.IsNullOrWhiteSpace(json))
                        {
                            return false;
                        }

                        // Convert the JSON to a collection of URLs
                        Sources = Newtonsoft.Json.JsonConvert.DeserializeObject<IEnumerable<WeatherDataSources>>(json);
                    }
                }
                else
                {
                    return false;
                }
            }
            catch (Newtonsoft.Json.JsonException x)
            {
                WriteToEventApplicationLog(x);
                SendMail(x.Message);
                return false;
            }
            catch (Exception x)
            {
                WriteToEventApplicationLog(x);
                SendMail(x.Message);
                return false;
            }

            return true;
        }

        /// <summary>
        ///     Gets the weather data.
        /// </summary>
        private static void GetWeatherData()
        {
            Debug.Assert(Sources != null && Sources.Any(), "Weather data sources cannot be null or empty.");

            foreach (WeatherDataSources source in Sources)
            {
                try
                {
                    // Validate the format of the URL
                    Uri sourceUri;
                    if (Uri.TryCreate(source.Url, UriKind.Absolute, out sourceUri) && sourceUri.Scheme == Uri.UriSchemeHttp)
                    {
                        using (System.Net.WebClient client = new System.Net.WebClient())
                        {
                            string html = client.DownloadString(sourceUri);

                            if (!string.IsNullOrWhiteSpace(html))
                            {
                                // Get the first line of HTML
                                HtmlDocument = new HtmlAgilityPack.HtmlDocument();
                                HtmlDocument.LoadHtml(html);

                                DataParser parser = new DataParser();

                                // TODO: Evaluate whether to use an enumerator here instead of strings
                                switch (source.Airport.ToLowerInvariant())
                                {
                                    case "cynr":
                                        parser.SetParseStrategy(new CynrParse());
                                        break;
                                    case "cet2":
                                        parser.SetParseStrategy(new Cet2Parse());
                                        break;
                                    case "cfg6":
                                        parser.SetParseStrategy(new Cfg6Parse());
                                        break;
                                    default:
                                        throw new InvalidOperationException("Invalid airport code specified in the configuration file.");
                                }

                                string data = parser.Parse();

                                // Parse and store the data
                                if (!string.IsNullOrWhiteSpace(data))
                                {
                                    // Does the string look as we expect?
                                    const string pattern = @"^(METAR|SPECI)\s\w{4}\s\d{6}Z(.*)$";

                                    System.Text.RegularExpressions.Regex expression = new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.Compiled);
                                    bool isValidData = expression.IsMatch(data);

                                    if (!isValidData)
                                    {
                                        throw new Exception(string.Format("Parsed data returned by {0} is an invalid data format: {1}", sourceUri, data));
                                    }

                                    ParseWeatherData(data);
                                }
                            }
                            else
                            {
                                // The HTML was blank, so there's nothing else we can do
                                throw new Exception(string.Format("The HTML document returned by a configured URL ({0}) is empty.", sourceUri));
                            }
                        }
                    }
                    else
                    {
                        // We had trouble creating the URL using configuration file entries
                        throw new UriFormatException(string.Format("The system was unable to parse a URL ({0}) in the configuration file.", sourceUri));
                    }
                }
                catch (Exception x)
                {
                    WriteToEventApplicationLog(x);
                    SendMail(x.Message);
                }
            }
        }

        /// <summary>
        ///     Parses the weather data.
        /// </summary>
        /// <param name="data">The data.</param>
        private static void ParseWeatherData(string data)
        {
            Debug.Assert(!string.IsNullOrWhiteSpace(data), "Weather data cannot be null or empty.");

            try
            {
                // For now, we parse the data all the same way
                IEnumerable<string> parts = data.Split(' ');

                string airportCode = parts.ElementAt(1);
                string dayTime = parts.ElementAt(2).TrimEnd('Z');

                string fileCommonName = string.Format("SACN61.{0}", airportCode);

                // Get the previously saved data file and generate an appropriate sequential file extension
                FileSystemInfo previousDataFile = GetPreviousDataFile(fileCommonName);

                int fileExtension = 1;

                if (previousDataFile != null)
                {
                    fileExtension = GetUniqueFileExtension(previousDataFile);
                }

                string fileUniqueName = string.Format("{0}.{1}.{2}", fileCommonName, dayTime, fileExtension);
                string fullPath = string.Concat(EnsureTrailingSlash(DataPath), fileUniqueName);

                // If the file already exists, do nothing
                if (File.Exists(fullPath))
                {
                    return;
                }

                // Build the file contents
                IEnumerable<string> lines = new[] { "\x0001", "981 ", string.Format("SACN61 {0} {1} ", airportCode, dayTime), string.Concat(data, "=") };

                // Compare the previous file contents (if we have it) to what we retreived from the web source
                if (previousDataFile != null)
                {
                    IEnumerable<string> oldLines = File.ReadAllLines(previousDataFile.FullName);

                    if (oldLines.SequenceEqual(lines))
                    {
                        // The content is exactly the same as last time, so don't bother creating a new file
                        return;
                    }
                }

                // Write the file to disk
                File.WriteAllLines(fullPath, lines, System.Text.Encoding.UTF8);
            }
            catch (Exception x)
            {
                WriteToEventApplicationLog(x);
                SendMail(x.Message);
            }
        }

        /// <summary>
        ///     Ensures that the data path provided on invocation has a trailing slash.
        /// </summary>
        /// <param name="path">The path.</param>
        /// <returns></returns>
        private static string EnsureTrailingSlash(string path)
        {
            Debug.Assert(!string.IsNullOrWhiteSpace(path), "Cannot add a trailing slash to a null or empty path.");

            return !path.EndsWith("\\") ? string.Concat(path, "\\") : path;
        }

        /// <summary>
        ///     Gets a unique extension for the data file.
        /// </summary>
        /// <param name="previousDataFile">The previous data file.</param>
        /// <returns></returns>
        private static int GetUniqueFileExtension(FileSystemInfo previousDataFile)
        {
            // If this isn't the first file of the day, increment the extension number
            if (previousDataFile.CreationTime.Date.CompareTo(DateTime.Now.Date) == 0)
            {
                return Convert.ToInt32(previousDataFile.Extension.TrimStart('.')) + 1;
            }

            return 1;
        }

        /// <summary>
        ///     Gets the previous data file.
        /// </summary>
        /// <param name="name">The name.</param>
        /// <returns></returns>
        /// <exception cref="System.IO.FileNotFoundException">
        ///     Attempts to locate the newest file in the list of parsed data files
        ///     returned null.
        /// </exception>
        private static FileSystemInfo GetPreviousDataFile(string name)
        {
            Debug.Assert(!string.IsNullOrWhiteSpace(name), "The file name cannot be null or empty.");

            string searchPattern = string.Format("{0}*", name);
            IEnumerable<FileSystemInfo> fileInfo = new DirectoryInfo(DataPath).GetFileSystemInfos(searchPattern, SearchOption.TopDirectoryOnly);

            if (!fileInfo.Any())
            {
                return null;
            }

            // Get the newest file in the list
            FileSystemInfo newestFile = fileInfo.OrderByDescending(f => f.CreationTime).FirstOrDefault();

            if (newestFile == null)
            {
                throw new FileNotFoundException("Attempts to locate the newest file in the list of parsed data files returned null.");
            }

            return newestFile;
        }

        /// <summary>
        ///     Shows information about the program to the user.
        /// </summary>
        /// <param name="options">The options.</param>
        private static void ShowHelp(NDesk.Options.OptionSet options)
        {
            Console.WriteLine();
            Console.WriteLine("Usage: weatherscraper [OPTIONS]");
            Console.WriteLine();
            Console.WriteLine("Options:");
            options.WriteOptionDescriptions(Console.Out);
        }

        /// <summary>
        ///     Writes to event application log.
        /// </summary>
        /// <param name="x">The exception to log.</param>
        private static void WriteToEventApplicationLog(Exception x)
        {
            string errorMessage = string.Format("{0}\r\n{1}", x.Message, x.StackTrace);

            EventLog applicationLog = new EventLog { Source = "WeatherScraper" };
            applicationLog.WriteEntry(errorMessage, EventLogEntryType.Error);
        }

        /// <summary>
        ///     Sends mail to the system administrator on error.
        /// </summary>
        /// <param name="message">The message.</param>
        private static void SendMail(string message)
        {
            Debug.Assert(!string.IsNullOrWhiteSpace(message), "The email message cannot be null or empty.");

            // Make sure that the required email addresses and server were specified on invocation before attempting to send notifications
            if (string.IsNullOrWhiteSpace(MailFrom) || string.IsNullOrWhiteSpace(MailTo) || string.IsNullOrWhiteSpace(MailServer))
            {
                return;
            }

            try
            {
                MailMessage mailMessage = new MailMessage(MailFrom, MailTo, MailSubject, message);

                SmtpClient client = new SmtpClient { Host = MailServer };
                client.Send(mailMessage);
            }
            catch (SmtpException x)
            {
                EventLog.WriteEntry("WeatherScraper", x.Message, EventLogEntryType.Error);
            }
        }

        /// <summary>
        ///     Abstract class for parsing weather data
        /// </summary>
        internal abstract class ParseStrategy
        {
            /// <summary>
            ///     Parses the specified HTML document.
            /// </summary>
            /// <param name="htmlDocument">The HTML document.</param>
            /// <returns></returns>
            public abstract string Parse(HtmlAgilityPack.HtmlDocument htmlDocument);
        }

        /// <summary>
        ///     Parses weather data for CYNR
        /// </summary>
        internal class CynrParse : ParseStrategy
        {
            /// <summary>
            ///     Parses the specified HTML document.
            /// </summary>
            /// <param name="htmlDocument">The HTML document.</param>
            /// <returns></returns>
            public override string Parse(HtmlAgilityPack.HtmlDocument htmlDocument)
            {
                Debug.Assert(htmlDocument != null, "The source HTML cannot be null or empty.");

                HtmlAgilityPack.HtmlNode dataNode = htmlDocument.DocumentNode.SelectSingleNode("//br").NextSibling;

                return dataNode.InnerText;
            }
        }

        /// <summary>
        ///     Parses weather data for CET2
        /// </summary>
        internal class Cet2Parse : ParseStrategy
        {
            /// <summary>
            ///     Parses the specified HTML document.
            /// </summary>
            /// <param name="htmlDocument">The HTML document.</param>
            /// <returns></returns>
            public override string Parse(HtmlAgilityPack.HtmlDocument htmlDocument)
            {
                Debug.Assert(htmlDocument != null, "The source HTML cannot be null or empty.");

                HtmlAgilityPack.HtmlNode dataNode = htmlDocument.DocumentNode.SelectSingleNode("//br").NextSibling;

                return dataNode.InnerText;
            }
        }

        /// <summary>
        ///     Parses weather data from HTML for CFG6
        /// </summary>
        internal class Cfg6Parse : ParseStrategy
        {
            /// <summary>
            ///     Parses the specified HTML document.
            /// </summary>
            /// <param name="htmlDocument">The HTML document.</param>
            /// <returns></returns>
            public override string Parse(HtmlAgilityPack.HtmlDocument htmlDocument)
            {
                Debug.Assert(htmlDocument != null, "The source HTML cannot be null or empty.");

                HtmlAgilityPack.HtmlNode dataNode = htmlDocument.DocumentNode.SelectSingleNode("//div[@id='METAR']");

                return dataNode.InnerText.Trim();
            }
        }

        /// <summary>
        ///     Parses weather data from HTML
        /// </summary>
        internal class DataParser
        {
            private ParseStrategy _parseStrategy;

            /// <summary>
            ///     Sets the parse strategy.
            /// </summary>
            /// <param name="parseStrategy">The parse strategy.</param>
            public void SetParseStrategy(ParseStrategy parseStrategy)
            {
                this._parseStrategy = parseStrategy;
            }

            /// <summary>
            ///     Parses this instance.
            /// </summary>
            /// <returns></returns>
            public string Parse()
            {
                return this._parseStrategy.Parse(HtmlDocument);
            }
        }

        /// <summary>
        ///     The URLs from which to retreive weather data.
        /// </summary>
        public class WeatherDataSources
        {
            /// <summary>
            ///     Gets or sets the URL.
            /// </summary>
            /// <value>
            ///     The URL.
            /// </value>
            public string Url { get; set; }

            /// <summary>
            ///     Gets or sets the source.
            /// </summary>
            /// <value>
            ///     The source.
            /// </value>
            public string Airport { get; set; }
        }
    }
}