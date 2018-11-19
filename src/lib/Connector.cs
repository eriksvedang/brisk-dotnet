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
using System.Collections.Generic;
using System.Net;
using System.Text;
using Flux.Client.Datagram;
using Piot.Brisk.Commands;
using Piot.Brisk.Serializers;
using Piot.Brisk.Time;
using Piot.Brook;
using Piot.Brook.Octet;
using Piot.Tend.Client;
using Piot.Log;
using Piot.Brisk.deserializers;

namespace Piot.Brisk.Connect
{
    public enum ConnectionState
    {
        Challenge,
        TimeSync,
        Connected,
        Disconnected
    }

    public class Connector : IPacketReceiver
    {
        const byte NormalMode = 0x01;
        const byte OobMode = 0x02;

        ConnectionState state = ConnectionState.Challenge;
        Client udpClient;
        byte outSequenceNumber;
        uint challengeNonce;
        ushort connectionId;
        SequenceId lastIncomingSequence = SequenceId.Max;
        SequenceId outgoingSequenceNumber = SequenceId.Max;
        //Queue<byte[]> messageQueue = new Queue<byte[]>();
        IReceiveStream receiveStream;
        ISendStream sendStream;
        DateTime lastStateChange = DateTime.UtcNow;
        uint stateChangeWait = 500;
        MonotonicClockStopwatch monotonicStopwatch = new MonotonicClockStopwatch();
        IMonotonicClock monotonicClock;
        LatencyCollection latencies = new LatencyCollection();
        long localMillisecondsToRemoteMilliseconds;
        ILog log;
        DateTime lastValidHeader = DateTime.UtcNow;
        OutgoingLogic tendOut = new OutgoingLogic();
        IncomingLogic tendIn = new IncomingLogic();

        readonly bool useDebugLogging;

        public Connector(ILog log, IReceiveStream receiveStream, ISendStream sendStream)
        {
            this.receiveStream = receiveStream;
            this.sendStream = sendStream;
            this.log = log;
            this.useDebugLogging = useDebugLogging;
            monotonicClock = monotonicStopwatch;
        }

        public void Connect(string hostAndPort)
        {
            udpClient = new Client(hostAndPort, this);
            StartChallenge();
        }

        public long RemoteMonotonicMilliseconds
        {
            get
            {
                if (state != ConnectionState.Connected)
                {
                    throw new Exception("You can only check remote time if connected!");
                }
                return monotonicClock.NowMilliseconds() + localMillisecondsToRemoteMilliseconds;
            }
        }

        public long RemoteMonotonicSimulationFrame
        {
            get
            {
                return ElapsedSimulationFrame.FromElapsedMilliseconds(RemoteMonotonicMilliseconds);
            }
        }

        static void WriteHeader(IOutOctetStream outStream, byte mode, byte sequence, ushort connectionIdToSend)
        {
            outStream.WriteUint8(mode);
            outStream.WriteUint8(sequence);
            outStream.WriteUint16(connectionIdToSend);
        }

        void RequestTime(IOutOctetStream outStream)
        {
            var now = monotonicClock.NowMilliseconds();
            var timeSyncRequest = new TimeSyncRequest(now);

            TimeSyncRequestSerializer.Serialize(outStream, timeSyncRequest);
        }

        void SendPing(IOutOctetStream outStream)
        {
            var now = monotonicClock.NowMilliseconds();
            var pingRequest = new PingRequestHeader(now);

            PingRequestHeaderSerializer.Serialize(outStream, pingRequest);
            TendSerializer.Serialize(outStream, tendIn, tendOut);
        }

        void SendChallenge(IOutOctetStream outStream)
        {
            log.Debug("Sending Challenge!");
            var challenge = new ChallengeRequest(challengeNonce);

            ChallengeRequestSerializer.Serialize(outStream, challenge);
            lastStateChange = DateTime.UtcNow;
            stateChangeWait = 500;
        }

        public void SendTimeSync(IOutOctetStream outStream)
        {
            // log.Trace("Sending TimeSync!");

            RequestTime(outStream);
        }

