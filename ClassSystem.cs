using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.Xna.Framework;
using Terraria;
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
        public override string Description => "Account-bound class system with underworld lock before login, webbed lock only after login until class selection, starter gear replacement, strict weapon locks, tool damage only for Warrior, and real-time Max HP sync.";
        public override Version Version => new Version(15, 6, 4);

        private SqliteConnection _db;
        private bool _dbReady;
        private readonly Dictionary<int, string> _playerClasses = new Dictionary<int, string>();
        private readonly Dictionary<int, Vector2> _frozenPositions = new Dictionary<int, Vector2>();
        private readonly Dictionary<int, DateTime> _warnCooldown = new Dictionary<int, DateTime>();
        private readonly Dictionary<int, DateTime> _slotWarnCooldown = new Dictionary<int, DateTime>();
        private readonly Dictionary<int, int> _lastSyncedMaxLife = new Dictionary<int, int>();

        private const int MaxLifeCrystalsAllowed = 15;
        private const int BaseStartHp = 100;
        private const int HpPerLifeCrystal = 20;

        // Warrior stats
        private const double WarriorMeleeDamageMult = 0.10;
        private const double WarriorMaxHpMult = 0.15;
        private const double WarriorDefenseMult = 0.10;
        private const double WarriorMoveSpeedMult = -0.15;

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
        private const int MageMaxHpFlat = -30;
        private const double MageDefenseMult = -0.10;

        // Summoner stats
        private const double SummonerSummonDamageMult = 0.12;
        private const double SummonerWhipDamageMult = -0.15;
        private const int SummonerExtraMinions = 2;
        private const double SummonerMoveSpeedMult = 0.20;
        private const int SummonerMaxHpFlat = -40;
        private const double SummonerDefenseMult = -0.20;

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

        private readonly Dictionary<int, string> _restrictedArmorsAndAccessories = new Dictionary<int, string>
        {
            { ItemID.WarriorEmblem, "warrior" },
            { ItemID.RangerEmblem, "ranger" },
            { ItemID.SorcererEmblem, "mage" },
            { ItemID.SummonerEmblem, "summoner" },
            { ItemID.FeralClaws, "warrior" },
            { ItemID.MagicQuiver, "ranger" },
            { ItemID.ManaFlower, "mage" },
            { ItemID.PygmyNecklace, "summoner" }
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

                if (_db != null)
                {
                    try { _db.Dispose(); } catch { }
                    _db = null;
                }
            }
            base.Dispose(disposing);
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

            Commands.ChatCommands.Add(new Command("", CmdClass, "class")
            {
                HelpText = "Select or view your class: /class [classname]"
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
                player.SendWarningMessage("[ClassSystem] Welcome to the Underworld! Log in with /login and pick a class with /class <warrior|ranger|mage|summoner>.");
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
                EnforceInventoryRestrictions(args.Player, savedClass);
                _lastSyncedMaxLife.Remove(args.Player.Index);
                ApplyClassStats(args.Player, savedClass);
                args.Player.SendSuccessMessage($"[ClassSystem] Welcome back! Your class is: {savedClass.ToUpper()}");
            }
            else
            {
                ApplyUnderworldLock(args.Player, true);
                args.Player.SendWarningMessage("[ClassSystem] You are webbed in the Underworld until you select a class using /class <warrior|ranger|mage|summoner>.");
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
            _lastSyncedMaxLife.Remove(args.Who);
        }

        private bool IsGatheringTool(Item item)
        {
            if (item == null || item.IsAir || item.type == ItemID.None) return false;
            return item.pick > 0 || item.axe > 0 || item.hammer > 0;
        }

        private bool IsItemAllowedInInventory(Item item, string className)
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

        private void EnforceInventoryRestrictions(TSPlayer player, string className)
        {
            if (player?.TPlayer?.inventory == null) return;

            for (int i = 0; i < 50; i++)
            {
                Item invItem = player.TPlayer.inventory[i];
                if (invItem == null || invItem.IsAir || invItem.type == ItemID.None) continue;

                if (!IsItemAllowedInInventory(invItem, className))
                {
                    string itemName = invItem.Name;
                    player.TPlayer.inventory[i] = new Item();
                    NetMessage.SendData((int)PacketTypes.PlayerSlot, -1, -1, null, player.Index, i, 0, 0, 0);
                    NetMessage.SendData((int)PacketTypes.PlayerSlot, player.Index, -1, null, player.Index, i, 0, 0, 0);

                    if (!_slotWarnCooldown.TryGetValue(player.Index, out DateTime lastWarn) || (DateTime.UtcNow - lastWarn).TotalSeconds >= 3)
                    {
                        _slotWarnCooldown[player.Index] = DateTime.UtcNow;
                        player.SendWarningMessage($"[ClassSystem] Blocked '{itemName}' — not allowed for class {className.ToUpper()}.");
                    }
                }
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
                        player.SendWarningMessage("[ClassSystem] Select a class using /class <warrior|ranger|mage|summoner> to break the web and leave the Underworld.");
                    }
                }
                else
                {
                    _frozenPositions.Remove(player.Index);
                    ClearWebBuff(player);
                    Item held = player.TPlayer.HeldItem;
                    if (!IsItemAllowedInInventory(held, currentClass))
                    {
                        EnforceInventoryRestrictions(player, currentClass);
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

            Item testItem = new Item();
            testItem.SetDefaults(args.Type);

            if (!IsItemAllowedInInventory(testItem, currentClass))
            {
                args.Handled = true;
                player.TPlayer.inventory[args.Slot] = new Item();
                NetMessage.SendData((int)PacketTypes.PlayerSlot, -1, -1, null, player.Index, args.Slot, 0, 0, 0);
                NetMessage.SendData((int)PacketTypes.PlayerSlot, player.Index, -1, null, player.Index, args.Slot, 0, 0, 0);

                if (!_slotWarnCooldown.TryGetValue(player.Index, out DateTime lastWarn) || (DateTime.UtcNow - lastWarn).TotalSeconds >= 3)
                {
                    _slotWarnCooldown[player.Index] = DateTime.UtcNow;
                    player.SendWarningMessage($"[ClassSystem] Blocked '{Lang.GetItemNameValue(args.Type)}' — not allowed for {currentClass.ToUpper()}.");
                }
            }
        }

        private void OnGameUpdate(EventArgs args)
        {
            foreach (var player in TShock.Players)
            {
                if (player == null || !player.Active) continue;

                if (!player.IsLoggedIn || player.Account == null)
                {
                    ApplyUnderworldLock(player, false);
                    continue;
                }

                string playerClass = GetPlayerClass(player);
                if (string.IsNullOrEmpty(playerClass))
                {
                    ApplyUnderworldLock(player, true);
                    continue;
                }

                ClearWebBuff(player);
                ApplyClassStats(player, playerClass);

                if (playerClass == "ranger" && RangerArcheryBuff)
                {
                    foreach (var proj in Main.projectile)
                    {
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
            if (args.Player == null) return;
            int playerIndex = Array.IndexOf(Main.player, args.Player);
            if (playerIndex < 0 || playerIndex >= TShock.Players.Length) return;

            var tsPlayer = TShock.Players[playerIndex];
            if (tsPlayer == null || !tsPlayer.IsLoggedIn || tsPlayer.Account == null) return;

            string playerClass = GetPlayerClass(tsPlayer);
            if (string.IsNullOrEmpty(playerClass)) return;

            var heldItem = args.Player.HeldItem;
            if (heldItem == null) return;

            if (IsGatheringTool(heldItem))
            {
                if (string.Equals(playerClass, "warrior", StringComparison.OrdinalIgnoreCase))
                {
                    if (heldItem.melee)
                    {
                        args.Damage = (int)Math.Round(args.Damage * (1.0 + WarriorMeleeDamageMult));
                    }
                }
                else
                {
                    args.Damage = 0;
                }
                return;
            }

            if (!IsItemAllowedInInventory(heldItem, playerClass))
            {
                args.Damage = 0;
                EnforceInventoryRestrictions(tsPlayer, playerClass);
                return;
            }

            if (playerClass == "warrior" && heldItem.melee)
            {
                args.Damage = (int)Math.Round(args.Damage * (1.0 + WarriorMeleeDamageMult));
            }
            else if (playerClass == "ranger" && heldItem.ranged)
            {
                args.Damage = (int)Math.Round(args.Damage * (1.0 + RangerRangedDamageMult));
            }
            else if (playerClass == "mage" && heldItem.magic)
            {
                args.Damage = (int)Math.Round(args.Damage * (1.0 + MageMagicDamageMult));
            }
            else if (playerClass == "summoner" && heldItem.summon)
            {
                double mult = 1.0 + SummonerSummonDamageMult;
                if (heldItem.useStyle == ItemUseStyleID.Swing && heldItem.noMelee)
                {
                    mult += SummonerWhipDamageMult;
                }
                args.Damage = (int)Math.Round(args.Damage * mult);
            }
        }

        private void ClearDefaultStarterWeapons(TSPlayer player)
        {
            for (int i = 0; i < 50; i++)
            {
                Item inv = player.TPlayer.inventory[i];
                if (inv == null || inv.IsAir) continue;
                if (IsGatheringTool(inv)) continue;

                if (inv.type == ItemID.CopperShortsword || inv.type == ItemID.CopperBroadsword || inv.type == ItemID.WoodenSword)
                {
                    player.TPlayer.inventory[i] = new Item();
                    NetMessage.SendData((int)PacketTypes.PlayerSlot, -1, -1, null, player.Index, i, 0, 0, 0);
                    NetMessage.SendData((int)PacketTypes.PlayerSlot, player.Index, -1, null, player.Index, i, 0, 0, 0);
                }
            }
        }

        private void GiveStarterItems(TSPlayer player, string className)
        {
            ClearDefaultStarterWeapons(player);

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

            EnforceInventoryRestrictions(player, className);
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
                string info = string.IsNullOrEmpty(currentClass) ? "None (use /class <warrior|ranger|mage|summoner>)" : currentClass.ToUpper();
                args.Player.SendInfoMessage($"[ClassSystem] Current class: {info}");
                return;
            }

            string chosen = args.Parameters[0].ToLowerInvariant();
            if (!_availableClasses.Contains(chosen))
            {
                args.Player.SendErrorMessage("[ClassSystem] Invalid class. Available: warrior, ranger, mage, summoner");
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
            _lastSyncedMaxLife.Remove(args.Player.Index);
            ApplyClassStats(args.Player, chosen);
            args.Player.SendSuccessMessage($"[ClassSystem] Class selected: {chosen.ToUpper()}! Web cleared, starter weapons replaced, and stats synced.");
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
                target.SendWarningMessage("[ClassSystem] Your class has been reset. You are webbed in the Underworld until you select a class with /class.");
                args.Player.SendSuccessMessage($"Reset class for {target.Name}.");
                return;
            }

            if (!_availableClasses.Contains(option))
            {
                args.Player.SendErrorMessage("Invalid class. Use warrior, ranger, mage, summoner, or none.");
                return;
            }

            SaveClassToDB(target.Account.ID, option);
            _playerClasses[target.Account.ID] = option;
            ClearWebAndTeleportToSpawn(target);
            GiveStarterItems(target, option);
            _lastSyncedMaxLife.Remove(target.Index);
            ApplyClassStats(target, option);
            target.SendSuccessMessage($"[ClassSystem] Admin set your class to {option.ToUpper()}.");
            args.Player.SendSuccessMessage($"Set {target.Name}'s class to {option.ToUpper()}.");
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
            int maxCrystals = Math.Max(0, MaxLifeCrystalsAllowed);
            int baseCapLife = BaseStartHp + maxCrystals * HpPerLifeCrystal;
            int currentBaseLife = Math.Min(Math.Max(BaseStartHp, tPlayer.statLifeMax), baseCapLife);

            int targetMaxLife = currentBaseLife;

            if (className == "warrior")
            {
                targetMaxLife = (int)Math.Round(currentBaseLife * (1.0 + WarriorMaxHpMult));
                tPlayer.statDefense = (int)Math.Round(tPlayer.statDefense * (1.0 + WarriorDefenseMult));
                tPlayer.moveSpeed *= (float)(1.0 + WarriorMoveSpeedMult);
            }
            else if (className == "ranger")
            {
                targetMaxLife = Math.Max(40, currentBaseLife + RangerMaxHpFlat);
                tPlayer.statDefense = (int)Math.Round(tPlayer.statDefense * (1.0 + RangerDefenseMult));
                tPlayer.moveSpeed *= (float)(1.0 + RangerMoveSpeedMult);
            }
            else if (className == "mage")
            {
                targetMaxLife = Math.Max(40, currentBaseLife + MageMaxHpFlat);
                tPlayer.statManaMax2 += MageMaxManaFlat;
                tPlayer.statDefense = (int)Math.Round(tPlayer.statDefense * (1.0 + MageDefenseMult));
                tPlayer.moveSpeed *= (float)(1.0 + MageMoveSpeedMult);
            }
            else if (className == "summoner")
            {
                targetMaxLife = Math.Max(40, currentBaseLife + SummonerMaxHpFlat);
                tPlayer.maxMinions += SummonerExtraMinions;
                tPlayer.statDefense = (int)Math.Round(tPlayer.statDefense * (1.0 + SummonerDefenseMult));
                tPlayer.moveSpeed *= (float)(1.0 + SummonerMoveSpeedMult);
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
