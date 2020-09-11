/*

MIT License

Copyright (c) 2017 Peter Bjorklund

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

*/
using System;
using Piot.Brisk;
using Piot.Brisk.Commands;
using Piot.Brisk.Connect;
using Piot.Brook;
using Piot.Log;
using Piot.Tend.Client;
using Flux.Client.Datagram;

namespace BriskConsole
{
    using Version = Piot.Brisk.Commands.Version;

    class Client
    {
        readonly Connector connector;
        readonly ILog log;

        public Client(ILog log, string hostnameAndPort)
        {
            this.log = log;
            var port = new UdpClient();
            connector = new Connector(log, 30, port, true);
            var info = new ConnectInfo
            {
                BriskVersion = new NameVersion
                {
                    Version = new Version(1, 0, 0, ""),
                    Name = "brisk"
                },
                SdkVersion = new NameVersion
                {
                    Version = new Version(1, 0, 0, ""),
                    Name = "unknown"
                },
                SchemaVersion = new NameVersion
                {
                    Version = new Version(1, 0, 0, ""),
                    Name = "schema"
                },
                ApplicationVersion = new NameVersion
                {
                    Version = new Version(1, 0, 0, ""),
                    Name = "brisk"
                },
                Payload = new CustomConnectPayload
                {
                    Payload = new byte[] { 0x08 },
                },
            };
            connector.Connect(hostnameAndPort, info);
        }

        public void Update()
        {
            connector.Update();

            var (stream, sequenceId, canSend) = connector.PreparePacket();

            connector.SendPreparedPacket();
        }
    }
}
