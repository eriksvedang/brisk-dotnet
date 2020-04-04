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
using Piot.Brisk.Connect;
using Piot.Brook;
using Piot.Log;
using Piot.Tend.Client;

namespace BriskConsole
{
    class Client : IReceiveStream, ISendStream
    {
        readonly Connector connector;
        readonly ILog log;

        public Client(ILog log, string hostnameAndPort)
        {
            this.log = log;
            connector = new Connector(log, 30);
            connector.Connect(hostnameAndPort);
        }

        public void Lost()
        {
            log.Debug("Disconnected!");
        }

        public void PacketDelivery(SequenceId sequenceId, bool wasReceived)
        {
            log.Debug("Packet delivered");
        }

        public void Receive(IInOctetStream stream, SequenceId sequenceId)
        {
            log.Debug("Receiving packet...");
        }

        public void Update()
        {
            connector.Update();
        }

        void IReceiveStream.HandleException(Exception e)
        {
            throw new NotImplementedException();
        }

        void IReceiveStream.Lost()
        {
            throw new NotImplementedException();
        }

        void IReceiveStream.OnTimeSynced()
        {
        }

        void IReceiveStream.PacketDelivery(SequenceId sequenceId, bool wasReceived)
        {
            throw new NotImplementedException();
        }

        void IReceiveStream.Receive(IInOctetStream stream, SequenceId sequenceId)
        {
            throw new NotImplementedException();
        }

        bool ISendStream.Send(IOutOctetStream stream, SequenceId sequenceId)
        {
            log.Debug("Sending packet...");
            return true;
        }
    }
}
