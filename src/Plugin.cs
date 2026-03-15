using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;
using TerrariaApi.Server;
using Terraria.ID;
using TShockAPI;
using TShockAPI.DB;

[ApiVersion(2, 1)]
public class ItemSearchPlugin : TerrariaPlugin
{
    #region 插件核心配置（静态常量+私有字段）
    // 扫描间隔（10秒）
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(10);
    // 惩罚冷却（30秒，避免重复处罚）
    private static readonly TimeSpan PunishCooldown = TimeSpan.FromSeconds(30);
    // 默认非法物品ID列表
    private static readonly HashSet<int> IllegalItemIds = new()
    {
        ItemID.Zenith,
        ItemID.LastPrism,
        ItemID.CoinGun,
        ItemID.LunarFlareBook
    };

    // 上次扫描时间
    private DateTime _lastInventoryScan = DateTime.MinValue;
    // 玩家处罚记录（玩家索引 -> 上次处罚时间）
    private readonly Dictionary<int, DateTime> _punishRecords = new();
    // 数据库查询临时结果
    private QueryResult? _queryResult;
    // 临时数据存储
    private readonly Dictionary<int, string> _tempData = new();
    #endregion

    #region 插件基础信息
    public override string Name => "物品查找增强版";
    public override Version Version => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0, 0);
    public override string Author => "ak改版 | 优化版";
    public override string Description => "显示拥有指定物品的玩家/箱子，自动检测并处置非法物品";

    public ItemSearchPlugin(Main game) : base(game) { }
    #endregion

    #region 插件初始化/销毁
    public override void Initialize()
    {
        // 注册核心指令
        RegisterCommands();
        // 注册游戏更新钩子（定时扫描）
        ServerApi.Hooks.GamePostUpdate.Register(this, OnGamePostUpdate);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // 注销钩子，避免内存泄漏
            ServerApi.Hooks.GamePostUpdate.Deregister(this, OnGamePostUpdate);
            // 释放数据库连接
            _queryResult?.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>
    /// 统一注册所有指令（优化代码结构）
    /// </summary>
    private void RegisterCommands()
    {
        var commands = new List<Command>
        {
            new Command("itemsearch.cmd", ItemSearchCmd, "searchitem", "si", "查找物品"),
            new Command("itemsearch.chest", ChestSearchCmd, "searchchest", "sc", "查找箱子"),
            new Command("itemsearch.chesttp", TpSearchCmd, "tpchest", "tpc", "传送箱子"),
            new Command("itemsearch.chestinfo", InfoSearchCmd, "chestinfo", "ci", "箱子信息"),
            new Command("itemsearch.tpall", TpAllPlayer, "tpall", "传送所有人"),
            new Command("itemsearch.tpall", TpAllChest, "tpallchest", "tpallc", "传送所有箱子"),
            new Command("itemsearch.rci", RemoveItemChest, "removechestitem", "rci", "删除箱子物品"),
            new Command("itemsearch.ri", RemoveItem, "removeitem", "ri", "删除物品")
        };

        foreach (var cmd in commands)
        {
            Commands.ChatCommands.Add(cmd);
        }
    }
    #endregion

    #region 核心功能：定时扫描玩家物品（非法/异常检测）
    private void OnGamePostUpdate(EventArgs args)
    {
        // 控制扫描频率
        if (DateTime.UtcNow - _lastInventoryScan < ScanInterval)
        {
            return;
        }
        _lastInventoryScan = DateTime.UtcNow;

        // 遍历所有在线玩家
        foreach (var player in TShock.Players.Where(p => p != null && p.Active && p.TPlayer != null))
        {
            // 扫描玩家所有物品栏
            foreach (var item in GetAllPlayerItems(player.TPlayer))
            {
                if (item == null || item.type <= 0) continue;

                // 检测非法物品
                if (IsIllegalItem(item))
                {
                    PunishPlayer(player, item, "检测到非法物品");
                    break; // 找到违规物品后停止扫描该玩家
                }

                // 检测异常堆叠
                if (IsAbnormalStack(item))
                {
                    PunishPlayer(player, item, "检测到异常数量");
                    break;
                }
            }
        }
    }

    /// <summary>
    /// 获取玩家所有物品（覆盖全物品栏）
    /// </summary>
    private static IEnumerable<Item> GetAllPlayerItems(Player player)
    {
        foreach (var item in player.inventory) yield return item;
        foreach (var item in player.armor) yield return item;
        foreach (var item in player.dye) yield return item;
        foreach (var item in player.miscEquips) yield return item;
        foreach (var item in player.miscDyes) yield return item;
        foreach (var item in player.bank.item) yield return item;
        foreach (var item in player.bank2.item) yield return item;
        foreach (var item in player.bank3.item) yield return item;
        foreach (var item in player.bank4.item) yield return item;
        
        // 遍历所有负载栏
        foreach (var loadout in player.Loadouts)
        {
            foreach (var item in loadout.Armor) yield return item;
            foreach (var item in loadout.Dye) yield return item;
        }
        
        yield return player.trashItem; // 垃圾桶物品
    }

    /// <summary>
    /// 判断是否为非法物品
    /// </summary>
    private static bool IsIllegalItem(Item item) => IllegalItemIds.Contains(item.type);

    /// <summary>
    /// 判断是否为异常堆叠（超过最大堆叠数的2倍）
    /// </summary>
    private static bool IsAbnormalStack(Item item)
    {
        if (item.stack <= 0) return false;
        int maxAllowed = Math.Max(1, item.maxStack);
        return item.stack > maxAllowed * 2;
    }

    /// <summary>
    /// 处罚违规玩家（网住+通报+日志）
    /// </summary>
    private void PunishPlayer(TSPlayer player, Item targetItem, string reason)
    {
        // 冷却判断：避免短时间重复处罚
        if (_punishRecords.TryGetValue(player.Index, out var lastPunishTime) 
            && DateTime.UtcNow - lastPunishTime < PunishCooldown)
        {
            return;
        }
        _punishRecords[player.Index] = DateTime.UtcNow;

        // 执行处罚：网住玩家（1小时）+ 全服通报
        player.SetBuff(BuffID.Webbed, 60 * 60 * 60);
        string itemTag = TShock.Utils.ItemTag(targetItem);
        TShock.Utils.Broadcast($"[物品查找] 检测到玩家[{player.Name}]持有{itemTag}（{reason}），已自动网住并通报！", Color.OrangeRed);
        
        // 记录日志（便于管理员排查）
        TShock.Log.Warn($"[ItemSearchPlus] 玩家 {player.Name} 违规: {reason}, 物品={targetItem.Name}, 数量={targetItem.stack}");
    }
    #endregion

    #region 指令实现：删除玩家物品（在线/离线）
    private void RemoveItem(CommandArgs args)
    {
        _tempData.Clear();
        // 参数校验
        if (args.Parameters.Count != 2)
        {
            args.Player.SendInfoMessage("用法:/ri <玩家名> <物品名/ID>");
            return;
        }

        // 获取玩家账号
        var account = TShock.UserAccounts.GetUserAccountByName(args.Parameters[0]);
        if (account == null)
        {
            args.Player.SendErrorMessage($"找不到玩家: {args.Parameters[0]}");
            return;
        }

        // 解析目标物品
        var targetItems = TShock.Utils.GetItemByIdOrName(args.Parameters[1]);
        if (!ValidateTargetItem(args.Player, targetItems)) return;
        int targetItemId = targetItems[0].type;

        int removedCount = 0;
        // 处理在线玩家
        var onlinePlayer = TSPlayer.FindByNameOrID(account.Name).FirstOrDefault();
        if (onlinePlayer != null && onlinePlayer.Active)
        {
            removedCount = RemoveOnlinePlayerItems(onlinePlayer, targetItemId);
        }
        // 处理离线玩家（从数据库修改）
        else
        {
            removedCount = RemoveOfflinePlayerItems(args.Player, account.ID, targetItemId);
        }

        // 反馈结果
        string itemTag = TShock.Utils.ItemTag(new Item { type = targetItemId, stack = 1 });
        args.Player.SendSuccessMessage($"已移除玩家{account.Name}的{itemTag} × {removedCount}");
    }

    /// <summary>
    /// 移除在线玩家的指定物品
    /// </summary>
    private int RemoveOnlinePlayerItems(TSPlayer player, int itemId)
    {
        int count = 0;
        // 定义需要清理的物品栏（优化硬编码，便于维护）
        var itemSlots = new List<(Item[] items, PlayerItemSlotID baseSlot)>
        {
            (player.TPlayer.inventory, PlayerItemSlotID.Inventory0),
            (player.TPlayer.armor, PlayerItemSlotID.Armor0),
            (player.TPlayer.dye, PlayerItemSlotID.Dye0),
            (player.TPlayer.miscEquips, PlayerItemSlotID.Misc0),
            (player.TPlayer.miscDyes, PlayerItemSlotID.MiscDye0),
            (player.TPlayer.bank.item, PlayerItemSlotID.Bank1_0),
            (player.TPlayer.bank2.item, PlayerItemSlotID.Bank2_0),
            (player.TPlayer.bank3.item, PlayerItemSlotID.Bank3_0),
            (player.TPlayer.bank4.item, PlayerItemSlotID.Bank4_0)
        };

        // 清理基础物品栏
        foreach (var (slots, baseSlot) in itemSlots)
        {
            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i].type == itemId)
                {
                    count += slots[i].stack;
                    slots[i].SetDefaults(0);
                    NetMessage.SendData(5, -1, -1, null, player.Index, (int)baseSlot + i);
                }
            }
        }

        // 清理负载栏
        var loadoutSlots = new List<(Item[] items, PlayerItemSlotID baseSlot)>
        {
            (player.TPlayer.Loadouts[0].Armor, PlayerItemSlotID.Loadout1_Armor_0),
            (player.TPlayer.Loadouts[1].Armor, PlayerItemSlotID.Loadout2_Armor_0),
            (player.TPlayer.Loadouts[2].Armor, PlayerItemSlotID.Loadout3_Armor_0),
            (player.TPlayer.Loadouts[0].Dye, PlayerItemSlotID.Loadout1_Dye_0),
            (player.TPlayer.Loadouts[1].Dye, PlayerItemSlotID.Loadout2_Dye_0),
            (player.TPlayer.Loadouts[2].Dye, PlayerItemSlotID.Loadout3_Dye_0)
        };
        foreach (var (slots, baseSlot) in loadoutSlots)
        {
            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i].type == itemId)
                {
                    count += slots[i].stack;
                    slots[i].SetDefaults(0);
                    NetMessage.SendData(5, -1, -1, null, player.Index, (int)baseSlot + i);
                }
            }
        }

        // 清理垃圾桶
        if (player.TPlayer.trashItem.type == itemId)
        {
            count += player.TPlayer.trashItem.stack;
            player.TPlayer.trashItem.SetDefaults(0);
            NetMessage.SendData(5, -1, -1, null, player.Index, (int)PlayerItemSlotID.TrashItem);
        }

        return count;
    }

    /// <summary>
    /// 移除离线玩家的指定物品（修改数据库）
    /// </summary>
    private int RemoveOfflinePlayerItems(TSPlayer admin, int accountId, int itemId)
    {
        try
        {
            var playerData = TShock.CharacterDB.GetPlayerData(new TSPlayer(-1), accountId);
            if (playerData == null) return 0;

            int count = 0;
            // 清理背包数据
            for (int i = 0; i < playerData.inventory.Length; i++)
            {
                if (playerData.inventory[i].netID == itemId)
                {
                    count += playerData.inventory[i].Stack;
                    playerData.inventory[i] = new NetItem(0, 0, 0);
                }
            }

            // 更新数据库
            TShock.CharacterDB.database.Query(
                "UPDATE tsCharacter SET Inventory = @0 WHERE Account = @1",
                string.Join("~", playerData.inventory), accountId);

            return count;
        }
        catch (Exception ex)
        {
            admin.SendErrorMessage("处理离线玩家物品失败！");
            TShock.Log.ConsoleError($"[ItemSearch] 离线物品删除错误: {ex}");
            return 0;
        }
    }
    #endregion

    #region 指令实现：删除箱子物品
    private void RemoveItemChest(CommandArgs args)
    {
        // 参数校验
        if (args.Parameters.Count != 2)
        {
            args.Player.SendInfoMessage("用法:/rci <箱子ID> <物品名/ID>");
            return;
        }

        // 解析箱子ID
        if (!int.TryParse(args.Parameters[0], out int chestId) || chestId < 0 
            || chestId >= Main.chest.Length || Main.chest[chestId] == null)
        {
            args.Player.SendErrorMessage($"找不到ID为{args.Parameters[0]}的箱子！");
            return;
        }

        // 解析目标物品
        var targetItems = TShock.Utils.GetItemByIdOrName(args.Parameters[1]);
        if (!ValidateTargetItem(args.Player, targetItems)) return;
        int targetItemId = targetItems[0].type;

        // 清理箱子物品
        var chest = Main.chest[chestId];
        foreach (var item in chest.item.Where(i => i.type == targetItemId))
        {
            item.SetDefaults(0);
        }

        // 反馈结果
        string itemTag = TShock.Utils.ItemTag(new Item { type = targetItemId, stack = 1 });
        args.Player.SendSuccessMessage($"已移除箱子[{chestId}]的{itemTag}！");
        // 显示箱子剩余信息
        ShowChestInfo(args.Player, chestId);
    }
    #endregion

    #region 指令实现：查找玩家物品
    private void ItemSearchCmd(CommandArgs args)
    {
        try
        {
            _tempData.Clear();
            // 参数校验
            if (args.Parameters.Count == 0)
            {
                args.Player.SendInfoMessage("用法:/si <物品ID/名称>\n其他指令：\n" +
                    "sc(查找箱子物品) | ci(箱子信息) | tpc(传送箱子) | tpallc(传送所有箱子)\n" +
                    "rci(删除箱子物品) | ri(删除玩家物品)");
                return;
            }

            // 解析目标物品
            var targetItems = TShock.Utils.GetItemByIdOrName(args.Parameters[0]);
            if (!ValidateTargetItem(args.Player, targetItems)) return;
            int targetItemId = targetItems[0].type;

            // 保存所有玩家数据（确保数据最新）
            foreach (var player in TShock.Players.Where(p => p != null))
            {
                player.SaveServerCharacter();
            }

            // 读取数据库中所有玩家背包
            var playerItemCounts = new List<(string PlayerName, int Count)>();
            using (var reader = TShock.DB.QueryReader("SELECT * FROM tsCharacter"))
            {
                while (reader.Reader.Read())
                {
                    _tempData[reader.Reader.GetInt32(0)] = reader.Reader.GetString(5);
                }
            }

            // 统计每个玩家的物品数量
            foreach (var account in TShock.UserAccounts.GetUserAccounts())
            {
                // 跳过有豁免权限的玩家
                if (TShock.Groups.GetGroupByName(account.Group).HasPermission("tshock.ignore.bypassssc"))
                {
                    continue;
                }

                var inventory = TryGetPlayerInventory(account.ID);
                if (inventory == null) continue;

                int count = inventory.Where(i => i.NetId == targetItemId).Sum(i => i.Stack);
                if (count > 0)
                {
                    playerItemCounts.Add((account.Name, count));
                }
            }

            // 输出结果
            if (playerItemCounts.Any())
            {
                args.Player.SendSuccessMessage($"物品[i:{targetItemId}]的持有情况：");
                var sortedResult = playerItemCounts.OrderByDescending(x => x.Count)
                    .Select(x => $"[{x.PlayerName}] - {x.Count}个");
                args.Player.SendInfoMessage(string.Join("\n", sortedResult));
            }
            else
            {
                args.Player.SendWarningMessage($"当前服务器暂无玩家持有[i:{targetItemId}]");
            }
        }
        catch (Exception ex)
        {
            args.Player.SendErrorMessage("查询失败！");
            TShock.Log.ConsoleError($"[ItemSearch] 玩家物品查询错误: {ex}");
        }
    }
    #endregion

    #region 指令实现：查找箱子物品
    private void ChestSearchCmd(CommandArgs args)
    {
        // 参数校验
        if (args.Parameters.Count == 0)
        {
            args.Player.SendInfoMessage("用法:/sc <物品ID/名称>");
            return;
        }

        // 解析目标物品
        var targetItems = TShock.Utils.GetItemByIdOrName(args.Parameters[0]);
        if (!ValidateTargetItem(args.Player, targetItems)) return;
        int targetItemId = targetItems[0].type;

        // 统计所有箱子的物品数量
        var chestItemCounts = new List<(int ChestId, int X, int Y, int Count)>();
        for (int id = 0; id < Main.chest.Length; id++)
        {
            var chest = Main.chest[id];
            if (chest == null) continue;

            int count = chest.item.Where(i => i.type == targetItemId).Sum(i => i.stack);
            if (count > 0)
            {
                chestItemCounts.Add((id, chest.x, chest.y, count));
            }
        }

        // 输出结果
        if (chestItemCounts.Any())
        {
            args.Player.SendSuccessMessage($"物品[i:{targetItemId}]在箱子中的分布：");
            var sortedResult = chestItemCounts.OrderByDescending(x => x.Count)
                .Select(x => $"ID:{x.ChestId} ({x.X},{x.Y}) - {x.Count}个");
            args.Player.SendInfoMessage(string.Join("\n", sortedResult));
        }
        else
        {
            args.Player.SendWarningMessage($"当前服务器暂无箱子包含[i:{targetItemId}]");
        }
    }
    #endregion

    #region 指令实现：传送至箱子
    private void TpSearchCmd(CommandArgs args)
    {
        if (!args.Player.RealPlayer)
        {
            args.Player.SendErrorMessage("仅限游戏内使用！");
            return;
        }

        // 参数校验
        if (args.Parameters.Count == 0)
        {
            args.Player.SendInfoMessage("用法:/tpc <箱子ID>");
            return;
        }

        // 解析箱子ID并传送
        if (int.TryParse(args.Parameters[0], out int chestId) 
            && chestId >= 0 && chestId < Main.chest.Length 
            && Main.chest[chestId] != null)
        {
            var chest = Main.chest[chestId];
            args.Player.Teleport(chest.x * 16, chest.y * 16 + 2);
            args.Player.SendSuccessMessage($"已传送至箱子[{chestId}]！坐标：({chest.x},{chest.y})");
        }
        else
        {
            args.Player.SendErrorMessage($"找不到ID为{args.Parameters[0]}的箱子！");
        }
    }
    #endregion

    #region 指令实现：箱子信息查询
    private void InfoSearchCmd(CommandArgs args)
    {
        // 参数校验
        if (args.Parameters.Count == 0)
        {
            args.Player.SendInfoMessage("用法:/ci <箱子ID>");
            return;
        }

        // 解析箱子ID
        if (!int.TryParse(args.Parameters[0], out int chestId) 
            || chestId < 0 || chestId >= Main.chest.Length 
            || Main.chest[chestId] == null)
        {
            args.Player.SendErrorMessage($"找不到ID为{args.Parameters[0]}的箱子！");
            return;
        }

        // 显示箱子信息
        ShowChestInfo(args.Player, chestId);
    }

    /// <summary>
    /// 显示箱子详细信息（复用逻辑）
    /// </summary>
    private void ShowChestInfo(TSPlayer player, int chestId)
    {
        var chest = Main.chest[chestId];
        var items = chest.item.Where(i => i.type > 0)
            .Select(TShock.Utils.ItemTag)
            .ToList();

        string itemStr = items.Any() ? string.Join(" | ", items) : "空箱子";
        string chestName = string.IsNullOrEmpty(chest.name) ? "无名箱子" : chest.name;

        player.SendSuccessMessage($"箱子信息 [ID:{chestId}]\n" +
            $"坐标：({chest.x},{chest.y})\n" +
            $"名称：{chestName}\n" +
            $"是否商店：{(chest.bankChest ? "是" : "否")}\n" +
            $"物品：{itemStr}");
    }
    #endregion

    #region 指令实现：传送至所有玩家
    private void TpAllPlayer(CommandArgs args)
    {
        if (!args.Player.RealPlayer)
        {
            args.Player.SendErrorMessage("仅限游戏内使用！");
            return;
        }

        args.Player.SendInfoMessage("开始逐个传送至所有在线玩家位置（间隔1秒）...");
        // 异步执行，避免阻塞服务器
        Task.Run(() =>
        {
            foreach (var player in TShock.Players.Where(p => p != null && p.Active))
            {
                if (!args.Player.Active) break; // 玩家离线则停止
                args.Player.Teleport(player.X, player.Y);
                args.Player.SendInfoMessage($"已传送至[{player.Name}]！");
                System.Threading.Thread.Sleep(1000);
            }
            args.Player.SendSuccessMessage("传送完成！");
        });
    }
    #endregion

    #region 指令实现：传送至所有箱子
    private void TpAllChest(CommandArgs args)
    {
        if (!args.Player.RealPlayer)
        {
            args.Player.SendErrorMessage("仅限游戏内使用！");
            return;
        }

        args.Player.SendInfoMessage("开始逐个传送至所有箱子位置（间隔0.3秒）...");
        // 异步执行
        Task.Run(() =>
        {
            foreach (var chest in Main.chest.Where(c => c != null))
            {
                if (!args.Player.Active) break;
                args.Player.Teleport(chest.x * 16, chest.y * 16 + 2);
                args.Player.SendInfoMessage($"已传送至箱子（坐标：{chest.x},{chest.y}）");
                System.Threading.Thread.Sleep(300);
            }
            args.Player.SendSuccessMessage("传送完成！");
        });
    }
    #endregion

    #region 工具方法
    /// <summary>
    /// 验证目标物品（处理多匹配/无匹配）
    /// </summary>
    private bool ValidateTargetItem(TSPlayer player, List<Item> items)
    {
        if (items.Count > 1)
        {
            player.SendMultipleMatchError(items.Select(i => 
                (player.RealPlayer ? $"[i:{i.type}]" : "") + i.Name));
            return false;
        }
        if (items.Count == 0)
        {
            player.SendErrorMessage("指定的物品无效！");
            return false;
        }
        return true;
    }

    /// <summary>
    /// 尝试获取玩家背包数据
    /// </summary>
    private List<NetItem>? TryGetPlayerInventory(int accountId)
    {
        if (accountId == -1 || !_tempData.TryGetValue(accountId, out string? inventoryStr) || string.IsNullOrEmpty(inventoryStr))
        {
            return null;
        }

        try
        {
            var inventory = inventoryStr.Split('~')
                .Select(NetItem.Parse)
                .ToList();

            // 补全背包长度（避免索引越界）
            if (inventory.Count < NetItem.MaxInventory)
            {
                inventory.AddRange(new NetItem[NetItem.MaxInventory - inventory.Count]);
            }

            return inventory;
        }
        catch
        {
            return null;
        }
    }
    #endregion
}