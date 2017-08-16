using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Timers;

namespace Betsy1
{

    /* This module handles communication with Betsy, the Hacklab's lovable giant LED sign.
     * 
     * 
     */

    public class SignLayer
    {
        public bool alpha; // When true, black pixels are not painted.
        public Byte[,,] pData;
        public DateTime lastPacket;
        public bool Hidden; // When true, the layer is not rendered.

        int SW, SH;

        public SignLayer(int SignWidth, int SignHeight)
        {
            // Set up the sign
            SW = SignWidth;
            SH = SignHeight;

            pData = new Byte[SW, SH, 3];

        }
    }

    public class HacklabBetsy
    {

        IPAddress[] Tiles; // The IP addresses of each tile
        UdpClient[] Clients; // UDP communications socket for each tile

        IPAddress TilesBroadcast; // The broadcast address, to talk to the entire grid at once.
        UdpClient ClientBroadcast;

        int[] OffsetsX; // X and Y offsets of each tile
        int[] OffsetsY;
        String[] TileAdr;

        // The width and height of the array.  This shouldn't be hard-coded actually.
        public int LWIDTH = 162;
        public int LHEIGHT = 108;

        int tileCount = 0;

        public Byte[,,] pixData; // The buffer of what is on the sign right now, in 8-bit-per-channel
        public double gainScale = 0.2;

        System.Timers.Timer aPing;

        public bool SignOnline = false; // Whether or not the sign is currently online.
        IPAddress bindIP;

        public HacklabBetsy(string bindLocal)
        {
            // TODO: Find the local IP endpoint

            if (bindLocal == "Any")
            {
                bindIP = IPAddress.IPv6Any; // usb3 ethernet
            }
            else
            {
                bindIP = IPAddress.Parse(bindLocal); // usb3 ethernet
            }

            setupBinds();

            aPing = new System.Timers.Timer();
            aPing.Elapsed += new ElapsedEventHandler(APing_Elapsed);
            aPing.Interval = 1000;

            aPing.Start();


        }

        private void setupBinds()
        {
            

            IPEndPoint Binder = new IPEndPoint(bindIP, 0);

            // Read the inventory.
            JObject jInv;
            JArray jInvItems;
            JObject item;
            JArray jTileMap;

            jInv = JObject.Parse(File.ReadAllText(Directory.GetCurrentDirectory() + @"\..\..\inventory.json"));
            jInvItems = (JArray)jInv["inventory"];

            tileCount = jInvItems.Count;

            Tiles = new IPAddress[tileCount];
            Clients = new UdpClient[tileCount];
            OffsetsX = new int[tileCount];
            OffsetsY = new int[tileCount];
            TileAdr = new String[tileCount];

            for (int i = 0; i < tileCount; i++)
            {
                item = (JObject)jInvItems[i];

                //      "serial_number": 2,
                //      "mac": "00-04-A3-1B-93-2B",
                //      "ipv6_link_local": "fe80::204:a3ff:fe1b:932b",
                //      "itc_revision": "0.2"
                TileAdr[i] = (String)item["ipv6_link_local"];

                //Console.WriteLine("Tile " + i + ", address: " + TileAdr[i]);

                Tiles[i] = IPAddress.Parse((String)item["ipv6_link_local"]);
                Clients[i] = new UdpClient(Binder);
                Clients[i].Connect(Tiles[i], 48757);
            }

            // Set up the broadcast address.
            TilesBroadcast = IPAddress.Parse("ff02::1");
            ClientBroadcast = new UdpClient(Binder);
            ClientBroadcast.Connect(TilesBroadcast, 48757);

            // Now parse the tile map.
            jTileMap = (JArray)jInv["tilemap"];
            for (int i = 0; i < jTileMap.Count; i++)
            {
                // Find this tile in the inventory and set the offset.
                item = (JObject)jTileMap[i];

                String ipAdr = (String)item["ipv6_link_local"];
                for (int j = 0; j < tileCount; j++)
                {
                    if (TileAdr[j] == ipAdr)
                    {
                        OffsetsX[j] = (int)item["start"][0];
                        OffsetsY[j] = (int)item["start"][1];
                        break;
                    }
                }

            }

            pixData = new Byte[LWIDTH, LHEIGHT, 3];

            setGain(100);

        }


        public void DrawAll()
        {
            // Send the pixel data to all signs
            if (!SignOnline) return; // Don't draw to the sign if the sign is offline!

            for (int i = 0; i < tileCount; i++)
            {
                sendToTile(i);
            }

            // Send the broadcast to make them all change.
            Byte[] sendBytes = Encoding.ASCII.GetBytes("dpc upload 0;");
            ClientBroadcast.Send(sendBytes, sendBytes.Length);
        }


