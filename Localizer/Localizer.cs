using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using Harmony;
using Localizer.Attributes;
using Localizer.DataModel;
using Localizer.DataModel.Default;
using Localizer.Helpers;
using Localizer.Modules;
using Localizer.Network;
using Localizer.Package.Import;
using Localizer.UIs;
using Localizer.UIs.Views;
using log4net;
using Microsoft.Xna.Framework;
using MonoMod.Cil;
using MonoMod.RuntimeDetour.HookGen;
using MonoMod.Utils;
using Ninject;
using Ninject.Modules;
using Noro;
using Noro.Access;
using Terraria;
using Terraria.Localization;
using Terraria.ModLoader;
using Terraria.ModLoader.Core;
using Terraria.UI;
using static Localizer.Lang;
using File = System.IO.File;

namespace Localizer
{
    public sealed class Localizer : Mod
    {
        public static string SavePath;
        public static string SourcePackageDirPath;
        public static string DownloadPackageDirPath;
        public static string ConfigPath;
        public static Localizer Instance { get; private set; }
        public static ILog Log { get; private set; }
        public static TmodFile TmodFile { get; private set; }
        public static Configuration Config { get; set; }
        public static OperationTiming State { get; internal set; }
        internal static LocalizerKernel Kernel { get; private set; }
        internal static HarmonyInstance Harmony { get; set; }

        private static Dictionary<int, GameCulture> _gameCultures;

        private static bool _initiated = false;

        public Localizer()
        {
            Instance = this;
            var mod = new LoadedModWrapper(ReflUtils.FindType("Terraria.ModLoader.Core.AssemblyManager")
                                               .F("loadedMods")
                                               .M("get_Item", "!Localizer"));
            this.A()["<File>k__BackingField"] = mod.File;
            this.A()["<Code>k__BackingField"] = mod.Code;
            Log = LogManager.GetLogger(nameof(Localizer));

            Harmony = HarmonyInstance.Create(nameof(Localizer));
            Harmony.Prefix<Localizer>(nameof(AfterLocalizerCtorHook))
                   .Detour("Terraria.ModLoader.Core.AssemblyManager", "Instantiate");

            State = OperationTiming.BeforeModCtor;
            TmodFile = Instance.P("File") as TmodFile;
            Init();
            _initiated = true;
        }

        private static void AfterLocalizerCtorHook(object mod)
        {
            Hooks.InvokeBeforeModCtor(mod);
        }

        private static void Init()
        {
            _gameCultures = typeof(GameCulture).F("_legacyCultures") as Dictionary<int, GameCulture>;

            ServicePointManager.ServerCertificateValidationCallback += (s, cert, chain, sslPolicyErrors) => true;
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;

            SavePath = "./Localizer/";
            SourcePackageDirPath = SavePath + "/Source/";
            DownloadPackageDirPath = SavePath + "/Download/";
            ConfigPath = SavePath + "/Config.json";

            Utils.EnsureDir(SavePath);
            Utils.EnsureDir(SourcePackageDirPath);
            Utils.EnsureDir(DownloadPackageDirPath);

            LoadConfig();
            AddModTranslations(Instance);
            Kernel = new LocalizerKernel();
            Kernel.Init();

            var autoImportService = Kernel.Get<AutoImportService>();
        }

        public override void Load()
        {
            if(!_initiated)
                throw new Exception("Localizer not initialized.");
            State = OperationTiming.BeforeModLoad;
            Hooks.InvokeBeforeLoad();
            Kernel.Get<RefreshLanguageService>();
            
            if (LanguageManager.Instance.ActiveCulture == GameCulture.Chinese)
            {
                ModBrowser.Patches.Patch();
            }
        }

        public override void PostSetupContent()
        {
            State = OperationTiming.BeforeContentLoad;
            Hooks.InvokeBeforeSetupContent();
            CheckUpdate();
            AddPostDrawHook();
        }

