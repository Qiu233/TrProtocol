﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using TrProtocol;
using TrProtocol.Models;
using TrProtocol.Packets;
using TrProtocol.Packets.Modules;

namespace TrClient
{
	public class TClient
    {
        private TcpClient client;

        public byte PlayerSlot { get; private set; }
        public string CurRelease = "Terraria248";
        public string Username = "";
        public bool IsPlaying { get; private set; }

        private BinaryReader br;
        private BinaryWriter bw;
        private readonly PacketSerializer mgr = new(true);

        public void Connect(string hostname, int port)
        {
            client = new TcpClient();
            client.Connect(hostname, port);
            br = new BinaryReader(client.GetStream());
            bw = new BinaryWriter(client.GetStream());
        }

        public void Connect(IPEndPoint server, IPEndPoint proxy = null)
        {
            if (proxy == null)
            {
                client = new TcpClient();
                client.Connect(server);
                br = new BinaryReader(client.GetStream());
                bw = new BinaryWriter(client.GetStream());
                return;
            }

            client.Connect(proxy);

            //Console.WriteLine("Proxy connected to " + proxy.ToString());
            var encoding = new UTF8Encoding(false, true);
			using var sw = new StreamWriter(client.GetStream(), encoding, 4096, true) { NewLine = "\r\n" };
			using var sr = new StreamReader(client.GetStream(), encoding, false, 4096, true);
			sw.WriteLine($"CONNECT {server} HTTP/1.1");
			sw.WriteLine("User-Agent: Java/1.8.0_192");
			sw.WriteLine($"Host: {server}");
			sw.WriteLine("Accept: text/html, image/gif, image/jpeg, *; q=.2, */*; q=.2");
			sw.WriteLine("Proxy-Connection: keep-alive");
			sw.WriteLine();
			sw.Flush();

			var resp = sr.ReadLine();
			Console.WriteLine("Proxy connection; " + resp);
			if (!resp.StartsWith("HTTP/1.1 200")) throw new Exception();

			while (true)
			{
				resp = sr.ReadLine();
				if (string.IsNullOrEmpty(resp)) break;
			}
		}

        public void KillServer()
        {
            client.GetStream().Write(new byte[] { 0, 0 }, 0, 2);
        }
        public Packet Receive()
        {
            return mgr.Deserialize(br);
        }
        public void Send(Packet packet)
        {
            if (packet is IPlayerSlot ips) ips.PlayerSlot = PlayerSlot;
            bw.Write(mgr.Serialize(packet));
        }
        public void Hello(string message)
        {
            Send(new ClientHello { Version = message });
        }

        public void TileGetSection(int x, int y)
        {
            Send(new RequestTileData { Position = new Position { X = x, Y = y } });
        }

        public void Spawn(short x, short y)
        {
            Send(new SpawnPlayer
            {
                Position = new ShortPosition { X = x, Y = y },
                Context = PlayerSpawnContext.SpawningIntoWorld
            });
        }

        public void SendPlayer()
        {
            Send(new SyncPlayer
            {
                Name = Username
            });
            Send(new PlayerHealth { StatLifeMax = 100, StatLife = 100 });
            for (byte i = 0; i < 73; ++i)
                Send(new SyncEquipment { ItemSlot = i });
        }

        public void ChatText(string message)
        {
            Send(new NetTextModuleC2S
            {
                Command = "Say",
                Text = message
            });
        }
        
        public event Action<TClient, NetworkText, Color> OnChat;
        public event Action<TClient, string> OnMessage;
        public Func<bool> shouldExit = () => false;

        private readonly Dictionary<Type, Action<Packet>> handlers = new();

        public void On<T>(Action<T> handler) where T : Packet
        {
            void Handler(Packet p) => handler(p as T);

            if (handlers.TryGetValue(typeof(T), out var val)) val += Handler;
            else handlers.Add(typeof(T), Handler);
        }

        public TClient()
        {
            InternalOn();
        }

        private void InternalOn()
        {

            On<StatusText>(status => OnChat?.Invoke(this, status.Text, Color.White));
            On<NetTextModuleS2C>(text => OnChat?.Invoke(this, text.Text, text.Color));
            On<SmartTextMessage>(text => OnChat?.Invoke(this, text.Text, text.Color));
            On<Kick>(kick =>
            {
                OnMessage?.Invoke(this, "Kicked : " + kick.Reason);
                connected = false;
            });
            On<LoadPlayer>(player =>
            {
                PlayerSlot = player.PlayerSlot;
                SendPlayer();
                Send(new RequestWorldInfo());
            });
            On<WorldData>(_ =>
            {
                if (!IsPlaying)
                {
                    TileGetSection(100, 100);
                    IsPlaying = true;
                }
            });
            On<StartPlaying>(_ =>
            {
                Spawn(100, 100);

            });
        }

        public bool connected = false;

        public void GameLoop(string host, int port, string password)
        {
            Connect(host, port);
            GameLoopInternal(password);
        }
        public void GameLoop(IPEndPoint endPoint, string password, IPEndPoint proxy = null)
        {
            Connect(endPoint, proxy);
            GameLoopInternal(password);
        }
        private void GameLoopInternal(string password)
        {

            Console.WriteLine("Sending Client Hello...");
            Hello(CurRelease);

            /*TcpClient verify = new TcpClient();
            byte[] raw = Encoding.ASCII.GetBytes("-1551487326");
            verify.Connect(new IPEndPoint(endPoint.Address, 7980));
            verify.GetStream().Write(raw, 0, raw.Length);
            verify.Close();*/

            On<RequestPassword>(_ => Send(new SendPassword { Password = password }));

            connected = true;
            while (connected && !shouldExit())
            {
                Packet packet = Receive();
                try
                {
                    if (handlers.TryGetValue(packet.GetType(), out var act))
                        act(packet);
                    else
                        ;//Console.WriteLine($"[Warning] not processed packet type {packet}");
                }
                catch (Exception e)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    var msg = $"Exception caught when trying to parse packet {packet.Type}\n{e}";
                    Console.WriteLine(msg);
                    File.AppendAllText("log.txt", msg + "\n");
                    Console.ResetColor();
                }
            }

            client.Close();

        }
    }
}