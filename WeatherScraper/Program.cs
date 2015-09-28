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
                                HtmlAgilityPack.HtmlDocument htmlDocument = new HtmlAgilityPack.HtmlDocument();
                                htmlDocument.LoadHtml(html);

                                HtmlAgilityPack.HtmlNode dataNode = htmlDocument.DocumentNode.SelectSingleNode("//br").NextSibling;

                                string data = dataNode.InnerText;

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
                int fileExtension = GetUniqueFileExtension(fileCommonName);

                // If this is the first file of the day, restart the extension numbering at .1
                if (fileExtension > 1)
                {
                    string oldFileName = string.Format("{0}.{1}.{2}", fileCommonName, dayTime, fileExtension - 1);
                    string oldFullPath = string.Concat(EnsureTrailingSlash(DataPath), oldFileName);

                    if (File.Exists(oldFullPath))
                    {
                        DateTime oldFileCreateTime = File.GetCreationTime(oldFullPath);

                        // Was the oldest file created yesterday?
                        if (oldFileCreateTime.AddDays(1).Day == DateTime.Now.Day)
                        {
                            fileExtension = 1;
                        }
                    }
                }

                string fileUniqueName = string.Format("{0}.{1}.{2}", fileCommonName, dayTime, fileExtension);
                string fullPath = string.Concat(EnsureTrailingSlash(DataPath), fileUniqueName);

                // Write the file to disk
                IEnumerable<string> lines = new[] {" ", "981 ", string.Format("SACN61 {0} {1} ", airportCode, dayTime), string.Concat(data, "=")};
                File.WriteAllLines(fullPath, lines);
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

        // TODO: Restart the count at midnight every day
        /// <summary>
        ///     Gets a unique extension for the data file.
        /// </summary>
        /// <param name="name">The file name.</param>
        /// <returns></returns>
        private static int GetUniqueFileExtension(string name)
        {
            Debug.Assert(!string.IsNullOrWhiteSpace(name), "The file name cannot be null or empty.");

            string searchPattern = string.Format("{0}*", name);
            List<string> files = Directory.EnumerateFiles(DataPath, searchPattern, SearchOption.TopDirectoryOnly).ToList();

            if (!files.Any())
            {
                return 1;
            }

            List<int> extensions = (from file in files select Path.GetExtension(file) into extension where !string.IsNullOrWhiteSpace(extension) select Convert.ToInt32(extension.TrimStart('.'))).ToList();

            return extensions.Any() ? extensions.Max() + 1 : 1;
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

            EventLog applicationLog = new EventLog {Source = "WeatherScraper"};
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

                SmtpClient client = new SmtpClient {Host = MailServer};
                client.Send(mailMessage);
            }
            catch (SmtpException x)
            {
                EventLog.WriteEntry("WeatherScraper", x.Message, EventLogEntryType.Error);
            }
        }

        /// <summary>
        ///     The URLs from which to retreive weather data.
        /// </summary>
        public class WeatherDataSources
        {
            public string Url { get; set; }
        }
    }
}