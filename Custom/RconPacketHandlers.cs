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
			Seek(1, SeekOrigin.End);
			var end = ReadByte();
			Seek(0, SeekOrigin.Begin);
			var head = ReadInt32();
			m_CommandByte = ReadByte();
			if (head != -1 || end != 0x0A || !m_Commands.ContainsKey(m_CommandByte)) { m_Valid = false; }
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

	public static class RconResponsePackets
	{
		public static byte[] Success = new byte[] { 0x0A };
		public static byte[] Fail = new byte[] { 0xFF };
		public static byte[] InvalidChallenge = new byte[] { 0xF0 };
		public static byte[] InvalidPassword = new byte[] { 0xF1 };
	}

	public static class RconPacketHandlers
	{
		private readonly static Dictionary<byte, Func<IPEndPoint, PacketReader, byte[]>> m_Funcs = new Dictionary<byte, Func<IPEndPoint, PacketReader, byte[]>>() {
			{ 0x1B, Status },
			{ 0x1C, WorldBroadcast },
			{ 0x1D, ChannelSend }
		};

		private readonly static Dictionary<byte, Func<IPEndPoint, PacketReader, byte[]>> m_FuncsNoVerify = new Dictionary<byte, Func<IPEndPoint, PacketReader, byte[]>>() {
			{ 0x1A, GetChallenge },
			{ 0x20, KeepAlive }
		};

		private readonly static Dictionary<byte, Action<IPEndPoint, PacketReader>> m_Actions = new Dictionary<byte, Action<IPEndPoint, PacketReader>>() {
			{ 0x1E, Save },
			{ 0x1F, Shutdown }
		};

		private static Dictionary<string, RconChallengeRecord> m_Challenges;
		private static RconConfig rconConfig;
		private static UdpClient udpListener;

		public static void Configure()
		{
			rconConfig = new RconConfig("RconConfig.cfg");
			m_Challenges = new Dictionary<string, RconChallengeRecord>();
			udpListener = new UdpClient(rconConfig.ListenPort);

			if (rconConfig.RelayEnabled())
			{
				Channel.AddStaticChannel(rconConfig.ChatChannel);
				ChatActionHandlers.Register(0x61, true, new OnChatAction(RelayChatPacket));
			}

			UDPListener();
		}

		private static void UDPListener()
		{
			Task.Run(async () =>
			{
				Utility.PushColor(ConsoleColor.Green);
				Console.WriteLine("RCON: Listening on *.*.*.*:{0}", rconConfig.ListenPort);
				Utility.PopColor();
					
				while(true)
				{
					var receivedResults = await udpListener.ReceiveAsync();
						
					ProcessPacket(receivedResults, udpListener);
				}
			});
		}

		private static void ProcessPacket(UdpReceiveResult data, UdpClient client)
		{
			var packetReader = new RconPacketReader(data.Buffer, data.Buffer.Length);
			if(!packetReader.IsValid)
			{
				client.Send(RconResponsePackets.Fail, 1, data.RemoteEndPoint);
				return;
			}

			byte[] response;

			if (m_FuncsNoVerify.ContainsKey(packetReader.Command))
			{
				response = m_FuncsNoVerify[packetReader.Command](data.RemoteEndPoint, packetReader);
				client.Send(response, response.Length, data.RemoteEndPoint);
				return;
			}

			try
			{
				response = Verify(data.RemoteEndPoint, ref packetReader);
			}
			catch (Exception ex)
			{
				Console.WriteLine(ex.Message);
				client.Send(RconResponsePackets.Fail, 1, data.RemoteEndPoint);
				return;
			}

			try
			{
				if (response != null && response.SequenceEqual(RconResponsePackets.Success))
				{
					if (m_Funcs.ContainsKey(packetReader.Command)) {
						response = m_Funcs[packetReader.Command](data.RemoteEndPoint, packetReader);
						client.Send(response, response.Length, data.RemoteEndPoint);
					}
					else if (m_Actions.ContainsKey(packetReader.Command))
					{
						client.Send(response, response.Length, data.RemoteEndPoint);
						m_Actions[packetReader.Command](data.RemoteEndPoint, packetReader);
					}
				}
				else
				{
					client.Send(response, response.Length, data.RemoteEndPoint);
				}
			}
			catch(Exception ex)
			{
				Console.WriteLine(ex.Message);
			}
		}

		private static bool IsChallengeValid(IPEndPoint remote, ref RconPacketReader pvSrc)
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

		private static bool IsPasswordValid(ref RconPacketReader pvSrc)
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

		private static byte[] Verify(IPEndPoint remote, ref RconPacketReader pvSrc)
		{
			if (!IsChallengeValid(remote, ref pvSrc))
			{
				return RconResponsePackets.InvalidChallenge;
			}

			if (!IsPasswordValid(ref pvSrc))
			{
				return RconResponsePackets.InvalidPassword;
			}

			return RconResponsePackets.Success;
		}

		private static void RelayChatPacket(ChatUser from, Channel channel, string param)
		{
			ChatActionHandlers.ChannelMessage(from, channel, param);
			if(channel.Name == rconConfig.ChatChannel || rconConfig.ChatChannel == "*")
			{
				byte[] data = Encoding.ASCII.GetBytes("UO\tm\t" + channel.Name + "\t" + from.Username + "\t" + param);
				udpListener.Send(data, data.Length, rconConfig.ChatPacketTargetAddress, rconConfig.ChatPacketTargetPort);
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

			byte[] challenge = { 0xFF, 0xFF, 0xFF, 0xFF, 0x0A, 0x20, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x20, 0x32, 0x0A };
			challengeBytes.CopyTo(challenge, 6);
			
			return challenge;
		}

		private static byte[] WorldBroadcast(IPEndPoint remote, PacketReader pvSrc)
		{
			string message = pvSrc.ReadUTF8String();
			int hue = pvSrc.ReadInt32();
			// bool ascii = pvSrc.ReadBoolean();

			World.Broadcast(hue, false, message);

			return RconResponsePackets.Success;
		}

		private static byte[] ChannelSend(IPEndPoint remote, PacketReader pvSrc)
		{
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
				return RconResponsePackets.Fail;
			}

			return RconResponsePackets.Success;
		}

		private static byte[] KeepAlive(IPEndPoint remote, PacketReader pvSrc)
		{
			if(m_Challenges.ContainsKey(remote.Address.ToString()))
			{
				m_Challenges[remote.Address.ToString()].Refresh();
			}
			else
			{
				return RconResponsePackets.Fail;
			}

			return RconResponsePackets.Success;
		}

		private static void Save(IPEndPoint remote, PacketReader pvSrc)
		{
			AutoSave.Save();
		}

		private static void Shutdown(IPEndPoint remote, PacketReader pvSrc)
		{
			bool save = pvSrc.ReadBoolean();
			bool restart = pvSrc.ReadBoolean();
			Console.WriteLine("RCON: shutting down server (Restart: {0}) (Save: {1}) [{2}]", restart, save, DateTime.Now);

			if (save && !AutoRestart.Restarting)
				AutoSave.Save();

			Core.Kill(restart);
		}

		private static byte[] Status(IPEndPoint remote, PacketReader pvSrc)
		{
			return RconResponsePackets.Success;
		}
	}
}