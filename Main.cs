using CounterStrikeSharp.API;

using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Attributes;

using CounterStrikeSharp.API.Modules.Config;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Memory;

using Microsoft.Extensions.Logging;

using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Data;
using System.Reflection;

namespace WeaponRestrict
{
    enum RestrictReason
    {
        NotRestricted,
        Limit,
        Disabled
    }

    public class MapConfig
    {
        [JsonPropertyName("WeaponQuotas")]
        public Dictionary<string, float> WeaponQuotas { get; set; } = [];

        [JsonPropertyName("WeaponLimits")]
        public Dictionary<string, int> WeaponLimits { get; set; } = [];
    }

    public class WeaponRestrictConfig : BasePluginConfig
    {
        [JsonIgnore]
        public const int CONFIG_VERSION = 5;

        [JsonIgnore]
        public const string WEAPON_QUOTAS = "WeaponQuotas";
        
        [JsonIgnore]
        public const string WEAPON_LIMITS = "WeaponLimits";

        [JsonPropertyName("MessagePrefix")] public string MessagePrefix { get; set; } = "{Color.Orange}[WeaponRestrict] ";
        [JsonPropertyName("RestrictMessage")] public string RestrictMessage { get; set; } = "{Color.LightPurple}{0}{Color.Default} is currently restricted to {Color.LightRed}{1}{Color.Default} per team.";
        [JsonPropertyName("DisabledMessage")] public string DisabledMessage { get; set; } = "{Color.LightPurple}{0}{Color.Default} is currently {Color.LightRed}disabled{Color.Default}.";

        [JsonPropertyName("MessageCooldownSeconds")]
        public float MessageCooldownSeconds { get; set; } = 10f;

        public MapConfig DefaultConfig { get; set; } = new MapConfig()
        {
            WeaponQuotas = new Dictionary<string, float>()
            {
                ["weapon_awp"] = 0.2f
            },
            WeaponLimits = new Dictionary<string, int>()
            {
                ["weapon_awp"] = 1
            }
        };

        [JsonPropertyName("DoTeamCheck")] public bool DoTeamCheck { get; set; } = true;

        [JsonPropertyName("AllowPickup")] public bool AllowPickup { get; set; } = false;

        [JsonPropertyName("RestrictWarmup")] public bool RestrictWarmup { get; set; } = true;

        [JsonPropertyName("VIPFlag")] public string VIPFlag { get; set; } = "@css/vip";

        [JsonPropertyName("MapConfigs")]
        public Dictionary<string, MapConfig> MapConfigs { get; set; } = new Dictionary<string, MapConfig>() {
            ["awp.*"] = new MapConfig()
            {
                WeaponQuotas = [],
                WeaponLimits = []
            }
        };
        
        [JsonPropertyName("ConfigVersion")] public new int Version { get; set; } = CONFIG_VERSION;
    }

    [MinimumApiVersion(361)]
    public class WeaponRestrictPlugin : BasePlugin, IPluginConfig<WeaponRestrictConfig>
    {
        public override string ModuleName => "WeaponRestrict";

        public override string ModuleVersion => "2.0.0";

        public override string ModuleAuthor => "jon, sapphyrus, FireBird & stefanx111";

        public override string ModuleDescription => "Restricts player weapons based on total player or teammate count.";

        public required WeaponRestrictConfig Config { get; set; }

        /// <summary>
        /// The current map config.
        /// </summary>
        public MapConfig CurrentMapConfig { get; set; } = new();
        
        /// <summary>
		/// Quick lookup of all restricted weapons (generated on Setup())
		/// </summary>
		public readonly HashSet<string> RestrictedWeapons = [];

        /// <summary>
		/// (UserID, Time) of last message sent to player
		/// </summary>
		public readonly Dictionary<int, float> LastPlayerMessage = [];

        public bool InWarmup = false;