        void SwitchState(ConnectionState newState, uint period)
        {
            log.Debug($"State:{newState} period:{period}");
            state = newState;
            stateChangeWait = period;
            lastStateChange = DateTime.UtcNow;
        }



        bool SendOneUpdatePacket(IOutOctetStream octetStream)
        {
            if (tendOut.CanIncrementOutgoingSequence)
            {
                outgoingSequenceNumber = outgoingSequenceNumber.Next();
                var tendSequenceId = tendOut.IncreaseOutgoingSequenceId();
                WriteHeader(octetStream, NormalMode, outgoingSequenceNumber.Value, connectionId);
                TendSerializer.Serialize(octetStream, tendIn, tendOut);
                log.Trace($"SendOneUpdatePacket: tendSeq{tendSequenceId}");
                return sendStream.Send(octetStream, tendSequenceId);
            }
            else
            {
                log.Trace("Can not send, forced to ping");
                SendPing(octetStream);
                return true;
            }
        }

        void StartChallenge()
        {
            challengeNonce = RandomGenerator.RandomUInt();
        }

        bool SendOnePacket()
        {
            var isCompleteSend = true;
            var octetStream = new OutOctetStream();

            switch (state)
            {
                case ConnectionState.Challenge:
                    WriteHeader(octetStream, OobMode, outSequenceNumber++, 0);
                    SendChallenge(octetStream);
                    break;
                case ConnectionState.TimeSync:
                    WriteHeader(octetStream, OobMode, outSequenceNumber++, connectionId);
                    SendTimeSync(octetStream);
                    break;
                case ConnectionState.Connected:
                    isCompleteSend = SendOneUpdatePacket(octetStream);
                    break;
            }

            var octetsToSend = octetStream.Close();

            if (octetsToSend.Length > 0)
            {
                log.Trace($"Sending packet {ByteArrayToString(octetsToSend)}");
                udpClient.Send(octetsToSend);
            }
            else
            {
                log.Trace($"Strange nothing to send");

            }
            return isCompleteSend;
        }

        void CheckDisconnect()
        {
            if (state == ConnectionState.Challenge)
            {
                return;
            }
            var timeSinceReceivedHeader = DateTime.UtcNow - lastValidHeader;
            const int DisconnectTime = 3;

            if (timeSinceReceivedHeader.Seconds > DisconnectTime)
            {
                log.Debug("Disconnect detected!");
                SwitchState(ConnectionState.Disconnected, 9999);
                receiveStream.Lost();
            }
        }

        public void Update()
        {
            var diff = DateTime.UtcNow - lastStateChange;

            if (diff.TotalMilliseconds < stateChangeWait)
            {
                return;
            }
            CheckDisconnect();
            const int burstCount = 3;
            for (var i = 0; i < burstCount; ++i)
            {
                var isDone = SendOnePacket();
                if (isDone)
                {
                    break;
                }
            }
        }

        static string ByteArrayToString(byte[] ba)
        {
            StringBuilder hex = new StringBuilder(ba.Length * 2);

            foreach (byte b in ba)
            {
                hex.AppendFormat("{0:x2}", b);
            }
            return hex.ToString();
        }

        void OnChallengeResponse(IInOctetStream stream)
        {
            if (state != ConnectionState.Challenge)
            {
                return;
            }
            var nonce = stream.ReadUint32();
            var assignedConnectionId = stream.ReadUint16();

            log.Debug($"Challenge response {nonce:X} {assignedConnectionId:X}");

            if (nonce == challengeNonce)
            {
                log.Debug($"We have a connection! {assignedConnectionId}");
                connectionId = assignedConnectionId;
                SwitchState(ConnectionState.TimeSync, 100);
            }
        }

        void OnPongResponseCmd(PongResponseHeader response)
        {
        }

        void OnPongResponse(IInOctetStream stream)
        {
            var pongResponse = PongResponseHeaderDeserializer.Deserialize(stream);

            OnPongResponseCmd(pongResponse);
            HandleTend(stream);
        }

