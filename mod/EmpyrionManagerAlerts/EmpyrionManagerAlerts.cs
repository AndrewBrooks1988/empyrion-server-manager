// Empyrion Server Manager - alert bridge (dedicated-server mod).
//
// Shows on-screen messages (the coloured banner at the top of the screen, with sound) to players, on behalf of the
// Empyrion Server Manager. The manager drops small text files into this mod's "outbox" folder; the mod picks them up
// about once a second and sends them with the game's own Request_InGameMessage_* requests, then deletes them.
//
// Message file format (UTF-8, *.msg):
//   line 1: priority   0 = red (alarm), 1 = yellow (warning), 2 = blue (info)
//   line 2: seconds on screen (e.g. 10)
//   line 3: target     "all", "player:<entityId>" or "faction:<factionId>"
//   line 4+: message text
//
// The mod also writes "heartbeat.txt" every few seconds so the manager can tell it is loaded.
// No network ports, no game-content changes; players don't need to install anything.
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Eleon.Modding;

namespace EmpyrionManagerAlerts
{
    public class EmpyrionManagerAlerts : ModInterface
    {
        ModGameAPI api;
        string outbox, heartbeat;
        readonly Stopwatch clock = Stopwatch.StartNew();
        long lastPoll, lastBeat;
        ushort seq = 1000;

        public void Game_Start(ModGameAPI dediAPI)
        {
            api = dediAPI;
            var dir = Path.GetDirectoryName(typeof(EmpyrionManagerAlerts).Assembly.Location) ?? ".";
            outbox = Path.Combine(dir, "outbox");
            heartbeat = Path.Combine(dir, "heartbeat.txt");
            Directory.CreateDirectory(outbox);
            api.Console_Write("[EmpyrionManagerAlerts] loaded, watching " + outbox);
            Beat();
        }

        public void Game_Update()
        {
            var now = clock.ElapsedMilliseconds;
            if (now - lastBeat > 5000) { lastBeat = now; Beat(); }
            if (now - lastPoll < 1000) return;
            lastPoll = now;
            try
            {
                foreach (var file in Directory.GetFiles(outbox, "*.msg").OrderBy(f => f))
                {
                    try { Send(File.ReadAllLines(file, Encoding.UTF8)); }
                    catch (Exception ex) { api.Console_Write("[EmpyrionManagerAlerts] bad message " + Path.GetFileName(file) + ": " + ex.Message); }
                    finally { try { File.Delete(file); } catch { } }
                }
            }
            catch (Exception ex) { api.Console_Write("[EmpyrionManagerAlerts] " + ex.Message); }
        }

        void Send(string[] lines)
        {
            if (lines.Length < 4) throw new FormatException("expected 4+ lines");
            byte prio = byte.Parse(lines[0].Trim());
            float seconds = float.Parse(lines[1].Trim(), System.Globalization.CultureInfo.InvariantCulture);
            string target = lines[2].Trim();
            string text = string.Join("\n", lines.Skip(3)).Trim();
            if (text.Length == 0) return;
            if (prio > 2) prio = 2;

            if (target.StartsWith("player:"))
                api.Game_Request(CmdId.Request_InGameMessage_SinglePlayer, seq++, new IdMsgPrio(int.Parse(target.Substring(7)), text, prio, seconds));
            else if (target.StartsWith("faction:"))
                api.Game_Request(CmdId.Request_InGameMessage_Faction, seq++, new IdMsgPrio(int.Parse(target.Substring(8)), text, prio, seconds));
            else
                api.Game_Request(CmdId.Request_InGameMessage_AllPlayers, seq++, new IdMsgPrio(0, text, prio, seconds));
        }

        void Beat()
        {
            try { File.WriteAllText(heartbeat, DateTime.UtcNow.ToString("o") + "\nv1"); } catch { }
        }

        public void Game_Event(CmdId eventId, ushort seqNr, object data) { }

        public void Game_Exit()
        {
            try { File.Delete(heartbeat); } catch { }
        }
    }
}
