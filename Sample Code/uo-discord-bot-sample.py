import asyncio
import json
import datetime
import re

from AsyncUORcon import AsyncUORcon

from discord.ext import commands as discord_commands
from aioudp import UDPServer


class Config:
    def __init__(self, cfg="UORconConfig.json"):
        self.filename = cfg
        with open(f'.\\{cfg}', 'rb') as file:
            self.cfgdata = json.load(file)

    def get_discord_token(self):
        return self.cfgdata['login']['discord']['token']

    def get_discord_home_channel(self):
        return int(self.cfgdata['discord-settings']['home-channel-id'])

    def get_discord_command_accounts(self):
        r = [int(self.cfgdata['discord-settings']['owner-id'])]
        for mod in self.cfgdata['discord-settings']['moderators']:
            r.append(int(mod))
        return r

    def get_uo_chat_channel(self):
        return self.cfgdata['uo-settings']['chat-channel']

    def get_uo_server_address(self):
        return self.cfgdata['uo-settings']['server-ip']

    def get_uo_server_port(self):
        return int(self.cfgdata['uo-settings']['server-port'])

    def get_uo_rcon_password(self):
        return self.cfgdata['login']['uo']['rcon-password']

    def get_listen_address(self):
        return self.cfgdata['listen-server']['listen-address']

    def get_listen_port(self):
        return int(self.cfgdata['listen-server']['listen-port'])

    def is_listen_server_enabled(self):
        return bool(self.cfgdata['listen-server']['enable-listen-server'])

    def save_to_file(self):
        with open(f'.\\{self.filename}', 'w', encoding='utf-8') as outfile:
            json.dump(self.cfgdata, outfile, indent=2, ensure_ascii=False)


class BaseMessage:
    def __init__(self, service="", author="", time=None, message="", location=""):
        self.service = service.lower()
        self.author = author
        self.time = datetime.datetime.now()
        if time:
            self.time = time
        self.message = message
        self.location = location

    def __str__(self):
        return f'[{self.time.strftime("%H:%M:%S")}] {self.service} | {self.location} | {self.author}: {self.message}'

    def relay_string(self, target_service=False):
        if target_service and target_service == "discord":
            return f'**__{self.service[0].capitalize()}__** | *{self.location}* | **{self.author}**: {self.message}'
        if self.service == "discord":
            return f'{self.service[0].capitalize()} | {self.author}: {self.message}'
        return f'{self.service[0].capitalize()} | {self.location} | {self.author}: {self.message}'


class DiscordMessage(BaseMessage):
    def __init__(self, author="", time=None, message="", location=""):
        super().__init__(service="discord", author=author, time=time, message=message, location=location)

    def relay_string(self, target_service=False):
        return f'{self.service[0].capitalize()} | {self.author}: {self.message}'


class UOMessage(BaseMessage):
    def __init__(self, data=b'', location=""):
        super().__init__(service="uo", location=location)

        mstring = data.replace(b'\xff', b'').replace(b'\x00', b'').replace(b'\n', b'').decode("ascii")
        split = mstring.split("\t")
        self.log_type = None

        self.chat_message = False
        if split[1] == "m":
            self.chat_message = True
            self.log_type = "chat message"
            self.location += f" | {split[2]}"

            author = split[3]
            m = re.search("^<([0-9]+)>(.+)$", author)
            if m:
                self.author = m.group(2)
                self.id = int(m.group(1))
            else:
                self.author = author
                self.id = -1
            self.message = split[4]
        elif split[1] == "v":
            self.chat_message = False
            self.log_type = "verify"
            self.author = split[2]
            self.message = int(split[3])
        elif split[1] == "mw":
            self.chat_message = False
            self.log_type = "world message"
            self.author = split[2]
            self.location += f" ({', '.join(split[3].split(' '))})"
            self.message = split[4]

    def relay_string(self, target_service=False):
        return f'{self.service.upper()} | {self.author}: {self.message}'


