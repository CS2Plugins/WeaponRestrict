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
        [JsonIgnore]
        public const int CONFIG_VERSION = 3;

        [JsonIgnore]
        public const string WEAPON_QUOTAS = "WeaponQuotas";
        
        [JsonIgnore]
        public const string WEAPON_LIMITS = "WeaponLimits";

        [JsonPropertyName("MessagePrefix")] public string MessagePrefix { get; set; } = "{Color.Orange}[WeaponRestrict] ";
        [JsonPropertyName("RestrictMessage")] public string RestrictMessage { get; set; } = "{Color.LightPurple}{0}{Color.Default} is currently restricted to {Color.LightRed}{1}{Color.Default} per team.";
        [JsonPropertyName("DisabledMessage")] public string DisabledMessage { get; set; } = "{Color.LightPurple}{0}{Color.Default} is currently {Color.LightRed}disabled{Color.Default}.";

        [JsonPropertyName("DefaultQuotas")]
        public Dictionary<string, float> DefaultQuotas { get; set; } = new Dictionary<string, float>()
        {
            ["weapon_awp"] = 0.2f
        };

        [JsonPropertyName("DefaultLimits")]
        public Dictionary<string, int> DefaultLimits { get; set; } = new Dictionary<string, int>()
        {
            ["weapon_awp"] = 1
        };

        [JsonPropertyName("DoTeamCheck")] public bool DoTeamCheck { get; set; } = true;

        [JsonPropertyName("AllowPickup")] public bool AllowPickup { get; set; } = false;

        [JsonPropertyName("RestrictWarmup")] public bool RestrictWarmup { get; set; } = true;

        [JsonPropertyName("VIPFlag")] public string VIPFlag { get; set; } = "@css/vip";

        [JsonPropertyName("MapConfigs")]
        public Dictionary<string, Dictionary<string, Dictionary<string, float>>> MapConfigs { get; set; } = new Dictionary<string, Dictionary<string, Dictionary<string, float>>>()
        {
            ["de_dust2"] = new Dictionary<string, Dictionary<string, float>>()
            {
                [WEAPON_QUOTAS] = new Dictionary<string, float>()
                {
                    ["weapon_awp"] = 0.2f
                },
                [WEAPON_LIMITS] = new Dictionary<string, float>()
                {
                    ["weapon_awp"] = 1
                },
                ["awp.*"] = new Dictionary<string, float>()
            }
        };
        
        [JsonPropertyName("ConfigVersion")] public new int Version { get; set; } = CONFIG_VERSION;
    }

    [MinimumApiVersion(239)]
    public class WeaponRestrictPlugin : BasePlugin, IPluginConfig<WeaponRestrictConfig>
    {
        public override string ModuleName => "WeaponRestrict";

        public override string ModuleVersion => "2.3.1";

        public override string ModuleAuthor => "jon, sapphyrus & FireBird";

        public override string ModuleDescription => "Restricts player weapons based on total player or teammate count.";

        public required WeaponRestrictConfig Config { get; set; }

        public required MemoryFunctionWithReturn<CCSPlayer_ItemServices, CEconItemView, AcquireMethod, NativeObject, AcquireResult> CCSPlayer_CanAcquireFunc;

        public required MemoryFunctionWithReturn<int, string, CCSWeaponBaseVData> GetCSWeaponDataFromKeyFunc;

        public readonly Dictionary<string, float> WeaponQuotas = new();
        public readonly Dictionary<string, int> WeaponLimits = new();

        public bool InWarmup = false;

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

            RegisterEventHandler<EventRoundAnnounceWarmup>((@event, info) =>
            {
                InWarmup = true;

                return HookResult.Continue;
            });

            RegisterEventHandler<EventRoundAnnounceMatchStart>((@event, info) =>
            {
                InWarmup = false;

                return HookResult.Continue;
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
                    if (limit >= 0f)
                    {
                        WeaponQuotas[weapon] = limit;

                        commandInfo.ReplyToCommand($"WeaponRestrict: Restricted \"{weapon}\" to \"{limit}\" per player(s) on team");
                    }
                    else
                    {
                        WeaponQuotas.Remove(weapon);

                        commandInfo.ReplyToCommand($"WeaponRestrict: Removed quota for \"{weapon}\"");
                    }

                    break;
                case "limit":
                    if (limit >= 0f)
                    {
                        int roundedLimit = (int)Math.Round(limit);
                        WeaponLimits[weapon] = roundedLimit;

                        commandInfo.ReplyToCommand($"WeaponRestrict: Restricted \"{weapon}\" to \"{roundedLimit}\" per team");
                    }
                    else
                    {
                        WeaponLimits.Remove(weapon);

                        commandInfo.ReplyToCommand($"WeaponRestrict: Removed limit for \"{weapon}\"");
                    }

                    break;
                case "default":
                    // TODO: Grab the value from the config and do not reload the entire map config.
                    LoadMapConfig();

                    commandInfo.ReplyToCommand($"WeaponRestrict: Reset to default weapon restrictions.");
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

            // Key: Restriction type, Value: Restriction values
            Dictionary<string, Dictionary<string, float>>? currentMapConfig = null;

            if (!Config.MapConfigs.TryGetValue(Server.MapName, out currentMapConfig))
            {
                currentMapConfig = null;
                var cfgEnum = Config.MapConfigs.Where(x => Regex.IsMatch(Server.MapName, $"^{x.Key}$")).Select(x => x.Value);

                if (cfgEnum.Any())
                {
                    if (cfgEnum.Count() > 1)
                    {
                        Logger.LogInformation("WeaponRestrict: Ambiguous wildcard search for {Mapname} in configs.", Server.MapName);
                    }

                    // Load the found wildcard config
                    currentMapConfig = cfgEnum.First();
                }
            }

            WeaponQuotas.Clear();
            WeaponLimits.Clear();

            if (currentMapConfig == null)
            {
                // Load the default config
                foreach (var (key, value) in Config.DefaultQuotas)
                {
                    WeaponQuotas[key] = value;
                }

                foreach (var (key, value) in Config.DefaultLimits)
                {
                    WeaponLimits[key] = value;
                }
            }
            else
            {
                // Load the found config
                if (currentMapConfig.TryGetValue(WeaponRestrictConfig.WEAPON_QUOTAS, out Dictionary<string, float>? newQuotas))
                {
                    foreach (var (key, value) in newQuotas)
                    {
                        WeaponQuotas[key] = value;
                    }
                }

                if (currentMapConfig.TryGetValue(WeaponRestrictConfig.WEAPON_LIMITS, out Dictionary<string, float>? newLimits))
                {
                    foreach (var (key, value) in newLimits)
                    {
                        WeaponLimits[key] = (int)value;
                    }
                }
            }

            Logger.LogInformation("WeaponRestrict: Loaded {DefaultPrefix}config for {MapName} (Limits: {Limits}, Quotas: {Quotas})", 
                        currentMapConfig == null ? "default " : "",
                        Server.MapName, 
                        string.Join(",", WeaponLimits), 
                        string.Join(",", WeaponQuotas));
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

        public void OnConfigParsed(WeaponRestrictConfig loadedConfig)
        {
            loadedConfig = ConfigManager.Load<WeaponRestrictConfig>("WeaponRestrict");

            if (loadedConfig.Version < WeaponRestrictConfig.CONFIG_VERSION)
            {
                Logger.LogInformation("WeaponRestrict: Outdated config version. Please review the latest changes and update the config version to {NewCfgVersion}", WeaponRestrictConfig.CONFIG_VERSION);
            }

            // TODO: Somehow check for default values?

            // Format chat colors
            loadedConfig.MessagePrefix     = "\u1010" + FormatChatColors(loadedConfig.MessagePrefix);
            loadedConfig.DisabledMessage   = FormatChatColors(loadedConfig.DisabledMessage);
            loadedConfig.RestrictMessage   = FormatChatColors(loadedConfig.RestrictMessage);

            Config = loadedConfig;

            LoadMapConfig();
        }

        private int CountWeaponsOnTeam(string designerName, IEnumerable<CCSPlayerController> players)
        {
            int count = 0;

            foreach (CCSPlayerController player in players)
            {
                // TODO: Null check can be simplified due to new API changes
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
            // Warmup check
            if (!Config.RestrictWarmup && InWarmup)
                return HookResult.Continue;
            
            if (gameRules != null)
            {
                if (Config.AllowPickup && gameRules.BuyTimeEnded && hook.GetParam<AcquireMethod>(2) == AcquireMethod.PickUp)
                    return HookResult.Continue;
            }

            CCSWeaponBaseVData vdata = GetCSWeaponDataFromKeyFunc.Invoke(-1, hook.GetParam<CEconItemView>(1).ItemDefinitionIndex.ToString()) ?? throw new Exception("Failed to get CCSWeaponBaseVData");

            // Weapon is not restricted
            if (!WeaponQuotas.ContainsKey(vdata.Name) && !WeaponLimits.ContainsKey(vdata.Name))
                return HookResult.Continue;

            CCSPlayerController client = hook.GetParam<CCSPlayer_ItemServices>(0).Pawn.Value!.Controller.Value!.As<CCSPlayerController>();

            if (client == null || !client.IsValid || !client.PawnIsAlive)
                return HookResult.Continue;

            /*
            Logger.LogInformation("WeaponRestrict: {Player} is trying to acquire {Weapon} ({Method}) on {Map} (WMUP: {Warmup}, BUYT: {BuyTime})",
                client.PlayerName,
                vdata.Name,
                hook.GetParam<AcquireMethod>(2),
                Server.MapName,
                InWarmup,
                gameRules.BuyTimeEnded
            );
            */

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
                    msg = string.Format(Config.MessagePrefix + Config.DisabledMessage, vdata.Name);
                else if (Config.RestrictMessage != "")
                    msg = string.Format(Config.MessagePrefix + Config.RestrictMessage, vdata.Name, limit.ToString());

                if (msg != "")
                    Server.NextFrame(() => client.PrintToChat(msg));
            }
            else
            {
                hook.SetReturn(AcquireResult.InvalidItem);
            }

            return HookResult.Stop;
        }
    }
}