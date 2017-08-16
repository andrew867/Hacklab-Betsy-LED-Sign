using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace Betsy1
{

    /*  Receive frames from TPM2.NET and push them into the specified pixel buffer
     * 
     */ 
    class TMP2NET
    {
        UdpClient tpm2;
        IPEndPoint tpm2e;
        int LWIDTH, LHEIGHT;

        public event EventHandler FrameReceived;
        public Byte[,,] pixData;

        public TMP2NET(int SignWidth, int SignHeight, Byte [,,] pixArray)
        {

            // Listen for tpm2 messages.
            //tpm2e = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 65506);
            tpm2e = new IPEndPoint(IPAddress.Any, 65506);
            tpm2 = new UdpClient(tpm2e);
            tpm2.BeginReceive(new AsyncCallback(ReceiveCallback), null);

            // TODO: Read these out of the protocol
            LWIDTH = SignWidth;
            LHEIGHT = SignHeight;
            pixData = pixArray; // Assign a link

        }

        protected virtual void OnFrameReceived(EventArgs e)
        {
            if (FrameReceived != null)
                FrameReceived(this, e);
        }

        private void ReceiveCallback(IAsyncResult ar)
        {
            Byte[] data = tpm2.EndReceive(ar, ref tpm2e);
            //Console.WriteLine("Received " + data.Length + " bytes");

            int w = LWIDTH;
            int h = LHEIGHT;
            int s = 6; // Start at.

            if (data[0] == (byte)0x9C)
            {
                if (data[1] == (byte)0xDA) // Frame data! 
                {

                    int pblen = (data[2] & 0xFF) * 256 + (data[3] & 0xFF);

                    if (pblen >= w * h * 3)
                    {
                        for (int y = 0; y < h; y++)
                        {
                            for (int x = 0; x < w; x++)
                            {
                                int i = ((y * w) + x) * 3;

                                pixData[x, y, 0] = (byte)(data[s + i] & 0xFF);
                                pixData[x, y, 1] = (byte)(data[s + i + 1] & 0xFF);
                                pixData[x, y, 2] = (byte)(data[s + i + 2] & 0xFF);

                            }
                        }

                        OnFrameReceived(new EventArgs());
                    }

                }
            }

            tpm2.BeginReceive(new AsyncCallback(ReceiveCallback), null);
        }
    }
}