class UO(discord_commands.Cog):
    listen_host = ''
    listen_port = 27035
    server_address = '127.0.0.1'
    server_port = 27030
    rcon_password = ""
    listen_server = None

    def __init__(self, bot):
        self.discord_bot = bot
        self.server_address = bot.config.get_uo_server_address()
        self.server_port = bot.config.get_uo_server_port()
        self.rcon_password = bot.config.get_uo_rcon_password()
        self.rcon_socket = AsyncUORcon(host=self.server_address, port=self.server_port, password=self.rcon_password, loop=bot.loop)

        if bot.config.is_listen_server_enabled():
            self.listen_host = bot.config.get_listen_address()
            self.listen_port = bot.config.get_listen_port()
            self.listen_server = UDPServer()
            self.listen_server.run(self.listen_host, self.listen_port, loop=self.discord_bot.loop)
            self.listen_server.subscribe(self.on_datagram_received)

        self.discord_bot.loop.create_task(self.start())

    async def start(self):
        await self.discord_bot.wait_until_ready()
        self.discord_bot.loop.create_task(self.keep_alive())

    async def on_datagram_received(self, data, addr):
        source = f'{addr[0]}:{addr[1]}'

        if data[0:2] == b'UO':
            message = UOMessage(data=data, location=source)
            if message.chat_message or message.log_type == "world message":
                print(message)
                await self.discord_bot.on_service_message(message)
            elif message.log_type == "verify":
                if self.rcon_socket.verify_check(message.author, int(message.message)):
                    relay_channel = self.discord_bot.get_channel(self.discord_bot.config.get_discord_home_channel())
                    await relay_channel.send(f"The {message.author} account is now linked.")

    async def keep_alive(self):
        while True:
            await asyncio.sleep(30)
            try:
                await self.rcon_socket.keep_alive()
            except:
                print("An error occurred trying to send a keep alive request to the server.")

    async def send(self, message, channel=None, hue=0):
        try:
            if channel:
                await self.rcon_socket.send_channel_chat(channel, message, hue=hue)
            else:
                await self.rcon_socket.broadcast(message, hue=hue)
        except:
            print("An error occurred trying to send a message to the server.")

    def check_if_allowed_commands(self, ctx):
        list_ids = self.discord_bot.config.get_discord_command_accounts()
        if ctx.message.author.id in list_ids:
            return True
        return False

    @discord_commands.command(name="uo")
    async def uo_commands(self, ctx, command: str, *args):
        if not self.check_if_allowed_commands(ctx):
            return None
        sanitized = command.lower()
        if "broadcast" == sanitized or "b" == sanitized:
            msg = ""
            for param in args:
                msg += f"{param} "
            await self.send(msg[:-1])
        elif "save" == sanitized:
            await self.rcon_socket.server_save()
        elif "shutdown" == sanitized:
            await self.rcon_socket.server_shutdown()
        elif "restart" == sanitized:
            await self.rcon_socket.server_shutdown(restart=True)
        elif "status" == sanitized:
            res = await self.rcon_socket.server_status()
            if res != b'\xFF':
                item_count = int.from_bytes(res[-5:-1], "big")
                account_count = int.from_bytes(res[-9:-5], "big")
                character_count = int.from_bytes(res[-13:-9], "big")
                online_character_count = int.from_bytes(res[-17:-13], "big")
                shard_name = res[5:-18].decode("utf-8")
                concat_string = f"{shard_name} - {online_character_count}/{character_count} characters online, {account_count} accounts, {item_count} items"
                # print(concat_string)
                await ctx.send(concat_string)
        elif "verify" == sanitized:
            res = await self.rcon_socket.verify(args[0])
            if res != b'\xFF':
                await ctx.send(f"Sent a verification code to {args[0]} in-game.")
        elif "kick" == sanitized:
            res = await self.rcon_socket.kickban(args[0], kick=True, ban=False)
            if res != b'\xFF':
                await ctx.send(f"{args[0]} kicked.")
        elif "ban" == sanitized:
            res = await self.rcon_socket.kickban(args[0], kick=False, ban=True)
            if res != b'\xFF':
                await ctx.send(f"{args[0]} banned.")
        elif "kickban" == sanitized:
            res = await self.rcon_socket.kickban(args[0], kick=True, ban=True)
            if res != b'\xFF':
                await ctx.send(f"{args[0]} kicked and banned.")
        elif "unban" == sanitized:
            res = await self.rcon_socket.unban(args[0])
            if res != b'\xFF':
                await ctx.send(f"{args[0]} kicked and banned.")
        elif "online" == sanitized:
            res = await self.rcon_socket.online_users()
            if res != b'\xFF':
                t = res[5:-1]
                if t == b'':
                    await ctx.send("No players online.")
                else:
                    t = t.split(b'\n')
                    concat_string = "Character (Account) | Region (coords) | IP\n"

                    for x in t:
                        if x:
                            player = x.split(b'\t')
                            name = player[0].decode("utf-8")
                            account = player[1].decode("utf-8")
                            region = player[2].decode("utf-8")
                            loc = player[3].split(b',')
                            ip = player[4].decode("utf-8")
                            concat_string += f"{name} ({account}) | {region} ({loc[0].decode('utf-8')}x{loc[1].decode('utf-8')}y{loc[2].decode('utf-8')}z) | {ip}\n"

                    # print(concat_string)
                    await ctx.send(concat_string)


class UOBotDiscord(discord_commands.Bot):
    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self.config = config

    async def on_ready(self):
        print(f"Logged into Discord | {self.user.name}")
        await self.get_cog('UO').start()
        
    async def on_service_message(self, message):
        if message.service == "uo":
            relay_channel = self.get_channel(self.config.get_discord_home_channel())
            await relay_channel.send(message.relay_string())

    async def on_message(self, message):
        mobj = DiscordMessage(author=message.author.name, message=message.content, location=message.channel.name)

        if not message.content.startswith("$") and message.author.name != self.user.name:
            print(str(mobj))

            if message.channel.id == int(self.config.get_discord_home_channel()):
                uo = self.get_cog('UO')
                await uo.send(f"Discord | {message.author.name}: {message.content}", channel=self.config.get_uo_chat_channel())
                return
        else:
            await self.process_commands(message)


def setup(discord_bot):
    discord_bot.add_cog(UO(discord_bot))


config = Config()

x = UOBotDiscord(command_prefix='$', config=config)
setup(x)

x.run(config.get_discord_token())
