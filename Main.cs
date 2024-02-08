using System.Text.Json.Serialization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Config;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Utils;
using System.Data;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using System.Text.RegularExpressions;
using CounterStrikeSharp.API.Core.Attributes;

namespace WeaponRestrict
{
    // Possible results for CSPlayer::CanAcquire
    public enum AcquireResult : int
    {
        Allowed = 0,
        InvalidItem,
        AlreadyOwned,
        AlreadyPurchased,
        ReachedGrenadeTypeLimit,
        ReachedGrenadeTotalLimit,
        NotAllowedByTeam,
        NotAllowedByMap,
        NotAllowedByMode,
        NotAllowedForPurchase,
        NotAllowedByProhibition,
    };

    // Possible results for CSPlayer::CanAcquire
    public enum AcquireMethod : int
    {
        PickUp = 0,
        Buy,
    };

    public class WeaponRestrictConfig : BasePluginConfig
    {
        [JsonPropertyName("MessagePrefix")] public string MessagePrefix { get; set; } = "\u1010\u0010[WeaponRestrict] ";
        [JsonPropertyName("RestrictMessage")] public string RestrictMessage { get; set; } = "\u0003{0}\u0001 is currently restricted to \u000F{1}\u0001 per team.";
        [JsonPropertyName("DisabledMessage")] public string DisabledMessage { get; set; } = "\u0003{0}\u0001 is currently \u000Fdisabled\u0001.";

        [JsonPropertyName("WeaponQuotas")]
        public Dictionary<string, float> WeaponQuotas { get; set; } = new Dictionary<string, float>()
        {
            ["weapon_awp"] = 0.2f
        };

        [JsonPropertyName("WeaponLimits")]
        public Dictionary<string, int> WeaponLimits { get; set; } = new Dictionary<string, int>()
        {
            ["weapon_awp"] = 1
        };

        [JsonPropertyName("DoTeamCheck")] public bool DoTeamCheck { get; set; } = true;

        [JsonPropertyName("AllowPickup")] public bool AllowPickup { get; set; } = false;

        [JsonPropertyName("VIPFlag")] public string VIPFlag { get; set; } = "@css/vip";

        [JsonPropertyName("MapConfigs")]
        public Dictionary<string, Dictionary<string, Dictionary<string, float>>> MapConfigs { get; set; } = new Dictionary<string, Dictionary<string, Dictionary<string, float>>>()
        {
            ["de_dust2"] = new Dictionary<string, Dictionary<string, float>>()
            {
                ["WeaponQuotas"] = new Dictionary<string, float>()
                {
                    ["weapon_awp"] = 0.2f
                },
                ["WeaponLimits"] = new Dictionary<string, float>()
                {
                    ["weapon_awp"] = 1
                },
                ["awp.*"] = new Dictionary<string, float>()
            }
        };
        [JsonPropertyName("ConfigVersion")] public new int Version { get; set; } = 2;
    }

    [MinimumApiVersion(163)]
    public class WeaponRestrictPlugin : BasePlugin, IPluginConfig<WeaponRestrictConfig>
    {
        public override string ModuleName => "WeaponRestrict";

        public override string ModuleVersion => "2.2.0";

        public override string ModuleAuthor => "jon, sapphyrus & FireBird";

        public override string ModuleDescription => "Restricts player weapons based on total player or teammate count.";

        public required WeaponRestrictConfig Config { get; set; }

        public required MemoryFunctionWithReturn<CCSPlayer_ItemServices, CEconItemView, AcquireMethod, NativeObject, AcquireResult> CCSPlayer_CanAcquireFunc;

        public required MemoryFunctionWithReturn<int, string, CCSWeaponBaseVData> GetCSWeaponDataFromKeyFunc;

        public Dictionary<string, float> WeaponQuotas = new();
        public Dictionary<string, int> WeaponLimits = new();

        public override void Load(bool hotReload)
        {
            GetCSWeaponDataFromKeyFunc = new(GameData.GetSignature("GetCSWeaponDataFromKey"));
            CCSPlayer_CanAcquireFunc = new(GameData.GetSignature("CCSPlayer_CanAcquire"));
            CCSPlayer_CanAcquireFunc.Hook(OnWeaponCanAcquire, HookMode.Pre);

            RegisterListener<Listeners.OnMapStart>((mapName) =>
            {
                LoadMapConfig();
            });
        }

        public override void Unload(bool hotReload)
        {
            CCSPlayer_CanAcquireFunc.Unhook(OnWeaponCanAcquire, HookMode.Pre);
        }

