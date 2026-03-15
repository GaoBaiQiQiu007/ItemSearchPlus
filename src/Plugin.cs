using System.Reflection;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.DB;
using Terraria.ID;
using Microsoft.Xna.Framework;

[ApiVersion(2, 1)]
public class MainPlugin : TerrariaPlugin
{
    QueryResult? queryResult = null;

    private DateTime _lastInventoryScan = DateTime.MinValue;
    private readonly Dictionary<int, DateTime> _punishRecords = new();
    private bool _detectionEnabled = true;
    private bool _autoWebOnDetect = true;
    private bool _broadcastOnDetect = true;
    private int _abnormalStackMultiplier = 2;
    private int _actionLevel = 3;
    private TimeSpan _scanInterval = TimeSpan.FromSeconds(10);
    private TimeSpan _punishCooldown = TimeSpan.FromSeconds(30);
    private bool _abnormalLogBroadcastEnabled = true;
    private TimeSpan _abnormalLogBroadcastInterval = TimeSpan.FromHours(1);
    private DateTime _lastAbnormalLogBroadcast = DateTime.UtcNow;
    private HashSet<int> _illegalItemIds = new()
    {
        ItemID.Zenith,
        ItemID.LastPrism,
        ItemID.CoinGun,
        ItemID.LunarFlareBook
    };
    private string ConfigPath => Path.Combine(TShock.SavePath, "itemsearchplus.guard.json");

    Dictionary<int, string> data = new Dictionary<int, string>();
    public override string Name => "物品查找";

