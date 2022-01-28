/***************************************************************************
 *                     CustomRemoteAdminPacketHandlers.cs
 *                            -------------------
 *   begin                : Jan 24, 2022
 *   copyright            : (C) Michael Rosiles
 *   email                : zindryr@gmail.com
 *   website              : https://rosil.es/
 *
 *   Copyright (C) 2022 Michael Rosiles
 *   This program is free software: you can redistribute it and/or modify
 *   it under the terms of the GNU General Public License as published by
 *   the Free Software Foundation, either version 3 of the License, or
 *   (at your option) any later version.
 *   
 *   This program is distributed in the hope that it will be useful,
 *   but WITHOUT ANY WARRANTY; without even the implied warranty of
 *   MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 *   GNU General Public License for more details.
 *   
 *   You should have received a copy of the GNU General Public License
 *   along with this program. If not, see <http://www.gnu.org/licenses/>.
 ***************************************************************************/

using System;
using System.Text;
using System.Net;
using System.IO;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using Server.Misc;
using Server.Network;
using Server.Engines.Chat;

namespace Server.RemoteAdmin
{
	public class RconPacketReader : PacketReader
	{
		private bool m_Valid;
		private byte m_CommandByte;
		private readonly Dictionary<byte, string> m_Commands = new Dictionary<byte, string>() {
			{ 0x1A, "challenge" },
			{ 0x1B, "status" },
			{ 0x1C, "broadcast" },
			{ 0x1D, "send channel" },
			{ 0x1E, "save" },
			{ 0x1F, "shutdown" },
			{ 0x20, "keepalive" }
		};

		public RconPacketReader(byte[] data, int size) : base(data, size, true)
		{
			if(data.Length < 6) { m_Valid = false; return; }
			Seek(1, System.IO.SeekOrigin.End);
			var end = ReadByte();
			Seek(0, System.IO.SeekOrigin.Begin);
			var head = ReadInt32();
			m_CommandByte = ReadByte();
			if (head != -1 || end != 10 || !m_Commands.ContainsKey(m_CommandByte)) { m_Valid = false; }
			else { m_Valid = true; }
		}

		public bool IsValid => m_Valid;
		public string CommandString => m_Commands[m_CommandByte];
		public byte Command => m_CommandByte;
	}

	public class RconChallengeRecord
	{
		private DateTime m_LastChallenge;
		private readonly IPEndPoint m_IPEndPoint;
		private readonly byte[] m_Challenge;

		public IPEndPoint IPEndPoint => m_IPEndPoint;
		public DateTime LastChallenge => m_LastChallenge;
		public byte[] Challenge => m_Challenge;

		public RconChallengeRecord(IPEndPoint ip, byte[] ch)
		{
			m_IPEndPoint = ip;
			m_LastChallenge = DateTime.Now;
			m_Challenge = ch;
		}

		public void Refresh()
		{
			m_LastChallenge = DateTime.Now;
		}
	}

	public class RconConfig
	{
		public string RconPassword => m_Vars["RconPassword"];
		public int ListenPort => Int32.Parse(m_Vars["ListenPort"]);
		public string ChatPacketTargetAddress => m_Vars["ChatPacketTargetAddress"];
		public int ChatPacketTargetPort => Int32.Parse(m_Vars["ChatPacketTargetPort"]);
		public string ChatChannel => m_Vars["ChatChannel"];

		private Dictionary<string, string> m_Vars;

		public RconConfig(string filename)
		{
			m_Vars = new Dictionary<string, string>();
			var path = Path.Combine("Scripts/Custom", filename);
			// var path = Directory.GetCurrentDirectory() + "\\Scripts\\Custom\\" + filename;
			FileInfo cfg = new FileInfo(path);
			if(cfg.Exists)
			{
				using (StreamReader stream = new StreamReader(cfg.FullName))
				{
					String line;
					while ((line = stream.ReadLine()) != null)
					{
						if (!line.StartsWith("#"))
						{
							var parts = line.Split('=');
							if(parts.Length == 2)
							{
								var key = parts[0];
								var value = parts[1];
								m_Vars.Add(key, value);
							}
						}
					}
				}
			}
			else
			{
				throw new Exception("RconConfig.cfg file is missing.");
			}
		}

