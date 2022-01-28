# ServUO-RconPacketHandlers
Provides basic RCON functionality for ServUO servers.

Usage
-----
Grab the Custom folder from this repo and drop it in your server's Scripts folder.

The result should look like this:
ServUO
- Scripts
- - Custom
- - - RconConfig.cfg
- - - RconPacketHandlers.cs

Check out the RconConfig.cfg file and change any settings in there that you need. The main thing you'll want to set are your RconPassword and your ListenPort. Once you've changed everything there to what you want, you can just run your server like normal.


WARNING

At the moment, this script ONLY allows the server to read and parse incoming pockets in resembling rcon packets. It does not connect to any systems or do anything on its own. Currently, you will have to write your own code to interface with the server's new rcon functionality. I won't help you write the necessary code to create and send packets, but I will be potentially adding some code to this repo to help in understanding that at some point in the future.

For now, I'll describe the packets below.


Packets and Commands
(to be written)