        private void sendToTile(int tileID)
        {
            int xOff = OffsetsX[tileID];
            int yOff = OffsetsY[tileID];

            //Console.WriteLine("Sending to tile " + tileID, " offsets are " + xOff + ", " + yOff);

            Byte[] imgData = new Byte[1944]; // 18 x 18 x 6 (16 bit);

            // Build the image data.
            for (int y = 0; y < 18; y++)
            {
                for (int x = 0; x < 18; x++)
                {
                    // Pixel position in the byte array index
                    int p = (y * 18 * 6) + (x * 6);
                    //Color c = b.GetPixel(x + xOff, y + yOff);

                    // R G B, convert to 12-bits
                    int R = (int)(gamma(pixData[x + xOff, y + yOff, 0] * 16) * 1);
                    int G = (int)(gamma(pixData[x + xOff, y + yOff, 1] * 16) * 1);
                    int B = (int)(gamma(pixData[x + xOff, y + yOff, 2] * 16) * 1);

                    imgData[p + 1] = (Byte)(R >> 8);
                    imgData[p + 0] = (Byte)(R & 0xFF);
                    imgData[p + 3] = (Byte)(G >> 8);
                    imgData[p + 2] = (Byte)(G & 0xFF);
                    imgData[p + 5] = (Byte)(B >> 8);
                    imgData[p + 4] = (Byte)(B & 0xFF);
                }
            }

            // Send this section of the picture to this tile.
            Byte[] cmdBytes = Encoding.ASCII.GetBytes("dpc data 0 0;");
            Byte[] sendBytes = new Byte[13 + 1024];
            Buffer.BlockCopy(cmdBytes, 0, sendBytes, 0, 13);
            Buffer.BlockCopy(imgData, 0, sendBytes, 13, 1024);
            Clients[tileID].Send(sendBytes, sendBytes.Length);

            // Send the 2nd half of the buffer
            cmdBytes = Encoding.ASCII.GetBytes("dpc data 0 1024;");
            sendBytes = new Byte[cmdBytes.Length + 1024];
            Buffer.BlockCopy(cmdBytes, 0, sendBytes, 0, cmdBytes.Length);
            Buffer.BlockCopy(imgData, 1024, sendBytes, 16, 920);
            Clients[tileID].Send(sendBytes, sendBytes.Length);

        }

        public void setGain(Byte bGain)
        {
            // Send the broadcast to make them all change.
            Byte[] sendBytes = Encoding.ASCII.GetBytes("dpc gain " + bGain + ";");
            ClientBroadcast.Send(sendBytes, sendBytes.Length);

        }

        public void resetSign()
        {
            // Send the broadcast to make them all change.
            Byte[] sendBytes = Encoding.ASCII.GetBytes("reset firmware;");
            ClientBroadcast.Send(sendBytes, sendBytes.Length);

        }

        // Adjust the gamma to fix the LED stuff.
        public int gamma(int inC)
        {
            // return inC;
            return (int)((Math.Pow(inC, 2) / 4096) * gainScale);
        }
        
        private void APing_Elapsed(object sender, ElapsedEventArgs e)
        {
            aPing.Enabled = false;

            Ping pingSender = new Ping();
            PingOptions options = new PingOptions();

            // Use the default Ttl value which is 128,
            // but change the fragmentation behavior.
            options.DontFragment = true;

            

            // Create a buffer of 32 bytes of data to be transmitted.
            string data = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaabb";
            byte[] buffer = Encoding.ASCII.GetBytes(data);
            int timeout = 120;
            PingReply reply = pingSender.Send(Tiles[0], timeout, buffer, options);

            bool isOnline = false;
            if (reply.Status == IPStatus.Success)
            {
                // Console.WriteLine("Address: {0}", reply.Address.ToString());
                // Console.WriteLine("RoundTrip time: {0}", reply.RoundtripTime);

                reply = pingSender.Send(Tiles[Tiles.Length-1], timeout, buffer, options);
                if (reply.Status == IPStatus.Success)
                {
                    isOnline = true;
                }
            }

            // TEMP TEMP
            //isOnline = true;

            // If the sign's status is different from last time
            if (isOnline != SignOnline)
            {
                if (isOnline)
                {
                    // Sign is NOW online.
                    // Send reset command.

                    Console.WriteLine("Sign is available!");

                    System.Threading.Thread.Sleep(3000);

                    // Rebind the network interface.
                    setupBinds(); 

                    resetSign();
                    // Set gamma.
                    System.Threading.Thread.Sleep(500);

                    setGain(100);
                }
                else
                {

                    Console.WriteLine("Sign is offline");
                }

                SignOnline = isOnline;

            }

            aPing.Enabled = true;


        }


    }
}
