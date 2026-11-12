using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace LiveSplit.Salt
{
    public class SaltMemory : IDisposable
    {
        private DateTime _nextHookAttempt = DateTime.MinValue;

        // PlayerMgr.Update
        private readonly ProgramPointer _players =
            new ProgramPointer(
                "558BEC5633F6A1????????837804007E253B700473238B4CB00880B9????????0074083909FF15????????46A1????????3970047FDB5E5DC3",
                7, 2);

        // MouseMgr.Update
        private readonly ProgramPointer _gameState =
            new ProgramPointer("7E1CA1????????8378040076758B7808C6472000C705????????00002041A1????????83F803731F", 31,
                1);

        // GameStateManager.GetMainCharacter
        private readonly ProgramPointer _characters =
            new ProgramPointer("85C07C168B15????????3942047E0B3B4204730E8B448208", 6, 2);

        private readonly Dictionary<int, Dictionary<string, InvLoot>> _playerItems =
            new Dictionary<int, Dictionary<string, InvLoot>>();
        private readonly Dictionary<int, DateTime> _playerItemTimes = new Dictionary<int, DateTime>();

        public Process Program { get; private set; }

        public bool IsHooked
        {
            get
            {
                try
                {
                    return Program != null && !Program.HasExited;
                }
                catch (InvalidOperationException)
                {
                    // A disposed Process handle can outlive the process it represented.
                    return false;
                }
            }
        }

        public bool IsReady
        {
            get
            {
                if (!IsHooked)
                {
                    return false;
                }

                // Start all three scans without blocking LiveSplit's update thread.
                // Until each pointer chain is resolved, callers must not read game data.
                bool playersReady = _players.IsReady(Program);
                bool gameStateReady = _gameState.IsReady(Program);
                bool charactersReady = _characters.IsReady(Program);

                return playersReady && gameStateReady && charactersReady;
            }
        }

        public void Hook()
        {
            if (IsHooked || DateTime.Now < _nextHookAttempt)
            {
                return;
            }

            _nextHookAttempt = DateTime.Now.AddSeconds(1);

            // Retire the previous handle before looking for a replacement.
            // The process may have exited or been disposed since the last update.
            if (Program != null)
            {
                Program.Dispose();
                Program = null;
            }

            Process[] processes = Process.GetProcessesByName("salt");
            if (processes.Length == 0)
            {
                return;
            }

            Program = processes[0];
            for (int i = 1; i < processes.Length; i++)
            {
                processes[i].Dispose();
            }

            try
            {
                MemoryReader.Update64Bit(Program);
            }
            catch (InvalidOperationException)
            {
                Program.Dispose();
                Program = null;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                Program.Dispose();
                Program = null;
            }
        }

        public int GetPlayerCount()
        {
            // PlayerMgr.player.Length
            return _players.Read<int>(Program, 0x4);
        }

        public IntPtr GetPlayer(int player)
        {
            if (!HasPlayerIndex(player))
            {
                return IntPtr.Zero;
            }

            // PlayerMgr.player[player]
            return (IntPtr) _players.Read<uint>(Program, 0x8 + sizeof(uint) * player);
        }

        public MenuLevel GetMenuLevel(int player)
        {
            if (!HasPlayerIndex(player))
            {
                return MenuLevel.PressStart;
            }

            // PlayerMgr.player[player].menuMgr.curLevel
            return (MenuLevel) _players.Read<int>(Program, 0x8 + sizeof(uint) * player, 0x3C, 0x14);
        }

        public TransitionMode GetMenuTransitionMode(int player)
        {
            if (!HasPlayerIndex(player))
            {
                return TransitionMode.None;
            }

            // PlayerMgr.player[player].menuMgr.transMode
            return (TransitionMode) _players.Read<int>(Program, 0x8 + sizeof(uint) * player, 0x3C, 0x18);
        }

        public void UpdateInventory(int player)
        {
            if (!HasPlayerIndex(player))
            {
                return;
            }

            // 100ms cooldown between inventory updates
            if (!_playerItemTimes.TryGetValue(player, out DateTime time) || DateTime.Now > time)
            {
                _playerItemTimes[player] = DateTime.Now.AddMilliseconds(100);
            }
            else
            {
                return;
            }

            // Get player items list, clear
            if (!_playerItems.TryGetValue(player, out Dictionary<string, InvLoot> items))
            {
                items = new Dictionary<string, InvLoot>();
                _playerItems[player] = items;
            }

            items.Clear();

            // PlayerMgr.player[player].playerInv.inventory
            IntPtr inventory = (IntPtr) _players.Read<uint>(Program, 0x8 + sizeof(uint) * player, 0x24, 0x8);

            int len = Program.Read<int>(inventory, 0x4);
            for (int i = 0; i < len; i++)
            {
                // inventory[i]
                IntPtr itemPtr = (IntPtr) Program.Read<uint>(inventory, 0x8 + sizeof(uint) * i);

                if (itemPtr == IntPtr.Zero)
                {
                    continue;
                }

                // Follow ptr to struct
                InvLoot item = new InvLoot(Program, itemPtr);

                if (item.name != null)
                {
                    items[item.name] = item;
                }
            }
        }

        public bool TryGetPlayerItem(int player, string itemName, out InvLoot item)
        {
            if (!HasPlayerIndex(player))
            {
                item = default;
                return false;
            }

            UpdateInventory(player);

            return _playerItems[player].TryGetValue(itemName, out item);
        }

        public void RandomizePlayerAppearance(int i)
        {
            Random rnd = new Random();
            IntPtr player = GetPlayer(i);
            if (player == IntPtr.Zero)
            {
                return;
            }

            // skinIdx (Origin)
            Program.Write(player, rnd.Next(14), 0xDC);

            // skinClass (Sex)
            Program.Write(player, rnd.Next(2), 0xE0);

            // hair
            Program.Write(player, rnd.Next(25), 0xE4);

            // hairColor
            Program.Write(player, rnd.Next(18), 0xE8);

            // beard
            Program.Write(player, rnd.Next(11), 0xEC);

            // beardColor
            Program.Write(player, rnd.Next(18), 0xF0);

            // eyeColor
            Program.Write(player, rnd.Next(10), 0xF4);
        }

        public string GetPlayerAnim(int player)
        {
            if (!HasPlayerIndex(player))
            {
                return string.Empty;
            }

            // PlayerMgr.player[0].charIdx
            int charIndex = _players.Read<int>(Program, 0x8, 0x9C);
            if (!HasCharIndex(charIndex))
            {
                return string.Empty;
            }

            // CharMgr.character[charIndex].anim.animName
            IntPtr animName = (IntPtr) _characters.Read<uint>(Program, 0x8 + sizeof(uint) * charIndex, 0x4, 0x8);

            return Program.GetString(animName);
        }

        public bool IsGameEnding()
        {
            if (!HasPlayerIndex(0))
            {
                return false;
            }

            // PlayerMgr.player[0].charIdx
            int charIndex = _players.Read<int>(Program, 0x8, 0x9C);
            if (!HasCharIndex(charIndex))
            {
                return false;
            }

            // CharMgr.character[charIndex].loc.Y
            float charY = _characters.Read<float>(Program, 0x8 + sizeof(uint) * charIndex, 0xD8);

            return GetPlayerAnim(0) == "takehead" || charY > 47900;
        }

        public GameState GetGameState()
        {
            return (GameState) _gameState.Read<int>(Program, 0x0);
        }

        public int GetCharCount()
        {
            // CharMgr.character.Length
            return _characters.Read<int>(Program, 0x4);
        }

        public EnemyType GetCharType(int ch)
        {
            if (!HasCharIndex(ch))
            {
                return EnemyType.Unknown;
            }

            // CharMgr.character[ch].monsterIdx
            return (EnemyType) _characters.Read<int>(Program, 0x8 + sizeof(uint) * ch, 0x5C);
        }

        public float GetCharHealth(int ch)
        {
            if (!HasCharIndex(ch))
            {
                return 0f;
            }

            // CharMgr.character[ch].hp
            return _characters.Read<float>(Program, 0x8 + sizeof(uint) * ch, 0x60);
        }

        private bool HasPlayerIndex(int player)
        {
            return player >= 0 && player < GetPlayerCount();
        }

        private bool HasCharIndex(int ch)
        {
            return ch >= 0 && ch < GetCharCount();
        }

        public void Dispose()
        {
            Program?.Dispose();
            Program = null;
        }
    }
}