        public CCSGameRules? gameRules;

#region CS# functions
        public void OnConfigParsed(WeaponRestrictConfig loadedConfig)
        {
            loadedConfig = ConfigManager.Load<WeaponRestrictConfig>("WeaponRestrict");

            if (loadedConfig.Version < WeaponRestrictConfig.CONFIG_VERSION)
            {
                Logger.LogInformation("Outdated config version. Please review the latest changes and update the config version to {NewCfgVersion}", WeaponRestrictConfig.CONFIG_VERSION);
            }

            // Format chat colors
            loadedConfig.MessagePrefix     = "\u1010" + FormatChatColors(loadedConfig.MessagePrefix);
            loadedConfig.DisabledMessage   = FormatChatColors(loadedConfig.DisabledMessage);
            loadedConfig.RestrictMessage   = FormatChatColors(loadedConfig.RestrictMessage);

            Config = loadedConfig;

            LoadMapConfig();
        }

        public override void Load(bool hotReload)
        {
            VirtualFunctions.CCSPlayer_ItemServices_CanAcquireFunc.Hook(OnWeaponCanAcquire, HookMode.Pre);

            RegisterListener<Listeners.OnMapStart>((mapName) =>
            {
                Server.NextWorldUpdate(Setup);

                LastPlayerMessage.Clear();
            });

            // Warmup state tracking
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

            // Clear last message cache on round start
            RegisterEventHandler<EventRoundPrestart>((@event, info) =>
            {
                LastPlayerMessage.Clear();

                return HookResult.Continue;
            });

            if (hotReload)
            {
                Server.NextWorldUpdate(Setup);
            }
        }

        public override void Unload(bool hotReload)
        {
            VirtualFunctions.CCSPlayer_ItemServices_CanAcquireFunc.Unhook(OnWeaponCanAcquire, HookMode.Pre);
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
                
                CurrentMapConfig.WeaponQuotas.Remove(weapon);
                CurrentMapConfig.WeaponLimits.Remove(weapon);

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
                        CurrentMapConfig.WeaponQuotas[weapon] = limit;

                        commandInfo.ReplyToCommand($"WeaponRestrict: Restricted \"{weapon}\" to \"{limit}\" per player(s) on team");
                    }
                    else
                    {
                        CurrentMapConfig.WeaponQuotas.Remove(weapon);

                        commandInfo.ReplyToCommand($"WeaponRestrict: Removed quota for \"{weapon}\"");
                    }

