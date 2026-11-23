using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.DB;
using TShockAPI.Hooks;

namespace ClassSystemPlugin
{
    [ApiVersion(2, 1)]
    public class ClassSystem : TerrariaPlugin
    {
        public override string Name => "ClassSystem";
        public override string Author => "CustomDev";
        public override string Description => "Account-bound class system with movement lock before selection, starter weapon loadouts, item restrictions, Life Crystal cap, and custom buffs/nerfs.";
        public override Version Version => new Version(15, 0, 0);

        private IDbConnection _db;
        private readonly Dictionary<int, string> _playerClasses = new Dictionary<int, string>();
        private readonly Dictionary<int, Vector2> _frozenPositions = new Dictionary<int, Vector2>();
        private readonly Dictionary<int, DateTime> _warnCooldown = new Dictionary<int, DateTime>();
        private readonly Dictionary<int, DateTime> _regenTick = new Dictionary<int, DateTime>();
        private readonly Dictionary<int, DateTime> _warriorRegenPenaltyTick = new Dictionary<int, DateTime>();
        private readonly Dictionary<int, DateTime> _manaRegenTick = new Dictionary<int, DateTime>();
        private readonly Random _rng = new Random();

        // ============================================================
        // Buff / Nerf Configuration (Percentages: 0.10 = +10%, -0.10 = -10%; Flat: integer units)
        // Edit values here directly without affecting core mechanics.
        // ============================================================

        // --- LIFE CRYSTAL / HP CAP SETTINGS ---
        private const int MaxLifeCrystalsAllowed = 15;           // Max 15 Life Crystals (+300 HP from crystals -> 100 + 300 = 400 base HP)
        private const int BaseStartHp = 100;
        private const int HpPerLifeCrystal = 20;
        private const bool BlockLifeCrystalUseAboveCap = true;

        // --- WARRIOR / MELEE ---
        private const double WarriorMeleeDamageMult = 0.10;       // Melee damage +10%
        private const double WarriorMaxHpMult = 0.15;           // Max HP +15% (can exceed 400 max HP)
        private const double WarriorDefenseMult = 0.10;         // Defense +10%
        private const double WarriorMoveSpeedMult = -0.15;      // Movement speed -15%
        private const double WarriorLifeRegenPenaltyMult = -0.15; // Life regeneration -15% (no mana penalty)

        // --- RANGER ---
        private const double RangerRangedDamageMult = 0.10;     // Ranged damage +10%
        private const double RangerMoveSpeedMult = 0.30;        // Movement speed +30%
        private const double RangerAmmoSaveChance = 0.15;       // Ammo/arrow refund chance 15%
        private const int RangerMaxHpFlat = -60;                // Max HP -60 flat
        private const double RangerDefenseMult = -0.15;         // Defense -15%
        private const bool RangerArcheryBuff = true;            // Faster bow draw/velocity (Archery buff)

        // --- MAGE ---
        private const double MageMagicDamageMult = 0.12;        // Magic damage +12%
        private const double MageMoveSpeedMult = 0.15;          // Movement speed +15%
        private const int MageMaxManaFlat = 80;                 // Max Mana +80 flat
        private const double MageManaRegenMult = 0.15;          // Mana regeneration rate +15%
        private const int MageMaxHpFlat = -40;                  // Max HP -40 flat
        private const double MageDefenseMult = -0.10;           // Defense -10%

        // --- SUMMONER ---
        private const double SummonerSummonDamageMult = 0.12;   // Summon damage +12%
        private const double SummonerWhipDamageMult = -0.15;    // Whip damage nerf -15%
        private const int SummonerExtraMinions = 2;             // Bonus minion slots +2
        private const double SummonerLifeRegenMult = 0.15;      // Passive life regeneration +15%
        private const double SummonerMoveSpeedMult = 0.20;      // Movement speed +20%
        private const int SummonerMaxHpFlat = -80;              // Max HP -80 flat
        private const double SummonerDefenseMult = -0.20;       // Defense -20%

        // ============================================================
        // Starter Loadouts (Weapons only, no armor; replace starter sword)
        // ============================================================

        private const bool RemoveStarterSwordOnSelect = true;

        private readonly Dictionary<string, (int ItemId, int Stack)[]> _classStarterItems =
            new Dictionary<string, (int ItemId, int Stack)[]>
            {
                {
                    "warrior", new[]
                    {
                        (ItemID.CopperBroadsword, 1)
                    }
                },
                {
                    "ranger", new[]
                    {
                        (ItemID.WoodenBow, 1),
                        (ItemID.WoodenArrow, 500)
                    }
                },
                {
                    "mage", new[]
                    {
                        (ItemID.WandofSparking, 1),
                        (ItemID.LesserManaPotion, 15)
                    }
                },
                {
                    "summoner", new[]
                    {
                        (ItemID.FinchStaff, 1),
                        (ItemID.Cobwhip, 1) // Spider whip (Cobwhip)
                    }
                }
            };

        private readonly List<string> _availableClasses = new List<string>
        {
            "warrior",
            "ranger",
            "mage",
            "summoner"
        };

