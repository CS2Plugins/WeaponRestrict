using CounterStrikeSharp.API;

using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Attributes;

using CounterStrikeSharp.API.Modules.Config;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Modules.Commands;

using Microsoft.Extensions.Logging;

using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Data;
using System.Reflection;

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
        [JsonPropertyName("MessagePrefix")] public string MessagePrefix { get; set; } = "{Colors.Orange}[WeaponRestrict] ";
        [JsonPropertyName("RestrictMessage")] public string RestrictMessage { get; set; } = "{Colors.LightPurple}{0}{Colors.Default} is currently restricted to {Colors.LightRed}{1}{Colors.Default} per team.";
        [JsonPropertyName("DisabledMessage")] public string DisabledMessage { get; set; } = "{Colors.LightPurple}{0}{Colors.Default} is currently {Colors.LightRed}disabled{Colors.Default}.";

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

        public override string ModuleVersion => "2.3.0";

        public override string ModuleAuthor => "jon, sapphyrus & FireBird";

        public override string ModuleDescription => "Restricts player weapons based on total player or teammate count.";

        public required WeaponRestrictConfig Config { get; set; }

        public required MemoryFunctionWithReturn<CCSPlayer_ItemServices, CEconItemView, AcquireMethod, NativeObject, AcquireResult> CCSPlayer_CanAcquireFunc;

        public required MemoryFunctionWithReturn<int, string, CCSWeaponBaseVData> GetCSWeaponDataFromKeyFunc;

        public Dictionary<string, float> WeaponQuotas = new();
        public Dictionary<string, int> WeaponLimits = new();

        public CCSGameRules? gameRules;

        public override void Load(bool hotReload)
        {
            GetCSWeaponDataFromKeyFunc = new(GameData.GetSignature("GetCSWeaponDataFromKey"));
            CCSPlayer_CanAcquireFunc = new(GameData.GetSignature("CCSPlayer_CanAcquire"));
            CCSPlayer_CanAcquireFunc.Hook(OnWeaponCanAcquire, HookMode.Pre);

            RegisterListener<Listeners.OnMapStart>((mapName) =>
            {
                Server.NextWorldUpdate(() =>
                {
                    gameRules = GetGameRules();
                    LoadMapConfig();
                });
            });

            if (hotReload)
            {
                Server.NextWorldUpdate(() =>
                {
                    gameRules = GetGameRules();
                    LoadMapConfig();
                });
            }
        }

        public override void Unload(bool hotReload)
        {
            CCSPlayer_CanAcquireFunc.Unhook(OnWeaponCanAcquire, HookMode.Pre);
        }

        private static CCSGameRules GetGameRules()
        {
            foreach (CBaseEntity entity in Utilities.FindAllEntitiesByDesignerName<CBaseEntity>("cs_gamerules"))
            {
                return new CCSGameRules(entity.Handle);
            }

            throw new Exception("No CCSGameRules found!");
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
                return;
            }
            
            if (commandType == "none")
            {
                WeaponQuotas.Remove(weapon);
                WeaponLimits.Remove(weapon);

                commandInfo.ReplyToCommand($"WeaponRestrict: \"{weapon}\" is now unrestricted.");
                return;
            }
            
            if (!float.TryParse(commandInfo.GetArg(3), out float limit))
            {
                limit = -1f;
            }

            switch (commandType)
            {
                case "quota":
                    if (limit >= 0)
                    {
                        WeaponQuotas[weapon] = limit;

                        commandInfo.ReplyToCommand($"WeaponRestrict: Restricted \"{weapon}\" to \"{limit}\" per player(s) on team");
                        return;
                    }

                    WeaponQuotas.Remove(weapon);

                    commandInfo.ReplyToCommand($"WeaponRestrict: Removed quota for \"{weapon}\"");
                    break;
                case "limit":
                    if (limit >= 0)
                    {
                        int roundedLimit = (int)Math.Round(limit);
                        WeaponLimits[weapon] = roundedLimit;

                        commandInfo.ReplyToCommand($"WeaponRestrict: Restricted \"{weapon}\" to \"{roundedLimit}\" per team");
                    }

                    WeaponLimits.Remove(weapon);

                    commandInfo.ReplyToCommand($"WeaponRestrict: Removed limit for \"{weapon}\"");
                    break;
                case "default":
                    LoadMapConfig();

                    commandInfo.ReplyToCommand($"WeaponRestrict: Reset to default for \"{weapon}\"");
                    break;
                default:
                    commandInfo.ReplyToCommand("WeaponRestrict: Unknown restrict method specified. Please use \"quota\", \"limit\", \"default\", or \"none\"");
                    break;
            }
        }

        private void LoadMapConfig()
        {
            // Load map config if exists
            if (Server.MapName == null) return; // Null check on server boot

            Dictionary<string, Dictionary<string, float>> currentMapConfig;

            // First check if there is any direct value for the map name in MapConfigs
            if (!Config.MapConfigs.TryGetValue(Server.MapName, out currentMapConfig!))
            {
                // If the first check failed, check with regex on every MapConfigs key
                KeyValuePair<string, Dictionary<string, Dictionary<string, float>>> wildcardConfig = Config.MapConfigs.FirstOrDefault(p => Regex.IsMatch(Server.MapName, $"^{p.Key}$"));

                // If there is a match, and the properties are not null, set the currentMapConfig variable to the regex match value.
                if (wildcardConfig.Value != null && wildcardConfig.Value.Count >= 0)
                {
                    currentMapConfig = wildcardConfig.Value;
                }
                else
                {
                    // Load the default config
                    if (Config.WeaponLimits != null) {
                        WeaponLimits = Config.WeaponLimits;
                    } else {
                        WeaponLimits.Clear();
                    }

                    if (Config.WeaponQuotas != null) {
                        WeaponQuotas = Config.WeaponQuotas;
                    } else {
                        WeaponLimits.Clear();
                    }

                    Logger.LogInformation($"WeaponRestrict: Loaded default config for {Server.MapName} (Limits: {string.Join(Environment.NewLine, WeaponLimits)}, Quotas: {string.Join(Environment.NewLine, WeaponQuotas)})");
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

            Logger.LogInformation($"WeaponRestrict: Loaded map config for {Server.MapName} (Limits: {string.Join(Environment.NewLine, WeaponLimits)}, Quotas: {string.Join(Environment.NewLine, WeaponQuotas)})");
        }

        public static string FormatChatColors(string s)
        {
            const string FieldPrefix = "Color.";

            if (string.IsNullOrEmpty(s))
            {
                return s;
            }

            foreach (FieldInfo field in typeof(ChatColors).GetFields().Where(f => (f.FieldType == typeof(char) || f.FieldType == typeof(string)) && f.IsStatic && !string.IsNullOrEmpty(f.Name)))
            {
                s = s.Replace($"{{{FieldPrefix}{field.Name}}}", field.GetValue(null)!.ToString());
            }
            return s;
        }

        public void OnConfigParsed(WeaponRestrictConfig newConfig)
        {
            newConfig = ConfigManager.Load<WeaponRestrictConfig>("WeaponRestrict");

            // Create empty variables for non-nullable types
            newConfig.WeaponLimits  ??= new();
            newConfig.WeaponQuotas  ??= new();

            if (newConfig.MapConfigs == null)
            {
                newConfig.MapConfigs = new Dictionary<string, Dictionary<string, Dictionary<string, float>>>();
                newConfig.MapConfigs.Clear();
            }

            // Format chat colors
            newConfig.MessagePrefix     = "\u1010" + FormatChatColors(newConfig.MessagePrefix);
            newConfig.DisabledMessage   = FormatChatColors(newConfig.DisabledMessage);
            newConfig.RestrictMessage   = FormatChatColors(newConfig.RestrictMessage);

            Config = newConfig;

            LoadMapConfig();
        }

        private int CountWeaponsOnTeam(string designerName, IEnumerable<CCSPlayerController> players)
        {
            int count = 0;

            foreach (CCSPlayerController player in players)
            {
                // VIP, null and alive check
                if ((Config.VIPFlag != "" && AdminManager.PlayerHasPermissions(player, Config.VIPFlag))
                    || player.PlayerPawn == null
                    || !player.PawnIsAlive
                    || player.PlayerPawn.Value == null
                    || !player.PlayerPawn.Value.IsValid
                    || player.PlayerPawn.Value.WeaponServices == null
                    || player.PlayerPawn.Value.WeaponServices.MyWeapons == null)
                continue;

                // Iterate over player weapons
                foreach (CHandle<CBasePlayerWeapon> weapon in player.PlayerPawn.Value.WeaponServices.MyWeapons)
                {
                    //Get the item DesignerName and compare it to the counted name
                    if (weapon.Value == null || weapon.Value.DesignerName != designerName) continue;
                    // Increment count if weapon is found
                    count++;
                }
            }

            return count;
        }

        public HookResult OnWeaponCanAcquire(DynamicHook hook)
        {
            if (Config.AllowPickup && gameRules != null && gameRules.BuyTimeEnded && hook.GetParam<AcquireMethod>(2) == AcquireMethod.PickUp)
                return HookResult.Continue;

            CCSWeaponBaseVData vdata = GetCSWeaponDataFromKeyFunc.Invoke(-1, hook.GetParam<CEconItemView>(1).ItemDefinitionIndex.ToString()) ?? throw new Exception("Failed to get CCSWeaponBaseVData");

            // Weapon is not restricted
            if (!WeaponQuotas.ContainsKey(vdata.Name) && !WeaponLimits.ContainsKey(vdata.Name))
                return HookResult.Continue;

            CCSPlayerController client = hook.GetParam<CCSPlayer_ItemServices>(0).Pawn.Value!.Controller.Value!.As<CCSPlayerController>();

            if (client == null || !client.IsValid || !client.PawnIsAlive)
                return HookResult.Continue;

            // Player is VIP proof
            if (Config.VIPFlag != "" && AdminManager.PlayerHasPermissions(client, Config.VIPFlag))
                return HookResult.Continue;

            // Get every valid player that is currently connected
            IEnumerable<CCSPlayerController> players = Utilities.GetPlayers().Where(player =>
                player.IsValid // Unneccessary?
                && player.Connected == PlayerConnectedState.PlayerConnected
                && (!Config.DoTeamCheck || player.Team == client.Team)
                );

            int limit = int.MaxValue;
            bool disabled = false;
            if (WeaponQuotas.TryGetValue(vdata.Name, out float cfgQuota))
            {
                limit = Math.Min(limit, cfgQuota > 0f ? (int)(players.Count() * cfgQuota) : 0);
                disabled |= cfgQuota == 0f;
            }
            
            if (WeaponLimits.TryGetValue(vdata.Name, out int cfgLimit))
            {
                limit = Math.Min(limit, cfgLimit);
                disabled |= cfgLimit == 0;
            }

            if (!disabled)
            {
                int count = CountWeaponsOnTeam(vdata.Name, players);
                if (count < limit)
                    return HookResult.Continue;
            }

            // Print chat message if we attempted to do anything except pick up this weapon. This is to prevent chat spam.
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