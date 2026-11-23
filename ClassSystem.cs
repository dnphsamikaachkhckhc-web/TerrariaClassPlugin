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
                if (