        // Class-specific armor/accessory restrictions (expandable with additional ItemIDs)
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
            ServerApi.Hooks.ProjectileNew.Register(this, OnProjectileNew);

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
                ServerApi.Hooks.ProjectileNew.Deregister(this, OnProjectileNew);

                PlayerHooks.PlayerPostLogin -= OnPlayerPostLogin;
                GetDataHandlers.PlayerUpdate -= OnPlayerUpdate;
                GetDataHandlers.PlayerSlot -= OnPlayerSlot;
            }
            base.Dispose(disposing);
        }

        private void SetupDatabase()
        {
            _db = TShock.DB;
            var table = new SqlTable("PlayerClasses",
                new SqlColumn("AccountID", MySqlDbType.Int32) { Primary = true, Unique = true },
                new SqlColumn("ClassName", MySqlDbType.VarChar, 32)
            );
            var creator = new SqlTableCreator(_db, new SqliteQueryCreator());
            creator.EnsureTableStructure(table);
        }

        private void OnGameInitialize(EventArgs args)
        {
            Commands.ChatCommands.Add(new Command("class.use", CmdClass, "class")
            {
                HelpText = "Select or view your class: /class [classname]"
            });

            Commands.ChatCommands.Add(new Command("class.admin", CmdSetClass, "setclass")
            {
                HelpText = "Admin command to change or reset a player's class: /setclass <player> <classname|none>"
            });
        }

        private void OnGreetPlayer(GreetPlayerEventArgs args)
        {
            var player = TShock.Players[args.Who];
            if (player != null && !player.IsLoggedIn)
            {
                player.SendInfoMessage("[ClassSystem] Please log in to your TShock account to use the class system.");
            }
        }

        private void OnPlayerPostLogin(PlayerPostLoginEventArgs args)
        {
            if (args.Player?.Account == null) return;

            int accountId = args.Player.Account.ID;
            string savedClass = GetClassFromDB(accountId);

            if (!string.IsNullOrEmpty(savedClass))
            {
                _playerClasses[accountId] = savedClass;
                _frozenPositions.Remove(args.Player.Index);
                ApplyClassStats(args.Player, savedClass);
                args.Player.SendSuccessMessage($"[ClassSystem] Welcome back! Your class is: {savedClass.ToUpper()}");
            }
            else
            {
                _frozenPositions[args.Player.Index] = args.Player.TPlayer.position;
                args.Player.SendWarningMessage("[ClassSystem] You cannot move until you select a class using /class <warrior|ranger|mage|summoner>.");
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
            _regenTick.Remove(args.Who);
            _warriorRegenPenaltyTick.Remove(args.Who);
            _manaRegenTick.Remove(args.Who);
        }

        private void OnPlayerUpdate(object sender, GetDataHandlers.PlayerUpdateEventArgs args)
        {
            var player = TShock.Players[args.PlayerId];
            if (player == null || !player.Active) return;

            if (player.IsLoggedIn && player.Account != null && !_playerClasses.ContainsKey(player.Account.ID))
            {
                if (_frozenPositions.TryGetValue(player.Index, out Vector2 frozenPos))
                {
                    player.Teleport(frozenPos.X, frozenPos.Y);
                    if (!_warnCooldown.TryGetValue(player.Index, out DateTime lastWarn) || (DateTime.UtcNow - lastWarn).TotalSeconds >= 4)
                    {
                        _warnCooldown[player.Index] = DateTime.UtcNow;
                        player.SendWarningMessage("[ClassSystem] Select a class first using /class <warrior|ranger|mage|summoner>.");
                    }
                }
            }
        }

        private void OnPlayerSlot(object sender, GetDataHandlers.PlayerSlotEventArgs args)
        {
            var player = TShock.Players[args.PlayerId];
            if (player == null || !player.Active || !player.IsLoggedIn || player.Account == null) return;

            if (_restrictedArmorsAndAccessories.TryGetValue(args.Type, out string requiredClass))
            {
                string currentClass = GetPlayerClass(player);
                if (!string.Equals(currentClass, requiredClass, StringComparison.OrdinalIgnoreCase))
                {
                    player.SendWarningMessage($"[ClassSystem] Item '{Lang.GetItemNameValue(args.Type)}' requires class: {requiredClass.ToUpper()}.");
                }
            }
        }

        private void OnGameUpdate(EventArgs args)
        {
            foreach (var player in TShock.Players)
            {
                if (player == null || !player.Active || !player.IsLoggedIn || player.Account == null) continue;

                string playerClass = GetPlayerClass(player);
                if (string.IsNullOrEmpty(playerClass)) continue;

                ApplyClassStats(player, playerClass);
            }
        }

        private void OnNpcStrike(NpcStrikeEventArgs args)
        {
            if (args.Player == null || !args.Player.IsLoggedIn || args.Player.Account == null) return;

            string playerClass = GetPlayerClass(args.Player);
            if (string.IsNullOrEmpty(playerClass)) return;

            if (playerClass == "warrior" && args.Player.SelectedItem != null && args.Player.SelectedItem.melee)
            {
                args.Damage = (int)Math.Round(args.Damage * (1.0 + WarriorMeleeDamageMult));
            }
            else if (playerClass == "ranger" && args.Player.SelectedItem != null && args.Player.SelectedItem.ranged)
            {
                args.Damage = (int)Math.Round(args.Damage * (1.0 + RangerRangedDamageMult));
            }
            else if (playerClass == "mage" && args.Player.SelectedItem != null && args.Player.SelectedItem.magic)
            {
                args.Damage = (int)Math.Round(args.Damage * (1.0 + MageMagicDamageMult));
            }
            else if (playerClass == "summoner" && args.Player.SelectedItem != null && args.Player.SelectedItem.summon)
            {
                double mult = 1.0 + SummonerSummonDamageMult;
                if (args.Player.SelectedItem.useStyle == ItemUseStyleID.Swing && args.Player.SelectedItem.noMelee)
                {
                    mult += SummonerWhipDamageMult;
                }
                args.Damage = (int)Math.Round(args.Damage * mult);
            }
        }

        private void OnProjectileNew(ProjectileNewEventArgs args)
        {
            var proj = Main.projectile[args.ProjectileId];
            if (proj == null || !proj.active) return;

            var player = TShock.Players[proj.owner];
            if (player == null || !player.IsLoggedIn || player.Account == null) return;

            string playerClass = GetPlayerClass(player);
            if (playerClass == "ranger" && proj.ranged && RangerArcheryBuff)
            {
                proj.velocity *= 1.15f;
            }
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
            _frozenPositions.Remove(args.Player.Index);

            if (_classStarterItems.TryGetValue(chosen, out var items))
            {
                foreach (var (itemId, stack) in items)
                {
                    args.Player.GiveItem(itemId, stack);
                }
            }

            ApplyClassStats(args.Player, chosen);
            args.Player.SendSuccessMessage($"[ClassSystem] Class selected: {chosen.ToUpper()}!");
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
                _frozenPositions[target.Index] = target.TPlayer.position;
                target.SendWarningMessage("[ClassSystem] Your class has been reset. Use /class to select again.");
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
            _frozenPositions.Remove(target.Index);
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
            int allowedMaxLife = Math.Min(tPlayer.statLifeMax2, BaseStartHp + maxCrystals * HpPerLifeCrystal);

            if (className == "warrior")
            {
                tPlayer.statLifeMax2 = (int)Math.Round(Math.Max(BaseStartHp, tPlayer.statLifeMax2) * (1.0 + WarriorMaxHpMult));
                tPlayer.statDefense = (int)Math.Round(tPlayer.statDefense * (1.0 + WarriorDefenseMult));
                tPlayer.moveSpeed *= (float)(1.0 + WarriorMoveSpeedMult);
            }
            else if (className == "ranger")
            {
                tPlayer.statLifeMax2 = Math.Max(50, allowedMaxLife + RangerMaxHpFlat);
                tPlayer.statDefense = (int)Math.Round(tPlayer.statDefense * (1.0 + RangerDefenseMult));
                tPlayer.moveSpeed *= (float)(1.0 + RangerMoveSpeedMult);
            }
            else if (className == "mage")
            {
                tPlayer.statLifeMax2 = Math.Max(50, allowedMaxLife + MageMaxHpFlat);
                tPlayer.statManaMax2 += MageMaxManaFlat;
                tPlayer.statDefense = (int)Math.Round(tPlayer.statDefense * (1.0 + MageDefenseMult));
                tPlayer.moveSpeed *= (float)(1.0 + MageMoveSpeedMult);
            }
            else if (className == "summoner")
            {
                tPlayer.statLifeMax2 = Math.Max(50, allowedMaxLife + SummonerMaxHpFlat);
                tPlayer.maxMinions += SummonerExtraMinions;
                tPlayer.statDefense = (int)Math.Round(tPlayer.statDefense * (1.0 + SummonerDefenseMult));
                tPlayer.moveSpeed *= (float)(1.0 + SummonerMoveSpeedMult);
            }
        }

        private string GetClassFromDB(int accountId)
        {
            using (var reader = _db.QueryReader("SELECT ClassName FROM PlayerClasses WHERE AccountID = @0", accountId))
            {
                if (reader.Read())
                {
                    return reader.Get<string>("ClassName");
                }
            }
            return null;
        }

        private void SaveClassToDB(int accountId, string className)
        {
            string existing = GetClassFromDB(accountId);
            if (existing == null)
            {
                _db.Query("INSERT INTO PlayerClasses (AccountID, ClassName) VALUES (@0, @1)", accountId, className);
            }
            else
            {
                _db.Query("UPDATE PlayerClasses SET ClassName = @1 WHERE AccountID = @0", accountId, className);
            }
        }

        private void DeleteClassFromDB(int accountId)
        {
            _db.Query("DELETE FROM PlayerClasses WHERE AccountID = @0", accountId);
        }
    }
}
