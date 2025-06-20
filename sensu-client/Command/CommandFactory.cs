﻿using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using NLog;
using System.Collections.Generic;
using System.Text;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Net;

namespace sensu_client.Command
{

    public struct CommandResult
    {
        public string Output { get; set; }
        public int Status { get; set; }
    }

    public static class CommandFactory
    {

        public static Command Create(CommandConfiguration commandConfiguration, string command)
        {
            var command_lower = command.ToLower();
            if (command_lower.StartsWith(PerformanceCounterCommand.PREFIX)) return new PerformanceCounterCommand(commandConfiguration, command);
            if (command_lower.StartsWith(HTTPCommand.PREFIX)) return new HTTPCommand(commandConfiguration, command);
            if (command_lower.Contains(".ps1")) return new PowerShellCommand(commandConfiguration, command);
            if (command_lower.Contains(".rb")) return new RubyCommand(commandConfiguration, command);

            return new ShellCommand(commandConfiguration, command);
        }
    }

    public abstract class Command
    {
        protected static readonly Logger Log = LogManager.GetCurrentClassLogger();
        protected readonly CommandConfiguration _commandConfiguration;
        protected readonly string _unparsedCommand;
        private string _arguments;

        protected Command(CommandConfiguration commandConfiguration, string unparsedCommand)
        {
            _commandConfiguration = commandConfiguration;
            _unparsedCommand = unparsedCommand;
        }
        
        public abstract string FileName { get; protected internal set; }

        public virtual string Arguments
        {
            get
            {
                if (!String.IsNullOrEmpty(_arguments)) return _arguments;

                _arguments = ParseArguments();
                return _arguments;
            }
            protected internal set { _arguments = value; }
        }

        protected abstract string ParseArguments();

        public virtual CommandResult Execute()
        {
            var result = new CommandResult();
            var processstartinfo = new ProcessStartInfo()
            {
                FileName = FileName,
                Arguments = Arguments,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = _commandConfiguration.Plugins,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true
            };
            var process = new Process { StartInfo = processstartinfo };
            try
            {
                process.Start();
                if (_commandConfiguration.TimeOut.HasValue)
                {
                    if (!process.WaitForExit(1000 * _commandConfiguration.TimeOut.Value))
                    {
                        Log.Debug("Process to be killed {0}", FileName);
                        process.Kill();
                    }
                }
                else
                {
                    process.WaitForExit();
                }

                var output = process.StandardOutput.ReadToEnd();
                var errors = process.StandardError.ReadToEnd();
                var status = process.ExitCode;
                result.Output = String.Format("{0}{1}", output, errors);
                result.Status = status;
                if (!string.IsNullOrEmpty(errors))
                {
                    Log.Error(
                        "Error when executing command: '{0}' on '{1}' \n resulted in: {2} \n",
                        _unparsedCommand,
                        _commandConfiguration.Plugins,
                        errors
                        );
                }
            }
            catch (Exception ex)
            {
                Log.Warn(
                    ex,
                    "Unexpected error when executing command: '{0}' on '{1}'",
                    _unparsedCommand,
                    _commandConfiguration.Plugins
                    );
                result.Output = String.Format("Unexpected error: {0}", ex.Message);
                result.Status = 2;
            }
            finally
            {
                process.Close();
            }
            return result;

        }
    }

    public class PowerShellCommand : Command
    {
        private string _fileName;
        private string _arguments;
        const string PowershellOptions = "-NoProfile -NonInteractive -NoLogo -ExecutionPolicy Bypass ";

        public PowerShellCommand(CommandConfiguration commandConfiguration, string unparsedCommand) : base(commandConfiguration, unparsedCommand)
        {
        }

        public override string FileName
        {
            get
            {
                if (!String.IsNullOrEmpty(_fileName)) return _fileName;

                _fileName = GetPowerShellExePath();
                return _fileName;
            }
            protected internal set { _fileName = value; }
        }

        private static string GetPowerShellExePath()
        {
            var systemRoot = Environment.ExpandEnvironmentVariables("%systemroot%").ToLower();
            if (File.Exists(string.Format("{0}\\sysnative\\WindowsPowershell\\v1.0\\powershell.exe", systemRoot)))
            {
                return string.Format("{0}\\sysnative\\WindowsPowershell\\v1.0\\powershell.exe", systemRoot);
            }
            if (File.Exists(string.Format("{0}\\system32\\WindowsPowershell\\v1.0\\powershell.exe", systemRoot)))
            {
                return string.Format("{0}\\system32\\WindowsPowershell\\v1.0\\powershell.exe", systemRoot);
            }
            return "powershell.exe";
        }

