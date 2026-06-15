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

    // 上次扫描时间
    private DateTime _lastInventoryScan = DateTime.MinValue;
    // 玩家处罚记录（玩家索引 -> 上次处罚时间）
    private readonly Dictionary<int, DateTime> _punishRecords = new();
    // 数据库查询临时结果
    private QueryResult? _queryResult;
    // 临时数据存储
    private readonly Dictionary<int, string> _tempData = new();

    // 非法物品检测开关
    private bool _autoScanEnabled = true;
    // 检测豁免权限（默认关闭，可通过指令开关）
    private bool _exemptOwnerSuperadmin = false;
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
            new Command("itemsearch.ri", RemoveItem, "removeitem", "ri", "删除物品"),
            new Command("itemsearch.admin", ToggleAutoScan, "toggleautoscan", "tas", "开关自动检测"),
            new Command("itemsearch.admin", ToggleExempt, "toggleexempt", "tex", "开关豁免检测")
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
        // 检测总开关
        if (!_autoScanEnabled) return;

        // 控制扫描频率
        if (DateTime.UtcNow - _lastInventoryScan < ScanInterval)
        {
            return;
        }
        _lastInventoryScan = DateTime.UtcNow;

        // 遍历所有在线玩家
        foreach (var player in TShock.Players.Where(p => p != null && p.Active && p.TPlayer != null))
        {
            // 豁免检测：owner/superadmin 可通过指令开关控制
            if (_exemptOwnerSuperadmin && player.Group != null)
            {
                var groupName = player.Group.Name?.ToLowerInvariant() ?? "";
                if (groupName == "owner" || groupName == "superadmin")
                    continue;
            }

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
    /// 判断是否为非法物品（按游戏进度阶段检测）
    /// </summary>
    private static bool IsIllegalItem(Item item)
    {
        // 月总后物品（最终阶段，永远合法）
        if (IsPostMoonLordItem(item.type))
            return false;

        // 月总前但世纪之花后的物品（中后期，永远合法）
        if (IsPostPlanteraItem(item.type))
            return false;

        // 机械Boss后但世纪之花前的物品（中期，永远合法）
        if (IsPostMechanicalBossItem(item.type))
            return false;

        // 困难模式初期物品（早期困难模式，永远合法）
        if (IsEarlyHardmodeItem(item.type))
            return false;

        // 肉山前物品（基础阶段，永远合法）
        if (IsPreHardmodeItem(item.type))
            return false;

        // 以下物品在对应进度前持有视为非法
        return IsProgressLockedItem(item.type);
    }

    #region 游戏进度物品分类

    // 肉山前物品（永远合法）
    private static bool IsPreHardmodeItem(int itemId)
    {
        // 基础矿石/锭
        if (itemId is >= 20 and <= 39) return true; // 铜-金锭等
        if (itemId is >= 700 and <= 702) return true; // 铂金锭等
        if (itemId is >= 19 and <= 21) return true; // 基础矿石

        // 肉山前Boss掉落/相关
        if (itemId == ItemID.EyeOfCthulhuYoyo) return true;
        if (itemId == ItemID.BeesKnees) return true;
        if (itemId == ItemID.BeeGun) return true;
        if (itemId == ItemID.HiveWand) return true;
        if (itemId == ItemID.BeeKeeper) return true;
        if (itemId == ItemID.HornetStaff) return true;
        if (itemId == ItemID.SkeletronHand) return true;
        if (itemId == ItemID.BookofSkulls) return true;
        if (itemId == ItemID.LaserRifle) return true;
        if (itemId == ItemID.Cascade) return true;
        if (itemId == ItemID.DemonScythe) return true;
        if (itemId == ItemID.Muramasa) return true;
        if (itemId == ItemID.BladeofGrass) return true;
        if (itemId == ItemID.FieryGreatsword) return true;
        if (itemId == ItemID.NightsEdge) return true;
        if (itemId == ItemID.MoltenFury) return true;
        if (itemId == ItemID.PhoenixBlaster) return true;
        if (itemId == ItemID.ImpStaff) return true;
        if (itemId == ItemID.Flamelash) return true;
        if (itemId == ItemID.Sunfury) return true;
        if (itemId == ItemID.DarkLance) return true;
        if (itemId == ItemID.Starfury) return true;
        if (itemId == ItemID.EnchantedSword) return true;
        if (itemId == ItemID.Arkhalis) return true;
        if (itemId == ItemID.Terragrim) return true;
        if (itemId == ItemID.FalconBlade) return true;
        if (itemId == ItemID.BladedGlove) return true;
        if (itemId == ItemID.BatBat) return true;
        if (itemId == ItemID.TrifoldMap) return true;

        // 基础工具/装备
        if (itemId == ItemID.MoltenPickaxe) return true;
        if (itemId == ItemID.NightmarePickaxe) return true;
        if (itemId == ItemID.DeathbringerPickaxe) return true;
        if (itemId == ItemID.CobaltShield) return true;
        if (itemId == ItemID.ObsidianShield) return true;
        if (itemId == ItemID.AnkhShield) return true;
        if (itemId == ItemID.CloudinaBottle) return true;
        if (itemId == ItemID.HermesBoots) return true;
        if (itemId == ItemID.RocketBoots) return true;
        if (itemId == ItemID.SpectreBoots) return true;
        if (itemId == ItemID.LightningBoots) return true;
        if (itemId == ItemID.FrostsparkBoots) return true;
        if (itemId == ItemID.TerrasparkBoots) return true;
        if (itemId == ItemID.ShinyRedBalloon) return true;
        if (itemId == ItemID.StarCloak) return true;
        if (itemId == ItemID.BeeCloak) return true;
        if (itemId == ItemID.CrossNecklace) return true;
        if (itemId == ItemID.PanicNecklace) return true;
        if (itemId == ItemID.SharkToothNecklace) return true;
        if (itemId == ItemID.StingerNecklace) return true;
        if (itemId == ItemID.FeralClaws) return true;
        if (itemId == ItemID.TitanGlove) return true;
        if (itemId == ItemID.PowerGlove) return true;
        if (itemId == ItemID.MechanicalGlove) return true;
        if (itemId == ItemID.FireGauntlet) return true;
        if (itemId == ItemID.MagmaStone) return true;
        if (itemId == ItemID.ObsidianRose) return true;

        return false;
    }

    // 困难模式初期物品（永远合法）
    private static bool IsEarlyHardmodeItem(int itemId)
    {
        // 困难模式三矿装备
        if (itemId == ItemID.CobaltSword) return true;
        if (itemId == ItemID.CobaltPickaxe) return true;
        if (itemId == ItemID.CobaltRepeater) return true;
        if (itemId == ItemID.PalladiumSword) return true;
        if (itemId == ItemID.PalladiumPickaxe) return true;
        if (itemId == ItemID.PalladiumRepeater) return true;
        if (itemId == ItemID.MythrilSword) return true;
        if (itemId == ItemID.MythrilPickaxe) return true;
        if (itemId == ItemID.MythrilRepeater) return true;
        if (itemId == ItemID.OrichalcumSword) return true;
        if (itemId == ItemID.OrichalcumPickaxe) return true;
        if (itemId == ItemID.OrichalcumRepeater) return true;
        if (itemId == ItemID.AdamantiteSword) return true;
        if (itemId == ItemID.AdamantitePickaxe) return true;
        if (itemId == ItemID.AdamantiteRepeater) return true;
        if (itemId == ItemID.TitaniumSword) return true;
        if (itemId == ItemID.TitaniumPickaxe) return true;
        if (itemId == ItemID.TitaniumRepeater) return true;

        // 困难模式初期武器
        if (itemId == ItemID.CrystalStorm) return true;
        if (itemId == ItemID.CursedFlames) return true;
        if (itemId == ItemID.GoldenShower) return true;
        if (itemId == ItemID.CrystalSerpent) return true;
        if (itemId == ItemID.Toxikarp) return true;
        if (itemId == ItemID.Bladetongue) return true;
        if (itemId == ItemID.Frostbrand) return true;
        if (itemId == ItemID.IceSickle) return true;
        if (itemId == ItemID.DeathSickle) return true;
        if (itemId == ItemID.MagicDagger) return true;
        if (itemId == ItemID.LightDisc) return true;
        if (itemId == ItemID.Flamarang) return true;
        if (itemId == ItemID.Frostbrand) return true;
        if (itemId == ItemID.DaoofPow) return true;
        if (itemId == ItemID.SoulDrain) return true;
        if (itemId == ItemID.CrystalVileShard) return true;
        if (itemId == ItemID.ShadowFlameKnife) return true;
        if (itemId == ItemID.ShadowFlameBow) return true;
        if (itemId == ItemID.ShadowFlameHexDoll) return true;
        if (itemId == ItemID.SpiderStaff) return true;
        if (itemId == ItemID.QueenSpiderStaff) return true;
        if (itemId == ItemID.PirateStaff) return true;
        if (itemId == ItemID.CoolWhip) return true;
        if (itemId == ItemID.FireWhip) return true;
        if (itemId == ItemID.SanguineStaff) return true;
        if (itemId == ItemID.MeteorStaff) return true;
        if (itemId == ItemID.SkyFracture) return true;
        if (itemId == ItemID.SoulDrain) return true;

        // 翅膀类
        if (itemId == ItemID.AngelWings) return true;
        if (itemId == ItemID.DemonWings) return true;
        if (itemId == ItemID.FairyWings) return true;
        if (itemId == ItemID.HarpyWings) return true;
        if (itemId == ItemID.ButterflyWings) return true;
        if (itemId == ItemID.FrozenWings) return true;
        if (itemId == ItemID.FlameWings) return true;
        if (itemId == ItemID.SpectreWings) return true;
        if (itemId == ItemID.BeetleWings) return true;
        if (itemId == ItemID.BatWings) return true;
        if (itemId == ItemID.BeeWings) return true;
        if (itemId == ItemID.BoneWings) return true;
        if (itemId == ItemID.MothronWings) return true;
        if (itemId == ItemID.SpookyWings) return true;
        if (itemId == ItemID.TatteredBeeWings) return true;
        if (itemId == ItemID.SteampunkWings) return true;
        if (itemId == ItemID.FestiveWings) return true;
        if (itemId == ItemID.FishronWings) return true;
        if (itemId == ItemID.WingsNebula) return true;
        if (itemId == ItemID.WingsSolar) return true;
        if (itemId == ItemID.WingsStardust) return true;
        if (itemId == ItemID.WingsVortex) return true;
        if (itemId == ItemID.WingsCelestialStarboard) return true;

        return false;
    }

    // 机械Boss后物品（中期，永远合法）
    private static bool IsPostMechanicalBossItem(int itemId)
    {
        // 神圣装备
        if (itemId == ItemID.Excalibur) return true;
        if (itemId == ItemID.Gungnir) return true;
        if (itemId == ItemID.HallowedRepeater) return true;
        if (itemId == ItemID.HallowedArmor) return true;
        if (itemId == ItemID.PickaxeAxe) return true;
        if (itemId == ItemID.Drax) return true;

        // 神圣Boss掉落
        if (itemId == ItemID.LightDisc) return true;
        if (itemId == ItemID.SlapHand) return true;
        if (itemId == ItemID.Megashark) return true;
        if (itemId == ItemID.Flamethrower) return true;
        if (itemId == ItemID.OpticStaff) return true;
        if (itemId == ItemID.SoulofSight) return true;
        if (itemId == ItemID.SoulofMight) return true;
        if (itemId == ItemID.SoulofFright) return true;
        if (itemId == ItemID.HallowedBar) return true;

        // 其他机械Boss后物品
        if (itemId == ItemID.TrueExcalibur) return true;
        if (itemId == ItemID.TrueNightsEdge) return true;
        if (itemId == ItemID.TerraBlade) return true;
        if (itemId == ItemID.ChlorophyteSword) return true;
        if (itemId == ItemID.ChlorophytePickaxe) return true;
        if (itemId == ItemID.ChlorophyteShotbow) return true;
        if (itemId == ItemID.ChlorophytePartisan) return true;
        if (itemId == ItemID.ChlorophyteClaymore) return true;
        if (itemId == ItemID.ChlorophyteWarhammer) return true;
        if (itemId == ItemID.ChlorophyteArmor) return true;
        if (itemId == ItemID.VenomStaff) return true;
        if (itemId == ItemID.LeafBlower) return true;
        if (itemId == ItemID.FlowerPow) return true;
        if (itemId == ItemID.WaspGun) return true;
        if (itemId == ItemID.Seedler) return true;
        if (itemId == ItemID.ThornHook) return true;
        if (itemId == ItemID.NettleBurst) return true;
        if (itemId == ItemID.PygmyStaff) return true;
        if (itemId == ItemID.DeadlySphereStaff) return true;
        if (itemId == ItemID.PirateStaff) return true;
        if (itemId == ItemID.XenoStaff) return true;
        if (itemId == ItemID.MagnetSphere) return true;
        if (itemId == ItemID.RainbowGun) return true;
        if (itemId == ItemID.StaffofEarth) return true;
        if (itemId == ItemID.InfernoFork) return true;
        if (itemId == ItemID.ShadowbeamStaff) return true;
        if (itemId == ItemID.SpectreStaff) return true;
        if (itemId == ItemID.EldMelter) return true;
        if (itemId == ItemID.ChristmasTreeSword) return true;
        if (itemId == ItemID.Razorpine) return true;
        if (itemId == ItemID.BlizzardStaff) return true;
        if (itemId == ItemID.NorthPole) return true;
        if (itemId == ItemID.SnowmanCannon) return true;
        if (itemId == ItemID.EldMelter) return true;
        if (itemId == ItemID.BatScepter) return true;
        if (itemId == ItemID.CandyCornRifle) return true;
        if (itemId == ItemID.JackOLanternLauncher) return true;
        if (itemId == ItemID.BlackSpot) return true;

        return false;
    }

    // 世纪之花后物品（中后期，永远合法）
    private static bool IsPostPlanteraItem(int itemId)
    {
        // 世纪之花掉落
        if (itemId == ItemID.GrenadeLauncher) return true;
        if (itemId == ItemID.VenusMagnum) return true;
        if (itemId == ItemID.NettleBurst) return true;
        if (itemId == ItemID.LeafBlower) return true;
        if (itemId == ItemID.FlowerPow) return true;
        if (itemId == ItemID.WaspGun) return true;
        if (itemId == ItemID.Seedler) return true;
        if (itemId == ItemID.ThornHook) return true;
        if (itemId == ItemID.TheAxe) return true;

        // 石巨人掉落
        if (itemId == ItemID.Stynger) return true;
        if (itemId == ItemID.PossessedHatchet) return true;
        if (itemId == ItemID.SunStone) return true;
        if (itemId == ItemID.EyeoftheGolem) return true;
        if (itemId == ItemID.HeatRay) return true;
        if (itemId == ItemID.StaffofEarth) return true;
        if (itemId == ItemID.GolemFist) return true;

        // 猪龙鱼公爵掉落
        if (itemId == ItemID.TempestStaff) return true;
        if (itemId == ItemID.Flairon) return true;
        if (itemId == ItemID.Tsunami) return true;
        if (itemId == ItemID.RazorbladeTyphoon) return true;
        if (itemId == ItemID.BubbleGun) return true;
        if (itemId == ItemID.Sharknado) return true;

        // 火星暴乱
        if (itemId == ItemID.InfluxWaver) return true;
        if (itemId == ItemID.Xenopopper) return true;
        if (itemId == ItemID.LaserMachinegun) return true;
        if (itemId == ItemID.ElectrosphereLauncher) return true;
        if (itemId == ItemID.CosmicCarKey) return true;
        if (itemId == ItemID.BrainScrambler) return true;
        if (itemId == ItemID.ChargedBlasterCannon) return true;
        if (itemId == ItemID.AntiGravityHook) return true;

        // 日食（世纪之花后）
        if (itemId == ItemID.DeathSickle) return true;
        if (itemId == ItemID.BrokenHeroSword) return true;
        if (itemId == ItemID.EyeSpring) return true;
        if (itemId == ItemID.MothronWings) return true;
        if (itemId == ItemID.TheEyeofCthulhu) return true;
        if (itemId == ItemID.ButchersChainsaw) return true;
        if (itemId == ItemID.DeadlySphereStaff) return true;
        if (itemId == ItemID.NailGun) return true;
        if (itemId == ItemID.PsychoKnife) return true;
        if (itemId == ItemID.BatWings) return true;

        // 南瓜月/霜月
        if (itemId == ItemID.TheHorsemansBlade) return true;
        if (itemId == ItemID.CandyCornRifle) return true;
        if (itemId == ItemID.JackOLanternLauncher) return true;
        if (itemId == ItemID.Sickle) return true;
        if (itemId == ItemID.RavenStaff) return true;
        if (itemId == ItemID.SpiderEgg) return true;
        if (itemId == ItemID.BatScepter) return true;
        if (itemId == ItemID.BlackBlade) return true;
        if (itemId == ItemID.ChristmasTreeSword) return true;
        if (itemId == ItemID.Razorpine) return true;
        if (itemId == ItemID.BlizzardStaff) return true;
        if (itemId == ItemID.NorthPole) return true;
        if (itemId == ItemID.SnowmanCannon) return true;
        if (itemId == ItemID.EldMelter) return true;

        return false;
    }

    // 月总后物品（最终阶段，永远合法）
    private static bool IsPostMoonLordItem(int itemId)
    {
        // 天顶剑
        if (itemId == ItemID.Zenith) return true;
        // 最终棱镜
        if (itemId == ItemID.LastPrism) return true;
        // 猫咪之刃
        if (itemId == ItemID.Meowmere) return true;
        // 星辰之怒
        if (itemId == ItemID.StarWrath) return true;
        // SDMG
        if (itemId == ItemID.SDMG) return true;
        // 庆典2型
        if (itemId == ItemID.CelebrationMk2) return true;
        // 彩虹猫之刃
        if (itemId == ItemID.RainbowCrystalStaff) return true;
        // 月亮传送门法杖
        if (itemId == ItemID.MoonlordTurretStaff) return true;
        // 月耀
        if (itemId == ItemID.LunarFlareBook) return true;
        // 破晓之光
        if (itemId == ItemID.DayBreak) return true;
        // 日曜喷发剑
        if (itemId == ItemID.SolarEruption) return true;
        // 星尘之龙法杖
        if (itemId == ItemID.StardustDragonStaff) return true;
        // 星尘细胞法杖
        if (itemId == ItemID.StardustCellStaff) return true;
        // 星云烈焰
        if (itemId == ItemID.NebulaBlaze) return true;
        // 星云奥秘
        if (itemId == ItemID.NebulaArcanum) return true;
        // 涡流碎星炮
        if (itemId == ItemID.VortexBeater) return true;
        // 幻影弓
        if (itemId == ItemID.Phantasm) return true;
        // 夜明锭相关
        if (itemId == ItemID.LunarBar) return true;
        // 夜明装备
        if (itemId == ItemID.SolarFlareHelmet) return true;
        if (itemId == ItemID.SolarFlareBreastplate) return true;
        if (itemId == ItemID.SolarFlareLeggings) return true;
        if (itemId == ItemID.VortexHelmet) return true;
        if (itemId == ItemID.VortexBreastplate) return true;
        if (itemId == ItemID.VortexLeggings) return true;
        if (itemId == ItemID.NebulaHelmet) return true;
        if (itemId == ItemID.NebulaBreastplate) return true;
        if (itemId == ItemID.NebulaLeggings) return true;
        if (itemId == ItemID.StardustHelmet) return true;
        if (itemId == ItemID.StardustBreastplate) return true;
        if (itemId == ItemID.StardustLeggings) return true;

        return false;
    }

    // 进度锁定物品（在达到对应进度前持有视为非法）
    private static bool IsProgressLockedItem(int itemId)
    {
        // 这些物品需要特定Boss击败后才能合法获得
        // 如果在服务器未达到对应进度时出现，视为非法

        // 机械Boss专属物品（需要至少一个机械Boss击败）
        if (itemId == ItemID.HallowedBar) return true;
        if (itemId == ItemID.SoulofSight) return true;
        if (itemId == ItemID.SoulofMight) return true;
        if (itemId == ItemID.SoulofFright) return true;
        if (itemId == ItemID.TrueExcalibur) return true;
        if (itemId == ItemID.TrueNightsEdge) return true;
        if (itemId == ItemID.TerraBlade) return true;

        // 世纪之花专属物品
        if (itemId == ItemID.TheAxe) return true;
        if (itemId == ItemID.GrenadeLauncher) return true;
        if (itemId == ItemID.VenusMagnum) return true;
        if (itemId == ItemID.NettleBurst) return true;
        if (itemId == ItemID.LeafBlower) return true;
        if (itemId == ItemID.FlowerPow) return true;
        if (itemId == ItemID.WaspGun) return true;
        if (itemId == ItemID.Seedler) return true;
        if (itemId == ItemID.ThornHook) return true;

        // 石巨人专属物品
        if (itemId == ItemID.Stynger) return true;
        if (itemId == ItemID.PossessedHatchet) return true;
        if (itemId == ItemID.SunStone) return true;
        if (itemId == ItemID.EyeoftheGolem) return true;
        if (itemId == ItemID.HeatRay) return true;
        if (itemId == ItemID.StaffofEarth) return true;
        if (itemId == ItemID.GolemFist) return true;

        // 猪龙鱼公爵专属物品
        if (itemId == ItemID.TempestStaff) return true;
        if (itemId == ItemID.Flairon) return true;
        if (itemId == ItemID.Tsunami) return true;
        if (itemId == ItemID.RazorbladeTyphoon) return true;
        if (itemId == ItemID.BubbleGun) return true;
        if (itemId == ItemID.Sharknado) return true;

        // 拜月教邪教徒专属物品
        if (itemId == ItemID.AncientManipulator) return true;
        if (itemId == ItemID.LunarCraftingStation) return true;

        // 四柱/月总专属物品
        if (itemId == ItemID.SolarFragment) return true;
        if (itemId == ItemID.VortexFragment) return true;
        if (itemId == ItemID.NebulaFragment) return true;
        if (itemId == ItemID.StardustFragment) return true;
        if (itemId == ItemID.LunarBar) return true;
        if (itemId == ItemID.SolarFlareHelmet) return true;
        if (itemId == ItemID.SolarFlareBreastplate) return true;
        if (itemId == ItemID.SolarFlareLeggings) return true;
        if (itemId == ItemID.VortexHelmet) return true;
        if (itemId == ItemID.VortexBreastplate) return true;
        if (itemId == ItemID.VortexLeggings) return true;
        if (itemId == ItemID.NebulaHelmet) return true;
        if (itemId == ItemID.NebulaBreastplate) return true;
        if (itemId == ItemID.NebulaLeggings) return true;
        if (itemId == ItemID.StardustHelmet) return true;
        if (itemId == ItemID.StardustBreastplate) return true;
        if (itemId == ItemID.StardustLeggings) return true;

        // 最终武器
        if (itemId == ItemID.Zenith) return true;
        if (itemId == ItemID.LastPrism) return true;
        if (itemId == ItemID.Meowmere) return true;
        if (itemId == ItemID.StarWrath) return true;
        if (itemId == ItemID.SDMG) return true;
        if (itemId == ItemID.CelebrationMk2) return true;
        if (itemId == ItemID.RainbowCrystalStaff) return true;
        if (itemId == ItemID.MoonlordTurretStaff) return true;
        if (itemId == ItemID.LunarFlareBook) return true;
        if (itemId == ItemID.DayBreak) return true;
        if (itemId == ItemID.SolarEruption) return true;
        if (itemId == ItemID.StardustDragonStaff) return true;
        if (itemId == ItemID.StardustCellStaff) return true;
        if (itemId == ItemID.NebulaBlaze) return true;
        if (itemId == ItemID.NebulaArcanum) return true;
        if (itemId == ItemID.VortexBeater) return true;
        if (itemId == ItemID.Phantasm) return true;

        // 特殊稀有物品（通常需要特定条件）
        if (itemId == ItemID.CoinGun) return true;
        if (itemId == ItemID.DiscountCard) return true;
        if (itemId == ItemID.LuckyCoin) return true;
        if (itemId == ItemID.PirateMap) return true;
        if (itemId == ItemID.GoldRing) return true;

        return false;
    }
    #endregion

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

    #region 指令实现：开关自动检测
    private void ToggleAutoScan(CommandArgs args)
    {
        if (args.Parameters.Count == 0)
        {
            args.Player.SendInfoMessage($"当前自动检测状态：{(_autoScanEnabled ? "开启" : "关闭")}\n用法: /tas on|off");
            return;
        }

        var param = args.Parameters[0].ToLowerInvariant();
        if (param == "on" || param == "1" || param == "true")
        {
            _autoScanEnabled = true;
            args.Player.SendSuccessMessage("非法物品自动检测已开启！");
        }
        else if (param == "off" || param == "0" || param == "false")
        {
            _autoScanEnabled = false;
            args.Player.SendSuccessMessage("非法物品自动检测已关闭！");
        }
        else
        {
            args.Player.SendErrorMessage("参数错误！用法: /tas on|off");
        }
    }
    #endregion

    #region 指令实现：开关豁免检测
    private void ToggleExempt(CommandArgs args)
    {
        if (args.Parameters.Count == 0)
        {
            args.Player.SendInfoMessage($"当前豁免检测状态：{(_exemptOwnerSuperadmin ? "开启" : "关闭")}\n用法: /tex on|off");
            return;
        }

        var param = args.Parameters[0].ToLowerInvariant();
        if (param == "on" || param == "1" || param == "true")
        {
            _exemptOwnerSuperadmin = true;
            args.Player.SendSuccessMessage("Owner/SuperAdmin 豁免检测已开启！");
        }
        else if (param == "off" || param == "0" || param == "false")
        {
            _exemptOwnerSuperadmin = false;
            args.Player.SendSuccessMessage("Owner/SuperAdmin 豁免检测已关闭！");
        }
        else
        {
            args.Player.SendErrorMessage("参数错误！用法: /tex on|off");
        }
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
        var itemSlots = new List<(Item[] items, int baseSlot)>
        {
            (player.TPlayer.inventory, (int)PlayerItemSlotID.Inventory0),
            (player.TPlayer.armor, (int)PlayerItemSlotID.Armor0),
            (player.TPlayer.dye, (int)PlayerItemSlotID.Dye0),
            (player.TPlayer.miscEquips, (int)PlayerItemSlotID.Misc0),
            (player.TPlayer.miscDyes, (int)PlayerItemSlotID.MiscDye0),
            (player.TPlayer.bank.item, (int)PlayerItemSlotID.Bank1_0),
            (player.TPlayer.bank2.item, (int)PlayerItemSlotID.Bank2_0),
            (player.TPlayer.bank3.item, (int)PlayerItemSlotID.Bank3_0),
            (player.TPlayer.bank4.item, (int)PlayerItemSlotID.Bank4_0)
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
                    NetMessage.SendData(5, -1, -1, null, player.Index, baseSlot + i);
                }
            }
        }

        // 清理负载栏
        var loadoutSlots = new List<(Item[] items, int baseSlot)>
        {
            (player.TPlayer.Loadouts[0].Armor, (int)PlayerItemSlotID.Loadout1_Armor_0),
            (player.TPlayer.Loadouts[1].Armor, (int)PlayerItemSlotID.Loadout2_Armor_0),
            (player.TPlayer.Loadouts[2].Armor, (int)PlayerItemSlotID.Loadout3_Armor_0),
            (player.TPlayer.Loadouts[0].Dye, (int)PlayerItemSlotID.Loadout1_Dye_0),
            (player.TPlayer.Loadouts[1].Dye, (int)PlayerItemSlotID.Loadout2_Dye_0),
            (player.TPlayer.Loadouts[2].Dye, (int)PlayerItemSlotID.Loadout3_Dye_0)
        };
        foreach (var (slots, baseSlot) in loadoutSlots)
        {
            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i].type == itemId)
                {
                    count += slots[i].stack;
                    slots[i].SetDefaults(0);
                    NetMessage.SendData(5, -1, -1, null, player.Index, baseSlot + i);
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
                if (playerData.inventory[i].NetId == itemId)
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
