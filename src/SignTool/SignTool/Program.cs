// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;
using Newtonsoft.Json;

namespace SignTool
{
    internal static class Program
    {
        internal const int ExitSuccess = 0;
        internal const int ExitFailure = 1;

        internal static int Main(string[] args)
        {
            SignToolArgs signToolArgs;
            if (!ParseCommandLineArguments(StandardHost.Instance, args, out signToolArgs))
            {
                PrintUsage();
                return ExitFailure;
            }

            if (!signToolArgs.Test && !File.Exists(signToolArgs.MSBuildPath))
            {
                Console.WriteLine($"Unable to locate MSBuild at the path '{signToolArgs.MSBuildPath}'.");
                return ExitFailure;
            }

            var signTool = SignToolFactory.Create(signToolArgs);
            var batchData = ReadConfigFile(signToolArgs.OutputPath, signToolArgs.ConfigFile);
            var util = new BatchSignUtil(signTool, batchData);
            try
            {
                return util.Go(Console.Out) ? ExitSuccess : ExitFailure;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Unexpected exception: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
                return 1;
            }
        }

        internal static BatchSignInput ReadConfigFile(string outputPath, string configFile)
        {
            using (var file = File.OpenText(configFile))
            {
                BatchSignInput batchData;
                if (!TryReadConfigFile(Console.Out, file, outputPath, out batchData))
                {
                    Environment.Exit(ExitFailure);
                }

                return batchData;
            }
        }

        internal static bool TryReadConfigFile(TextWriter output, TextReader configReader, string outputPath, out BatchSignInput batchData)
        {
            var serializer = new JsonSerializer();
            var fileJson = (Json.FileJson)serializer.Deserialize(configReader, typeof(Json.FileJson));
            var map = new Dictionary<string, SignInfo>(StringComparer.OrdinalIgnoreCase);
            var allGood = true;
            foreach (var item in fileJson.SignList)
            {
                var data = new SignInfo(certificate: item.Certificate, strongName: item.StrongName);
                foreach (var relativeFileName in ExpandFileList(outputPath, item.FileList, ref allGood))
                {
                    if (map.ContainsKey(relativeFileName))
                    {
                        Console.WriteLine($"Duplicate file entry: {relativeFileName}");
                        allGood = false;
                    }
                    else
                    {
                        map.Add(relativeFileName, data);
                    }
                }
            }

            if (!allGood)
            {
                batchData = null;
                return false;
            }

            batchData = new BatchSignInput(outputPath, map, fileJson.ExcludeList ?? Array.Empty<string>());
            return true;
        }

        /// <summary>
        /// The files to sign section supports globbing. The only caveat is that globs must expand to match at least a 
        /// single file else an error occurs. This function will expand those globas as necessary.
        /// </summary>
        private static List<string> ExpandFileList(string outputPath, IEnumerable<string> relativeFileNames, ref bool allGood)
        {
            var directoryInfo = new DirectoryInfo(outputPath);
            var matchDir = new DirectoryInfoWrapper(directoryInfo);

            var list = new List<string>();
            foreach (var relativeFileName in relativeFileNames)
            {
                if (!relativeFileName.Contains('*'))
                {
                    list.Add(relativeFileName);
                    continue;
                }

                try
                {
                    var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
                    matcher.AddInclude(relativeFileName);
                    var result = matcher.Execute(matchDir);
                    if (!result.HasMatches)
                    {
                        Console.WriteLine($"The glob {relativeFileName} expanded to 0 entries");
                        continue;
                    }

                    list.AddRange(result.Files.Select(x => PathUtil.NormalizeSeparators(x.Path)));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error expanding glob {relativeFileName}: {ex.Message}");
                    allGood = false;
                }
            }

            return list;
        }

