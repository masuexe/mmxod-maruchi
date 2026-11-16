using Microsoft.VisualStudio.TestTools.UnitTesting;
using MMXOnline;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Tests
{
    [TestClass]
    public class ExpansionTests
    {
        [TestMethod]
        public void NetIdAllocation_Supports80Slots_WithoutOverflowOrCollision()
        {
            Assert.AreEqual(800, Player.netIdsPerPlayer);
            Assert.AreEqual(64, Server.maxPlayerCap);

            var allNetIds = new HashSet<ushort>();
            for (int id = 0; id < 80; id++)
            {
                var player = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));
                player.id = id;
                ushort startNetId = player.getStartNetId();
                ushort firstAvailable = player.getFirstAvailableNetId();
                player.setNextActorNetId(firstAvailable);

                Assert.IsTrue(startNetId >= Level.maxReservedNetId, $"Player {id} collided with reserved stage netIds");
                Assert.IsTrue((int)startNetId + Player.netIdsPerPlayer - 1 <= ushort.MaxValue, $"Player {id} range overflows ushort");
                Assert.AreEqual(id, Player.getPlayerIdFromCharNetId(startNetId));

                Assert.IsTrue(allNetIds.Add(startNetId), $"Duplicate char netId for player {id}");
                Assert.IsTrue(allNetIds.Add((ushort)(startNetId + 1)), $"Duplicate ride armor netId for player {id}");

                var playerRangeIds = new HashSet<ushort>();
                for (int i = 0; i < 2000; i++)
                {
                    ushort next = player.getNextActorNetId();
                    Assert.IsTrue(next >= firstAvailable && next <= startNetId + Player.netIdsPerPlayer - 1,
                        $"Player {id} allocated out-of-range netId {next}");
                    playerRangeIds.Add(next);
                }

                Assert.AreEqual(Player.netIdsPerPlayer - 2, playerRangeIds.Count);
                foreach (ushort allocated in playerRangeIds)
                {
                    Assert.IsTrue(allNetIds.Add(allocated), $"NetId collision across players at {allocated} (player {id})");
                }
            }

            Assert.AreEqual("11th", Helpers.getNthString(11));
            Assert.AreEqual("12th", Helpers.getNthString(12));
            Assert.AreEqual("13th", Helpers.getNthString(13));
            Assert.AreEqual("21st", Helpers.getNthString(21));
            Assert.AreEqual("22nd", Helpers.getNthString(22));
            Assert.AreEqual("23rd", Helpers.getNthString(23));
            Assert.AreEqual("64th", Helpers.getNthString(64));
        }

        [TestMethod]
        public void SpawnFallback_NeverReturnsNull_WhenAllSpawnsOccupied()
        {
            var spawns = new List<SpawnPoint>
            {
                new SpawnPoint("sp0", new Point(100, 100), 1, -1),
                new SpawnPoint("sp1", new Point(300, 100), 1, -1),
                new SpawnPoint("sp2", new Point(900, 100), -1, -1),
            };
            var occupied = new List<Point>
            {
                new Point(105, 100),
                new Point(305, 100),
                new Point(800, 100),
            };

            SpawnPoint chosen = Level.getLeastCrowdedSpawnPoint(spawns, occupied);
            Assert.IsNotNull(chosen);
            Assert.AreEqual("sp2", chosen.name);

            // Empty occupied list should still safely return one of the available spawns.
            SpawnPoint fallback = Level.getLeastCrowdedSpawnPoint(spawns, new List<Point>());
            Assert.IsNotNull(fallback);
        }

        [TestMethod]
        public void ServerCapacity_AndLive64PlayerUdpRelay()
        {
            float prevLatency = Global.simulatedLatency;
            Global.simulatedLatency = 0f;

            Server server = null;
            var connectedClients = new List<ServerClient>();

            try
            {
                server = new Server(
                    Global.version,
                    new Region("Local", "127.0.0.1"),
                    "test64",
                    "training",
                    "training",
                    GameMode.Deathmatch,
                    60,
                    48,
                    64,
                    0,
                    false,
                    false,
                    NetcodeModel.FavorAttacker,
                    200,
                    false,
                    false,
                    false,
                    Global.checksum,
                    null,
                    null,
                    new ExtraCpuCharData(),
                    null,
                    false,
                    false);

                Assert.AreEqual(64, server.maxPlayers);
                Server.servers[server] = true;
                server.start();

                var hostPlayer = new ServerPlayer("Host", -1, true, 0, null, "dev-host", null, 0);
                var hostClient = ServerClient.Create("127.0.0.1", server.name, server.port, hostPlayer, out JoinServerResponse hostResp, out string hostErr);
                Assert.IsNotNull(hostClient, $"Host failed to connect: {hostErr}");
                connectedClients.Add(hostClient);

                // Connect 15 additional human UDP clients (total 16 real connections + 48 bots = 64 active combatants).
                for (int i = 1; i <= 15; i++)
                {
                    var sp = new ServerPlayer($"Player{i}", -1, false, i % 5, null, $"dev-{i}", null, 0);
                    var client = ServerClient.Create("127.0.0.1", server.name, server.port, sp, out JoinServerResponse joinResp, out string err);
                    Assert.IsNotNull(client, $"Client {i} failed to connect: {err}");
                    Assert.IsFalse(client.serverPlayer.isSpectator, $"Client {i} should join as active player");
                    connectedClients.Add(client);
                }

                Assert.AreEqual(64, server.players.Count(p => !p.isSpectator));
                Assert.AreEqual(48, server.players.Count(p => p.isBot));
                Assert.AreEqual(64, server.players.Select(p => p.id).Distinct().Count());
                Assert.AreEqual(63, server.players.Max(p => p.id));

                // Pump messages across clients before testing broadcast relay and 65th overflow spectator.
                foreach (var c in connectedClients)
                {
                    c.getMessages(out _, false);
                }

                // 65th joiner (17th human when 48 bots + 16 humans = 64) replaces a bot; add 1 bot back then connect 65th?
                // Even simpler: connect 1 more human after removing bots? No, currently 48 bots exist so 17th human replaces 1 bot!
                // Verify bot replacement keeps active players at 64, then verify RPC broadcast reaches all connected clients.
                var extraPlayer = new ServerPlayer("Extra", -1, false, 0, null, "dev-extra", null, 0);
                var extraClient = ServerClient.Create("127.0.0.1", server.name, server.port, extraPlayer, out _, out string extraErr);
                Assert.IsNotNull(extraClient, $"Extra client failed to connect: {extraErr}");
                Assert.IsFalse(extraClient.serverPlayer.isSpectator, "Human joiner should replace a bot rather than spectating");
                connectedClients.Add(extraClient);

                Assert.AreEqual(64, server.players.Count(p => !p.isSpectator));
                Assert.AreEqual(47, server.players.Count(p => p.isBot));

                string chatPayload = "{\"message\":\"hello64\",\"sender\":\"Host\",\"alliance\":0,\"isSystemMessage\":false}";
                hostClient.rpc(RPC.sendChatMessage, chatPayload);
                hostClient.flush();

                var receivedClients = new HashSet<int>();
                for (int attempt = 0; attempt < 30 && receivedClients.Count < connectedClients.Count - 1; attempt++)
                {
                    Thread.Sleep(50);
                    for (int idx = 1; idx < connectedClients.Count; idx++)
                    {
                        connectedClients[idx].getMessages(out var msgs, false);
                        if (msgs.Any(m => m.Contains("hello64")))
                        {
                            receivedClients.Add(idx);
                        }
                    }
                }

                Assert.AreEqual(connectedClients.Count - 1, receivedClients.Count,
                    "All other connected UDP clients must receive relayed RPC packets in a 64-player room");
            }
            finally
            {
                Global.simulatedLatency = prevLatency;
                foreach (var client in connectedClients)
                {
                    try { client.disconnect("test cleanup"); } catch { }
                }
                if (server != null)
                {
                    server.killServer = true;
                    for (int i = 0; i < 40 && Server.servers.ContainsKey(server); i++)
                    {
                        Thread.Sleep(50);
                    }
                    Server.servers.TryRemove(server, out _);
                }
            }
        }

        [TestMethod]
        public void Server_AssignsOverflowSpectators_UpToCap8()
        {
            Assert.AreEqual(8, Server.maxOverflowSpectatorCount);

            float prevLatency = Global.simulatedLatency;
            Global.simulatedLatency = 0f;

            Server server = null;
            var clients = new List<ServerClient>();

            try
            {
                server = new Server(
                    Global.version,
                    new Region("Local", "127.0.0.1"),
                    "testspec",
                    "training",
                    "training",
                    GameMode.Deathmatch,
                    20,
                    0,
                    2,
                    0,
                    false,
                    false,
                    NetcodeModel.FavorAttacker,
                    200,
                    false,
                    false,
                    false,
                    Global.checksum,
                    null,
                    null,
                    new ExtraCpuCharData(),
                    null,
                    false,
                    false);

                Assert.AreEqual(2, server.maxPlayers);
                Server.servers[server] = true;
                server.start();

                for (int i = 0; i < 2; i++)
                {
                    var sp = new ServerPlayer($"P{i}", -1, i == 0, 0, null, $"spec-dev-{i}", null, 0);
                    var c = ServerClient.Create("127.0.0.1", server.name, server.port, sp, out _, out string err);
                    Assert.IsNotNull(c, err);
                    Assert.IsFalse(c.serverPlayer.isSpectator);
                    clients.Add(c);
                }

                var specPlayer = new ServerPlayer("Spec1", -1, false, 0, null, "spec-dev-2", null, 0);
                var specClient = ServerClient.Create("127.0.0.1", server.name, server.port, specPlayer, out _, out string specErr);
                Assert.IsNotNull(specClient, specErr);
                Assert.IsTrue(specClient.serverPlayer.isSpectator, "Player joining full room without bots should become overflow spectator");
                clients.Add(specClient);
            }
            finally
            {
                Global.simulatedLatency = prevLatency;
                foreach (var c in clients)
                {
                    try { c.disconnect("test cleanup"); } catch { }
                }
                if (server != null)
                {
                    server.killServer = true;
                    for (int i = 0; i < 40 && Server.servers.ContainsKey(server); i++)
                    {
                        Thread.Sleep(50);
                    }
                    Server.servers.TryRemove(server, out _);
                }
            }
        }
    }
}
