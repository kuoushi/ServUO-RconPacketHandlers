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
using Server.Commands;

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
			{ 0x20, "keepalive" },
			{ 0x21, "verify link" },
			{ 0x22, "kick/ban user" },
			{ 0x23, "unban user" },
			{ 0x24, "get online users" }
		};

		public RconPacketReader(byte[] data, int size) : base(data, size, true)
		{
			if (data.Length < 6) { m_Valid = false; return; }
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
		public bool AutoJoinChatChannel => IsBooleanKeyEnabled("AutoJoinChatChannel");
		public bool AllowAccountLink => IsBooleanKeyEnabled("AllowAccountLink");
		public bool WorldChatLog => IsBooleanKeyEnabled("WorldChatLog");

		private Dictionary<string, string> m_Vars;

		public RconConfig(string filename)
		{
			m_Vars = new Dictionary<string, string>();
			var path = Path.Combine("Scripts/Custom", filename);
			FileInfo cfg = new FileInfo(path);
			if (cfg.Exists)
			{
				using (StreamReader stream = new StreamReader(cfg.FullName))
				{
					String line;
					while ((line = stream.ReadLine()) != null)
					{
						if (!line.StartsWith("#"))
						{
							var parts = line.Split('=');
							if (parts.Length == 2)
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

		private bool IsBooleanKeyEnabled(string key)
		{
			if (m_Vars.ContainsKey(key) && m_Vars[key].ToLower() == "true")
				return true;
			return false;
		}
	}

	public static class RconResponsePackets
	{
		public static byte[] Success = { 0x0A };
		public static byte[] Fail = { 0xFF };
		public static byte[] InvalidChallenge = { 0xF0 };
		public static byte[] InvalidPassword = { 0xF1 };
	}

	public static class RconPacketHandlers
	{
		private static readonly Dictionary<byte, Func<IPEndPoint, PacketReader, byte[]>> m_Funcs = new Dictionary<byte, Func<IPEndPoint, PacketReader, byte[]>>();
		private static readonly Dictionary<byte, Func<IPEndPoint, PacketReader, byte[]>> m_FuncsNoVerify = new Dictionary<byte, Func<IPEndPoint, PacketReader, byte[]>>();
		private static readonly Dictionary<byte, Action<IPEndPoint, PacketReader>> m_Actions = new Dictionary<byte, Action<IPEndPoint, PacketReader>>();

		private static Dictionary<string, RconChallengeRecord> m_Challenges;
		private static Dictionary<string, Tuple<int, DateTime>> m_AccountLinkChallenges = new Dictionary<string, Tuple<int, DateTime>>();
		private static RconConfig rconConfig;
		private static UdpClient udpListener;

		public static void Configure()
		{
			EventSink.Connected += EventSink_Connected;
			EventSink.Disconnected += EventSink_Disconnected;

			rconConfig = new RconConfig("RconConfig.cfg");
			m_Challenges = new Dictionary<string, RconChallengeRecord>();
			udpListener = new UdpClient(rconConfig.ListenPort);

			m_FuncsNoVerify.Add(0x1A, GetChallenge);
			m_Funcs.Add(        0x1B, Status);
			m_Funcs.Add(        0x1C, WorldBroadcast);
			m_Funcs.Add(        0x1D, ChannelSend);
			m_Actions.Add(      0x1E, Save);
			m_Actions.Add(      0x1F, Shutdown);
			m_FuncsNoVerify.Add(0x20, KeepAlive);
			m_Funcs.Add(        0x22, KickBanUser);
			m_Funcs.Add(        0x23, UnbanUser);
			m_Funcs.Add(        0x24, GetOnlineUsers);

			if (rconConfig.RelayEnabled())
			{
				if (rconConfig.ChatChannel != "*")
				{
					Channel.AddStaticChannel(rconConfig.ChatChannel);
					if (rconConfig.AutoJoinChatChannel)
					{
						EventSink.Login += EventSink_Login;

						// quietly ignore General channel join attempt on player login for 5 seconds
						// ClassicUO client sends a join request when entering the game, but we want players in our channel
						ChatActionHandlers.Register(0x62, false, new OnChatAction(BlockGeneralAtLogin));
					}
				}
				ChatActionHandlers.Register(0x61, true, new OnChatAction(RelayChatPacket));
			}

			if (rconConfig.AllowAccountLink)
			{
				CommandHandlers.Register("VerifyExternal", AccessLevel.Player, SendVerifyPacket_OnCommand);
				m_Funcs.Add(0x21, VerifyExternal);
			}

			if (rconConfig.WorldChatLog)
				EventSink.Speech += EventSink_Speech;

			UDPListener();
		}

		private static void EventSink_Connected(ConnectedEventArgs e)
		{
			// to send log when user connects
		}

		private static void EventSink_Login(LoginEventArgs e)
		{
			var from = e.Mobile;
			var defaultChannel = Channel.FindChannelByName(rconConfig.ChatChannel);
			var chatUser = ChatUser.AddChatUser(from);
			defaultChannel.AddUser(chatUser);
		}

		private static void EventSink_Disconnected(DisconnectedEventArgs e)
		{
			// to send log when user disconnects
		}

		private static void EventSink_Speech(SpeechEventArgs e)
		{
			Mobile from = e.Mobile;
			if (from is Mobiles.PlayerMobile)
			{
				byte[] data = Encoding.ASCII.GetBytes("UO\tmw\t" + from.Name + "\t" + from.X + " " + from.Y + " " + from.Z + "\t" + e.Speech);
				udpListener.Send(data, data.Length, rconConfig.ChatPacketTargetAddress, rconConfig.ChatPacketTargetPort);
			}
		}

		[Usage("VerifyExternal")]
		[Description("Sends a verification response to the requester.")]
		private static void SendVerifyPacket_OnCommand(CommandEventArgs e)
		{
			var from = e.Mobile;
			var confirmation = e.GetInt32(0);

			if(confirmation == 0 || !m_AccountLinkChallenges.ContainsKey(from.Account.Username))
            {
				return;
			}

			if(m_AccountLinkChallenges[from.Account.Username].Item1 != confirmation)
			{
				return;
			}

			byte[] data = Encoding.ASCII.GetBytes("UO\tv\t" + from.Account.Username + "\t" + confirmation);
			udpListener.Send(data, data.Length, rconConfig.ChatPacketTargetAddress, rconConfig.ChatPacketTargetPort);
			m_AccountLinkChallenges.Remove(from.Account.Username);
		}

		private static void UDPListener()
		{
			Task.Run(async () =>
			{
				Utility.PushColor(ConsoleColor.Green);
				Console.WriteLine("RCON: Listening on *.*.*.*:{0}", rconConfig.ListenPort);
				Utility.PopColor();

				while (true)
				{
					var receivedResults = await udpListener.ReceiveAsync();

					ProcessPacket(receivedResults, udpListener);
				}
			});
		}

		private static void ProcessPacket(UdpReceiveResult data, UdpClient client)
		{
			var packetReader = new RconPacketReader(data.Buffer, data.Buffer.Length);
			if (!packetReader.IsValid)
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
			catch (Exception ex)
			{
				Console.WriteLine(ex.Message);
			}
		}

		private static bool IsChallengeValid(IPEndPoint remote, ref RconPacketReader pvSrc)
		{
			try
			{
				var challenge = new byte[8];
				for (int i = 0; i < 8; i++)
				{
					challenge[i] = pvSrc.ReadByte();
				}
				if (m_Challenges.ContainsKey(remote.Address.ToString()) && m_Challenges[remote.Address.ToString()].Challenge.SequenceEqual(challenge) && m_Challenges[remote.Address.ToString()].LastChallenge > DateTime.Now.AddMinutes(-30))
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

			m_Challenges[remote.Address.ToString()].Refresh();
			return RconResponsePackets.Success;
		}

		public static void BlockGeneralAtLogin(ChatUser from, Channel channel, string param)
		{
			if (param.Contains("General") && from.Mobile.NetState.ConnectedFor.TotalSeconds < 5)
				return;

			ChatActionHandlers.JoinChannel(from, channel, param);
		}
		private static void RelayChatPacket(ChatUser from, Channel channel, string param)
		{
			ChatActionHandlers.ChannelMessage(from, channel, param);
			if (channel.Name == rconConfig.ChatChannel || rconConfig.ChatChannel == "*")
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
			int access = pvSrc.ReadInt32();
			// bool ascii = pvSrc.ReadBoolean();

			if (access > 10 || access < 0)
				return RconResponsePackets.Fail;

			AccessLevel accessLevel = (AccessLevel)access;

			World.Broadcast(hue, false, accessLevel, message);
			return RconResponsePackets.Success;
		}

		private static byte[] ChannelSend(IPEndPoint remote, PacketReader pvSrc)
		{
			string channel_name = pvSrc.ReadUTF8String();
			string message = pvSrc.ReadUTF8String();
			int hue = pvSrc.ReadInt32();
			// bool ascii_text = pvSrc.ReadBoolean();
			Channel channel = Channel.FindChannelByName(channel_name);

			if (channel != null)
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
			if (m_Challenges.ContainsKey(remote.Address.ToString()))
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
			CommandLogging.WriteLine(null, "RCON saving the server");
			AutoSave.Save();
		}

		private static void Shutdown(IPEndPoint remote, PacketReader pvSrc)
		{
			bool save = pvSrc.ReadBoolean();
			bool restart = pvSrc.ReadBoolean();
			Console.WriteLine("RCON: Shutting down server (Restart: {0}) (Save: {1}) [{2}]", restart, save, DateTime.Now);
			CommandLogging.WriteLine(null, "RCON shutting down server (Restart: {0}) (Save: {1}) [{2}]", restart, save, DateTime.Now);

			if (save && !AutoRestart.Restarting)
				AutoSave.Save();

			Core.Kill(restart);
		}

		private static byte[] Status(IPEndPoint remote, PacketReader pvSrc)
		{
			string shardName = ServerList.ServerName;
			int userTotalCount = Mobiles.PlayerMobile.Instances.Count;
			int userOnlineCount = NetState.Instances.Count;
			int itemTotalCount = World.Items.Count;
			int accountTotalCount = Accounting.Accounts.Count;

			byte[] result = { 0xFF, 0xFF, 0xFF, 0xFF, 0x0A };
			result = result.Concat(Encoding.ASCII.GetBytes(shardName)).Append((byte)0x00).Concat(BitConverter.GetBytes(userOnlineCount).Reverse()).Concat(BitConverter.GetBytes(userTotalCount).Reverse()).Concat(BitConverter.GetBytes(accountTotalCount).Reverse()).Concat(BitConverter.GetBytes(itemTotalCount).Reverse()).Append((byte)0x0A).ToArray();

			return result;
		}

		private static byte[] VerifyExternal(IPEndPoint remote, PacketReader pvSrc)
		{
			int code = pvSrc.ReadInt32();
			string accountName = pvSrc.ReadString();
			var acc = (Accounting.Account)Accounting.Accounts.GetAccount(accountName);

			Mobile m_Found = null;
			for(var i = 0; i < acc.Length; i++)
			{
				var mob = acc[i];
				if(mob == null || mob.NetState == null)
					continue;

				m_Found = mob;
				break;
			}

			if (m_Found == null)
				return RconResponsePackets.Fail;

			var x = new Tuple<int, DateTime>(code, DateTime.Now);
			m_AccountLinkChallenges.Add(accountName, x);

			m_Found.SendMessage(0, "An external source has requested to link with your account. Please use the following command.");
			m_Found.SendMessage(0, "[VerifyExternal {0}", code);

			CommandLogging.WriteLine(null, "RCON sending verification request ({0}) to {1}", code, CommandLogging.Format(acc));
			return RconResponsePackets.Success;
		}

		private static byte[] KickBanUser(IPEndPoint remote, PacketReader pvSrc)
		{
			bool ban = pvSrc.ReadBoolean();
			bool kick = pvSrc.ReadBoolean();

			if (!kick && !ban)
				return RconResponsePackets.Fail;

			bool accountType = pvSrc.ReadBoolean();
			string name = pvSrc.ReadString();

			if (accountType)
			{
				var acc = Accounting.Accounts.GetAccount(name);
				if(acc != null)
				{
					KickBanAccount(acc, kick, ban);
					return RconResponsePackets.Success;
				}
			}
			else
			{
				foreach (var x in NetState.Instances)
				{
					if (x == null)
						continue;

					if (x.Mobile.Name == name)
					{
						KickBanMobile(x.Mobile, kick, ban);
						return RconResponsePackets.Success;
					}
				}
			}

			return RconResponsePackets.Fail;
		}

		private static void KickBanMobile(Mobile to, bool kick, bool ban)
		{
			if (kick)
			{
				to.NetState.Dispose();
				CommandLogging.WriteLine(null, "RCON kicking {0}", CommandLogging.Format(to));
			}
			if (ban)
			{
				to.Account.Banned = true;
				CommandLogging.WriteLine(null, "RCON banning {0}", CommandLogging.Format(to.Account));
			}
		}

		private static void KickBanAccount(Accounting.IAccount to, bool kick, bool ban)
		{
			if (kick)
			{
				Mobile m_Found = null;
				for (var i = 0; i < to.Length; i++)
				{
					var mob = to[i];
					if (mob == null || mob.NetState == null)
						continue;

					m_Found = mob;
					break;
				}

				if (m_Found != null)
				{
					m_Found.NetState.Dispose();
					CommandLogging.WriteLine(null, "RCON kicking {0}", CommandLogging.Format(m_Found));
				}
			}
			if (ban)
			{
				to.Banned = true;
				CommandLogging.WriteLine(null, "RCON banning {0}", CommandLogging.Format(to));
			}
		}

		private static byte[] UnbanUser(IPEndPoint remote, PacketReader pvSrc)
		{
			string name = pvSrc.ReadString();
			var acc = Accounting.Accounts.GetAccount(name);

			if (acc == null)
				return RconResponsePackets.Fail;

			acc.Banned = false;
			CommandLogging.WriteLine(null, "RCON unbanning {0}", CommandLogging.Format(acc));
			return RconResponsePackets.Success;
		}

		private static byte[] GetOnlineUsers(IPEndPoint remote, PacketReader pvSrc)
		{
			int startIndex = pvSrc.ReadInt32();
			int maxEntries = pvSrc.ReadInt32();
			
			if (maxEntries == 0 || maxEntries < -1)
				return RconResponsePackets.Fail;

			byte[] return_packet = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x0A };
			int i = -1;
			foreach (var ns in NetState.Instances)
			{
				if (ns == null)
					continue;

				i++;
				if (i < startIndex)
					continue;

				if (maxEntries != -1 && i > maxEntries - 1)
					break;

				var mob = ns.Mobile;
				byte[] packet = Encoding.ASCII.GetBytes(mob.Name + "\t" + mob.Account.Username + "\t" + mob.Region.Name + "\t" + mob.Location.X + "," + mob.Location.Y + "," + mob.Location.Z + "\t" + ns.Address.ToString() + "\n");
				return_packet = return_packet.Concat(packet).ToArray();
			}

			return_packet = return_packet.Append((byte)0x0A).ToArray();
			return return_packet;
		}
	}
}