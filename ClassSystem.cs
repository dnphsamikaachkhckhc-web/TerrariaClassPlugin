using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.Hooks;

namespace ClassSystemPlugin
{
    internal sealed class MiscEntitySource : IEntitySource
    {
    }

    [ApiVersion(2, 1)]
    public class ClassSystem : TerrariaPlugin
    {
        public override string Name => "ClassSystem";
        public override string Author => "CustomDev";
        public override string Description => "Account-bound class system with underworld lock before login, webbed lock after login until class selection, starter gear, safe cross-class inventory ejection (never deleted) and dropped item pickup lock, Max HP modifiers per class + permanent Summoning buff + permanent Honey buff (+15% life regen) + permanent uncleansable Broken Armor buff (5% defense reduction) for Summoner, quick /c shortcuts, Life Crystal limit, and real-time HP sync.";
        public override Version Version => new Version(15, 35, 0);

        private SqliteConnection _db;
        private bool _dbReady;
        private readonly Dictionary<int, string> _playerClasses = new Dictionary<int, string>();
        private readonly Dictionary<int, Vector2> _frozenPositions = new Dictionary<int, Vector2>();
        private readonly Dictionary<int, DateTime> _warnCooldown = new Dictionary<int, DateTime>();
        private readonly Dictionary<int, DateTime> _dropWarnCooldown = new Dictionary<int, DateTime>();
        private readonly Dictionary<int, DateTime> _slotEjectCooldown = new Dictionary<int, DateTime>();
        private readonly Dictionary<int, DateTime> _crystalWarnCooldown = new Dictionary<int, DateTime>();
        private readonly Dictionary<int, DateTime> _summonBuffCooldown = new Dictionary<int, DateTime>();
        private readonly Dictionary<int, DateTime> _honeyBuffCooldown = new Dictionary<int, DateTime>();
        private readonly Dictionary<int, DateTime> _brokenArmorBuffCooldown = new Dictionary<int, DateTime>();
        private readonly Dictionary<int, int> _lastSyncedMaxLife = new Dictionary<int, int>();
        private readonly Dictionary<int, double> _honeyRegenAccumulator = new Dictionary<int, double>();
        private readonly Dictionary<int, HashSet<int>> _blockedWorldItemsByPlayer = new Dictionary<int, HashSet<int>>();
        private DateTime _lastTickSync = DateTime.MinValue;
        private DateTime _lastHoneyTick = DateTime.UtcNow;

        private const int MaxLifeCrystalsAllowed = 15;
        private const int BaseStartHp = 100;
        private const int HpPerLifeCrystal = 20;
        private const double HoneyBonusLifeRegenMultiplier = 0.15;
        private const double SummonerBrokenArmorReduction = 0.05;

        // Max HP modifiers per class only
        private const int WarriorMaxHpFlat = 20;
        private const int RangerMaxHpFlat = -40;
        private const int MageMaxHpFlat = -60;
        private const int SummonerMaxHpFlat = -80;

        private readonly Dictionary<string, (short ItemId, int Stack)[]> _classStarterItems =
            new Dictionary<string, (short ItemId, int Stack)[]>
            {
                ["warrior"] = new[] { (ItemID.CopperBroadsword, 1) },
                ["ranger"] = new[] { (ItemID.WoodenBow, 1), (ItemID.WoodenArrow, 500) },
                ["mage"] = new[] { (ItemID.WandofSparking, 1), (ItemID.LesserManaPotion, 15) },
                ["summoner"] = new[] { (ItemID.SlimeStaff, 1), (ItemID.BlandWhip, 1) }
            };

        private readonly List<string> _availableClasses = new List<string>
        {
            "warrior", "ranger", "mage", "summoner"
        };

        private readonly Dictionary<string, string> _classShortcuts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["me"] = "warrior",
            ["rg"] = "ranger",
            ["mg"] = "mage",
            ["sm"] = "summoner"
        };

        // ชุดเกราะที่แยกสายชัดเจน (ไม่รวมเครื่องประดับเพราะเครื่องประดับใส่อะไรก็ได้)
        private readonly Dictionary<int, string> _restrictedArmors = new Dictionary<int, string>
        {
            // Pre-Hardmode Melee / Warrior
            { ItemID.GladiatorHelmet, "warrior" },
            { ItemID.GladiatorBreastplate, "warrior" },
            { ItemID.GladiatorLeggings, "warrior" },
            { ItemID.ShadowHelmet, "warrior" },
            { ItemID.ShadowScalemail, "warrior" },
            { ItemID.ShadowGreaves, "warrior" },
            { ItemID.AncientShadowHelmet, "warrior" },
            { ItemID.AncientShadowScalemail, "warrior" },
            { ItemID.AncientShadowGreaves, "warrior" },
            { ItemID.MoltenHelmet, "warrior" },
            { ItemID.MoltenBreastplate, "warrior" },
            { ItemID.MoltenGreaves, "warrior" },

            // Pre-Hardmode Ranged / Ranger
            { ItemID.FossilHelm, "ranger" },
            { ItemID.NecroHelmet, "ranger" },
            { ItemID.NecroBreastplate, "ranger" },
            { ItemID.NecroGreaves, "ranger" },
            { ItemID.AncientNecroHelmet, "ranger" },

            // Pre-Hardmode Magic / Mage
            { ItemID.WizardHat, "mage" },
            { ItemID.MagicHat, "mage" },
            { ItemID.JungleHat, "mage" },
            { ItemID.JungleShirt, "mage" },
            { ItemID.JunglePants, "mage" },
            { ItemID.AncientCobaltHelmet, "mage" },
            { ItemID.AncientCobaltBreastplate, "mage" },
            { ItemID.AncientCobaltLeggings, "mage" },
            { ItemID.GypsyRobe, "mage" },

            // Pre-Hardmode Summon / Summoner
            { ItemID.ObsidianHelm, "summoner" },
            { ItemID.ObsidianShirt, "summoner" },
            { ItemID.ObsidianPants, "summoner" },
            { ItemID.BeeHeadgear, "summoner" },
            { ItemID.BeeBreastplate, "summoner" },
            { ItemID.BeeGreaves, "summoner" },

            // Hardmode Cobalt / Palladium
            { ItemID.CobaltHelmet, "warrior" },
            { ItemID.CobaltHat, "ranger" },
            { ItemID.CobaltMask, "mage" },
            { ItemID.CobaltBreastplate, "warrior" },
            { ItemID.CobaltLeggings, "warrior" },
            { ItemID.PalladiumHelmet, "warrior" },
            { ItemID.PalladiumHeadgear, "ranger" },
            { ItemID.PalladiumMask, "mage" },
            { ItemID.PalladiumBreastplate, "warrior" },
            { ItemID.PalladiumLeggings, "warrior" },

            // Hardmode Mythril / Orichalcum
            { ItemID.MythrilHelmet, "warrior" },
            { ItemID.MythrilHat, "ranger" },
            { ItemID.MythrilHood, "mage" },
            { ItemID.MythrilChainmail, "warrior" },
            { ItemID.MythrilGreaves, "warrior" },
            { ItemID.OrichalcumHelmet, "warrior" },
            { ItemID.OrichalcumHeadgear, "ranger" },
            { ItemID.OrichalcumMask, "mage" },
            { ItemID.OrichalcumBreastplate, "warrior" },
            { ItemID.OrichalcumLeggings, "warrior" },

            // Hardmode Adamantite / Titanium
            { ItemID.AdamantiteHelmet, "warrior" },
            { ItemID.AdamantiteMask, "ranger" },
            { ItemID.AdamantiteHeadgear, "mage" },
            { ItemID.AdamantiteBreastplate, "warrior" },
            { ItemID.AdamantiteLeggings, "warrior" },
            { ItemID.TitaniumHelmet, "warrior" },
            { ItemID.TitaniumMask, "ranger" },
            { ItemID.TitaniumHeadgear, "mage" },
            { ItemID.TitaniumBreastplate, "warrior" },
            { ItemID.TitaniumLeggings, "warrior" },

            // Hardmode Frost / Spider / Hallowed / Chlorophyte
            { ItemID.FrostHelmet, "ranger" },
            { ItemID.FrostBreastplate, "ranger" },
            { ItemID.FrostLeggings, "ranger" },
            { ItemID.SpiderMask, "summoner" },
            { ItemID.SpiderBreastplate, "summoner" },
            { ItemID.SpiderGreaves, "summoner" },
            { ItemID.HallowedHelmet, "warrior" },
            { ItemID.HallowedHeadgear, "ranger" },
            { ItemID.HallowedHood, "mage" },
            { ItemID.HallowedPlateMail, "warrior" },
            { ItemID.HallowedGreaves, "warrior" },
            { ItemID.ChlorophyteHelmet, "warrior" },
            { ItemID.ChlorophyteMask, "ranger" },
            { ItemID.ChlorophyteHeadgear, "mage" },
            { ItemID.ChlorophytePlateMail, "warrior" },
            { ItemID.ChlorophyteGreaves, "warrior" },

            // Tiki / Turtle / Beetle / Shroomite / Spectre / Spooky / Lunar
            { ItemID.TikiMask, "summoner" },
            { ItemID.TikiShirt, "summoner" },
            { ItemID.TikiPants, "summoner" },
            { ItemID.TurtleHelmet, "warrior" },
            { ItemID.TurtleScaleMail, "warrior" },
            { ItemID.TurtleLeggings, "warrior" },
            { ItemID.BeetleHelmet, "warrior" },
            { ItemID.BeetleScaleMail, "warrior" },
            { ItemID.BeetleShell, "warrior" },
            { ItemID.BeetleLeggings, "warrior" },
            { ItemID.ShroomiteHeadgear, "ranger" },
            { ItemID.ShroomiteMask, "ranger" },
            { ItemID.ShroomiteHelmet, "ranger" },
            { ItemID.ShroomiteBreastplate, "ranger" },
            { ItemID.ShroomiteLeggings, "ranger" },
            { ItemID.SpectreHood, "mage" },
            { ItemID.SpectreMask, "mage" },
            { ItemID.SpectreRobe, "mage" },
            { ItemID.SpectrePants, "mage" },
            { ItemID.SpookyHelmet, "summoner" },
            { ItemID.SpookyBreastplate, "summoner" },
            { ItemID.SpookyLeggings, "summoner" },
            { ItemID.SolarFlareHelmet, "warrior" },
            { ItemID.SolarFlareBreastplate, "warrior" },
            { ItemID.SolarFlareLeggings, "warrior" },
            { ItemID.VortexHelmet, "ranger" },
            { ItemID.VortexBreastplate, "ranger" },
            { ItemID.VortexLeggings, "ranger" },
            { ItemID.NebulaHelmet, "mage" },
            { ItemID.NebulaBreastplate, "mage" },
            { ItemID.NebulaLeggings, "mage" },
            { ItemID.StardustHelmet, "summoner" },
            { ItemID.StardustBreastplate, "summoner" },
            { ItemID.StardustLeggings, "summoner" }
        };

