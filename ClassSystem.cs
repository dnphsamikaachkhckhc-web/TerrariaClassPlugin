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
    [ApiVersion(2, 1)]
    public class ClassSystem : TerrariaPlugin
    {
        public override string Name => "ClassSystem";
        public override string Author => "CustomDev";
        public override string Description => "Account-bound class system with underworld lock before login, webbed lock only after login until class selection, starter gear replacement, strict cross-class weapon blocks without deleting items, +1 minion via Summoning buff (2 total minions at start), direct nerf on all whips from 1.0 base, boosted summon damage, gathering tool damage rules, quick /c shortcuts, hard Life Crystal usage block, custom stat modifiers, smooth update loop, and real-time HP/Mana sync.";
        public override Version Version => new Version(15, 20, 12);

        private SqliteConnection _db;
        private bool _dbReady;
        private readonly Dictionary<int, string> _playerClasses = new Dictionary<int, string>();
        private readonly Dictionary<int, Vector2> _frozenPositions = new Dictionary<int, Vector2>();
        private readonly Dictionary<int, DateTime> _warnCooldown = new Dictionary<int, DateTime>();
        private readonly Dictionary<int, DateTime> _slotWarnCooldown = new Dictionary<int, DateTime>();
        private readonly Dictionary<int, DateTime> _crystalWarnCooldown = new Dictionary<int, DateTime>();
        private readonly Dictionary<int, DateTime> _summonBuffCooldown = new Dictionary<int, DateTime>();
        private readonly Dictionary<int, int> _lastSyncedMaxLife = new Dictionary<int, int>();
        private readonly Dictionary<int, int> _lastSyncedMaxMana = new Dictionary<int, int>();
        private DateTime _lastTickSync = DateTime.MinValue;

        private const int MaxLifeCrystalsAllowed = 15;
        private const int BaseStartHp = 100;
        private const int HpPerLifeCrystal = 20;

        // Direct nerf on ALL whips from 1.0 base (e.g., -0.25 = -25% damage)
        private const double WhipNerfMult = -0.25;

        // Warrior stats
        private const double WarriorMeleeDamageMult = 0.10;
        private const int WarriorMaxHpFlat = 20;
        private const double WarriorDefenseMult = 0.10;
        private const double WarriorMoveSpeedMult = -0.15;
        private const double WarriorLifeRegenMult = -0.20;

        // Ranger stats
        private const double RangerRangedDamageMult = 0.10;
        private const double RangerMoveSpeedMult = 0.30;
        private const int RangerMaxHpFlat = -40;
        private const double RangerDefenseMult = -0.15;
        private const bool RangerArcheryBuff = true;

        // Mage stats
        private const double MageMagicDamageMult = 0.12;
        private const double MageMoveSpeedMult = 0.15;
        private const int MageMaxManaFlat = 80;
        private const int MageMaxHpFlat = -60;
        private const double MageDefenseMult = -0.10;

        // Summoner stats (+1 minion from Summoning buff = 2 minions at start; boosted summon damage; -80 Max HP; faster life regen)
        private const double SummonerSummonDamageMult = 0.12;
        private const double SummonerMoveSpeedMult = 0.20;
        private const int SummonerMaxHpFlat = -80;
        private const double SummonerDefenseMult = -0.20;
        private const double SummonerLifeRegenMult = 0.15;

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

        private readonly Dictionary<int, string> _restrictedArmorsAndAccessories = new Dictionary<int, string>
        {
            { ItemID.WarriorEmblem, "warrior" },
            { ItemID.RangerEmblem, "ranger" },
            { ItemID.SorcererEmblem, "mage" },
            { ItemID.SummonerEmblem, "summoner" },
            { ItemID.FeralClaws, "warrior" },
            { ItemID.MagicQuiver, "ranger" },
            { ItemID.ManaFlower, "mage" },
            { ItemID.PygmyNecklace, "summoner" },
            { ItemID.NecromanticScroll, "summoner" },
            { ItemID.PapyrusScarab, "summoner" },
            { ItemID.HerculesBeetle, "summoner" }
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
                GetDataHandlers.PlayerDamage -= OnPlayerDamage;

                if (_db != null)
                {
                    try { _db.Dispose(); } catch { }
                    _db = null;
                }
            }
            base.Dispose(disposing);
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

        private void BlockItemUsage(TSPlayer player, bool cancelProjectiles)
        {
            if (player?.TPlayer == null) return;

            player.TPlayer.controlUseItem = false;
            player.TPlayer.controlUseTile = false;
            player.TPlayer.itemAnimation = 0;
            player.TPlayer.itemAnimationMax = 0;
            player.TPlayer.itemTime = 0;

            if (cancelProjectiles)
            {
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
        }

        private bool IsWhipItem(Item item)
        {
            if (item == null || item.IsAir || item.type == ItemID.None) return false;

            if (item.summon && item.useStyle == ItemUseStyleID.Swing && item.noMelee)
                return true;

            return item.type == ItemID.BlandWhip ||
                   item.type == ItemID.ThornWhip ||
                   item.type == ItemID.BoneWhip ||
                   item.type == ItemID.CoolWhip ||
                   item.type == ItemID.SwordWhip ||
                   item.type == ItemID.MaceWhip ||
                   item.type == ItemID.ScytheWhip ||
                   item.type == ItemID.RainbowWhip;
        }

        private bool HasClassLifeRegen(TSPlayer tsPlayer, string playerClass)
        {
            if (tsPlayer?.TPlayer == null) return false;

            double classRegenMult = 0.0;
            if (string.Equals(playerClass, "warrior", StringComparison.OrdinalIgnoreCase))
                classRegenMult = WarriorLifeRegenMult;
            else if (string.Equals(playerClass, "summoner", StringComparison.OrdinalIgnoreCase))
                classRegenMult = SummonerLifeRegenMult;

            return classRegenMult > 0.0 && tsPlayer.TPlayer.lifeRegen > 0;
        }

        private void EnsureSummonerSummoningBuff(TSPlayer player)
        {
            if (player == null || player.TPlayer == null || !player.Active) return;

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

        private void OnPlayerDamage(object sender, GetDataHandlers.PlayerDamageEventArgs args)
        {
            var player = TShock.Players[args.ID];
            if (player == null || !player.Active || !player.IsLoggedIn || player.Account == null) return;

            string playerClass = GetPlayerClass(player);
            if (string.IsNullOrEmpty(playerClass)) return;

            double defenseMult = 0.0;
            if (playerClass == "warrior") defenseMult = WarriorDefenseMult;
            else if (playerClass == "ranger") defenseMult = RangerDefenseMult;
            else if (playerClass == "mage") defenseMult = MageDefenseMult;
            else if (playerClass == "summoner") defenseMult = SummonerDefenseMult;

            if (Math.Abs(defenseMult) < 0.001) return;

            int baseDef = Math.Max(0, player.TPlayer.statDefense);
            int adjustedDef = Math.Max(0, (int)Math.Round(baseDef * (1.0 + defenseMult)));
            int defDiff = adjustedDef - baseDef;

            int hpDelta = (int)Math.Round(defDiff * 0.75);
            if (hpDelta > 0)
            {
                int healBack = Math.Min(hpDelta, Math.Max(0, args.Damage - 1));
                if (healBack > 0)
                {
                    player.TPlayer.statLife = Math.Min(player.TPlayer.statLifeMax2, player.TPlayer.statLife + healBack);
                    NetMessage.SendData((int)PacketTypes.PlayerHp, -1, -1, null, player.Index);
                    NetMessage.SendData((int)PacketTypes.PlayerHp, player.Index, -1, null, player.Index);
                }
            }
            else if (hpDelta < 0)
            {
                int extraDamage = -hpDelta;
                player.TPlayer.statLife = Math.Max(1, player.TPlayer.statLife - extraDamage);
                NetMessage.SendData((int)PacketTypes.PlayerHp, -1, -1, null, player.Index);
                NetMessage.SendData((int)PacketTypes.PlayerHp, player.Index, -1, null, player.Index);
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
                _lastSyncedMaxLife.Remove(args.Player.Index);
                _lastSyncedMaxMana.Remove(args.Player.Index);
                ApplyClassStats(args.Player, savedClass);
                if (savedClass == "summoner")
                {
                    EnsureSummonerSummoningBuff(args.Player);
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
            _slotWarnCooldown.Remove(args.Who);
            _crystalWarnCooldown.Remove(args.Who);
            _summonBuffCooldown.Remove(args.Who);
            _lastSyncedMaxLife.Remove(args.Who);
            _lastSyncedMaxMana.Remove(args.Who);
        }

        private bool IsGatheringTool(Item item)
        {
            if (item == null || item.IsAir || item.type == ItemID.None) return false;
            return item.pick > 0 || item.axe > 0 || item.hammer > 0;
        }

        private bool IsItemAllowedForClass(Item item, string className)
        {
            if (item == null || item.IsAir || item.type == ItemID.None) return true;

            if (IsGatheringTool(item)) return true;

            if (_restrictedArmorsAndAccessories.TryGetValue(item.type, out string reqClass))
            {
                return string.Equals(className, reqClass, StringComparison.OrdinalIgnoreCase);
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

        private void NotifyBlockedItem(TSPlayer player, string itemName, string className)
        {
            if (player == null || string.IsNullOrWhiteSpace(itemName)) return;
            if (!_slotWarnCooldown.TryGetValue(player.Index, out DateTime lastWarn) || (DateTime.UtcNow - lastWarn).TotalSeconds >= 3)
            {
                _slotWarnCooldown[player.Index] = DateTime.UtcNow;
                player.SendWarningMessage($"[ClassSystem] Blocked '{itemName}' — cannot be used by {className.ToUpper()} (kept in inventory).");
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
                    Item held = player.TPlayer.HeldItem;

                    if (held != null && !held.IsAir && held.type == ItemID.LifeCrystal && IsLifeCrystalAtLimit(player))
                    {
                        BlockItemUsage(player, false);
                        NotifyBlockedLifeCrystal(player);
                    }
                    else if (held != null && !held.IsAir && !IsItemAllowedForClass(held, currentClass))
                    {
                        BlockItemUsage(player, true);
                        NotifyBlockedItem(player, held.Name, currentClass);
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
                BlockItemUsage(player, false);
                NotifyBlockedLifeCrystal(player);
                return;
            }

            Item testItem = new Item();
            testItem.SetDefaults(args.Type);

            if (!IsItemAllowedForClass(testItem, currentClass))
            {
                args.Handled = true;
                BlockItemUsage(player, true);
                NotifyBlockedItem(player, Lang.GetItemNameValue(args.Type), currentClass);
            }
        }

        private void OnGameUpdate(EventArgs args)
        {
            bool runPeriodicSync = (DateTime.UtcNow - _lastTickSync).TotalMilliseconds >= 100;
            if (runPeriodicSync)
            {
                _lastTickSync = DateTime.UtcNow;
            }

            foreach (var player in TShock.Players)
            {
                if (player == null || !player.Active) continue;

                if (!player.IsLoggedIn || player.Account == null)
                {
                    if (runPeriodicSync)
                    {
                        ApplyUnderworldLock(player, false);
                    }
                    continue;
                }

                string playerClass = GetPlayerClass(player);
                if (string.IsNullOrEmpty(playerClass))
                {
                    if (runPeriodicSync)
                    {
                        ApplyUnderworldLock(player, true);
                    }
                    continue;
                }

                ApplyClassStats(player, playerClass);

                if (runPeriodicSync)
                {
                    ClearWebBuff(player);

                    if (playerClass == "summoner")
                    {
                        EnsureSummonerSummoningBuff(player);
                    }
                }

                if (runPeriodicSync && playerClass == "ranger" && RangerArcheryBuff)
                {
                    for (int i = 0; i < Main.maxProjectiles; i++)
                    {
                        var proj = Main.projectile[i];
                        if (proj != null && proj.active && proj.owner == player.Index && proj.ranged && proj.ai[1] == 0f)
                        {
                            proj.velocity *= 1.02f;
                            proj.ai[1] = 1f;
                        }
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

            // 1. บล็อกอาวุธ/ของไม่ตรงคลาสก่อนทุกอย่าง (ไม่ลบของออกจากตัว)
            if (heldItem != null && !heldItem.IsAir && !IsItemAllowedForClass(heldItem, playerClass))
            {
                args.Damage = 0;
                BlockItemUsage(tsPlayer, true);
                NotifyBlockedItem(tsPlayer, heldItem.Name, playerClass);
                return;
            }

            // 2. เนิฟแส้ทุกกระบอก (คิดจากฐาน 1.0 + WhipNerfMult)
            if (IsWhipItem(heldItem))
            {
                if (args.Damage > 0)
                {
                    double whipMult = Math.Max(0.0, 1.0 + WhipNerfMult);
                    int nerfedDamage = (int)Math.Round(args.Damage * whipMult);
                    args.Damage = Math.Max(1, nerfedDamage);
                }
                return;
            }

            // 3. กฎเครื่องมือขุด/ตัด/ทุบ
            if (heldItem != null && IsGatheringTool(heldItem))
            {
                if (!HasClassLifeRegen(tsPlayer, playerClass))
                {
                    args.Damage = 0;
                    return;
                }

                if (string.Equals(playerClass, "summoner", StringComparison.OrdinalIgnoreCase) && args.Damage > 0)
                {
                    args.Damage = Math.Max(1, (int)Math.Round(args.Damage * (1.0 + SummonerSummonDamageMult)));
                }
                return;
            }

            // 4. คลาส Summoner (ดาเมจซัมม่อน/การโจมตีที่ไม่ใช่แส้)
            if (playerClass == "summoner")
            {
                if (args.Damage > 0)
                {
                    args.Damage = Math.Max(1, (int)Math.Round(args.Damage * (1.0 + SummonerSummonDamageMult)));
                }
                return;
            }

            if (heldItem == null) return;

            // 5. ตัวคูณดาเมจคลาสอื่นๆ
            if (args.Damage > 0)
            {
                if (playerClass == "warrior" && heldItem.melee)
                {
                    args.Damage = Math.Max(1, (int)Math.Round(args.Damage * (1.0 + WarriorMeleeDamageMult)));
                }
                else if (playerClass == "ranger" && heldItem.ranged)
                {
                    args.Damage = Math.Max(1, (int)Math.Round(args.Damage * (1.0 + RangerRangedDamageMult)));
                }
                else if (playerClass == "mage" && heldItem.magic)
                {
                    args.Damage = Math.Max(1, (int)Math.Round(args.Damage * (1.0 + MageMagicDamageMult)));
                }
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
            GiveStarterItems(args.Player, chosen);
            if (chosen == "summoner")
            {
                EnsureSummonerSummoningBuff(args.Player);
            }
            _lastSyncedMaxLife.Remove(args.Player.Index);
            _lastSyncedMaxMana.Remove(args.Player.Index);
            ApplyClassStats(args.Player, chosen);
            args.Player.SendSuccessMessage($"[ClassSystem] Class selected: {chosen.ToUpper()}! Web cleared, starter gear equipped, and stats synced.");
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
                _lastSyncedMaxMana.Remove(target.Index);
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
            GiveStarterItems(target, resolvedOption);
            if (resolvedOption == "summoner")
            {
                EnsureSummonerSummoningBuff(target);
            }
            _lastSyncedMaxLife.Remove(target.Index);
            _lastSyncedMaxMana.Remove(target.Index);
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
            int targetMaxMana = tPlayer.statManaMax;

            if (className == "warrior")
            {
                targetMaxLife = Math.Max(40, currentBaseLife + WarriorMaxHpFlat);
                tPlayer.moveSpeed *= (float)(1.0 + WarriorMoveSpeedMult);
                if (tPlayer.lifeRegen > 0)
                {
                    tPlayer.lifeRegen = (int)Math.Round(tPlayer.lifeRegen * (1.0 + WarriorLifeRegenMult));
                }
            }
            else if (className == "ranger")
            {
                targetMaxLife = Math.Max(40, currentBaseLife + RangerMaxHpFlat);
                tPlayer.moveSpeed *= (float)(1.0 + RangerMoveSpeedMult);
            }
            else if (className == "mage")
            {
                targetMaxLife = Math.Max(40, currentBaseLife + MageMaxHpFlat);
                targetMaxMana = tPlayer.statManaMax + MageMaxManaFlat;
                tPlayer.statManaMax2 = targetMaxMana;
                tPlayer.moveSpeed *= (float)(1.0 + MageMoveSpeedMult);
            }
            else if (className == "summoner")
            {
                targetMaxLife = Math.Max(20, currentBaseLife + SummonerMaxHpFlat);
                tPlayer.moveSpeed *= (float)(1.0 + SummonerMoveSpeedMult);
                if (tPlayer.lifeRegen > 0)
                {
                    tPlayer.lifeRegen = (int)Math.Round(tPlayer.lifeRegen * (1.0 + SummonerLifeRegenMult));
                }
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

            if (className == "mage" && (!_lastSyncedMaxMana.TryGetValue(player.Index, out int lastMaxMana) || lastMaxMana != targetMaxMana))
            {
                _lastSyncedMaxMana[player.Index] = targetMaxMana;
                NetMessage.SendData((int)PacketTypes.PlayerMana, -1, -1, null, player.Index);
                NetMessage.SendData((int)PacketTypes.PlayerMana, player.Index, -1, null, player.Index);
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