                    break;
                case "limit":
                    if (limit >= 0f)
                    {
                        int roundedLimit = (int)Math.Round(limit);
                        CurrentMapConfig.WeaponLimits[weapon] = roundedLimit;

                        commandInfo.ReplyToCommand($"WeaponRestrict: Restricted \"{weapon}\" to \"{roundedLimit}\" per team");
                    }
                    else
                    {
                        CurrentMapConfig.WeaponLimits.Remove(weapon);

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
#endregion

#region Static Helpers
        /// <summary>
        /// Gets the CCSGameRules entity
        /// </summary>
        /// <exception cref="Exception">Thrown when no CCSGameRules entity is found</exception>
        private static CCSGameRules GetGameRules()
        {
            foreach (CBaseEntity entity in Utilities.FindAllEntitiesByDesignerName<CBaseEntity>("cs_gamerules"))
            {
                return new CCSGameRules(entity.Handle);
            }

            throw new Exception("No CCSGameRules found!");
        }
        /// <summary>
        /// Removes item prefixes from items for chat messages
        /// </summary>
        private static string RemoveItemPrefix(string item)
        {
            if (item.StartsWith("weapon_"))
                return item[7..];
            if (item.StartsWith("item_"))
                return item[5..];

            return item;
        }

        /// <summary>
		/// Get all weapon names from <paramref name="config"/>
        /// </summary>
		private static List<string> GetConfigWeapons(MapConfig config)
		{
			List<string> weapons = [];
			weapons.AddRange(config.WeaponLimits.Keys);
			weapons.AddRange(config.WeaponQuotas.Keys);
			return [.. weapons.Distinct()];
		}

        /// <summary>
        /// Formats chat color codes from readable format to actual color codes
        /// </summary>
        private static string FormatChatColors(string s)
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

        private static CCSWeaponBaseVData GetWeaponVData(CEconItemView econItemView)
        {
            return VirtualFunctions.GetCSWeaponDataFromKeyFunc.Invoke(-1, econItemView.ItemDefinitionIndex.ToString()) ?? throw new Exception("Failed to get CCSWeaponBaseVData");
        }
#endregion

#region Instance Helpers

        /// <summary>
        /// Set up on map start (load config)
        /// </summary>
        private void Setup()
        {
            gameRules = GetGameRules();
            LoadMapConfig();

            // Get every restricted weapon and store it
            List<string> allRestrictedWeapons = GetConfigWeapons(Config.DefaultConfig);
			foreach (var mapConfigs in Config.MapConfigs.Values)
            {
                allRestrictedWeapons.AddRange(GetConfigWeapons(mapConfigs));
			}

            RestrictedWeapons.UnionWith(allRestrictedWeapons);
        }
        /// <summary>
        /// Counts the amount of <paramref name="designerName"/> weapons on <paramref name="players"/>
        /// </summary>
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
                {
                    continue;
                }

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

        /// <summary>
        /// Load the server map config or default if none found
        /// </summary>
        private void LoadMapConfig()
        {
            // Load map config if exists
            if (Server.MapName == null) return; // Null check on server boot

            MapConfig? mapConfig = null;
			// Exact match first (if false, try wildcard inside if statement, will null check again later)
			if (!Config.MapConfigs.TryGetValue(Server.MapName, out mapConfig))
            {
				// Wildcard match
				mapConfig = null;
                var cfgEnum = Config.MapConfigs.Where(x => Regex.IsMatch(Server.MapName, $"^{x.Key}$")).Select(x => x.Value);

                if (cfgEnum.Any())
                {
                    if (cfgEnum.Count() > 1)
                        Logger.LogWarning("Ambiguous wildcard search for {Mapname} in configs.", Server.MapName);

					// Load the found wildcard config
					mapConfig = cfgEnum.First();
                }
            }

            // Load default if no map config found
            bool isDefault = mapConfig == null;
			mapConfig ??= Config.DefaultConfig;

			// Set current map config
			CurrentMapConfig = mapConfig;

            var configType = isDefault ? "default " : "";
            var limits = string.Join(", ", CurrentMapConfig.WeaponLimits.Select(x => $"{x.Key}:{x.Value}"));
            var quotas = string.Join(", ", CurrentMapConfig.WeaponQuotas.Select(x => $"{x.Key}:{x.Value:F2}"));

            Logger.LogInformation("Loaded {ConfigType}config for {MapName}\n  Limits: {Limits}\n  Quotas: {Quotas}",
                configType, Server.MapName, limits, quotas);
        }

        /// <summary>
        /// Gets restriction reason and limit for <paramref name="weaponName"/> for <paramref name="client"/>
        /// </summary>
        private (RestrictReason reason, int limit) GetRestriction(string weaponName, CCSPlayerController client)
        {
            int limit = int.MaxValue;
            
            // Get every valid player that is currently connected
            IEnumerable<CCSPlayerController> players = Utilities.GetPlayers().Where(player =>
                player.IsValid // Unneccessary?
                && player.Connected == PlayerConnectedState.PlayerConnected
                && (!Config.DoTeamCheck || player.Team == client.Team)
                );

            // Check quota (if exists)
            if (CurrentMapConfig.WeaponQuotas.TryGetValue(weaponName, out float cfgQuota))
            {
                if (cfgQuota == 0f)
                    return (RestrictReason.Disabled, 0);

                // Calculate the current acceptable max value of weapons based on the weapon quota
                limit = Math.Min(limit,
                        cfgQuota > 0f ?
                        // pCount * quota (ex. 10 players and 0.2 quota = 2)
                        (int)(players.Count() * cfgQuota)
                        : 0);
            }

            // Check limit
            if (CurrentMapConfig.WeaponLimits.TryGetValue(weaponName, out int cfgLimit))
            {
                // Weapon is fully blocked
                if (cfgLimit == 0)
                    return (RestrictReason.Disabled, 0);
                
                if (cfgLimit <= limit)
                {
                    limit = cfgLimit;
                }
            }

            // Count amount of weapons on team, and check limit
            int count = CountWeaponsOnTeam(weaponName, players);
            if (count >= limit)
                return (RestrictReason.Limit, limit);

            // All checks passed, weapon is not restricted
            return (RestrictReason.NotRestricted, 0);
        }
#endregion

        public HookResult OnWeaponCanAcquire(DynamicHook hook)
        {
            // Warmup check
            if (!Config.RestrictWarmup && InWarmup)
                return HookResult.Continue;

            var acquireMethod = hook.GetParam<AcquireMethod>(2);
            
            if (gameRules != null)
            {
                if (Config.AllowPickup && gameRules.BuyTimeEnded && acquireMethod == AcquireMethod.PickUp)
                    return HookResult.Continue;
            }

            string weaponName = GetWeaponVData(hook.GetParam<CEconItemView>(1)).Name;

            // Weapon is not restricted
            if (!RestrictedWeapons.Contains(weaponName))
                return HookResult.Continue;

            CCSPlayerController client = hook.GetParam<CCSPlayer_ItemServices>(0).Pawn.Value!.Controller.Value!.As<CCSPlayerController>();

            // Client validity check
            if (client == null || !client.IsValid || !client.PawnIsAlive)
                return HookResult.Continue;

            // Player is VIP proof
            if (Config.VIPFlag != "" && AdminManager.PlayerHasPermissions(client, Config.VIPFlag))
                return HookResult.Continue;

            (RestrictReason reason, int limit) = GetRestriction(weaponName, client);

            // Weapon is not restricted
            if (reason == RestrictReason.NotRestricted)
                return HookResult.Continue;

            // Allow pickup if configured
            if (acquireMethod == AcquireMethod.PickUp && Config.AllowPickup)
                return HookResult.Continue;

            // If the player is picking up the weapon, check if we are in cooldown for printing messages
            if (acquireMethod == AcquireMethod.PickUp && Config.MessageCooldownSeconds > 0)
            {
                if (client.UserId != null)
                {
                    // Check if we already warned this player recently
                    if (LastPlayerMessage.TryGetValue((int)client.UserId, out float lastMessageTime)
                        && (Server.CurrentTime - lastMessageTime) < Config.MessageCooldownSeconds)
                    {
                        // Still in cooldown, do not print message
                        hook.SetReturn(AcquireResult.InvalidItem);
                        return HookResult.Stop;
                    }
                    else
                    {
                        // Not in cooldown, update the last message time
                        LastPlayerMessage[(int)client.UserId] = Server.CurrentTime;
                    }
                }
            }

            string msg = "";

            // Remove the item prefix for cleaner chat printing
            weaponName = RemoveItemPrefix(weaponName);

            switch (reason)
            {
                case RestrictReason.Disabled when !string.IsNullOrEmpty(Config.DisabledMessage):
                    msg = string.Format(Config.MessagePrefix + Config.DisabledMessage, weaponName);
                    break;
                case RestrictReason.Disabled:
                case RestrictReason.Limit:
                    msg = string.Format(Config.MessagePrefix + Config.RestrictMessage, weaponName, limit.ToString());
                    break;
            }

            if (msg != "")
            {
                Server.NextFrame(() =>
                {
                    client.PrintToChat(msg);
                });
            }

            hook.SetReturn(acquireMethod == AcquireMethod.PickUp ? AcquireResult.InvalidItem : AcquireResult.AlreadyOwned);
            return HookResult.Stop;
        }
    }
}