﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Dalamud.Configuration;
using Dalamud.Plugin;
using JetBrains.Annotations;

namespace LivePluginLoad {
    public class LivePluginLoad : IDalamudPlugin {
        public string Name => "LivePluginLoader";
        public DalamudPluginInterface PluginInterface { get; private set; }
        public LivePluginLoadConfig PluginConfig { get; private set; }

        private bool drawConfigWindow = false;
        private bool disposed = false;
        object pluginManager;
        private Dalamud.Dalamud dalamud;
        private PluginConfigurations pluginConfigs;
        private List<(IDalamudPlugin Plugin, PluginDefinition Definition, DalamudPluginInterface PluginInterface)> pluginsList;

        private Task reloadLoop;

        public void Dispose() {
            disposed = true;
            PluginInterface.UiBuilder.OnBuildUi -= this.BuildUI;
            reloadLoop?.Dispose();
            RemoveCommands();
            PluginInterface?.Dispose();
        }

        public void Initialize(DalamudPluginInterface pluginInterface) {
            this.PluginInterface = pluginInterface;
            this.PluginConfig = (LivePluginLoadConfig) pluginInterface.GetPluginConfig() ?? new LivePluginLoadConfig();
            this.PluginConfig.Init(this, pluginInterface);

            dalamud = (Dalamud.Dalamud) pluginInterface.GetType()
                ?.GetField("dalamud", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(pluginInterface);
            
            pluginManager = dalamud?.GetType()
                ?.GetProperty("PluginManager", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(dalamud);

            pluginConfigs = (PluginConfigurations) pluginManager?.GetType()
                ?.GetField("pluginConfigs", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(pluginManager);

            pluginsList = (List<(IDalamudPlugin Plugin, PluginDefinition Definition, DalamudPluginInterface PluginInterface)>) pluginManager?.GetType()
                ?.GetField("Plugins", BindingFlags.Instance | BindingFlags.Public)
                ?.GetValue(pluginManager);


            if (dalamud == null || pluginManager == null || pluginConfigs == null || pluginsList == null) {
                PluginLog.LogError("Failed to setup.");
                return;
            }

            PluginInterface.UiBuilder.OnBuildUi += this.BuildUI;

            reloadLoop = Task.Run(async () => {
                await Task.Delay(1000);
                foreach (var plc in PluginConfig.PluginLoadConfigs.Where(plc => plc.LoadAtStartup == true)) {
                    LoadPlugin(plc.FilePath, plc);
                }

                while (!disposed) {
                    await Task.Delay(1000);
                    foreach (var plc in PluginConfig.PluginLoadConfigs.Where(plc => plc.Loaded && plc.AutoReload)) {
                        FileInfo fi = new FileInfo(plc.FilePath);
                        if (plc.FileChanged != fi.LastWriteTime.Ticks) {
                            await Task.Delay(500);
                            FileInfo fi2 = new FileInfo(plc.FilePath);
                            if (fi2.LastWriteTime.Ticks == fi.LastWriteTime.Ticks) {
                                PluginLog.Log($"Changes Detected in {plc.PluginInternalName}");
                                LoadPlugin(plc.FilePath, plc);
                            }
                        }
                    }
                }
            });


            SetupCommands();
        }

        public bool UnloadPlugin(string internalName, [CanBeNull] PluginLoadConfig pluginLoadConfig = null) {
           try {
               (IDalamudPlugin plugin, PluginDefinition definition, DalamudPluginInterface pluginInterface) = pluginsList.FirstOrDefault(p => p.Definition.InternalName == internalName);
               plugin?.Dispose();
               pluginsList.Remove((plugin, definition, pluginInterface));

               if (pluginLoadConfig != null) {
                   pluginLoadConfig.Loaded = false;
               }

               return true;
           } catch (Exception) {
               PluginLog.LogError("Failed to unload");
               return false;
           }
        }

        public void LoadPlugin(string dllPath, [CanBeNull] PluginLoadConfig pluginLoadConfig = null) {
            if (!File.Exists(dllPath)) {
                PluginLog.LogError("File does not exist: {0}", dllPath);
                return;
            }

            FileInfo dllFile = new FileInfo(dllPath);

            if (pluginLoadConfig != null) {
                pluginLoadConfig.FileSize = dllFile.Length;
                pluginLoadConfig.FileChanged = dllFile.LastWriteTime.Ticks;
            }

            PluginLog.Log($"Attempting to load DLL at {dllFile.FullName}");

            Assembly pluginAssembly = Assembly.Load(File.ReadAllBytes(dllFile.FullName));

            var types = pluginAssembly.GetTypes();
            foreach (var type in types) {
                if (type.IsInterface || type.IsAbstract) {
                    continue;
                }

                if (type.GetInterface(typeof(IDalamudPlugin).FullName) != null) {
                    UnloadPlugin(type.Assembly.GetName().Name, pluginLoadConfig);

                    if (pluginsList.Any(x => x.Plugin.GetType().Assembly.GetName().Name == type.Assembly.GetName().Name)) {
                        PluginLog.LogError("Duplicate plugin found: {0}", dllFile.FullName);
                        return;
                    }

                    var plugin = (IDalamudPlugin) Activator.CreateInstance(type);

                    var pluginDef = new PluginDefinition {
                        Author = "developer",
                        Name = plugin.Name,
                        InternalName = Path.GetFileNameWithoutExtension(dllFile.Name),
                        AssemblyVersion = plugin.GetType().Assembly.GetName().Version.ToString(),
                        Description = "Loaded by DalamudControl",
                        ApplicableVersion = "any",
                        IsHide = false
                    };

                    var dalamudInterface = new DalamudPluginInterface(dalamud, type.Assembly.GetName().Name, pluginConfigs);

                    plugin.Initialize(dalamudInterface);
                    PluginLog.Log("Loaded Plugin: {0}", plugin.Name);

                    pluginsList.Add((plugin, pluginDef, dalamudInterface));
                    if (pluginLoadConfig != null) {
                        pluginLoadConfig.PluginInternalName = pluginDef.InternalName;
                        pluginLoadConfig.Loaded = true;
                    }
                }
            }
        }


        public void SetupCommands() {
            PluginInterface.CommandManager.AddHandler("/plpl", new Dalamud.Game.Command.CommandInfo(OnConfigCommandHandler) {
                HelpMessage = $"Open config window for {this.Name}",
                ShowInHelp = false
            });

            PluginInterface.CommandManager.AddHandler("/plpl_load", new Dalamud.Game.Command.CommandInfo(OnLoadCommandHandler) {
                HelpMessage = $"Load a plugin from a given path",
                ShowInHelp = true
            });
        }

        private void OnLoadCommandHandler(string command, string arguments) {
            PluginInterface.Framework.Gui.Chat.Print($"Loading Plugin: {arguments}");
            LoadPlugin(arguments);
        }

        public void OnConfigCommandHandler(string command, string args) {
            drawConfigWindow = !drawConfigWindow;
        }

        public void RemoveCommands() {
            PluginInterface.CommandManager.RemoveHandler("/plivepluginloader");
        }

        private void BuildUI() {
            drawConfigWindow = drawConfigWindow && PluginConfig.DrawConfigUI();
        }
    }
}