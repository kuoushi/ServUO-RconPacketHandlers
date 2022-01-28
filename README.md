# ServUO-RconPacketHandlers
Provides basic RCON functionality for ServUO servers.

Installation
-----
Grab the Custom folder from this repo and drop it in your server's Scripts folder.

The result should look like this:
ServUO
- Scripts
- - Custom
- - - RconConfig.cfg
- - - RconPacketHandlers.cs

Check out the RconConfig.cfg file and change any settings in there that you need. The main thing you'll want to set are your RconPassword and your ListenPort. Once you've changed everything there to what you want, you can just run your server like normal.

Usage
-----
WARNING

At the moment, this script ONLY allows the server to read and parse incoming pockets in resembling rcon packets. It does not connect to any systems or do anything on its own. Currently, you will have to write your own code to interface with the server's new rcon functionality. I won't help you write the necessary code to create and send packets, but I will be potentially adding some code to this repo to help in understanding that at some point in the future.

What can you do with this? Well, you can currently use it to do a few simple things like tell the server to shutdown, broadcast messages to the whole server, or send/receive messages from a specific chat channel in the server. How you use the packets is generally up to what you can make.


Packets and Commands
-----
Basic packet structure (spaces only added for visual clarity here):
0xFF FF FF FF 1A 0A

header: int 32, -1, 4 bytes of 0xFF)
command: 0x1A (specific command bytes, detailed below. 0x1A is the get challenge byte)
(challenge, password, and other variables would go here)
footer: 0x0A

GetChallenge
0x1A
Ex: 0xFF FF FF FF 1A 0A

Send this command with no following parameters to request a challenge code from the server. The server will respond with a packet containing a series of random bytes that you will need to save, then use in all of the following commands. I recommend you get a fresh challenge code every time you send any command, but it isn't necessary just yet.

Server will send a packet like the following as a response:
0xFFFFFFFF 0A 20 1122334455667788 20 32 0A
header > success byte > delim > 8 byte challenge code > delim > success > footer

Broadcast
0x1C
Ex: 0xFF FF FF FF 1B (challenge) 00 (password) 00 (message) 00 (hue - int 32) 0A

Send this command with a challenge, the password, and your desired message to broadcast a message to the whole server. You will also need to include a 4-byte integer to specify a hue.

Channel Send
0x1D
Ex: 0xFF FF FF FF 1B (challenge) 00 (password) 00 (channel) 00 (message) 00 (hue - int 32) 0A

Send this command with a challenge, the password, your desired channel, and a message to send a message to a specific chat channel in the server. You will also need to include a 4-byte integer to specify a hue.

Shutdown/Restart
0x1F
Ex: 0xFF FF FF FF 1B (challenge) 00 (password) 00 (save bool) (restart bool) 0A

Send this command with a challenge, the password, and then two 1-byte bools to specify whether or not the server should save and/or restart.

KeepAlive
0x20
Ex: 0xFF FF FF FF 20 0A

This command is used to refresh the last time a packet was sent from the given address. It isn't used currently, but in the coming versions challenges will be flushed unless they are kept alive. Sending other commands with a challenge also refreshes the challenge code's usage.


(not yet implemented functions)
Status
0x1B
Ex: 0xFF FF FF FF 1B (challenge) 00 (password) 00 0A

Send this command with a challenge and the password to request server status information. Response format is currently still being determined.

Save
0x1E
Ex: 0xFF FF FF FF 1B (challenge) 00 (password) 00 0A

Send this command with a challenge and the password to start a server save.

Parameters
-----
Integers take up 4 bytes, booleans take up 1 byte, and strings take up an arbitrary number of bytes but they must end with a null byte.
