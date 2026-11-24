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

    internal sealed class PlayerClassState
    {
        public string ClassName { get; set; }
        public int StarterChoice { get; set; } // 0 = ยังไม่เลือกไอเทม, 1 = /ch 1, 2 = /ch 2
        public int BaseLife { get; set; } = 100; // เลือดพื้นฐานก่อนบวก/ลบโบนัสสาย (เริ่มต้น 100, เพิ่มทีละ 20 จาก Life Crystal สูงสุด 400)
    }

    [ApiVersion(2, 1)]
    public class ClassSystem : TerrariaPlugin
    {
        public override string Name => "ClassSystem";
        public override string Author => "CustomDev";
        public override string Description => "Class & starter-choice (/ch 1|2) system with persistent SQLite BaseLife across relogs, strict 15 Life Crystal cap & refund on excess use, proper death/respawn state tracking, cross-class item ejection away from the player, and zero-flicker permanent buffs/debuffs.";
        public override Version Version => new Version(17, 25, 0);

        private const string ClassSelectPrompt =
            "Chat by Shokuya :3 Please select your class >w<\n\n" +
            "Melee = Me\n" +
            "Ranger = Rg\n" +
            "Magic = Mg\n" +
            "summon = Sm\n\n" +
            "/c and Me Rg Mg Sm";

        private SqliteConnection _db;
        private bool _dbReady;
        private readonly Dictionary<int, PlayerClassState> _playerStates = new Dictionary<int, PlayerClassState>();
        private readonly Dictionary<int, Vector2> _frozenPositions = new Dictionary<int, Vector2>();
        private readonly Dictionary<int, DateTime> _warnCooldown = new Dictionary<int, DateTime>();
        private readonly Dictionary<int, DateTime> _serverBroadcastCooldown = new Dictionary<int, DateTime>();
        private readonly Dictionary<int, string> _lastBroadcastPhase = new Dictionary<int, string>();
        private readonly Dictionary<int, DateTime> _dropWarnCooldown = new Dictionary<int, DateTime>();
        private readonly Dictionary<int, DateTime> _craftWarnCooldown = new Dictionary<int, DateTime>();
        private readonly Dictionary<long, DateTime> _itemTypeEjectCooldown = new Dictionary<long, DateTime>();
        private readonly Dictionary<int, DateTime> _crystalWarnCooldown = new Dictionary<int, DateTime>();
        private readonly Dictionary<int, DateTime> _crystalUseCooldown = new Dictionary<int, DateTime>();
        private readonly Dictionary<int, int> _lastLifeCrystalSlot = new Dictionary<int, int>();
        private readonly Dictionary<int, DateTime> _buffRefreshCooldown = new Dictionary<int, DateTime>();
        private readonly Dictionary<int, DateTime> _lockedBuffRestoreCooldown = new Dictionary<int, DateTime>();
        private readonly Dictionary<int, DateTime> _legacyBuffPurgeCooldown = new Dictionary<int, DateTime>();
        private readonly Dictionary<int, int> _lastSyncedMaxLife = new Dictionary<int, int>();
        private readonly Dictionary<int, int> _playerBaseLife = new Dictionary<int, int>();
        private readonly Dictionary<int, DateTime> _loginLifeLockUntil = new Dictionary<int, DateTime>();
        private readonly Dictionary<int, double> _honeyRegenAccumulator = new Dictionary<int, double>();
        private readonly Dictionary<int, HashSet<int>> _blockedWorldItemsByPlayer = new Dictionary<int, HashSet<int>>();
        private readonly Dictionary<int, DateTime> _playerDeadUntil = new Dictionary<int, DateTime>();
        private readonly HashSet<int> _pluginGrantedGodMode = new HashSet<int>();
        private Vector2? _cachedSafeUnderworldPos = null;
        private DateTime _lastTickSync = DateTime.MinValue;
        private DateTime _lastHoneyTick = DateTime.UtcNow;

        private const int MaxLifeCrystalsAllowed = 15;
        private const int BaseStartHp = 100;
        private const int HpPerLifeCrystal = 20;

        // ระยะเวลาบัฟ 15 นาที (54000 ticks) เพื่อให้นิ่งสนิทไม่เกิดการรีเฟรชถี่
        private const int PermanentBuffDurationTicks = 54000;

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
            ServerApi.Hooks.NetGetData.Register(this, OnNetGetData);

            PlayerHooks.PlayerCommand += OnPlayerCommand;
            PlayerHooks.PlayerPostLogin += OnPlayerPostLogin;
            GetDataHandlers.PlayerUpdate += OnPlayerUpdate;
            GetDataHandlers.PlayerSlot += OnPlayerSlot;
            GetDataHandlers.ItemDrop += OnItemDrop;
            GetDataHandlers.PlayerDamage += OnPlayerDamage;
            GetDataHandlers.KillMe += OnPlayerKillMe;
            GetDataHandlers.PlayerSpawn += OnPlayerSpawn;
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
                ServerApi.Hooks.NetGetData.Deregister(this, OnNetGetData);

                PlayerHooks.PlayerCommand -= OnPlayerCommand;
                PlayerHooks.PlayerPostLogin -= OnPlayerPostLogin;
                GetDataHandlers.PlayerUpdate -= OnPlayerUpdate;
                GetDataHandlers.PlayerSlot -= OnPlayerSlot;
                GetDataHandlers.ItemDrop -= OnItemDrop;
                GetDataHandlers.PlayerDamage -= OnPlayerDamage;
                GetDataHandlers.KillMe -= OnPlayerKillMe;
                GetDataHandlers.PlayerSpawn -= OnPlayerSpawn;

                if (_db != null)
                {
                    try { _db.Dispose(); } catch { }
                    _db = null;
                }
            }
            base.Dispose(disposing);
        }

        private bool IsInLoginLifeLock(TSPlayer player)
        {
            if (player == null) return false;
            if (_loginLifeLockUntil.TryGetValue(player.Index, out DateTime until))
            {
                if (DateTime.UtcNow < until) return true;
                _loginLifeLockUntil.Remove(player.Index);
            }
            return false;
        }

        private void ArmLoginLifeLock(TSPlayer player, double seconds = 3.0)
        {
            if (player == null) return;
            _loginLifeLockUntil[player.Index] = DateTime.UtcNow.AddSeconds(seconds);
        }

        private bool IsPlayerDeadState(TSPlayer player)
        {
            if (player?.TPlayer == null) return true;
            if (player.TPlayer.dead || player.TPlayer.statLife <= 0)
                return true;

            if (_playerDeadUntil.TryGetValue(player.Index, out DateTime deadUntil))
            {
                if (DateTime.UtcNow < deadUntil)
                    return true;
                _playerDeadUntil.Remove(player.Index);
            }
            return false;
        }

        private void MarkPlayerDead(TSPlayer player, double lockSeconds = 20.0)
        {
            if (player?.TPlayer == null) return;
            player.TPlayer.dead = true;
            player.TPlayer.statLife = 0;
            _playerDeadUntil[player.Index] = DateTime.UtcNow.AddSeconds(lockSeconds);
            _honeyRegenAccumulator.Remove(player.Index);
            _lastSyncedMaxLife.Remove(player.Index);
        }

        private void ClearPlayerDeadState(TSPlayer player)
        {
            if (player == null) return;
            _playerDeadUntil.Remove(player.Index);
            _honeyRegenAccumulator.Remove(player.Index);
            _lastSyncedMaxLife.Remove(player.Index);
        }

        private void OnPlayerKillMe(object sender, GetDataHandlers.KillMeEventArgs args)
        {
            var player = args.Player;
            if (player == null || !player.Active) return;

            if (IsPlayerInSelectionLock(player))
            {
                args.Handled = true;
                SetUnderworldGodMode(player, true);
                return;
            }

            MarkPlayerDead(player, 25.0);
        }

        private void OnPlayerSpawn(object sender, GetDataHandlers.SpawnEventArgs args)
        {
            var player = args.Player;
            if (player == null || !player.Active) return;

            ClearPlayerDeadState(player);

            if (player.IsLoggedIn && player.Account != null)
            {
                var dbState = GetStateFromDB(player.Account.ID);
                if (dbState != null && !string.IsNullOrEmpty(dbState.ClassName))
                {
                    int persistedBase = NormalizeBaseLifeValue(dbState.BaseLife);
                    dbState.BaseLife = persistedBase;
                    _playerStates[player.Account.ID] = dbState;
                    _playerBaseLife[player.Index] = persistedBase;
                    _lastSyncedMaxLife.Remove(player.Index);
                    ArmLoginLifeLock(player, 2.5);

                    if (dbState.StarterChoice > 0)
                    {
                        if (player.TPlayer != null)
                        {
                            player.TPlayer.dead = false;
                            int targetMaxLife = Math.Max(20, persistedBase + GetMaxHpDelta(dbState));
                            player.TPlayer.statLifeMax = targetMaxLife;
                            player.TPlayer.statLifeMax2 = targetMaxLife;
                            if (player.TPlayer.statLife <= 0 || player.TPlayer.statLife > targetMaxLife)
                            {
                                player.TPlayer.statLife = targetMaxLife;
                            }
                        }
                        ForceSyncLifeToClient(player, dbState);
                        EnsurePermanentClassBuffs(player, dbState, true);
                    }
                }
            }
        }

        private bool IsPlayerInSelectionLock(TSPlayer player)
        {
            if (player == null || !player.Active) return false;
            if (!player.IsLoggedIn || player.Account == null) return true;
            var state = GetPlayerState(player);
            if (state == null || string.IsNullOrEmpty(state.ClassName) || state.StarterChoice <= 0)
                return true;
            return false;
        }

        private void DisarmLockedPlayer(TSPlayer player)
        {
            if (player?.TPlayer == null) return;
            player.TPlayer.controlUseItem = false;
            player.TPlayer.itemAnimation = 0;
            player.TPlayer.itemAnimationMax = 0;
            player.TPlayer.itemTime = 0;
            CancelActivePlayerProjectiles(player);
        }

        private void SetUnderworldGodMode(TSPlayer player, bool enable)
        {
            if (player == null || !player.Active) return;

            if (enable)
            {
                ClearPlayerDeadState(player);
                if (!player.GodMode)
                {
                    player.GodMode = true;
                    _pluginGrantedGodMode.Add(player.Index);
                }

                if (player.TPlayer != null)
                {
                    player.TPlayer.dead = false;
                    player.TPlayer.immune = true;
                    player.TPlayer.immuneTime = 60;
                    player.TPlayer.breath = player.TPlayer.breathMax;
                    if (player.TPlayer.statLife < player.TPlayer.statLifeMax2)
                    {
                        player.TPlayer.statLife = player.TPlayer.statLifeMax2;
                        NetMessage.SendData((int)PacketTypes.PlayerHp, -1, -1, null, player.Index);
                        NetMessage.SendData((int)PacketTypes.PlayerHp, player.Index, -1, null, player.Index);
                    }
                }
            }
            else
            {
                if (_pluginGrantedGodMode.Remove(player.Index))
                {
                    player.GodMode = false;
                }
            }
        }

        private string GetStarterChoicePrompt(string className)
        {
            string c = (className ?? "").ToLowerInvariant();
            if (c == "warrior")
            {
                return "Please choose the origin of your ability :))))\n\n/ch 1  = sword\n/ch 2 = Boomerang";
            }
            if (c == "ranger")
            {
                return "Please choose the origin of your ability :))))\n\n/ch 1  = Bowe\n/ch 2 = Gus";
            }
            if (c == "mage")
            {
                return "Please choose the origin of your ability :))))\n\n/ch 1 = Wands\n/ch 2 = books";
            }
            if (c == "summoner")
            {
                return "Please choose the origin of your ability :))))\n\n/ch 1 = Summon\n/ch 2 = Whips";
            }
            return "Please choose the origin of your ability :))))\n\n/ch 1\n/ch 2";
        }

        private void BroadcastSelectionPhase(TSPlayer player, string phaseKey, string message, double cooldownSeconds = 15.0)
        {
            if (player == null || string.IsNullOrWhiteSpace(player.Name)) return;

            bool phaseChanged = !_lastBroadcastPhase.TryGetValue(player.Index, out string prevPhase) || prevPhase != phaseKey;
            bool cooldownExpired = !_serverBroadcastCooldown.TryGetValue(player.Index, out DateTime lastTime) || (DateTime.UtcNow - lastTime).TotalSeconds >= cooldownSeconds;

            if (phaseChanged || cooldownExpired)
            {
                _lastBroadcastPhase[player.Index] = phaseKey;
                _serverBroadcastCooldown[player.Index] = DateTime.UtcNow;
                TShock.Utils.Broadcast(message, Color.Gold);
            }
        }

        private int GetMaxHpDelta(PlayerClassState state)
        {
            if (state == null || string.IsNullOrEmpty(state.ClassName)) return 0;
            string c = state.ClassName.ToLowerInvariant();
            int ch = state.StarterChoice;

            if (c == "warrior")
            {
                if (ch == 1) return 60;
                if (ch == 2) return -40;
                return 0;
            }
            if (c == "ranger")
            {
                if (ch == 1 || ch == 2) return -40;
                return 0;
            }
            if (c == "mage")
            {
                if (ch == 1 || ch == 2) return -40;
                return 0;
            }
            if (c == "summoner")
            {
                if (ch == 1) return -80;
                if (ch == 2) return -40;
                return 0;
            }
            return 0;
        }

        private double GetBrokenArmorReduction(PlayerClassState state)
        {
            if (state == null || string.IsNullOrEmpty(state.ClassName) || state.StarterChoice <= 0)
                return 0.0;

            string c = state.ClassName.ToLowerInvariant();
            if (c == "ranger" && (state.StarterChoice == 1 || state.StarterChoice == 2))
                return 0.10;

            if (c == "mage" && (state.StarterChoice == 1 || state.StarterChoice == 2))
                return 0.10;

            return 0.0;
        }

        private double GetHoneyBonusMultiplier(PlayerClassState state)
        {
            if (state == null || string.IsNullOrEmpty(state.ClassName) || state.StarterChoice <= 0)
                return 0.0;

            if (string.Equals(state.ClassName, "summoner", StringComparison.OrdinalIgnoreCase) && state.StarterChoice == 1)
                return 0.20;

            return 0.0;
        }

        private int[] GetLockedBuffsForState(PlayerClassState state)
        {
            var list = new List<int>();
            if (state == null || string.IsNullOrEmpty(state.ClassName) || state.StarterChoice <= 0)
                return list.ToArray();

            string c = state.ClassName.ToLowerInvariant();
            int ch = state.StarterChoice;

            if (c == "warrior" && ch == 1)
            {
                list.Add(BuffID.Bleeding);
                list.Add(BuffID.Weak);
                list.Add(BuffID.Endurance);
            }

            return list.ToArray();
        }

        private int[] GetPermanentBuffsForState(PlayerClassState state, Player tPlayer)
        {
            var list = new List<int>();
            if (state == null || string.IsNullOrEmpty(state.ClassName) || state.StarterChoice <= 0)
                return list.ToArray();

            string c = state.ClassName.ToLowerInvariant();
            int ch = state.StarterChoice;

            if (c != "warrior")
            {
                list.Add(BuffID.Swiftness);
            }

            if (c == "warrior")
            {
                if (ch == 1)
                {
                    list.Add(BuffID.Ironskin);
                    list.Add(BuffID.Bleeding);
                    list.Add(BuffID.Weak);
                    list.Add(BuffID.Endurance);
                }
            }
            else if (c == "ranger")
            {
                bool hasImmunity = HasBrokenArmorImmunityItem(tPlayer);
                list.Add(hasImmunity ? BuffID.WitheredArmor : BuffID.BrokenArmor);
                if (ch == 1)
                {
                    list.Add(BuffID.Archery);
                }
                else if (ch == 2)
                {
                    list.Add(BuffID.AmmoReservation);
                }
            }
            else if (c == "mage")
            {
                bool hasImmunity = HasBrokenArmorImmunityItem(tPlayer);
                list.Add(hasImmunity ? BuffID.WitheredArmor : BuffID.BrokenArmor);
                if (ch == 1)
                {
                    list.Add(BuffID.MagicPower);
                }
                else if (ch == 2)
                {
                    list.Add(BuffID.ManaRegeneration);
                }
            }
            else if (c == "summoner")
            {
                if (ch == 1)
                {
                    list.Add(BuffID.Summoning);
                    list.Add(BuffID.Honey);
                }
            }

            return list.ToArray();
        }

        private void RestoreMissingLockedBuffs(TSPlayer player, PlayerClassState state, ICollection<int> missingBuffIds, bool bypassCooldown = false)
        {
            if (player?.TPlayer == null || !player.Active || IsPlayerDeadState(player)) return;
            if (missingBuffIds == null || missingBuffIds.Count == 0) return;

            if (!bypassCooldown &&
                _lockedBuffRestoreCooldown.TryGetValue(player.Index, out DateTime lastRestore) &&
                (DateTime.UtcNow - lastRestore).TotalMilliseconds < 400)
            {
                return;
            }

            _lockedBuffRestoreCooldown[player.Index] = DateTime.UtcNow;

            foreach (int buffId in missingBuffIds)
            {
                player.SetBuff(buffId, PermanentBuffDurationTicks, true);
            }
        }

        private void CleanDisallowedSpeedBuffs(TSPlayer player, PlayerClassState state, bool forceImmediate = false)
        {
            if (player?.TPlayer == null || !player.Active) return;

            bool shouldHaveSwiftness = state != null &&
                                       !string.IsNullOrEmpty(state.ClassName) &&
                                       state.StarterChoice > 0 &&
                                       !string.Equals(state.ClassName, "warrior", StringComparison.OrdinalIgnoreCase);

            if (shouldHaveSwiftness) return;

            bool foundDisallowedOnServer = false;
            for (int i = 0; i < Player.maxBuffs; i++)
            {
                if (player.TPlayer.buffType[i] == BuffID.Swiftness)
                {
                    player.TPlayer.buffType[i] = 0;
                    player.TPlayer.buffTime[i] = 0;
                    foundDisallowedOnServer = true;
                }
            }

            if (forceImmediate || foundDisallowedOnServer)
            {
                _legacyBuffPurgeCooldown[player.Index] = DateTime.UtcNow;
                NetMessage.SendData((int)PacketTypes.PlayerAddBuff, player.Index, -1, null, player.Index, BuffID.Swiftness, 0);
            }
        }

        private void EnsurePermanentClassBuffs(TSPlayer player, PlayerClassState state, bool forceInitial = false)
        {
            if (player == null || player.TPlayer == null || !player.Active || IsPlayerDeadState(player)) return;
            if (state == null || string.IsNullOrEmpty(state.ClassName) || state.StarterChoice <= 0) return;

            if (!forceInitial &&
                _buffRefreshCooldown.TryGetValue(player.Index, out DateTime lastCd) &&
                (DateTime.UtcNow - lastCd).TotalSeconds < 3.0)
            {
                return;
            }

            _buffRefreshCooldown[player.Index] = DateTime.UtcNow;

            int[] requiredBuffs = GetPermanentBuffsForState(state, player.TPlayer);
            if (requiredBuffs.Length == 0) return;

            foreach (int buffId in requiredBuffs)
            {
                bool exists = false;
                for (int i = 0; i < Player.maxBuffs; i++)
                {
                    if (player.TPlayer.buffType[i] == buffId)
                    {
                        exists = true;
                        if (player.TPlayer.buffTime[i] > 0 && player.TPlayer.buffTime[i] < 3600)
                        {
                            player.SetBuff(buffId, PermanentBuffDurationTicks, true);
                        }
                        break;
                    }
                }

                if (!exists)
                {
                    player.SetBuff(buffId, PermanentBuffDurationTicks, true);
                }
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

        private void RefundBlockedLifeCrystal(TSPlayer player)
        {
            if (player?.TPlayer == null) return;
            var tPlayer = player.TPlayer;

            int refundSlot = -1;

            if (tPlayer.inventory.Length > 58 &&
                tPlayer.inventory[58] != null &&
                !tPlayer.inventory[58].IsAir &&
                tPlayer.inventory[58].type == ItemID.LifeCrystal &&
                tPlayer.inventory[58].stack < tPlayer.inventory[58].maxStack)
            {
                refundSlot = 58;
            }
            else if (tPlayer.selectedItem >= 0 && tPlayer.selectedItem < 50 &&
                     tPlayer.inventory[tPlayer.selectedItem] != null &&
                     !tPlayer.inventory[tPlayer.selectedItem].IsAir &&
                     tPlayer.inventory[tPlayer.selectedItem].type == ItemID.LifeCrystal &&
                     tPlayer.inventory[tPlayer.selectedItem].stack < tPlayer.inventory[tPlayer.selectedItem].maxStack)
            {
                refundSlot = tPlayer.selectedItem;
            }
            else if (_lastLifeCrystalSlot.TryGetValue(player.Index, out int lastSlot) &&
                     lastSlot >= 0 && lastSlot < 59)
            {
                var sItem = tPlayer.inventory[lastSlot];
                if (sItem == null || sItem.IsAir || sItem.type == ItemID.None ||
                    (sItem.type == ItemID.LifeCrystal && sItem.stack < sItem.maxStack))
                {
                    refundSlot = lastSlot;
                }
            }

            if (refundSlot < 0)
            {
                for (int i = 0; i < 59 && i < tPlayer.inventory.Length; i++)
                {
                    var inv = tPlayer.inventory[i];
                    if (inv != null && !inv.IsAir && inv.type == ItemID.LifeCrystal && inv.stack < inv.maxStack)
                    {
                        refundSlot = i;
                        break;
                    }
                }
            }

            if (refundSlot < 0)
            {
                for (int i = 0; i < 50; i++)
                {
                    var inv = tPlayer.inventory[i];
                    if (inv == null || inv.IsAir || inv.type == ItemID.None)
                    {
                        refundSlot = i;
                        break;
                    }
                }
            }

            if (refundSlot >= 0 && refundSlot < tPlayer.inventory.Length)
            {
                var targetItem = tPlayer.inventory[refundSlot];
                if (targetItem == null || targetItem.IsAir || targetItem.type != ItemID.LifeCrystal)
                {
                    targetItem = new Item();
                    targetItem.SetDefaults(ItemID.LifeCrystal);
                    targetItem.stack = 1;
                    tPlayer.inventory[refundSlot] = targetItem;
                }
                else
                {
                    targetItem.stack = Math.Min(targetItem.maxStack, targetItem.stack + 1);
                }

                NetMessage.SendData((int)PacketTypes.PlayerSlot, -1, -1, null, player.Index, refundSlot, targetItem.prefix, 0, 0);
                NetMessage.SendData((int)PacketTypes.PlayerSlot, player.Index, -1, null, player.Index, refundSlot, targetItem.prefix, 0, 0);
            }
        }

        private void OnNetGetData(GetDataEventArgs args)
        {
            if (args == null || args.Handled) return;

            var player = TShock.Players[args.Msg.whoAmI];
            if (player == null || !player.Active) return;

            if (args.MsgID == PacketTypes.ItemOwner)
            {
                if (player.IsLoggedIn && player.Account != null)
                {
                    try
                    {
                        long pos = args.Msg.reader.BaseStream.Position;
                        short itemIdx = args.Msg.reader.ReadInt16();
                        byte ownerId = args.Msg.reader.ReadByte();
                        args.Msg.reader.BaseStream.Position = pos;

                        if (itemIdx >= 0 && itemIdx < Main.maxItems && ownerId == player.Index && IsBlockedWorldItem(player.Index, itemIdx))
                        {
                            args.Handled = true;
                            var wItem = Main.item[itemIdx];
                            if (wItem != null && wItem.active)
                            {
                                wItem.playerIndexTheItemIsReservedFor = 255;
                            }
                            NetMessage.SendData((int)PacketTypes.ItemOwner, -1, -1, null, itemIdx);
                            return;
                        }
                    }
                    catch { }
                }
                return;
            }

            if (args.MsgID == PacketTypes.PlayerAddBuff || args.MsgID == PacketTypes.PlayerBuff)
            {
                if (player.IsLoggedIn && player.Account != null && !IsPlayerDeadState(player))
                {
                    var state = GetPlayerState(player);
                    int[] lockedBuffs = GetLockedBuffsForState(state);
                    if (lockedBuffs.Length > 0)
                    {
                        if (args.MsgID == PacketTypes.PlayerAddBuff)
                        {
                            try
                            {
                                long pos = args.Msg.reader.BaseStream.Position;
                                byte targetId = args.Msg.reader.ReadByte();
                                ushort buffType = args.Msg.reader.ReadUInt16();
                                int buffTime = args.Msg.reader.ReadInt32();
                                args.Msg.reader.BaseStream.Position = pos;

                                if (targetId == player.Index && Array.IndexOf(lockedBuffs, (int)buffType) >= 0 && buffTime <= 0)
                                {
                                    args.Handled = true;
                                    RestoreMissingLockedBuffs(player, state, new[] { (int)buffType }, true);
                                    return;
                                }
                            }
                            catch { }
                        }
                        else if (args.MsgID == PacketTypes.PlayerBuff)
                        {
                            try
                            {
                                long pos = args.Msg.reader.BaseStream.Position;
                                byte targetId = args.Msg.reader.ReadByte();
                                if (targetId == player.Index)
                                {
                                    var incomingBuffs = new HashSet<int>();
                                    for (int i = 0; i < Player.maxBuffs; i++)
                                    {
                                        ushort bType = args.Msg.reader.ReadUInt16();
                                        if (bType > 0) incomingBuffs.Add(bType);
                                    }
                                    args.Msg.reader.BaseStream.Position = pos;

                                    var missing = new List<int>();
                                    foreach (int lockId in lockedBuffs)
                                    {
                                        if (!incomingBuffs.Contains(lockId))
                                        {
                                            missing.Add(lockId);
                                        }
                                    }

                                    if (missing.Count > 0)
                                    {
                                        RestoreMissingLockedBuffs(player, state, missing, false);
                                    }
                                }
                                else
                                {
                                    args.Msg.reader.BaseStream.Position = pos;
                                }
                            }
                            catch { }
                        }
                    }
                }
                return;
            }

            if (args.MsgID != PacketTypes.PlayerHp) return;

            try
            {
                if (args.Msg?.reader == null) return;

                byte pktPlayerId = args.Msg.reader.ReadByte();
                short statLife = args.Msg.reader.ReadInt16();
                short statLifeMax = args.Msg.reader.ReadInt16();

                if (statLife <= 0)
                {
                    MarkPlayerDead(player, 25.0);
                    return;
                }

                if (IsPlayerDeadState(player))
                {
                    return;
                }

                int maxAllowedBaseLife = GetMaxAllowedBaseLife();
                int incomingMax = statLifeMax > 0 ? statLifeMax : BaseStartHp;

                if (!player.IsLoggedIn || player.Account == null)
                {
                    if (player.TPlayer != null)
                    {
                        player.TPlayer.statLife = statLife;
                    }
                    int rawBase = Math.Min(Math.Max(BaseStartHp, incomingMax), maxAllowedBaseLife);
                    _playerBaseLife[player.Index] = rawBase;
                    return;
                }

                var state = GetPlayerState(player);
                int hpDelta = GetMaxHpDelta(state);

                // ช่วงเพิ่งล็อกอินหรือเกิดใหม่ ให้ยึดค่า BaseLife จาก DB/แคช ไม่ให้ Client มาทับค่า Max HP ให้ลดลง
                if (IsInLoginLifeLock(player))
                {
                    int lockedBase = GetTrackedBaseLife(player);
                    int lockedTargetMax = Math.Max(20, lockedBase + hpDelta);
                    if (incomingMax < lockedTargetMax)
                    {
                        args.Handled = true;
                        if (player.TPlayer != null)
                        {
                            player.TPlayer.statLife = Math.Min(lockedTargetMax, Math.Max(1, statLife));
                            player.TPlayer.statLifeMax = lockedTargetMax;
                            player.TPlayer.statLifeMax2 = lockedTargetMax;
                        }
                        ForceSyncLifeToClient(player, state);
                        return;
                    }
                }

                if (state == null || string.IsNullOrEmpty(state.ClassName) || state.StarterChoice <= 0)
                {
                    if (player.TPlayer != null)
                    {
                        player.TPlayer.statLife = statLife;
                    }
                    int rawBase = Math.Min(Math.Max(BaseStartHp, incomingMax), maxAllowedBaseLife);
                    _playerBaseLife[player.Index] = rawBase;
                    state?.BaseLife = rawBase;
                    if (state != null && !string.IsNullOrEmpty(state.ClassName))
                    {
                        SaveStateToDB(player.Account.ID, state);
                    }
                    return;
                }

                int currentBase = GetTrackedBaseLife(player);
                int expectedTargetMax = Math.Max(20, currentBase + hpDelta);

                // ดักการใช้ Life Crystal เกินลิมิต 15 ก้อนทันที ไม่ว่า incomingMax จะเพิ่มทีละเท่าไร
                if (incomingMax > expectedTargetMax && IsLifeCrystalAtLimit(player))
                {
                    args.Handled = true;
                    BlockLifeCrystalUsage(player);
                    RefundBlockedLifeCrystal(player);
                    NotifyBlockedLifeCrystal(player);

                    _playerBaseLife[player.Index] = maxAllowedBaseLife;
                    state.BaseLife = maxAllowedBaseLife;
                    SaveStateToDB(player.Account.ID, state);

                    if (player.TPlayer != null)
                    {
                        int revertedLife = Math.Max(1, Math.Min(expectedTargetMax, statLife - HpPerLifeCrystal));
                        player.TPlayer.statLife = revertedLife;
                        player.TPlayer.statLifeMax = expectedTargetMax;
                        player.TPlayer.statLifeMax2 = expectedTargetMax;
                    }

                    ForceSyncLifeToClient(player, state);
                    return;
                }

                bool baseChanged = false;
                if (incomingMax > expectedTargetMax)
                {
                    int rawNewBase = Math.Min(maxAllowedBaseLife, incomingMax - hpDelta);
                    int normalizedNewBase = NormalizeBaseLifeValue(rawNewBase);
                    if (normalizedNewBase > currentBase)
                    {
                        currentBase = normalizedNewBase;
                        baseChanged = true;
                    }
                }

                if (baseChanged)
                {
                    int newTargetMax = Math.Max(20, currentBase + hpDelta);
                    _crystalUseCooldown[player.Index] = DateTime.UtcNow;
                    _playerBaseLife[player.Index] = currentBase;
                    state.BaseLife = currentBase;
                    SaveStateToDB(player.Account.ID, state);

                    if (player.TPlayer != null)
                    {
                        player.TPlayer.statLife = Math.Min(newTargetMax, Math.Max(1, statLife));
                        player.TPlayer.statLifeMax = newTargetMax;
                        player.TPlayer.statLifeMax2 = newTargetMax;
                    }

                    args.Handled = true;
                    ForceSyncLifeToClient(player, state);
                    return;
                }

                if (player.TPlayer != null)
                {
                    player.TPlayer.statLife = Math.Min(expectedTargetMax, statLife);
                }

                if (incomingMax != expectedTargetMax)
                {
                    args.Handled = true;
                    ForceSyncLifeToClient(player, state);
                }
            }
            catch { }
        }

        private void OnPlayerDamage(object sender, GetDataHandlers.PlayerDamageEventArgs args)
        {
            var player = TShock.Players[args.ID];
            if (player == null || !player.Active) return;

            if (IsPlayerInSelectionLock(player))
            {
                args.Handled = true;
                SetUnderworldGodMode(player, true);
                return;
            }

            if (IsPlayerDeadState(player)) return;
            if (!player.IsLoggedIn || player.Account == null) return;

            var state = GetPlayerState(player);
            double reduction = GetBrokenArmorReduction(state);
            if (reduction <= 0.0 || args.Damage <= 1) return;

            int baseDefense = CalculatePlayerEquipmentDefense(player.TPlayer);
            if (baseDefense <= 0) return;

            double defenseEffectiveness = Main.masterMode ? 1.0 : (Main.expertMode ? 0.75 : 0.5);

            int defAt50Percent = (int)Math.Floor(baseDefense * 0.50);
            int defAtCustomLoss = (int)Math.Floor(baseDefense * (1.0 - reduction));
            int extraDefenseSaved = Math.Max(0, defAtCustomLoss - defAt50Percent);

            int damageRefund = (int)Math.Floor(extraDefenseSaved * defenseEffectiveness);
            if (damageRefund > 0)
            {
                int maxRefund = Math.Max(0, args.Damage - 1);
                int actualRefund = Math.Min(damageRefund, maxRefund);

                int predictedRemainingLife = player.TPlayer.statLife - args.Damage;
                if (actualRefund > 0 && predictedRemainingLife > 0 && player.TPlayer.statLife > 0 && !player.TPlayer.dead)
                {
                    player.TPlayer.statLife = Math.Min(player.TPlayer.statLifeMax2, player.TPlayer.statLife + actualRefund);
                    NetMessage.SendData((int)PacketTypes.PlayerHp, -1, -1, null, player.Index);
                    NetMessage.SendData((int)PacketTypes.PlayerHp, player.Index, -1, null, player.Index);
                }
            }
        }

        private void ApplyBrokenArmorDefenseEffect(TSPlayer player, PlayerClassState state)
        {
            if (player?.TPlayer == null || !player.Active || IsPlayerDeadState(player)) return;
            double reduction = GetBrokenArmorReduction(state);
            if (reduction <= 0.0) return;

            var tPlayer = player.TPlayer;
            int baseDef = CalculatePlayerEquipmentDefense(tPlayer);
            int reducedAmount = (int)Math.Floor(baseDef * reduction);
            tPlayer.statDefense = Math.Max(0, baseDef - reducedAmount);
        }

        private void ApplyHoneyRegenBonus(TSPlayer player, PlayerClassState state, double deltaSeconds)
        {
            if (player?.TPlayer == null || !player.Active || IsPlayerDeadState(player))
            {
                if (player != null) _honeyRegenAccumulator.Remove(player.Index);
                return;
            }

            double bonusMult = GetHoneyBonusMultiplier(state);
            if (bonusMult <= 0.0)
            {
                _honeyRegenAccumulator.Remove(player.Index);
                return;
            }

            var tPlayer = player.TPlayer;
            double baseHoneyHpPerSec = 3.0;
            if (HasBuff(player, BuffID.Campfire)) baseHoneyHpPerSec += 0.5;
            if (HasBuff(player, BuffID.HeartLamp)) baseHoneyHpPerSec += 1.0;
            if (HasBuff(player, BuffID.Regeneration)) baseHoneyHpPerSec += 2.0;

            double bonusHpPerSec = Math.Max(0.5, baseHoneyHpPerSec * bonusMult);

            if (!tPlayer.dead && tPlayer.statLife > 0 && tPlayer.statLife < tPlayer.statLifeMax2 && deltaSeconds > 0)
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
                var state = GetPlayerState(player);
                if (state != null && !string.IsNullOrEmpty(state.ClassName))
                {
                    Item checkItem = new Item();
                    checkItem.SetDefaults(wItem.type);
                    checkItem.stack = Math.Max(1, wItem.stack);
                    if (!IsItemAllowedForClass(checkItem, state.ClassName))
                    {
                        RegisterBlockedWorldItem(playerIndex, worldItemIndex);
                        return true;
                    }
                }
            }

            return false;
        }

        private void MaintainBlockedWorldItems(TSPlayer player)
        {
            if (player?.TPlayer == null || !player.Active) return;
            if (!_blockedWorldItemsByPlayer.TryGetValue(player.Index, out var set) || set.Count == 0) return;

            var expired = new List<int>();
            Vector2 playerCenter = player.TPlayer.Center;

            foreach (int idx in set)
            {
                if (idx < 0 || idx >= Main.maxItems || Main.item[idx] == null || !Main.item[idx].active || Main.item[idx].type <= ItemID.None)
                {
                    expired.Add(idx);
                    continue;
                }

                var wItem = Main.item[idx];
                wItem.playerIndexTheItemIsReservedFor = 255;

                float dist = Vector2.Distance(playerCenter, wItem.Center);
                if (dist < 120f)
                {
                    float pushDir = wItem.Center.X >= playerCenter.X ? 1f : -1f;
                    int targetTileX = (int)((playerCenter.X + pushDir * 145f) / 16f);
                    int targetTileY = (int)(wItem.Center.Y / 16f);

                    if (targetTileX > 20 && targetTileX < Main.maxTilesX - 20 &&
                        targetTileY > 20 && targetTileY < Main.maxTilesY - 20 &&
                        !Collision.SolidCollision(new Vector2(targetTileX * 16f, targetTileY * 16f), wItem.width, wItem.height))
                    {
                        wItem.position.X = targetTileX * 16f;
                    }

                    wItem.velocity = new Vector2(pushDir * 6.5f, -3.5f);
                    NetMessage.SendData((int)PacketTypes.ItemDrop, -1, -1, null, idx);
                    NetMessage.SendData((int)PacketTypes.ItemOwner, -1, -1, null, idx);
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

            var state = GetPlayerState(player);
            if (state == null || string.IsNullOrEmpty(state.ClassName)) return;

            if (args.ID >= 0 && args.ID < Main.maxItems)
            {
                var worldItem = Main.item[args.ID];
                if (worldItem != null && worldItem.active && worldItem.type > ItemID.None)
                {
                    Item checkItem = new Item();
                    checkItem.SetDefaults(worldItem.type);
                    checkItem.stack = Math.Max(1, worldItem.stack);

                    if (!IsItemAllowedForClass(checkItem, state.ClassName))
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
                        float pushDir = wItem.Center.X >= player.TPlayer.Center.X ? 1f : -1f;
                        wItem.playerIndexTheItemIsReservedFor = 255;
                        wItem.velocity = new Vector2(pushDir * 6.5f, -3.5f);
                    }
                    NetMessage.SendData((int)PacketTypes.ItemDrop, -1, -1, null, args.ID);
                    NetMessage.SendData((int)PacketTypes.ItemOwner, -1, -1, null, args.ID);
                }
            }
        }

        private int GetMaxAllowedBaseLife()
        {
            int crystals = Math.Max(0, MaxLifeCrystalsAllowed);
            return BaseStartHp + (crystals * HpPerLifeCrystal);
        }

        private int NormalizeBaseLifeValue(int rawBase)
        {
            int maxAllowed = GetMaxAllowedBaseLife();
            if (rawBase <= BaseStartHp) return BaseStartHp;
            if (rawBase >= maxAllowed) return maxAllowed;
            int offset = rawBase - BaseStartHp;
            int steps = offset / HpPerLifeCrystal;
            return BaseStartHp + (steps * HpPerLifeCrystal);
        }

        private int GetTrackedBaseLife(TSPlayer player)
        {
            int maxAllowedBaseLife = GetMaxAllowedBaseLife();
            if (player != null && player.IsLoggedIn && player.Account != null)
            {
                var state = GetPlayerState(player);
                if (state != null && state.BaseLife >= BaseStartHp)
                {
                    int fromState = NormalizeBaseLifeValue(state.BaseLife);
                    _playerBaseLife[player.Index] = fromState;
                    return fromState;
                }
            }

            if (player != null && _playerBaseLife.TryGetValue(player.Index, out int tracked))
            {
                return Math.Min(Math.Max(BaseStartHp, tracked), maxAllowedBaseLife);
            }

            _playerBaseLife[player?.Index ?? 0] = BaseStartHp;
            return BaseStartHp;
        }

        private bool IsLifeCrystalAtLimit(TSPlayer player)
        {
            if (player?.TPlayer == null) return false;
            int maxAllowedBaseLife = GetMaxAllowedBaseLife();
            return GetTrackedBaseLife(player) >= maxAllowedBaseLife;
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

        private int SpawnDroppedItemAwayFromPlayer(TSPlayer player, int itemType, int stack, byte prefix)
        {
            if (player?.TPlayer == null || itemType <= ItemID.None) return -1;

            float dir = player.TPlayer.direction != 0 ? player.TPlayer.direction : 1f;
            float targetX = player.TPlayer.Center.X + (dir * 160f);
            float targetY = player.TPlayer.Center.Y - 24f;

            Vector2 dropPos = new Vector2(targetX, targetY);
            if (Collision.SolidCollision(dropPos, 24, 24))
            {
                float altX = player.TPlayer.Center.X - (dir * 160f);
                Vector2 altPos = new Vector2(altX, targetY);
                if (!Collision.SolidCollision(altPos, 24, 24))
                {
                    dropPos = altPos;
                    dir = -dir;
                }
                else
                {
                    dropPos = new Vector2(player.TPlayer.Center.X + (dir * 80f), targetY);
                }
            }

            int droppedIndex = Item.NewItem(
                new MiscEntitySource(),
                dropPos,
                itemType,
                Math.Max(1, stack)
            );

            if (droppedIndex >= 0 && droppedIndex < Main.maxItems)
            {
                Main.item[droppedIndex].prefix = prefix;
                Main.item[droppedIndex].playerIndexTheItemIsReservedFor = 255;
                Main.item[droppedIndex].velocity = new Vector2(dir * 8.5f, -4.5f);

                NetMessage.SendData((int)PacketTypes.ItemDrop, -1, -1, null, droppedIndex);
                NetMessage.SendData((int)PacketTypes.ItemOwner, -1, -1, null, droppedIndex);

                RegisterBlockedWorldItem(player.Index, droppedIndex);
            }

            return droppedIndex;
        }

        private void NotifyBlockedPickup(TSPlayer player, string itemName, bool fromCraftingOrCursor = false, bool droppedAway = false)
        {
            if (player == null) return;
            var state = GetPlayerState(player);
            string className = state?.ClassName ?? "class";

            if (fromCraftingOrCursor)
            {
                if (!_craftWarnCooldown.TryGetValue(player.Index, out DateTime lastCraftWarn) || (DateTime.UtcNow - lastCraftWarn).TotalSeconds >= 3)
                {
                    _craftWarnCooldown[player.Index] = DateTime.UtcNow;
                    string tail = droppedAway
                        ? "Item dropped away from you and cannot be picked up."
                        : "Item removed.";
                    player.SendErrorMessage($"[ClassSystem] '{itemName}' blocked — {className.ToUpper()} cannot craft or hold items from other classes! {tail}");
                }
                return;
            }

            if (!_dropWarnCooldown.TryGetValue(player.Index, out DateTime lastWarn) || (DateTime.UtcNow - lastWarn).TotalSeconds >= 3)
            {
                _dropWarnCooldown[player.Index] = DateTime.UtcNow;
                player.SendWarningMessage($"[ClassSystem] Dropped '{itemName}' away — {className.ToUpper()} cannot use or pick up items from other classes.");
            }
        }

        private bool TryAcquireItemTypeEjectLock(int playerIndex, int itemType, double cooldownMs = 1500)
        {
            long key = ((long)playerIndex << 32) | (uint)Math.Max(0, itemType);
            if (_itemTypeEjectCooldown.TryGetValue(key, out DateTime lastTime) && (DateTime.UtcNow - lastTime).TotalMilliseconds < cooldownMs)
            {
                return false;
            }
            _itemTypeEjectCooldown[key] = DateTime.UtcNow;
            return true;
        }

        private void EjectDisallowedItemSafely(TSPlayer player, int netSlotIndex, Item itemToDrop, int fallbackType, int fallbackStack, byte fallbackPrefix = 0, bool isCursorOrCraft = false, bool bypassCooldown = false)
        {
            if (player?.TPlayer == null) return;

            int itemType = itemToDrop != null && !itemToDrop.IsAir && itemToDrop.type > ItemID.None
                ? itemToDrop.type
                : fallbackType;

            if (itemType <= ItemID.None) return;

            int incomingStack = itemToDrop != null && itemToDrop.stack > 0
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

            int serverFoundStack = 0;
            var tPlayer = player.TPlayer;

            if (netSlotIndex >= 0 && netSlotIndex < 59 && tPlayer.inventory[netSlotIndex] != null && !tPlayer.inventory[netSlotIndex].IsAir && tPlayer.inventory[netSlotIndex].type == itemType)
            {
                serverFoundStack += Math.Max(1, tPlayer.inventory[netSlotIndex].stack);
                ClearPlayerNetSlot(player, netSlotIndex);
            }
            else if (netSlotIndex >= 59 && netSlotIndex < 79)
            {
                int armorIdx = netSlotIndex - 59;
                if (armorIdx < tPlayer.armor.Length && tPlayer.armor[armorIdx] != null && !tPlayer.armor[armorIdx].IsAir && tPlayer.armor[armorIdx].type == itemType)
                {
                    serverFoundStack += Math.Max(1, tPlayer.armor[armorIdx].stack);
                    ClearPlayerNetSlot(player, netSlotIndex);
                }
            }
            else if (netSlotIndex == 179 && tPlayer.trashItem != null && !tPlayer.trashItem.IsAir && tPlayer.trashItem.type == itemType)
            {
                serverFoundStack += Math.Max(1, tPlayer.trashItem.stack);
                ClearPlayerNetSlot(player, 179);
            }
            else if (netSlotIndex >= 0)
            {
                ClearPlayerNetSlot(player, netSlotIndex);
            }

            for (int i = 0; i < tPlayer.inventory.Length && i < 59; i++)
            {
                Item inv = tPlayer.inventory[i];
                if (inv != null && !inv.IsAir && inv.type == itemType)
                {
                    serverFoundStack += Math.Max(1, inv.stack);
                    ClearPlayerNetSlot(player, i);
                }
            }

            for (int i = 0; i < 3 && i < tPlayer.armor.Length; i++)
            {
                Item arm = tPlayer.armor[i];
                if (arm != null && !arm.IsAir && arm.type == itemType)
                {
                    serverFoundStack += Math.Max(1, arm.stack);
                    ClearPlayerNetSlot(player, 59 + i);
                }
            }

            if (tPlayer.trashItem != null && !tPlayer.trashItem.IsAir && tPlayer.trashItem.type == itemType)
            {
                serverFoundStack += Math.Max(1, tPlayer.trashItem.stack);
                ClearPlayerNetSlot(player, 179);
            }

            int totalStackToDrop = Math.Max(incomingStack, serverFoundStack);
            if (totalStackToDrop <= 0) return;

            bool isCraftAction = isCursorOrCraft || netSlotIndex == 58;
            if (!TryAcquireItemTypeEjectLock(player.Index, itemType, 1500))
            {
                return;
            }

            int dropStack = Math.Min(totalStackToDrop, Math.Max(1, itemToDrop?.maxStack ?? 1));
            int droppedIdx = SpawnDroppedItemAwayFromPlayer(player, itemType, dropStack, prefix);
            NotifyBlockedPickup(player, itemName, isCraftAction, droppedIdx >= 0);
        }

        private void ClearPlayerNetSlot(TSPlayer player, int netSlotIndex)
        {
            var tPlayer = player.TPlayer;
            if (netSlotIndex >= 0 && netSlotIndex < 59)
            {
                tPlayer.inventory[netSlotIndex] = new Item();
                tPlayer.inventory[netSlotIndex].TurnToAir();
                if (netSlotIndex == 58)
                {
                    Main.mouseItem = new Item();
                    Main.mouseItem.TurnToAir();
                }
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

        private void RemoveAllWeaponsFromInventory(TSPlayer player)
        {
            if (player?.TPlayer == null) return;
            var tPlayer = player.TPlayer;

            for (int i = 0; i < tPlayer.inventory.Length && i < 59; i++)
            {
                Item item = tPlayer.inventory[i];
                if (item != null && !item.IsAir && item.type > ItemID.None && IsWeaponItem(item))
                {
                    ClearPlayerNetSlot(player, i);
                }
            }

            if (tPlayer.trashItem != null && !tPlayer.trashItem.IsAir && tPlayer.trashItem.type > ItemID.None && IsWeaponItem(tPlayer.trashItem))
            {
                ClearPlayerNetSlot(player, 179);
            }
        }

        private bool IsWeaponItem(Item item)
        {
            if (item == null || item.IsAir || item.type <= ItemID.None) return false;
            if (item.pick > 0 || item.axe > 0 || item.hammer > 0) return false;
            return item.damage > 0 && (item.melee || item.ranged || item.magic || item.summon);
        }

        private bool HasAnyDisallowedItemInInventory(TSPlayer player, string currentClass)
        {
            if (player?.TPlayer == null || string.IsNullOrEmpty(currentClass)) return false;
            var tPlayer = player.TPlayer;

            if (tPlayer.HeldItem != null && !tPlayer.HeldItem.IsAir && tPlayer.HeldItem.type > ItemID.None &&
                !IsItemAllowedForClass(tPlayer.HeldItem, currentClass))
            {
                return true;
            }

            for (int i = 0; i < tPlayer.inventory.Length && i < 59; i++)
            {
                Item item = tPlayer.inventory[i];
                if (item != null && !item.IsAir && item.type > ItemID.None && !IsItemAllowedForClass(item, currentClass))
                {
                    return true;
                }
            }

            for (int i = 0; i < 3 && i < tPlayer.armor.Length; i++)
            {
                Item arm = tPlayer.armor[i];
                if (arm != null && !arm.IsAir && arm.type > ItemID.None && !IsItemAllowedForClass(arm, currentClass))
                {
                    return true;
                }
            }

            if (tPlayer.trashItem != null && !tPlayer.trashItem.IsAir && tPlayer.trashItem.type > ItemID.None &&
                !IsItemAllowedForClass(tPlayer.trashItem, currentClass))
            {
                return true;
            }

            return false;
        }

        private void EnforceAllowedInventory(TSPlayer player, string currentClass)
        {
            if (player?.TPlayer == null || string.IsNullOrEmpty(currentClass)) return;

            var tPlayer = player.TPlayer;

            if (tPlayer.inventory.Length > 58)
            {
                Item cursorItem = tPlayer.inventory[58];
                if (cursorItem != null && !cursorItem.IsAir && cursorItem.type > ItemID.None && !IsItemAllowedForClass(cursorItem, currentClass))
                {
                    EjectDisallowedItemSafely(player, 58, cursorItem, cursorItem.type, cursorItem.stack, cursorItem.prefix, true, false);
                }
            }

            for (int i = 0; i < tPlayer.inventory.Length && i < 58; i++)
            {
                Item item = tPlayer.inventory[i];
                if (item != null && !item.IsAir && item.type > ItemID.None && !IsItemAllowedForClass(item, currentClass))
                {
                    EjectDisallowedItemSafely(player, i, item, item.type, item.stack, item.prefix, false, false);
                }
            }

            for (int i = 0; i < 3 && i < tPlayer.armor.Length; i++)
            {
                Item armorItem = tPlayer.armor[i];
                if (armorItem != null && !armorItem.IsAir && armorItem.type > ItemID.None && !IsItemAllowedForClass(armorItem, currentClass))
                {
                    EjectDisallowedItemSafely(player, 59 + i, armorItem, armorItem.type, armorItem.stack, armorItem.prefix, false, false);
                }
            }

            if (tPlayer.trashItem != null && !tPlayer.trashItem.IsAir && tPlayer.trashItem.type > ItemID.None && !IsItemAllowedForClass(tPlayer.trashItem, currentClass))
            {
                EjectDisallowedItemSafely(player, 179, tPlayer.trashItem, tPlayer.trashItem.type, tPlayer.trashItem.stack, tPlayer.trashItem.prefix, false, false);
            }
        }

        private Vector2 GetSurfaceSpawnPosition()
        {
            int tileX = Main.spawnTileX > 0 ? Main.spawnTileX : Main.maxTilesX / 2;
            int tileY = Main.spawnTileY > 0 ? Main.spawnTileY - 3 : (int)(Main.worldSurface * 0.35);
            return new Vector2(tileX * 16f, tileY * 16f);
        }

        private bool IsTileStandableGround(int centerTileX, int feetTileY)
        {
            if (centerTileX < 50 || centerTileX > Main.maxTilesX - 50 || feetTileY < 50 || feetTileY > Main.maxTilesY - 30)
                return false;

            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -3; dy <= 0; dy++)
                {
                    var tile = Main.tile[centerTileX + dx, feetTileY + dy];
                    if (tile == null) return false;
                    if (tile.active() && Main.tileSolid[tile.type] && !Main.tileSolidTop[tile.type])
                        return false;
                    if (tile.liquid > 0)
                        return false;
                }
            }

            var floorTile = Main.tile[centerTileX, feetTileY + 1];
            if (floorTile == null || floorTile.liquid > 0)
                return false;

            bool hasSolidFloor = floorTile.active() && (Main.tileSolid[floorTile.type] || Main.tileSolidTop[floorTile.type]);
            if (!hasSolidFloor)
                return false;

            return true;
        }

        private Vector2 GetUnderworldPosition()
        {
            if (_cachedSafeUnderworldPos.HasValue)
            {
                int cx = (int)(_cachedSafeUnderworldPos.Value.X / 16f);
                int cy = (int)(_cachedSafeUnderworldPos.Value.Y / 16f) + 3;
                if (IsTileStandableGround(cx, cy))
                {
                    return _cachedSafeUnderworldPos.Value;
                }
            }

            int startX = Main.maxTilesX / 2;
            int minY = Math.Max(100, Main.maxTilesY - 195);
            int maxY = Math.Max(minY + 10, Main.maxTilesY - 40);

            for (int offsetX = 0; offsetX <= 300; offsetX += 2)
            {
                int[] candidateXs = offsetX == 0 ? new[] { startX } : new[] { startX + offsetX, startX - offsetX };
                foreach (int tx in candidateXs)
                {
                    for (int ty = minY; ty <= maxY; ty++)
                    {
                        if (IsTileStandableGround(tx, ty))
                        {
                            Vector2 safePos = new Vector2(tx * 16f, (ty - 3) * 16f);
                            _cachedSafeUnderworldPos = safePos;
                            return safePos;
                        }
                    }
                }
            }

            int fallbackX = Main.maxTilesX / 2;
            int fallbackY = Math.Max(100, Main.maxTilesY - 120);
            return new Vector2(fallbackX * 16f, fallbackY * 16f);
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
                NetMessage.SendData((int)PacketTypes.PlayerAddBuff, player.Index, -1, null, player.Index, BuffID.Webbed, 0);
            }
        }

        private void ApplyUnderworldLock(TSPlayer player, bool applyWeb)
        {
            if (player == null || !player.Active) return;

            SetUnderworldGodMode(player, true);
            DisarmLockedPlayer(player);

            Vector2 hellPos = GetUnderworldPosition();
            _frozenPositions[player.Index] = hellPos;

            if (Vector2.Distance(player.TPlayer.position, hellPos) > 64f)
            {
                player.TPlayer.velocity = Vector2.Zero;
                player.TPlayer.fallStart = (int)(hellPos.Y / 16f);
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

            SetUnderworldGodMode(player, false);

            _frozenPositions.Remove(player.Index);
            _lastBroadcastPhase.Remove(player.Index);
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

                ExecuteNonQuery("CREATE TABLE IF NOT EXISTS PlayerClasses (AccountID INTEGER PRIMARY KEY, ClassName TEXT NOT NULL, StarterChoice INTEGER NOT NULL DEFAULT 0, BaseLife INTEGER NOT NULL DEFAULT 100)");
                EnsureColumnsExist();
                _dbReady = true;
            }
            catch (Exception ex)
            {
                _dbReady = false;
                TShock.Log.Error($"[ClassSystem] SetupDatabase error: {ex.Message}");
            }
        }

        private void EnsureColumnsExist()
        {
            if (_db == null) return;
            try
            {
                EnsureConnectionOpen();
                bool hasChoiceCol = false;
                bool hasBaseLifeCol = false;
                using (var cmd = _db.CreateCommand())
                {
                    cmd.CommandText = "PRAGMA table_info(PlayerClasses)";
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            if (!reader.IsDBNull(1))
                            {
                                string colName = reader.GetString(1);
                                if (string.Equals(colName, "StarterChoice", StringComparison.OrdinalIgnoreCase))
                                    hasChoiceCol = true;
                                else if (string.Equals(colName, "BaseLife", StringComparison.OrdinalIgnoreCase))
                                    hasBaseLifeCol = true;
                            }
                        }
                    }
                }
                if (!hasChoiceCol)
                {
                    ExecuteNonQuery("ALTER TABLE PlayerClasses ADD COLUMN StarterChoice INTEGER NOT NULL DEFAULT 0");
                }
                if (!hasBaseLifeCol)
                {
                    ExecuteNonQuery("ALTER TABLE PlayerClasses ADD COLUMN BaseLife INTEGER NOT NULL DEFAULT 100");
                }
            }
            catch { }
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

            Commands.ChatCommands.Add(new Command("", CmdStarterChoice, "ch")
            {
                HelpText = "Select your starter item pack after picking a class: /ch <1|2>"
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
                ClearPlayerDeadState(player);
                ClearWebBuff(player);
                CleanDisallowedSpeedBuffs(player, null, true);
                ApplyUnderworldLock(player, false);
                player.SendWarningMessage("[ClassSystem] Welcome to the Underworld! (GodMode active) Log in with /login and pick a class with /c <Me|Rg|Mg|sm> or /class <warrior|ranger|mage|summoner>.");
            }
        }

        private void OnPlayerPostLogin(PlayerPostLoginEventArgs args)
        {
            if (args.Player?.Account == null) return;

            ClearWebBuff(args.Player);
            ArmLoginLifeLock(args.Player, 4.0);

            int accountId = args.Player.Account.ID;
            var savedState = GetStateFromDB(accountId);
            CleanDisallowedSpeedBuffs(args.Player, savedState, true);

            if (savedState != null && !string.IsNullOrEmpty(savedState.ClassName))
            {
                int normalizedBase = NormalizeBaseLifeValue(savedState.BaseLife);
                savedState.BaseLife = normalizedBase;
                _playerStates[accountId] = savedState;
                _playerBaseLife[args.Player.Index] = normalizedBase;
                _lastSyncedMaxLife.Remove(args.Player.Index);

                if (savedState.StarterChoice > 0)
                {
                    ClearWebAndTeleportToSpawn(args.Player);
                    CleanDisallowedSpeedBuffs(args.Player, savedState, true);
                    EnforceAllowedInventory(args.Player, savedState.ClassName);
                    if (!IsPlayerDeadState(args.Player))
                    {
                        ForceSyncLifeToClient(args.Player, savedState);
                        EnsurePermanentClassBuffs(args.Player, savedState, true);
                    }
                    args.Player.SendSuccessMessage($"[ClassSystem] Welcome back! Class: {savedState.ClassName.ToUpper()} (Pack /ch {savedState.StarterChoice})");
                }
                else
                {
                    string choicePrompt = GetStarterChoicePrompt(savedState.ClassName);
                    ApplyUnderworldLock(args.Player, true);
                    args.Player.SendWarningMessage(choicePrompt);
                    BroadcastSelectionPhase(
                        args.Player,
                        $"choose_item_{savedState.ClassName}",
                        $"[ClassSystem] {args.Player.Name}\n{choicePrompt}"
                    );
                }
            }
            else
            {
                int initialBase = NormalizeBaseLifeValue(args.Player.TPlayer?.statLifeMax ?? BaseStartHp);
                _playerBaseLife[args.Player.Index] = initialBase;
                ApplyUnderworldLock(args.Player, true);
                args.Player.SendWarningMessage(ClassSelectPrompt);
                BroadcastSelectionPhase(
                    args.Player,
                    "choose_class",
                    $"[ClassSystem] {args.Player.Name}\n{ClassSelectPrompt}"
                );
            }
        }

        private void OnServerLeave(LeaveEventArgs args)
        {
            var player = TShock.Players[args.Who];
            if (player?.Account != null)
            {
                if (_playerStates.TryGetValue(player.Account.ID, out var st) && !string.IsNullOrEmpty(st.ClassName))
                {
                    st.BaseLife = NormalizeBaseLifeValue(st.BaseLife);
                    SaveStateToDB(player.Account.ID, st);
                }
                _playerStates.Remove(player.Account.ID);
            }
            _pluginGrantedGodMode.Remove(args.Who);
            _frozenPositions.Remove(args.Who);
            _warnCooldown.Remove(args.Who);
            _serverBroadcastCooldown.Remove(args.Who);
            _lastBroadcastPhase.Remove(args.Who);
            _dropWarnCooldown.Remove(args.Who);
            _craftWarnCooldown.Remove(args.Who);
            _crystalWarnCooldown.Remove(args.Who);
            _crystalUseCooldown.Remove(args.Who);
            _lastLifeCrystalSlot.Remove(args.Who);
            _buffRefreshCooldown.Remove(args.Who);
            _lockedBuffRestoreCooldown.Remove(args.Who);
            _legacyBuffPurgeCooldown.Remove(args.Who);
            _lastSyncedMaxLife.Remove(args.Who);
            _playerBaseLife.Remove(args.Who);
            _loginLifeLockUntil.Remove(args.Who);
            _honeyRegenAccumulator.Remove(args.Who);
            _blockedWorldItemsByPlayer.Remove(args.Who);
            _playerDeadUntil.Remove(args.Who);

            var keysToRemove = new List<long>();
            foreach (var kv in _itemTypeEjectCooldown)
            {
                if ((int)(kv.Key >> 32) == args.Who)
                {
                    keysToRemove.Add(kv.Key);
                }
            }
            foreach (long k in keysToRemove)
            {
                _itemTypeEjectCooldown.Remove(k);
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
                var state = GetPlayerState(player);
                if (state == null || string.IsNullOrEmpty(state.ClassName))
                {
                    ApplyUnderworldLock(player, true);
                    BroadcastSelectionPhase(
                        player,
                        "choose_class",
                        $"[ClassSystem] {player.Name}\n{ClassSelectPrompt}"
                    );
                    if (!_warnCooldown.TryGetValue(player.Index, out DateTime lastWarn) || (DateTime.UtcNow - lastWarn).TotalSeconds >= 4)
                    {
                        _warnCooldown[player.Index] = DateTime.UtcNow;
                        player.SendWarningMessage(ClassSelectPrompt);
                    }
                }
                else if (state.StarterChoice <= 0)
                {
                    string choicePrompt = GetStarterChoicePrompt(state.ClassName);
                    ApplyUnderworldLock(player, true);
                    BroadcastSelectionPhase(
                        player,
                        $"choose_item_{state.ClassName}",
                        $"[ClassSystem] {player.Name}\n{choicePrompt}"
                    );
                    if (!_warnCooldown.TryGetValue(player.Index, out DateTime lastWarn) || (DateTime.UtcNow - lastWarn).TotalSeconds >= 4)
                    {
                        _warnCooldown[player.Index] = DateTime.UtcNow;
                        player.SendWarningMessage(choicePrompt);
                    }
                }
                else
                {
                    SetUnderworldGodMode(player, false);
                    _frozenPositions.Remove(player.Index);

                    if (IsPlayerDeadState(player))
                    {
                        return;
                    }

                    MaintainBlockedWorldItems(player);

                    Item held = player.TPlayer.HeldItem;
                    Item cursor = (player.TPlayer.inventory.Length > 58) ? player.TPlayer.inventory[58] : null;
                    bool holdingCrystal = (held != null && !held.IsAir && held.type == ItemID.LifeCrystal) ||
                                          (cursor != null && !cursor.IsAir && cursor.type == ItemID.LifeCrystal);

                    if (holdingCrystal && IsLifeCrystalAtLimit(player))
                    {
                        if (cursor != null && !cursor.IsAir && cursor.type == ItemID.LifeCrystal)
                            _lastLifeCrystalSlot[player.Index] = 58;
                        else if (player.TPlayer.selectedItem >= 0 && player.TPlayer.selectedItem < 50)
                            _lastLifeCrystalSlot[player.Index] = player.TPlayer.selectedItem;

                        BlockLifeCrystalUsage(player);
                        if (args.Control.IsUsingItem)
                        {
                            NotifyBlockedLifeCrystal(player);
                        }
                    }

                    if (HasAnyDisallowedItemInInventory(player, state.ClassName))
                    {
                        player.TPlayer.controlUseItem = false;
                        player.TPlayer.itemAnimation = 0;
                        player.TPlayer.itemTime = 0;
                        CancelActivePlayerProjectiles(player);
                        EnforceAllowedInventory(player, state.ClassName);
                    }
                }
            }
        }

        private void OnPlayerSlot(object sender, GetDataHandlers.PlayerSlotEventArgs args)
        {
            var player = TShock.Players[args.PlayerId];
            if (player == null || !player.Active || !player.IsLoggedIn || player.Account == null) return;

            var state = GetPlayerState(player);
            if (state == null || string.IsNullOrEmpty(state.ClassName)) return;

            if (args.Type == ItemID.LifeCrystal && args.Stack > 0 && args.Slot >= 0 && args.Slot < 59)
            {
                _lastLifeCrystalSlot[player.Index] = args.Slot;
            }

            if (args.Type > ItemID.None && args.Stack > 0)
            {
                Item testItem = new Item();
                testItem.SetDefaults(args.Type);
                testItem.stack = Math.Max(1, (int)args.Stack);
                testItem.prefix = (byte)args.Prefix;

                if (!IsItemAllowedForClass(testItem, state.ClassName))
                {
                    args.Handled = true;
                    player.TPlayer.controlUseItem = false;
                    player.TPlayer.itemAnimation = 0;
                    player.TPlayer.itemTime = 0;
                    CancelActivePlayerProjectiles(player);
                    bool isCursorOrCraft = (args.Slot == 58);
                    EjectDisallowedItemSafely(player, args.Slot, testItem, args.Type, args.Stack, (byte)args.Prefix, isCursorOrCraft, false);
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
                    SetUnderworldGodMode(player, true);
                    DisarmLockedPlayer(player);
                    if (runPeriodicSync)
                    {
                        CleanDisallowedSpeedBuffs(player, null, false);
                        ApplyUnderworldLock(player, false);
                    }
                    continue;
                }

                var state = GetPlayerState(player);
                if (state == null || string.IsNullOrEmpty(state.ClassName))
                {
                    _honeyRegenAccumulator.Remove(player.Index);
                    SetUnderworldGodMode(player, true);
                    DisarmLockedPlayer(player);
                    if (runPeriodicSync)
                    {
                        CleanDisallowedSpeedBuffs(player, null, false);
                        ApplyUnderworldLock(player, true);
                        BroadcastSelectionPhase(
                            player,
                            "choose_class",
                            $"[ClassSystem] {player.Name}\n{ClassSelectPrompt}"
                        );
                    }
                    continue;
                }

                if (state.StarterChoice <= 0)
                {
                    string choicePrompt = GetStarterChoicePrompt(state.ClassName);
                    _honeyRegenAccumulator.Remove(player.Index);
                    SetUnderworldGodMode(player, true);
                    DisarmLockedPlayer(player);
                    if (runPeriodicSync)
                    {
                        CleanDisallowedSpeedBuffs(player, state, false);
                        ApplyUnderworldLock(player, true);
                        BroadcastSelectionPhase(
                            player,
                            $"choose_item_{state.ClassName}",
                            $"[ClassSystem] {player.Name}\n{choicePrompt}"
                        );
                    }
                    continue;
                }

                SetUnderworldGodMode(player, false);

                if (IsPlayerDeadState(player))
                {
                    _honeyRegenAccumulator.Remove(player.Index);
                    continue;
                }

                ApplyHoneyRegenBonus(player, state, honeyDeltaSec);
                ApplyClassStats(player, state);
                ApplyBrokenArmorDefenseEffect(player, state);

                if (runPeriodicSync)
                {
                    ClearWebBuff(player);
                    CleanDisallowedSpeedBuffs(player, state, false);
                    MaintainBlockedWorldItems(player);
                    EnforceAllowedInventory(player, state.ClassName);
                    EnsurePermanentClassBuffs(player, state, false);
                }
            }
        }

        private void OnNpcStrike(NpcStrikeEventArgs args)
        {
            if (args.Player == null || args.Npc == null) return;
            int playerIndex = Array.IndexOf(Main.player, args.Player);
            if (playerIndex < 0 || playerIndex >= TShock.Players.Length) return;

            var tsPlayer = TShock.Players[playerIndex];
            if (tsPlayer == null || !tsPlayer.Active) return;

            if (IsPlayerInSelectionLock(tsPlayer))
            {
                args.Damage = 0;
                args.Handled = true;
                DisarmLockedPlayer(tsPlayer);
                return;
            }

            if (IsPlayerDeadState(tsPlayer)) return;
            if (!tsPlayer.IsLoggedIn || tsPlayer.Account == null) return;

            var state = GetPlayerState(tsPlayer);
            if (state == null || string.IsNullOrEmpty(state.ClassName)) return;

            var heldItem = args.Player.HeldItem;

            if ((heldItem != null && !heldItem.IsAir && !IsItemAllowedForClass(heldItem, state.ClassName)) ||
                HasAnyDisallowedItemInInventory(tsPlayer, state.ClassName))
            {
                args.Damage = 0;
                args.Handled = true;
                DisarmLockedPlayer(tsPlayer);
                EnforceAllowedInventory(tsPlayer, state.ClassName);
            }
        }

        private (short ItemId, int Stack)[] GetStarterPackItems(string className, int choice)
        {
            string c = (className ?? "").ToLowerInvariant();
            if (c == "warrior")
            {
                if (choice == 1) return new[] { (ItemID.EnchantedSword, 1) };
                if (choice == 2) return new[] { (ItemID.EnchantedBoomerang, 1) };
            }
            else if (c == "ranger")
            {
                if (choice == 1) return new[] { (ItemID.TendonBow, 1), (ItemID.WoodenArrow, 1000) };
                if (choice == 2) return new[] { (ItemID.FlintlockPistol, 1), (ItemID.MusketBall, 1000) };
            }
            else if (c == "mage")
            {
                if (choice == 1) return new[] { (ItemID.WandofSparking, 1), (ItemID.LesserManaPotion, 100) };
                if (choice == 2) return new[] { (ItemID.WaterBolt, 1), (ItemID.LesserManaPotion, 100) };
            }
            else if (c == "summoner")
            {
                if (choice == 1) return new[] { (ItemID.SlimeStaff, 1) };
                if (choice == 2) return new[] { (ItemID.ThornWhip, 1) };
            }
            return Array.Empty<(short, int)>();
        }

        private void GiveStarterItems(TSPlayer player, string className, int choice)
        {
            var items = GetStarterPackItems(className, choice);
            if (items == null || items.Length == 0) return;

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

            var currentState = GetPlayerState(args.Player);
            if (args.Parameters.Count == 0)
            {
                string info = (currentState == null || string.IsNullOrEmpty(currentState.ClassName))
                    ? ClassSelectPrompt
                    : (currentState.StarterChoice > 0
                        ? $"{currentState.ClassName.ToUpper()} (Pack: /ch {currentState.StarterChoice})"
                        : GetStarterChoicePrompt(currentState.ClassName));
                args.Player.SendInfoMessage(info);
                return;
            }

            string chosen = ResolveClassName(args.Parameters[0]);
            if (string.IsNullOrEmpty(chosen))
            {
                args.Player.SendErrorMessage(ClassSelectPrompt);
                return;
            }

            if (currentState != null && !string.IsNullOrEmpty(currentState.ClassName))
            {
                args.Player.SendErrorMessage($"[ClassSystem] Your class is already set to {currentState.ClassName.ToUpper()}. Ask an admin to reset it.");
                return;
            }

            int baseLife = _playerBaseLife.TryGetValue(args.Player.Index, out int tracked)
                ? NormalizeBaseLifeValue(tracked)
                : NormalizeBaseLifeValue(args.Player.TPlayer?.statLifeMax ?? BaseStartHp);

            var newState = new PlayerClassState
            {
                ClassName = chosen,
                StarterChoice = 0,
                BaseLife = baseLife
            };

            SaveStateToDB(args.Player.Account.ID, newState);
            _playerStates[args.Player.Account.ID] = newState;
            _playerBaseLife[args.Player.Index] = baseLife;

            string choicePrompt = GetStarterChoicePrompt(chosen);
            ApplyUnderworldLock(args.Player, true);
            args.Player.SendSuccessMessage(choicePrompt);
            TShock.Utils.Broadcast($"[ClassSystem] {args.Player.Name}\n{choicePrompt}", Color.Gold);
        }

        private void CmdStarterChoice(CommandArgs args)
        {
            if (!args.Player.IsLoggedIn || args.Player.Account == null)
            {
                args.Player.SendErrorMessage("[ClassSystem] Please log in first.");
                return;
            }

            var state = GetPlayerState(args.Player);
            if (state == null || string.IsNullOrEmpty(state.ClassName))
            {
                args.Player.SendErrorMessage(ClassSelectPrompt);
                return;
            }

            if (state.StarterChoice > 0)
            {
                args.Player.SendErrorMessage($"[ClassSystem] You already selected starter pack /ch {state.StarterChoice}. Ask an admin to reset if needed.");
                return;
            }

            if (args.Parameters.Count == 0 || !int.TryParse(args.Parameters[0], out int choice) || (choice != 1 && choice != 2))
            {
                args.Player.SendErrorMessage(GetStarterChoicePrompt(state.ClassName));
                return;
            }

            int baseLife = NormalizeBaseLifeValue(state.BaseLife);
            state.StarterChoice = choice;
            state.BaseLife = baseLife;

            SaveStateToDB(args.Player.Account.ID, state);
            _playerStates[args.Player.Account.ID] = state;
            _playerBaseLife[args.Player.Index] = baseLife;

            RemoveAllWeaponsFromInventory(args.Player);
            EnforceAllowedInventory(args.Player, state.ClassName);
            GiveStarterItems(args.Player, state.ClassName, choice);
            ClearWebAndTeleportToSpawn(args.Player);
            CleanDisallowedSpeedBuffs(args.Player, state, true);

            ForceSyncLifeToClient(args.Player, state);
            EnsurePermanentClassBuffs(args.Player, state, true);

            args.Player.SendSuccessMessage($"[ClassSystem] Starter pack /ch {choice} selected for {state.ClassName.ToUpper()}! Weapons cleared, starter gear granted, buffs active, GodMode disabled, and released from the Underworld.");
            TShock.Utils.Broadcast($"[ClassSystem] {args.Player.Name} picked starter pack /ch {choice} for {state.ClassName.ToUpper()} and joined the surface!", Color.LightGreen);
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

            var prevState = GetPlayerState(target);
            int preservedBaseLife = prevState != null
                ? NormalizeBaseLifeValue(prevState.BaseLife)
                : (_playerBaseLife.TryGetValue(target.Index, out int tracked) ? NormalizeBaseLifeValue(tracked) : BaseStartHp);

            string option = args.Parameters[1].ToLowerInvariant();
            if (option == "none" || option == "reset")
            {
                DeleteStateFromDB(target.Account.ID);
                _playerStates.Remove(target.Account.ID);
                _lastSyncedMaxLife.Remove(target.Index);
                _playerBaseLife[target.Index] = preservedBaseLife;
                _lastBroadcastPhase.Remove(target.Index);
                ClearPlayerDeadState(target);

                target.TPlayer.statLifeMax = preservedBaseLife;
                target.TPlayer.statLifeMax2 = preservedBaseLife;
                if (target.TPlayer.statLife > preservedBaseLife)
                {
                    target.TPlayer.statLife = preservedBaseLife;
                }
                NetMessage.SendData((int)PacketTypes.PlayerHp, -1, -1, null, target.Index);
                NetMessage.SendData((int)PacketTypes.PlayerHp, target.Index, -1, null, target.Index);

                CleanDisallowedSpeedBuffs(target, null, true);
                ApplyUnderworldLock(target, true);
                target.SendWarningMessage(ClassSelectPrompt);
                TShock.Utils.Broadcast($"[ClassSystem] {target.Name}\n{ClassSelectPrompt}", Color.Gold);
                args.Player.SendSuccessMessage($"Reset class for {target.Name}.");
                return;
            }

            string resolvedOption = ResolveClassName(option);
            if (string.IsNullOrEmpty(resolvedOption))
            {
                args.Player.SendErrorMessage("Invalid class. Use Me/warrior, Rg/ranger, Mg/mage, sm/summoner, or none.");
                return;
            }

            var newState = new PlayerClassState
            {
                ClassName = resolvedOption,
                StarterChoice = 0,
                BaseLife = preservedBaseLife
            };
            SaveStateToDB(target.Account.ID, newState);
            _playerStates[target.Account.ID] = newState;
            _lastSyncedMaxLife.Remove(target.Index);
            _playerBaseLife[target.Index] = preservedBaseLife;
            _lastBroadcastPhase.Remove(target.Index);
            ClearPlayerDeadState(target);

            target.TPlayer.statLifeMax = preservedBaseLife;
            target.TPlayer.statLifeMax2 = preservedBaseLife;
            if (target.TPlayer.statLife > preservedBaseLife)
            {
                target.TPlayer.statLife = preservedBaseLife;
            }
            NetMessage.SendData((int)PacketTypes.PlayerHp, -1, -1, null, target.Index);
            NetMessage.SendData((int)PacketTypes.PlayerHp, target.Index, -1, null, target.Index);

            CleanDisallowedSpeedBuffs(target, newState, true);
            string choicePrompt = GetStarterChoicePrompt(resolvedOption);
            ApplyUnderworldLock(target, true);
            target.SendSuccessMessage(choicePrompt);
            TShock.Utils.Broadcast($"[ClassSystem] {target.Name}\n{choicePrompt}", Color.Gold);
            args.Player.SendSuccessMessage($"Set {target.Name}'s class to {resolvedOption.ToUpper()} (awaiting /ch 1 or /ch 2).");
        }

        private PlayerClassState GetPlayerState(TSPlayer player)
        {
            if (player?.Account == null) return null;
            if (_playerStates.TryGetValue(player.Account.ID, out var cached)) return cached;
            var fromDb = GetStateFromDB(player.Account.ID);
            if (fromDb != null)
            {
                _playerStates[player.Account.ID] = fromDb;
            }
            return fromDb;
        }

        private void ForceSyncLifeToClient(TSPlayer player, PlayerClassState state)
        {
            if (player?.TPlayer == null || !player.Active || IsPlayerDeadState(player)) return;

            var tPlayer = player.TPlayer;
            int currentBaseLife = GetTrackedBaseLife(player);
            int hpDelta = GetMaxHpDelta(state);
            int targetMaxLife = Math.Max(20, currentBaseLife + hpDelta);

            tPlayer.statLifeMax = targetMaxLife;
            tPlayer.statLifeMax2 = targetMaxLife;

            if (tPlayer.statLife > targetMaxLife)
            {
                tPlayer.statLife = targetMaxLife;
            }
            else if (tPlayer.statLife <= 0)
            {
                tPlayer.statLife = Math.Min(targetMaxLife, 100);
            }

            _lastSyncedMaxLife[player.Index] = targetMaxLife;
            NetMessage.SendData((int)PacketTypes.PlayerHp, -1, -1, null, player.Index);
            NetMessage.SendData((int)PacketTypes.PlayerHp, player.Index, -1, null, player.Index);
        }

        private void ApplyClassStats(TSPlayer player, PlayerClassState state)
        {
            if (IsPlayerDeadState(player)) return;

            var tPlayer = player.TPlayer;
            int currentBaseLife = GetTrackedBaseLife(player);
            int hpDelta = GetMaxHpDelta(state);
            int targetMaxLife = Math.Max(20, currentBaseLife + hpDelta);

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

        private PlayerClassState GetStateFromDB(int accountId)
        {
            if (_db == null) return null;
            try
            {
                EnsureConnectionOpen();
                using (var cmd = _db.CreateCommand())
                {
                    cmd.CommandText = "SELECT ClassName, StarterChoice, BaseLife FROM PlayerClasses WHERE AccountID = @0";
                    var p = cmd.CreateParameter();
                    p.ParameterName = "@0";
                    p.Value = accountId;
                    cmd.Parameters.Add(p);

                    using (var reader = cmd.ExecuteReader())
                    {
                        if (reader.Read() && !reader.IsDBNull(0))
                        {
                            string cName = reader.GetString(0);
                            int choice = !reader.IsDBNull(1) ? reader.GetInt32(1) : 0;
                            int baseLife = !reader.IsDBNull(2) ? reader.GetInt32(2) : BaseStartHp;
                            return new PlayerClassState
                            {
                                ClassName = cName,
                                StarterChoice = choice,
                                BaseLife = NormalizeBaseLifeValue(baseLife)
                            };
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                TShock.Log.Error($"[ClassSystem] GetStateFromDB error: {ex.Message}");
            }
            return null;
        }

        private void SaveStateToDB(int accountId, PlayerClassState state)
        {
            if (state == null || string.IsNullOrEmpty(state.ClassName)) return;
            try
            {
                int normalizedBase = NormalizeBaseLifeValue(state.BaseLife);
                ExecuteNonQuery(
                    "INSERT INTO PlayerClasses (AccountID, ClassName, StarterChoice, BaseLife) VALUES (@0, @1, @2, @3) ON CONFLICT(AccountID) DO UPDATE SET ClassName = excluded.ClassName, StarterChoice = excluded.StarterChoice, BaseLife = excluded.BaseLife",
                    ("@0", accountId), ("@1", state.ClassName), ("@2", state.StarterChoice), ("@3", normalizedBase));
            }
            catch (Exception ex)
            {
                TShock.Log.Error($"[ClassSystem] SaveStateToDB error: {ex.Message}");
            }
        }

        private void DeleteStateFromDB(int accountId)
        {
            try
            {
                ExecuteNonQuery("DELETE FROM PlayerClasses WHERE AccountID = @0", ("@0", accountId));
            }
            catch (Exception ex)
            {
                TShock.Log.Error($"[ClassSystem] DeleteStateFromDB error: {ex.Message}");
            }
        }
    }
}