    public override Version Version => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0, 0);

    public override string Author => "ak改版";

    public override string Description => "显示拥有指定物品的玩家或箱子";

    public MainPlugin(Main game)
        : base(game)
    {
    }

    public override void Initialize()
    {
        Commands.ChatCommands.Add(new Command("itemsearch.cmd", ItemSearchCmd, "searchitem", "si", "查找物品"));
        Commands.ChatCommands.Add(new Command("itemsearch.chest", ChestSearchCmd, "searchchest", "sc", "查找箱子"));
        Commands.ChatCommands.Add(new Command("itemsearch.chesttp", TpSearchCmd, "tpchest", "tpc", "传送箱子"));
        Commands.ChatCommands.Add(new Command("itemsearch.chestinfo", InfoSearchCmd, "chestinfo", "ci", "箱子信息"));
        Commands.ChatCommands.Add(new Command("itemsearch.tpall", TpAllPlayer, "tpall", "传送所有人"));
        Commands.ChatCommands.Add(new Command("itemsearch.tpall", TpAllChest, "tpallchest", "tpallc", "传送所有箱子"));
        Commands.ChatCommands.Add(new Command("itemsearch.rci", RemoveItemChest, "removechestitem", "rci", "删除箱子物品"));
        Commands.ChatCommands.Add(new Command("itemsearch.ri", RemoveItem, "removeitem", "ri", "删除物品"));
        Commands.ChatCommands.Add(new Command("itemsearch.guard", GuardControlCmd, "itemguard", "ig", "检测控制"));

        EnsureEvidenceTable();
        LoadGuardConfig();
        ServerApi.Hooks.GamePostUpdate.Register(this, OnGamePostUpdate);
    }

    private void OnGamePostUpdate(EventArgs args)
    {
        TryBroadcastAbnormalLogSummary();

        if (!_detectionEnabled)
        {
            return;
        }

        if (DateTime.UtcNow - _lastInventoryScan < _scanInterval)
        {
            return;
        }

        _lastInventoryScan = DateTime.UtcNow;

        foreach (var player in TShock.Players)
        {
            if (player == null || !player.Active || player.TPlayer == null)
            {
                continue;
            }

            foreach (var entry in GetAllItems(player.TPlayer))
            {
                var item = entry.Item;
                if (item == null || item.type <= 0)
                {
                    continue;
                }

                if (IsIllegalItem(item))
                {
                    PunishPlayer(player, item, entry.Slot, "检测到非法物品");
                    break;
                }

                if (IsAbnormalStack(item, _abnormalStackMultiplier))
                {
                    PunishPlayer(player, item, entry.Slot, "检测到异常数量");
                    break;
                }
            }
        }
    }

    private static IEnumerable<(Item Item, string Slot)> GetAllItems(Player player)
    {
        if (player.selectedItem >= 0 && player.selectedItem < player.inventory.Length)
        {
            yield return (player.inventory[player.selectedItem], "held");
        }

        for (int i = 0; i < player.inventory.Length; i++) yield return (player.inventory[i], $"inventory[{i}]");
        for (int i = 0; i < player.armor.Length; i++) yield return (player.armor[i], $"armor[{i}]");
        for (int i = 0; i < player.dye.Length; i++) yield return (player.dye[i], $"dye[{i}]");
        for (int i = 0; i < player.miscEquips.Length; i++) yield return (player.miscEquips[i], $"miscEquips[{i}]");
        for (int i = 0; i < player.miscDyes.Length; i++) yield return (player.miscDyes[i], $"miscDyes[{i}]");
        for (int i = 0; i < player.bank.item.Length; i++) yield return (player.bank.item[i], $"bank1[{i}]");
        for (int i = 0; i < player.bank2.item.Length; i++) yield return (player.bank2.item[i], $"bank2[{i}]");
        for (int i = 0; i < player.bank3.item.Length; i++) yield return (player.bank3.item[i], $"bank3[{i}]");
        for (int i = 0; i < player.bank4.item.Length; i++) yield return (player.bank4.item[i], $"bank4[{i}]");
        for (int i = 0; i < player.Loadouts[0].Armor.Length; i++) yield return (player.Loadouts[0].Armor[i], $"loadout1.armor[{i}]");
        for (int i = 0; i < player.Loadouts[1].Armor.Length; i++) yield return (player.Loadouts[1].Armor[i], $"loadout2.armor[{i}]");
        for (int i = 0; i < player.Loadouts[2].Armor.Length; i++) yield return (player.Loadouts[2].Armor[i], $"loadout3.armor[{i}]");
        for (int i = 0; i < player.Loadouts[0].Dye.Length; i++) yield return (player.Loadouts[0].Dye[i], $"loadout1.dye[{i}]");
        for (int i = 0; i < player.Loadouts[1].Dye.Length; i++) yield return (player.Loadouts[1].Dye[i], $"loadout2.dye[{i}]");
        for (int i = 0; i < player.Loadouts[2].Dye.Length; i++) yield return (player.Loadouts[2].Dye[i], $"loadout3.dye[{i}]");
        yield return (player.trashItem, "trash");
    }

    private static bool IsAbnormalStack(Item item, int multiplier)
    {
        if (item.stack <= 0)
        {
            return false;
        }

        var allowedMax = Math.Max(1, item.maxStack);
        return item.stack > allowedMax * Math.Max(1, multiplier);
    }

    private bool IsIllegalItem(Item item)
    {
        return _illegalItemIds.Contains(item.type);
    }

    private void PunishPlayer(TSPlayer player, Item targetItem, string slot, string reason)
    {
        if (_punishRecords.TryGetValue(player.Index, out var lastPunishAt) && DateTime.UtcNow - lastPunishAt < _punishCooldown)
        {
            return;
        }

        _punishRecords[player.Index] = DateTime.UtcNow;

        int itemId = targetItem.type;
        string itemName = targetItem.Name;
        int itemStack = targetItem.stack;
        string action = "log";

        if (_actionLevel >= 2)
        {
            targetItem.SetDefaults(0);
            action = "remove";
        }

        if (_actionLevel >= 3)
        {
            if (_autoWebOnDetect)
            {
                player.SetBuff(BuffID.Webbed, 60 * 60 * 60);
            }

            if (_broadcastOnDetect)
            {
                TShock.Utils.Broadcast($"[物品查找] 检测到玩家[{player.Name}]持有异常物品（{reason}），已触发自动处置。", Color.OrangeRed);
            }

            action = _autoWebOnDetect || _broadcastOnDetect ? "web_broadcast" : "level3_noop";
        }

        SaveEvidence(player, itemId, itemName, itemStack, slot, reason, action);
        TShock.Log.Warn($"[ItemSearchPlus] 玩家 {player.Name} 触发检测: {reason}, 物品={itemId}, 数量={itemStack}, 槽位={slot}, action={action}");
    }


    private void TryBroadcastAbnormalLogSummary()
    {
        if (!_abnormalLogBroadcastEnabled)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (now - _lastAbnormalLogBroadcast < _abnormalLogBroadcastInterval)
        {
            return;
        }

        QueryResult? result = null;
        try
        {
            result = TShock.DB.QueryReader("SELECT COUNT(1) FROM item_guard_logs WHERE reason LIKE @0 AND time_utc >= @1", "%异常%", _lastAbnormalLogBroadcast.ToString("O"));
            if (result.Reader.Read())
            {
                var count = Convert.ToInt32(result.Reader.GetValue(0));
                if (count > 0)
                {
                    TShock.Utils.Broadcast($"[物品查找] 最近 {Math.Max(1, (int)_abnormalLogBroadcastInterval.TotalHours)} 小时新增异常日志 {count} 条，请管理员及时使用 /ig logs 查看。", Color.Gold);
                }
            }
        }
        catch (Exception ex)
        {
            TShock.Log.ConsoleError($"[ItemSearchPlus] 异常日志定时通报失败: {ex}");
        }
        finally
        {
            result?.Dispose();
            _lastAbnormalLogBroadcast = now;
        }
    }

    private void ViewGuardLogsCmd(CommandArgs args)
    {
        if (args.Parameters.Count < 3)
        {
            args.Player.SendInfoMessage("用法:/ig logs <all|玩家名> <all|held|trash|inventory|armor|dye|misc|bank|loadout> [条数]");
            return;
        }

        var playerFilter = args.Parameters[1];
        var slotType = args.Parameters[2].ToLowerInvariant();
        var limit = 20;
        if (args.Parameters.Count >= 4 && (!int.TryParse(args.Parameters[3], out limit) || limit < 1 || limit > 100))
        {
            args.Player.SendErrorMessage("条数范围: 1-100");
            return;
        }

        var slotPattern = GetSlotPattern(slotType);
        if (slotPattern == null)
        {
            args.Player.SendErrorMessage("无效槽位类型。可用: all|held|trash|inventory|armor|dye|misc|bank|loadout");
            return;
        }

        QueryResult? result = null;
        try
        {
            var sql = "SELECT time_utc, player_name, item_name, stack, slot, reason, action FROM item_guard_logs WHERE 1=1";
            var parameters = new List<object>();

            if (!playerFilter.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                sql += " AND player_name = @" + parameters.Count;
                parameters.Add(playerFilter);
            }

            if (!slotPattern.Equals("%"))
            {
                sql += " AND slot LIKE @" + parameters.Count;
                parameters.Add(slotPattern);
            }

            sql += " ORDER BY id DESC LIMIT @" + parameters.Count;
            parameters.Add(limit);

            result = TShock.DB.QueryReader(sql, parameters.ToArray());
            var lines = new List<string>();
            while (result.Reader.Read())
            {
                var time = result.Reader.GetString(0);
                var player = result.Reader.GetString(1);
                var itemName = result.Reader.IsDBNull(2) ? "未知物品" : result.Reader.GetString(2);
                var stack = result.Reader.GetInt32(3);
                var slot = result.Reader.IsDBNull(4) ? "unknown" : result.Reader.GetString(4);
                var reason = result.Reader.IsDBNull(5) ? "-" : result.Reader.GetString(5);
                var action = result.Reader.IsDBNull(6) ? "-" : result.Reader.GetString(6);
                lines.Add($"[{time}] {player} | {itemName}x{stack} | {slot} | {reason} | {action}");
            }

            if (lines.Count == 0)
            {
                args.Player.SendInfoMessage("未找到符合条件的异常日志。");
                return;
            }

            args.Player.SendSuccessMessage($"异常日志查询结果({lines.Count}条):");
            args.Player.SendInfoMessage(string.Join("\n", lines));
        }
        finally
        {
            result?.Dispose();
        }
    }

    private static string? GetSlotPattern(string slotType)
    {
        return slotType switch
        {
            "all" => "%",
            "held" => "held",
            "trash" => "trash",
            "inventory" => "inventory%",
            "armor" => "armor%",
            "dye" => "dye%",
            "misc" => "misc%",
            "bank" => "bank%",
            "loadout" => "loadout%",
            _ => null
        };
    }

    private void EnsureEvidenceTable()
    {
        TShock.DB.Query(@"CREATE TABLE IF NOT EXISTS item_guard_logs (
id INTEGER PRIMARY KEY AUTOINCREMENT,
time_utc TEXT NOT NULL,
player_name TEXT NOT NULL,
account_id INTEGER,
item_id INTEGER NOT NULL,
item_name TEXT,
stack INTEGER NOT NULL,
slot TEXT,
reason TEXT,
action TEXT
)");
    }

    private void SaveEvidence(TSPlayer player, int itemId, string itemName, int itemStack, string slot, string reason, string action)
    {
        var accountId = player.Account?.ID ?? -1;
        TShock.DB.Query("INSERT INTO item_guard_logs (time_utc, player_name, account_id, item_id, item_name, stack, slot, reason, action) VALUES (@0, @1, @2, @3, @4, @5, @6, @7, @8)",
            DateTime.UtcNow.ToString("O"), player.Name, accountId, itemId, itemName, itemStack, slot, reason, action);
    }

    private void LoadGuardConfig()
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                SaveGuardConfig();
                return;
            }

            var text = File.ReadAllText(ConfigPath);
            var cfg = System.Text.Json.JsonSerializer.Deserialize<GuardConfig>(text);
            if (cfg == null)
            {
                return;
            }

            _detectionEnabled = cfg.DetectionEnabled;
            _autoWebOnDetect = cfg.AutoWebOnDetect;
            _broadcastOnDetect = cfg.BroadcastOnDetect;
            _abnormalStackMultiplier = Math.Clamp(cfg.AbnormalStackMultiplier, 2, 20);
            _actionLevel = Math.Clamp(cfg.ActionLevel, 1, 3);
            _scanInterval = TimeSpan.FromSeconds(Math.Clamp(cfg.ScanIntervalSeconds, 1, 300));
            _punishCooldown = TimeSpan.FromSeconds(Math.Clamp(cfg.PunishCooldownSeconds, 1, 600));
            _abnormalLogBroadcastEnabled = cfg.AbnormalLogBroadcastEnabled;
            _abnormalLogBroadcastInterval = TimeSpan.FromHours(Math.Clamp(cfg.AbnormalLogBroadcastHours, 1, 24));
            _illegalItemIds = new HashSet<int>(cfg.IllegalItemIds?.Where(i => i > 0) ?? Array.Empty<int>());
            if (_illegalItemIds.Count == 0)
            {
                _illegalItemIds = new HashSet<int> { ItemID.Zenith, ItemID.LastPrism, ItemID.CoinGun, ItemID.LunarFlareBook };
            }
        }
        catch (Exception ex)
        {
            TShock.Log.ConsoleError($"[ItemSearchPlus] 读取配置失败: {ex}");
        }
    }

    private void SaveGuardConfig()
    {
        var cfg = new GuardConfig
        {
            DetectionEnabled = _detectionEnabled,
            AutoWebOnDetect = _autoWebOnDetect,
            BroadcastOnDetect = _broadcastOnDetect,
            AbnormalStackMultiplier = _abnormalStackMultiplier,
            ActionLevel = _actionLevel,
            ScanIntervalSeconds = (int)_scanInterval.TotalSeconds,
            PunishCooldownSeconds = (int)_punishCooldown.TotalSeconds,
            AbnormalLogBroadcastEnabled = _abnormalLogBroadcastEnabled,
            AbnormalLogBroadcastHours = (int)_abnormalLogBroadcastInterval.TotalHours,
            IllegalItemIds = _illegalItemIds.OrderBy(i => i).ToList()
        };

        var text = System.Text.Json.JsonSerializer.Serialize(cfg, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true
        });
        File.WriteAllText(ConfigPath, text);
    }

    private void SendGuardHelp(TSPlayer player)
    {
        player.SendInfoMessage("[物品守卫] 指令帮助");
        player.SendInfoMessage("/ig help - 显示本帮助");
        player.SendInfoMessage("/ig status - 查看当前状态");
        player.SendInfoMessage("/ig enable|disable - 开启/关闭检测");
        player.SendInfoMessage("/ig web <on|off> - 命中后是否自动网住");
        player.SendInfoMessage("/ig broadcast <on|off> - 命中后是否全服通报");
        player.SendInfoMessage("/ig multiplier <2-20> - 异常堆叠倍率阈值");
        player.SendInfoMessage("/ig level <1-3> - 处置等级(1记录/2清除/3清除+网住通报)");
        player.SendInfoMessage("/ig ablog <on|off> - 异常日志定时通报开关");
        player.SendInfoMessage("/ig abloghours <1-24> - 异常日志通报周期(小时)");
        player.SendInfoMessage("/ig logs <all|玩家名> <all|held|trash|inventory|armor|dye|misc|bank|loadout> [1-100]");
        player.SendInfoMessage("/ig save|reload - 保存/重载配置");
    }

    private void GuardControlCmd(CommandArgs args)
    {
        if (args.Parameters.Count == 0)
        {
            SendGuardHelp(args.Player);
            return;
        }

        var action = args.Parameters[0].ToLowerInvariant();
        switch (action)
        {
            case "help":
                SendGuardHelp(args.Player);
                return;
            case "status":
                args.Player.SendInfoMessage($"检测状态:{(_detectionEnabled ? "开启" : "关闭")}, 自动网住:{(_autoWebOnDetect ? "开启" : "关闭")}, 全服通报:{(_broadcastOnDetect ? "开启" : "关闭")}, 异常倍率:{_abnormalStackMultiplier}x, 处置等级:{_actionLevel}, 扫描间隔:{_scanInterval.TotalSeconds}s, 异常日志定时通报:{(_abnormalLogBroadcastEnabled ? "开启" : "关闭")}, 周期:{_abnormalLogBroadcastInterval.TotalHours}h");
                return;
            case "enable":
                _detectionEnabled = true;
                args.Player.SendSuccessMessage("非法物品检测已开启。");
                return;
            case "disable":
                _detectionEnabled = false;
                args.Player.SendWarningMessage("非法物品检测已关闭。");
                return;
            case "web":
                if (args.Parameters.Count < 2 || (args.Parameters[1] != "on" && args.Parameters[1] != "off"))
                {
                    args.Player.SendInfoMessage("用法:/ig web <on|off>");
                    return;
                }

                _autoWebOnDetect = args.Parameters[1] == "on";
                args.Player.SendSuccessMessage($"自动网住已{(_autoWebOnDetect ? "开启" : "关闭")}");
                return;
            case "broadcast":
                if (args.Parameters.Count < 2 || (args.Parameters[1] != "on" && args.Parameters[1] != "off"))
                {
                    args.Player.SendInfoMessage("用法:/ig broadcast <on|off>");
                    return;
                }

                _broadcastOnDetect = args.Parameters[1] == "on";
                args.Player.SendSuccessMessage($"全服通报已{(_broadcastOnDetect ? "开启" : "关闭")}");
                return;
            case "multiplier":
                if (args.Parameters.Count < 2 || !int.TryParse(args.Parameters[1], out var multiplier) || multiplier < 2 || multiplier > 20)
                {
                    args.Player.SendInfoMessage("用法:/ig multiplier <2-20>");
                    return;
                }

                _abnormalStackMultiplier = multiplier;
                args.Player.SendSuccessMessage($"异常数量判定倍率已调整为 {_abnormalStackMultiplier}x。");
                return;
            case "level":
                if (args.Parameters.Count < 2 || !int.TryParse(args.Parameters[1], out var level) || level < 1 || level > 3)
                {
                    args.Player.SendInfoMessage("用法:/ig level <1-3> (1=只记录,2=记录+清除,3=记录+清除+网住/通报)");
                    return;
                }

                _actionLevel = level;
                args.Player.SendSuccessMessage($"处置等级已调整为 {_actionLevel}");
                return;
            case "ablog":
                if (args.Parameters.Count < 2 || (args.Parameters[1] != "on" && args.Parameters[1] != "off"))
                {
                    args.Player.SendInfoMessage("用法:/ig ablog <on|off>");
                    return;
                }

                _abnormalLogBroadcastEnabled = args.Parameters[1] == "on";
                args.Player.SendSuccessMessage($"异常日志定时通报已{(_abnormalLogBroadcastEnabled ? "开启" : "关闭")}");
                return;
            case "abloghours":
                if (args.Parameters.Count < 2 || !int.TryParse(args.Parameters[1], out var hours) || hours < 1 || hours > 24)
                {
                    args.Player.SendInfoMessage("用法:/ig abloghours <1-24>");
                    return;
                }

                _abnormalLogBroadcastInterval = TimeSpan.FromHours(hours);
                args.Player.SendSuccessMessage($"异常日志通报周期已调整为 {hours} 小时。");
                return;
            case "logs":
                ViewGuardLogsCmd(args);
                return;
            case "save":
                SaveGuardConfig();
                args.Player.SendSuccessMessage($"配置已保存到: {ConfigPath}");
                return;
            case "reload":
                LoadGuardConfig();
                args.Player.SendSuccessMessage("配置已重新加载。");
                return;
            default:
                args.Player.SendErrorMessage("未知子命令，请使用 /ig help 查看帮助。");
                return;
        }
    }

    private void RemoveItem(CommandArgs args)
    {
        data.Clear();
        if (args.Parameters.Count != 2)
        {
            args.Player.SendInfoMessage("用法:/ri <玩家名> <物品名/ID>");
            return;
        }
        var acc = TShock.UserAccounts.GetUserAccountByName(args.Parameters[0]);
        if (acc == null)
        {
            args.Player.SendErrorMessage($"找不到名字为{args.Parameters[0]}的玩家!");
            return;
        }

        List<Item> itemByIdOrName = TShock.Utils.GetItemByIdOrName(args.Parameters[1]);
        if (itemByIdOrName.Count > 1)
        {
            args.Player.SendMultipleMatchError(from i in itemByIdOrName
                                               select (args.Player.RealPlayer ? string.Format("[i:{0}]", i.type) : "") + i.Name);
            return;
        }
        if (itemByIdOrName.Count == 0)
        {
            args.Player.SendErrorMessage("指定的物品无效");
            return;
        }
        int item = itemByIdOrName[0].type;
        TSPlayer player = new(-1);
        PlayerData? playerdata;
        int count = 0;
        if (TSPlayer.FindByNameOrID(acc.Name).FirstOrDefault() != null)
        {
            //Item[] armor;
            //Item[] dye;
            //Item[] miscEquips;
            //Item[] miscDyes;
            var plr = TSPlayer.FindByNameOrID(acc.Name).FirstOrDefault()!;
            for (int i = 0; i < plr.TPlayer.inventory.Length; i++)
            {
                if (plr.TPlayer.inventory[i].type == item)
                {
                    count += plr.TPlayer.inventory[i].stack;
                    plr.TPlayer.inventory[i].SetDefaults(0);
                    NetMessage.SendData(5, -1, -1, null, plr.Index, i);

                }
            }
            for (int i = 0; i < plr.TPlayer.armor.Length; i++)
            {
                if (plr.TPlayer.armor[i].type == item)
                {
                    count += plr.TPlayer.armor[i].stack;
                    plr.TPlayer.armor[i].SetDefaults(0);
                    NetMessage.SendData(5, -1, -1, null, plr.Index, PlayerItemSlotID.Armor0 + i);

                }
            }
            for (int i = 0; i < plr.TPlayer.dye.Length; i++)
            {
                if (plr.TPlayer.dye[i].type == item)
                {
                    count += plr.TPlayer.dye[i].stack;
                    plr.TPlayer.dye[i].SetDefaults(0);
                    NetMessage.SendData(5, -1, -1, null, plr.Index, PlayerItemSlotID.Dye0 + i);

                }
            }
            for (int i = 0; i < plr.TPlayer.miscEquips.Length; i++)
            {
                if (plr.TPlayer.miscEquips[i].type == item)
                {
                    count += plr.TPlayer.miscEquips[i].stack;
                    plr.TPlayer.miscEquips[i].SetDefaults(0);
                    NetMessage.SendData(5, -1, -1, null, plr.Index, PlayerItemSlotID.Misc0 + i);

                }
            }
            for (int i = 0; i < plr.TPlayer.miscDyes.Length; i++)
            {
                if (plr.TPlayer.miscDyes[i].type == item)
                {
                    count += plr.TPlayer.miscDyes[i].stack;
                    plr.TPlayer.miscDyes[i].SetDefaults(0);
                    NetMessage.SendData(5, -1, -1, null, plr.Index, PlayerItemSlotID.MiscDye0 + i);

                }
            }
            for (int i = 0; i < plr.TPlayer.bank.item.Length; i++)
            {
                if (plr.TPlayer.bank.item[i].type == item)
                {
                    count += plr.TPlayer.bank.item[i].stack;
                    plr.TPlayer.bank.item[i].SetDefaults(0);
                    NetMessage.SendData(5, -1, -1, null, plr.Index, PlayerItemSlotID.Bank1_0 + i);

                }
            }
            for (int i = 0; i < plr.TPlayer.bank2.item.Length; i++)
            {
                if (plr.TPlayer.bank2.item[i].type == item)
                {
                    count += plr.TPlayer.bank2.item[i].stack;
                    plr.TPlayer.bank2.item[i].SetDefaults(0);
                    NetMessage.SendData(5, -1, -1, null, plr.Index, PlayerItemSlotID.Bank2_0 + i);

                }
            }
            for (int i = 0; i < plr.TPlayer.bank3.item.Length; i++)
            {
                if (plr.TPlayer.bank3.item[i].type == item)
                {
                    count += plr.TPlayer.bank3.item[i].stack;
                    plr.TPlayer.bank3.item[i].SetDefaults(0);
                    NetMessage.SendData(5, -1, -1, null, plr.Index, PlayerItemSlotID.Bank3_0 + i);

                }
            }
            for (int i = 0; i < plr.TPlayer.bank4.item.Length; i++)
            {
                if (plr.TPlayer.bank4.item[i].type == item)
                {
                    count += plr.TPlayer.bank4.item[i].stack;
                    plr.TPlayer.bank4.item[i].SetDefaults(0);
                    NetMessage.SendData(5, -1, -1, null, plr.Index, PlayerItemSlotID.Bank4_0 + i);

                }
            }
            for (int i = 0; i < plr.TPlayer.Loadouts[0].Armor.Length; i++)
            {
                if (plr.TPlayer.Loadouts[0].Armor[i].type == item)
                {
                    count += plr.TPlayer.Loadouts[0].Armor[i].stack;
                    plr.TPlayer.Loadouts[0].Armor[i].SetDefaults(0);
                    NetMessage.SendData(5, -1, -1, null, plr.Index, PlayerItemSlotID.Loadout1_Armor_0 + i);

                }
            }
            for (int i = 0; i < plr.TPlayer.Loadouts[1].Armor.Length; i++)
            {
                if (plr.TPlayer.Loadouts[1].Armor[i].type == item)
                {
                    count += plr.TPlayer.Loadouts[1].Armor[i].stack;
                    plr.TPlayer.Loadouts[1].Armor[i].SetDefaults(0);
                    NetMessage.SendData(5, -1, -1, null, plr.Index, PlayerItemSlotID.Loadout2_Armor_0 + i);

                }
            }
            for (int i = 0; i < plr.TPlayer.Loadouts[2].Armor.Length; i++)
            {
                if (plr.TPlayer.Loadouts[2].Armor[i].type == item)
                {
                    count += plr.TPlayer.Loadouts[2].Armor[i].stack;
                    plr.TPlayer.Loadouts[2].Armor[i].SetDefaults(0);
                    NetMessage.SendData(5, -1, -1, null, plr.Index, PlayerItemSlotID.Loadout3_Armor_0 + i);

                }
            }
            for (int i = 0; i < plr.TPlayer.Loadouts[0].Dye.Length; i++)
            {
                if (plr.TPlayer.Loadouts[0].Dye[i].type == item)
                {
                    count += plr.TPlayer.Loadouts[0].Dye[i].stack;

                    plr.TPlayer.Loadouts[0].Dye[i].SetDefaults(0);
                    NetMessage.SendData(5, -1, -1, null, plr.Index, PlayerItemSlotID.Loadout1_Dye_0 + i);

                }
            }
            for (int i = 0; i < plr.TPlayer.Loadouts[1].Dye.Length; i++)
            {
                if (plr.TPlayer.Loadouts[1].Dye[i].type == item)
                {
                    count += plr.TPlayer.Loadouts[1].Dye[i].stack;
                    plr.TPlayer.Loadouts[1].Dye[i].SetDefaults(0);
                    NetMessage.SendData(5, -1, -1, null, plr.Index, PlayerItemSlotID.Loadout2_Dye_0 + i);

                }
            }
            for (int i = 0; i < plr.TPlayer.Loadouts[2].Dye.Length; i++)
            {
                if (plr.TPlayer.Loadouts[2].Dye[i].type == item)
                {
                    count += plr.TPlayer.Loadouts[2].Dye[i].stack;
                    plr.TPlayer.Loadouts[2].Dye[i].SetDefaults(0);
                    NetMessage.SendData(5, -1, -1, null, plr.Index, PlayerItemSlotID.Loadout3_Dye_0 + i);

                }
            }
            if (plr.TPlayer.trashItem.type == item)
            {
                count += plr.TPlayer.trashItem.stack;
                plr.TPlayer.trashItem.SetDefaults(0);
                NetMessage.SendData(5, -1, -1, null, plr.Index, PlayerItemSlotID.TrashItem);
            }
            //Item[] array = player.TPlayer.inventory; 1
            //Item[] armor = player.TPlayer.armor;1
            //Item[] dye = player.TPlayer.dye;1
            //Item[] miscEquips = player.TPlayer.miscEquips;1
            //Item[] miscDyes = player.TPlayer.miscDyes;1
            //Item[] item = player.TPlayer.bank.item;1
            //Item[] item2 = player.TPlayer.bank2.item;1
            //Item[] item3 = player.TPlayer.bank3.item;1
            //Item[] item4 = player.TPlayer.bank4.item;1
            //Item trashItem = player.TPlayer.trashItem;
            //Item[] armor2 = player.TPlayer.Loadouts[0].Armor;
            //Item[] dye2 = player.TPlayer.Loadouts[0].Dye;
            //Item[] armor3 = player.TPlayer.Loadouts[1].Armor;
            //Item[] dye3 = player.TPlayer.Loadouts[1].Dye;
            //Item[] armor4 = player.TPlayer.Loadouts[2].Armor;
            //Item[] dye4 = player.TPlayer.Loadouts[2].Dye;
        }
        else
        {
            try
            {
                playerdata = TShock.CharacterDB.GetPlayerData(player, acc.ID);

                //改成for()循环
                for (int i = 0; i < playerdata.inventory.Length; i++)
                {
                    if (playerdata.inventory[i].netID == item)
                    {
                        count += playerdata.inventory[i].Stack;
                        playerdata.inventory[i] = new(0, 0, 0);
                    }
                }

                //更新数据库背包
                TShock.CharacterDB.database.Query("UPDATE tsCharacter SET Inventory = @0 WHERE Account = @1", string.Join("~", playerdata.inventory), acc.ID);

            }
            catch (Exception ex)
            {
                TShock.Log.ConsoleError(ex.ToString());
                return;
            }
        }
        args.Player.SendSuccessMessage($"已移除玩家{acc.Name}的{TShock.Utils.ItemTag(new() { type = item, stack = 1, prefix = 0 })}X{count}");
        return;


    }

    private void RemoveItemChest(CommandArgs args)
    {
        if (args.Parameters.Count != 2)
        {
            args.Player.SendInfoMessage("用法:/rci <箱子ID> <物品名/ID>");
            return;
        }
        try
        {
            if (Main.chest[int.Parse(args.Parameters[0])] == null)
            {
                args.Player.SendErrorMessage($"找不到ID为{args.Parameters[0]}的箱子!");
                return;
            }

        }
        catch
        {
            args.Player.SendErrorMessage($"找不到ID为{args.Parameters[0]}的箱子!");
        }
        List<Item> itemByIdOrName = TShock.Utils.GetItemByIdOrName(args.Parameters[1]);
        if (itemByIdOrName.Count > 1)
        {
            args.Player.SendMultipleMatchError(from i in itemByIdOrName
                                               select (args.Player.RealPlayer ? string.Format("[i:{0}]", i.type) : "") + i.Name);
            return;
        }
        if (itemByIdOrName.Count == 0)
        {
            args.Player.SendErrorMessage("指定的物品无效");
            return;
        }
        int item = itemByIdOrName[0].type;
        for (int i = 0; i < Main.chest[int.Parse(args.Parameters[0])].item.Length; i++)
        {
            if (Main.chest[int.Parse(args.Parameters[0])].item[i] != null && Main.chest[int.Parse(args.Parameters[0])].item[i].type == item)
            {
                Main.chest[int.Parse(args.Parameters[0])].item[i] = new Item { type = 0 };
            }
        }
        var chest = Main.chest[int.Parse(args.Parameters[0])];
        var itemStr = "";
        foreach (var i in chest.item)
        {
            if (i.type == 0)
            {
                continue;
            }
            itemStr += TShock.Utils.ItemTag(i);
        }
        if (string.IsNullOrEmpty(itemStr))
        {
            itemStr = "空箱子";
        }
        args.Player.SendSuccessMessage($"箱子中的所有{TShock.Utils.ItemTag(new() { type = item, stack = 1, prefix = 0 })}已被移除\n" +
            $"箱子ID:{args.Parameters[0]}\n" +
            $"坐标:({chest.x},{chest.y})\n" +
            $"名字:{(string.IsNullOrEmpty(chest.name) ? "无名箱子" : chest.name)}\n" +
            $"NPC商店:{(chest.bankChest ? "是" : "否")}\n" +
            $"物品:{itemStr}");

    }

    private void TpAllPlayer(CommandArgs args)
    {
        args.Player.SendInfoMessage("传送开始!");

        Task.Run(delegate
        {
            foreach (var i in TShock.Players)
            {
                if (i != null && args.Player.Active)
                    args.Player.Teleport(i.X, i.Y);
                Thread.Sleep(1000);
            }

        });

    }
    private void TpAllChest(CommandArgs args)
    {
        args.Player.SendInfoMessage("传送开始!");

        Task.Run(delegate
        {
            foreach (var i in Main.chest)
            {
                if (i != null && args.Player.Active)
                    args.Player.Teleport(i.x * 16, i.y * 16 + 2);
                Thread.Sleep(300);
            }

        });

    }

    private void InfoSearchCmd(CommandArgs args)
    {
        if (args.Parameters.Count == 0)
        {
            args.Player.SendInfoMessage("用法:/ci <箱子ID>");
            return;
        }
        try
        {
            if (Main.chest[int.Parse(args.Parameters[0])] == null)
            {
                args.Player.SendErrorMessage($"找不到ID为{args.Parameters[0]}的箱子!");
                return;
            }

        }
        catch
        {
            args.Player.SendErrorMessage($"找不到ID为{args.Parameters[0]}的箱子!");
        }
        var chest = Main.chest[int.Parse(args.Parameters[0])];
        var itemStr = "";
        foreach (var i in chest.item)
        {
            if (i.type == 0)
            {
                continue;
            }
            itemStr += TShock.Utils.ItemTag(i);
        }
        if (string.IsNullOrEmpty(itemStr))
        {
            itemStr = "空箱子";
        }
        args.Player.SendSuccessMessage($"箱子的信息\n" +
            $"箱子ID:{args.Parameters[0]}\n" +
            $"坐标:({chest.x},{chest.y})\n" +
            $"名字:{(string.IsNullOrEmpty(chest.name) ? "无名箱子" : chest.name)}\n" +
            $"NPC商店:{(chest.bankChest ? "是" : "否")}\n" +
            $"物品:{itemStr}");
    }


    private void TpSearchCmd(CommandArgs args)
    {
        if (!args.Player.RealPlayer)
        {
            args.Player.SendErrorMessage("仅限游戏内使用");
            return;
        }
        if (args.Parameters.Count == 0)
        {
            args.Player.SendInfoMessage("用法:/tpc <箱子ID>");
            return;
        }
        try
        {
            if (Main.chest[int.Parse(args.Parameters[0])] == null)
            {
                args.Player.SendErrorMessage($"找不到ID为{args.Parameters[0]}的箱子!");
                return;
            }

        }
        catch
        {
            args.Player.SendErrorMessage($"找不到ID为{args.Parameters[0]}的箱子!");
        }

        args.Player.Teleport(Main.chest[int.Parse(args.Parameters[0])].x * 16, Main.chest[int.Parse(args.Parameters[0])].y * 16 + 2);
        args.Player.SendSuccessMessage($"已将你传送至箱子{args.Parameters[0]}");


    }

    private void ChestSearchCmd(CommandArgs args)
    {
        if (args.Parameters.Count == 0)
        {
            args.Player.SendInfoMessage("用法:/sc <物品ID/名称>");
            return;
        }
        List<Item> itemByIdOrName = TShock.Utils.GetItemByIdOrName(args.Parameters[0]);
        if (itemByIdOrName.Count > 1)
        {
            args.Player.SendMultipleMatchError(from i in itemByIdOrName
                                               select (args.Player.RealPlayer ? string.Format("[i:{0}]", i.type) : "") + i.Name);
            return;
        }
        if (itemByIdOrName.Count == 0)
        {
            args.Player.SendErrorMessage("指定的物品无效");
            return;
        }
        int item = itemByIdOrName[0].type;
        List<(string, int)> list = new();
        for (int id = 0; id < Main.chest.Length; id++)
        {
            if (Main.chest[id] == null)
            {
                continue;
            }
            int items = 0;
            foreach (var c in Main.chest[id].item)
            {
                if (c.type == item)
                {
                    items += c.stack;
                }

            }
            if (items != 0)
            {
                list.Add(($"[ID:{id}]({Main.chest[id].x},{Main.chest[id].y}) ", items));
            }
        }
        List<string> dataToPaginate = (from c in list
                                       orderby c.Item2 descending
                                       select string.Format("{0}{1}个", c.Item1, c.Item2)).ToList<string>();
        if (!PaginationTools.TryParsePageNumber(args.Parameters, 1, args.Player, out var pageNumber))
        {
            args.Player.SendErrorMessage("无效的页码！");
            return;
        }
        if (dataToPaginate.Count > 0)
        {
            args.Player.SendSuccessMessage($"物品[i:{item}]在服务器箱子中的拥有情况:");
            args.Player.SendInfoMessage(string.Join("\n", dataToPaginate));
        }
        else
        {
            args.Player.SendWarningMessage($"当前服务器暂无箱子拥有[i:{item}]");
        }

    }

    private void ItemSearchCmd(CommandArgs args)
    {
        try
        {
            data.Clear();
            if (args.Parameters.Count == 0)
            {
                args.Player.SendInfoMessage("用法:/si <物品ID/名称>\n其他命令:sc(查找箱子物品),ci(查询箱子信息),tpc(传送至指定箱子),tpallc(传送到地图的所有箱子),rci(移除箱子的物品),ri(移除玩家的指定物品)");
                return;
            }
            List<Item> itemByIdOrName = TShock.Utils.GetItemByIdOrName(args.Parameters[0]);
            if (itemByIdOrName.Count > 1)
            {
                args.Player.SendMultipleMatchError(from i in itemByIdOrName
                                                   select (args.Player.RealPlayer ? string.Format("[i:{0}]", i.type) : "") + i.Name);
                return;
            }
            if (itemByIdOrName.Count == 0)
            {
                args.Player.SendErrorMessage("指定的物品无效");
                return;
            }
            int item = itemByIdOrName[0].type;
            foreach (var p in TShock.Players)
            {
                p?.SaveServerCharacter();
            }
            List<UserAccount> userAccounts = TShock.UserAccounts.GetUserAccounts();
            List<(string, int)> list = new List<(string, int)>();
            //queryResult = TShock.CharacterDB.database.QueryReader("SELECT * FROM tsCharacter");
            queryResult = TShock.DB.QueryReader("SELECT * FROM tsCharacter");
            //queryResult.Read();
            while (queryResult.Reader.Read())
            {
                data.Add(queryResult.Reader.GetInt32(0), queryResult.Reader.GetString(5));
            }
            foreach (UserAccount item2 in userAccounts)
            {
                try
                {
                    if (TShock.Groups.GetGroupByName(item2.Group).HasPermission("tshock.ignore.bypassssc"))
                    {
                        continue;
                    }
                    List<NetItem>? list2 = TryGetInventory(item2.ID);
                    if (list2 != null)
                    {
                        int num = list2.Where((NetItem i) => i.NetId == item).Sum((NetItem i) => i.Stack);
                        if (num > 0)
                        {
                            list.Add((item2.Name, num));
                        }
                    }
                    else
                    {
                        TShock.Log.Info($"未能找到用户{item2.Name}的背包数据，已忽略");
                    }
                }
                catch
                {
                    TShock.Log.Info("用户" + item2.Name + "的背包错误，已忽略");
                }
            }
            List<string> dataToPaginate = (from c in list
                                           orderby c.Item2 descending
                                           select string.Format("[{0}]{1}个", c.Item1, c.Item2)).ToList<string>();
            if (!PaginationTools.TryParsePageNumber(args.Parameters, 1, args.Player, out var pageNumber))
            {
                args.Player.SendErrorMessage("无效的页码！");
                return;
            }
            if (dataToPaginate.Count > 0)
            {
                args.Player.SendSuccessMessage($"物品[i:{item}]在服务器中的拥有情况:");
                args.Player.SendInfoMessage(string.Join("\n", dataToPaginate));
            }
            else
            {
                args.Player.SendWarningMessage($"当前服务器暂无玩家拥有[i:{item}]");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
        }
        finally
        {
            queryResult?.Dispose();
            queryResult = null;
        }

    }

    private List<NetItem>? TryGetInventory(int accid)
    {
        if (accid == -1)
        {
            return null;
        }
        try
        {
            data.TryGetValue(accid, out string? inventory);
            if (string.IsNullOrEmpty(inventory))
            {
                return null;
            }
            List<NetItem> list = inventory.Split(new char[]
                    {
                        '~'
                    }).Select(new Func<string, NetItem>(NetItem.Parse)).ToList<NetItem>();
            if (list.Count < NetItem.MaxInventory)
            {
                list.InsertRange(67, new NetItem[2]);
                list.InsertRange(77, new NetItem[2]);
                list.InsertRange(87, new NetItem[2]);
                list.AddRange(new NetItem[NetItem.MaxInventory - list.Count]);
            }
            return list;
        }
        catch
        {
            return null;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ServerApi.Hooks.GamePostUpdate.Deregister(this, OnGamePostUpdate);
        }
        base.Dispose(disposing);
    }


public class GuardConfig
{
    public bool DetectionEnabled { get; set; } = true;
    public bool AutoWebOnDetect { get; set; } = true;
    public bool BroadcastOnDetect { get; set; } = true;
    public int AbnormalStackMultiplier { get; set; } = 2;
    public int ActionLevel { get; set; } = 3;
    public int ScanIntervalSeconds { get; set; } = 10;
    public int PunishCooldownSeconds { get; set; } = 30;
    public bool AbnormalLogBroadcastEnabled { get; set; } = true;
    public int AbnormalLogBroadcastHours { get; set; } = 1;
    public List<int> IllegalItemIds { get; set; } = new();
}
}