		public bool RelayEnabled()
		{
			if (m_Vars.ContainsKey("ChatPacketTargetAddress") && m_Vars.ContainsKey("ChatPacketTargetPort") && m_Vars.ContainsKey("ChatChannel"))
				return true;
			return false;
		}
	}

	public static class RconPacketHandlers
	{
		private readonly static Dictionary<byte, Func<IPEndPoint, PacketReader, byte[]>> m_Funcs = new Dictionary<byte, Func<IPEndPoint, PacketReader, byte[]>>() {
			{ 0x1A, GetChallenge },
			{ 0x1B, Status },
			{ 0x1C, WorldBroadcast },
			{ 0x1D, ChannelSend },
			{ 0x1E, Save },
			{ 0x1F, Shutdown },
			{ 0x20, KeepAlive }
		};

		private static Dictionary<string, RconChallengeRecord> m_Challenges;
		private static RconConfig rconConfig;

		public static void Configure()
		{
			rconConfig = new RconConfig("RconConfig.cfg");
			m_Challenges = new Dictionary<string, RconChallengeRecord>();

			if (rconConfig.RelayEnabled())
			{
				Channel.AddStaticChannel(rconConfig.ChatChannel);
				ChatActionHandlers.Register(0x61, true, new OnChatAction(RelayToDiscord));
			}

			UDPListener();
		}

		private static void UDPListener()
		{
			Task.Run(async () =>
			{
				using (var udpClient = new UdpClient(rconConfig.ListenPort))
				{
					Utility.PushColor(ConsoleColor.Green);
					Console.WriteLine("RCON: Listening on *.*.*.*:{0}", rconConfig.ListenPort);
					Utility.PopColor();
					
					while(true)
					{
						var receivedResults = await udpClient.ReceiveAsync();
						
						ProcessPacket(receivedResults, udpClient);
					}
				}
			});
		}

		private static void ProcessPacket(UdpReceiveResult data, UdpClient client)
		{
			var packetReader = new RconPacketReader(data.Buffer, data.Buffer.Length);
			if(!packetReader.IsValid)
			{
				client.Send(new byte[] { 0xFF }, 1, data.RemoteEndPoint);
				return;
			}

			byte[] response;
			try
			{
				response = m_Funcs[packetReader.Command](data.RemoteEndPoint, packetReader);
			}
			catch (Exception ex)
			{
				Console.WriteLine(ex.Message);
				client.Send(new byte[] { 0xFF }, 1, data.RemoteEndPoint);
				return;
			}
			try
			{
				if (response != null)
				{
					client.Send(response, response.Length, data.RemoteEndPoint);
				}
				else
				{
					client.Send(new byte[] { 0xFF }, 1, data.RemoteEndPoint);
				}
			}
			catch(Exception ex)
			{
				Console.WriteLine(ex.Message);
			}
		}

		private static bool IsChallengeValid(IPEndPoint remote, ref PacketReader pvSrc)
		{
			try
			{
				var challenge = new byte[8];
				for(int i = 0; i < 8; i++)
				{
					challenge[i] = pvSrc.ReadByte();
				}
				if (m_Challenges.ContainsKey(remote.Address.ToString()) && m_Challenges[remote.Address.ToString()].Challenge.SequenceEqual(challenge))
				{
					return true;
				}
			}
			catch(Exception ex)
			{
				Console.WriteLine(ex.Message);
			}
			
			return false;
		}