        [ConsoleCommand("css_restrictweapon", "Restricts or unrestricts the specified weapon until mapchange or plugin reload")]
        [RequiresPermissions("@css/generic")]
        [CommandHelper(minArgs: 1, usage: "weapon_name [[default/none] | [quota/limit value]]", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
        public void OnRestrictCommand(CCSPlayerController? player, CommandInfo commandInfo)
        {
            string weapon = commandInfo.GetArg(1).ToLower();

            string commandType = commandInfo.GetArg(2).ToLower();

            if (weapon == "")
            {
                commandInfo.ReplyToCommand("WeaponRestrict: Please specify a weapon name");
            }
            else if (commandType == "none")
            {
                WeaponQuotas.Remove(weapon);
                WeaponLimits.Remove(weapon);

                commandInfo.ReplyToCommand($"WeaponRestrict: \"{weapon}\" is now unrestricted.");
            }
            else
            {
                // Unrestrict based on method
                if (float.TryParse(commandInfo.GetArg(3), out float limit))
                {
                    if (commandType == "quota")
                    {
                        WeaponQuotas[weapon] = limit;

                        commandInfo.ReplyToCommand($"WeaponRestrict: Restricted \"{weapon}\" to \"{limit}\" per player(s) on team");
                    }
                    else if (commandType == "limit")
                    {
                        int roundedLimit = (int)Math.Round(limit);
                        WeaponLimits[weapon] = roundedLimit;

                        commandInfo.ReplyToCommand($"WeaponRestrict: Restricted \"{weapon}\" to \"{roundedLimit}\" per team");
                    }
                    else
                    {
                        commandInfo.ReplyToCommand("WeaponRestrict: Unknown restrict method specified. Please use \"quota\" or \"limit\" with a number");
                    }
                }
                else
                {
                    if (commandType == "quota")
                    {
                        WeaponQuotas.Remove(weapon);

                        commandInfo.ReplyToCommand($"WeaponRestrict: Removed quota for \"{weapon}\"");
                    }
                    else if (commandType == "limit")
                    {
                        WeaponLimits.Remove(weapon);

                        commandInfo.ReplyToCommand($"WeaponRestrict: Removed limit for \"{weapon}\"");
                    }
                    else if (commandType == "default")
                    {
                        LoadMapConfig();

                        commandInfo.ReplyToCommand($"WeaponRestrict: Reset to default for \"{weapon}\"");
                    }
                    else
                    {
                        commandInfo.ReplyToCommand("WeaponRestrict: Unknown restrict method specified. Please use \"quota\", \"limit\", \"default\", or \"none\"");
                    }
                }
            }
        }

        public void LoadMapConfig()
        {
            // Load map config if exists
            if (Server.MapName == null) return; // Null check on server boot

            Dictionary<string, Dictionary<string, float>> currentMapConfig;

            if (!Config.MapConfigs.TryGetValue(Server.MapName, out currentMapConfig!))
            {
                KeyValuePair<string, Dictionary<string, Dictionary<string, float>>> wildcardConfig = Config.MapConfigs.FirstOrDefault(p => Regex.IsMatch(Server.MapName, $"^{p.Key}$"));

                if (wildcardConfig.Value != null && wildcardConfig.Value.Count >= 0)
                {
                    currentMapConfig = wildcardConfig.Value;
                }
                else
                {
                    // Load default config
                    WeaponLimits = Config.WeaponLimits;
                    WeaponQuotas = Config.WeaponQuotas;

                    Console.WriteLine($"WeaponRestrict: Loaded default config for {Server.MapName} (Limits: {string.Join(Environment.NewLine, WeaponLimits)}, Quotas: {string.Join(Environment.NewLine, WeaponQuotas)})");
                    return;
                }
            };

            if (currentMapConfig.ContainsKey("WeaponQuotas"))
            {
                WeaponQuotas = currentMapConfig["WeaponQuotas"];
            }
            else
            {
                WeaponQuotas.Clear();
            }

            if (currentMapConfig.ContainsKey("WeaponLimits"))
            {
                // Convert float dict to int dict (stored as float values for simplicity)
                WeaponLimits = currentMapConfig["WeaponLimits"].ToDictionary(k => k.Key, v => (int)v.Value);
            }
            else
            {
                WeaponLimits.Clear();
            }

            Console.WriteLine($"WeaponRestrict: Loaded map config for {Server.MapName} (Limits: {string.Join(Environment.NewLine, WeaponLimits)}, Quotas: {string.Join(Environment.NewLine, WeaponQuotas)})");
        }

        public void OnConfigParsed(WeaponRestrictConfig newConfig)
        {
            newConfig = ConfigManager.Load<WeaponRestrictConfig>("WeaponRestrict");

            // Create empty variables for non-nullable types
            newConfig.MapConfigs    ??= new();
            newConfig.WeaponLimits  ??= new();
            newConfig.WeaponQuotas  ??= new();

            Config = newConfig;

            LoadMapConfig();
        }

        private int CountWeaponsOnTeam(string name, List<CCSPlayerController> players)
        {
            int count = 0;

            foreach (CCSPlayerController player in players)
            {
                // Skip counting VIP players
                if (Config.VIPFlag != "" && AdminManager.PlayerHasPermissions(player, Config.VIPFlag)) continue;

                // Get all weapons (ignore null dereference because we already null checked these)
                foreach (CHandle<CBasePlayerWeapon> weapon in player.PlayerPawn.Value!.WeaponServices!.MyWeapons)
                {
                    //Get the item DesignerName and compare it to the counted name
                    if (weapon.Value is null || weapon.Value.DesignerName != name) continue;
                    // Increment count if weapon is found
                    count++;
                }
            }

            return count;
        }

        private HookResult OnWeaponCanAcquire(DynamicHook hook)
        {
            if (Config.AllowPickup && hook.GetParam<AcquireMethod>(2) == AcquireMethod.PickUp) 
                return HookResult.Continue;

            var vdata = GetCSWeaponDataFromKeyFunc.Invoke(-1, hook.GetParam<CEconItemView>(1).ItemDefinitionIndex.ToString());

            // Weapon is not restricted
            if (!WeaponQuotas.ContainsKey(vdata.Name) && !WeaponLimits.ContainsKey(vdata.Name))
                return HookResult.Continue;

            var client = hook.GetParam<CCSPlayer_ItemServices>(0).Pawn.Value!.Controller.Value!.As<CCSPlayerController>();

            if (client is null || !client.IsValid || !client.PawnIsAlive)
                return HookResult.Continue;

            // Player is VIP proof
            if (Config.VIPFlag != "" && AdminManager.PlayerHasPermissions(client, Config.VIPFlag))
                return HookResult.Continue;

            // Get every valid player, that is currently connected, and is alive, also check for teamcheck (and LOTS of null checks... because Valve)
            List<CCSPlayerController> players = Utilities.GetPlayers().Where(player =>
                player.IsValid
                && player.Connected == PlayerConnectedState.PlayerConnected
                && player.PlayerPawn != null
                && player.PawnIsAlive
                && player.PlayerPawn.Value != null
                && player.PlayerPawn.Value.IsValid
                && player.PlayerPawn.Value.WeaponServices != null
                && player.PlayerPawn.Value.WeaponServices.MyWeapons != null
                && (!Config.DoTeamCheck || player.Team == client.Team)
                ).ToList();

            int limit = int.MaxValue;
            bool disabled = false;
            if (WeaponQuotas.ContainsKey(vdata.Name))
            {
                limit = Math.Min(limit, WeaponQuotas[vdata.Name] > 0f ? (int)(players.Count * WeaponQuotas[vdata.Name]) : 0);
                disabled |= WeaponQuotas[vdata.Name] == 0f;
            }
            if (WeaponLimits.ContainsKey(vdata.Name))
            {
                limit = Math.Min(limit, WeaponLimits[vdata.Name]);
                disabled |= WeaponLimits[vdata.Name] == 0;
            }

            int count = CountWeaponsOnTeam(vdata.Name, players);
            if (count < limit)
                return HookResult.Continue;

            // Print chat message if we attempted to buy this weapon
            if (hook.GetParam<AcquireMethod>(2) != AcquireMethod.PickUp)
            {
                hook.SetReturn(AcquireResult.AlreadyOwned);

                string msg = "";
                if (disabled && Config.DisabledMessage != "")
                    msg = FormatChatMessage(Config.DisabledMessage, vdata.Name);
                else if (Config.RestrictMessage != "")
                    msg = FormatChatMessage(Config.RestrictMessage, vdata.Name, limit.ToString());

                if (msg != "")
                    Server.NextFrame(() => client.PrintToChat(msg));
            }
            else
            {
                hook.SetReturn(AcquireResult.InvalidItem);
            }

            return HookResult.Stop;
        }

        private string FormatChatMessage(string message, params string[] varArgs)
        {
            return string.Format(Config.MessagePrefix + message, varArgs);
        }
    }
}