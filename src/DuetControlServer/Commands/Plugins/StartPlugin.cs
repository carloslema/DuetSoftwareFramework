﻿using DuetAPI.ObjectModel;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace DuetControlServer.Commands
{
    /// <summary>
    /// Implementation of the <see cref="DuetAPI.Commands.StartPlugin"/> command
    /// </summary>
    public sealed class StartPlugin : DuetAPI.Commands.StartPlugin
    {
        /// <summary>
        /// Logger instance
        /// </summary>
        private NLog.Logger _logger;

        /// <summary>
        /// Start a plugin
        /// </summary>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="ArgumentException">Plugin is invalid</exception>
        public override async Task Execute()
        {
            _logger = NLog.LogManager.GetLogger($"Plugin {Plugin}");

            using (await Model.Provider.AccessReadWriteAsync())
            {
                if (!await Start(Plugin))
                {
                    throw new ArgumentException($"Plugin {Plugin} not found");
                }
            }
        }

        /// <summary>
        /// Start a plugin (as a dependency)
        /// </summary>
        /// <param name="name">Plugin name</param>
        /// <param name="requiredBy">Plugin that requires this plugin</param>
        /// <returns>Whether the plugin could be found</returns>
        private async Task<bool> Start(string name, string requiredBy = null)
        {
            foreach (Plugin item in Model.Provider.Get.Plugins)
            {
                if (item.Name == Plugin)
                {
                    // Don't do anything if the plugin is already running or if it cannot be started on the SBC
                    if (item.Pid > 0 || string.IsNullOrEmpty(item.SbcExecutable))
                    {
                        return true;
                    }

                    // Start plugin dependencies
                    foreach (string dependency in item.SbcPluginDependencies)
                    {
                        if (dependency == requiredBy)
                        {
                            throw new ArgumentException($"Circular plugin dependencies are not supported ({dependency} <-> {requiredBy})");
                        }
                        if (!await Start(dependency, name))
                        {
                            throw new ArgumentException($"Dependency {dependency} of plugin {name} not found");
                        }
                    }

                    // Check the required DSF version
                    if (!Utility.Plugins.CheckVersion(Program.Version, item.SbcDsfVersion))
                    {
                        throw new ArgumentException($"Incompatible DSF version (requires {item.SbcDsfVersion}, got {Program.Version})");
                    }

                    // Check the required RRF version
                    if (!string.IsNullOrEmpty(item.RrfVersion))
                    {
                        if (Model.Provider.Get.Boards.Count > 0)
                        {
                            string rrfVersion = Model.Provider.Get.Boards[0].FirmwareVersion;
                            if (!Utility.Plugins.CheckVersion(rrfVersion, item.RrfVersion))
                            {
                                throw new ArgumentException($"Incompatible RRF version (requires {item.RrfVersion}, got {rrfVersion})");
                            }
                        }
                        else
                        {
                            _logger.Warn("Failed to check RRF version");
                        }
                    }

                    // Start the plugin
                    StartProcess(item);
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Start a plugin process
        /// </summary>
        /// <param name="plugin">Plugin to start</param>
        /// <exception cref="ArgumentException">Invalid executable path</exception>
        private void StartProcess(Plugin plugin)
        {
            // Verify the SBC executable path
            if (plugin.SbcExecutable.Contains(".."))
            {
                throw new ArgumentException("Invalid characters in SBC executable path");
            }

            // Get the actual executable
            string architecture = RuntimeInformation.OSArchitecture switch
            {
                Architecture.Arm => "arm",
                Architecture.Arm64 => "arm64",
                Architecture.X86 => "x86",
                Architecture.X64 => "x86_64",
                _ => "unknown"
            };

            string sbcExecutable = Path.Combine(Settings.PluginDirectory, plugin.Name, "bin", architecture, plugin.SbcExecutable);
            if (!File.Exists(sbcExecutable))
            {
                sbcExecutable = Path.Combine(Settings.PluginDirectory, plugin.Name, "bin", plugin.SbcExecutable);
            }

            // TODO Start the process via the elevation service if it requires super user permissions

            // Start the plugin process
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = sbcExecutable,
                Arguments = plugin.SbcExecutableArguments,
                WorkingDirectory = Path.GetDirectoryName(sbcExecutable),
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            Process process = Process.Start(startInfo);
            DataReceivedEventHandler outputHandler = MakeOutputHandler(Plugin, MessageType.Success, plugin.SbcOutputRedirected);
            DataReceivedEventHandler errorHandler = MakeOutputHandler(Plugin, MessageType.Error, plugin.SbcOutputRedirected);
            process.OutputDataReceived += outputHandler;
            process.ErrorDataReceived += errorHandler;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Update the PID
            plugin.Pid = process.Id;
            _logger.Info("Process has been started (pid {0})", process.Id);

            // Wait for the plugin to terminate in the background
            _ = Task.Run(async delegate
            {
                try
                {
                    // Wait for it to be terminated
                    process.WaitForExit();
                    process.ErrorDataReceived -= errorHandler;
                    process.OutputDataReceived -= outputHandler;

                    // Update the PID again
                    using (await Model.Provider.AccessReadWriteAsync())
                    {
                        foreach (Plugin item in Model.Provider.Get.Plugins)
                        {
                            if (item.Name == Plugin)
                            {
                                _logger.Info("Process has been stopped");
                                item.Pid = Program.CancellationToken.IsCancellationRequested ? 0 : -1;
                                break;
                            }
                        }
                    }

                    // Kill any leftover child processes
                    process.Kill(true);
                }
                finally
                {
                    process.Dispose();
                }
            });
        }

        /// <summary>
        /// Create a new handler to capture messages from stdin/stderr
        /// </summary>
        /// <param name="pluginName">Name of the plugin</param>
        /// <param name="messageType">Message type</param>
        /// <param name="outputMessages">Output messages through the object model</param>
        /// <returns>Event handler</returns>
        private DataReceivedEventHandler MakeOutputHandler(string pluginName, MessageType messageType, bool outputMessages)
        {
            return (object sender, DataReceivedEventArgs e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    if (outputMessages)
                    {
                        Model.Provider.Output(messageType, $"[{pluginName}]: {e.Data}");
                    }
                    else if (messageType == MessageType.Error)
                    {
                        _logger.Error(e.Data);
                    }
                    else
                    {
                        _logger.Info(e.Data);
                    }
                }
            };
        }
    }
}
