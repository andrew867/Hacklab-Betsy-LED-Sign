using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace Betsy1
{
    /*  Simple protocol handler for receiving the BMIX protocol, as defined by BlinkenLights.
     * 
     */
    struct bLayer
    {
        public SignLayer pLayer;
        public int port;
        public UdpClient bMixRecv;
        public IPEndPoint ipEnd;
    }

    class BMIX
    {

        int LWIDTH, LHEIGHT;
        public event EventHandler FrameReceived;
        Byte[,,] outData;

        List<bLayer> bLayers;

        public BMIX(int SignWidth, int SignHeight)
        {

            // TODO: Read these out of the protocol
            LWIDTH = SignWidth;
            LHEIGHT = SignHeight;

            bLayers = new List<bLayer>();
            
        }

        public void addLayer(SignLayer pLayer, int port)
        {
            // Add a layer to the bMIX listener.
            bLayer bL = new bLayer();
            bL.port = port;
            bL.pLayer = pLayer;

            bL.ipEnd = new IPEndPoint(IPAddress.Any, port);
            bL.bMixRecv = new UdpClient(bL.ipEnd);

            BMIXStateObject SO = new BMIXStateObject();
            SO.bMixPort = bL;
            bL.bMixRecv.BeginReceive(new AsyncCallback(ReceiveCallback), SO);

            // Add to the layer list.
            bLayers.Add(bL);

        }

        protected virtual void OnFrameReceived(EventArgs e)
        {
            if (FrameReceived != null)
                FrameReceived(this, e);
        }

        private void ReceiveCallback(IAsyncResult ar)
        {
            BMIXStateObject SO = (BMIXStateObject)ar.AsyncState;
            
            Byte[] data = SO.bMixPort.bMixRecv.EndReceive(ar, ref SO.bMixPort.ipEnd);
            // Console.WriteLine("BMIX Received " + data.Length + " bytes");
            SignLayer pL = SO.bMixPort.pLayer;

            int maxw = LWIDTH;
            int maxh = LHEIGHT;

            /*
             *  BMIX PROTOCOL:
             * byte  0: 0x23		- magic number
                byte  1: 0x54		- magic number
                byte  2: 0x26		- magic number
                byte  3: 0x66		- magic number
                byte  4: height MSB	- upper 8 bits of height
                byte  5: height LSB	- lower 8 bits of height
                byte  6: width MSB	- upper 8 bits of width
                byte  7: width LSB	- lower 8 bits of width
                byte  8: channels MSB	- upper 8 bits of channels (must be 0x00)
                byte  9: channels LSB	- lower 8 bits of channels (must be 0x01)
                byte 10: maxval MSB	- upper 8 bits of maxval (must be 0x00)
                byte 11: maxval LSB	- lower 8 bits of maxval (must be 0xff)
             * 
             */


            if (data[0] == (byte)0x23 && data[1] == (byte)0x54 && data[2] == (byte)0x26 && data[3] == (byte)0x66)
            {
                int h = (data[4] << 8) | data[5];
                int w = (data[6] << 8) | data[7];
                int chan = (data[8] << 8) | data[9];
                int s = 12; // start at

                Console.WriteLine("Got a BMIX Frame: " + w + ", " + h + " channels: " + chan + " size: " + data.Length);
                int i = 0;

                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        i = ((y * w) + x) * chan;
                        // If it is in single-channel mode, make it greyscale.
                        if (chan == 1)
                        {

                            pL.pData[x, y, 0] = (byte)(data[s + i] & 0xFF);
                            pL.pData[x, y, 1] = 0; // outData[x, y, 0];
                            pL.pData[x, y, 2] = 0; // outData[x, y, 0];
                        }
                        else if (chan == 3)
                        {

                            pL.pData[x, y, 0] = (byte)(data[s + i] & 0xFF);
                            pL.pData[x, y, 1] = (byte)(data[s + i + 1] & 0xFF);
                            pL.pData[x, y, 2] = (byte)(data[s + i + 2] & 0xFF);
                        }

                    }
                }
            }

            pL.lastPacket = DateTime.Now;

            OnFrameReceived(new EventArgs());
            SO.bMixPort.bMixRecv.BeginReceive(new AsyncCallback(ReceiveCallback), SO);
        }

    }

    class BMIXStateObject
    {
        public bLayer bMixPort;
    }

}
