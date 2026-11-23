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
        public override string Description => "Account-bound class system with underworld lock before login, webbed lock after login until class selection, starter gear, instant item drop on cross-class usage (ignoring coins/ammo/misc), Max HP modifiers per class + Summoning buff (+1 minion) only (other stat buffs/nerfs removed), quick /c shortcuts, Life Crystal limit, and real-time HP sync.";
        public override Version Version => new Version(15, 24, 4);

        private SqliteConnection _db;
        private bool _dbReady;
        private readonly Dictionary<int, string> _playerClasses = new Dictionary<int, string>();
        private readonly Dictionary<int, Vector2> _frozenPositions = new Dictionary<int, Vector2>();
        private readonly Dictionary<int, DateTime> _warnCooldown = new Dictionary<int, DateTime>();
        private readonly Dictionary<int, DateTime> _dropWarnCooldown = new Dictionary<int, DateTime>();
        private readonly Dictionary<int, DateTime> _crystalWarnCooldown = new Dictionary<int, DateTime>();
        private readonly Dictionary<int, DateTime> _summonBuffCooldown = new Dictionary<int, DateTime>();
        private readonly Dictionary<int, int> _lastSyncedMaxLife = new Dictionary<int, int>();
        private DateTime _lastTickSync = DateTime.MinValue;

        private const int MaxLifeCrystalsAllowed = 15;
        private const int BaseStartHp = 100;
        private const int HpPerLifeCrystal = 20;

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

        private void DropDisallowedItemAndClearSlot(TSPlayer player, int slotIndex, Item itemToDrop, int fallbackType, int fallbackStack)
        {
            if (player?.TPlayer == null) return;

            int itemType = itemToDrop != null && !itemToDrop.IsAir && itemToDrop.type > ItemID.None
                ? itemToDrop.type
                : fallbackType;

            int stack = itemToDrop != null && itemToDrop.stack > 0
                ? itemToDrop.stack
                : Math.Max(1, fallbackStack);

            byte prefix = itemToDrop != null ? itemToDrop.prefix : (byte)0;
            string itemName = itemToDrop != null && !string.IsNullOrWhiteSpace(itemToDrop.Name)
                ? itemToDrop.Name
                : Lang.GetItemNameValue(itemType);

            player.TPlayer.controlUseItem = false;
            player.TPlayer.controlUseTile = false;
            player.TPlayer.itemAnimation = 0;
            player.TPlayer.itemAnimationMax = 0;
            player.TPlayer.itemTime = 0;
            CancelActivePlayerProjectiles(player);

            if (itemType > ItemID.None)
            {
                int droppedIndex = Item.NewItem(
                    new MiscEntitySource(),
                    (int)player.TPlayer.position.X,
                    (int)player.TPlayer.position.Y,
                    player.TPlayer.width,
                    player.TPlayer.height,
                    itemType,
                    stack,
                    false,
                    0,
                    NewItemOwnership.None
                );

                if (droppedIndex >= 0 && droppedIndex < Main.maxItems)
                {
                    if (prefix > 0)
                    {
                        Main.item[droppedIndex].prefix = prefix;
                    }
                    NetMessage.SendData((int)PacketTypes.ItemDrop, -1, -1, null, droppedIndex);
                    NetMessage.SendData((int)PacketTypes.ItemOwner, -1, -1, null, droppedIndex);
                }
            }

            if (slotIndex >= 0 && slotIndex < player.TPlayer.inventory.Length)
            {
                player.TPlayer.inventory[slotIndex] = new Item();
                player.TPlayer.inventory[slotIndex].TurnToAir();
                NetMessage.SendData((int)PacketTypes.PlayerSlot, -1, -1, null, player.Index, slotIndex, 0, 0, 0);
                NetMessage.SendData((int)PacketTypes.PlayerSlot, player.Index, -1, null, player.Index, slotIndex, 0, 0, 0);
            }

            if (!_dropWarnCooldown.TryGetValue(player.Index, out DateTime lastWarn) || (DateTime.UtcNow - lastWarn).TotalSeconds >= 2)
            {
                _dropWarnCooldown[player.Index] = DateTime.UtcNow;
                string className = GetPlayerClass(player) ?? "class";
                player.SendWarningMessage($"[ClassSystem] Dropped '{itemName}' — cannot be used by {className.ToUpper()}.");
            }
        }

        private void DropHeldDisallowedItemIfAny(TSPlayer player, string currentClass)
        {
            if (player?.TPlayer == null || string.IsNullOrEmpty(currentClass)) return;

            int selected = player.TPlayer.selectedItem;
            if (selected >= 0 && selected < player.TPlayer.inventory.Length)
            {
                Item held = player.TPlayer.inventory[selected];
                if (held != null && !held.IsAir && held.type > ItemID.None && !IsItemAllowedForClass(held, currentClass))
                {
                    DropDisallowedItemAndClearSlot(player, selected, held, held.type, held.stack);
                }
            }
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
            _dropWarnCooldown.Remove(args.Who);
            _crystalWarnCooldown.Remove(args.Who);
            _summonBuffCooldown.Remove(args.Who);
            _lastSyncedMaxLife.Remove(args.Who);
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

            if (IsCoinOrAmmoOrMisc(item)) return true;

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
                        BlockLifeCrystalUsage(player);
                        NotifyBlockedLifeCrystal(player);
                    }
                    else if (player.TPlayer.controlUseItem || (held != null && !held.IsAir && !IsItemAllowedForClass(held, currentClass)))
                    {
                        DropHeldDisallowedItemIfAny(player, currentClass);
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

            Item testItem = new Item();
            testItem.SetDefaults(args.Type);
            testItem.stack = Math.Max(1, (int)args.Stack);

            if (!IsItemAllowedForClass(testItem, currentClass))
            {
                args.Handled = true;
                Item existing = (args.Slot >= 0 && args.Slot < player.TPlayer.inventory.Length)
                    ? player.TPlayer.inventory[args.Slot]
                    : null;
                DropDisallowedItemAndClearSlot(player, args.Slot, existing, args.Type, args.Stack);
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
            }
        }

        private void OnNpcStrike(NpcStrikeEventArgs args)
        {
            if (args.Player == null || args.Npc == null) return;
            int playerIndex = Array.IndexOf(Main.player, args.Player);
            if
