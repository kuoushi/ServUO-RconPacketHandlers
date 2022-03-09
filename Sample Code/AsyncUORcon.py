import asyncio
import random
import datetime

from async_timeout import timeout


class Protocol(asyncio.Protocol):
    def __init__(self, message, on_con_lost=None):
        self.message = message
        self.on_con_lost = on_con_lost
        self.timeout = None
        self.transport = None
        self.received = b''

    def connection_made(self, transport):
        self.transport = transport
        self.transport.sendto(self.message)

    def datagram_received(self, data, addr):
        self.received = data
        self.transport.close()

    def error_received(self, exc):
        print('Error received:', exc)

    def connection_lost(self, exc):
        if self.on_con_lost:
            try:
                self.on_con_lost.set_result(True)
            except:
                pass


class AsyncUORcon:
    host = '127.0.0.1'
    port = 27030
    password = ''

    start_bytes = b'\xFF\xFF\xFF\xFF'
    end_bytes = b'\n'
    packet_size = 1024

    challenges = {}

    def __init__(self, host='127.0.0.1', port=27030, password='', loop=None):
        self.host = host
        self.port = int(port)
        self.password = password
        if loop:
            self.loop = loop

    def verify_check(self, name: str, code: int):
        if name in self.challenges and self.challenges[name][0] == code:
            del self.challenges[name]
            return True
        return False

    async def send_wait_response(self, message, timeout_param=1.5):
        on_con_lost = self.loop.create_future()
        transport, protocol = await self.loop.create_datagram_endpoint(
            lambda: Protocol(message, on_con_lost),
            remote_addr=(self.host, self.port))

        try:
            async with timeout(timeout_param):
                await on_con_lost
        finally:
            if transport:
                transport.close()

        return protocol.received

    async def send(self, message):
        transport, protocol = await self.loop.create_datagram_endpoint(
            lambda: Protocol(message),
            remote_addr=(self.host, self.port))
        transport.close()

    async def _rcon_challenge(self):
        msg = self.start_bytes + b'\x1A' + self.end_bytes
        response = await self.send_wait_response(msg)
        challenge = response[6:14]
        return challenge

    async def rcon_no_auth(self, command):
        msg = self.start_bytes + command + self.end_bytes
        response = await self.send_wait_response(msg)
        return response

    async def rcon(self, command, *args, **kwargs):
        challenge = await self._rcon_challenge()
        msg = self.start_bytes + command + challenge + self.password.encode() + b'\x00'

        for x in args:
            if isinstance(x, str):
                msg += x.encode() + b'\x00'
            elif isinstance(x, bool):
                msg += x.to_bytes(1, byteorder='big')
            elif isinstance(x, int):
                msg += x.to_bytes(4, byteorder='big')

        msg += self.end_bytes

        # print(msg)

        if 'timeout_param' in kwargs.items():
            response = await self.send_wait_response(msg, timeout_param=kwargs['timeout_param'])
        else:
            response = await self.send_wait_response(msg)
        return response

    async def send_channel_chat(self, channel, message, hue=0, ascii_text=False):
        return await self.rcon(b'\x1D', channel, message, hue, ascii_text)

    async def broadcast(self, message: str, hue=1, ascii_text=False, staff_level=0):
        return await self.rcon(b'\x1C', message, hue, staff_level, ascii_text)

    async def keep_alive(self):
        return await self.rcon_no_auth(b'\x20')

    async def server_save(self, timeout_param=15):
        return await self.rcon(b'\x1E', timeout_param=timeout_param)

    async def server_shutdown(self, save=True, restart=False):
        return await self.rcon(b'\x1F', save, restart)

    async def server_status(self):
        return await self.rcon(b'\x1B')

    async def verify(self, account: str, code=-1):
        if code == -1:
            code = random.randint(10000, 99999)
        self.challenges[account] = (code, datetime.datetime.now())
        return await self.rcon(b'\x21', code, account)

    async def kickban(self, name: str, is_account=False, kick=False, ban=False):
        return await self.rcon(b'\x22', ban, kick, is_account, name)

    async def unban(self, name: str):
        return await self.rcon(b'\x23', name)

    async def online_users(self, start_index=0, max_entries=20):
        return await self.rcon(b'\x24', start_index, max_entries)

    async def add_log_target(self, ip: str, port: int):
        return await self.rcon(b'\x25', ip, port)

    async def remove_log_target(self, ip: str, port: int):
        return await self.rcon(b'\x26', ip, port)


async def main(loop):
    # for testing library directly
    x = AsyncUORcon('127.0.0.1', port=27030, password='passwordgoeshere', loop=loop)
    # print(await x.server_status())

if __name__ == "__main__":
    loop = asyncio.get_event_loop()
    loop.run_until_complete(main(loop))