        void OnTimeSyncResponse(IInOctetStream stream)
        {
            //log.Trace("On Time Sync response");

            if (state != ConnectionState.TimeSync)
            {
                log.Warning("We are not in timesync state anymore");
                return;
            }
            var echoedTicks = stream.ReadUint64();
            var latency = monotonicClock.NowMilliseconds() - (long)echoedTicks;
            // log.Trace($"Latency: {latency}");
            var remoteTicks = stream.ReadUint64();
            latencies.AddLatency((ushort)latency);
            ushort averageLatency;
            var isStable = latencies.StableLatency(out averageLatency);

            if (isStable)
            {
                var remoteTimeIsNow = remoteTicks + averageLatency / (ulong)2;
                //log.Trace($"We are stable! latency:{averageLatency}");
                localMillisecondsToRemoteMilliseconds = (long)remoteTimeIsNow - monotonicClock.NowMilliseconds();
                latencies = new LatencyCollection();
                SwitchState(ConnectionState.Connected, 100);
            }
            else
            {
                //log.Trace("Not stable yet, keep sending");
            }
        }

        void ReadOOB(IInOctetStream stream)
        {
            var cmd = stream.ReadUint8();

            switch (cmd)
            {
                case CommandValues.ChallengeResponse:
                    OnChallengeResponse(stream);
                    break;
                case CommandValues.TimeSyncResponse:
                    OnTimeSyncResponse(stream);
                    break;
                case CommandValues.PongResponse:
                    OnPongResponse(stream);
                    break;
                default:
                    throw new Exception($"Unknown command {cmd}");
            }
        }

        void CallbackTend()
        {
            while (tendOut.Count > 0)
            {
                var deliveryInfo = tendOut.Dequeue();
                log.Debug($"we have deliveryInfo {deliveryInfo}");
                receiveStream.PacketDelivery(deliveryInfo.PacketSequenceId, deliveryInfo.WasDelivered);
            }
        }

        SequenceId HandleTend(IInOctetStream stream)
        {
            var info = TendDeserializer.Deserialize(stream);
            log.Debug($"received tend {info} ");

            tendIn.ReceivedToUs(info.PacketId);
            tendOut.ReceivedByRemote(info.Header);
            CallbackTend();

            return info.PacketId;
        }

        void ReadConnectionPacket(IInOctetStream stream)
        {
            var packetId = HandleTend(stream);

            receiveStream.Receive(stream, packetId);
        }



        void ReadHeader(IInOctetStream stream, byte mode)
        {
            var sequence = stream.ReadUint8();
            var assignedConnectionId = stream.ReadUint16();

            if (assignedConnectionId == 0)
            {
                ReadOOB(stream);
            }
            else
            {
                if (assignedConnectionId == connectionId)
                {
                    var headerSequenceId = new SequenceId(sequence);

                    if (lastIncomingSequence.IsValidSuccessor(headerSequenceId))
                    {
                        lastIncomingSequence = headerSequenceId;

                        if (mode == OobMode)
                        {
                            ReadOOB(stream);
                        }
                        else
                        {
                            ReadConnectionPacket(stream);
                        }
                    }
                    else
                    {
                        log.Warning($"Warning: out of sequence! expected {lastIncomingSequence.Next()} but received {headerSequenceId}");
                    }
                }
            }

            lastValidHeader = DateTime.UtcNow;
        }

        void IPacketReceiver.ReceivePacket(byte[] octets, IPEndPoint fromEndpoint)
        {
            if (octets.Length < 4)
            {
                return;
            }
            log.Trace($"received packet:{octets.Length}");
            var stream = new InOctetStream(octets);
            var mode = stream.ReadUint8();
            switch (mode)
            {
                case NormalMode:
                    ReadHeader(stream, mode);
                    break;
                case OobMode:
                    ReadHeader(stream, mode);
                    break;

                default:
                    throw new Exception($"Unknown mode {mode}");
            }
        }

        public ConnectionState ConnectionState
        {
            get
            {
                return state;
            }
        }
    }
}