        public override string Arguments
        {
            get
            {
                if (!String.IsNullOrEmpty(_arguments)) return _arguments;

                _arguments = ParseArguments();
                return _arguments;
            }
            protected internal set { _arguments = value; }
        }

        protected override string ParseArguments()
        {
            int lastSlash = _unparsedCommand.LastIndexOf('/');
            var powershellargument = (lastSlash > -1) ? _unparsedCommand.Substring(lastSlash + 1) : _unparsedCommand;
            return String.Format("{0} -FILE {1}\\{2}", PowershellOptions, _commandConfiguration.Plugins, powershellargument);
        }
    }

    public class RubyCommand : Command
    {
        //string envRubyPath = Environment.GetEnvironmentVariable("RUBYPATH");
        private string _fileName;
        private string _arguments;

        public RubyCommand(CommandConfiguration commandConfiguration, string unparsedCommand)
            : base(commandConfiguration, unparsedCommand)
        {
        }


        public override string FileName
        {
            get
            {
                if (!String.IsNullOrEmpty(_fileName)) return _fileName;

                _fileName = RubyExePath();
                return _fileName;
            }
            protected internal set { _fileName = value; }
        }

        private static string RubyExePath()
        {
            var defaultSensuClientPath = @"c:\opt\sensu\embedded\bin";
            var rubyPath = Path.Combine(defaultSensuClientPath, "ruby.exe");
            if (File.Exists(rubyPath))
            {
                return rubyPath;
            }

            return "ruby.exe";
        }

        public override string Arguments
        {
            get
            {
                if (!String.IsNullOrEmpty(_arguments)) return _arguments;

                _arguments = ParseArguments();
                return _arguments;
            }
            protected internal set { _arguments = value; }
        }


        protected override string ParseArguments()
        {
            int lastSlash = _unparsedCommand.LastIndexOf('/');
            var rubyArgument = (lastSlash > -1) ? _unparsedCommand.Substring(lastSlash + 1) : _unparsedCommand;
            return String.Format("{0}\\{1}", _commandConfiguration.Plugins, rubyArgument);
        }
    }

    public class ShellCommand : Command
    {
        protected string _filename;
        protected string _arguments;

        public ShellCommand(CommandConfiguration commandConfiguration, string unparsedCommand) : base(commandConfiguration, unparsedCommand)
        {
            var cmd = unparsedCommand.Split(new char[] { ' ' }, 2);
            _filename = cmd[0];
            if (cmd.Length > 1)
                _arguments = cmd[1];
        }

        public override string FileName
        {
            protected internal set { _filename = value; }
       
            get
            {
                return _filename;                
            }
        }

        protected override string ParseArguments()
        {
            return _arguments;
        }
    }


    public class HTTPCommand : Command
    {
        public static string PREFIX = "!http> ";
        private Dictionary<string, string> parameters = new Dictionary<string, string>();

        public HTTPCommand(CommandConfiguration commandConfiguration, string unparsedCommand) : base(commandConfiguration, unparsedCommand)
        {
            ParseArguments();
        }
        
        public override string FileName
        {
            // I'm afraid this method is not required in this Command
            get { return ""; }
            protected internal set { }
        }

        protected override string ParseArguments()
        {
            var rawArgs = _unparsedCommand.Substring(PREFIX.Length);
            string[] split = rawArgs.Split(';');

            for (var i = 0; i < split.Length; ++i)
            {
                var Item = split[i];
                if (!Item.Contains("="))
                {
                    Log.Warn("Invalid format for argument {0}. Ignored.", Item);
                    continue;
                }
                string[] aux = Item.Split(new char[] { '=' }, 2);
                string key = aux[0].Trim();
                string value = aux[1].Trim();

                parameters[key] = value;
            }

            // retire the magic word
            return rawArgs;
        }

        private string getParam(string key, string defaultValue)
        {
            if (parameters.ContainsKey(key))
                return parameters[key];
            return defaultValue;
        }