        internal static void PrintUsage()
        {
            var usage =
@"SignTool.exe [-test] [-testSign] [-intermediateOutputPath <path>] [-msbuildPath <path>] [-nugetPackagesPath <path>] [-config <path>] outputPath

test: Run tool without actually modifying any state.
testSign: The binaries will be test signed. The default is to real sign.
outputPath: Directory containing the binaries.
intermediateOutputPath: Directory containing intermediate output.  Default is (outputpath\..\Obj).
nugetPackagesPath: Path containing downloaded NuGet packages.
msbuildPath: Path to MSBuild.exe to use as signing mechanism.
config: Path to SignToolData.json. Default build\config\SignToolData.json.
";
            Console.WriteLine(usage);
        }

        internal static bool ParseCommandLineArguments(
            IHost host,
            string[] args,
            out SignToolArgs signToolArgs)
        {
            signToolArgs = default(SignToolArgs);

            string intermediateOutputPath = null;
            string outputPath = null;
            string msbuildPath = null;
            string nugetPackagesPath = null;
            string configFile = null;
            var test = false;
            var testSign = false;

            var i = 0;

            while (i + 1 < args.Length)
            {
                var current = args[i].ToLower();
                switch (current)
                {
                    case "-test":
                        test = true;
                        i++;
                        break;
                    case "-testsign":
                        testSign = true;
                        i++;
                        break;
                    case "-intermediateoutputpath":
                        if (!ParsePathOption(args, ref i, current, out intermediateOutputPath))
                        {
                            return false;
                        }
                        break;
                    case "-msbuildpath":
                        if (!ParsePathOption(args, ref i, current, out msbuildPath))
                        {
                            return false;
                        }
                        break;
                    case "-nugetpackagespath":
                        if (!ParsePathOption(args, ref i, current, out nugetPackagesPath))
                        {
                            return false;
                        }
                        break;
                    case "-config":
                        if (!ParsePathOption(args, ref i, current, out configFile))
                        {
                            return false;
                        }
                        break;
                    default:
                        Console.Error.WriteLine($"Unrecognized option {current}");
                        return false;
                }
            }

            if (i + 1 != args.Length)
            {
                Console.Error.WriteLine("Need a value for outputPath");
                return false;
            }

            if (msbuildPath == null && !test)
            {
                Console.Error.WriteLine("-msbuildpath argument must be specified unless running in validation-only mode.");
                return false;
            }

            outputPath = args[i];

            intermediateOutputPath = intermediateOutputPath ?? Path.Combine(Path.GetDirectoryName(outputPath), "Obj");

            if (string.IsNullOrWhiteSpace(nugetPackagesPath))
            {
                nugetPackagesPath = host.GetEnvironmentVariable("NUGET_PACKAGES");
                if (string.IsNullOrWhiteSpace(nugetPackagesPath))
                {
                    nugetPackagesPath = Path.Combine(
                        host.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        @".nuget\packages");
                }
            }

            if (configFile == null)
            {
                var sourcesPath = GetSourcesPath(host, outputPath);
                if (sourcesPath != null)
                {
                    configFile = Path.Combine(sourcesPath, @"build\config\SignToolData.json");
                }
            }

            signToolArgs = new SignToolArgs(
                outputPath: outputPath,
                msbuildPath: msbuildPath,
                intermediateOutputPath: intermediateOutputPath,
                nugetPackagesPath: nugetPackagesPath,
                appPath: AppContext.BaseDirectory,
                configFile: configFile,
                test: test,
                testSign: testSign);
            return true;
        }

        private static bool ParsePathOption(string[] args, ref int i, string optionName, out string optionValue)
        {
            if (i + 1 >= args.Length)
            {
                Console.WriteLine($"{optionName} needs an argument");
                optionValue = null;
                return false;
            }

            optionValue = args[i + 1];
            i += 2;
            return true;
        }

        private static string GetSourcesPath(IHost host, string outputPath)
        {
            var current = Path.GetDirectoryName(outputPath);
            while (!string.IsNullOrEmpty(current))
            {
                var gitDir = Path.Combine(current, ".git");
                if (host.DirectoryExists(gitDir))
                {
                    return current;
                }

                current = Path.GetDirectoryName(current);
            }

            return null;
        }
    }
}