        public ClassSystem(Main game) : base(game) { }

        public override void Initialize()
        {
            SetupDatabase();

            ServerApi.Hooks.GameInitialize.Register(this, OnGameInitialize);
            ServerApi.Hooks.GameUpdate.Register(this, OnGameUpdate);
            ServerApi.Hooks.NetGreetPlayer.Register(this, OnGreetPlayer);
            ServerApi.Hooks.ServerLeave.Register(this, OnServerLeave);
            ServerApi.Hooks.NpcStrike.Register(this, OnNpcStrike);

            PlayerHooks.PlayerCommand += OnPlayerCommand;
            PlayerHooks.PlayerPostLogin += OnPlayerPostLogin;
            GetDataHandlers.PlayerUpdate += OnPlayerUpdate;
            GetDataHandlers.PlayerSlot += OnPlayerSlot;
            GetDataHandlers.ItemDrop += OnItemDrop;
            GetDataHandlers.PlayerDamage += OnPlayerDamage;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ServerApi.Hooks.GameInitialize.Deregister(this, OnGameInitialize);
                ServerApi.Hooks.GameUpdate.Deregister(this, OnGameUpdate);
                ServerApi.Hooks.NetGreetPlayer.Deregister(this, OnGreetPlayer);
                ServerApi.Hooks.ServerLeave.Deregister(this, OnServerLeave);
                ServerApi.Hooks.NpcStrike.Deregister(this, OnNpcStrike);

                PlayerHooks.PlayerCommand -= OnPlayerCommand;
                PlayerHooks.PlayerPostLogin -= OnPlayerPostLogin;
                GetDataHandlers.PlayerUpdate -= OnPlayerUpdate;
                GetDataHandlers.PlayerSlot -= OnPlayerSlot;
                GetDataHandlers.ItemDrop -= OnItemDrop;
                GetDataHandlers.PlayerDamage -= OnPlayerDamage;

                if (_db != null)
                {
                    try { _db.Dispose(); } catch { }
                    _db = null;
                }
            }
            base.Dispose(disposing);
        }

        private void EnsureSummonerHoneyBuff(TSPlayer player)
        {
            if (player == null || player.TPlayer == null || !player.Active || player.TPlayer.dead) return;

            if (_honeyBuffCooldown.TryGetValue(player.Index, out DateTime lastCd) && (DateTime.UtcNow - lastCd).TotalSeconds < 1.5)
                return;

            bool hasHoney = false;
            for (int i = 0; i < Player.maxBuffs; i++)
            {
                if (player.TPlayer.buffType[i] == BuffID.Honey && player.TPlayer.buffTime[i] > 60)
                {
                    hasHoney = true;
                    break;
                }
            }

            if (!hasHoney)
            {
                _honeyBuffCooldown[player.Index] = DateTime.UtcNow;
                player.SetBuff(BuffID.Honey, 240, true);
            }
        }

        private void EnsureSummonerPermanentBrokenArmorBuff(TSPlayer player)
        {
            if (player == null || player.TPlayer == null || !player.Active || player.TPlayer.dead) return;

            if (_brokenArmorBuffCooldown.TryGetValue(player.Index, out DateTime lastCd) && (DateTime.UtcNow - lastCd).TotalMilliseconds < 400)
                return;

            bool hasImmunityAccessory = HasBrokenArmorImmunityItem(player.TPlayer);
            int targetBuffId = hasImmunityAccessory ? BuffID.WitheredArmor : BuffID.BrokenArmor;

            bool hasActiveBuff = false;
            for (int i = 0; i < Player.maxBuffs; i++)
            {
                int bType = player.TPlayer.buffType[i];
                if ((bType == BuffID.BrokenArmor || bType == BuffID.WitheredArmor) && player.TPlayer.buffTime[i] > 60)
                {
                    if (!hasImmunityAccessory || bType == BuffID.WitheredArmor)
                    {
                        hasActiveBuff = true;
                        break;
                    }
                }
            }

            if (!hasActiveBuff)
            {
                _brokenArmorBuffCooldown[player.Index] = DateTime.UtcNow;
                player.SetBuff(targetBuffId, 240, true);
            }
        }

        private int CalculatePlayerEquipmentDefense(Player tPlayer)
        {
            if (tPlayer == null) return 0;
            int totalDef = 0;

            for (int i = 0; i < 3 && i < tPlayer.armor.Length; i++)
            {
                Item arm = tPlayer.armor[i];
                if (arm != null && !arm.IsAir && arm.type > ItemID.None)
                {
                    totalDef += Math.Max(0, arm.defense);
                }
            }

            for (int i = 3; i < 10 && i < tPlayer.armor.Length; i++)
            {
                Item acc = tPlayer.armor[i];
                if (acc != null && !acc.IsAir && acc.type > ItemID.None)
                {
                    totalDef += Math.Max(0, acc.defense);
                    totalDef += GetDefensePrefixBonus(acc.prefix);
                }
            }

            if (HasBuff(tPlayer, BuffID.Ironskin)) totalDef += 8;
            if (HasBuff(tPlayer, BuffID.WellFed)) totalDef += 2;
            if (HasBuff(tPlayer, BuffID.WellFed2)) totalDef += 3;
            if (HasBuff(tPlayer, BuffID.WellFed3)) totalDef += 4;
            if (HasBuff(tPlayer, BuffID.CatBast)) totalDef += 5;

            return Math.Max(0, totalDef);
        }

        private int GetDefensePrefixBonus(byte prefix)
        {
            switch (prefix)
            {
                case PrefixID.Hard: return 1;
                case PrefixID.Guarding: return 2;
                case PrefixID.Armored: return 3;
                case PrefixID.Warding: return 4;
                default: return 0;
            }
        }

        private bool HasBuff(Player tPlayer, int buffId)
        {
            if (tPlayer == null) return false;
            for (int i = 0; i < Player.maxBuffs; i++)
            {
                if (tPlayer.buffType[i] == buffId && tPlayer.buffTime[i] > 0)
                {
                    return true;
                }
            }
            return false;
        }

        private bool HasBuff(TSPlayer player, int buffId)
        {
            if (player?.TPlayer == null) return false;
            for (int i = 0; i < Player.maxBuffs; i++)
            {
                if (player.TPlayer.buffType[i] == buffId && player.TPlayer.buffTime[i] > 0)
                {
                    return true;
                }
            }
            return false;
        }

        private bool HasBrokenArmorImmunityItem(Player tPlayer)
        {
            if (tPlayer == null) return false;
            for (int i = 3; i < 10 && i < tPlayer.armor.Length; i++)
            {
                Item acc = tPlayer.armor[i];
                if (acc == null || acc.IsAir) continue;
                if (acc.type == ItemID.ArmorPolish ||
                    acc.type == ItemID.ArmorBracing ||
                    acc.type == ItemID.AnkhCharm ||
                    acc.type == ItemID.AnkhShield)
                {
                    return true;
                }
            }
            return false;
        }

        private void OnPlayerDamage(object sender, GetDataHandlers.PlayerDamageEventArgs args)
        {
            var player = TShock.Players[args.ID];
            if (player == null || !player.Active || !player.IsLoggedIn || player.Account == null) return;

            string playerClass = GetPlayerClass(player);
            if (!string.Equals(playerClass, "summoner", StringComparison.OrdinalIgnoreCase)) return;

            if (args.Damage <= 1) return;

            int baseDefense = CalculatePlayerEquipmentDefense(player.TPlayer);
            if (baseDefense <= 0) return;

            double defenseEffectiveness = Main.masterMode ? 1.0 : (Main.expertMode ? 0.75 : 0.5);

            int defAt50Percent = (int)Math.Floor(baseDefense * 0.50);
            int defAt5PercentLoss = (int)Math.Floor(baseDefense * (1.0 - SummonerBrokenArmorReduction));
            int extraDefenseSaved = Math.Max(0, defAt5PercentLoss - defAt50Percent);

            int damageRefund = (int)Math.Floor(extraDefenseSaved * defenseEffectiveness);
            if (damageRefund > 0)
            {
                int maxRefund = Math.Max(0, args.Damage - 1);
                int actualRefund = Math.Min(damageRefund, maxRefund);
                if (actualRefund > 0 && player.TPlayer.statLife > 0)
                {
                    player.TPlayer.statLife = Math.Min(player.TPlayer.statLifeMax2, player.TPlayer.statLife + actualRefund);
                    NetMessage.SendData((int)PacketTypes.PlayerHp, -1, -1, null, player.Index);
                    NetMessage.SendData((int)PacketTypes.PlayerHp, player.Index, -1, null, player.Index);
                }
            }
        }

        private void ApplySummonerPermanentBrokenArmorEffect(TSPlayer player, string playerClass)
        {
            if (player?.TPlayer == null || !player.Active || player.TPlayer.dead) return;

            if (!string.Equals(playerClass, "summoner", StringComparison.OrdinalIgnoreCase))
                return;

            var tPlayer = player.TPlayer;
            int baseDef = CalculatePlayerEquipmentDefense(tPlayer);
            int reducedAmount = (int)Math.Floor(baseDef * SummonerBrokenArmorReduction);
            tPlayer.statDefense = Math.Max(0, baseDef - reducedAmount);
        }

        private void ApplyHoneyRegenBonus(TSPlayer player, string playerClass, double deltaSeconds)
        {
            if (player?.TPlayer == null || !player.Active || player.TPlayer.dead) return;

            if (!string.Equals(playerClass, "summoner", StringComparison.OrdinalIgnoreCase))
            {
                _honeyRegenAccumulator.Remove(player.Index);
                return;
            }

            var tPlayer = player.TPlayer;

            double baseHoneyHpPerSec = 3.0;
            if (HasBuff(player, BuffID.Campfire)) baseHoneyHpPerSec += 0.5;
            if (HasBuff(player, BuffID.HeartLamp)) baseHoneyHpPerSec += 1.0;
            if (HasBuff(player, BuffID.Regeneration)) baseHoneyHpPerSec += 2.0;

            double bonusHpPerSec = Math.Max(0.5, baseHoneyHpPerSec * HoneyBonusLifeRegenMultiplier);

            if (tPlayer.statLife > 0 && tPlayer.statLife < tPlayer.statLifeMax2 && deltaSeconds > 0)
            {
                _honeyRegenAccumulator.TryGetValue(player.Index, out double acc);
                acc += bonusHpPerSec * deltaSeconds;

                int healAmount = (int)Math.Floor(acc);
                if (healAmount > 0)
                {
                    acc -= healAmount;
                    int newLife = Math.Min(tPlayer.statLifeMax2, tPlayer.statLife + healAmount);
                    if (newLife > tPlayer.statLife)
                    {
                        tPlayer.statLife = newLife;
                        NetMessage.SendData((int)PacketTypes.PlayerHp, -1, -1, null, player.Index);
                        NetMessage.SendData((int)PacketTypes.PlayerHp, player.Index, -1, null, player.Index);
                    }
                }
                _honeyRegenAccumulator[player.Index] = acc;
            }
        }

        private void RegisterBlockedWorldItem(int playerIndex, int worldItemIndex)
        {
            if (playerIndex < 0 || worldItemIndex < 0 || worldItemIndex >= Main.maxItems) return;
            if (!_blockedWorldItemsByPlayer.TryGetValue(playerIndex, out var set))
            {
                set = new HashSet<int>();
                _blockedWorldItemsByPlayer[playerIndex] = set;
            }
            set.Add(worldItemIndex);
        }

        private bool IsBlockedWorldItem(int playerIndex, int worldItemIndex)
        {
            if (worldItemIndex < 0 || worldItemIndex >= Main.maxItems) return false;
            var wItem = Main.item[worldItemIndex];
            if (wItem == null || !wItem.active || wItem.type <= ItemID.None) return false;

            if (_blockedWorldItemsByPlayer.TryGetValue(playerIndex, out var set) && set.Contains(worldItemIndex))
            {
                return true;
            }

            var player = TShock.Players[playerIndex];
            if (player?.Account != null && player.IsLoggedIn)
            {
                string currentClass = GetPlayerClass(player);
                if (!string.IsNullOrEmpty(currentClass))
                {
                    Item checkItem = new Item();
                    checkItem.SetDefaults(wItem.type);
                    checkItem.stack = Math.Max(1, wItem.stack);
                    if (!IsItemAllowedForClass(checkItem, currentClass))
                    {
                        RegisterBlockedWorldItem(playerIndex, worldItemIndex);
                        return true;
                    }
                }
            }

            return false;
        }

        private void CleanupInactiveBlockedWorldItems(int playerIndex)
        {
            if (!_blockedWorldItemsByPlayer.TryGetValue(playerIndex, out var set) || set.Count == 0) return;
            var expired = new List<int>();
            foreach (int idx in set)
            {
                if (idx < 0 || idx >= Main.maxItems || Main.item[idx] == null || !Main.item[idx].active || Main.item[idx].type <= ItemID.None)
                {
                    expired.Add(idx);
                }
            }
            foreach (int idx in expired)
            {
                set.Remove(idx);
            }
        }

        private void OnItemDrop(object sender, GetDataHandlers.ItemDropEventArgs args)
        {
            var player = args.Player;
            if (player == null || !player.Active || !player.IsLoggedIn || player.Account == null) return;

            string currentClass = GetPlayerClass(player);
            if (string.IsNullOrEmpty(currentClass)) return;

            if (args.ID >= 0 && args.ID < Main.maxItems)
            {
                var worldItem = Main.item[args.ID];
                if (worldItem != null && worldItem.active && worldItem.type > ItemID.None)
                {
                    Item checkItem = new Item();
                    checkItem.SetDefaults(worldItem.type);
                    checkItem.stack = Math.Max(1, worldItem.stack);

                    if (!IsItemAllowedForClass(checkItem, currentClass))
                    {
                        RegisterBlockedWorldItem(player.Index, args.ID);
                    }
                }

                if (args.Stacks <= 0 && IsBlockedWorldItem(player.Index, args.ID))
                {
                    args.Handled = true;
                    var wItem = Main.item[args.ID];
                    if (wItem != null && wItem.active && wItem.type > ItemID.None)
                    {
                        wItem.playerIndexTheItemIsReservedFor = 255;
                    }
                    NetMessage.SendData((int)PacketTypes.ItemDrop, -1, -1, null, args.ID);
                    NetMessage.SendData((int)PacketTypes.ItemOwner, -1, -1, null, args.ID);
                    string itemName = (wItem != null && wItem.type > ItemID.None) ? Lang.GetItemNameValue(wItem.type) : "item";
                    NotifyBlockedPickup(player, itemName);
                }
            }
        }

        private int GetMaxAllowedBaseLife()
        {
            int crystals = Math.Max(0, MaxLifeCrystalsAllowed);
            return BaseStartHp + (crystals * HpPerLifeCrystal);
        }

        private bool IsLifeCrystalAtLimit(TSPlayer player)
        {
            if (player?.TPlayer == null) return false;
            int maxAllowedBaseLife = GetMaxAllowedBaseLife();
            int currentBaseLife = Math.Max(BaseStartHp, player.TPlayer.statLifeMax);
            return currentBaseLife >= maxAllowedBaseLife;
        }

        private void CancelActivePlayerProjectiles(TSPlayer player)
        {
            if (player == null) return;
            for (int i = 0; i < Main.maxProjectiles; i++)
            {
                var proj = Main.projectile[i];
                if (proj != null && proj.active && proj.owner == player.Index && proj.friendly)
                {
                    proj.active = false;
                    NetMessage.SendData((int)PacketTypes.ProjectileDestroy, -1, -1, null, i, player.Index);
                }
            }
        }

        private void BlockLifeCrystalUsage(TSPlayer player)
        {
            if (player?.TPlayer == null) return;
            player.TPlayer.controlUseItem = false;
            player.TPlayer.controlUseTile = false;
            player.TPlayer.itemAnimation = 0;
            player.TPlayer.itemAnimationMax = 0;
            player.TPlayer.itemTime = 0;
        }

        private int SpawnDroppedItemOnGround(TSPlayer player, int itemType, int stack, byte prefix)
        {
            if (player?.TPlayer == null || itemType <= ItemID.None) return -1;

            int droppedIndex = Item.NewItem(
                new MiscEntitySource(),
                player.TPlayer.position,
                itemType,
                Math.Max(1, stack)
            );

            if (droppedIndex >= 0 && droppedIndex < Main.maxItems)
            {
                float dir = player.TPlayer.direction != 0 ? player.TPlayer.direction : 1f;
                Main.item[droppedIndex].prefix = prefix;
                Main.item[droppedIndex].playerIndexTheItemIsReservedFor = 255;
                Main.item[droppedIndex].velocity = new Vector2(dir * 3.5f, -2.5f);

                NetMessage.SendData((int)PacketTypes.ItemDrop, -1, -1, null, droppedIndex);
                NetMessage.SendData((int)PacketTypes.ItemOwner, -1, -1, null, droppedIndex);
                RegisterBlockedWorldItem(player.Index, droppedIndex);
            }

            return droppedIndex;
        }

        private void NotifyBlockedPickup(TSPlayer player, string itemName)
        {
            if (player == null) return;
            if (!_dropWarnCooldown.TryGetValue(player.Index, out DateTime lastWarn) || (DateTime.UtcNow - lastWarn).TotalSeconds >= 2)
            {
                _dropWarnCooldown[player.Index] = DateTime.UtcNow;
                string className = GetPlayerClass(player) ?? "class";
                player.SendWarningMessage($"[ClassSystem] Dropped '{itemName}' onto the ground (not deleted) — {className.ToUpper()} cannot use or pick up items from other classes.");
            }
        }

        private bool CanEjectSlotNow(int playerIndex, int netSlotIndex)
        {
            int key = (playerIndex * 1000) + Math.Max(0, netSlotIndex);
            if (_slotEjectCooldown.TryGetValue(key, out DateTime lastTime) && (DateTime.UtcNow - lastTime).TotalMilliseconds < 350)
            {
                return false;
            }
            _slotEjectCooldown[key] = DateTime.UtcNow;
            return true;
        }

        private void EjectDisallowedItemSafely(TSPlayer player, int netSlotIndex, Item itemToDrop, int fallbackType, int fallbackStack, byte fallbackPrefix = 0)
        {
            if (player?.TPlayer == null) return;
            if (!CanEjectSlotNow(player.Index, netSlotIndex)) return;

            int itemType = itemToDrop != null && !itemToDrop.IsAir && itemToDrop.type > ItemID.None
                ? itemToDrop.type
                : fallbackType;

            if (itemType <= ItemID.None) return;

            int stack = itemToDrop != null && itemToDrop.stack > 0
                ? itemToDrop.stack
                : Math.Max(1, fallbackStack);

            byte prefix = itemToDrop != null && itemToDrop.prefix > 0 ? itemToDrop.prefix : fallbackPrefix;
            string itemName = itemToDrop != null && !string.IsNullOrWhiteSpace(itemToDrop.Name)
                ? itemToDrop.Name
                : Lang.GetItemNameValue(itemType);

            player.TPlayer.controlUseItem = false;
            player.TPlayer.controlUseTile = false;
            player.TPlayer.itemAnimation = 0;
            player.TPlayer.itemAnimationMax = 0;
            player.TPlayer.itemTime = 0;
            CancelActivePlayerProjectiles(player);

            // เสกไอเทมตกพื้นก่อนเสมอ เพื่อไม่ให้ไอเทมหายหากเกิดการขัดข้องใดๆ
            int droppedIdx = SpawnDroppedItemOnGround(player, itemType, stack, prefix);
            if (droppedIdx < 0) return;

            if (netSlotIndex >= 0)
            {
                ClearPlayerNetSlot(player, netSlotIndex);
            }

            NotifyBlockedPickup(player, itemName);
        }

        private void ClearPlayerNetSlot(TSPlayer player, int netSlotIndex)
        {
            var tPlayer = player.TPlayer;
            if (netSlotIndex >= 0 && netSlotIndex < 59)
            {
                tPlayer.inventory[netSlotIndex] = new Item();
                tPlayer.inventory[netSlotIndex].TurnToAir();
            }
            else if (netSlotIndex >= 59 && netSlotIndex < 79)
            {
                int armorIdx = netSlotIndex - 59;
                if (armorIdx < tPlayer.armor.Length)
                {
                    tPlayer.armor[armorIdx] = new Item();
                    tPlayer.armor[armorIdx].TurnToAir();
                }
            }
            else if (netSlotIndex >= 79 && netSlotIndex < 89)
            {
                int dyeIdx = netSlotIndex - 79;
                if (dyeIdx < tPlayer.dye.Length)
                {
                    tPlayer.dye[dyeIdx] = new Item();
                    tPlayer.dye[dyeIdx].TurnToAir();
                }
            }
            else if (netSlotIndex >= 89 && netSlotIndex < 94)
            {
                int miscIdx = netSlotIndex - 89;
                if (miscIdx < tPlayer.miscEquips.Length)
                {
                    tPlayer.miscEquips[miscIdx] = new Item();
                    tPlayer.miscEquips[miscIdx].TurnToAir();
                }
            }
            else if (netSlotIndex == 179)
            {
                tPlayer.trashItem = new Item();
                tPlayer.trashItem.TurnToAir();
            }

            NetMessage.SendData((int)PacketTypes.PlayerSlot, -1, -1, null, player.Index, netSlotIndex, 0, 0, 0);
            NetMessage.SendData((int)PacketTypes.PlayerSlot, player.Index, -1, null, player.Index, netSlotIndex, 0, 0, 0);
        }

        private void EnforceAllowedInventory(TSPlayer player, string currentClass)
        {
            if (player?.TPlayer == null || string.IsNullOrEmpty(currentClass)) return;

            var tPlayer = player.TPlayer;

            for (int i = 0; i < tPlayer.inventory.Length && i < 59; i++)
            {
                Item item = tPlayer.inventory[i];
                if (item != null && !item.IsAir && item.type > ItemID.None && !IsItemAllowedForClass(item, currentClass))
                {
                    EjectDisallowedItemSafely(player, i, item, item.type, item.stack, item.prefix);
                }
            }

            for (int i = 0; i < 3 && i < tPlayer.armor.Length; i++)
            {
                Item armorItem = tPlayer.armor[i];
                if (armorItem != null && !armorItem.IsAir && armorItem.type > ItemID.None && !IsItemAllowedForClass(armorItem, currentClass))
                {
                    EjectDisallowedItemSafely(player, 59 + i, armorItem, armorItem.type, armorItem.stack, armorItem.prefix);
                }
            }

            if (tPlayer.trashItem != null && !tPlayer.trashItem.IsAir && tPlayer.trashItem.type > ItemID.None && !IsItemAllowedForClass(tPlayer.trashItem, currentClass))
            {
                EjectDisallowedItemSafely(player, 179, tPlayer.trashItem, tPlayer.trashItem.type, tPlayer.trashItem.stack, tPlayer.trashItem.prefix);
            }
        }

        private void EnsureSummonerSummoningBuff(TSPlayer player)
        {
            if (player == null || player.TPlayer == null || !player.Active || player.TPlayer.dead) return;

            if (_summonBuffCooldown.TryGetValue(player.Index, out DateTime lastCd) && (DateTime.UtcNow - lastCd).TotalSeconds < 2.0)
                return;

            bool hasBuff = false;
            for (int i = 0; i < Player.maxBuffs; i++)
            {
                if (player.TPlayer.buffType[i] == BuffID.Summoning && player.TPlayer.buffTime[i] > 60)
                {
                    hasBuff = true;
                    break;
                }
            }

            if (!hasBuff)
            {
                _summonBuffCooldown[player.Index] = DateTime.UtcNow;
                player.SetBuff(BuffID.Summoning, 240, true);
            }
        }

        private Vector2 GetSurfaceSpawnPosition()
        {
            int tileX = Main.spawnTileX > 0 ? Main.spawnTileX : Main.maxTilesX / 2;
            int tileY = Main.spawnTileY > 0 ? Main.spawnTileY - 3 : (int)(Main.worldSurface * 0.35);
            return new Vector2(tileX * 16f, tileY * 16f);
        }

        private Vector2 GetUnderworldPosition()
        {
            int tileX = Main.maxTilesX / 2;
            int tileY = Math.Max(100, Main.maxTilesY - 60);
            return new Vector2(tileX * 16f, tileY * 16f);
        }

        private void ClearWebBuff(TSPlayer player)
        {
            if (player == null || player.TPlayer == null) return;
            bool removed = false;
            for (int i = 0; i < Player.maxBuffs; i++)
            {
                if (player.TPlayer.buffType[i] == BuffID.Webbed)
                {
                    player.TPlayer.buffType[i] = 0;
                    player.TPlayer.buffTime[i] = 0;
                    removed = true;
                }
            }
            if (removed)
            {
                NetMessage.SendData((int)PacketTypes.PlayerBuff, -1, -1, null, player.Index);
                NetMessage.SendData((int)PacketTypes.PlayerBuff, player.Index, -1, null, player.Index);
            }
        }

        private void ApplyUnderworldLock(TSPlayer player, bool applyWeb)
        {
            if (player == null || !player.Active) return;

            Vector2 hellPos = GetUnderworldPosition();
            _frozenPositions[player.Index] = hellPos;

            if (Vector2.Distance(player.TPlayer.position, hellPos) > 64f)
            {
                player.TPlayer.velocity = Vector2.Zero;
                player.Teleport(hellPos.X, hellPos.Y);
            }

            if (!player.IsLoggedIn)
            {
                ClearWebBuff(player);
                return;
            }

            if (applyWeb)
            {
                player.SetBuff(BuffID.Webbed, 180, true);
            }
            else
            {
                ClearWebBuff(player);
            }
        }

        private void ClearWebAndTeleportToSpawn(TSPlayer player)
        {
            if (player == null) return;

            _frozenPositions.Remove(player.Index);
            ClearWebBuff(player);

            Vector2 spawn = GetSurfaceSpawnPosition();
            player.TPlayer.velocity = Vector2.Zero;
            player.TPlayer.fallStart = (int)(spawn.Y / 16f);
            player.Teleport(spawn.X, spawn.Y);
        }

        private void SetupDatabase()
        {
            try
            {
                string saveDir = string.IsNullOrWhiteSpace(TShock.SavePath) ? "tshock" : TShock.SavePath;
                Directory.CreateDirectory(saveDir);
                string dbPath = Path.Combine(saveDir, "ClassSystem.sqlite");

                _db = new SqliteConnection($"Data Source={dbPath}");
                _db.Open();

                ExecuteNonQuery("CREATE TABLE IF NOT EXISTS PlayerClasses (AccountID INTEGER PRIMARY KEY, ClassName TEXT NOT NULL)");
                _dbReady = true;
            }
            catch (Exception ex)
            {
                _dbReady = false;
                TShock.Log.Error($"[ClassSystem] SetupDatabase error: {ex.Message}");
            }
        }

        private void EnsureConnectionOpen()
        {
            if (_db == null) return;
            if (_db.State != System.Data.ConnectionState.Open)
            {
                _db.Open();
            }
        }

        private void ExecuteNonQuery(string sql, params (string Name, object Value)[] parameters)
        {
            if (_db == null) return;
            EnsureConnectionOpen();
            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = sql;
                foreach (var (name, value) in parameters)
                {
                    var p = cmd.CreateParameter();
                    p.ParameterName = name;
                    p.Value = value ?? DBNull.Value;
                    cmd.Parameters.Add(p);
                }
                cmd.ExecuteNonQuery();
            }
        }

        private void OnGameInitialize(EventArgs args)
        {
            if (!_dbReady)
            {
                SetupDatabase();
            }

            Commands.ChatCommands.Add(new Command("", CmdClass, "class", "c")
            {
                HelpText = "Select or view your class: /class [classname] or /c <Me|Rg|Mg|sm>"
            });

            Commands.ChatCommands.Add(new Command("class.admin", CmdSetClass, "setclass")
            {
                HelpText = "Admin command to change or reset a player's class: /setclass <player> <classname|none>"
            });
        }

        private void OnPlayerCommand(PlayerCommandEventArgs args)
        {
            if (args.Player == null || string.IsNullOrWhiteSpace(args.CommandName)) return;
            string cmdName = args.CommandName.ToLowerInvariant();
            if (cmdName == "login" || cmdName == "register")
            {
                ClearWebBuff(args.Player);
            }
        }

        private void OnGreetPlayer(GreetPlayerEventArgs args)
        {
            var player = TShock.Players[args.Who];
            if (player != null && !player.IsLoggedIn)
            {
                ClearWebBuff(player);
                ApplyUnderworldLock(player, false);
                player.SendWarningMessage("[ClassSystem] Welcome to the Underworld! Log in with /login and pick a class with /c <Me|Rg|Mg|sm> or /class <warrior|ranger|mage|summoner>.");
            }
        }

        private void OnPlayerPostLogin(PlayerPostLoginEventArgs args)
        {
            if (args.Player?.Account == null) return;

            ClearWebBuff(args.Player);

            int accountId = args.Player.Account.ID;
            string savedClass = GetClassFromDB(accountId);

            if (!string.IsNullOrEmpty(savedClass))
            {
                _playerClasses[accountId] = savedClass;
                ClearWebAndTeleportToSpawn(args.Player);
                EnforceAllowedInventory(args.Player, savedClass);
                _lastSyncedMaxLife.Remove(args.Player.Index);
                ApplyClassStats(args.Player, savedClass);
                if (savedClass == "summoner")
                {
                    EnsureSummonerSummoningBuff(args.Player);
                    EnsureSummonerHoneyBuff(args.Player);
                    EnsureSummonerPermanentBrokenArmorBuff(args.Player);
                }
                args.Player.SendSuccessMessage($"[ClassSystem] Welcome back! Your class is: {savedClass.ToUpper()}");
            }
            else
            {
                ApplyUnderworldLock(args.Player, true);
                args.Player.SendWarningMessage("[ClassSystem] You are webbed in the Underworld until you select a class using /c <Me|Rg|Mg|sm> or /class <warrior|ranger|mage|summoner>.");
            }
        }

        private void OnServerLeave(LeaveEventArgs args)
        {
            var player = TShock.Players[args.Who];
            if (player?.Account != null)
            {
                _playerClasses.Remove(player.Account.ID);
            }
            _frozenPositions.Remove(args.Who);
            _warnCooldown.Remove(args.Who);
            _dropWarnCooldown.Remove(args.Who);
            _crystalWarnCooldown.Remove(args.Who);
            _summonBuffCooldown.Remove(args.Who);
            _honeyBuffCooldown.Remove(args.Who);
            _brokenArmorBuffCooldown.Remove(args.Who);
            _lastSyncedMaxLife.Remove(args.Who);
            _honeyRegenAccumulator.Remove(args.Who);
            _blockedWorldItemsByPlayer.Remove(args.Who);

            var keysToRemove = new List<int>();
            foreach (var kv in _slotEjectCooldown)
            {
                if (kv.Key / 1000 == args.Who)
                {
                    keysToRemove.Add(kv.Key);
                }
            }
            foreach (int k in keysToRemove)
            {
                _slotEjectCooldown.Remove(k);
            }
        }

        private bool IsGatheringTool(Item item)
        {
            if (item == null || item.IsAir || item.type == ItemID.None) return false;
            return item.pick > 0 || item.axe > 0 || item.hammer > 0;
        }

        private bool IsCoinOrAmmoOrMisc(Item item)
        {
            if (item == null || item.IsAir || item.type == ItemID.None) return true;

            if (item.type == ItemID.CopperCoin ||
                item.type == ItemID.SilverCoin ||
                item.type == ItemID.GoldCoin ||
                item.type == ItemID.PlatinumCoin)
            {
                return true;
            }

            if (item.ammo > 0 || item.useStyle == ItemUseStyleID.None)
            {
                return true;
            }

            return false;
        }

        private bool IsItemAllowedForClass(Item item, string className)
        {
            if (item == null || item.IsAir || item.type == ItemID.None) return true;

            if (item.accessory) return true;

            if (IsCoinOrAmmoOrMisc(item)) return true;

            if (IsGatheringTool(item)) return true;

            if (_restrictedArmors.TryGetValue(item.type, out string reqArmorClass))
            {
                return string.Equals(className, reqArmorClass, StringComparison.OrdinalIgnoreCase);
            }

            bool isClassWeapon = item.melee || item.ranged || item.magic || item.summon;
            if (!isClassWeapon || item.damage <= 0) return true;

            switch (className.ToLowerInvariant())
            {
                case "warrior":
                    return item.melee;
                case "ranger":
                    return item.ranged;
                case "mage":
                    return item.magic;
                case "summoner":
                    return item.summon;
                default:
                    return false;
            }
        }

        private void NotifyBlockedLifeCrystal(TSPlayer player)
        {
            if (player == null) return;
            if (!_crystalWarnCooldown.TryGetValue(player.Index, out DateTime lastCrystalWarn) || (DateTime.UtcNow - lastCrystalWarn).TotalSeconds >= 3)
            {
                _crystalWarnCooldown[player.Index] = DateTime.UtcNow;
                player.SendWarningMessage($"[ClassSystem] Life Crystal usage blocked — limit is {MaxLifeCrystalsAllowed} crystals ({GetMaxAllowedBaseLife()} base HP). Item kept in inventory.");
            }
        }

        private void OnPlayerUpdate(object sender, GetDataHandlers.PlayerUpdateEventArgs args)
        {
            var player = TShock.Players[args.PlayerId];
            if (player == null || !player.Active) return;

            if (!player.IsLoggedIn)
            {
                ApplyUnderworldLock(player, false);
                if (!_warnCooldown.TryGetValue(player.Index, out DateTime lastWarn) || (DateTime.UtcNow - lastWarn).TotalSeconds >= 5)
                {
                    _warnCooldown[player.Index] = DateTime.UtcNow;
                    player.SendWarningMessage("[ClassSystem] Please log in with /login first to escape the Underworld.");
                }
                return;
            }

            if (player.Account != null)
            {
                string currentClass = GetPlayerClass(player);
                if (string.IsNullOrEmpty(currentClass))
                {
                    ApplyUnderworldLock(player, true);
                    if (!_warnCooldown.TryGetValue(player.Index, out DateTime lastWarn) || (DateTime.UtcNow - lastWarn).TotalSeconds >= 4)
                    {
                        _warnCooldown[player.Index] = DateTime.UtcNow;
                        player.SendWarningMessage("[ClassSystem] Select a class using /c <Me|Rg|Mg|sm> or /class <warrior|ranger|mage|summoner> to break the web and leave the Underworld.");
                    }
                }
                else
                {
                    _frozenPositions.Remove(player.Index);
                    CleanupInactiveBlockedWorldItems(player.Index);
                    Item held = player.TPlayer.HeldItem;

                    if (held != null && !held.IsAir && held.type == ItemID.LifeCrystal && IsLifeCrystalAtLimit(player))
                    {
                        BlockLifeCrystalUsage(player);
                        NotifyBlockedLifeCrystal(player);
                    }
                    else
                    {
                        EnforceAllowedInventory(player, currentClass);
                    }
                }
            }
        }

        private void OnPlayerSlot(object sender, GetDataHandlers.PlayerSlotEventArgs args)
        {
            var player = TShock.Players[args.PlayerId];
            if (player == null || !player.Active || !player.IsLoggedIn || player.Account == null) return;

            string currentClass = GetPlayerClass(player);
            if (string.IsNullOrEmpty(currentClass)) return;

            if (args.Type == ItemID.LifeCrystal && IsLifeCrystalAtLimit(player))
            {
                args.Handled = true;
                BlockLifeCrystalUsage(player);
                NotifyBlockedLifeCrystal(player);
                return;
            }

            if (args.Type > ItemID.None && args.Stack > 0)
            {
                Item testItem = new Item();
                testItem.SetDefaults(args.Type);
                testItem.stack = Math.Max(1, (int)args.Stack);
                testItem.prefix = (byte)args.Prefix;

                if (!IsItemAllowedForClass(testItem, currentClass))
                {
                    args.Handled = true;
                    EjectDisallowedItemSafely(player, args.Slot, testItem, args.Type, args.Stack, (byte)args.Prefix);
                }
            }
        }

        private void OnGameUpdate(EventArgs args)
        {
            DateTime now = DateTime.UtcNow;
            double honeyDeltaSec = Math.Min(1.0, (now - _lastHoneyTick).TotalSeconds);
            _lastHoneyTick = now;

            bool runPeriodicSync = (now - _lastTickSync).TotalMilliseconds >= 100;
            if (runPeriodicSync)
            {
                _lastTickSync = now;
            }

            foreach (var player in TShock.Players)
            {
                if (player == null || !player.Active) continue;

                if (!player.IsLoggedIn || player.Account == null)
                {
                    _honeyRegenAccumulator.Remove(player.Index);
                    if (runPeriodicSync)
                    {
                        ApplyUnderworldLock(player, false);
                    }
                    continue;
                }

                string playerClass = GetPlayerClass(player);
                if (string.IsNullOrEmpty(playerClass))
                {
                    _honeyRegenAccumulator.Remove(player.Index);
                    if (runPeriodicSync)
                    {
                        ApplyUnderworldLock(player, true);
                    }
                    continue;
                }

                ApplyHoneyRegenBonus(player, playerClass, honeyDeltaSec);
                ApplyClassStats(player, playerClass);
                ApplySummonerPermanentBrokenArmorEffect(player, playerClass);

                if (runPeriodicSync)
                {
                    ClearWebBuff(player);
                    CleanupInactiveBlockedWorldItems(player.Index);
                    EnforceAllowedInventory(player, playerClass);

                    if (playerClass == "summoner")
                    {
                        EnsureSummonerSummoningBuff(player);
                        EnsureSummonerHoneyBuff(player);
                        EnsureSummonerPermanentBrokenArmorBuff(player);
                    }
                }
            }
        }

        private void OnNpcStrike(NpcStrikeEventArgs args)
        {
            if (args.Player == null || args.Npc == null) return;
            int playerIndex = Array.IndexOf(Main.player, args.Player);
            if (playerIndex < 0 || playerIndex >= TShock.Players.Length) return;

            var tsPlayer = TShock.Players[playerIndex];
            if (tsPlayer == null || !tsPlayer.IsLoggedIn || tsPlayer.Account == null) return;

            string playerClass = GetPlayerClass(tsPlayer);
            if (string.IsNullOrEmpty(playerClass)) return;

            var heldItem = args.Player.HeldItem;

            if (heldItem != null && !heldItem.IsAir && !IsItemAllowedForClass(heldItem, playerClass))
            {
                args.Damage = 0;
                EnforceAllowedInventory(tsPlayer, playerClass);
            }
        }

        private void GiveStarterItems(TSPlayer player, string className)
        {
            if (!_classStarterItems.TryGetValue(className, out var items)) return;

            foreach (var (itemId, stack) in items)
            {
                Item item = new Item();
                item.SetDefaults(itemId);
                item.stack = stack;

                int placedSlot = -1;
                for (int i = 0; i < 50; i++)
                {
                    if (player.TPlayer.inventory[i] == null || player.TPlayer.inventory[i].IsAir || player.TPlayer.inventory[i].type == ItemID.None)
                    {
                        player.TPlayer.inventory[i] = item;
                        placedSlot = i;
                        break;
                    }
                }

                if (placedSlot >= 0)
                {
                    NetMessage.SendData((int)PacketTypes.PlayerSlot, -1, -1, null, player.Index, placedSlot, item.prefix, 0, 0);
                    NetMessage.SendData((int)PacketTypes.PlayerSlot, player.Index, -1, null, player.Index, placedSlot, item.prefix, 0, 0);
                }
                else
                {
                    player.GiveItem(itemId, stack);
                }
            }
        }

        private string ResolveClassName(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;
            string trimmed = input.Trim().ToLowerInvariant();
            if (_classShortcuts.TryGetValue(trimmed, out string mapped))
            {
                return mapped;
            }
            if (_availableClasses.Contains(trimmed))
            {
                return trimmed;
            }
            return null;
        }

        private void CmdClass(CommandArgs args)
        {
            if (!args.Player.IsLoggedIn || args.Player.Account == null)
            {
                args.Player.SendErrorMessage("[ClassSystem] Please log in first.");
                return;
            }

            string currentClass = GetPlayerClass(args.Player);
            if (args.Parameters.Count == 0)
            {
                string info = string.IsNullOrEmpty(currentClass)
                    ? "None (use /c <Me|Rg|Mg|sm> or /class <warrior|ranger|mage|summoner>)"
                    : currentClass.ToUpper();
                args.Player.SendInfoMessage($"[ClassSystem] Current class: {info}");
                return;
            }

            string chosen = ResolveClassName(args.Parameters[0]);
            if (string.IsNullOrEmpty(chosen))
            {
                args.Player.SendErrorMessage("[ClassSystem] Invalid class. Use /c <Me|Rg|Mg|sm> or /class <warrior|ranger|mage|summoner>");
                return;
            }

            if (!string.IsNullOrEmpty(currentClass))
            {
                args.Player.SendErrorMessage($"[ClassSystem] Your class is already set to {currentClass.ToUpper()}. Ask an admin to reset it.");
                return;
            }

            SaveClassToDB(args.Player.Account.ID, chosen);
            _playerClasses[args.Player.Account.ID] = chosen;

            ClearWebAndTeleportToSpawn(args.Player);
            EnforceAllowedInventory(args.Player, chosen);
            GiveStarterItems(args.Player, chosen);
            if (chosen == "summoner")
            {
                EnsureSummonerSummoningBuff(args.Player);
                EnsureSummonerHoneyBuff(args.Player);
                EnsureSummonerPermanentBrokenArmorBuff(args.Player);
            }
            _lastSyncedMaxLife.Remove(args.Player.Index);
            ApplyClassStats(args.Player, chosen);
            args.Player.SendSuccessMessage($"[ClassSystem] Class selected: {chosen.ToUpper()}! Web cleared, starter gear equipped, and Max HP synced.");
        }

        private void CmdSetClass(CommandArgs args)
        {
            if (args.Parameters.Count < 2)
            {
                args.Player.SendErrorMessage("Usage: /setclass <player> <warrior|ranger|mage|summoner|none>");
                return;
            }

            var targets = TSPlayer.FindByNameOrID(args.Parameters[0]);
            if (targets.Count == 0)
            {
                args.Player.SendErrorMessage("Player not found.");
                return;
            }

            var target = targets[0];
            if (!target.IsLoggedIn || target.Account == null)
            {
                args.Player.SendErrorMessage("Target player is not logged in.");
                return;
            }

            string option = args.Parameters[1].ToLowerInvariant();
            if (option == "none" || option == "reset")
            {
                DeleteClassFromDB(target.Account.ID);
                _playerClasses.Remove(target.Account.ID);
                _lastSyncedMaxLife.Remove(target.Index);
                ApplyUnderworldLock(target, true);
                target.SendWarningMessage("[ClassSystem] Your class has been reset. You are webbed in the Underworld until you select a class with /c <Me|Rg|Mg|sm> or /class.");
                args.Player.SendSuccessMessage($"Reset class for {target.Name}.");
                return;
            }

            string resolvedOption = ResolveClassName(option);
            if (string.IsNullOrEmpty(resolvedOption))
            {
                args.Player.SendErrorMessage("Invalid class. Use Me/warrior, Rg/ranger, Mg/mage, sm/summoner, or none.");
                return;
            }

            SaveClassToDB(target.Account.ID, resolvedOption);
            _playerClasses[target.Account.ID] = resolvedOption;
            ClearWebAndTeleportToSpawn(target);
            EnforceAllowedInventory(target, resolvedOption);
            GiveStarterItems(target, resolvedOption);
            if (resolvedOption == "summoner")
            {
                EnsureSummonerSummoningBuff(target);
                EnsureSummonerHoneyBuff(target);
                EnsureSummonerPermanentBrokenArmorBuff(target);
            }
            _lastSyncedMaxLife.Remove(target.Index);
            ApplyClassStats(target, resolvedOption);
            target.SendSuccessMessage($"[ClassSystem] Admin set your class to {resolvedOption.ToUpper()}.");
            args.Player.SendSuccessMessage($"Set {target.Name}'s class to {resolvedOption.ToUpper()}.");
        }

        private string GetPlayerClass(TSPlayer player)
        {
            if (player?.Account == null) return null;
            if (_playerClasses.TryGetValue(player.Account.ID, out string cached)) return cached;
            string fromDb = GetClassFromDB(player.Account.ID);
            if (!string.IsNullOrEmpty(fromDb))
            {
                _playerClasses[player.Account.ID] = fromDb;
            }
            return fromDb;
        }

        private void ApplyClassStats(TSPlayer player, string className)
        {
            var tPlayer = player.TPlayer;
            int maxAllowedBaseLife = GetMaxAllowedBaseLife();
            int rawBaseLife = tPlayer.statLifeMax > 0 ? tPlayer.statLifeMax : BaseStartHp;
            int currentBaseLife = Math.Min(Math.Max(BaseStartHp, rawBaseLife), maxAllowedBaseLife);

            int targetMaxLife = currentBaseLife;

            if (className == "warrior")
            {
                targetMaxLife = Math.Max(40, currentBaseLife + WarriorMaxHpFlat);
            }
            else if (className == "ranger")
            {
                targetMaxLife = Math.Max(40, currentBaseLife + RangerMaxHpFlat);
            }
            else if (className == "mage")
            {
                targetMaxLife = Math.Max(40, currentBaseLife + MageMaxHpFlat);
            }
            else if (className == "summoner")
            {
                targetMaxLife = Math.Max(20, currentBaseLife + SummonerMaxHpFlat);
            }

            tPlayer.statLifeMax = targetMaxLife;
            tPlayer.statLifeMax2 = targetMaxLife;

            if (tPlayer.statLife > targetMaxLife)
            {
                tPlayer.statLife = targetMaxLife;
            }

            if (!_lastSyncedMaxLife.TryGetValue(player.Index, out int lastMaxLife) || lastMaxLife != targetMaxLife)
            {
                _lastSyncedMaxLife[player.Index] = targetMaxLife;
                NetMessage.SendData((int)PacketTypes.PlayerHp, -1, -1, null, player.Index);
                NetMessage.SendData((int)PacketTypes.PlayerHp, player.Index, -1, null, player.Index);
            }
        }

        private string GetClassFromDB(int accountId)
        {
            if (_db == null) return null;
            try
            {
                EnsureConnectionOpen();
                using (var cmd = _db.CreateCommand())
                {
                    cmd.CommandText = "SELECT ClassName FROM PlayerClasses WHERE AccountID = @0";
                    var p = cmd.CreateParameter();
                    p.ParameterName = "@0";
                    p.Value = accountId;
                    cmd.Parameters.Add(p);

                    using (var reader = cmd.ExecuteReader())
                    {
                        if (reader.Read() && !reader.IsDBNull(0))
                        {
                            return reader.GetString(0);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                TShock.Log.Error($"[ClassSystem] GetClassFromDB error: {ex.Message}");
            }
            return null;
        }

        private void SaveClassToDB(int accountId, string className)
        {
            try
            {
                ExecuteNonQuery(
                    "INSERT INTO PlayerClasses (AccountID, ClassName) VALUES (@0, @1) ON CONFLICT(AccountID) DO UPDATE SET ClassName = excluded.ClassName",
                    ("@0", accountId), ("@1", className));
            }
            catch (Exception ex)
            {
                TShock.Log.Error($"[ClassSystem] SaveClassToDB error: {ex.Message}");
            }
        }

        private void DeleteClassFromDB(int accountId)
        {
            try
            {
                ExecuteNonQuery("DELETE FROM PlayerClasses WHERE AccountID = @0", ("@0", accountId));
            }
            catch (Exception ex)
            {
                TShock.Log.Error($"[ClassSystem] DeleteClassFromDB error: {ex.Message}");
            }
        }
    }
}