        public override CommandResult Execute()
        {
            var result = new CommandResult();
            result.Status = 2;
            result.Output = "No checks were run";

            try {
                var url = getParam("url", null);
                var timeout = Int64.Parse(getParam("timeout", "10000"));
                var uri = new Uri(url);
                var method = getParam("method", "GET");
                var schema = getParam("schema", String.Format(CultureInfo.InvariantCulture, "{0}.http.{1}.{2}", System.Environment.MachineName, uri.Host, uri.Port));
                var validStatusRaw = getParam("valid_codes", "200, 302");
                var validStatus = new List<int>();
                foreach (var code in validStatusRaw.Split(','))
                    validStatus.Add(Int32.Parse(code));

                var stopwatch = new Stopwatch();
                stopwatch.Start();
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = method;
                var response = (HttpWebResponse)request.GetResponse();
                stopwatch.Stop();

                if (validStatus.Contains((int)response.StatusCode))
                {
                    var unixTimestamp = (Int32)(DateTime.UtcNow.Subtract(new DateTime(1970, 1, 1))).TotalSeconds;
                    result.Output = String.Format(CultureInfo.InvariantCulture, "{0} {1:f2} {2}\n",
                                    schema,
                                    stopwatch.ElapsedMilliseconds / 1000.0,
                                    unixTimestamp
                                );
                    result.Status = 0;
                } else
                {
                    result.Output = String.Format("# Error accessing to {0} (code: {1}): {2}", url, response.StatusCode, response.StatusDescription);
                    result.Status = 1;
                }
                
            } catch (Exception e)
            {
                Log.Error(e, "There was an error accessing to an HTTP check");
                result.Output = e.Message;
                result.Status = 2;
            }
            
            return result;
        }
    }

    public class PerformanceCounterCommand : Command
    {
        public static string PREFIX = "!perfcounter> ";
        private static Dictionary<string, List<PerformanceCounter>> counters = new Dictionary<string, List<PerformanceCounter>>();
        private static PerformanceCounterRegEx DefaultPerfCounterRegEx = new PerformanceCounterRegEx();

        public PerformanceCounterCommand(CommandConfiguration commandConfiguration, string unparsedCommand) : base(commandConfiguration, unparsedCommand)
        {
        }
        
        public override string FileName
        {
            // I'm afraid this method is not required in this Command
            get
            {
                return "";
            }

            protected internal set { }
        }

        protected override string ParseArguments()
        {
            // retire the magic word
            return _unparsedCommand.Substring(PREFIX.Length);
        }

        public override CommandResult Execute()
        {
            var result = new CommandResult();
            result.Status = 0;
            result.Output = "No checks were run";
            {
                string[] splittedArguments = ParseArguments().Split(';');
                var counterlist = getCounterlist(splittedArguments[0]);
                var parameters = ParseParameters(splittedArguments);

                var unixTimestamp = (Int32)(DateTime.UtcNow.Subtract(new DateTime(1970, 1, 1))).TotalSeconds;
                var stdout = new StringBuilder();
                var stderr = new StringBuilder();
                string schema;

                foreach (var counter in counterlist)
                {
                    if (parameters.ContainsKey("schema"))
                        schema = normalizeString(
                            parameters["schema"]
                                .Replace("{INSTANCE}", counter.InstanceName)
                                .Replace("{COUNTER}", counter.CounterName)
                                .Replace("{CATEGORY}", counter.CategoryName)
                                .Replace("%", "_PERCENT_")
                        ).Replace("_PERCENT_", "percent.");
                    else
                        schema = String.Format(CultureInfo.InvariantCulture, "{0}.{1}.{2}.{3}",
                                System.Environment.MachineName,
                                normalizeString(counter.CategoryName),
                                normalizeString(counter.CounterName.Replace('.', '_').Replace("%", "_PERCENT_")).Replace("_PERCENT_", "percent."),
                                "performance_counter");
                    schema = Regex.Replace(schema, @"_*\._*", @".");
                    try
                    {
                        var value = counter.NextValue();
                        stdout.Append(
                            String.Format(CultureInfo.InvariantCulture, "{0} {1:f2} {2}\n",
                                schema,
                                value,
                                unixTimestamp
                            )
                        );

                        if (result.Status == 0)
                        {
                            result.Status = getNewStatus(parameters, counter.ToString(), value, stderr);
                        }
                    }
                    catch (Exception e)
                    {
                        Log.Warn("Error running performance counter {0}:\n {1}", counter.CounterName, e);
                        stderr.AppendLine("# " + e.Message);
                        result.Status = 2;
                    }
                    result.Output = stderr.Append(stdout).ToString().Trim(' ', '_');
                }
            }

            return result;
        }