        private UIHost _uiHost;
        private void AddPostDrawHook()
        {
            _uiHost = new UIHost();
            
            Main.OnPostDraw += OnPostDraw;
            Hooks.PostDraw += time =>
            {
                _uiHost.Update(time);
                _uiHost.Draw(time);
            };
        }

        private void OnPostDraw(GameTime time)
        {
            if (Main.dedServ)
                return;
                
            Main.spriteBatch.SafeBegin();
            Hooks.InvokeOnPostDraw(time);
            Main.DrawCursor(Main.DrawThickCursor(false), false);
            Main.spriteBatch.SafeEnd();
        }

        public override void PostAddRecipes()
        {
            State = OperationTiming.PostContentLoad;
            Hooks.InvokePostSetupContent();
        }

        public override void UpdateUI(GameTime gameTime)
        {
            Hooks.InvokeOnGameUpdate(gameTime);
        }

        public void CheckUpdate()
        {
            Task.Run(() =>
            {
                var curVersion = this.Version;
                if (Kernel.Get<IUpdateService>().CheckUpdate(curVersion, out var updateInfo))
                {
                    var msg = _("NewVersion", updateInfo.Version);
                    if (Main.gameMenu)
                    {
                        UI.ShowInfoMessage(msg, 0);
                    }
                    else
                    {
                        Main.NewText(msg, Color.Red);
                    }
                }
            });
        }

        public override void Unload()
        {
            try
            {
                SaveConfig();

                Main.OnPostDraw -= OnPostDraw;
                
                HookEndpointManager.RemoveAllOwnedBy(this);
                Harmony.UnpatchAll(nameof(Localizer));
                Kernel.Dispose();

                Harmony = null;
                Kernel = null;
                _gameCultures = null;
                Config = null;
                Instance = null;
            }
            catch (Exception e)
            {
                Log.Error(e);
            }
            finally
            {
                _initiated = false;
                Log = null;
            }

            base.Unload();
        }

        public static void LoadConfig()
        {
            Log.Info("Loading config");
            if (File.Exists(ConfigPath))
            {
                Config = Utils.ReadFileAndDeserializeJson<Configuration>(ConfigPath);
                if (Config is null)
                {
                    throw new Exception("Config read failed!");
                }
            }
            else
            {
                Log.Info("No config file, creating...");
                Config = new Configuration();
            }

            Utils.SerializeJsonAndCreateFile(Config, ConfigPath);
            Log.Info("Config loaded");
        }

        public static void SaveConfig()
        {
            Log.Info("Saving config...");
            Utils.SerializeJsonAndCreateFile(Config, ConfigPath);
            Log.Info("Config saved");
        }

        public static GameCulture AddGameCulture(CultureInfo culture)
        {
            return GameCulture.FromName(culture.Name) != null
                ? null
                : new GameCulture(culture.Name, _gameCultures.Count);
        }

        public static GameCulture CultureInfoToGameCulture(CultureInfo culture)
        {
            var gc = GameCulture.FromName(culture.Name);
            return gc ?? AddGameCulture(culture);
        }

        public static void RefreshLanguages()
        {
            Kernel.Get<RefreshLanguageService>().Refresh();
        }

        public static IMod GetWrappedMod(string name)
        {
            if (State < OperationTiming.PostContentLoad)
            {
                var loadedMods = Utils.TR().F("Terraria.ModLoader.Core.AssemblyManager")
                                     .F("loadedMods");
                if ((bool)loadedMods.M("ContainsKey", name))
                {
                    return new LoadedModWrapper(loadedMods.M("get_Item", name));
                }

                return null;
            }

            var mod = Utils.GetModByName(name);
            if (mod is null)
                return null;
            return new ModWrapper(mod);
        }

        public static bool CanDoOperationNow(Type t)
        {
            var attribute = t.GetCustomAttribute<OperationTimingAttribute>();
            return attribute == null || CanDoOperationNow(attribute.Timing);
        }

        public static bool CanDoOperationNow(OperationTiming t)
        {
            return (t & State) != 0;
        }
    }
}
