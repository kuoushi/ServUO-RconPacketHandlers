import asyncio
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

    def __init__(self, host='127.0.0.1', port=27030, password='', loop=None):
        self.host = host
        self.port = int(port)
        self.password = password
        if loop:
            self.loop = loop

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

        if 'timeout_param' in kwargs.items():
            response = await self.send_wait_response(msg, timeout_param=kwargs['timeout_param'])
        else:
            response = await self.send_wait_response(msg)
        return response

    async def send_channel_chat(self, channel, message, hue=0, ascii_text=False):
        return await self.rcon(b'\x1D', channel, message, hue, ascii_text)

    async def broadcast(self, message: str, hue=1, ascii_text=False):
        return await self.rcon(b'\x1C', message, hue, ascii_text)

    async def keep_alive(self):
        return await self.rcon_no_auth(b'\x20')

    async def server_save(self, timeout_param=15):
        return await self.rcon(b'\x1E', timeout_param=timeout_param)

    async def server_shutdown(self, save=True, restart=False):
        return await self.rcon(b'\x1F', save, restart)


async def main(loop):
    x = AsyncUORcon('127.0.0.1', port=27030, password='passwordgoeshere', loop=loop)
    print(await x.send_channel_chat("Discord", "Channel message test.", hue=57))
    print(await x.send_channel_chat("General", "Channel message test.", hue=57))
    print(await x.broadcast("Here's a broadcast message.", hue=5))
    print(await x.keep_alive())
    # print(await x.server_save())
    # print(await x.server_shutdown(restart=True))
    await asyncio.sleep(15)
    print(await x.broadcast("Here's a broadcast message again.", hue=5))

if __name__ == "__main__":
    loop = asyncio.get_event_loop()
    loop.run_until_complete(main(loop))