        private int getNewStatus(Dictionary<string, string> parameters, string name, float value, StringBuilder output)
        {
            Boolean ascendent = true;
            char symbol = '>';
            if (parameters.ContainsKey("growth") && parameters["growth"].Equals("desc"))
            {
                ascendent = false;
                symbol = '<';
            }

            if (parameters.ContainsKey("error") && (
                (ascendent && value > Int64.Parse(parameters["error"])) ||
                (!ascendent && value < Int64.Parse(parameters["error"]))
                ))
            {
                output.AppendLine(String.Format("# CRITICAL: {0} has value {1} {3} {2}", name, value, parameters["error"], symbol));
                return 1;
            }
            else if (parameters.ContainsKey("warn") && (
                (ascendent && value > Int64.Parse(parameters["warn"])) ||
                (!ascendent && value < Int64.Parse(parameters["warn"]))
                ))
            {
                output.AppendLine(String.Format("# WARNING: {0} has value {1} {3} {2}", name, value, parameters["warn"], symbol));
                return 1;
            }
            return 0;
        }

        private string normalizeString(string str)
        {
            return Regex.Replace(str, @"[^A-Za-z0-9\.\-]+", "_");
        }

        private List<PerformanceCounter> getCounterlist(string counterName)
        {
            if (!counters.ContainsKey(counterName))
            {
                List<System.Diagnostics.PerformanceCounter> counterlist = new List<PerformanceCounter>();
                var counterData = DefaultPerfCounterRegEx.split(counterName);
                try
                {
                    PerformanceCounterCategory mycat = new PerformanceCounterCategory(counterData.Category);
                    var foo = PerformanceCounterCategory.GetCategories();
                    var foolist = new List<string>();
                    foreach (var f in foo)
                    {
                        foolist.Add(f.CategoryName);
                    }
                    foolist.Sort();
                    switch (mycat.CategoryType)
                    {
                        case PerformanceCounterCategoryType.SingleInstance:
                            foreach (var counter in mycat.GetCounters())
                            {
                                if (!counter.CounterName.Equals(counterData.Counter, StringComparison.InvariantCultureIgnoreCase))
                                    continue;
                                counterlist.Add(counter);
                                counter.NextValue(); // Initialize performance counters in order to avoid them to return 0.
                            }
                            break;
                        case PerformanceCounterCategoryType.MultiInstance:
                            if (counterData.Instance == null || counterData.Instance.Equals("*"))
                            {
                                foreach (var instance in mycat.GetInstanceNames())
                                {
                                    foreach (var counter in mycat.GetCounters(instance))
                                    {
                                        if (!counter.CounterName.Equals(counterData.Counter, StringComparison.InvariantCultureIgnoreCase))
                                            continue;
                                        counterlist.Add(counter);
                                        counter.NextValue(); // Initialize performance counters in order to avoid them to return 0.
                                    }
                                }
                            }
                            else
                            {
                                foreach (var counter in mycat.GetCounters(counterData.Instance))
                                {
                                    if (!counter.CounterName.Equals(counterData.Counter, StringComparison.InvariantCultureIgnoreCase))
                                        continue;
                                    counterlist.Add(counter);
                                    counter.NextValue(); // Initialize performance counters in order to avoid them to return 0.
                                }
                            }
                            break;
                        default:
                            break;
                    }

                }
                catch (Exception e)
                {
                    Log.Error(String.Format("Counter {0} will be ignored due to errors: {1}", counterName, e));
                    Log.Error(e);
                }
                finally
                {
                    counters.Add(counterName, counterlist);
                }
            }
            return counters[counterName];
        }

        private Dictionary<string, string> ParseParameters(string[] split)
        {
            Dictionary<string, string> parameters = new Dictionary<string, string>();

            for (var i = 1; i < split.Length; ++i)
            {
                var Item = split[i];
                if (!Item.Contains("="))
                {
                    Log.Warn("Invalid format for argument {0}. Ignored.", Item);
                    continue;
                }
                string[] aux = Item.Split(new char[] { '=' }, 2);
                string key = aux[0].Trim();
                string value = aux[1].Trim();

                parameters[key] = value;
            }
            return parameters;
        }
    }

    
    public class PerformanceCounterRegEx
    {
        Regex regex = new Regex(
            @"^\\?" +
            @"(?<category>[^\\\(]+)" +
            @"(?:\(" + @"(?<instance>[^\)]+)" + @"\)\s*)?" +
            @"\\" +
            @"(?<counter>.*)" +
            @"$"
        );
        public PerformanceCounterData split(string pattern)
        {
            var result = new PerformanceCounterData();
            var match = regex.Match(pattern);

            result.Category = match.Groups["category"].Value.Trim();
            result.Counter = match.Groups["counter"].Value.Trim();
            if (match.Groups["instance"].Success)
            {
                result.Instance = match.Groups["instance"].Value.Trim();
            }
            return result;
        }
    }

    public class PerformanceCounterData
    {
        public string Category { get; set; }
        public string Counter { get; set; }
        public string Instance { get; set; }
    }
}