		private static bool IsPasswordValid(ref PacketReader pvSrc)
		{
			try
			{
				var password = pvSrc.ReadString();

				if (password == rconConfig.RconPassword)
				{
					return true;
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine(ex.Message);
			}

			return false;
		}

		private static void RelayToDiscord(ChatUser from, Channel channel, string param)
		{
			ChatActionHandlers.ChannelMessage(from, channel, param);
			if(channel.Name == rconConfig.ChatChannel)
			{
				byte[] data = Encoding.ASCII.GetBytes("UO\tm\t" + from.Username + "\t" + param);
				using (UdpClient c = new UdpClient(3896))
					c.Send(data, data.Length, rconConfig.ChatPacketTargetAddress, rconConfig.ChatPacketTargetPort);
			}
		}

		private static byte[] GetChallenge(IPEndPoint remote, PacketReader pvSrc)
		{
			byte[] challengeBytes;

			if (m_Challenges.ContainsKey(remote.Address.ToString()))
			{
				challengeBytes = m_Challenges[remote.Address.ToString()].Challenge;
				m_Challenges[remote.Address.ToString()].Refresh();
			}
			else
			{
				challengeBytes = new byte[8];
				Random rnd = new Random();
				rnd.NextBytes(challengeBytes);

				m_Challenges.Add(remote.Address.ToString(), new RconChallengeRecord(remote, challengeBytes));
			}

			byte[] header = BitConverter.GetBytes((int)-1).Reverse().ToArray();
			byte[] challenge = header.Append((byte)0x0A).Append((byte)0x20).Concat((byte[])challengeBytes).Append((byte)0x20).Append((byte)0x32).Append((byte)0x0A).ToArray();

			return challenge;
		}

		private static byte[] WorldBroadcast(IPEndPoint remote, PacketReader pvSrc)
		{
			if (!(IsChallengeValid(remote, ref pvSrc) && IsPasswordValid(ref pvSrc)))
			{
				return new byte[] { 0xFF };
			}

			string message = pvSrc.ReadUTF8String();
			int hue = pvSrc.ReadInt16();
			bool ascii = pvSrc.ReadBoolean();

			World.Broadcast(hue, ascii, message);

			return new byte[] { 0x0A };
		}

		private static byte[] ChannelSend(IPEndPoint remote, PacketReader pvSrc)
		{
			if(!(IsChallengeValid(remote, ref pvSrc) && IsPasswordValid(ref pvSrc)))
			{
				return new byte[] { 0xFF };
			}

			string channel_name = pvSrc.ReadUTF8String();
			string message = pvSrc.ReadUTF8String();
			int hue = pvSrc.ReadInt32();
			// bool ascii_text = pvSrc.ReadBoolean();
			Channel channel = Channel.FindChannelByName(channel_name);

			if(channel != null)
			{
				foreach (ChatUser user in channel.Users)
				{
					user.Mobile.SendMessage(hue, message);
				}
			}
			else
			{
				Utility.PushColor(ConsoleColor.Red);
				Console.WriteLine("RCON: {0} channel not found.", channel_name);
				Utility.PopColor();
				return new byte[] { 0xFF };
			}

			return new byte[] { 0x0A };
		}

		private static byte[] KeepAlive(IPEndPoint remote, PacketReader pvSrc)
		{
			if(m_Challenges.ContainsKey(remote.Address.ToString()))
			{
				m_Challenges[remote.Address.ToString()].Refresh();
			}
			else
			{
				return new byte[] { 0xFF };
			}

			return new byte[] { 0x0A };
		}

		private static byte[] Save(IPEndPoint remote, PacketReader pvSrc)
		{
			if (!(IsChallengeValid(remote, ref pvSrc) && IsPasswordValid(ref pvSrc)))
			{
				return new byte[] { 0xFF };
			}

			try
			{
				// AutoSave.Save();
			}
			catch(Exception ex)
			{
				Console.WriteLine(ex.Message);
			}

			return new byte[] { 0x0A };
		}

		private static byte[] Shutdown(IPEndPoint remote, PacketReader pvSrc)
		{
			if (!(IsChallengeValid(remote, ref pvSrc) && IsPasswordValid(ref pvSrc)))
			{
				return new byte[] { 0xFF };
			}

			bool save = pvSrc.ReadBoolean();
			bool restart = pvSrc.ReadBoolean();
			Console.WriteLine("RCON: shutting down server (Restart: {0}) (Save: {1}) [{2}]", restart, save, DateTime.Now);

			if (save && !AutoRestart.Restarting)
				AutoSave.Save();

			Core.Kill(restart);

			return new byte[] { 0x0A };
		}

		private static byte[] Status(IPEndPoint remote, PacketReader pvSrc)
		{
			if (!(IsChallengeValid(remote, ref pvSrc) && IsPasswordValid(ref pvSrc)))
			{
				return new byte[] { 0xFF };
			}

			return new byte[] { 0x0A };
		}
	}
}